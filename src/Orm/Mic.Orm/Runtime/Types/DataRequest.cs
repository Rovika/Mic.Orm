using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using Mic.Orm.Db.Readers;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Represents one unit of database work for a session — a query, a read by key, a hand-written statement
/// or a save — together with what the executor fills in while running it: the statement, the commands, the result.
/// </summary>
/// <remarks>The members apply by <see cref="Kind"/>; a member of another kind is <see langword="null"/> or empty. The
/// request is also the <see cref="ILinqCallContext"/> a query's parameters bind in and its evaluator runs in.</remarks>
public sealed class DataRequest : ILinqCallContext {
  // ----- what was asked ---------------------------------------------------------------------------------------------

  /// <summary>Gets the kind of work this request is, which decides which members apply.</summary>
  public DataRequestKind Kind { get; }

  /// <summary>Gets the session the request was made for.</summary>
  public EntitySession Session { get; }

  /// <summary>Gets the operation context of the session.</summary>
  public OperationContext Context => Session.Context;

  /// <summary>Gets the short name a log line or an error message uses for this request.</summary>
  public string Description { get; }

  /// <summary>Gets or initializes the token that cancels the run. The default is the session's operation token.
  /// </summary>
  public CancellationToken Cancellation { get; init; }

  /// <summary>Gets the query to run: what identifies it, its arguments and its options. For a read by key that goes
  /// to the server the executor sets it from <see cref="LookupKey"/>.</summary>
  /// <remarks>A query built by name passes one value per parameter and the count is checked exactly. A typed query
  /// passes the objects its scan captured, which may be more than the statement binds; the surplus goes unused.
  /// </remarks>
  public QueryInfo? Query { get; internal set; }

  /// <summary>Gets the builder of the query's tree, asked for a shape the query cache has not seen; or
  /// <see langword="null"/> for a query that brings its entry.</summary>
  public QueryTreeBuilder? RootBuilder { get; internal set; }

  /// <summary>Gets the entry the query runs under, where the caller holds one, as the smart load holds the many-owners
  /// form of a shape; or <see langword="null"/> for a query the executor finds or translates by its key.</summary>
  public QueryCacheEntry? CacheEntry { get; }

  /// <summary>Gets the values the query's parameters read; empty when there is no query.</summary>
  public IReadOnlyList<object?> Arguments => Query?.Arguments ?? [];

  /// <summary>Gets the key of the one row to look up, for a read by key.</summary>
  /// <remarks>The entity cache answers it under <see cref="DataRequestFlags.CanRunInCache"/>; otherwise the executor
  /// runs the by-key query under the key's own cache key.</remarks>
  public EntityKey? LookupKey { get; }

  /// <summary>Gets what the caller asked for beyond the request itself, and what the executor's preparation found.
  /// </summary>
  public DataRequestFlags Flags { get; internal set; }

  /// <summary>Gets or initializes when in the save's transaction a scheduled statement runs.</summary>
  public CommandSchedule ScheduledAt { get; init; }

  /// <summary>Gets or initializes the id of the save this request is or belongs to, from
  /// <see cref="EntitySession.NextTransactionId"/>.</summary>
  /// <value>The id, or <see langword="null"/> for a read.</value>
  public Guid? TransactionId { get; init; }

  /// <summary>Gets the text of a hand-written statement, with each parameter written <c>{name}</c>.</summary>
  /// <remarks>The statement runs on the session's connection and inside its open transaction, and is logged and timed
  /// as a query is. It tells the session's records and the entity cache nothing; <see cref="MutatesEntityTypes"/>
  /// is where the caller says what it wrote.</remarks>
  public string? Sql { get; }

  /// <summary>Gets the values of a hand-written statement's parameters, by the names the text uses. Every name in
  /// the text needs one; a value no name uses is refused.</summary>
  public IReadOnlyList<(string Name, object? Value)> Parameters { get; }

  /// <summary>Gets how a hand-written statement is executed: for its rows, for one value, or for its row count.
  /// </summary>
  public DbExecutionType ExecutionType { get; }

  /// <summary>Gets the type each row of a hand-written statement's result is read as: an entity interface of the
  /// model for tracked records, a scalar type for the first column of every row, or any other class or record for
  /// one instance per row built by column name. <see langword="null"/> reads through <see cref="ResultReader"/>.
  /// </summary>
  public Type? ReadResultAs { get; }

  /// <summary>Gets the entity types whose rows a hand-written statement writes; their cached blocks are dropped
  /// once it has run.</summary>
  public IReadOnlyList<Type> MutatesEntityTypes { get; }

  /// <summary>Gets the records a save writes, in the order their statements must run.</summary>
  public IReadOnlyList<EntityRecord> RecordsToSave { get; }

  /// <summary>Gets the set-based mutations scheduled into a save, in the order they were scheduled.</summary>
  public IReadOnlyList<ScheduledMutation> ScheduledMutations { get; }

