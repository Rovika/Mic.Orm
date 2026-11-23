using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Builds the queries a member implies, by name: a list member's rows of an owner, the rows that are any of
/// several records, the row of one key, and whether any row refers through a reference to one. This class cannot be
/// inherited.</summary>
/// <remarks>Every argument is an entity record, or an array of them; the engine turns a reference compared to an entity
/// into foreign-key columns against key values read off the argument when the statement is bound, so a parameter's
/// declared type is <c>object</c>. Many owners at once are an <c>EXISTS</c> over the rows of a parameter, so the text
/// is fixed however many owners there are.</remarks>
public static class MemberQueryHelper {
  /// <summary>Specifies the slot name of the link row in a many-to-many list's load.</summary>
  public const string LinkSlot = "Link";
  /// <summary>Specifies the slot name of the target row in a many-to-many list's load.</summary>
  public const string TargetSlot = "Target";

  /// <summary>Gets the entity a reference or a list leads to.</summary>
  /// <param name="member">The member.</param>
  /// <returns>The target entity, or <see langword="null"/> for a value member.</returns>
  public static ILinqEntity? GetTargetEntity(this ILinqEntityMember member) =>
    member.ReferenceInfo?.TargetEntity ?? member.ListInfo?.TargetEntity;

  /// <summary>Builds the query that loads a list's rows in its default order: for a one-to-many the target's rows;
  /// for a many-to-many the link's rows, each with the target it points at, as a bundle of <see cref="LinkSlot"/> and
  /// <see cref="TargetSlot"/>.</summary>
  /// <param name="list">The list member.</param>
  /// <param name="manyOwners"><see langword="true"/> to load the rows of any of several owners; otherwise,
  /// <see langword="false"/> to load the rows of one owner.</param>
  /// <returns>A query taking one argument: the owner, or an array of owners.</returns>
  /// <remarks>The order is the model's, flagged <see cref="NodeFlags.DefaultOrder"/>, so a caller composing over the
  /// query orders it with a plain <c>OrderBy</c> and takes over.</remarks>
  public static QueryNode BuildListLoadQuery(ILinqEntityMember list, bool manyOwners = false) {
    var (near, inverse) = GetListSource(list);
    var info = list.ListInfo!;
    var onward = info.IsManyToMany
      ? info.LinkTargetMember
        ?? throw new InvalidOperationException($"'{list.Entity?.Name}.{list.Name}': the link does not lead on to a target.")
      : null;
    var query = QueryMaker.From(near).Where(row => MatchOwner(row, inverse, manyOwners));
    foreach (var term in info.DefaultOrder) {
      var onTarget = onward != null && ReferenceEquals(term.Member.Entity, info.TargetEntity);
      foreach (var column in GetOrderColumns(term.Member))
        query = query.OrderBy(row => onTarget ? row.Member(onward!).Member(column) : row.Member(column),
          term.Descending, isDefault: true);
    }
    return onward == null ? query : query.Select(row => [row.As(LinkSlot), row.Member(onward).As(TargetSlot)]);
  }

  /// <summary>Builds a list member's rows of one owner as a query to compose over: the target's rows, unordered.
  /// </summary>
  /// <param name="list">The list member.</param>
  /// <param name="owner">The owner as a parameter node, however the caller has it read.</param>
  /// <returns>A query over the target's rows, which a filter, an ordering or a page goes on top of.</returns>
  /// <remarks>A many-to-many is an <c>EXISTS</c> over the link, so whatever is composed lands on one plain select.
  /// </remarks>
  public static QueryNode BuildListMemberQuery(ILinqEntityMember list, QueryNode owner) {
    ArgumentNullException.ThrowIfNull(owner);
    var (near, inverse) = GetListSource(list);
    var info = list.ListInfo!;
    var target = info.TargetEntity;
    if (!info.IsManyToMany)
      return QueryMaker.From(target).Where(row => row.Member(inverse).Equal(owner));
    var onward = info.LinkTargetMember
      ?? throw new InvalidOperationException($"'{list.Entity?.Name}.{list.Name}': the link does not lead on to a target.");
    return QueryMaker.From(target).Where(row =>
      QueryMaker.From(near).Any(link => link.Member(inverse).Equal(owner) & link.Member(onward).Equal(row)));
  }

