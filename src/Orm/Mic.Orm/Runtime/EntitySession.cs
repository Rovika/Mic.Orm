using System.Data;
using System.Globalization;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Logging;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Represents the work of one operation over one database: the entities it holds, what it has created,
/// changed and deleted, the connection it holds for its life, and the reads and saves it runs through the database's
/// executor.</summary>
/// <remarks>A session is used by one thread at a time and never shared; concurrency is expressed by using more
/// sessions. The session decides which requests to run, including the loads, the includes and the smart load; the
/// database's <see cref="DataRequestExecutor"/> runs each one.</remarks>
public partial class EntitySession : IEntitySession, IAsyncDisposable, IDisposable {
  /// <summary>Gets the operation this session is part of.</summary>
  public OperationContext Context { get; }

  /// <summary>Gets the session's identity, which its log entries carry.</summary>
  public Guid SessionId { get; }

  /// <summary>Gets the stamp every log entry this session posts carries.</summary>
  public LogSource LogSource { get; }

  /// <summary>Gets the entity model of the application.</summary>
  public EntityAppModel Model => Context.Model;

  /// <summary>Gets the database this session reads and writes.</summary>
  public Database Database { get; }

  /// <summary>Gets or sets the options that change how the session behaves beyond what a call says.</summary>
  public EntitySessionOptions Options { get; set; }

  /// <summary>Gets a value that indicates whether the session has an extension disabled, by type or all of them.
  /// </summary>
  internal bool HasDisabledExtensions => _allExtensionsDisabled || _disabledExtensions != null;

  /// <summary>Gets the commands the last request ran, with their row counts and timings.</summary>
  /// <remarks>A debugging facility; nothing in the ORM reads it. Replaced by each request.</remarks>
  public IReadOnlyList<DataCommandSnapshot> LastCommands { get; private set; } = [];

  /// <summary>Gets how the session's database calls are made: <see cref="RunMode.Sync"/> while one of its
  /// synchronous members runs; otherwise, <see cref="RunMode.Async"/>.</summary>
  public RunMode RunMode { get; internal set; }

  /// <summary>Gets the number of synchronous calls that returned an unfinished task and waited for it.</summary>
  /// <remarks>Zero unless something on the path of a synchronous member did not follow the session's
  /// <see cref="RunMode"/>, such as a result reader of the application's that awaits the driver itself.</remarks>
  public int BlockedSyncCallCount { get; private set; }

  /// <summary>Gets the connection this session uses, once it has one; held for the session's life and disposed with
  /// it.</summary>
  public DataConnection? Connection { get; private set; }

  /// <summary>Gets the records the next save will write: new, modified and marked for deletion.</summary>
  /// <remarks>The list is live. A record joins it once, on its first change; a handler that makes new records while
  /// walking it has to walk a copy.</remarks>
  public IReadOnlyList<EntityRecord> ChangedRecords => _entityTracker.Modified;

  /// <summary>Gets the id of the next save, minted when first read and replaced once a save has written.</summary>
  /// <remarks>A save that was refused or failed keeps the id, so a retry saves under it. An application that keeps
  /// its own audit rows stamps them with it; the save's request and its log entries carry it.</remarks>
  public Guid NextTransactionId => _nextTransactionId ??= _guids.NewGuid(Database.GuidScheme);

  /// <summary>Gets the instant this session calls now, taken when first needed and cleared by a save.</summary>
  private DateTime OperationDateTime => _operationDateTime ??= _time.UtcNow;

  readonly EntityTracker _entityTracker = new();
  readonly SessionGate? _gate;
  readonly ITimeService _time;
  readonly IGuidFactory _guids;
  DateTime? _operationDateTime;
  // How many Guids the session has made at the instant; the next one's place among them.
  int _guidSequence;
  Guid? _nextTransactionId;
  HashSet<Type>? _disabledExtensions;
  bool _allExtensionsDisabled;
  bool _isSaving;
  int _isRunningCommands;

  /// <summary>Initializes a new instance of the <see cref="EntitySession"/> class.</summary>
  /// <param name="context">The operation the session is part of.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the session does not
  /// do.</param>
  /// <remarks>An application opens a session with
  /// <see cref="EntityAppExtensions.OpenSession(OperationContext, EntitySessionOptions)"/>; the constructor is for
  /// the framework and its extensions.</remarks>
  public EntitySession(OperationContext context, EntitySessionOptions options = EntitySessionOptions.None) {
    ArgumentNullException.ThrowIfNull(context);
    Context = context;
    Options = options;
    _gate = options.HasFlag(EntitySessionOptions.AddThreadSafety) ? new SessionGate() : null;
    _time = context.App.TimeService;
    _guids = context.App.GetService<IGuidFactory>();
    Database = context.App.DataAccess.GetDatabase(context);
    SessionId = _guids.NewGuid(Database.GuidScheme);
    LogSource = new LogSource(SessionId, context.CorrelationId, context.WebRequestId);
    context.RegisterDisposable((IAsyncDisposable)this);
    context.App.Events.OnSessionOpened(this);
  }

