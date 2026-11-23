using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Readers;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db;

/// <summary>Builds the commands that run a <see cref="DataRequest"/>: the statements of a save, planned into
/// multi-row statements and batched commands; the bound statement of a query; the command of a hand-written
/// statement.</summary>
/// <remarks>Construction is synchronous and runs nothing. A save's commands are produced one at a time as the executor
/// reaches them, so that a key the server generated is on its record before the next statement binds it; a dependency
/// between commands is described as data the executor applies, never as code the builder runs.</remarks>
public class DataCommandSetBuilder {
  /// <summary>Represents the records one statement carries: all of one entity in one status.</summary>
  public sealed record RecordGroup(IReadOnlyList<EntityRecord> Records) {
    /// <summary>Gets the entity of the records.</summary>
    public EntityTypeInfo Entity => Records[0].Entity;

    /// <summary>Gets the status of the records.</summary>
    public EntityStatus Status => Records[0].Status;

    /// <summary>Gets the number of records.</summary>
    public int Count => Records.Count;
  }

  /// <summary>Represents the groups one command carries, one statement each.</summary>
  public sealed record CommandRecordGroupSet(IReadOnlyList<RecordGroup> RecordGroups);

  // Binds a translated template into a command; it carries no state of its own, so one serves every request.
  readonly TemplateCommandBuilder _templateCommandBuilder;

  /// <summary>Initializes a new instance of the <see cref="DataCommandSetBuilder"/> class.</summary>
  public DataCommandSetBuilder(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    Database = database;
    _templateCommandBuilder = new TemplateCommandBuilder(database.Provider);
  }

  /// <summary>Gets the database the commands are built for.</summary>
  protected Database Database { get; }

  /// <summary>Gets the database's provider.</summary>
  protected DbProvider Provider => Database.Provider;

  /// <summary>The number of parameters left unspent at the end of every command, so that an estimate that drifts does
  /// not reach the server's ceiling.</summary>
  protected const int ParameterHeadroom = 200;

  /// <summary>The most statements one command carries, whatever the parameter budget allows.</summary>
  /// <remarks>A backstop against a save of very many entity types becoming one command the server has to parse in
  /// one go, not a tuning knob.</remarks>
  public const int MaxStatementsPerCommand = 2000;

  /// <summary>Builds the request's commands and sets them on it, with their expected count, the number of statements
  /// they carry and whether they need a transaction.</summary>
  /// <remarks>Vendor variation is declared on the provider and its dialect, not by overriding this class. A statement
  /// the builder cannot make from the model is a request of kind <see cref="DataRequestKind.Sql"/>.</remarks>
  public void Build(DataRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    switch (request.Kind) {
      case DataRequestKind.Mutation: BuildMutation(request); break;
      case DataRequestKind.Query: BuildQuery(request); break;
      case DataRequestKind.Sql: BuildHandWrittenSql(request); break;
      default: throw new NotSupportedException($"Request kind {request.Kind} is not supported.");
    }
  }

  /// <summary>Plans a save into statements and commands and sets a streamed source of them on the request, each
  /// command built as the executor reaches it.</summary>
  /// <remarks>The records are not reordered; their order is the session's. The transaction is the executor's where
  /// the save comes to more than one command or the session has one open, and the batch's own, in its text, where one
  /// command carries several statements.</remarks>
  protected void BuildMutation(DataRequest request) {
    // Planning decides how many statements there are and which records go in each; building the text is deferred.
    var forget = request.Flags.HasFlag(DataRequestFlags.InsertAndForget);
    bool ShouldReturnId(EntityRecord record) => !forget || request.ReferencedNewIdentityRecords.Contains(record);
    // A slot left on a record by a save that failed before its keys were applied must not reach this one.
    foreach (var record in request.RecordsToSave)
      record.GeneratedIdSlot = null;
    var recordGroups = MakeRecordGroups(request.RecordsToSave, ShouldReturnId);                // one statement each
    var oneStatementPerCommand = request.Session.Options.HasFlag(EntitySessionOptions.DisableBatchMode);
    var commandRecordSets = MakeCommandRecordGroupSets(recordGroups, ShouldReturnId, oneStatementPerCommand);  // one command each
    var scheduledCount = request.ScheduledRequests.Count;

    // A batch inside an executor transaction must not begin its own: SQLite refuses it and others commit early.
    var executorOwnsTransaction = commandRecordSets.Count + scheduledCount > 1 || request.Session.HasOpenTransaction;
    request.SetStreamedCommands(BuildDataCommands(commandRecordSets), commandRecordSets.Count + scheduledCount);
    request.RequiresTransaction = executorOwnsTransaction;
    request.StatementCount = recordGroups.Count + scheduledCount;
    return;

    IEnumerable<DataCommand> BuildDataCommands(IReadOnlyList<CommandRecordGroupSet> recordGroupSets) {
      foreach (var command in BuildScheduled(request, CommandSchedule.TransactionStart))
        yield return command;
      foreach (var command in BuildRecordCommands(recordGroupSets))
        yield return command;
      foreach (var command in BuildScheduled(request, CommandSchedule.TransactionEnd))
        yield return command;
    }

    IEnumerable<DataCommand> BuildRecordCommands(IReadOnlyList<CommandRecordGroupSet> recordGroupSets) {
      var recordBuilder = Provider.CreateRecordCommandBuilder(Database.DbModel);
      recordBuilder.ShouldReturnId = ShouldReturnId;
      foreach (var groupSet in recordGroupSets) {
        var command = groupSet.RecordGroups.Count == 1
          ? BuildDataCommand(recordBuilder, groupSet.RecordGroups[0])
          : BuildBatchDataCommand(recordBuilder, groupSet, ownTransaction: !executorOwnsTransaction);
        yield return command;
        // A no-op unless the command was for one record with a generated id, or a batch that carried its ids.
        recordBuilder.AssignGeneratedId(command);
      }
    }
  }