  /// <summary>Builds the query for the rows of an entity that are any of several records.</summary>
  /// <param name="entity">The entity.</param>
  /// <returns>A query taking one argument: the records.</returns>
  public static QueryNode BuildByKeysQuery(ILinqEntity entity) {
    ArgumentNullException.ThrowIfNull(entity);
    var key = entity.PrimaryKeyMembers;
    if (key.Count == 0)
      throw new ArgumentException($"'{entity.Name}' has no primary key to load its rows by.", nameof(entity));
    return QueryMaker.From(entity).Where(row =>
      BuildListAnyQuery(row, key, QueryMaker.Parameter("rows", typeof(object[])), key));
  }

  /// <summary>Builds the query for some members of the rows of an entity that are any of several records.</summary>
  /// <param name="entity">The entity.</param>
  /// <param name="members">The members to read.</param>
  /// <returns>A query taking one argument, the records, and returning for each row its primary key members and the
  /// given members, each under its own name.</returns>
  public static QueryNode BuildMembersByKeysQuery(ILinqEntity entity, IReadOnlyList<ILinqEntityMember> members) {
    ArgumentNullException.ThrowIfNull(members);
    return BuildByKeysQuery(entity).Select(row =>
      [.. entity.PrimaryKeyMembers.Select(row.Member), .. members.Select(row.Member)]);
  }

  /// <summary>Builds the query for the row with a value of a key, with the terms of a composite key joined by
  /// <c>AND</c>.</summary>
  /// <param name="entity">The entity.</param>
  /// <param name="keyMembers">The members of the key, in order.</param>
  /// <returns>A query taking one argument per key member, named after it, in the key's order.</returns>
  public static QueryNode BuildByKeyQuery(ILinqEntity entity, IReadOnlyList<ILinqEntityMember> keyMembers) {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(keyMembers);
    if (keyMembers.Count == 0)
      throw new ArgumentException($"'{entity.Name}': a key of no members finds no row.", nameof(keyMembers));
    return QueryMaker.From(entity).Where(row => {
      var terms = new QueryNode[keyMembers.Count];
      for (var i = 0; i < terms.Length; i++) {
        var member = keyMembers[i];
        terms[i] = row.Member(member).Equal(QueryMaker.Parameter(member.Name, member.DataType ?? typeof(object)));
      }
      return terms.Length == 1 ? terms[0] : QueryMaker.And(terms);
    });
  }

