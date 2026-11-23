using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Builds an ordered upgrade script from a set of differences, placing each change's statements in their
/// <see cref="DbUpgradeStage"/>.</summary>
/// <remarks>A required column added to a table is added nullable, filled by a migration or a placeholder, and
/// tightened afterwards. A changed key or constraint is dropped in the drop stages and recreated in the add stages.
/// </remarks>
public class DbUpgradeScriptBuilder {
  public DbUpgradeScriptBuilder(DbProvider provider) {
    Provider = provider;
    Ddl = new DbDdlBuilder(provider);
  }

  protected DbProvider Provider { get; }
  protected DbDdlBuilder Ddl { get; }

  List<string> _warnings = new();

  /// <summary>Adds a warning to the script being built.</summary>
  protected void Warn(string message) => _warnings.Add(message);

  /// <summary>Adds the warning that a change is not emitted because the server cannot take it.</summary>
  protected void WarnWithheld(string what, DbObjectDiffInfo change, string why) =>
    Warn($"{change.DiffKind} of {what} '{change.Name}' is not emitted: {why} "
      + $"({Provider.ServerKind}). The database will differ from the model here.");

  /// <summary>Decides whether a key change is about a constraint rather than an index.</summary>
  private static bool NeedsConstraintAlter(DbObjectDiffInfo change) =>
    (change.Target ?? change.Source) is DbKeyInfo key && key.IsPrimaryKey;

  /// <summary>Builds the script for a set of differences.</summary>
  /// <param name="changes">The differences to apply.</param>
  /// <param name="migrations">The migrations to run, each after its stage, or <see langword="null"/> for none.</param>
  /// <returns>The script, its steps in stage order.</returns>
  public virtual DbUpgradeScript Build(DbModelDiffInfo changes, IEnumerable<DbMigration>? migrations = null) {
    var script = new DbUpgradeScript(Provider.SqlDialect);
    var staged = new Dictionary<DbUpgradeStage, List<DbUpgradeStep>>();
    _warnings = script.Warnings;

    void Emit(DbUpgradeStage stage, DdlStatement statement, DbObjectDiffInfo? change) {
      if (!staged.TryGetValue(stage, out var list))
        staged[stage] = list = new List<DbUpgradeStep>();
      list.Add(DbUpgradeStep.ForDdl(stage, statement, change));
    }

    // The server is made able to hold a geographic point before the first stage that adds one.
    if (changes.Items.Any(AddsGeoPoint) && Ddl.GeoPointPrerequisite() is { } prerequisite)
      Emit(DbUpgradeStage.AddSchemas, prerequisite, null);
    // And able to hold a vector before the first stage that adds one.
    if (changes.Items.Any(AddsVector) && Ddl.VectorPrerequisite() is { } vectorPrerequisite)
      Emit(DbUpgradeStage.AddSchemas, vectorPrerequisite, null);

    foreach (var change in changes.Items)
      Dispatch(change, Emit);

    // The views arrive with each after the views it reads; they are dropped the other way round.
    if (staged.TryGetValue(DbUpgradeStage.DropViews, out var viewDrops))
      viewDrops.Reverse();

    foreach (var migration in migrations ?? []) {
      if (!staged.TryGetValue(migration.After, out var list))
        staged[migration.After] = list = new List<DbUpgradeStep>();
      list.Add(DbUpgradeStep.ForMigration(migration));
    }

    // Stages in their declared order; within a stage, the order the changes arrived in, which is model order.
    foreach (var stage in Enum.GetValues<DbUpgradeStage>())
      if (staged.TryGetValue(stage, out var list))
        foreach (var step in list)
          script.Add(step);
    return script;
  }

  /// <summary>Determines whether a change adds or alters a table or a column that holds a geographic point.</summary>
  private static bool AddsGeoPoint(DbObjectDiffInfo change) =>
    change.DiffKind != DbDiffKind.Drop && change.Target switch {
      DbTableInfo table => DbDdlBuilder.HasGeoPoint(table),
      DbColumnInfo column => column.IsGeoPoint,
      _ => false,
    };

  /// <summary>Determines whether a change adds or alters a table or a column that holds a vector.</summary>
  private static bool AddsVector(DbObjectDiffInfo change) =>
    change.DiffKind != DbDiffKind.Drop && change.Target switch {
      DbTableInfo table => DbDdlBuilder.HasVector(table),
      DbColumnInfo column => column.IsVector,
      _ => false,
    };

