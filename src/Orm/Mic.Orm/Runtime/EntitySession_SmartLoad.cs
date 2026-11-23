using System.Runtime.CompilerServices;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

// The smart load: the same member of every owner the session holds fetched in one statement when one is asked for —
// stubs by their keys, list members for all their owners, a queryable property's chain for all owners at once.
public partial class EntitySession {
  /// <summary>The most owners one statement takes; the rest load one at a time as they are touched.</summary>
  const int MaxSmartLoadBatchSize = 500;

  /// <summary>The most results cached per owner, oldest out.</summary>
  const int MaxCachedResultsPerOwner = 8;

  /// <summary>The results many-owners queries produced, per owner, kept for as long as the owner lives.</summary>
  /// <remarks>This is the hand-over for a queryable property: a list member has its list object to put an owner's
  /// rows in, but a chain written over the member, a count, a projection, a filtered set, belongs to no member, so
  /// the batch's answer for each owner is kept here until that owner asks, and asking twice costs one statement.
  /// </remarks>
  readonly ConditionalWeakTable<EntityRecord, SmallLookup<CachedResultKey, object?>> _cachedQueryResults = new();

  /// <summary>Asynchronously fills every tracked stub of the record's entity, this one included, in one query by
  /// their keys.</summary>
  /// <returns><see langword="true"/> if the stubs were loaded; <see langword="false"/> where there was nothing to
  /// gain.</returns>
  private async Task<bool> TryLoadAllStubsAsync(EntityRecord record) {
    var entity = record.Entity;
    if (!IsSmartLoading || entity.PrimaryKey is not { } key || EntityCache.IsCached(entity))
      return false;
    var others = FindTrackedRecords(entity, r => r.Status == EntityStatus.Stub && !ReferenceEquals(r, record));
    if (others.Count == 0)
      return false;
    var stubs = TakeOwnersBatch(others, record);

    // The stubs are the argument; the engine reads each one's key when the statement is bound.
    await Database.RequestExecutor.ExecuteAsync(new DataRequest(this,
      new QueryInfo(key.SelectByKeysCacheKey, [stubs.ToArray()]),
      new QueryTreeBuilder(() => MemberQueryHelper.BuildByKeysQuery(entity)),
      $"load {entity.Name} stubs"));

    // A stub with no row is marked loaded, so the question is asked once.
    foreach (var stub in stubs)
      if (stub.Status == EntityStatus.Stub)
        stub.MarkFullyLoaded();
    return true;
  }

  /// <summary>Asynchronously loads the same list member of every tracked owner that has it unloaded, in one query,
  /// and distributes the rows to their owners.</summary>
  /// <returns><see langword="true"/> if the lists were loaded; <see langword="false"/> where there is one owner or
  /// the rows are of a cached entity.</returns>
  private async Task<bool> TryLoadAllOwnersListsAsync(IMemberEntityList list) {
    var member = list.Member;
    var info = member.ListInfo!;
    if (!IsSmartLoading || EntityCache.IsCached(info.TargetEntity) || info.LinkEntity is { } link && EntityCache.IsCached(link))
      return false;
    var others = FindTrackedRecords(member.Entity!, r =>
      !ReferenceEquals(r, list.Owner)
      && r.Status is not (EntityStatus.New or EntityStatus.Fantom)
      && r.GetRawValue(member) is not IMemberEntityList { IsLoaded: true }
      && EntityListHelper.GetOwnerKey(r, member) != null);
    if (others.Count == 0)
      return false;
    var owners = TakeOwnersBatch(others, list.Owner);
    object?[] keys = [owners.ToArray()];

    var rowsByOwnerKey = new Dictionary<EntityKey, List<object>>();
    if (info.Kind == ListKind.OneToMany) {
      var records = await RunListLoadAsync(member, manyOwners: true, keys) as IReadOnlyList<EntityRecord> ?? [];
      foreach (var record in records)
        if (record.Status != EntityStatus.Deleted && EntityListHelper.GetOwnerKeyOfRow(record, member) is { } ownerKey)
          AddOwnerRow(ownerKey, record);
    } else {
      var rows = await RunListLoadAsync(member, manyOwners: true, keys) as IReadOnlyList<ProjectionData> ?? [];
      foreach (var pair in EntityListHelper.GetLinkRecordPairs(rows))
        if (EntityListHelper.GetOwnerKeyOfRow(pair.Link, member) is { } ownerKey)
          AddOwnerRow(ownerKey, pair);
    }

    foreach (var owner in owners) {
      // Through the accessor, which makes the list object where the member has never been read.
      var ownerList = (IMemberEntityList)owner.GetValue(member)!;
      if (!rowsByOwnerKey.TryGetValue(EntityListHelper.GetOwnerKey(owner, member)!.Value, out var ownerRows))
        ownerList.SetEmpty();
      else if (info.Kind == ListKind.OneToMany)
        ownerList.SetItems(ownerRows.Cast<EntityRecord>());
      else
        ownerList.SetLinkRecordPairs(ownerRows.Cast<LinkRecordPair>());
    }
    return true;

    // ----- local functions
    void AddOwnerRow(EntityKey ownerKey, object row) {
      if (!rowsByOwnerKey.TryGetValue(ownerKey, out var ownerRows))
        rowsByOwnerKey[ownerKey] = ownerRows = [];
      ownerRows.Add(row);
    }
  }

