using System.Linq.Expressions;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Orm.Runtime;

// The session's queries, typed and by name, and what it carries for them: the scheduled statements and the includes.
public partial class EntitySession : IQueryExecutor {
  /// <summary>Gets the provider a typed query over this session composes through.</summary>
  internal EntityQueryProvider QueryProvider => _queryProvider ??= new EntityQueryProvider(this);

  /// <summary>Gets the set-based mutations held for the next save, in the order they were scheduled.</summary>
  public IReadOnlyList<ScheduledMutation> ScheduledMutations => _scheduled;

  /// <summary>Gets the includes this session carries for every query it runs.</summary>
  internal IReadOnlyList<QueryInclude> Includes => _includes;

  EntityQueryProvider? _queryProvider;
  readonly List<ScheduledMutation> _scheduled = [];
  readonly List<QueryInclude> _includes = [];

  /// <summary>Adds an include for every query this session runs that returns the entity, and for every row of the
  /// entity an include of such a query loads.</summary>
  public void AddInclude<TEntity>(Expression<Func<TEntity, object?>> selector) {
    ArgumentNullException.ThrowIfNull(selector);
    _includes.Add(new QueryInclude(selector));
  }

  /// <summary>Holds a typed mutation for the next save, with its captured locals as they are now; the executor makes
  /// a request of it when the save runs.</summary>
  internal void Schedule(Expression mutation, CommandSchedule schedule) {
    // A save has copied the list into its request and clears it when done: a statement added now would never run.
    ThrowIfSaving();
    _scheduled.Add(new ScheduledMutation(CapturedLocalSnapshotter.Snapshot(mutation), schedule));
  }

  /// <summary>Holds a mutation built by name, with its arguments, for the next save.</summary>
  internal void Schedule(QueryNode mutation, IReadOnlyList<object?> arguments, CommandSchedule schedule) {
    ThrowIfSaving();
    _scheduled.Add(new ScheduledMutation(mutation, arguments, schedule));
  }

  /// <summary>Asynchronously runs a typed query's expression and returns entities, projected rows or a value, with
  /// the includes loaded.</summary>
  /// <remarks>A query rooted at a queryable property is first offered to the smart load, which may answer it for
  /// every sibling owner at once.</remarks>
  async Task<object?> IQueryExecutor.ExecuteAsync(Expression expression, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(expression);
    var info = ExpressionTreeScanner.Scan(expression);
    var builder = new QueryTreeBuilder(() => ExpressionTreeConverter.Convert(expression));
    var request = new DataRequest(this, info, builder) { Cancellation = cancellationToken };
    object? result;
    if (IsSmartLoading && info.RootSet is IMemberRowSet rowSet
        && await TryRunForAllOwnersAsync(rowSet, request) is (true, var answer))
      result = answer;
    else
      result = await RunQueryAsync(request);

    // A typed query answers with entities, not records.
    switch (result) {
      case IReadOnlyList<EntityRecord> records: {
        var instances = new List<object?>(records.Count);
        // A null record is the row of an optional reference that points at nothing.
        foreach (var record in records)
          instances.Add(record?.EntityInstance);
        return instances;
      }
      case EntityRecord record:
        return record.EntityInstance;
      case IReadOnlyList<ProjectionData> rows:
        ReplaceRecordsWithEntities(rows);
        return rows;
      default:
        return GetGroups(result) is { } groups ? ReplaceRecordsWithEntities(groups) : result;
    }
  }

  /// <summary>Asynchronously runs a query request and returns its records, rows, values or one value, with the
  /// query's includes and the session's loaded on the records.</summary>
  internal async Task<object?> RunQueryAsync(DataRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    var result = await Database.RequestExecutor.ExecuteAsync(request);
    var includes = Database.QueryCache.Find(request.Query!.CacheKey)?.Includes ?? [];
    switch (result) {
      case IReadOnlyList<EntityRecord> records:
        await RunIncludesAsync(records, includes);
        break;
      case EntityRecord record:
        await RunIncludesAsync([record], includes);
        break;
      // A grouping returned as groups: the rows are inside the groups.
      default:
        if (GetGroups(result) is { } groups)
          await RunIncludesAsync([.. groups.SelectMany(g => g).OfType<EntityRecord>()], includes);
        break;
    }
    return result;
  }

  /// <summary>Gets a result as the groups of a grouping returned as groups, which the evaluator answers with as a
  /// list of objects; <see langword="null"/> for any other result.</summary>
  private static IReadOnlyList<IGrouping<object?, object?>>? GetGroups(object? result) =>
    result is IReadOnlyList<object?> { Count: > 0 } items && items[0] is IGrouping<object?, object?>
      ? [.. items.Cast<IGrouping<object?, object?>>()]
      : null;

  /// <summary>Makes the groups of a grouping returned as groups again, with the records among their rows, and in a
  /// key of several values, replaced by their entities.</summary>
  private static List<IGrouping<object?, object?>> ReplaceRecordsWithEntities(
      IReadOnlyList<IGrouping<object?, object?>> groups) {
    var replaced = new List<IGrouping<object?, object?>>();
    foreach (var group in groups) {
      if (group.Key is ProjectionData keyRow)
        ReplaceRecordsWithEntities([keyRow]);
      replaced.Add(new RowGrouping<object?, object?>(group.Key,
        [.. group.Select(row => row is EntityRecord record ? record.EntityInstance : row)]));
    }
    return replaced;
  }

  /// <summary>Replaces the records in the slots of projected rows with their entities, in place.</summary>
  internal static void ReplaceRecordsWithEntities(IReadOnlyList<ProjectionData> rows) {
    if (rows.Count == 0)
      return;
    var members = rows[0].Entity.Members;
    foreach (var row in rows)
      foreach (var member in members)
        if (row.GetValue(member) is EntityRecord slot)
          row.SetValue(member, slot.EntityInstance);
  }
}
