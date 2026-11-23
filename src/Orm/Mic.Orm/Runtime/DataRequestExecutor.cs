using System.Data.Common;
using System.Diagnostics;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Readers;
using Mic.Orm.Db.Sql;
using Mic.Orm.Logging;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Runs a <see cref="DataRequest"/> of any session of its database over that session's connection.</summary>
/// <remarks>One executor serves every session of a database and holds nothing of any of them. A request is answered
/// from the entity cache where it can be, and by the statement its shape translated to otherwise; the loads, the
/// includes and the smart load, which make requests of their own, are the session's.</remarks>
public class DataRequestExecutor {
  /// <summary>Gets the database the executor runs requests against.</summary>
  public Database Database { get; }

  /// <summary>Gets the builder that turns a request into its commands.</summary>
  public DataCommandSetBuilder CommandSetBuilder { get; }

  /// <summary>Distinguishes the by-key shape's locked statement from its plain one in the query cache.</summary>
  const ulong LockedShapeSalt = 0x4C4F434B5F555044UL;   // "LOCK_UPD"

  /// <summary>Initializes a new instance of the <see cref="DataRequestExecutor"/> class.</summary>
  public DataRequestExecutor(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    Database = database;
    CommandSetBuilder = new DataCommandSetBuilder(database);
  }

  /// <summary>Asynchronously runs a request and returns its result, which is also left on the request as
  /// <see cref="DataRequest.Result"/>.</summary>
  /// <returns>A query's records, rows, values or one value; the row read by key, or <see langword="null"/>; a
  /// hand-written statement's rows, value or row count; <see langword="null"/> for a save.</returns>
  /// <remarks>The run finishes only a transaction it began; commands join a transaction the session already has open.
  /// </remarks>
  /// <exception cref="DataAccessException">A command failed, or an update or a delete matching on the row's version
  /// found no row.</exception>
  public async Task<object?> ExecuteAsync(DataRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    using var turn = await request.Session.EnterTurnAsync();
    // A temp table registered since the last request is created and filled first, so that this request can read it.
    await request.Session.CreatePendingTempTablesAsync();
    await PrepareAsync(request);
    if (await TryGetFromCacheAsync(request))
      return request.Result;
    CommandSetBuilder.Build(request);
    await RunCommandsAsync(request);
    ReadResult(request);
    return request.Result;
  }

  /// <summary>Returns the query cache's entry for the request's query shape, building the tree and translating it
  /// if the shape is new.</summary>
  /// <remarks>A session with an extension disabled reads without the extension's filters, a shape the cache does not
  /// hold under the query's key: its queries are translated each time, with the filters left out, and kept nowhere.
  /// </remarks>
  internal QueryCacheEntry TranslateIfNew(DataRequest request) {
    var query = request.Query!;
    var rootBuilder = request.RootBuilder!;
    if (request.Session.HasDisabledExtensions)
      return Translate(query, request.Session.WrapWithoutDisabledFilters(rootBuilder.Build()));
    // A query that bypasses the cache is neither looked up nor kept: translated each time, for a shape that will not
    // repeat, and so for the reading its options ask for.
    if (query.Options.HasFlag(QueryOptions.BypassQueryCache))
      return Translate(query, rootBuilder.Build(), query.Options.HasFlag(QueryOptions.SkipEntityCache));
    if (Database.QueryCache.Hit(query.CacheKey) is { } entry)
      return entry;
    return Database.QueryCache.AddEntry(query.CacheKey, Translate(query, rootBuilder.Build()));
  }

  /// <summary>Translates a query's tree into the entry that runs it: the plan where its entities are cached, the
  /// template of its statement, and its includes; the cache is not told.</summary>
  internal QueryCacheEntry Translate(QueryInfo query, QueryNode root, bool skipEntityCache = false) {
    var translation = QueryTranslator.Translate(query, root, Database.DbModel, skipEntityCache);
    var plan = translation.CanRunInCache
      ? new CacheQuery(translation.CacheEvaluator!, translation.CachedEntities, translation.PreparedRoot.IsFirstLastSingle())
      : null;
    return new QueryCacheEntry(plan, SqlBuilder.Build(translation, Database.DbModel), translation.Includes);
  }

