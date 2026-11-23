using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Binds a query tree to the model, in place: every lambda parameter to the entity it is a row of, every
/// member access to the member it reads, every queryable call to what it produces rows of.</summary>
/// <remarks>Two walks over the tree. The first, top-down, links each lambda parameter to the node whose rows it
/// stands for: its call's source, a join's inner rowset, a collection selector's body. The second, bottom-up, binds
/// each node from its children and its link; a parameter's source is a sibling subtree of its lambda, visited
/// earlier, so it is bound by the time the parameter is reached. A member the entity does not have, or a queryable
/// method the engine does not translate, is refused by name.</remarks>
public sealed class QueryModelBinder {
  readonly ILinqDataModel _model;

  /// <summary>Initializes a new instance of the <see cref="QueryModelBinder"/> class.</summary>
  /// <param name="model">The model the tree's entities belong to, which makes a shape where the tree makes one.
  /// </param>
  public QueryModelBinder(ILinqDataModel model) {
    ArgumentNullException.ThrowIfNull(model);
    _model = model;
  }

  /// <summary>Binds a tree to the model, in place.</summary>
  /// <param name="root">The root of the tree.</param>
  public void Bind(QueryNode root) {
    ArgumentNullException.ThrowIfNull(root);
    root.ForEach(LinkParameters);
    root.ForEach(BindNode, bottomUp: true);
  }

  /// <summary>Binds a condition written over a row, such as an extension's filter, to the model, in place.
  /// </summary>
  /// <param name="condition">The condition, built against an entity set's row.</param>
  /// <param name="model">The model the condition's entities belong to.</param>
  /// <returns>The same condition, bound.</returns>
  public static QueryNode BindCondition(QueryNode condition, ILinqDataModel model) {
    ArgumentNullException.ThrowIfNull(condition);
    new QueryModelBinder(model).Bind(condition);
    return condition;
  }

  // ----- the first walk: links --------------------------------------------------------------------------------------

  /// <summary>Links the lambda parameters of a call to the nodes whose rows they stand for.</summary>
  private static void LinkParameters(QueryNode node) {
    if (node is not MethodCallNode call)
      return;
    if (TranslationHelper.IsCteCall(call)) {
      LinkCteParameters(call);
      return;
    }
    if (!call.IsQueryableCall())
      return;
    var count = call.ArgumentCount;
    var source = call.Argument(0);
    switch (call.MethodName) {
      case nameof(Queryable.Where):
      case nameof(Queryable.OrderBy):
      case nameof(Queryable.OrderByDescending):
      case nameof(Queryable.ThenBy):
      case nameof(Queryable.ThenByDescending):
      case nameof(Queryable.Skip):
      case nameof(Queryable.Take):
      case nameof(Queryable.Distinct):
      case nameof(Queryable.First):
      case nameof(Queryable.FirstOrDefault):
      case nameof(Queryable.Single):
      case nameof(Queryable.SingleOrDefault):
      case nameof(Queryable.Last):
      case nameof(Queryable.LastOrDefault):
      case nameof(Queryable.DefaultIfEmpty):
      case nameof(Queryable.Select):
        LinkLambdas(call, source, overGroupedRows: false);
        break;

      case nameof(Queryable.GroupBy):
        // The result selector overload is refused by name; GroupBy(key).Select(g => …) says the same thing.
        for (var i = 2; i < count; i++)
          if (call.Argument(i) is LambdaNode { ParameterCount: 2 })
            throw new NotSupportedException(
              "GroupBy with a RESULT selector is not translated — group by the key, then Select over the groups.");
        LinkLambdas(call, source, overGroupedRows: false);
        break;

      // A typed Join has two key selectors and a result selector over the pair; an untyped one has a condition over
      // the pair and, optionally, a result over the pair.
      case nameof(Queryable.Join):
      case nameof(Queryable.GroupJoin): {
        var inner = call.Argument(1);
        for (var i = 2; i < count; i++) {
          var lambda = call.GetLambda(i);
          if (lambda.ParameterCount == 2)
            Link(lambda, source, inner);
          else
            Link(lambda, i == 2 ? source : inner);
        }
        break;
      }

      // The result selector is over a row of the source and a row of what the collection selector produces.
      case nameof(Queryable.SelectMany) when count is 2 or 3: {
        var collection = call.GetLambda(1);
        Link(collection, source);
        if (count == 3)
          Link(call.GetLambda(2), source, collection.Body);
        break;
      }

      // A predicate or a selector is written over a row of the source, or over a group, over a row of the group's
      // source.
      case nameof(Queryable.Any):
      case nameof(Queryable.All):
      case nameof(Queryable.Contains):
      case nameof(Queryable.Count):
      case nameof(Queryable.LongCount):
      case nameof(Queryable.Sum):
      case nameof(Queryable.Min):
      case nameof(Queryable.Max):
      case nameof(Queryable.Average):
        LinkLambdas(call, source, overGroupedRows: true);
        break;

      // Two whole rowsets, no lambda.
      case nameof(Queryable.Union):
      case nameof(Queryable.Concat):
      case nameof(Queryable.Intersect):
      case nameof(Queryable.Except):
        break;

      default:
        throw new NotSupportedException(
          $"'{call.MethodName}' is not a queryable call the engine translates.");
    }
  }

