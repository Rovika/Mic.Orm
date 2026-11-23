using System.Collections;
using Mic.Common;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

// The loads: a record from its row, a list member from its rows, and the includes of a query's rows.
public partial class EntitySession {
  /// <summary>The number of times one entity's includes run within one query's includes, where a cycle ends. A
  /// level's rows get the includes for their own entity too.</summary>
  const int MaxIncludeRunsPerEntity = 2;

  /// <summary>Asynchronously fills a stub from its row, and every other tracked stub of the entity with it where the
  /// smart load is on.</summary>
  /// <remarks>A row that is not there is not an error: the members are left null and marked loaded, so the question
  /// is asked once.</remarks>
  internal async Task LoadRecordAsync(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    if (record.Status is EntityStatus.New or EntityStatus.Deleted)
      return;
    var key = record.PrimaryKey;
    if (key.IsEmpty)
      return;
    if (await TryLoadAllStubsAsync(record))
      return;
    // The reader attaches what it materialises, and this record is tracked under that key, so it is filled in place.
    if (await LoadRecordAsync(key) == null)
      record.MarkFullyLoaded();
  }

  /// <summary>Asynchronously loads an on-demand member of a record from its row, and of every other tracked record
  /// of the entity that lacks it where the smart load is on, in one statement by their keys.</summary>
  /// <param name="record">The record the member was asked of.</param>
  /// <param name="member">The on-demand member to load.</param>
  /// <remarks>A record that holds the member, or is new or deleted, is not loaded.</remarks>
  internal Task LoadMemberAsync(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (!IsSmartLoading)
      return LoadMemberAsync([record], member);
    var others = FindTrackedRecords(record.Entity, r =>
      !ReferenceEquals(r, record) && r.Status is EntityStatus.Loaded or EntityStatus.Modified
      && !r.Loaded.IsSet(member.DataIndex));
    return LoadMemberAsync(TakeOwnersBatch(others, record), member);
  }

  /// <summary>Asynchronously loads an on-demand member of the given records from their rows, a statement for each
  /// <see cref="MaxSmartLoadBatchSize"/> of them.</summary>
  /// <param name="records">The records, of one entity.</param>
  /// <param name="member">The on-demand member to load.</param>
  /// <remarks>A record that holds the member is left as it is, and one that is new or deleted is not loaded. A
  /// record with no row has the member marked loaded, so the question is asked once.</remarks>
  internal async Task LoadMemberAsync(IReadOnlyList<EntityRecord> records, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(records);
    ArgumentNullException.ThrowIfNull(member);
    var entity = member.Entity!;
    if (entity.PrimaryKey is not { } key)
      return;
    var lacking = new Dictionary<EntityKey, EntityRecord>();
    foreach (var record in records)
      if (record.Status is EntityStatus.Loaded or EntityStatus.Modified or EntityStatus.Stub
          && !record.Loaded.IsSet(member.DataIndex) && !record.PrimaryKey.IsEmpty)
        lacking[record.PrimaryKey] = record;
    // A document read as the type another member names needs that member in the row beside it.
    EntityMemberInfo[] read = member.JsonDiscriminatorMember is { } discriminator ? [member, discriminator] : [member];
    var keyMembers = key.MembersExpanded;

    foreach (var batch in lacking.Values.Chunk(MaxSmartLoadBatchSize)) {
      // The records are the argument; the engine reads each one's key when the statement is bound.
      var rows = await Database.RequestExecutor.ExecuteAsync(new DataRequest(this,
        new QueryInfo(member.LoadCacheKey, [batch]),
        new QueryTreeBuilder(() => MemberQueryHelper.BuildMembersByKeysQuery(entity, read)),
        $"load {entity.Name}.{member.Name}")) as IReadOnlyList<ProjectionData> ?? [];
      foreach (var row in rows) {
        var values = new object?[keyMembers.Count];
        for (var i = 0; i < values.Length; i++)
          values[i] = row.GetValue(keyMembers[i].Member.Name);
        if (!lacking.TryGetValue(new EntityKey(key, values), out var found) || found.Loaded.IsSet(member.DataIndex))
          continue;
        if (member.IsJson)
          JsonMemberHelper.SetLoadedDocument(found, member, row.GetValue(member.Name));
        else
          found.SetLoadedValue(member, row.GetValue(member.Name));
      }
    }
    foreach (var rowless in lacking.Values)
      if (!rowless.Loaded.IsSet(member.DataIndex))
        rowless.SetLoadedValue(member, null);
  }

  /// <summary>Asynchronously reads one row by a key value and returns its tracked record; <see langword="null"/>
  /// where no such row exists.</summary>
  /// <param name="key">The key value.</param>
  /// <param name="skipEntityCache"><see langword="true"/> to read the server whatever the entity cache holds;
  /// otherwise, <see langword="false"/>.</param>
  private async Task<EntityRecord?> LoadRecordAsync(EntityKey key, bool skipEntityCache = false) =>
    await Database.RequestExecutor.ExecuteAsync(new DataRequest(this, key, skipEntityCache: skipEntityCache))
      as EntityRecord;