  /// <summary>Builds the save's scheduled statements for one moment of its transaction, each a non-query command.
  /// </summary>
  private IEnumerable<DataCommand> BuildScheduled(DataRequest request, CommandSchedule schedule) {
    foreach (var scheduled in request.ScheduledRequests) {
      if (scheduled.ScheduledAt != schedule)
        continue;
      var template = scheduled.SqlTemplate
        ?? throw new InvalidOperationException("A scheduled mutation carries the template its shape translated to, and this one has none.");
      // Several where the mutation stages the rows of a parameter in a temp table around its statement.
      foreach (var command in _templateCommandBuilder.BuildCommands(template, scheduled))
        yield return command;
    }
  }


  /// <summary>Builds the one statement of a group: a single row's, a multi-row insert or a keyed delete.</summary>
  protected DataCommand BuildDataCommand(RecordCommandBuilder recordBuilder, RecordGroup group) =>
    group.Count == 1 ? recordBuilder.BuildFor(group.Records[0])
      : group.Status == EntityStatus.New ? recordBuilder.BuildInsertMany(group.Records)
      : recordBuilder.BuildDeleteMany(group.Records);

  /// <summary>Builds several statements into one command, with the transaction in its text where the batch owns it.
  /// </summary>
  /// <param name="recordBuilder">The builder of a record's statement.</param>
  /// <param name="groupSet">The record groups the batch writes, in their order.</param>
  /// <param name="ownTransaction"><see langword="true"/> to begin and commit the transaction in the batch's text;
  /// <see langword="false"/> where the executor has begun one to span several commands.</param>
  /// <remarks>Where the server carries generated ids through the batch as session variables, a final select reads
  /// them back and the batch runs as a reader.</remarks>
  protected DataCommand BuildBatchDataCommand(RecordCommandBuilder recordBuilder, CommandRecordGroupSet groupSet,
      bool ownTransaction = true) {
    var dialect = Provider.SqlDialect;
    var dbCommand = Provider.CreateDbCommand();
    var text = new System.Text.StringBuilder();
    var recordCount = 0;

    recordBuilder.BatchDbCommand = dbCommand;
    try {
      foreach (var group in groupSet.RecordGroups) {
        var command = BuildDataCommand(recordBuilder, group);
        if (text.Length > 0)
          text.Append(dialect.StatementSeparator);
        text.Append(command.DbCommand.CommandText);
        recordCount += group.Count;      // rows, not statements; the description says both
      }
    } finally {
      recordBuilder.BatchDbCommand = null;
    }

    // The records that left a key in a slot. Where the dialect reads the keys back by a statement, it selects each
    // slot under its own name, so the keys are applied by name.
    var batchRecords = groupSet.RecordGroups.SelectMany(g => g.Records).ToArray();
    var slotNames = batchRecords.Where(r => r.GeneratedIdSlot != null).Select(r => r.GeneratedIdSlot!.Name).ToArray();
    var executionType = DbExecutionType.NonQuery;
    IResultReader? resultReader = null;
    if (slotNames.Length > 0 && dialect.BatchReadIdentities.Length > 0) {
      text.Append(dialect.StatementSeparator).Append(string.Format(dialect.BatchReadIdentities,
        string.Join(", ", slotNames.Select(name => recordBuilder.GetIdSlotReference(name) + " AS " + name))));
      executionType = DbExecutionType.Reader;
      resultReader = GeneratedIdsReader.Instance;
    }

    // The dialect's fragments carry their own terminator and the statements do not.
    dbCommand.CommandText = dialect.BatchBegin + (ownTransaction ? dialect.BatchBeginTransaction : "")
      + dialect.LineBreak + text + dialect.StatementSeparator
      + (ownTransaction ? dialect.BatchCommitTransaction : "") + dialect.BatchEnd;

    return new DataCommand(dbCommand, executionType,
      $"batch of {groupSet.RecordGroups.Count} statements over {recordCount} records", resultReader) {
      CarriesItsOwnTransaction = ownTransaction,
      BatchRecords = slotNames.Length > 0 ? batchRecords : null,
    };
  }