  /// <summary>Links a common table expression's step over the rows found so far, and its result selector over a row
  /// and the level it was found at.</summary>
  private static void LinkCteParameters(MethodCallNode call) {
    if (TranslationHelper.IsAsCte(call))
      return;
    var source = call.Argument(0);
    Link(call.GetLambda(2), source);
    if (call.Argument(3) is not LambdaNode result)
      return;
    Link(result, source, null);
    ((LambdaParameterNode)result.Children[1]).DataType ??= typeof(int);
  }

  /// <summary>Links every lambda among a call's arguments after the source to the source.</summary>
  private static void LinkLambdas(MethodCallNode call, QueryNode source, bool overGroupedRows) {
    for (var i = 1; i < call.ArgumentCount; i++)
      if (call.Argument(i) is LambdaNode lambda)
        Link(lambda, [source], overGroupedRows);
  }

  /// <summary>Links a lambda's parameters, in order, to the nodes whose rows they stand for.</summary>
  private static void Link(LambdaNode lambda, params QueryNode?[] rowSources) =>
    Link(lambda, rowSources, overGroupedRows: false);

  private static void Link(LambdaNode lambda, QueryNode?[] rowSources, bool overGroupedRows) {
    if (lambda.ParameterCount != rowSources.Length)
      throw new NotSupportedException(
        $"A lambda with {lambda.ParameterCount} parameter(s) is written where {rowSources.Length} row(s) are in scope.");
    for (var i = 0; i < rowSources.Length; i++) {
      var parameter = (LambdaParameterNode)lambda.Children[i];
      parameter.RowSource = rowSources[i];
      parameter.OverGroupedRows = overGroupedRows;
    }
  }

  // ----- the second walk: binding -----------------------------------------------------------------------------------

  /// <summary>Binds a node, its children and the link already bound.</summary>
  private void BindNode(QueryNode node) {
    switch (node) {
      // The rows' entity is made here, where the node was built with no model in hand.
      case JsonRowsNode rows:
        if (!rows.HasEntity)
          rows.SetEntity(rows.ElementType is { } elementType
            ? JsonTranslationHelper.GetJsonShape(_model, elementType)
            : _model.CreateTransientEntity(rows.EntityName, rows.Specs));
        break;

      case LambdaParameterNode parameter: {
        var entity = parameter.RowSource?.GetEntity();
        parameter.Entity = parameter.OverGroupedRows ? GetAggregateRows(entity) : entity;
        break;
      }

      case MemberAccessNode member:
        member.Entity = BindMember(member);
        break;

      case MethodCallNode call when call.IsQueryableCall():
        call.Entity = BindQueryableCall(call);
        break;

      // A node built by name is typed again, since it was made before its members were bound; a node converted
      // from C# keeps the compiler's type.
      case BinaryNode { Source: null } binary:
        binary.DataType = binary.IsComparison || binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse
          ? typeof(bool)
          : binary.Left.DataType ?? binary.Right.DataType;
        break;

      // A function's rows written inside the query: bound to the entity its type argument names.
      case MethodCallNode function when TranslationHelper.IsTableFunctionCall(function):
        function.Entity = BindTableFunction(function);
        break;

      // A common table expression comes before the refusal below: it returns a query and is no call on a session.
      case MethodCallNode cte when TranslationHelper.IsCteCall(cte):
        cte.Entity = BindCte(cte);
        break;

      case MethodCallNode mutation when TranslationHelper.IsMutation(mutation):
        BindMutation(mutation);
        break;

      // A call that starts a query inside a lambda names no set without running; a sub-query is written into a local.
      case MethodCallNode { ReturnType: { } returns } call when typeof(IQueryable).IsAssignableFrom(returns):
        throw new NotSupportedException(
          $"'{call.MethodName}' returns a query and is called inside the query — put the sub-query in a local variable "
          + "and use that; a query cannot be started inside a lambda.");
    }
  }

