using System.Data.Common;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Readers;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Provider;

/// <summary>Builds the insert, update and delete commands that write an entity record to one database.</summary>
/// <remarks>The values are bound as parameters, so a statement's text varies only with its shape, such as which
/// columns an update names. The one exception is the multi-row insert, which writes short values as literals. A
/// builder serves one save: it carries the save's key rule, the batch being joined and the slots the batch leaves
/// its generated ids in, which a child's insert reads; so it is not shared, unlike the statement command builder.
/// </remarks>
public class RecordCommandBuilder {
  public RecordCommandBuilder(DbModel dbModel) {
    ArgumentNullException.ThrowIfNull(dbModel);
    DbModel = dbModel;
    Provider = dbModel.Provider;
    Dialect = Provider.SqlDialect;
  }

  /// <summary>Gets the provider of the db model's server.</summary>
  protected DbProvider Provider { get; }

  /// <summary>Gets the db model this builder writes for, which says which table an entity is stored in.</summary>
  protected DbModel DbModel { get; }

  protected SqlDialect Dialect { get; }

  /// <summary>Gets or sets the function that decides whether an insert of a record asks the server for the id it
  /// generates.</summary>
  /// <value>The function. The default returns <see langword="true"/> for every record; under
  /// <see cref="SaveOptions.InsertAndForget"/> the save sets one that returns <see langword="true"/> only for a
  /// record a later record of the save refers to.</value>
  public Func<EntityRecord, bool> ShouldReturnId { get; set; } = _ => true;

  /// <summary>Gets a value that indicates whether this builder is writing a batch on a server with
  /// <see cref="DbFeatures.IdentityCarriedInBatch"/>.</summary>
  protected bool CarriesGeneratedIds => BatchDbCommand != null && Provider.Supports(DbFeatures.IdentityCarriedInBatch);

  // Numbers the slots a save's batches leave their generated ids in, so no two share a name.
  int _slotCount;

  /// <summary>Gets or sets the command every statement is built into while a batch is assembled, or
  /// <see langword="null"/> to give each statement a command of its own.</summary>
  /// <remarks>Each build overwrites the command's text with its own statement and adds its parameters; the caller
  /// takes the text away before the next build and joins the statements once.</remarks>
  public DbCommand? BatchDbCommand { get; set; }

  private DbCommand NextDbCommand() => BatchDbCommand ?? Provider.CreateDbCommand();

  /// <summary>Builds the command that writes a record, by its status, with its values bound.</summary>
  /// <param name="record">The record to write.</param>
  /// <returns>The command.</returns>
  /// <remarks>The record's foreign keys are brought into step with its references first, since a referenced record's
  /// key may have been generated since the reference was assigned, and its persisted computed members are computed.
  /// </remarks>
  public virtual DataCommand BuildFor(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    MemberValueAccessorHelper.RefreshForeignKeys(record);
    MemberValueAccessorHelper.ComputeStoredValues(record);
    return record.Status switch {
      EntityStatus.New => BuildInsert(record),
      EntityStatus.Modified => BuildUpdate(record),
      EntityStatus.Deleted => BuildDelete(record),
      _ => throw new InvalidOperationException(
        $"A '{record.Entity.Name}' record with status {record.Status} has nothing to save."),
    };
  }

  /// <summary>Builds the insert of a record, naming every column but the ones the database generates.</summary>
  /// <param name="record">The new record.</param>
  /// <returns>The command, which returns the generated id as its scalar result where the server makes one and
  /// <see cref="ShouldReturnId"/> says to ask for it.</returns>
  public virtual DataCommand BuildInsert(EntityRecord record) {
    var table = GetTable(record);
    var dbCommand = NextDbCommand();

    var columns = table.InsertColumnList;
    var values = string.Join(", ", table.InsertColumns.Select(column =>
      GetIdSlotReferenceFor(record, column) ?? BindColumnValue(dbCommand, record, column, asLiteral: false)));

    // Where the server makes the key, the insert asks for it back in the same round trip.
    var identity = record.Entity.IdentityMember;
    if (identity != null && table.GetColumn(identity) is { } identityColumn && ShouldReturnId(record)) {
      // In a batch that carries keys, the server keeps the key in a slot for the statements after this one.
      if (CarriesGeneratedIds) {
        dbCommand.CommandText = ConfigureIdValueReturn(dbCommand, record, identityColumn,
          string.Format(Dialect.Insert, table.RefName, columns, values));
        return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"insert {record.Entity.Name}") { Record = record };
      }
      if (Dialect.InsertReturningIdentity.Length > 0) {
        dbCommand.CommandText = string.Format(Dialect.InsertReturningIdentity,
          table.RefName, columns, values, identityColumn.QuotedName);
        return new DataCommand(dbCommand, DbExecutionType.Scalar, $"insert {record.Entity.Name}") { Record = record };
      }
    }