  /// <summary>Gets the new records whose server-made key a later record of the save refers to. A save that forgets
  /// its records still reads these keys back; empty for a save that reads every key back.</summary>
  public IReadOnlySet<EntityRecord> ReferencedNewIdentityRecords { get; }

  // ----- what the executor's preparation found -----------------------------------------------------------------------

  /// <summary>Gets the statement the query's shape translated to; <see langword="null"/> where the shape runs in
  /// memory.</summary>
  public SqlTemplate? SqlTemplate { get; internal set; }

  /// <summary>Gets the plan that answers the query from the entity cache; <see langword="null"/> where the statement
  /// runs.</summary>
  public CacheQuery? CacheQuery { get; internal set; }

  /// <summary>Gets the rows of the cached entities the query reads, loaded before it binds or evaluates;
  /// <see langword="null"/> where it reads none.</summary>
  public Func<ILinqEntity, IReadOnlyList<object>>? CachedRows { get; internal set; }

  /// <summary>Gets the rows the statement returned, for the part of the query that runs over them in memory;
  /// <see langword="null"/> until the statement has run.</summary>
  public IEnumerable<object?>? ResultRows { get; internal set; }

  /// <summary>Gets the query requests the executor made of a save's <see cref="ScheduledMutations"/>, each prepared
  /// and carrying its <see cref="ScheduledAt"/>, in the order they were scheduled.</summary>
  public IReadOnlyList<DataRequest> ScheduledRequests { get; internal set; } = [];

  /// <summary>Gets the result reader for a hand-written statement's rows.
  /// <see langword="null"/> buffers the rows into an <see cref="InfoTable"/>, unless <see cref="ReadResultAs"/> names
  /// a type, in which case the executor sets the result reader for that type.</summary>
  public IResultReader? ResultReader { get; internal set; }

  // ----- the commands, the builder's ---------------------------------------------------------------------------------

  /// <summary>Gets the commands, in order.</summary>
  /// <remarks>A save's commands are built one at a time, each after the previous one has run, so that a key the
  /// server generated is on its record before the next statement binds it. During a save this holds the commands
  /// produced so far; after a failure, the ones that were attempted.</remarks>
  public IReadOnlyList<DataCommand> DataCommands { get; private set; } = [];

  /// <summary>Gets the command whose result is the request's: the one of a query or a hand-written statement, which
  /// may follow commands that stage the rows of a parameter in a temp table.</summary>
  public DataCommand ResultCommand => _resultCommand ?? DataCommands[0];
  DataCommand? _resultCommand;

  /// <summary>Sets the command whose result is the request's, where it is not the first.</summary>
  internal void SetResultCommand(DataCommand command) => _resultCommand = command;

  /// <summary>Gets the number of commands the run will have when finished.</summary>
  public int ExpectedDataCommandCount { get; private set; }

  /// <summary>Gets the number of statements the commands carry between them.</summary>
  public int StatementCount { get; internal set; }

  /// <summary>Gets a value that indicates whether the commands run inside one transaction.</summary>
  /// <remarks>A single write statement is atomic on its own; the builder asks for a transaction only where there is
  /// more than one.</remarks>
  public bool RequiresTransaction { get; internal set; }

  // ----- the result ---------------------------------------------------------------------------------------------------

  /// <summary>Gets what the run answered: a query's records, rows, values or one value; the row read by key, or
  /// <see langword="null"/>; a hand-written statement's rows, value or row count; <see langword="null"/> for a save.
  /// </summary>
  public object? Result { get; internal set; }

  /// <summary>Gets the duration of the whole run, in milliseconds.</summary>
  public double TotalMs { get; internal set; }

  object? ILinqCallContext.OperationContext => Context;

  IEnumerable<DataCommand>? _commandProducer;
  List<DataCommand>? _producedCommands;

  /// <summary>Initializes a new request to run a query known by its info, whose tree a builder builds.</summary>
  public DataRequest(EntitySession session, QueryInfo query, QueryTreeBuilder rootBuilder, string? description = null)
      : this(DataRequestKind.Query, session, description ?? "query") {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(rootBuilder);
    Query = query;
    RootBuilder = rootBuilder;
  }

  /// <summary>Initializes a new request to run a query known by its info under an entry the caller holds.</summary>
  public DataRequest(EntitySession session, QueryInfo query, QueryCacheEntry cacheEntry, string? description = null)
      : this(DataRequestKind.Query, session, description ?? "query") {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(cacheEntry);
    Query = query;
    CacheEntry = cacheEntry;
  }

  /// <summary>Initializes a new request to run a query built by name, whose root is in hand and is scanned here.
  /// </summary>
  public DataRequest(EntitySession session, QueryNode root, IReadOnlyList<object?>? arguments = null,
      QueryOptions options = QueryOptions.None, string? description = null)
      : this(DataRequestKind.Query, session, description ?? "query") {
    ArgumentNullException.ThrowIfNull(root);
    Query = QueryTreeScanner.Scan(root, arguments, options);
    RootBuilder = new QueryTreeBuilder(() => root);
  }