  // ----- the line through: prepare, answer from memory, build, run, read ----------------------------------------------

  /// <summary>Fills in what the run needs beyond what was asked: a query's statement or plan, a hand-written
  /// statement's reader, a save's scheduled mutations made into query requests and prepared.</summary>
  private async Task PrepareAsync(DataRequest request) {
    switch (request.Kind) {
      case DataRequestKind.Query:
        await PrepareQueryAsync(request);
        break;
      case DataRequestKind.Sql:
        if (request.ReadResultAs is { } readAs)
          request.ResultReader = CreateRowReader(request.Session, readAs);
        break;
      case DataRequestKind.Mutation:
        await AssignSequenceValuesAsync(request);
        if (request.ScheduledMutations.Count > 0)
          await PrepareScheduledMutationsAsync(request);
        break;
    }
  }

  /// <summary>Asynchronously makes a save's scheduled mutations into query requests, each prepared, in the order
  /// scheduled.</summary>
  private async Task PrepareScheduledMutationsAsync(DataRequest save) {
    var scheduled = new List<DataRequest>(save.ScheduledMutations.Count);
    foreach (var mutation in save.ScheduledMutations) {
      var scheduledRequest = CreateScheduledRequest(save, mutation);
      await PrepareQueryAsync(scheduledRequest);
      scheduled.Add(scheduledRequest);
    }
    save.ScheduledRequests = scheduled;
  }

  /// <summary>Asynchronously draws the next values from each sequence a save's new records need and puts them on
  /// the records, one statement per sequence, before the save's commands are built.</summary>
  /// <remarks>A sequence member the application assigned, with anything but its type's default, keeps its value.
  /// On a server without sequences the column is an ordinary one and nothing is drawn. A drawn value that is a key
  /// reaches the records referencing it when their statements are built, through
  /// <see cref="MemberValueAccessorHelper.RefreshForeignKeys"/>, which copies a target's key into the foreign key.
  /// </remarks>
  private async Task AssignSequenceValuesAsync(DataRequest save) {
    Dictionary<DbSequenceInfo, List<(EntityRecord Record, EntityMemberInfo Member)>>? needed = null;
    foreach (var record in save.RecordsToSave) {
      if (record.Status != EntityStatus.New || record.Entity.SequenceMembers.Count == 0)
        continue;
      var table = Database.DbModel.GetTable(record.Entity);
      foreach (var member in record.Entity.SequenceMembers) {
        if (table?.GetColumn(member)?.Sequence is not { } sequence)
          continue;
        if (record.GetRawValue(member) is { } assigned && !Equals(assigned, member.DefaultValue))
          continue;
        needed ??= new(ReferenceEqualityComparer.Instance);
        if (!needed.TryGetValue(sequence, out var slots))
          needed[sequence] = slots = [];
        slots.Add((record, member));
      }
    }
    if (needed == null)
      return;
    foreach (var (sequence, slots) in needed) {
      var drawn = await DrawSequenceValuesAsync(save.Session, sequence, slots.Count, save.Cancellation);
      for (var i = 0; i < slots.Count; i++)
        slots[i].Record.SetRawValue(slots[i].Member, Database.Provider.ConvertReadValue(drawn[i], slots[i].Member.DataType));
    }
  }