  /// <summary>Asynchronously fills a list member from its rows, and every other tracked owner's with it where the
  /// smart load is on.</summary>
  /// <remarks>An owner with no key value on its end of the reference is set empty without asking. Rows marked for
  /// deletion are left out.</remarks>
  internal async Task LoadListAsync(IMemberEntityList list) {
    ArgumentNullException.ThrowIfNull(list);
    var member = list.Member;
    var info = member.ListInfo
      ?? throw new ArgumentException($"'{member.Entity?.Name}.{member.Name}' is not a list member.", nameof(list));

    if (info.InverseMember?.ReferenceInfo is not { } reference || !EntityListHelper.HasKey(list.Owner, reference)) {
      list.SetEmpty();
      return;
    }
    // The owner's record is the argument; the engine reads the key off it.
    object?[] arguments = [list.Owner];

    if (await TryLoadAllOwnersListsAsync(list))
      return;

    if (info.Kind == ListKind.OneToMany) {
      var records = await RunListLoadAsync(member, manyOwners: false, arguments) as IReadOnlyList<EntityRecord> ?? [];
      list.SetItems(records.Where(r => r.Status != EntityStatus.Deleted));
      return;
    }
    var rows = await RunListLoadAsync(member, manyOwners: false, arguments) as IReadOnlyList<ProjectionData> ?? [];
    list.SetLinkRecordPairs(EntityListHelper.GetLinkRecordPairs(rows));
  }

  /// <summary>Asynchronously loads what the query's includes and the session's name on the rows.</summary>
  private async Task RunIncludesAsync(IReadOnlyList<EntityRecord> records, IReadOnlyList<QueryInclude> includes) {
    ArgumentNullException.ThrowIfNull(records);
    ArgumentNullException.ThrowIfNull(includes);
    var sessionIncludes = Includes;
    var contextIncludes = Context.GetQueryIncludes();
    if (records.Count == 0 || includes.Count == 0 && sessionIncludes.Count == 0 && contextIncludes.Count == 0)
      return;
    IReadOnlyList<QueryInclude> all = sessionIncludes.Count == 0 && contextIncludes.Count == 0
      ? includes
      : [.. includes, .. sessionIncludes, .. contextIncludes];
    // A query that selects an optional reference answers null where a row has none; nothing is included on it.
    if (records.Any(r => r is null))
      records = [.. records.Where(r => r is not null)];
    await RunIncludesAsync(records, all, new Dictionary<EntityTypeInfo, int>(), depth: 0);
  }

  // A path applies to the query's own rows alone; a selector applies wherever its entity's rows are loaded.
  private async Task RunIncludesAsync(IReadOnlyList<EntityRecord> records, IReadOnlyList<QueryInclude> includes,
      Dictionary<EntityTypeInfo, int> runs, int depth) {
    if (records.Count == 0)
      return;
    var entity = records[0].Entity;
    var matching = includes.Where(i => i.Path != null ? depth == 0 : i.EntityType == entity.InterfaceType).ToList();
    if (matching.Count == 0)
      return;
    var run = runs.GetValueOrDefault(entity) + 1;
    runs[entity] = run;
    if (run > MaxIncludeRunsPerEntity)
      return;
    foreach (var include in matching)
      foreach (var chain in IncludeHelper.GetChains(entity, include))
        await LoadChainAsync(records, chain, 0, includes, runs, depth);
  }

  // One hop of a chain over the rows; the hop's rows get their own entity's includes, then the next hop.
  private async Task LoadChainAsync(IReadOnlyList<EntityRecord> records, IReadOnlyList<EntityMemberInfo> chain, int at,
      IReadOnlyList<QueryInclude> includes, Dictionary<EntityTypeInfo, int> runs, int depth) {
    var loaded = await LoadIncludedMemberAsync(records, chain[at]);
    await RunIncludesAsync(loaded, includes, runs, depth + 1);
    if (at + 1 < chain.Count)
      await LoadChainAsync(loaded, chain, at + 1, includes, runs, depth);
  }

  /// <summary>Asynchronously loads one member of the rows, a reference's targets, a list's rows or an on-demand
  /// member's values, and returns the rows it holds for the next hop; none for an on-demand member.</summary>
  private async Task<IReadOnlyList<EntityRecord>> LoadIncludedMemberAsync(IReadOnlyList<EntityRecord> records, EntityMemberInfo member) {
    var loaded = new List<EntityRecord>();
    if (member.IsOnDemand) {
      await LoadMemberAsync(records, member);
      return loaded;
    }
    if (member.Kind == MemberKind.Reference) {
      var seen = new HashSet<EntityRecord>(ReferenceEqualityComparer.Instance);
      foreach (var record in records)
        if (record.Status is not (EntityStatus.New or EntityStatus.Deleted)
            && record.GetValue(member) is EntityBase target && seen.Add(target.Record))
          loaded.Add(target.Record);
      foreach (var target in loaded)
        if (target.Status == EntityStatus.Stub)
          await LoadRecordAsync(target);
      return loaded;
    }
    foreach (var record in records) {
      if (record.Status is EntityStatus.New or EntityStatus.Deleted || record.GetValue(member) is not IMemberEntityList list)
        continue;
      if (!list.IsLoaded)
        await LoadListAsync(list);
      foreach (var item in (IEnumerable)list)
        if (item is EntityBase entity)
          loaded.Add(entity.Record);
    }
    return loaded;
  }
}