  /// <summary>Binds a member access to the member on the entity its target produces, and returns what the member
  /// produces rows of.</summary>
  /// <remarks>A member of a row answers with what a reference reaches. A member of a value has no entity to bind
  /// against and keeps the CLR member the typed path recorded; by name, a member with nothing to read it from is
  /// refused.</remarks>
  private ILinqEntity? BindMember(MemberAccessNode member) {
    var owner = member.Target?.GetEntity();
    // A hop through a group row's key that leads nowhere means the source row's reference of that name, as in an
    // ordering written over the very expression the grouping groups on.
    if (owner == null && member.Target is MemberAccessNode hop && hop.Target != null
        && hop.Target.GetEntity() is GroupShape grouping
        && grouping.SourceEntity.FindMember(hop.MemberName) is { } sourceMember
        && sourceMember.GetTargetEntity() is { } reached) {
      hop.ModelMember = sourceMember;
      hop.DataType = null;
      hop.Entity = reached;
      owner = reached;
    }
    if (owner == null) {
      if (member.ClrMember == null)
        throw new NotSupportedException(
          $"'{member.MemberName}' is read from a '{member.Target?.Kind}' node, which names no entity to look it up on.");
      // A property holding an array of objects produces rows of the element type's shape; an array of scalars is a
      // value.
      return member.DataType is { } clrType && JsonTranslationHelper.GetJsonElementType(clrType) is { } element
          && !element.IsScalarType()
        ? JsonTranslationHelper.GetJsonShape(_model, element)
        : null;
    }
    // g.Key is what the grouping groups on: the one key's value, or the group itself for a new { … }, whose parts
    // are then read by member.
    if (owner is GroupShape group && member.ClrMember != null && member.MemberName == "Key") {
      if (group.IsComposite)
        return group;
      member.DataType = group.Keys[0].DataType;
      return group.Keys[0].GetEntity();
    }
    // A member of a bundle is the slot it names, which the transform reads in its place; there is no model member.
    if (owner is BundleShape bundle) {
      var slot = bundle.GetSlot(member.MemberName);
      member.DataType ??= slot.DataType;
      return slot.GetEntity();
    }
    // A member named off a group row that is not a key is a member of the source row, which the transform reads off
    // the source.
    var modelMember = owner.FindMember(member.MemberName)
      ?? (owner as GroupShape)?.SourceEntity.FindMember(member.MemberName)
      ?? throw new NotSupportedException($"Entity '{owner.Name}' has no member named '{member.MemberName}'.");
    member.ModelMember = modelMember;
    member.DataType ??= modelMember.DataType;
    // A JSON member holding an array of objects produces rows of the element type's shape.
    var target = modelMember.GetTargetEntity();
    if (target == null && modelMember.DataType is { } valueType
        && JsonTranslationHelper.GetJsonElementType(valueType) is { } jsonElement && !jsonElement.IsScalarType())
      return JsonTranslationHelper.GetJsonShape(_model, jsonElement);
    return target;
  }