  /// <summary>Emits a change's statements into their stages.</summary>
  protected virtual void Dispatch(DbObjectDiffInfo change, Action<DbUpgradeStage, DdlStatement, DbObjectDiffInfo?> emit) {
    switch (change.ObjectKind) {
      case DbObjectKind.Schema:
        if (change.DiffKind == DbDiffKind.Add)
          emit(DbUpgradeStage.AddSchemas, Ddl.CreateSchema((DbSchemaInfo)change.Target!), change);
        else if (change.DiffKind == DbDiffKind.Drop)
          emit(DbUpgradeStage.DropSchemas, Ddl.DropSchema((DbSchemaInfo)change.Source!), change);
        break;

      case DbObjectKind.Sequence:
        switch (change.DiffKind) {
          case DbDiffKind.Add:
            emit(DbUpgradeStage.AddSequences, Ddl.CreateSequence((DbSequenceInfo)change.Target!), change);
            break;
          case DbDiffKind.Drop:
            emit(DbUpgradeStage.DropSequences, Ddl.DropSequence((DbSequenceInfo)change.Source!), change);
            break;
          case DbDiffKind.Modify:
            emit(DbUpgradeStage.AddSequences, Ddl.AlterSequence((DbSequenceInfo)change.Target!), change);
            break;
        }
        break;

      case DbObjectKind.Table:
        DispatchTable(change, emit);
        break;

      case DbObjectKind.View:
        // A view has no incremental alter, so a Modify is a drop and a create.
        if (change.DiffKind is DbDiffKind.Drop or DbDiffKind.Modify)
          emit(DbUpgradeStage.DropViews, Ddl.DropView((DbViewInfo)change.Source!), change);
        if (change.DiffKind is DbDiffKind.Add or DbDiffKind.Modify)
          foreach (var statement in Ddl.CreateView((DbTableInfo)change.Target!))
            emit(DbUpgradeStage.AddViews, statement, change);
        break;

      case DbObjectKind.TableFunction:
        // A function is replaced whole: dropped where its signature may have changed, created again after the tables.
        if (change.DiffKind is DbDiffKind.Drop or DbDiffKind.Modify)
          emit(DbUpgradeStage.DropTableFunctions, Ddl.DropTableFunction((DbRoutineInfo)change.Source!), change);
        if (change.DiffKind is DbDiffKind.Add or DbDiffKind.Modify && Ddl.CreateTableFunction((DbTableInfo)change.Target!) is { } create)
          emit(DbUpgradeStage.AddTableFunctions, create, change);
        break;

      case DbObjectKind.Column:
        DispatchColumn(change, emit);
        break;

      case DbObjectKind.Key:
        // Only a constraint-shaped key needs the server to take constraints after the fact; an index is its own
        // object everywhere.
        if (NeedsConstraintAlter(change) && !Provider.Supports(DbFeatures.AddConstraintsAfterCreate)) {
          WarnWithheld("key", change, "constraints can only be part of CREATE TABLE on this server");
          break;
        }
        // A key has no incremental alter, so a Modify is a drop and an add.
        if (change.DiffKind is DbDiffKind.Drop or DbDiffKind.Modify)
          foreach (var statement in Ddl.DropKey((DbKeyInfo)change.Source!))
            emit(DbUpgradeStage.DropKeys, statement, change);
        if (change.DiffKind is DbDiffKind.Add or DbDiffKind.Modify)
          foreach (var statement in AddKey((DbKeyInfo)change.Target!))
            emit(DbUpgradeStage.AddKeys, statement, change);
        break;

      case DbObjectKind.RefConstraint:
        if (!Provider.Supports(DbFeatures.AddConstraintsAfterCreate)) {
          WarnWithheld("reference constraint", change, "constraints can only be part of CREATE TABLE on this server");
          break;
        }
        if (change.DiffKind is DbDiffKind.Drop or DbDiffKind.Modify)
          emit(DbUpgradeStage.DropRefConstraints, Ddl.DropRefConstraint((DbRefConstraintInfo)change.Source!), change);
        if (change.DiffKind is DbDiffKind.Add or DbDiffKind.Modify)
          emit(DbUpgradeStage.AddRefConstraints, Ddl.AddRefConstraint((DbRefConstraintInfo)change.Target!), change);
        break;

      case DbObjectKind.CheckConstraint:
        if (!Provider.Supports(DbFeatures.AddConstraintsAfterCreate)) {
          WarnWithheld("check constraint", change, "constraints can only be part of CREATE TABLE on this server");
          break;
        }
        // Dropped before a column it reads is renamed or changed, added after.
        if (change.DiffKind == DbDiffKind.Drop)
          emit(DbUpgradeStage.DropRefConstraints, Ddl.DropCheckConstraint((DbCheckConstraintInfo)change.Source!),
            change);
        else if (change.DiffKind == DbDiffKind.Add)
          emit(DbUpgradeStage.AddRefConstraints, Ddl.AddCheckConstraint((DbCheckConstraintInfo)change.Target!), change);
        break;
    }
  }