  /// <summary>Asynchronously draws the next values from a sequence, in one statement.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sequence">The sequence.</param>
  /// <param name="count">How many values to draw, at least one.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the values, in the order
  /// drawn.</returns>
  internal async Task<IReadOnlyList<long>> DrawSequenceValuesAsync(EntitySession session, DbSequenceInfo sequence,
      int count, CancellationToken cancellation) {
    var dialect = Database.Provider.SqlDialect;
    var sql = string.Format(dialect.SequenceNextValues, dialect.QualifiedName(sequence.Schema.Name, sequence.Name), count);
    var drawn = await ExecuteAsync(new DataRequest(session, sql, [], DbExecutionType.Reader,
      new ValueListReader(Database.Provider, typeof(long)), description: $"next {count} of {sequence.Name}") {
      Cancellation = cancellation,
    }) as List<object?>;
    if (drawn == null || drawn.Count != count)
      throw new InvalidOperationException(
        $"Sequence '{sequence.Name}' answered {drawn?.Count ?? 0} value(s) where {count} were asked for.");
    var values = new long[count];
    for (var i = 0; i < count; i++)
      values[i] = Convert.ToInt64(drawn[i]);
    return values;
  }

  /// <summary>Creates the query request a scheduled mutation runs as: the typed expression scanned, its tree built
  /// by conversion, or the by-name root with its arguments.</summary>
  private static DataRequest CreateScheduledRequest(DataRequest save, ScheduledMutation mutation) {
    var description = $"scheduled at {mutation.Schedule}";
    return mutation.Expression is { } expression
      ? new DataRequest(save.Session, ExpressionTreeScanner.Scan(expression),
        new QueryTreeBuilder(() => ExpressionTreeConverter.Convert(expression)), description) {
        ScheduledAt = mutation.Schedule,
        TransactionId = save.TransactionId,
        Cancellation = save.Cancellation,
      }
      : new DataRequest(save.Session, mutation.Root!, mutation.Arguments, description: description) {
        ScheduledAt = mutation.Schedule,
        TransactionId = save.TransactionId,
        Cancellation = save.Cancellation,
      };
  }

  /// <summary>Translates the query's shape if it is new and sets either the plan that answers it from memory or
  /// its statement, with the rows of the cached entities it reads loaded; marks the request
  /// <see cref="DataRequestFlags.CanRunInCache"/> where the entity cache answers it.</summary>
  private async Task PrepareQueryAsync(DataRequest request) {
    if (request.LookupKey is { } key) {
      // A locked read goes to the server whatever is cached, under the by-key shape's locked statement; so does a
      // read that skips the cache by flag, and a read of a session with an extension disabled, since the cached
      // block was filled under the filters.
      var locked = request.Flags.HasFlag(DataRequestFlags.LockForUpdate);
      var skips = request.Flags.HasFlag(DataRequestFlags.SkipEntityCache);
      if (!locked && !skips && !request.Session.HasDisabledExtensions && EntityCache.IsCached(key.Entity)) {
        request.Flags |= DataRequestFlags.CanRunInCache;
        return;
      }
      SetByKeyQuery(request, key, locked, skips);
    }
    var query = request.Query!;
    var entry = request.CacheEntry ?? TranslateIfNew(request);
    // A lock lives on the server, so a locked query skips the entity cache whether or not the caller said so.
    var skipsEntityCache = (query.Options
      & (QueryOptions.SkipEntityCache | QueryOptions.LockForUpdate | QueryOptions.SkipLocked)) != 0;
    // What a lock holds is the row as the server has it, so a tracked record is refreshed from it.
    if ((query.Options & (QueryOptions.LockForUpdate | QueryOptions.SkipLocked)) != 0)
      request.Flags |= DataRequestFlags.RefreshTracked;
    if (!skipsEntityCache && entry.CacheQuery is { } cacheQuery) {
      request.CacheQuery = cacheQuery;
      request.CachedRows = await LoadCachedRowsAsync(request.Session, cacheQuery.Entities);
      request.Flags |= DataRequestFlags.CanRunInCache;
      return;
    }
    var template = entry.SqlTemplate
      ?? throw new InvalidOperationException("The query's shape has no statement — it runs in memory, and the cache was skipped.");
    // The shape's statement may take a sub-query over a cached entity as a parameter answered from memory. A caller
    // that skips the cache reads the server there too, under the shape's other statement, translated on first use.
    if (query.Options.HasFlag(QueryOptions.SkipEntityCache) && template.CachedEntities.Count > 0
        && request.RootBuilder is { } rootBuilder)
      template = entry.GetServerOnlyTemplate(() => SqlBuilder.Build(
        QueryTranslator.Translate(query, rootBuilder.Build(), Database.DbModel, skipEntityCache: true),
        Database.DbModel));
    CheckArgumentCount(query, template);
    request.SqlTemplate = template;
    request.CachedRows = await LoadCachedRowsAsync(request.Session, template.CachedEntities);
  }