  /// <summary>Asynchronously runs the load of a list member for one owner or for many, under the member's fixed
  /// cache key, and returns the target's records or the link-and-target rows by the member's kind.</summary>
  private Task<object?> RunListLoadAsync(EntityMemberInfo member, bool manyOwners, IReadOnlyList<object?> arguments) =>
    Database.RequestExecutor.ExecuteAsync(new DataRequest(this,
        new QueryInfo(manyOwners ? member.LoadManyOwnersCacheKey : member.LoadCacheKey, arguments),
        new QueryTreeBuilder(() => MemberQueryHelper.BuildListLoadQuery(member, manyOwners)),
        $"load {member.Entity!.Name}.{member.Name}"));

  /// <summary>Asynchronously answers a query over a queryable property for all tracked owners at once: from the
  /// result cached for this owner, or by running the chain once for every owner without one and caching each
  /// owner's result.</summary>
  /// <returns>Whether the query was answered, and the answer; a <see langword="null"/> answer is valid for an
  /// aggregate over no rows. Not answered where the rows are of a cached entity, the chain cannot be written for
  /// many owners, or no other owner is waiting.</returns>
  private async Task<(bool Answered, object? Result)> TryRunForAllOwnersAsync(IMemberRowSet rowSet, DataRequest request) {
    var member = rowSet.Member;
    var info = member.ListInfo!;
    if (EntityCache.IsCached(info.TargetEntity) || info.LinkEntity is { } link && EntityCache.IsCached(link)
        || EntityListHelper.GetOwnerKey(rowSet.Owner, member) is not { } ownerKey)
      return default;
    if (FindBatchableEntry(request) is not { } cacheEntry)
      return default;
    var template = cacheEntry.SqlTemplate!;
    var resultKey = BuildCachedResultKey(rowSet, request, template);
    if (TryGetCachedResult(rowSet.Owner, resultKey, out var cachedResult))
      return (true, GetQueryResult(cachedResult));

    var owners = CollectOwnersWithoutResult(rowSet, resultKey);
    if (owners.Length == 0)
      return default;
    if (GetManyOwnersEntry(cacheEntry, request, rowSet, owners) is not { } manyOwners)
      return default;
    var batchResult = await RunManyOwnersQueryAsync(request, rowSet, owners, manyOwners);
    var resultsByOwnerKey = DistributeToOwners(batchResult, owners, member, template);
    // The shape's includes run over every owner's rows before the results are kept, so a kept answer has them.
    await RunIncludesAsync(CollectRecords(resultsByOwnerKey.Values), cacheEntry.Includes);
    CacheOwnerResults(owners, member, resultKey, resultsByOwnerKey);
    return (true, GetQueryResult(resultsByOwnerKey[ownerKey]));
  }

  /// <summary>Collects the records of the results that are records: the entity-returning forms.</summary>
  private static List<EntityRecord> CollectRecords(IEnumerable<object?> results) {
    var records = new List<EntityRecord>();
    foreach (var result in results)
      if (result is IReadOnlyList<EntityRecord> list)
        records.AddRange(list);
    return records;
  }

  /// <summary>Finds the query cache's entry for the single-owner shape where its statement can run for many owners;
  /// <see langword="null"/> where the shape runs in memory, is a mutation, or is known not to batch.</summary>
  private QueryCacheEntry? FindBatchableEntry(DataRequest request) {
    // The many-owners form hangs on the shape's cache entry, which a session with an extension disabled does not use.
    if (HasDisabledExtensions)
      return null;
    var cacheEntry = Database.RequestExecutor.TranslateIfNew(request);
    if (cacheEntry.SqlTemplate == null || cacheEntry.ManyOwnersEntry is { SqlTemplate: null })
      return null;
    // A mutation run for every tracked owner would delete their rows too.
    return cacheEntry.SqlTemplate.ResultKind == QueryResultKind.Affected ? null : cacheEntry;
  }