  /// <summary>Emits a table change's statements, including a new table's keys and foreign keys into the stages every
  /// other key goes to.</summary>
  protected virtual void DispatchTable(DbObjectDiffInfo change,
      Action<DbUpgradeStage, DdlStatement, DbObjectDiffInfo?> emit) {
    switch (change.DiffKind) {
      case DbDiffKind.Add:
        var table = (DbTableInfo)change.Target!;
        emit(DbUpgradeStage.AddTables, Ddl.CreateTable(table), change);
        foreach (var column in table.Columns)
          if (Ddl.CommentComputedColumn(column) is { } comment)
            emit(DbUpgradeStage.AddTables, comment, change);

        // Inlined constraints are already in the CREATE TABLE statement above.
        var inlined = Ddl.NeedsInlineConstraints(table);
        foreach (var key in DbDdlBuilder.GetKeysInCreationOrder(table)) {
          if (key.IsPrimaryKey && inlined)
            continue;
          foreach (var statement in AddKey(key))
            emit(DbUpgradeStage.AddKeys, statement, null);
        }
        if (!Ddl.InlinesRefConstraints())
          foreach (var constraint in table.RefConstraints)
            emit(DbUpgradeStage.AddRefConstraints, Ddl.AddRefConstraint(constraint), null);
        break;

      case DbDiffKind.Drop: {
        var droppedTable = (DbTableInfo)change.Source!;
        emit(DbUpgradeStage.DropTables, Ddl.DropTable(droppedTable), change);
        // The table took its indexes with it; what a spatial index left beside itself goes after it.
        foreach (var key in droppedTable.Keys)
          foreach (var statement in Ddl.CleanUpSpatialIndex(key, DdlKind.DropTable))
            emit(DbUpgradeStage.DropTables, statement, change);
        break;
      }

      case DbDiffKind.Rename:
        emit(DbUpgradeStage.RenameTables,
          Ddl.RenameTable((DbTableInfo)change.Source!, (DbTableInfo)change.Target!), change);
        break;
    }
  }

  protected virtual void DispatchColumn(DbObjectDiffInfo change,
      Action<DbUpgradeStage, DdlStatement, DbObjectDiffInfo?> emit) {
    switch (change.DiffKind) {
      case DbDiffKind.Add:
        var added = (DbColumnInfo)change.Target!;

        // A required column with no default is added nullable and tightened later, since existing rows would violate
        // it at once.
        var deferred = !added.IsNullable && added.DefaultExpression == null;

        // A server that cannot alter a column gets it in one statement, not null with the placeholder as a default.
        if (deferred && !Provider.Supports(DbFeatures.AlterColumn)) {
          var inline = Ddl.AddRequiredColumn(added);
          if (inline != null)
            emit(DbUpgradeStage.AddColumns, inline, change);
          else
            WarnWithheld("column", change,
              $"this server cannot alter a column, and {added.TypeSpec} has no value to add it with");
          break;
        }

        foreach (var statement in Ddl.AddColumnToTable(added, asNullable: deferred))
          emit(DbUpgradeStage.AddColumns, statement, change);
        if (Ddl.CommentComputedColumn(added) is { } addedComment)
          emit(DbUpgradeStage.AddColumns, addedComment, change);
        if (deferred)
          TightenColumn(added, change, emit);
        break;

      case DbDiffKind.Drop:
        if (!Provider.Supports(DbFeatures.DropColumn)) {
          WarnWithheld("column", change, "this server cannot drop a column");
          break;
        }
        var dropped = (DbColumnInfo)change.Source!;
        emit(DbUpgradeStage.DropColumns, Ddl.DropColumn(dropped), change);
        EmitAfterTableModified(DbUpgradeStage.DropColumns, dropped, change, emit);
        break;

      case DbDiffKind.Rename:
        emit(DbUpgradeStage.RenameColumns,
          Ddl.RenameColumn((DbColumnInfo)change.Source!, (DbColumnInfo)change.Target!), change);
        break;

      case DbDiffKind.Modify:
        var old = (DbColumnInfo)change.Source!;
        var @new = (DbColumnInfo)change.Target!;
        // A computed column has no alter: it is dropped and added again, before anything else adds, and the keys
        // over it that the model has go and come back with it. Another key over it is dropped by its own
        // difference, or is one the settings say to leave alone, which the script does not touch.
        if (old.IsComputed || @new.IsComputed) {
          var kept = GetKeysOver(@new).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
          foreach (var key in GetKeysOver(old).Where(k => kept.Contains(k.Name)))
            foreach (var statement in Ddl.DropKey(key))
              emit(DbUpgradeStage.DropKeys, statement, change);
          emit(DbUpgradeStage.DropComputedColumns, Ddl.DropColumn(old), change);
          EmitAfterTableModified(DbUpgradeStage.DropComputedColumns, old, change, emit);
          foreach (var statement in Ddl.AddColumnToTable(@new))
            emit(DbUpgradeStage.AddColumns, statement, change);
          if (Ddl.CommentComputedColumn(@new) is { } recreatedComment)
            emit(DbUpgradeStage.AddColumns, recreatedComment, change);
          foreach (var key in GetKeysOver(@new))
            foreach (var statement in AddKey(key))
              emit(DbUpgradeStage.AddKeys, statement, change);
          break;
        }
        if (!Provider.Supports(DbFeatures.AlterColumn)) {
          WarnWithheld("column", change, "this server cannot change a column in place");
          break;
        }
        var altered = false;
        foreach (var statement in Ddl.AlterColumn(old, @new)) {
          emit(DbUpgradeStage.AlterColumns, statement, change);
          altered = true;
        }
        if (altered)
          EmitAfterTableModified(DbUpgradeStage.AlterColumns, @new, change, emit);

        // Tightening an existing column follows the same sequence as adding a required one.
        if (old.IsNullable && !@new.IsNullable)
          TightenColumn(@new, change, emit);
        break;
    }
  }