  /// <summary>Sets the request's query to the read of one row of an entity by the values of one of its keys, under
  /// the key's own cache key.</summary>
  private static void SetByKeyQuery(DataRequest request, EntityKey key, bool lockForUpdate, bool skipEntityCache) {
    var members = key.KeyInfo.MembersExpanded;
    var values = new object?[members.Count];
    for (var i = 0; i < members.Count; i++)
      values[i] = members.Count == 1 ? key.SingleValue : key.ValueAt(i);
    var cacheKey = lockForUpdate ? key.KeyInfo.SelectByKeyCacheKey ^ LockedShapeSalt : key.KeyInfo.SelectByKeyCacheKey;
    // The shape's statement is taken instead of its in-memory plan where the cache is to be skipped.
    var options = lockForUpdate ? QueryOptions.LockForUpdate | QueryOptions.SkipEntityCache
      : skipEntityCache ? QueryOptions.SkipEntityCache : QueryOptions.None;
    request.Query = new QueryInfo(cacheKey, values, options);
    request.RootBuilder = new QueryTreeBuilder(() => MemberQueryHelper.BuildByKeyQuery(key.KeyInfo.Entity, key.KeyInfo.GetMembers()));
  }

  /// <summary>Answers a query from the entity cache where preparation found that it can.</summary>
  /// <returns><see langword="true"/> if the request's result was set from the cache; otherwise,
  /// <see langword="false"/>.</returns>
  private async Task<bool> TryGetFromCacheAsync(DataRequest request) {
    if (request.Kind != DataRequestKind.Query || !request.Flags.HasFlag(DataRequestFlags.CanRunInCache))
      return false;
    request.Result = request.LookupKey is { } key
      ? await Database.EntityCache.FindByKeyAsync(key, request.Session)
      : Database.EntityCache.Run(request.CacheQuery!, request);
    return true;
  }