    dbCommand.CommandText = string.Format(Dialect.Insert, table.RefName, columns, values);
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"insert {record.Entity.Name}") { Record = record };
  }

  /// <summary>Builds an insert with its key captured into a slot of the batch: the dialect's
  /// <see cref="SqlDialect.BatchCaptureIdentity"/> after the insert, the slot an output parameter of the command where
  /// the dialect reads none back by a statement and a session variable where it does.</summary>
  /// <remarks>A provider whose insert returns the id itself overrides this.</remarks>
  protected virtual string ConfigureIdValueReturn(DbCommand dbCommand, EntityRecord record, DbColumnInfo identityColumn, string insert) {
    var name = NextIdSlotName();
    DbParameter? parameter = null;
    if (Dialect.BatchReadIdentities.Length == 0) {
      parameter = dbCommand.CreateParameter();
      parameter.ParameterName = name;
      parameter.DbType = System.Data.DbType.Int64;
      parameter.Direction = System.Data.ParameterDirection.Output;
      dbCommand.Parameters.Add(parameter);
    }
    RegisterIdSlot(record, name, parameter);
    return insert + Dialect.StatementSeparator + string.Format(Dialect.BatchCaptureIdentity, GetIdSlotReference(name));
  }

  /// <summary>Gets the name of the next slot a batch leaves a key in, unique within the save.</summary>
  protected string NextIdSlotName() => "id" + _slotCount++;

  /// <summary>Gets the reference a statement writes for a slot: its name with the prefix.</summary>
  public string GetIdSlotReference(string name) => Provider.ParameterPrefix + name;

  /// <summary>Puts on a record where the batch leaves its key: the slot's name, and the output parameter where the
  /// dialect reads the key back through one.</summary>
  /// <remarks>The slot stays on the record until the batch's keys are applied, so that a child's insert built after
  /// it writes the slot's reference in place of the id it does not have yet.</remarks>
  protected static void RegisterIdSlot(EntityRecord record, string name, DbParameter? parameter) =>
    record.GeneratedIdSlot = new GeneratedIdSlot(name, parameter);

  /// <summary>Gets the slot reference to write for a column that is a foreign key to a record whose key is in a slot of
  /// the batch, or <see langword="null"/> for any other column.</summary>
  private string? GetIdSlotReferenceFor(EntityRecord record, DbColumnInfo column) {
    if (!CarriesGeneratedIds || column.Member?.OwnerReference is not { } reference)
      return null;
    return record.GetRawValue(reference) is EntityBase { Record.GeneratedIdSlot: { } slot } ? GetIdSlotReference(slot.Name) : null;
  }

  /// <summary>Puts a generated id on the record it belongs to, or, for a batch that carried its ids, every id on
  /// its record and then the children's foreign keys, which were written as slot names.</summary>
  /// <param name="dbCommand">The command that ran.</param>
  /// <remarks>A provider whose driver returns the key another way, as Oracle does through an output parameter,
  /// overrides this.</remarks>
  public virtual void AssignGeneratedId(DataCommand dbCommand) {
    if (dbCommand.BatchRecords is { } batchRecords) {
      AssignCarriedIds(dbCommand, batchRecords);
      return;
    }
    // Only an insert makes a key: an update or a delete comes back with a row count.
    if (dbCommand.Record is not { Status: EntityStatus.New } record
        || record.Entity.IdentityMember is not { } identity)
      return;
    // So does an insert that did not ask for a key.
    if (!ShouldReturnId(record))
      return;
    if (dbCommand.Result is not { } value || value == DBNull.Value)
      return;

    // Loaded, not assigned: the database chose the value, and the record is still New.
    record.SetLoadedValue(identity, Provider.ConvertReadValue(value, identity.DataType));
  }

  /// <summary>Puts the keys a batch carried, read off its output parameters or by name off the row its last
  /// statement selected, onto their records, takes the slots off them, then brings the foreign keys of every written
  /// record that references an identity-keyed entity into step.</summary>
  private void AssignCarriedIds(DataCommand command, IReadOnlyList<EntityRecord> batchRecords) {
    var row = command.Result as IReadOnlyDictionary<string, object?>;
    foreach (var record in batchRecords) {
      if (record.GeneratedIdSlot is not { } slot || record.Entity.IdentityMember is not { } identity)
        continue;
      var value = slot.Parameter != null ? ReadIdSlotValue(slot.Parameter) : row?.GetValueOrDefault(slot.Name);
      if (value == null || value == DBNull.Value)
        throw new InvalidOperationException($"The batch left no key for the '{record.Entity.Name}' row it inserted in slot '{slot.Name}'.");
      record.SetLoadedValue(identity, Provider.ConvertReadValue(value, identity.DataType));
      record.GeneratedIdSlot = null;
    }
    foreach (var record in batchRecords)
      if (record.Status == EntityStatus.New && record.Entity.Flags.HasFlag(EntityTypeFlags.ReferencesIdentity))
        MemberValueAccessorHelper.RefreshForeignKeys(record);
  }

  /// <summary>Reads a carried key off an output parameter.</summary>
  /// <remarks>Oracle's driver hands back its own numeric type, and its provider overrides this.</remarks>
  protected virtual object? ReadIdSlotValue(DbParameter parameter) => parameter.Value;

  /// <summary>Builds the update of a record, naming only the members that changed, matched on the key and on the row
  /// version where the entity counts one.</summary>
  /// <param name="record">The modified record.</param>
  /// <returns>The command, expecting one row where the entity has a row version.</returns>
  public virtual DataCommand BuildUpdate(EntityRecord record) {
    var table = GetTable(record);
    var dbCommand = NextDbCommand();

    var assignments = new List<string>();
    foreach (var member in record.ModifiedMembers) {
      // A key member cannot be modified; the record refuses the write. This is a guard, not a filter.
      if (table.GetColumn(member) is not { } column || member.IsDbGenerated || member.IsPrimaryKey)
        continue;
      assignments.Add($"{column.QuotedName} = {BuildAssignedValue(record, column, dbCommand)}");
    }

    if (assignments.Count == 0)
      throw new InvalidOperationException(
        $"A '{record.Entity.Name}' record is marked modified with no updatable member changed.");

    // The row version is counted up by the update and matched on by its WHERE, so a row changed since this session
    // read it is not found and the save is refused.
    var version = record.Entity.RowVersionMember;
    if (version != null && table.GetColumn(version) is { } versionColumn)
      assignments.Add($"{versionColumn.QuotedName} = {AddParameter(dbCommand, GetNextVersion(record, version))}");

    dbCommand.CommandText = string.Format(Dialect.Update,
      table.RefName, string.Join(", ", assignments), BuildPrimaryKeyMatch(record, dbCommand));
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"update {record.Entity.Name}") {
      Record = record,
      ExpectedRowCount = version == null ? -1 : 1,
    };
  }

  /// <summary>Gets the version a row carries after a save, one more than it holds, which the session puts on the
  /// record once the save has run.</summary>
  /// <param name="record">The record.</param>
  /// <param name="version">The row version member.</param>
  /// <returns>The next version, as a long or an int to match the member.</returns>
  public static object GetNextVersion(EntityRecord record, EntityMemberInfo version) {
    // Two returns rather than one conditional, which would make an int member's value a long.
    if (record.GetRawValue(version) is long wide)
      return wide + 1;
    return Convert.ToInt32(record.GetRawValue(version) ?? 0) + 1;
  }

  /// <summary>Builds a single insert of many new records of one entity, with short values written as literals.
  /// </summary>
  /// <param name="records">The new records, all of one entity.</param>
  /// <returns>The command.</returns>
  /// <remarks>The caller does not collapse records whose key the server generates, since the keys could only be
  /// matched back by position and no server guarantees the order.</remarks>
  public virtual DataCommand BuildInsertMany(IReadOnlyList<EntityRecord> records) {
    ArgumentOutOfRangeException.ThrowIfZero(records.Count);
    var entity = records[0].Entity;
    var table = GetTable(records[0]);
    var dbCommand = NextDbCommand();

    var columns = table.InsertColumnList;
    var tableRef = table.RefName;
    var rows = records.Select(record => BuildInsertManyRow(dbCommand, table, record));
    dbCommand.CommandText = string.Format(Dialect.InsertMany, tableRef, columns,
      string.Join(Dialect.InsertManyRowSeparator, rows));
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"insert {records.Count} × {entity.Name}") {
      Records = records,
    };
  }

  /// <summary>Builds one row of a multi-row insert: the record's foreign keys brought into step and its persisted
  /// computed members computed, then a value per column, a slot reference, a literal or a parameter.</summary>
  private string BuildInsertManyRow(DbCommand dbCommand, DbTableInfo table, EntityRecord record) {
    MemberValueAccessorHelper.RefreshForeignKeys(record);      // as BuildFor does, per row
    MemberValueAccessorHelper.ComputeStoredValues(record);
    var values = string.Join(", ", table.InsertColumns.Select(column =>
      GetIdSlotReferenceFor(record, column) ?? BindColumnValue(dbCommand, record, column, asLiteral: true)));
    return string.Format(Dialect.InsertManyRow, table.RefName, table.InsertColumnList, values);
  }

  /// <summary>Builds a single delete of many records of one entity by a key list.</summary>
  /// <param name="records">The deleted records, all of one entity with a single-column key.</param>
  /// <returns>The command.</returns>
  /// <remarks>A composite key has no portable list form and stays one statement per row; see
  /// <see cref="SqlDialect.DeleteMany"/>.</remarks>
  public virtual DataCommand BuildDeleteMany(IReadOnlyList<EntityRecord> records) {
    ArgumentOutOfRangeException.ThrowIfZero(records.Count);
    var entity = records[0].Entity;
    var key = entity.PrimaryKey
      ?? throw new InvalidOperationException(
        $"Entity '{entity.Name}' has no primary key, so its rows cannot be found to delete.");
    var member = key.MembersExpanded[0].Member;
    var table = GetTable(records[0]);
    var column = table.GetColumn(member)
      ?? throw new InvalidOperationException($"Key member '{entity.Name}.{member.Name}' has no column.");

    var dbCommand = NextDbCommand();
    var parameters = records.Select(record => AddParameter(dbCommand, GetValue(record, member)));
    dbCommand.CommandText = string.Format(Dialect.DeleteMany,
      table.RefName, column.QuotedName, string.Join(", ", parameters));
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"delete {records.Count} × {entity.Name}") {
      Records = records,
    };
  }

  /// <summary>Builds the delete of a record, matched on the key and on the row version where the entity counts one.
  /// </summary>
  /// <param name="record">The deleted record.</param>
  /// <returns>The command, expecting one row where the entity has a row version.</returns>
  public virtual DataCommand BuildDelete(EntityRecord record) {
    var table = GetTable(record);
    var dbCommand = NextDbCommand();
    dbCommand.CommandText = string.Format(Dialect.Delete,
      table.RefName, BuildPrimaryKeyMatch(record, dbCommand));
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"delete {record.Entity.Name}") {
      Record = record,
      ExpectedRowCount = record.Entity.RowVersionMember == null ? -1 : 1,
    };
  }

  // ----- the pieces ---------------------------------------------------------------------------------------------------

  /// <summary>Builds the condition that finds a record's row by its key, and by its row version where the entity
  /// counts one, with the values bound.</summary>
  protected string BuildPrimaryKeyMatch(EntityRecord record, DbCommand dbCommand) {
    var table = GetTable(record);
    var key = record.Entity.PrimaryKey
      ?? throw new InvalidOperationException(
        $"Entity '{record.Entity.Name}' has no primary key, so a row of it cannot be found to write.");

    var terms = new List<string>();
    foreach (var member in key.MembersExpanded) {
      var column = table.GetColumn(member.Member)
        ?? throw new InvalidOperationException(
          $"Key member '{record.Entity.Name}.{member.Member.Name}' has no column.");
      terms.Add($"{column.QuotedName} = {AddParameter(dbCommand, GetValue(record, member.Member))}");
    }
    if (record.Entity.RowVersionMember is { } version && table.GetColumn(version) is { } versionColumn)
      terms.Add($"{versionColumn.QuotedName} = {AddParameter(dbCommand, record.GetRawValue(version) ?? 0)}");
    return string.Join(Dialect.And, terms);
  }

  /// <summary>Builds what an update sets a column to: the member's value bound, or, for a JSON document changed in
  /// place where the dialect spells it, the changed paths written into the column.</summary>
  private string BuildAssignedValue(EntityRecord record, DbColumnInfo column, DbCommand dbCommand) {
    var member = column.Member!;
    if (member.IsJson && JsonMemberHelper.GetPatches(record, member) is { } patches
        && Dialect.BuildJsonSet(column.QuotedName, patches, value => AddParameter(dbCommand, value)) is { } patched)
      return patched;
    return BindColumnValue(dbCommand, record, column, asLiteral: false);
  }

  /// <summary>Binds a column's value for an insert or an update, as a parameter or, where the multi-row insert wants
  /// one, as a literal; a geographic point's well-known binary is wrapped as the server's point, and a vector's
  /// array as the server's vector.</summary>
  /// <remarks>A null point and a null binary are written as the literal: a bound null has no type and a driver
  /// sends it as text, which a server refuses for its point type and SQL Server for a binary column. So is a null
  /// Guid where the provider declares <see cref="DbFeatures.NullGuidAsLiteralDb2"/>.</remarks>
  private string BindColumnValue(DbCommand dbCommand, EntityRecord record, DbColumnInfo column, bool asLiteral) {
    var value = GetValue(record, column.Member!);
    if (value == null && (column.IsGeoPoint || column.Member!.DataType == typeof(byte[])
        || Provider.Supports(DbFeatures.NullGuidAsLiteralDb2) && column.TypeRef.ClrType == typeof(Guid)))
      return Dialect.GetLiteral((object?)null)!;
    var bound = asLiteral ? AddValue(dbCommand, value) : AddParameter(dbCommand, value);
    return Dialect.GetColumnWriteFormat(column) is { } writeFormat ? string.Format(writeFormat, bound) : bound;
  }

  /// <summary>Binds a value as a parameter named by its position in the command, and returns the prefixed name to write
  /// for it.</summary>
  /// <remarks>The position is the command's, not the statement's, so several statements can share a command; and a
  /// column may appear twice in one statement.</remarks>
  protected string AddParameter(DbCommand dbCommand, object? value) {
    var name = "p" + dbCommand.Parameters.Count;
    Provider.AddParameter(dbCommand, name, value ?? DBNull.Value);
    return Provider.ParameterPrefix + name;
  }

  /// <summary>Writes a value as a literal where the server can spell it, and binds it as a parameter where it cannot.
  /// </summary>
  /// <remarks>Only the multi-row insert uses this; its text varies with its row count, so no plan cache reuses it.
  /// The value goes through <see cref="DbProvider.AdaptParameterValue"/> first, so a literal and the same value bound
  /// agree byte for byte.</remarks>
  protected string AddValue(DbCommand dbCommand, object? value) {
    var adapted = value == null ? null : Provider.AdaptParameterValue(value);
    return Dialect.GetLiteral(adapted) ?? AddParameter(dbCommand, value);
  }

  /// <summary>Gets a member's value as the database takes it: a JSON member serialized, any other as its slot holds
  /// it.</summary>
  /// <remarks>A required key nobody assigned is refused by validation before any statement is built.</remarks>
  protected static object? GetValue(EntityRecord record, EntityMemberInfo member) =>
    member.IsJson ? JsonMemberHelper.Serialize(record, member) : record.GetRawValue(member);

  protected DbTableInfo GetTable(EntityRecord record) => GetTable(record.Entity);

  protected DbTableInfo GetTable(EntityTypeInfo entity) =>
    DbModel.GetTable(entity)
      ?? throw new InvalidOperationException(
        $"Entity '{entity.Name}' has no table in db model '{DbModel.Name}', so there is nowhere to write it.");
}