  /// <summary>Returns what a queryable call produces rows of, its arguments bound; a call that is one value types
  /// itself and returns <see langword="null"/>.</summary>
  private ILinqEntity? BindQueryableCall(MethodCallNode call) {
    var count = call.ArgumentCount;
    var source = call.Argument(0).GetEntity();
    switch (call.MethodName) {
      // Rows of the source, whatever was done to them.
      case nameof(Queryable.Where):
      case nameof(Queryable.OrderBy):
      case nameof(Queryable.OrderByDescending):
      case nameof(Queryable.ThenBy):
      case nameof(Queryable.ThenByDescending):
      case nameof(Queryable.Skip):
      case nameof(Queryable.Take):
      case nameof(Queryable.Distinct):
      case nameof(Queryable.First):
      case nameof(Queryable.FirstOrDefault):
      case nameof(Queryable.Single):
      case nameof(Queryable.SingleOrDefault):
      case nameof(Queryable.Last):
      case nameof(Queryable.LastOrDefault):
      case nameof(Queryable.DefaultIfEmpty):
        return source;

      // Rows of a shape, or of an entity a whole row of which was selected, or single values.
      case nameof(Queryable.Select): {
        var selector = call.GetLambda(1);
        var produced = GetProducedEntity(selector.Body, call);
        // By name, a projection over a grouping keeps the keys in front: the shape is the keys, then the slots.
        if (call.Source == null && source is GroupShape group && selector.Body is NewNode
            && produced is { IsTransient: true } and not (GroupShape or BundleShape))
          produced = BuildKeysFirstShape(group, produced);
        return produced;
      }

      // A grouping produces rows of groups: a GroupShape whose members are the keys.
      case nameof(Queryable.GroupBy):
        return GroupShape.Make(_model, source, call.GetLambda(1));

      // Rows of the pair's result selector where there is one; the source's otherwise.
      case nameof(Queryable.Join):
      case nameof(Queryable.GroupJoin): {
        if (count < 3)
          return source;
        var last = call.GetLambda(count - 1);
        return last.ParameterCount == 2 ? GetProducedEntity(last.Body, call) : source;
      }

      // Rows of what the collection produces, or of what the result selector makes of the pair.
      case nameof(Queryable.SelectMany) when count is 2 or 3:
        return count == 3
          ? GetProducedEntity(call.GetLambda(2).Body, call)
          : call.GetLambda(1).Body.GetEntity();

      // Two whole rowsets; the shape is the left side's.
      case nameof(Queryable.Union):
      case nameof(Queryable.Concat):
      case nameof(Queryable.Intersect):
      case nameof(Queryable.Except):
        return source;

      // One value over the rows.
      case nameof(Queryable.Any):
      case nameof(Queryable.All):
      case nameof(Queryable.Contains):
        call.DataType ??= typeof(bool);
        return null;

      case nameof(Queryable.Count):
        call.DataType ??= typeof(int);
        return null;

      case nameof(Queryable.LongCount):
        call.DataType ??= typeof(long);
        return null;

      case nameof(Queryable.Sum):
      case nameof(Queryable.Min):
      case nameof(Queryable.Max):
        // What the caller asked for where the typed path says; the selector's own type where it does not.
        call.DataType ??= call.ReturnType ?? (count > 1 ? call.GetLambda(1).Body.DataType : null);
        return null;

      case nameof(Queryable.Average):
        // As above, but an average of integers is a double, as LINQ's is.
        call.DataType ??= call.ReturnType ?? GetAverageType(count > 1 ? call.GetLambda(1).Body.DataType : null);
        return null;

      default:
        throw new NotSupportedException(
          $"'{call.MethodName}' is not a queryable call the engine translates.");
    }
  }

  /// <summary>Returns what a common table expression produces rows of: what it is written on, or what its result
  /// selector makes of a row and its level.</summary>
  /// <summary>Binds a <c>TableFunctions.Call</c> to the entity its row type names.</summary>
  private ILinqEntity BindTableFunction(MethodCallNode call) {
    var rowType = call.ReturnType is { IsGenericType: true } returns ? returns.GetGenericArguments()[0] : null;
    if (rowType == null)
      throw new NotSupportedException($"'{call}' names no row type.");
    return _model.FindEntity(rowType)
      ?? throw new NotSupportedException($"'{rowType.Name}' is not an entity of the model, so no function returns its rows.");
  }

  private ILinqEntity? BindCte(MethodCallNode call) {
    var source = call.Argument(0).GetEntity();
    if (TranslationHelper.IsAsCte(call) || call.Argument(3) is not LambdaNode result)
      return source;
    return GetProducedEntity(result.Body, call);
  }