  /// <summary>Groups the statements into commands: as many as fit the parameter budget and the statement ceiling,
  /// with a statement whose result is needed before the next one is built running alone.</summary>
  /// <param name="groups">The statements, one record group each, in the order they run.</param>
  /// <param name="shouldReturnId">A function that says whether a new record needs its generated id back.</param>
  /// <param name="oneStatementPerCommand"><see langword="true"/> to put every statement in a command of its own, as
  /// <see cref="EntitySessionOptions.DisableBatchMode"/> asks; otherwise, <see langword="false"/>.</param>
  /// <returns>The commands, each as the record groups it carries.</returns>
  /// <remarks>An insert that asks for a generated id runs alone unless the server carries the id through the batch;
  /// a version-matched update or delete runs alone, since its row count is what says whether the row was still the one
  /// read.</remarks>
  protected IReadOnlyList<CommandRecordGroupSet> MakeCommandRecordGroupSets(
      IReadOnlyList<RecordGroup> groups, Func<EntityRecord, bool> shouldReturnId, bool oneStatementPerCommand = false) {
    var groupSets = new List<CommandRecordGroupSet>();
    var currentGroupSet = new List<RecordGroup>();
    var paramCount = 0;
    var budget = Math.Max(1, Provider.MaxParameters - ParameterHeadroom);
    var idCarriedInBatch = Provider.Supports(DbFeatures.IdentityCarriedInBatch);
    var slotIsParameter = idCarriedInBatch && Provider.SqlDialect.BatchReadIdentities.Length == 0;

    foreach (var group in groups) {
      // A group wanting its id is a single record, so the first record answers for it.
      var returnsId = group.Status == EntityStatus.New && group.Entity.IdentityMember != null && shouldReturnId(group.Records[0]);
      var checksVersion = group.Status != EntityStatus.New && group.Entity.RowVersionMember != null;
      if (oneStatementPerCommand || (returnsId && !idCarriedInBatch) || checksVersion) {
        AddCurrentGroups();
        groupSets.Add(new CommandRecordGroupSet([group]));
        continue;
      }
      // Every record in a group binds alike, so the first speaks for all of them.
      var groupParamCount = GetParameterCount(group.Records[0]) * group.Count + (returnsId && slotIsParameter ? 1 : 0);
      if (currentGroupSet.Count >= MaxStatementsPerCommand || (currentGroupSet.Count > 0 && paramCount + groupParamCount > budget))
        AddCurrentGroups();
      currentGroupSet.Add(group);
      paramCount += groupParamCount;
    }
    AddCurrentGroups();
    return groupSets;

    // ----- local functions

    void AddCurrentGroups() {
      if (currentGroupSet.Count == 0)
        return;
      groupSets.Add(new CommandRecordGroupSet(currentGroupSet.ToArray()));
      currentGroupSet.Clear();
      paramCount = 0;
    }
  }

  /// <summary>Groups the records into the statements they become, in the order they must run: a run of one entity
  /// in one status collapses into multi-row statements where that is sound, cut by the parameter and row ceilings.
  /// </summary>
  protected IReadOnlyList<RecordGroup> MakeRecordGroups(IReadOnlyList<EntityRecord> records,
      Func<EntityRecord, bool>? shouldReturnId = null) {
    shouldReturnId ??= _ => true;
    var groups = new List<RecordGroup>();
    var start = 0;
    while (start < records.Count) {
      var first = records[start];
      var collapses = CanUseOneStatementFor(first, shouldReturnId(first));
      // A run is cut where a record wants its id and the one before did not, or the other way round.
      var run = 1;
      while (start + run < records.Count
          && records[start + run].Entity == first.Entity
          && records[start + run].Status == first.Status
          && CanUseOneStatementFor(records[start + run], shouldReturnId(records[start + run])) == collapses)
        run++;

      if (run == 1 || !collapses) {
        for (var i = 0; i < run; i++)
          groups.Add(new RecordGroup([records[start + i]]));
      } else {
        // At least one row per statement even where a single row exceeds the budget; the server will say so.
        var perStatement = Math.Max(1, Provider.MaxParameters / Math.Max(1, GetParameterCount(first)));
        if (first.Status == EntityStatus.New)
          perStatement = Math.Min(perStatement, Provider.MaxRowsPerInsert);
        for (var i = 0; i < run; i += perStatement)
          groups.Add(new RecordGroup(records.Skip(start + i).Take(Math.Min(perStatement, run - i)).ToArray()));
      }
      start += run;
    }
    return groups;
  }

