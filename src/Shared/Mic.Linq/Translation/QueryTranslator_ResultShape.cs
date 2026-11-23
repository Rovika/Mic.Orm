using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 3: the shape of the result, and the root's output list finalised to say so -----------------------------

public sealed partial class QueryTranslator {

  /// <summary>Builds the result info: what the SQL tree's root returns, finalising its output lists on the way.
  /// </summary>
  /// <remarks>An empty select list means every stored member of the result set's entity, and is left empty; a derived
  /// table or a set operation that has started naming values has every member projected out, since what such a
  /// select does not name it does not expose. A bundle is the one root whose list is written out here.</remarks>
  private QueryResultInfo BuildResultInfo(QueryNode sqlRoot) {
    switch (sqlRoot) {
      case SelectNode { SelectList.Children: [AggregateValueNode aggregate] }:
        return new QueryResultInfo(QueryResultKind.Scalar, null, [], aggregate.DataType ?? typeof(object));
      case ExistsNode or LogicalOpNode { OpType: LogicalOpType.Not }:
        return new QueryResultInfo(QueryResultKind.Scalar, null, [], typeof(bool));

      // A mutation returns the count of rows it affected, and the entity it wrote for whoever holds that entity's
      // rows in memory.
      case DeleteNode delete:
        return new QueryResultInfo(QueryResultKind.Affected, delete.Target.Entity, [], typeof(int));
      case UpdateNode update:
        return new QueryResultInfo(QueryResultKind.Affected, update.Target.Entity, [], typeof(int));
      case InsertNode insert:
        return new QueryResultInfo(QueryResultKind.Affected, insert.Target.Entity, [], typeof(int));

      case SelectNode { Entity: BundleShape bundle } select:
        return BuildBundleInfo(select, bundle);

      case SelectNode select:
        return BuildRowsInfo(select, select.Entity, select.ResultSet, select.SelectList);

      // Each side exposes the members; the left side's shape is the result's.
      case SetOperationNode combined: {
        var entity = combined.Entity
          ?? throw new InvalidOperationException("A set operation at the root names no entity to read rows of.");
        var members = GetResultMembers(entity, (SelectNode)combined.Left);
        if (ExposesOutputValues(combined))
          foreach (var member in members) {
            ProjectMember((SelectNode)combined.Left, member);
            ProjectMember((SelectNode)combined.Right, member);
          }
        return BuildMembersInfo(entity, members);
      }

      default:
        throw new NotSupportedException($"A '{sqlRoot}' at the root of the SQL tree returns nothing a reader can read.");
    }
  }

  /// <summary>Describes rows of an entity or of a shape: the members the row comes back as, exposed through whatever
  /// the rows come from.</summary>
  private QueryResultInfo BuildRowsInfo(SelectNode select, ILinqEntity? entity, RowSetNode resultSet, ClauseNode list) {
    if (entity == null)
      throw new InvalidOperationException("A select at the root names no entity to read rows of.");
    // A projection's list is its shape, written already.
    if (!list.IsEmpty)
      return BuildMembersInfo(entity, [.. list.Children.Select(selected => GetShapeField(entity, selected.Name!))]);
    // An inner select that already names values has to name them all, which asking for each does.
    var members = GetResultMembers(entity, resultSet);
    if (ExposesOutputValues(resultSet))
      foreach (var member in members)
        BuildColumn(resultSet.Row, member);
    return BuildMembersInfo(entity, members);
  }

  /// <summary>Determines whether a derived table or a set operation has started naming its output values, after
  /// which what it does not name it does not expose.</summary>
  private static bool ExposesOutputValues(RowSetNode resultSet) => resultSet switch {
    DerivedRowSetNode derived => !derived.Select.SelectList.IsEmpty,
    SetOperationNode combined => !((SelectNode)combined.Left).SelectList.IsEmpty || !((SelectNode)combined.Right).SelectList.IsEmpty,
    // A common table expression is read as what its body is.
    CteRefNode { Cte.Body: SelectNode body } => !body.SelectList.IsEmpty,
    CteRefNode { Cte.Body: var body } => ExposesOutputValues(body),
    _ => false,
  };

  /// <summary>Describes a bundle, writing each slot's columns onto the list, a row slot's as its entity's stored
  /// members and a value slot's as one value, and recording where each lies.</summary>
  /// <remarks>The columns are named slot_member so the list reads, though the reader goes by position.</remarks>
  private QueryResultInfo BuildBundleInfo(SelectNode select, BundleShape bundle) {
    var values = _bundles[bundle];
    var slots = new List<QueryResultSlot>(values.Count);
    var members = new List<ILinqEntityMember>();
    select.SelectList.Children.Clear();
    for (var i = 0; i < values.Count; i++) {
      var field = GetShapeField(bundle, bundle.Slots[i].Name!);
      var offset = members.Count;
      switch (values[i]) {
        case RowNode row when row.Entity is { } entity: {
          var rowMembers = GetResultMembers(entity, row.RowSet);
          foreach (var member in rowMembers)
            select.SelectList.AddChild(new SelectedValueNode(BuildColumn(row, member), $"{field.Name}_{member.Name}"));
          members.AddRange(rowMembers);
          slots.Add(new QueryResultSlot(field, entity, rowMembers, offset, rowMembers.Count, null));
          break;
        }
        case LambdaParameterNode { Entity: BundleShape }:
          throw new NotSupportedException(
            $"'{field.Name}' is a projection inside a projection — name the rows and values you mean in one `new {{ … }}`.");
        case var value: {
          select.SelectList.AddChild(new SelectedValueNode(value, field.Name));
          members.Add(field);
          slots.Add(new QueryResultSlot(field, null, [], offset, 1, field.DataType));
          break;
        }
      }
    }
    return new QueryResultInfo(QueryResultKind.Rows, bundle.Entity, members, null, slots);
  }

  /// <summary>Describes entities for an entity the database stores, and rows for a shape, collapsed to its one column
  /// where it has one.</summary>
  private QueryResultInfo BuildMembersInfo(ILinqEntity entity, IReadOnlyList<ILinqEntityMember> members) {
    if (IsStored(entity))
      return new QueryResultInfo(QueryResultKind.Entities, entity, members);
    if (members.Count == 1)
      return new QueryResultInfo(QueryResultKind.Values, null, members, GetValueType(members[0]));
    // The plain entity, not the engine's decorator over it: the host, which made the entity, reads the rows.
    return new QueryResultInfo(QueryResultKind.Rows, TranslationHelper.GetRowEntity(entity), members);
  }

  private bool IsStored(ILinqEntity entity) => _model.FindTable(entity) != null;

  /// <summary>Gets the members a row of an entity comes back as, in the model's order: the shape's fields for a
  /// shape, the members the mapping stores in a column for a stored entity, but for the on-demand ones.</summary>
  private IReadOnlyList<ILinqEntityMember> GetResultMembers(ILinqEntity entity, RowSetNode resultSet) {
    if (!IsStored(entity))
      return entity.Members;
    var members = new List<ILinqEntityMember>();
    foreach (var member in entity.Members)
      if (!member.IsOnDemand && _model.FindColumn(entity, member) != null)
        members.Add(member);
    if (members.Count == 0)
      throw new NotSupportedException($"Entity '{entity.Name}' has no stored members, so there is nothing to select.");
    return members;
  }

  private static ILinqEntityMember GetShapeField(ILinqEntity shape, string name) =>
    shape.FindMember(name)
      ?? throw new InvalidOperationException($"Shape '{shape.Name}' has no field '{name}' for the value selected under that name.");

  private static Type GetValueType(ILinqEntityMember member) => member.DataType ?? typeof(object);
}
