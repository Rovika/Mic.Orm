using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Builds the query behind a queryable property, and the many-owners form of a caller's query over one for
/// a smart load.</summary>
/// <remarks>A queryable property takes no ordering of its own; the caller orders it where a page needs one, as with
/// any query. Nothing here touches an expression: the property's own query is a tree the set carries and the
/// converter splices, and the many-owners form is node surgery over what the converter made of the caller's chain,
/// decided once per shape.</remarks>
internal static class QueryablePropertyHelper {
  /// <summary>Builds the query behind <c>owner.Member</c>, rooted at a <see cref="MemberRowSet{T}"/>.</summary>
  /// <param name="provider">The query provider the query runs through.</param>
  /// <param name="owner">The record the property is read off.</param>
  /// <param name="member">The queryable member.</param>
  /// <returns>The query, typed by the target's interface.</returns>
  internal static IQueryable BuildQuery(EntityQueryProvider provider, EntityRecord owner, EntityMemberInfo member) {
    var info = member.ListInfo
      ?? throw new InvalidOperationException($"'{member.Entity?.Name}.{member.Name}' is a list with no list info.");
    // The set is the query: an IQueryable whose expression is itself, and whose meaning is the tree it builds when the
    // engine asks.
    return (IQueryable)Activator.CreateInstance(
      typeof(MemberRowSet<>).MakeGenericType(GetInterfaceType(info.TargetEntity, member)),
      provider, info.TargetEntity, owner, member)!;
  }

  // ----- many owners at once ---------------------------------------------------------------------------------------------

  /// <summary>Specifies the prefix of the slots the owner's key rides in, one per key column: <c>OwnerKey0</c>,
  /// <c>OwnerKey1</c> and so on.</summary>
  public const string OwnerKeySlot = "OwnerKey";
  public const string RowSlot = "Row";

  /// <summary>Reads the owner a row of the many-owners form belongs to off its owner-key slots.</summary>
  /// <param name="row">The row of the many-owners form.</param>
  /// <param name="member">The queryable member.</param>
  /// <returns>The owner's key, or <see langword="null"/> where a slot is null.</returns>
  internal static EntityKey? ReadOwnerKey(ProjectionData row, EntityMemberInfo member) {
    var key = member.ListInfo!.InverseMember!.ReferenceInfo!.ToKey;
    var count = key.MembersExpanded.Count;
    if (count == 1)
      return row.GetValue(OwnerKeySlot + "0") is { } value ? new EntityKey(key, value) : null;
    var values = new object?[count];
    for (var i = 0; i < count; i++)
      if ((values[i] = row.GetValue(OwnerKeySlot + i)) == null)
        return null;
    return new EntityKey(key, values);
  }

  /// <summary>Builds the many-owners tree from the one-owner tree the converter made, when the shape is new: the
  /// same chain, asked of every sibling in one statement; see <see cref="ManyOwnersRootBuilder"/>.</summary>
  /// <param name="root">The caller's query, converted.</param>
  /// <param name="rowSet">The property's set at the bottom of the tree.</param>
  /// <param name="owners">The argument object the scan carries: the siblings' records.</param>
  /// <returns>The many-owners tree, or <see langword="null"/> for a chain the form cannot be written for, which then
  /// runs for its one owner.</returns>
  internal static QueryNode? BuildManyOwnersRoot(QueryNode root, IMemberRowSet rowSet, object owners) =>
    new ManyOwnersRootBuilder(rowSet, owners).Build(root);

  private static Type GetInterfaceType(EntityTypeInfo entity, EntityMemberInfo member) =>
    entity.InterfaceType
      ?? throw new InvalidOperationException(
        $"'{member.Entity?.Name}.{member.Name}': '{entity.Name}' has no interface to type a query by.");
}