  // ----- IEntitySession ---------------------------------------------------------------------------------------------

  /// <summary>Creates a new entity of the type, tracked by this session and written by the next save.</summary>
  public TEntity NewEntity<TEntity>() where TEntity : class =>
    (TEntity)(object)NewEntity(GetEntityTypeInfo(typeof(TEntity)));

  /// <summary>Gets the entity with the primary key: the tracked one, a stub if the flags ask for one, otherwise the
  /// one loaded; <see langword="null"/> where no such row exists.</summary>
  public TEntity GetEntity<TEntity>(object primaryKeyValue, LoadFlags flags = LoadFlags.Default)
    where TEntity : class => RunSync(() => GetEntityAsync<TEntity>(primaryKeyValue, flags));

  /// <summary>Asynchronously gets the entity with the primary key: the tracked one, a stub if the flags ask for one,
  /// otherwise the one loaded; <see langword="null"/> where no such row exists.</summary>
  /// <param name="primaryKeyValue">The key value: one value, an array of values for a composite key, or an
  /// <see cref="EntityKey"/>.</param>
  /// <param name="flags">What to load and how; a locked read begins a transaction for the caller where none is open,
  /// committed by the next save.</param>
  public async Task<TEntity> GetEntityAsync<TEntity>(object primaryKeyValue,
      LoadFlags flags = LoadFlags.Default) where TEntity : class {
    ArgumentNullException.ThrowIfNull(primaryKeyValue);
    var entity = GetEntityTypeInfo(typeof(TEntity));
    var keyInfo = entity.PrimaryKey
      ?? throw new InvalidOperationException($"Entity '{entity.Name}' has no primary key to look it up by.");
    var key = primaryKeyValue switch {
      EntityKey given => given.KeyInfo == keyInfo
        ? given
        : throw new ArgumentException(
          $"The key given is a '{given.KeyInfo.Entity.Name}' key, and a '{entity.Name}' was asked for.",
          nameof(primaryKeyValue)),
      object?[] values => new EntityKey(keyInfo, values),
      _ => new EntityKey(keyInfo, primaryKeyValue),
    };

    // A locked read goes to the server whatever is tracked or cached, and refreshes a tracked record in place. A
    // server without row locks, such as SQLite, accepts the flags and reads plainly.
    if ((flags.HasFlag(LoadFlags.WriteLock) || flags.HasFlag(LoadFlags.ReadLock))
        && Database.Provider.Supports(DbFeatures.RowLocks)) {
      var forUpdate = flags.HasFlag(LoadFlags.WriteLock);
      if (!HasOpenTransaction)
        await BeginTransactionAsync(forUpdate ? IsolationLevel.ReadCommitted : Database.Provider.SnapshotIsolationLevel,
          commitOnSave: true);
      var request = new DataRequest(this, key, forUpdate);
      request.Flags |= DataRequestFlags.RefreshTracked;
      var locked = await Database.RequestExecutor.ExecuteAsync(request) as EntityRecord;
      return locked == null ? null! : (TEntity)(object)locked.EntityInstance;
    }

    if (FindTrackedEntity(key) is { } tracked)
      return (TEntity)(object)tracked;
    if (flags.HasFlag(LoadFlags.Stub))
      return (TEntity)(object)CreateStub(key);      // already looked, just above

    var record = await LoadRecordAsync(key, flags.HasFlag(LoadFlags.SkipEntityCache));
    return record == null ? null! : (TEntity)(object)record.EntityInstance;
  }

  // ----- the transaction: begun by the caller, or for the caller by a locked read -------------------------------------

  /// <summary>Gets a value that indicates whether a transaction is open on the session's connection.</summary>
  public bool HasOpenTransaction => Connection?.DbTransaction != null;