  /// <summary>Returns a value that indicates whether a run of records like this one may become a single multi-row
  /// statement.</summary>
  /// <remarks>An update never collapses: its changed mask is its column list. An insert of a server-generated id
  /// collapses only when the key is not wanted back, since keys would otherwise be matched to records by position,
  /// which no server guarantees. A delete does not collapse for a composite key, a self-referencing entity, where an
  /// unordered <c>IN</c> list could delete a parent before its child, or a versioned entity, whose deletes are checked
  /// one by one.</remarks>
  protected bool CanUseOneStatementFor(EntityRecord first, bool shouldReturnId = true) {
    var entity = first.Entity;
    if (Database.DbModel.GetTable(entity) == null)
      return false;                          // not mapped in this database: there is no statement to collapse into

    switch (first.Status) {
      case EntityStatus.New:
        // Oracle's INSERT ALL evaluates a sequence once per statement, so an identity table needs the feature flag.
        return entity.IdentityMember == null
          || (!shouldReturnId && Provider.Supports(DbFeatures.BulkInsertAdvancesIdentity));

      case EntityStatus.Deleted:
        return entity.PrimaryKey is { MembersExpanded.Count: 1 }
          && !entity.Flags.HasFlag(EntityTypeFlags.SelfReferencing)
          && entity.RowVersionMember == null;

      default:
        return false;
    }
  }

  /// <summary>Returns the number of parameters the record's statement binds, as the record builder will bind them: every
  /// insert column for an insert, the key for a delete, the changed columns and the key for an update.</summary>
  protected int GetParameterCount(EntityRecord record) {
    var entity = record.Entity;
    if (Database.DbModel.GetTable(entity) is not { } table)
      return 0;
    var keyCount = entity.PrimaryKey?.MembersExpanded.Count ?? 0;

    return record.Status switch {
      EntityStatus.New => table.InsertColumns.Count,
      EntityStatus.Deleted => keyCount,
      EntityStatus.Modified => keyCount + record.ModifiedMembers.Count(
        m => table.GetColumn(m) != null && !m.IsDbGenerated && !m.IsPrimaryKey),
      _ => 0,
    };
  }

  /// <summary>Builds a query's one command from the template the executor found for its shape, bound in the request.
  /// </summary>
  protected void BuildQuery(DataRequest request) {
    var template = request.SqlTemplate
      ?? throw new InvalidOperationException("A query request carries the template its shape translated to, and this one has none.");
    request.SetCommands(_templateCommandBuilder.BuildCommands(template, request));
  }

  /// <summary>Builds a hand-written statement's one command: every <c>@name</c> converted to the provider's prefix and
  /// the name, and bound from the request's parameters.</summary>
  /// <remarks>Every value needs a name in the text, and every name needs a value,   /// prefix is shared with user variables: there a name with no value is a variable and is left in the text.</remarks>
  protected void BuildHandWrittenSql(DataRequest request) {
    var text = SqlTextHelper.ConvertParameters(request.Sql!, Provider.ParameterPrefix, out var names);
    var dbCommand = CreateDbCommand(text);
    foreach (var name in names) {
      var given = request.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
      if (given.Name == null) {
        if (Provider.Supports(DbFeatures.ParameterPrefixSharedWithVariables))
          continue;
        throw new ArgumentException($"The statement names a parameter '{name}' and no value was given for it.");
      }
      Provider.AddParameter(dbCommand, name, given.Value ?? DBNull.Value);
    }
    foreach (var (name, _) in request.Parameters)
      if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
        throw new ArgumentException($"A value was given for '{name}', which the statement does not name.");
    request.SetCommands([new DataCommand(dbCommand, request.ExecutionType, request.Description, request.ResultReader) {
      BulkInsert = request.BulkInsert,
    }]);
  }

  /// <summary>Creates a driver command with the text set and no connection.</summary>
  protected System.Data.Common.DbCommand CreateDbCommand(string sql) {
    var dbCommand = Provider.CreateDbCommand();
    dbCommand.CommandText = sql;
    return dbCommand;
  }
}