  /// <summary>Initializes a new request to read one row by a key value.</summary>
  public DataRequest(EntitySession session, EntityKey key, bool lockForUpdate = false, bool skipEntityCache = false,
      string? description = null)
      : this(DataRequestKind.Query, session, description ?? $"select {key.Entity.Name} by {key.KeyInfo.Name}") {
    LookupKey = key;
    if (lockForUpdate) Flags |= DataRequestFlags.LockForUpdate;
    if (skipEntityCache) Flags |= DataRequestFlags.SkipEntityCache;
  }

  /// <summary>Initializes a new request to run a hand-written statement.</summary>
  public DataRequest(EntitySession session, string sql, IReadOnlyList<(string Name, object? Value)> parameters,
      DbExecutionType executionType, IResultReader? resultReader = null, Type? readAs = null,
      IReadOnlyList<Type>? touches = null, string? description = null)
      : this(DataRequestKind.Sql, session, description ?? "sql") {
    ArgumentException.ThrowIfNullOrWhiteSpace(sql);
    ArgumentNullException.ThrowIfNull(parameters);
    if (resultReader != null && readAs != null)
      throw new ArgumentException("A statement is read through a result reader or as a type, not both.", nameof(readAs));
    Sql = sql;
    Parameters = parameters;
    ExecutionType = executionType;
    ResultReader = resultReader;
    ReadResultAs = readAs;
    MutatesEntityTypes = touches ?? [];
  }

  /// <summary>Initializes a new request to save records and the mutations scheduled with them.</summary>
  public DataRequest(EntitySession session, IReadOnlyList<EntityRecord> records, SaveOptions options = SaveOptions.None,
      IReadOnlyList<ScheduledMutation>? scheduled = null, IReadOnlySet<EntityRecord>? referencedNewIdentities = null,
      string description = "save")
      : this(DataRequestKind.Mutation, session, description) {
    ArgumentNullException.ThrowIfNull(records);
    RecordsToSave = records;
    TransactionId = session.NextTransactionId;
    if (options.HasFlag(SaveOptions.InsertAndForget)) Flags |= DataRequestFlags.InsertAndForget;
    ScheduledMutations = scheduled ?? [];
    ReferencedNewIdentityRecords = referencedNewIdentities ?? new HashSet<EntityRecord>();
  }

  private DataRequest(DataRequestKind kind, EntitySession session, string description) {
    ArgumentNullException.ThrowIfNull(session);
    Kind = kind;
    Session = session;
    Description = description;
    Cancellation = session.Context.CancellationToken;
    Parameters = [];
    MutatesEntityTypes = [];
    RecordsToSave = [];
    ScheduledMutations = [];
    ReferencedNewIdentityRecords = new HashSet<EntityRecord>();
  }

  /// <summary>Reads a member's value off a row, which may be a record or the entity instance over one.</summary>
  public object? ReadMember(object row, ILinqEntityMember member) {
    var record = row as EntityRecord ?? EntityHelper.GetRecord(row);
    return record.GetValue((EntityMemberInfo)member);
  }

  /// <summary>Creates a row of a transient entity, such as a projection's shape or a group key.</summary>
  public ILinqRow CreateRow(ILinqEntity entity) => new ProjectionData((TransientEntityInfo)entity);

  /// <summary>Gets the loaded rows of a cached entity; <see langword="null"/> where none were loaded.</summary>
  public IReadOnlyList<object>? GetCachedRows(ILinqEntity entity) => CachedRows?.Invoke(entity);

  /// <summary>Sets the commands when all of them are known.</summary>
  /// <summary>Gets the rows a request of kind <see cref="DataRequestKind.Sql"/> writes over the server's bulk channel
  /// in place of running its text.</summary>
  internal BulkInsertInfo? BulkInsert { get; init; }

  internal void SetCommands(IReadOnlyList<DataCommand> commands) {
    DataCommands = commands;
    ExpectedDataCommandCount = commands.Count;
  }

  /// <summary>Sets a source that produces the commands one at a time, each built after the previous one has run.
  /// </summary>
  internal void SetStreamedCommands(IEnumerable<DataCommand> source, int expectedCount) {
    _commandProducer = source;
    _producedCommands = new List<DataCommand>(expectedCount);
    DataCommands = _producedCommands;
    ExpectedDataCommandCount = expectedCount;
  }

  /// <summary>Returns the commands to run, in order, building each one as it is reached where they are streamed.
  /// </summary>
  internal IEnumerable<DataCommand> ProduceCommands() {
    if (_commandProducer == null)
      return DataCommands;
    return ProduceStreamed();

    // ----- local functions
    IEnumerable<DataCommand> ProduceStreamed() {
      foreach (var command in _commandProducer) {
        _producedCommands!.Add(command);
        yield return command;
      }
    }
  }

  public override string ToString() => $"{Description}: {DataCommands.Count} commands, {TotalMs:F1} ms";
}