  /// <summary>Asynchronously begins a transaction on the session's connection; every request until it ends runs
  /// inside it.</summary>
  /// <param name="isolationLevel">The isolation level; the server's default where unspecified.</param>
  /// <param name="commitOnSave"><see langword="true"/> to have the next save commit the transaction; otherwise,
  /// <see langword="false"/>, for a caller that commits or rolls back itself.</param>
  public async Task BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Unspecified, bool commitOnSave = false) {
    using var turn = await EnterTurnAsync();
    if (HasOpenTransaction)
      throw new InvalidOperationException("The session already has a transaction open; commit or roll it back first.");
    await GetConnection().BeginTransactionAsync(RunMode, commitOnSave, isolationLevel, Context.CancellationToken);
  }

  /// <summary>Asynchronously commits the open transaction, if there is one.</summary>
  public async Task CommitTransactionAsync() {
    using var turn = await EnterTurnAsync();
    if (Connection is { DbTransaction: not null } connection)
      await connection.CommitAsync(RunMode);
  }

  /// <summary>Asynchronously rolls the open transaction back, if there is one. The tracked records are unaffected.
  /// </summary>
  public async Task RollbackTransactionAsync() {
    using var turn = await EnterTurnAsync();
    if (Connection is { DbTransaction: not null } connection)
      await connection.AbortAsync(RunMode);
  }

  /// <summary>Gets the session's connection, creating it on the first call.</summary>
  internal DataConnection GetConnection() => Connection ??= Database.CreateConnection();

  /// <summary>Asynchronously commits a transaction begun for the caller by a locked read, where a save had nothing
  /// to write.</summary>
  private async Task CommitOnSaveAsync() {
    if (Connection is { DbTransaction: not null } connection && connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave))
      await connection.CommitAsync(RunMode);
  }

  /// <summary>Gets all the records of the entity as a queryable to compose over and run with the async extensions.
  /// </summary>
  /// <remarks>Execution is asynchronous only, since the client runs on WebAssembly.</remarks>
  public IQueryable<TEntity> EntitySet<TEntity>(QueryOptions options = QueryOptions.None) where TEntity : class {
    return new EntityQuery<TEntity>(QueryProvider, GetEntityTypeInfo(typeof(TEntity)), options);
  }

  /// <summary>Marks an entity for deletion by the next save. A new entity is forgotten instead.</summary>
  /// <remarks>The entity's <c>Deleting</c> event is raised before the status changes; a handler may throw to stop the
  /// deletion.</remarks>
  public void DeleteEntity<TEntity>(TEntity entity) where TEntity : class {
    ArgumentNullException.ThrowIfNull(entity);
    var record = (entity as EntityBase)?.Record
      ?? throw new ArgumentException($"{typeof(TEntity).Name} is not a MIC entity.", nameof(entity));
    if (record.Entity.IsReadOnly)
      throw new InvalidOperationException($"'{record.Entity.Name}' is {record.Entity.ReadOnlyKindName}; its rows are read-only.");

    if (record.Status == EntityStatus.New) {
      ThrowIfSaving();
      _entityTracker.RemoveModified(record);
      return;
    }
    if (record.Status == EntityStatus.Deleted)
      return;

    ThrowIfSaving();
    record.Entity.Events.OnDeleting(record);
    record.Status = EntityStatus.Deleted;
    _entityTracker.AddModified(record);
  }

  /// <summary>Returns a value that indicates whether the entity can be deleted, and the entity types whose rows
  /// block it.</summary>
  public bool CanDeleteEntity<TEntity>(TEntity entity, out Type[] blockingEntities) where TEntity : class {
    blockingEntities = RunSync(() => GetDeleteEntityBlockingTypesAsync(entity));
    return blockingEntities.Length == 0;
  }

  /// <summary>Asynchronously gets the entity types whose rows block deleting the entity.</summary>
  public async Task<Type[]> GetDeleteEntityBlockingTypesAsync<TEntity>(TEntity entity) where TEntity : class {
    ArgumentNullException.ThrowIfNull(entity);
    var blocking = await GetDeleteBlockingEntitiesAsync(EntityHelper.GetRecord(entity));
    return [.. blocking.Where(e => e.InterfaceType != null).Select(e => e.InterfaceType!)];
  }

  /// <summary>Validates every pending record and throws a <see cref="ClientFaultException"/> carrying every fault
  /// found.</summary>
  /// <remarks>The save calls it; a caller that wants to know before saving may call it too.</remarks>
  public void ValidateChanges() {
    List<ClientFault>? faults = null;
    for (var i = 0; i < _entityTracker.Modified.Count; i++)
      if (!ValidateRecord(_entityTracker.Modified[i]))
        (faults ??= []).AddRange(_entityTracker.Modified[i].ValidationFaults!);
    if (faults != null)
      throw new ClientFaultException(faults);
  }

  /// <summary>Writes everything pending to the database.</summary>
  public void SaveChanges(SaveOptions options = SaveOptions.None) =>
    RunSync(() => SaveChangesAsync(options));

  /// <summary>Asynchronously writes everything pending: the lists' changes, then the records, announced, stamped,
  /// validated, ordered and run as one request with the scheduled statements, and accepts what was written.</summary>
  /// <remarks>On failure nothing is cleared: the records stay pending, and a caller that fixes the cause can save
  /// again. A transaction begun for the caller by a locked read is committed by the save.</remarks>
  public async Task SaveChangesAsync(SaveOptions options = SaveOptions.None) {
    using var turn = await EnterTurnAsync();
    ThrowIfSaving();
    if (!_entityTracker.HasChanges && _scheduled.Count == 0) {
      await CommitOnSaveAsync();
      return;
    }

    Context.App.Events.OnSavingChanges(this);
    IReadOnlyList<EntityRecord>? ordered = null;
    try {
      // Lists first: what they produce is records, and everything below is over records.
      foreach (var list in _entityTracker.ChangedLists.ToArray())
        list.PrepareForSave();
      if (_entityTracker.Modified.Count == 0 && _scheduled.Count == 0) {
        AcceptListChanges();
        await CommitOnSaveAsync();
        return;
      }

      NotifySavingChanges();
      StampAutoValues();
      // After the stamps, so what is checked is what will be written.
      ValidateChanges();

      // From here the pending set is what the request writes: a change made while it runs would be accepted unwritten.
      _isSaving = true;
      ordered = SaveOrderHelper.Sort(_entityTracker.Modified);
      var referenced = options.HasFlag(SaveOptions.InsertAndForget) ? FindReferencedNewIdentities(ordered) : null;

      // A unique value the save frees and takes again is moved out of the way first, before the scheduled statements.
      ScheduledMutation[] scheduled = [.. UniqueValueVacateHelper.BuildVacates(ordered, _entityTracker), .. _scheduled];
      await Database.RequestExecutor.ExecuteAsync(new DataRequest(this, ordered, options, scheduled, referenced));
    } catch {
      _isSaving = false;
      // `ordered` is null when the throw came before the sort; the records are the same either way.
      foreach (var record in ordered ?? _entityTracker.Modified)
        record.Entity.Events.OnSaveChangesAborted(record);
      Context.App.Events.OnSaveChangesAborted(this);
      throw;
    }
    AcceptSavedChanges(ordered, options);
    AcceptListChanges();
    _scheduled.Clear();
    Context.App.Events.OnSavedChanges(this);
  }

  // ----- the extensions disabled on this session --------------------------------------------------------------------

  /// <summary>Disables an extension for the rest of this session: its filters are left out of every read, and its
  /// write hooks skip the session's saves.</summary>
  /// <param name="extensionType">The extension's type; a derived extension counts as disabled with its base.</param>
  /// <remarks>The session's queries bypass the query cache from then on, and read nothing from the entity cache.
  /// </remarks>
  public void DisableExtension(Type extensionType) {
    ArgumentNullException.ThrowIfNull(extensionType);
    if (!typeof(AppExtension).IsAssignableFrom(extensionType))
      throw new ArgumentException($"'{extensionType.Name}' is not an extension.", nameof(extensionType));
    (_disabledExtensions ??= []).Add(extensionType);
  }

  /// <summary>Disables every extension for the rest of this session.</summary>
  /// <remarks>See <see cref="DisableExtension"/>.</remarks>
  public void DisableAllExtensions() => _allExtensionsDisabled = true;

  /// <summary>Determines whether an extension is disabled on this session.</summary>
  /// <param name="extensionType">The extension's type.</param>
  /// <returns><see langword="true"/> if the extension, a base of it, or every extension is disabled; otherwise,
  /// <see langword="false"/>.</returns>
  public bool IsExtensionDisabled(Type extensionType) {
    ArgumentNullException.ThrowIfNull(extensionType);
    return _allExtensionsDisabled
      || _disabledExtensions != null && _disabledExtensions.Any(disabled => disabled.IsAssignableFrom(extensionType));
  }

  /// <summary>Wraps a query's root so that it reads without the filters of the disabled extensions: one node for all,
  /// or one per disabled extension.</summary>
  internal QueryNode WrapWithoutDisabledFilters(QueryNode root) {
    if (_allExtensionsDisabled)
      return new IgnoreFiltersNode(root, extensionType: null);
    foreach (var disabled in _disabledExtensions ?? [])
      root = new IgnoreFiltersNode(root, disabled);
    return root;
  }

  // ----- public: entities, the identity map, validation --------------------------------------------------------------

  /// <summary>Creates a new entity of the type, in the save set from the moment it exists, with its client-generated
  /// key members set.</summary>
  /// <remarks>Not in the identity map until it has an identity; a database-generated key stays empty until the
  /// insert has run. The entity's <c>New</c> event is raised last.</remarks>
  public EntityBase NewEntity(EntityTypeInfo entity) {
    if (entity.IsReadOnly)
      throw new InvalidOperationException($"'{entity.Name}' is {entity.ReadOnlyKindName}; its rows are read-only and none can be created.");
    ThrowIfSaving();
    var record = new EntityRecord(entity, this) { Status = EntityStatus.New };
    record.MarkFullyLoaded();
    foreach (var member in entity.AutoOnCreateMembers)
      record.SetRawValue(member, GetAutoValue(member.AutoValue, OperationDateTime));
    _entityTracker.AddModified(record);
    entity.Events.OnNew(record);
    return record.EntityInstance;
  }

  /// <summary>Gets the entity with the identity: the tracked one where there is one, otherwise a stub that holds the
  /// identity and loads when a member is read.</summary>
  public EntityBase GetStub(EntityKey key) => FindTrackedEntity(key) ?? CreateStub(key);

  /// <summary>Finds the tracked entity with the identity; <see langword="null"/> where there is none.</summary>
  public EntityBase? FindTrackedEntity(EntityKey key) => _entityTracker.TryGetTracked(key)?.EntityInstance;

  /// <summary>Puts a record under the session's care and returns the record that counts: the one already tracked
  /// under that identity where there is one, otherwise the given one.</summary>
  /// <remarks>A tracked stub is filled from the given record and keeps its identity. A tracked record that is loaded
  /// or modified takes the members it does not hold. A record discarded in favour of the tracked one is not
  /// announced as loaded.</remarks>
  public EntityRecord Attach(EntityRecord record) => Attach(record, refreshTracked: false);

  /// <summary>Puts a record under the session's care and returns the record that counts, the tracked one also taking
  /// the given record's values over its own where asked.</summary>
  /// <param name="record">The record to track.</param>
  /// <param name="refreshTracked"><see langword="true"/> to have a tracked record that is loaded or modified take
  /// the given record's values in the members that were not assigned; <see langword="false"/> to have it take only
  /// the members it does not hold.</param>
  internal EntityRecord Attach(EntityRecord record, bool refreshTracked) {
    ArgumentNullException.ThrowIfNull(record);
    var key = record.PrimaryKey;
    if (key.IsEmpty)
      throw new InvalidOperationException(
        $"A '{record.Entity.Name}' with no primary key value cannot be tracked — it has no identity yet.");

    if (_entityTracker.TryGetTracked(key) is { } existing) {
      if (ReferenceEquals(existing, record))
        return record;
      if (record.Status == EntityStatus.Stub)
        return existing;
      if (existing.Status == EntityStatus.Stub) {
        existing.FillFrom(record);
        NotifyLoaded(existing, record.Status);
      } else if (existing.Status is EntityStatus.Loaded or EntityStatus.Modified)
        existing.MergeFrom(record, refreshTracked);
      return existing;
    }

    _entityTracker.Track(key, record);
    NotifyLoaded(record, record.Status);
    return record;
  }

  /// <summary>Validates one record, the application's <c>ValidatingChanges</c> handler first and then the model's
  /// rules, and leaves the faults on the record.</summary>
  /// <returns><see langword="true"/> if the record is valid; otherwise, <see langword="false"/>.</returns>
  /// <remarks>The model checks a value missing where it cannot be and a string longer than its size. A deleted
  /// record is not checked, nor a member the record does not hold, which stays as the row has it.</remarks>
  public bool ValidateRecord(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    record.ClearValidationFaults();
    if (record.Status == EntityStatus.Deleted)
      return true;

    record.Entity.Events.OnValidatingChanges(record);
    foreach (var member in record.Entity.Members)
      switch (member.Kind) {
        case MemberKind.Column:
          // A foreign key member is checked as its reference, so the fault names what the developer wrote; a
          // sequence member is filled by the save.
          if (member.OwnerReference == null && !member.IsDbGenerated && member.AutoValue != AutoType.Sequence)
            ValidateValue(record, member);
          break;
        case MemberKind.Reference:
          ValidateReference(record, member);
          break;
      }

    if (!record.IsValid)
      record.Entity.Events.OnValidationFailed(record);
    return record.IsValid;
  }

  /// <summary>Asynchronously gets the entities with rows pointing at the record through a reference that does not
  /// cascade, which block deleting it.</summary>
  /// <remarks>Asked of the database in one statement, whatever the number of references; what the session has
  /// pending is not looked at. A record whose row is not there is blocked by nothing.</remarks>
  public async Task<IReadOnlyList<EntityTypeInfo>> GetDeleteBlockingEntitiesAsync(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    var blocking = new List<EntityTypeInfo>();
    if (record.Status is EntityStatus.New or EntityStatus.Fantom)
      return blocking;
    var entity = record.Entity;
    var referrers = GetBlockingReferrers(entity);
    if (referrers.Count == 0)
      return blocking;
    var result = await Database.RequestExecutor.ExecuteAsync(new DataRequest(this,
      new QueryInfo(entity.ReferrersCacheKey, [record]),
      new QueryTreeBuilder(() => MemberQueryHelper.BuildReferrersQuery(entity, [.. referrers.Select(r => r.References)])),
      $"referrers of {entity.Name}"));
    // One slot comes back as a list of values, several as rows; an empty list is a row that is not there.
    var first = (result as System.Collections.IList) is { Count: > 0 } list ? list[0] : null;
    for (var i = 0; i < referrers.Count; i++) {
      var answer = first is ProjectionData row ? row.GetValue(MemberQueryHelper.GetReferrerSlotName(i)) : first;
      if (answer != null && Convert.ToBoolean(answer, CultureInfo.InvariantCulture))
        blocking.Add(referrers[i].Entity);
    }
    return blocking;
  }

  /// <summary>Gets the references to an entity that do not cascade, grouped by the entity they are members of, in
  /// the model's order.</summary>
  private static List<(EntityTypeInfo Entity, List<EntityMemberInfo> References)> GetBlockingReferrers(
      EntityTypeInfo entity) {
    var referrers = new List<(EntityTypeInfo Entity, List<EntityMemberInfo> References)>();
    foreach (var member in entity.IncomingReferences) {
      if (member.ReferenceInfo!.CascadeDelete)
        continue;
      var group = referrers.Find(r => r.Entity == member.Entity);
      if (group.References == null)
        referrers.Add((member.Entity!, [member]));
      else
        group.References.Add(member);
    }
    return referrers;
  }

  /// <summary>Gets the model's entity for an entity interface type.</summary>
  public EntityTypeInfo GetEntityTypeInfo(Type interfaceType) =>
    Model.FindEntity(interfaceType)
      ?? throw new KeyNotFoundException($"'{interfaceType.Name}' is not an entity of model '{Model.Name}'.");

  public override string ToString() =>
    $"{Model.Name} session, {_entityTracker.LoadedCount} loaded, {_entityTracker.Modified.Count} pending";

  /// <summary>Asynchronously rolls back any open transaction and releases the session's connection. The tracked
  /// entities are unaffected.</summary>
  public async ValueTask DisposeAsync() {
    using var turn = await EnterTurnAsync();
    await RollbackTransactionAsync();
    await DropTempTablesAsync();
    if (Connection is { } connection) {
      Connection = null;
      if (RunMode == RunMode.Sync)
        connection.Dispose();
      else
        await connection.DisposeAsync();
    }
    GC.SuppressFinalize(this);
  }

  /// <summary>Rolls back any open transaction and releases the session's connection. The tracked entities are
  /// unaffected.</summary>
  public void Dispose() => RunSync(() => DisposeAsync().AsTask());

  // ----- internal: what records and lists call ---------------------------------------------------------------------

  /// <summary>Creates a new link row for a many-to-many list, pointing at both ends, with an identity.</summary>
  /// <remarks>A link keyed by a Guid nothing generates is given one here; a link with any other key nobody makes is
  /// refused.</remarks>
  internal EntityRecord NewLinkRecord(ListInfo info, EntityRecord owner, EntityRecord target) {
    var link = info.LinkEntity
      ?? throw new ArgumentException("The list has no link entity.", nameof(info));
    var record = NewEntity(link).Record;
    record.SetValue(info.InverseMember!, owner.EntityInstance);
    record.SetValue(info.OtherMember!, target.EntityInstance);
    if (!record.PrimaryKey.IsEmpty)
      return record;

    var key = link.PrimaryKey;
    if (key is { MembersExpanded.Count: 1 } && key.MembersExpanded[0].Member is { DataType: var type } member
        && type == typeof(Guid) && !member.IsDbGenerated) {
      record.SetRawValue(member, _guids.NewGuid(Database.GuidScheme, OperationDateTime, _guidSequence++));
      return record;
    }
    throw new InvalidOperationException(
      $"A '{link.Name}' link for '{owner.Entity.Name}.{info.TargetEntity.Name}' has no identity: its primary key is "
      + "neither its two references nor a Guid, and nothing generates it. Give the link a Guid key or an [Auto] one.");
  }

  /// <summary>Finds the tracked records of the entity that pass the filter.</summary>
  internal List<EntityRecord> FindTrackedRecords(EntityTypeInfo entity, Func<EntityRecord, bool> filter) =>
    _entityTracker.FindTrackedRecords(entity, filter);

  /// <summary>Gets the tracker that holds the identity map, the save set and the changed lists.</summary>
  internal EntityTracker GetEntityTracker() => _entityTracker;

  /// <summary>Registers a record that the next save has to act on.</summary>
  internal void RegisterModified(EntityRecord record) => _entityTracker.AddModified(record);

  /// <summary>Keeps the value a unique-key column had in its row, on the column's first change.</summary>
  internal void KeepOriginal(EntityRecord record, EntityMemberInfo member, object? value) =>
    _entityTracker.KeepOriginal(record, member, value);

  /// <summary>Registers a list that the next save has to look at.</summary>
  internal void RegisterChangedList(IMemberEntityList list) => _entityTracker.AddChangedList(list);

  internal void SetLastCommands(IReadOnlyList<DataCommandSnapshot> commands) => LastCommands = commands;

  /// <summary>Loads a member a record does not hold: the member alone where it is on demand, the record's row
  /// otherwise. Called from a property getter, so it is synchronous.</summary>
  internal void LoadValue(EntityRecord record, EntityMemberInfo member) =>
    RunSync(() => member.IsOnDemand ? LoadMemberAsync(record, member) : LoadRecordAsync(record));

  /// <summary>Fills a list member from its rows. Called from the list's accessors, so it is synchronous.</summary>
  internal void LoadList(IMemberEntityList list) => RunSync(() => LoadListAsync(list));

  /// <summary>Runs the work under <see cref="RunMode.Sync"/> and returns when it has finished.</summary>
  /// <remarks>Under the mode every database call on the way is the driver's synchronous one, so the task is
  /// complete when the work returns it. One that is not is waited for and counted in
  /// <see cref="BlockedSyncCallCount"/>.</remarks>
  internal void RunSync(Func<Task> start) => StartSync(start).GetAwaiter().GetResult();

  /// <summary>Runs the work under <see cref="RunMode.Sync"/> and returns its result.</summary>
  /// <remarks>See <see cref="RunSync(Func{Task})"/>.</remarks>
  internal TResult RunSync<TResult>(Func<Task<TResult>> start) => StartSync(start).GetAwaiter().GetResult();

  /// <summary>Asynchronously waits for the session's turn where the session was opened with
  /// <see cref="EntitySessionOptions.AddThreadSafety"/>; otherwise, completes at once.</summary>
  /// <returns>An awaitable whose result is the turn, to dispose when the operation ends.</returns>
  internal SessionGate.TurnAwaitable EnterTurnAsync() => SessionGate.EnterAsync(_gate, Context.CancellationToken);

  /// <summary>Refuses a change to the session's entities, and a statement scheduled, while a save is writing them.
  /// </summary>
  /// <exception cref="InvalidOperationException">A save has taken the pending set and has not finished.</exception>
  internal void ThrowIfSaving() {
    if (_isSaving)
      throw new InvalidOperationException(
        "The session is saving its changes: until the save completes no entity of the session can be changed, "
        + "created or deleted, no statement scheduled and no second save started. Await the save first.");
  }

  /// <summary>Marks the session's connection as running a request's commands.</summary>
  /// <exception cref="InvalidOperationException">Another request's commands are running.</exception>
  internal void EnterCommandRun() {
    if (Interlocked.Exchange(ref _isRunningCommands, 1) != 0)
      throw new InvalidOperationException(
        "The session is already running an operation. A session runs one operation at a time: await the first "
        + $"before starting the next, or open the session with {nameof(EntitySessionOptions)}."
        + $"{nameof(EntitySessionOptions.AddThreadSafety)}, which has the next one wait.");
  }

  /// <summary>Marks the session's connection as free for the next request's commands.</summary>
  internal void ExitCommandRun() => Volatile.Write(ref _isRunningCommands, 0);


  // ----- private ----------------------------------------------------------------------------------------------------

  /// <summary>Starts the work under <see cref="RunMode.Sync"/> and returns its task, finished.</summary>
  /// <remarks>The mode is restored to the caller's, so that a synchronous member called from a handler inside an
  /// asynchronous one leaves that one as it was.</remarks>
  private TTask StartSync<TTask>(Func<TTask> start) where TTask : Task {
    // Before the mode changes: an operation that is running reads it.
    using var turn = _gate?.Enter(Context.CancellationToken) ?? default;
    var callerMode = RunMode;
    RunMode = RunMode.Sync;
    try {
      var task = SyncOverAsyncHelper.Start(start);
      if (!task.IsCompleted) {
        BlockedSyncCallCount++;
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
      }
      return task;
    } finally {
      RunMode = callerMode;
    }
  }

  /// <summary>Creates and tracks a stub for the identity, without asking the map first.</summary>
  private EntityBase CreateStub(EntityKey key) {
    var record = new EntityRecord(key.Entity, this) { Status = EntityStatus.Stub };
    var members = key.KeyInfo.MembersExpanded;
    for (var i = 0; i < members.Count; i++)
      record.SetLoadedValue(members[i].Member, members.Count == 1 ? key.SingleValue : key.ValueAt(i));
    _entityTracker.Track(key, record);
    return record.EntityInstance;
  }

  /// <summary>Raises <c>SavingChanges</c> on every pending record, over a snapshot, since a handler may add to the
  /// save set.</summary>
  private void NotifySavingChanges() {
    var subscribed = false;
    foreach (var record in _entityTracker.Modified)
      if (record.Entity.Events.HasSavingChanges) {
        subscribed = true;
        break;
      }
    if (!subscribed)
      return;
    foreach (var record in _entityTracker.Modified.ToArray())
      record.Entity.Events.OnSavingChanges(record);
  }

  /// <summary>Makes the records agree with what the database now holds and raises <c>SavedChanges</c> on each.
  /// </summary>
  /// <remarks>A deleted or forgotten record leaves the map as a fantom, a new one joins it, every other one is
  /// loaded and clean. The events are raised after the pending set is cleared, so a record a handler creates is
  /// pending for the next save.</remarks>
  private void AcceptSavedChanges(IReadOnlyList<EntityRecord> saved, SaveOptions options) {
    var accepted = saved.ToArray();
    var forget = options.HasFlag(SaveOptions.InsertAndForget);
    foreach (var record in accepted) {
      record.StatusBeforeSave = record.Status;
      if (forget || record.Status == EntityStatus.Deleted) {
        _entityTracker.Forget(record.PrimaryKey);
        _cachedQueryResults.Remove(record);
        record.Status = EntityStatus.Fantom;
      } else {
        // Only a former new record joins the map: it had no identity to be tracked by until its insert ran.
        var joining = record.Status == EntityStatus.New;
        // The row version the update wrote goes on the record as loaded, so the next save matches on it.
        if (record.Status == EntityStatus.Modified && record.Entity.RowVersionMember is { } version)
          record.SetLoadedValue(version, RecordCommandBuilder.GetNextVersion(record, version));
        record.Status = EntityStatus.Loaded;
        record.AcceptChanges();
        if (joining && !record.PrimaryKey.IsEmpty)
          _entityTracker.Track(record.PrimaryKey, record);
      }
    }
    _entityTracker.ClearModified();
    _isSaving = false;
    _operationDateTime = null;
    _guidSequence = 0;
    _nextTransactionId = null;
    foreach (var record in accepted)
      record.Entity.Events.OnSavedChanges(record);
  }

  /// <summary>Accepts the changed lists as what the database now holds.</summary>
  private void AcceptListChanges() {
    foreach (var list in _entityTracker.ChangedLists)
      list.AcceptChanges();
    _entityTracker.ClearChangedLists();
  }

  /// <summary>Writes the created and updated stamps on the records about to be written, at the session's one
  /// instant.</summary>
  private void StampAutoValues() {
    var now = OperationDateTime;
    foreach (var record in _entityTracker.Modified) {
      if (record.Status == EntityStatus.Deleted)
        continue;
      if (record.Status == EntityStatus.New)
        foreach (var member in record.Entity.AutoOnInsertMembers)
          record.SetRawValue(member, GetAutoValue(member.AutoValue, now));
      foreach (var member in record.Entity.AutoOnSaveMembers)
        record.SetRawValue(member, GetAutoValue(member.AutoValue, now));
    }
  }

  /// <summary>Gets the value an <c>[Auto]</c> member takes at the instant.</summary>
  private object? GetAutoValue(AutoType type, DateTime now) => type switch {
    AutoType.NewGuid => _guids.NewGuid(Database.GuidScheme, now, _guidSequence++),
    AutoType.CreatedOn or AutoType.UpdatedOn => now,
    AutoType.CreatedBy or AutoType.UpdatedBy => Context.User.UserName,
    AutoType.CreatedById or AutoType.UpdatedById => Context.User.UserId,
    _ => null,
  };

  /// <summary>Finds the new records of the save whose server-made key another new record of it points at; a
  /// forgotten save still reads those keys back.</summary>
  private static HashSet<EntityRecord> FindReferencedNewIdentities(IReadOnlyList<EntityRecord> records) {
    var referenced = new HashSet<EntityRecord>(ReferenceEqualityComparer.Instance);
    foreach (var record in records) {
      if (record.Status != EntityStatus.New)
        continue;
      foreach (var member in record.Entity.Members) {
        if (member.ReferenceInfo is not { } reference || reference.ToKey.Entity.IdentityMember == null)
          continue;
        if (record.GetRawValue(member) is EntityBase { Record: { Status: EntityStatus.New } target }
            && ReferenceEquals(target.Session, record.Session))
          referenced.Add(target);
      }
    }
    return referenced;
  }

  /// <summary>Validates a column member: present where it must be, within its size where it is a string, and of
  /// the type its discriminator names where it is a JSON document.</summary>
  /// <remarks>An empty string counts as missing for a required text member. A string over its size is trimmed where
  /// the member says <c>AutoTrim</c>, and a fault otherwise.</remarks>
  private static void ValidateValue(EntityRecord record, EntityMemberInfo member) {
    if (!record.Loaded.IsSet(member.DataIndex))
      return;
    var value = record.GetRawValue(member);
    if (!member.IsNullable) {
      var missing = member.DataType == typeof(string) ? string.IsNullOrEmpty(value as string) : value == null;
      if (missing) {
        record.AddValidationFault(new ClientFault(record, ClientFaultCodes.ValueMissing, member.Name,
          $"'{member.Name}' is required."));
        return;
      }
    }
    if (member.IsJson && JsonMemberHelper.GetTypeFault(record, member) is { } typeFault) {
      record.AddValidationFault(new ClientFault(record, ClientFaultCodes.JsonTypeMismatch, member.Name, typeFault));
      return;
    }
    if (member.Size <= 0 || member.MemberFlags.HasFlag(MemberFlags.Unlimited) || value is not string text
        || text.Length <= member.Size)
      return;
    if (member.MemberFlags.HasFlag(MemberFlags.AutoTrim))
      record.SetValue(member, text[..member.Size]);
    else
      record.AddValidationFault(new ClientFault(record, ClientFaultCodes.ValueTooLong, member.Name,
        $"'{member.Name}' is {text.Length} characters; the limit is {member.Size}."));
  }

  /// <summary>Validates a reference member: present where the row must point at something, by the reference member
  /// or its foreign key members.</summary>
  private static void ValidateReference(EntityRecord record, EntityMemberInfo member) {
    if (member.IsNullable || member.ReferenceInfo is not { } reference)
      return;
    if (record.GetRawValue(member) != null)
      return;
    foreach (var keyMember in reference.FromKey.MembersExpanded)
      if (record.GetRawValue(keyMember.Member) != null || !record.Loaded.IsSet(keyMember.Member.DataIndex))
        return;
    record.AddValidationFault(new ClientFault(record, ClientFaultCodes.ValueMissing, member.Name,
      $"'{member.Name}' is required."));
  }

  /// <summary>Raises <c>Loaded</c> for a record filled from the database, where real data arrived.</summary>
  private static void NotifyLoaded(EntityRecord record, EntityStatus arrivedAs) {
    if (arrivedAs == EntityStatus.Loaded)
      record.Entity.Events.OnLoaded(record);
  }
}