  /// <summary>Builds the key a result is cached under: the member, the query's shape, and the values its parameters
  /// took, all but the owner's.</summary>
  private static CachedResultKey BuildCachedResultKey(IMemberRowSet rowSet, DataRequest request, SqlTemplate template) {
    var query = request.Query!;
    // The root's own parameter reads the owner off the set.
    var ownerAt = query.IndexOf(rowSet);
    // A placeholder read off the run, an extension's value or a sub-query over a cached entity, is fixed to the key's
    // eye too: the first is the session's, the second is shape, and reading it here would want the cached rows loaded.
    var parameterValues = new List<object?>();
    foreach (var placeholder in template.Placeholders)
      if (!placeholder.IsFixed && placeholder.ArgumentIndex != ownerAt)
        parameterValues.Add(placeholder.GetValue(request));
    return new CachedResultKey(rowSet.Member, query.CacheKey, parameterValues.ToArray());
  }

  private bool TryGetCachedResult(EntityRecord owner, CachedResultKey resultKey, out object? result) {
    result = null;
    return _cachedQueryResults.TryGetValue(owner, out var ownerResults) && ownerResults.TryGetValue(resultKey, out result);
  }

  /// <summary>Collects the tracked owners with no result cached under the key, this one last; none where this owner
  /// is new, deleted or forgotten, or is the only one waiting.</summary>
  private EntityRecord[] CollectOwnersWithoutResult(IMemberRowSet rowSet, CachedResultKey resultKey) {
    var member = rowSet.Member;
    if (rowSet.Owner.Status is EntityStatus.New or EntityStatus.Fantom or EntityStatus.Deleted)
      return [];
    var others = FindTrackedRecords(member.Entity!, r =>
      !ReferenceEquals(r, rowSet.Owner)
      && r.Status is not (EntityStatus.New or EntityStatus.Fantom or EntityStatus.Deleted)
      && EntityListHelper.GetOwnerKey(r, member) != null && !TryGetCachedResult(r, resultKey, out _));
    return others.Count == 0 ? [] : TakeOwnersBatch(others, rowSet.Owner).ToArray();
  }

  /// <summary>Takes one statement's worth of owners: the others, capped, and the one being asked for last.</summary>
  private static List<EntityRecord> TakeOwnersBatch(List<EntityRecord> others, EntityRecord last) {
    if (others.Count > MaxSmartLoadBatchSize - 1)
      others.RemoveRange(MaxSmartLoadBatchSize - 1, others.Count - (MaxSmartLoadBatchSize - 1));
    others.Add(last);
    return others;
  }

  /// <summary>Gets the entry of the chain's many-owners form, translated once and held on the single-owner shape's
  /// entry; or <see langword="null"/> where the form cannot be written for the chain.</summary>
  private QueryCacheEntry? GetManyOwnersEntry(QueryCacheEntry cacheEntry, DataRequest request, IMemberRowSet rowSet,
      EntityRecord[] owners) {
    if (cacheEntry.ManyOwnersEntry == null) {
      var root = BuildManyOwnersRoot(request, rowSet, owners);
      cacheEntry.ManyOwnersEntry = root == null
        ? new QueryCacheEntry(null, null)
        : Database.RequestExecutor.Translate(request.Query!.WithArgument(owners), root);
    }
    return cacheEntry.ManyOwnersEntry.SqlTemplate == null ? null : cacheEntry.ManyOwnersEntry;
  }

  /// <summary>Builds the single-owner query's tree afresh, strips its includes and re-roots it over all the owners.
  /// </summary>
  private static QueryNode? BuildManyOwnersRoot(DataRequest request, IMemberRowSet rowSet, EntityRecord[] owners) {
    var root = TranslationHelper.StripIncludes(request.RootBuilder!.Build(), out _);
    return QueryablePropertyHelper.BuildManyOwnersRoot(root, rowSet, owners);
  }

  /// <summary>Asynchronously runs the chain for all the owners at once, under the many-owners form's entry, the
  /// owners one more argument of the single-owner query.</summary>
  private Task<object?> RunManyOwnersQueryAsync(DataRequest request, IMemberRowSet rowSet, EntityRecord[] owners,
      QueryCacheEntry manyOwners) {
    var member = rowSet.Member;
    var description = $"{member.Entity!.Name}.{member.Name} for {owners.Length} owners";
    return Database.RequestExecutor.ExecuteAsync(
      new DataRequest(this, request.Query!.WithArgument(owners), manyOwners, description) { Cancellation = request.Cancellation });
  }