  /// <summary>Builds the query that asks, for one row, which entities have a row pointing at it.</summary>
  /// <param name="target">The entity of the row asked about.</param>
  /// <param name="referrers">The references to ask about, grouped by the entity they are members of.</param>
  /// <returns>A query taking one argument, the record of the row, and returning one row with a Boolean slot per
  /// group, named by <see cref="GetReferrerSlotName"/>; no row if the record's row is not there.</returns>
  public static QueryNode BuildReferrersQuery(ILinqEntity target,
      IReadOnlyList<IReadOnlyList<ILinqEntityMember>> referrers) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(referrers);
    return QueryMaker.From(target)
      .Where(row => row.Equal(QueryMaker.Parameter("record", typeof(object))))
      .Select(row => {
        var slots = new QueryNode[referrers.Count];
        for (var i = 0; i < slots.Length; i++)
          slots[i] = BuildAnyReferring(row, referrers[i]).As(GetReferrerSlotName(i));
        return slots;
      });
  }

  /// <summary>Gets the name of the slot that answers for a group of references in the query of
  /// <see cref="BuildReferrersQuery"/>.</summary>
  /// <param name="index">The position of the group.</param>
  /// <returns>The slot's name.</returns>
  public static string GetReferrerSlotName(int index) => "r" + index;

  /// <summary>Builds the condition that some row of an entity points at a row through any of its references.
  /// </summary>
  private static QueryNode BuildAnyReferring(QueryNode row, IReadOnlyList<ILinqEntityMember> references) {
    if (references.Count == 0 || references.Any(r => r.Kind != LinqMemberKind.Reference || r.Entity == null))
      throw new ArgumentException("A group of referrers is one or more reference members of one entity.");
    return QueryMaker.From(references[0].Entity!).Any(referrer => {
      var terms = new QueryNode[references.Count];
      for (var i = 0; i < terms.Length; i++)
        terms[i] = referrer.Member(references[i]).Equal(row);
      return terms.Length == 1 ? terms[0] : QueryMaker.Or(terms);
    });
  }

  /// <summary>Builds the condition that a row's key members match the key of any record of a parameter, as an
  /// <c>EXISTS</c> over the rows of the parameter.</summary>
  /// <param name="row">The row.</param>
  /// <param name="rowMembers">The members of the row that hold the key: its own key, or a foreign key.</param>
  /// <param name="records">The parameter whose value is the records; the rows are typed after the key members and
  /// read off each record when the statement is bound.</param>
  /// <param name="keyMembers">The key members of the records, lined up with <paramref name="rowMembers"/>.</param>
  /// <returns>A condition over the row.</returns>
  public static QueryNode BuildListAnyQuery(QueryNode row, IReadOnlyList<ILinqEntityMember> rowMembers,
      QueryParameterNode records, IReadOnlyList<ILinqEntityMember> keyMembers) {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentNullException.ThrowIfNull(records);
    if (rowMembers.Count != keyMembers.Count || rowMembers.Count == 0)
      throw new ArgumentException($"{rowMembers.Count} row member(s) against {keyMembers.Count} key member(s).", nameof(keyMembers));
    return QueryMaker.Rows(records, keyMembers.ToArray()).Any(key => {
      var terms = new QueryNode[keyMembers.Count];
      for (var i = 0; i < terms.Length; i++)
        terms[i] = row.Member(rowMembers[i]).Equal(key.Member(keyMembers[i]));
      return terms.Length == 1 ? terms[0] : QueryMaker.And(terms);
    });
  }

  /// <summary>Builds the condition that a row points at any of the owners of a parameter through a reference, its
  /// foreign key against the owners' key.</summary>
  /// <param name="row">The row.</param>
  /// <param name="reference">The reference member.</param>
  /// <param name="owners">The parameter whose value is the owners.</param>
  /// <returns>A condition over the row.</returns>
  public static QueryNode BuildListAnyOwnerQuery(QueryNode row, ILinqEntityMember reference, QueryParameterNode owners) {
    ArgumentNullException.ThrowIfNull(reference);
    if (reference.ReferenceInfo is not { FromMembers.Count: > 0 } pairing)
      throw new ArgumentException($"'{reference.Entity?.Name}.{reference.Name}' is not a reference with a key behind it.", nameof(reference));
    return BuildListAnyQuery(row, pairing.FromMembers, owners, pairing.ToMembers);
  }

  /// <summary>Builds the condition that a row's reference points at the owner parameter, or at any of the owners.
  /// </summary>
  private static QueryNode MatchOwner(QueryNode row, ILinqEntityMember reference, bool manyOwners) =>
    manyOwners
      ? BuildListAnyOwnerQuery(row, reference, QueryMaker.Parameter("owners", typeof(object[])))
      : row.Member(reference).Equal(QueryMaker.Parameter("owner", typeof(object)));

  /// <summary>Gets the columns an ordering term orders by: a reference's foreign-key members, or the member itself.
  /// </summary>
  private static IReadOnlyList<ILinqEntityMember> GetOrderColumns(ILinqEntityMember member) =>
    member.ReferenceInfo is { FromMembers.Count: > 0 } reference ? reference.FromMembers : [member];

  /// <summary>Gets the entity a list's rows are selected from, the target or the link, and the reference on it that
  /// points back at the owner.</summary>
  private static (ILinqEntity Near, ILinqEntityMember Inverse) GetListSource(ILinqEntityMember list) {
    ArgumentNullException.ThrowIfNull(list);
    var info = list.ListInfo
      ?? throw new ArgumentException($"'{list.Entity?.Name}.{list.Name}' is not a list member with something behind it.", nameof(list));
    var inverse = info.InverseMember
      ?? throw new InvalidOperationException($"'{list.Entity?.Name}.{list.Name}': nothing points back at the owner to load by.");
    var near = inverse.Entity
      ?? throw new InvalidOperationException($"'{list.Entity?.Name}.{list.Name}': the inverse belongs to no entity.");
    return (near, inverse);
  }
}