  /// <summary>Binds a mutation to its target: the source's own entity, which has to be a stored one, or the entity a
  /// by-key marker names.</summary>
  private void BindMutation(MethodCallNode mutation) {
    var entity = mutation.Argument(0).GetEntity();
    if (TranslationHelper.NamesMutationTarget(mutation)) {
      // The target is on the node where the tree was built by name; the typed path names it as a type argument.
      if (mutation.Entity == null) {
        var targetType = TranslationHelper.GetMutationTargetType(mutation);
        mutation.Entity = _model.FindEntity(targetType)
          ?? throw new NotSupportedException($"'{targetType.Name}' is not an entity of the model, so it has no rows to delete, update or insert.");
      }
      entity = mutation.Entity;
    } else if (entity == null || TranslationHelper.GetRowEntity(entity).IsTransient)
      throw new NotSupportedException(
        "A delete needs rows of an entity to delete — the query projects, groups or bundles rows, and there is no "
        + "table those come from. Write the filter over the entity set and delete that, or project the key and "
        + "name the entity: ExecuteDeleteAsync<IEntity>().");
    if (entity.IsReadOnly)
      throw new NotSupportedException(
        $"'{entity.Name}' is read-only, its rows a query's; "
        + "they cannot be deleted, updated or inserted by a statement.");
    mutation.Entity = entity;
    mutation.DataType = typeof(int);
  }

  /// <summary>Gets what a selector's body produces rows of: the shape it constructs, the entity of the row it hands
  /// back whole, or <see langword="null"/> for a single value.</summary>
  private ILinqEntity? GetProducedEntity(QueryNode body, MethodCallNode call) {
    if (body is not NewNode composite)
      return body.GetEntity();
    // One slot that is a whole row, the untyped spelling of selecting a reference, produces rows of that entity.
    if (composite.ChildCount == 1 && composite.Children[0].GetEntity() is { } wholeRow)
      return wholeRow;
    // The shape is named by the source it projects, so two projections of one entity read alike in a text.
    var names = composite.SlotNames;
    var slots = new List<QueryNode>(composite.ChildCount);
    var holdsRows = false;
    for (var i = 0; i < composite.ChildCount; i++) {
      var slot = composite.Children[i];
      if (names?[i] is { } name)
        slot.Name = name;
      if (string.IsNullOrEmpty(slot.Name))
        throw new NotSupportedException(
          $"Slot {i} of the projection in {call.MethodName} has no name to take — give it one.");
      slots.Add(slot);
      holdsRows |= slot.GetEntity() != null;
    }
    var sourceName = call.Argument(0).GetEntity()?.Name ?? "Row";
    // A projection with a whole row among its slots is a bundle, a renaming of what is in scope.
    return holdsRows
      ? BundleShape.Make(_model, sourceName + "Bundle", slots)
      : TranslationHelper.BuildRowShape(_model, sourceName + "Row", slots);
  }

  /// <summary>Builds the shape a by-name projection over a grouping produces: the grouping's keys, then the projected
  /// slots.</summary>
  private ILinqEntity BuildKeysFirstShape(GroupShape group, ILinqEntity slots) {
    var members = new List<LinqMemberSpec>();
    // A key the projection names itself gives way to that slot.
    foreach (var key in group.Members)
      if (slots.FindMember(key.Name) == null)
        members.Add(new LinqMemberSpec(key.Name, key.DataType ?? typeof(object), key.IsNullable));
    foreach (var slot in slots.Members)
      members.Add(new LinqMemberSpec(slot.Name, slot.DataType ?? typeof(object), slot.IsNullable));
    return _model.CreateTransientEntity(slots.Name, members);
  }

  /// <summary>Gets what an aggregate over rows is written over: the group's source rows for a group, the rows
  /// themselves otherwise.</summary>
  private static ILinqEntity? GetAggregateRows(ILinqEntity? source) =>
    source is GroupShape group ? group.SourceEntity : source;

  /// <summary>Gets the type of an average over values of a type: a double for an integer, nullable where the
  /// integer is, and the type itself for any other.</summary>
  private static Type? GetAverageType(Type? valueType) {
    if (valueType == null)
      return null;
    var type = Nullable.GetUnderlyingType(valueType) ?? valueType;
    var isInteger = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
    if (!isInteger)
      return valueType;
    return type == valueType ? typeof(double) : typeof(double?);
  }
}