  /// <summary>Deals the batch's rows out to their owners, a result per owner of the single-owner statement's kind
  /// and an empty one for an owner the batch had nothing for.</summary>
  private Dictionary<EntityKey, object?> DistributeToOwners(object? batchResult, IReadOnlyList<EntityRecord> owners,
      EntityMemberInfo member, SqlTemplate template) {
    var resultsByOwnerKey = new Dictionary<EntityKey, object?>();
    foreach (var owner in owners)
      resultsByOwnerKey[EntityListHelper.GetOwnerKey(owner, member)!.Value] = CreateEmptyResult(template.ResultKind);
    // A one-to-many's rows name their owner by their foreign key; the other forms carry the owner's key beside each row.
    if (batchResult is IReadOnlyList<EntityRecord> records)
      foreach (var record in records)
        AddOwnerResultRow(resultsByOwnerKey, EntityListHelper.GetOwnerKeyOfRow(record, member), record, template);
    else
      foreach (var row in batchResult as IReadOnlyList<ProjectionData> ?? [])
        AddOwnerResultRow(resultsByOwnerKey, QueryablePropertyHelper.ReadOwnerKey(row, member), row, template);
    return resultsByOwnerKey;
  }

  private static object? CreateEmptyResult(QueryResultKind kind) => kind switch {
    QueryResultKind.Scalar => null,
    QueryResultKind.Rows => new List<ProjectionData>(),
    QueryResultKind.Values => new List<object?>(),
    _ => new List<EntityRecord>(),
  };

  /// <summary>Puts one row of the batch into its owner's result, as the single-owner statement would have answered
  /// it. A row whose owner is not in the batch is dropped.</summary>
  private void AddOwnerResultRow(Dictionary<EntityKey, object?> resultsByOwnerKey, EntityKey? rowOwner, object row,
      SqlTemplate template) {
    if (rowOwner is not { } ownerKey || !resultsByOwnerKey.TryGetValue(ownerKey, out var ownerResult))
      return;
    switch (ownerResult) {
      case List<ProjectionData> rows:
        rows.Add(CopyIntoCallerShape((ProjectionData)row, (TransientEntityInfo)template.ResultEntity!));
        break;
      case List<object?> values:
        values.Add(((ProjectionData)row).GetValue(QueryablePropertyHelper.RowSlot));
        break;
      case List<EntityRecord> group:
        var slot = row is ProjectionData data ? data.GetValue(QueryablePropertyHelper.RowSlot) : row;
        var record = slot as EntityRecord ?? EntityHelper.GetRecord(slot!);
        if (record.Status != EntityStatus.Deleted)
          group.Add(record);
        break;
      default:
        // The caller's type, which the single-owner statement answers as.
        resultsByOwnerKey[ownerKey] = Database.Provider.ConvertReadValue(
          ((ProjectionData)row).GetValue(QueryablePropertyHelper.RowSlot), template.ScalarType!);
        break;
    }
  }

  /// <summary>Copies a batch row into a row of the caller's own shape, without the owner's key beside it.</summary>
  private static ProjectionData CopyIntoCallerShape(ProjectionData row, TransientEntityInfo shape) {
    var copy = new ProjectionData(shape);
    foreach (var shapeMember in shape.Members)
      copy.SetValue(shapeMember, row.GetValue(shapeMember.Name));
    return copy;
  }

  /// <summary>Caches each owner's result for as long as the owner lives, the oldest out past the cap.</summary>
  /// <remarks>The cache is the only place a queryable property's answer can live; see
  /// <see cref="_cachedQueryResults"/>.</remarks>
  private void CacheOwnerResults(IReadOnlyList<EntityRecord> owners, EntityMemberInfo member, CachedResultKey resultKey,
      Dictionary<EntityKey, object?> resultsByOwnerKey) {
    foreach (var owner in owners) {
      var ownerResults = _cachedQueryResults.GetOrCreateValue(owner);
      if (ownerResults.Count == MaxCachedResultsPerOwner)
        ownerResults.Remove(ownerResults.Keys.First());
      ownerResults.Add(resultKey, resultsByOwnerKey[EntityListHelper.GetOwnerKey(owner, member)!.Value]);
    }
  }

  /// <summary>Returns a cached result as the query's answer: records as their entities, projected rows with their
  /// records replaced by entities, a value as it is.</summary>
  private static object? GetQueryResult(object? cachedResult) {
    switch (cachedResult) {
      case List<EntityRecord> records:
        return records.Select(r => (object?)r.EntityInstance).ToList();
      case List<ProjectionData> rows:
        ReplaceRecordsWithEntities(rows);
        return rows;
      default:
        return cachedResult;
    }
  }

  /// <summary>Gets a value that indicates whether the smart load is on for the session.</summary>
  private bool IsSmartLoading => !Options.HasFlag(EntitySessionOptions.DisableSmartLoad);
}