  /// <summary>Runs the request's commands over the session's connection, inside a transaction where they need one,
  /// and records what ran.</summary>
  private async Task RunCommandsAsync(DataRequest request) {
    var connection = request.Session.GetConnection();
    var runMode = request.Session.RunMode;
    var cancellation = request.Cancellation;
    var started = Stopwatch.GetTimestamp();
    var index = 0;
    var ownsTransaction = false;

    // A session with no gate makes nobody wait: a second operation that reaches the connection while this one is on
    // it is refused here, before it opens, closes or reads anything. The session is let go of before the handlers of
    // a failure and of the commands that ran, which may use it.
    var session = request.Session;
    session.EnterCommandRun();
    try {
      await connection.OpenAsync(runMode, cancellation);
      if (request.RequiresTransaction && connection.DbTransaction == null) {
        await connection.BeginTransactionAsync(runMode, commitOnSave: false, cancellation: cancellation);
        ownsTransaction = true;
      }

      // A save builds each command as it is reached, so that a generated key is on its record before the next
      // statement binds it.
      foreach (var command in request.ProduceCommands()) {
        command.RunMode = runMode;
        await ExecuteOneAsync(connection, command, cancellation);
        // A version-matched update or delete that found no row is a concurrent update, refused before the next command.
        if (command.ExpectedRowCount >= 0 && command.RowCount != command.ExpectedRowCount)
          throw CreateConcurrentUpdateException(command);
        index++;
      }

      // A transaction begun for the caller by a locked read is committed by the save.
      if (ownsTransaction || (request.Kind == DataRequestKind.Mutation && connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave))) {
        await connection.CommitAsync(runMode);
      }
    } catch (Exception thrown) when (FindDbException(thrown) is { } ex) {
      if (ownsTransaction || connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave))
        await connection.AbortAsync(runMode);
      await RollBackBatchAsync(connection, request, index, cancellation);
      // After the rollback: the failed statement has poisoned the transaction on PostgreSQL.
      var culprit = await FindTheRowAsync(connection, request, index, cancellation);
      var failure = CreateDataAccessException(ex, request, index, culprit);
      session.ExitCommandRun();
      ProcessError(request, failure);
      throw failure;
    } catch (Exception ex) {
      // A cancellation, a reader that threw or a refused concurrent update leaves the transaction unfinished too.
      if (ownsTransaction || connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave))
        await connection.AbortAsync(runMode);
      session.ExitCommandRun();
      ProcessError(request, ex);
      throw;
    } finally {
      session.ExitCommandRun();
      RecordWhatRan(request);
    }

    // A transaction open here is the caller's, spanning several requests; the connection stays with it.
    if (request.Session.Context.ConnectionReuseMode == DbConnectionReuseMode.NoReuse
        && connection.Lifetime == DbConnectionLifetime.Operation
        && connection.DbTransaction == null)
      await connection.CloseAsync(runMode);
    request.TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
  }

  /// <summary>Takes the session's snapshot of the commands, logs them, raises the command event for each and
  /// disposes the driver commands.</summary>
  /// <remarks>Done in one walk before disposal, since a log entry reads the parameters off the live driver command.
  /// On the failure path the commands are those that were attempted.</remarks>
  private void RecordWhatRan(DataRequest request) {
    var snapshots = new List<DataCommandSnapshot>(request.DataCommands.Count);
    var session = request.Session;
    var context = session.Context;
    var logging = context.LogMode != LogMode.None;
    foreach (var command in request.DataCommands) {
      snapshots.Add(new DataCommandSnapshot(command.DbCommand.CommandText, command.Description,
        command.ExecutionType, command.RowCount, command.TimeMs));
      if (logging)
        context.Log(new DbCommandLogEntry(session.LogSource, context.Now, command.DbCommand,
          command.ExecutionType, command.RowCount, command.TimeMs) { TransactionId = request.TransactionId });
      command.DbCommand.Dispose();
    }
    session.SetLastCommands(snapshots);
    var events = context.App.Events;
    foreach (var snapshot in snapshots)
      events.OnCommandExecuted(session, snapshot);
  }

  /// <summary>Sets the request's result from what its commands answered and drops the cached blocks the write made
  /// stale.</summary>
  private void ReadResult(DataRequest request) {
    switch (request.Kind) {
      case DataRequestKind.Query:
        ReadQueryResult(request);
        break;
      case DataRequestKind.Sql:
        request.Result = request.ResultCommand.Result;
        InvalidateTouched(request);
        break;
      case DataRequestKind.Mutation:
        InvalidateSaved(request);
        break;
    }
  }

  /// <summary>Sets a query's result: the statement's rows, run through the part of the query that SQL could not do,
  /// or the one row for a read by key.</summary>
  private void ReadQueryResult(DataRequest request) {
    var template = request.SqlTemplate!;
    var rows = request.ResultCommand.Result;
    InvalidateWritten(template, request.Context);
    if (template.PostSqlEvaluator is { } postSql) {
      request.ResultRows = rows as IEnumerable<object?>
        ?? throw new InvalidOperationException($"The statement returned a {rows?.GetType().Name ?? "null"}, not rows for the C# above it to run over.");
      rows = postSql.Evaluate(request);
    }
    request.Result = request.LookupKey == null ? rows : (rows as IReadOnlyList<EntityRecord>)?.FirstOrDefault();
  }

  // ----- running one command --------------------------------------------------------------------------------------------

  /// <summary>Asynchronously runs one command on the connection and sets its result, row count and duration.</summary>
  /// <remarks>Under <see cref="RunMode.Sync"/> on the command the driver's synchronous calls are made and the task
  /// is complete when returned.</remarks>
  protected virtual async Task ExecuteOneAsync(DataConnection connection, DataCommand command,
      CancellationToken cancellation) {
    var sync = command.RunMode == RunMode.Sync;
    // No MARS: a reader left open would fail the next command.
    await connection.CloseActiveReaderAsync(command.RunMode);
    var dbCommand = command.DbCommand;
    dbCommand.Connection = connection.DbConnection;
    dbCommand.Transaction = connection.DbTransaction;

    var started = Stopwatch.GetTimestamp();
    if (command.BulkInsert is { } bulk) {
      command.RowCount = await Database.Provider.BulkInsertAsync(connection.DbConnection, connection.DbTransaction,
        bulk, command.RunMode, cancellation);
      command.Result = command.RowCount;
      command.TimeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
      return;
    }
    switch (command.ExecutionType) {
      case DbExecutionType.NonQuery:
        command.RowCount = sync ? dbCommand.ExecuteNonQuery() : await dbCommand.ExecuteNonQueryAsync(cancellation);
        command.Result = command.RowCount;
        break;
      case DbExecutionType.Scalar:
        command.Result = sync ? dbCommand.ExecuteScalar() : await dbCommand.ExecuteScalarAsync(cancellation);
        break;
      case DbExecutionType.Reader:
        await ReadAsync(connection, command, cancellation);
        break;
    }
    command.TimeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
  }

  /// <summary>Asynchronously runs a reader command, hands the open data reader to the command's result reader and closes it before
  /// returning.</summary>
  protected virtual async Task ReadAsync(DataConnection connection, DataCommand command,
      CancellationToken cancellation) {
    var sync = command.RunMode == RunMode.Sync;
    var reader = sync ? command.DbCommand.ExecuteReader() : await command.DbCommand.ExecuteReaderAsync(cancellation);
    connection.ActiveReader = reader;
    try {
      var resultReader = command.ResultReader ?? BufferedRowsReader.Instance;
      command.Result = await resultReader.ReadAsync(command, reader, cancellation);
    } finally {
      connection.ActiveReader = null;
      if (sync)
        reader.Dispose();
      else
        await reader.DisposeAsync();
    }
  }

  // ----- failures ---------------------------------------------------------------------------------------------------------

  /// <summary>Creates the exception for an update or a delete whose row's version no longer matched.</summary>
  private DataAccessException CreateConcurrentUpdateException(DataCommand command) {
    var record = command.Record;
    var what = record == null ? command.Description ?? "a statement"
      : $"the {record.Entity.Name} with key {record.PrimaryKey}";
    return new DataAccessException(
      $"Concurrent update: {what} was changed by somebody else since this session read it — the row's version no "
      + $"longer matches, and the statement found {command.RowCount} rows where it expected {command.ExpectedRowCount}. "
      + "Reload the entity and apply the change again.") {
      SubType = DataAccessException.SubTypeConcurrentUpdate,
      DatabaseAlias = Database.Settings.DatabaseAlias,
      TableName = record == null ? null : Database.DbModel.GetTable(record.Entity)?.Name,
      CommandSummary = command.DbCommand.CommandText,
    };
  }

  /// <summary>Creates the exception for a failed command: which command, its text and values, and the row the
  /// server refused where one was found.</summary>
  /// <summary>Finds the driver's failure in an exception: the exception itself, or the one inside an
  /// <see cref="AggregateException"/>, which IBM's driver throws when a row fails to be read.</summary>
  private static DbException? FindDbException(Exception thrown) =>
    thrown as DbException ?? (thrown as AggregateException)?.GetBaseException() as DbException;

  protected virtual DataAccessException CreateDataAccessException(DbException ex, DataRequest request, int index,
      string? culprit = null) {
    var command = index < request.DataCommands.Count ? request.DataCommands[index] : null;
    var where = command == null
      ? "while opening the connection"
      : $"at command {index + 1} of {request.ExpectedDataCommandCount}";
    var summary = command == null ? null : DbCommandHelper.Describe(command.DbCommand);
    var failure = Database.Provider.Fail(
      $"Database '{Database.Settings.DatabaseAlias}': {request.Description} failed {where}. "
      + ex.Message + culprit + (summary == null ? "" : Environment.NewLine + summary),
      ex, Database.Settings.DatabaseAlias);
    failure.CommandSummary = summary;
    return failure;
  }

  /// <summary>Asynchronously rolls back a batch that began a transaction in its own text and failed before its
  /// commit. Best effort; a failure here is swallowed.</summary>
  protected virtual async Task RollBackBatchAsync(DataConnection connection, DataRequest request, int index,
      CancellationToken cancellation) {
    if (index >= request.DataCommands.Count || !request.DataCommands[index].CarriesItsOwnTransaction)
      return;
    try {
      using var rollback = Database.Provider.CreateDbCommand();
      rollback.Connection = connection.DbConnection;
      rollback.CommandText = Database.Provider.SqlDialect.BatchRollbackTransaction;
      if (request.Session.RunMode == RunMode.Sync)
        rollback.ExecuteNonQuery();
      else
        await rollback.ExecuteNonQueryAsync(cancellation);
    } catch {
      // Nothing to add: an exception is already on its way out.
    }
  }

  /// <summary>Asynchronously finds which row the server refused when the failed statement carried many, by replaying
  /// them one at a time inside a transaction that is rolled back.</summary>
  /// <returns>A sentence naming the refused row, or <see langword="null"/> where the statement carried one row or
  /// every row passed alone.</returns>
  protected virtual async Task<string?> FindTheRowAsync(DataConnection connection, DataRequest request,
      int index, CancellationToken cancellation) {
    if (index >= request.DataCommands.Count || request.DataCommands[index].Records is not { Count: > 1 } records)
      return null;

    try {
      var runMode = request.Session.RunMode;
      var recordBuilder = Database.Provider.CreateRecordCommandBuilder(Database.DbModel);
      await connection.OpenAsync(runMode, cancellation);
      await connection.BeginTransactionAsync(runMode, commitOnSave: false, cancellation: cancellation);
      try {
        for (var row = 0; row < records.Count; row++) {
          using var rowCommand = recordBuilder.BuildFor(records[row]).DbCommand;
          rowCommand.Connection = connection.DbConnection;
          rowCommand.Transaction = connection.DbTransaction;
          try {
            if (runMode == RunMode.Sync)
              rowCommand.ExecuteNonQuery();
            else
              await rowCommand.ExecuteNonQueryAsync(cancellation);
          } catch (DbException) {
            var record = records[row];
            return Environment.NewLine
              + $"The statement covered {records.Count} rows; row {row + 1} is the one the server refused — "
              + $"{record.Entity.Name} with key {record.PrimaryKey}.";
          }
        }
        return null;      // every row passes alone: the objection is to the set, not to any one of them
      } finally {
        await connection.AbortAsync(runMode);
      }
    } catch {
      return null;
    }
  }

  /// <summary>Reports a failed request to the operation's log and the application's error event. A cancellation is
  /// not reported.</summary>
  private static void ProcessError(DataRequest request, Exception error) {
    if (error is OperationCanceledException)
      return;
    var context = request.Context;
    context.LogError($"{request.Description} failed: {error.Message}", error);
    context.App.Events.OnError(context, error);
  }

  // ----- the entity cache's consequences ------------------------------------------------------------------------------

  /// <summary>Drops the context's cached block of the entity a mutation statement wrote, where the entity is cached.
  /// </summary>
  private void InvalidateWritten(SqlTemplate template, OperationContext context) {
    if (template.ResultKind == QueryResultKind.Affected && template.Result?.Entity is EntityTypeInfo written
        && EntityCache.IsCached(written))
      Database.EntityCache.Invalidate(written, context);
  }

  /// <summary>Drops the context's cached block of every cached entity a save wrote, by a record or by a scheduled
  /// statement.</summary>
  private void InvalidateSaved(DataRequest request) {
    HashSet<EntityTypeInfo>? entities = null;
    foreach (var record in request.RecordsToSave)
      if (EntityCache.IsCached(record.Entity))
        (entities ??= []).Add(record.Entity);
    if (entities != null)
      foreach (var entity in entities)
        Database.EntityCache.Invalidate(entity, request.Context);
    foreach (var scheduled in request.ScheduledRequests)
      if (scheduled.SqlTemplate != null)
        InvalidateWritten(scheduled.SqlTemplate, request.Context);
  }

  /// <summary>Drops the context's cached blocks of the entities a hand-written statement declared it wrote.</summary>
  private void InvalidateTouched(DataRequest request) {
    foreach (var type in request.MutatesEntityTypes) {
      var entity = FindEntity(request.Context, type);
      if (EntityCache.IsCached(entity))
        Database.EntityCache.Invalidate(entity, request.Context);
    }
  }

  // ----- what preparation makes ----------------------------------------------------------------------------------------

  /// <summary>Creates the result reader that reads a hand-written statement's rows as the named type: tracked records
  /// for an entity interface, the first column of every row for a scalar type, one instance per row built by column
  /// name for any other class or record.</summary>
  private IResultReader CreateRowReader(EntitySession session, Type readAs) {
    if (readAs.IsInterface)
      return new SqlEntityReader(session, FindEntity(session.Context, readAs));
    if (readAs.IsScalarType())
      return new ValueListReader(Database.Provider, readAs);
    var readerType = typeof(SqlObjectReader<>).MakeGenericType(readAs);
    return (IResultReader)Activator.CreateInstance(readerType, Database.Provider)!;
  }

  private static EntityTypeInfo FindEntity(OperationContext context, Type type) =>
    context.App.Model!.FindEntity(type)
      ?? throw new NotSupportedException($"'{type.Name}' is not an entity of this application's model.");

  /// <summary>Refuses a by-name query whose argument count is not the statement's.</summary>
  /// <remarks>The count is checked exactly because too many arguments is the usual symptom of a reference-type array
  /// passed as one list argument and spread into one argument per element. A typed query's arguments are the objects
  /// its scan captured and are taken as they are.</remarks>
  private static void CheckArgumentCount(QueryInfo query, SqlTemplate template) {
    var arguments = query.Arguments;
    if (query.IsTyped || arguments.Count == template.ArgumentCount)
      return;
    throw new ArgumentException(
      $"The query takes {template.ArgumentCount} argument(s) and {arguments.Count} were given."
      + (arguments.Count > template.ArgumentCount
        ? " A collection passed as one argument has to be wrapped — `[values]`, not `values` — or it spreads into"
          + " one argument per element."
        : ""));
  }

  /// <summary>Asynchronously loads the blocks of the given cached entities and returns what reads their rows;
  /// <see langword="null"/> where there are none.</summary>
  private async Task<Func<ILinqEntity, IReadOnlyList<object>>?> LoadCachedRowsAsync(EntitySession session,
      IReadOnlyList<ILinqEntity> entities) =>
    entities.Count == 0 ? null : await Database.EntityCache.GetBlocksAsync(entities, session);
}