  /// <summary>Emits the backfill of a column and then the statement that makes it not null.</summary>
  /// <remarks>A foreign-key column is never backfilled, since a placeholder would point at nothing; a warning says a
  /// migration must fill it. The same applies to a type with no placeholder.</remarks>
  protected virtual void TightenColumn(DbColumnInfo column, DbObjectDiffInfo change,
      Action<DbUpgradeStage, DdlStatement, DbObjectDiffInfo?> emit) {
    if (IsForeignKey(column)) {
      Warn($"Column '{column.Table!.Name}.{column.Name}' is a FOREIGN KEY becoming required. Existing rows cannot be "
        + "filled with a placeholder — it would point at nothing — so a migration must set real values before this "
        + "runs.");
    } else {
      var init = Ddl.InitializeColumn(column);
      if (init == null)
        Warn($"Column '{column.Table!.Name}.{column.Name}' is becoming required and its type "
          + $"({column.TypeSpec}) has no value to fill existing rows with. A migration must fill it before this runs.");
      else
        emit(DbUpgradeStage.InitColumns, init, change);
    }
    emit(DbUpgradeStage.AlterColumns, Ddl.SetColumnNotNull(column), change);
    EmitAfterTableModified(DbUpgradeStage.AlterColumns, column, change, emit);
  }

  /// <summary>Emits the statement the server wants after a column of a table was dropped or altered, where it
  /// wants one, into the stage of the change.</summary>
  private void EmitAfterTableModified(DbUpgradeStage stage, DbColumnInfo column, DbObjectDiffInfo change,
      Action<DbUpgradeStage, DdlStatement, DbObjectDiffInfo?> emit) {
    if (column.Table != null && Ddl.AfterTableModified(column.Table) is { } statement)
      emit(stage, statement, change);
  }

  /// <summary>Gets the keys of a column's table that include the column.</summary>
  private static IEnumerable<DbKeyInfo> GetKeysOver(DbColumnInfo column) =>
    column.Table?.Keys.Where(k => k.Columns.Any(c => ReferenceEquals(c.Column, column))) ?? [];

  private static bool IsForeignKey(DbColumnInfo column) =>
    column.Table != null
    && column.Table.Keys.Any(k => k.IsForeignKey && k.Columns.Any(c => ReferenceEquals(c.Column, column)));

  /// <summary>Builds the statements that add a key: a constraint for a primary key, an index otherwise.</summary>
  protected virtual IEnumerable<DdlStatement> AddKey(DbKeyInfo key) {
    if (key.IsPrimaryKey)
      return [Ddl.AddPrimaryKey(key)];
    if (key.Flags.HasFlag(KeyFlags.Index) || key.IsUnique)
      return Ddl.CreateIndex(key);
    return [];
  }
}
