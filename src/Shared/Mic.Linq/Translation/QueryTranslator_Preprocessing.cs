using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 1: preprocessing, local conversions that leave the tree shaped as the caller wrote it ------------------

public sealed partial class QueryTranslator {

  /// <summary>Converts what the caller wrote in C#'s terms into the engine's, bottom-up, in place: the calls, members
  /// and operators that have an engine operation, such as <c>text.ToUpper()</c>, <c>Math.Abs(x)</c> or
  /// <c>when.HasValue</c>; and what was written in terms of entities into the key columns the database has, the
  /// foreign-key shortcut, entity equality as key comparison, a list of entities as a list of keys, and an entity
  /// written as the key of a join as its key columns.</summary>
  /// <remarks>After this pass both front ends' trees say the same thing, which is what lets the cache tier know a
  /// call under its engine name, and the evaluator compares keys as the server does, since a cached row's reference
  /// is a detached snapshot. The conventions that differ between C# and SQL, such as a 1-based <c>Substring</c>, are
  /// settled here and nowhere else. A key is read off a captured entity at run time through
  /// <see cref="ILinqCallContext.ReadMember"/>.</remarks>
  private void PreprocessOps() {
    RefuseWholeGroupKeys(_root);
    _root = PreprocessOps(_root);
  }

  /// <summary>Refuses the key of a grouping written as a <c>new { … }</c> where it is read whole and not by member.
  /// </summary>
  /// <exception cref="NotSupportedException">Such a key stands under a node that is not a member read.</exception>
  private static void RefuseWholeGroupKeys(QueryNode node) {
    foreach (var child in node.Children) {
      if (node is not MemberAccessNode
          && child is MemberAccessNode { MemberName: "Key", ModelMember: null, Target: { } group }
          && group.GetEntity() is GroupShape { IsComposite: true } shape)
        throw new NotSupportedException(
          $"The key of this grouping is a 'new {{ … }}' of {shape.Keys.Count} value(s), and 'Key' is read whole. "
          + $"Read it by member, as in 'g.Key.{shape.Members[0].Name}'.");
      RefuseWholeGroupKeys(child);
    }
  }

  /// <summary>Converts the spellings of a tree, in place; the extension filters go through it too, since they are
  /// built outside the passes.</summary>
  /// <remarks>On each node the operation conversion runs first and the entity conversion over its result: a
  /// <c>Contains</c> bound to a span overload is respelled before the entity conversion looks for a list of entities,
  /// and a replacement is not visited again.</remarks>
  private static QueryNode PreprocessOps(QueryNode root) =>
    root.Transform(node => ConvertEntityShape(ConvertOp(node)), bottomUp: true);

  /// <summary>Converts a call, member or operator that has an engine operation into it; returns any other node as it
  /// is.</summary>
  private static QueryNode ConvertOp(QueryNode node) =>
    node switch {
      MethodCallNode call => ConvertCall(call) ?? call,
      MemberAccessNode { ModelMember: null, ClrMember: not null } member => ConvertClrMember(member) ?? member,
      // Three operators whose text varies by vendor are named operations: %, ?? and + over strings.
      BinaryNode { NodeType: ExpressionType.Modulo } modulo =>
        QueryMaker.NamedOp(NamedOpType.Modulo, modulo.DataType ?? modulo.Left.DataType ?? typeof(object), modulo.Left, modulo.Right),
      BinaryNode { NodeType: ExpressionType.Coalesce } coalesce =>
        QueryMaker.NamedOp(NamedOpType.Coalesce, coalesce.DataType ?? coalesce.Left.DataType ?? typeof(object), coalesce.Left, coalesce.Right),
      BinaryNode { NodeType: ExpressionType.Add, DataType: var added } concat when added == typeof(string) =>
        QueryMaker.NamedOp(NamedOpType.Concat, typeof(string), concat.Left, concat.Right),
      ExpressionNode { NodeType: ExpressionType.Conditional, ChildCount: 3 } conditional =>
        ConvertConditional(conditional),
      // The Length of an array of values is the count of its items.
      ExpressionNode { NodeType: ExpressionType.ArrayLength, ChildCount: 1 } length =>
        MakeItemCount(length.Children[0], length.Source) ?? node,
      // x == null and x != null are one operation each, IS NULL and IS NOT NULL, however the sink spells them; an
      // entity against null is the entity conversion's, which makes them per key column.
      BinaryNode { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality
          when (IsNullConstant(equality.Right) && !IsEntityValued(equality.Left))
            || (IsNullConstant(equality.Left) && !IsEntityValued(equality.Right)) =>
        QueryMaker.NamedOp(equality.NodeType == ExpressionType.Equal ? NamedOpType.IsNull : NamedOpType.IsNotNull,
          typeof(bool), IsNullConstant(equality.Right) ? equality.Left : equality.Right),
      _ => node,
    };

  /// <summary>Converts <c>test ? a : b</c> into the operation the server writes as a <c>CASE</c>.</summary>
  /// <exception cref="NotSupportedException">A result is an entity or a <c>new { … }</c>, which is several columns.
  /// </exception>
  private static QueryNode ConvertConditional(ExpressionNode conditional) {
    var (test, whenTrue, whenFalse) = (conditional.Children[0], conditional.Children[1], conditional.Children[2]);
    foreach (var result in new[] { whenTrue, whenFalse })
      if (IsEntityValued(result) || result is NewNode)
        throw new NotSupportedException(
          "A conditional operator '?:' chooses between two values; one of its results here is "
          + (result is NewNode ? "a 'new { … }'" : $"the entity '{result.GetEntity()?.Name}'")
          + ". Write a conditional for each member to select.");
    var type = conditional.DataType ?? whenTrue.DataType ?? whenFalse.DataType ?? typeof(object);
    return QueryMaker.NamedOp(NamedOpType.Conditional, type, test, whenTrue, whenFalse);
  }

  /// <summary>Converts a call on a string, on <c>Math</c> or on one of the framework's query functions into the
  /// operation it spells, or returns <see langword="null"/> where it spells none.</summary>
  private static QueryNode? ConvertCall(MethodCallNode call) {
    var type = call.ReturnType ?? call.DataType ?? typeof(object);
    // A list of strings carries its element type, and its Contains is the list's, read below.
    if (call.HasInstance && call.Children[0].DataType == typeof(string)
        && !call.Children[0].Flags.HasFlag(NodeFlags.List))
      return ConvertStringCall(call, call.Children[0]);

    // Since .NET 9, array.Contains(x) binds to a span overload of MemoryExtensions, possibly with a third, null
    // comparer; it is respelled as Enumerable.Contains so that one name is what everything downstream reads.
    if (call.DeclaringType == typeof(MemoryExtensions) && call.MethodName == nameof(Enumerable.Contains)
        && (call.ArgumentCount == 2 || call.ArgumentCount == 3 && call.Argument(2) is ConstantNode { Value: null }))
      return new MethodCallNode(nameof(Enumerable.Contains), dataType: typeof(bool), source: call.Source) {
        DeclaringType = typeof(Enumerable), ReturnType = typeof(bool), Children = { call.Argument(0), call.Argument(1) },
      };

    // JsonFunctions.Value<T>(document, path): the value at a path, read as T.
    if (call.DeclaringType == typeof(JsonFunctions) && call.MethodName == nameof(JsonFunctions.Value) && call.ArgumentCount == 2)
      return QueryMaker.NamedOp(NamedOpType.JsonValue, type, call.Argument(0), call.Argument(1));

    // GeoFunctions.Distance(point, other): metres between two points, measured by the server.
    if (call.DeclaringType == typeof(GeoFunctions) && call.MethodName == nameof(GeoFunctions.Distance) && call.ArgumentCount == 2)
      return QueryMaker.NamedOp(NamedOpType.GeoDistance, typeof(double), call.Argument(0), call.Argument(1));

    // VectorFunctions.Distance(vector, other): the distance between two vectors, measured by the server.
    if (call.DeclaringType == typeof(VectorFunctions) && call.MethodName == nameof(VectorFunctions.Distance)
        && call.ArgumentCount == 2)
      return QueryMaker.NamedOp(NamedOpType.VectorDistance, typeof(double), call.Argument(0), call.Argument(1));

    // FullText.Matches(row, query, kind, distance) and Rank(…): the row, the words, and what the words mean.
    if (call.DeclaringType == typeof(FullText) && call.ArgumentCount == 4)
      return QueryMaker.NamedOp(call.MethodName == nameof(FullText.Rank) ? NamedOpType.TextRank : NamedOpType.TextMatch,
        type, call.Argument(0), call.Argument(1), call.Argument(2), call.Argument(3));

    // tags.Contains(x) over a JSON array of scalars is tags.Any(t => t == x), the elements asked as rows. A collection
    // the caller brought is IN and stays as written.
    if (call.MethodName == nameof(Enumerable.Contains) && call.Receiver is MemberAccessNode or NamedOpNode
        && call.ArgumentCount == (call.HasInstance ? 1 : 2)
        && call.Receiver.DataType is { } arrayType && JsonTranslationHelper.GetJsonElementType(arrayType) is { } element
        && element.IsScalarType()) {
      var item = call.Argument(call.HasInstance ? 0 : 1);
      return call.Receiver.Any(t => t.Equal(item));
    }

    // list.Contains(x), the instance method of a collection the caller brought, is Enumerable.Contains(list, x), so
    // that one name is what everything downstream reads.
    if (call.HasInstance && call.MethodName == nameof(Enumerable.Contains) && call.ArgumentCount == 1
        && call.Receiver is ConstantNode or QueryParameterNode && call.Receiver.Flags.HasFlag(NodeFlags.List))
      return new MethodCallNode(nameof(Enumerable.Contains), dataType: typeof(bool), source: call.Source) {
        DeclaringType = typeof(Enumerable), ReturnType = typeof(bool), Children = { call.Receiver, call.Argument(0) },
      };

    if (call.DeclaringType != typeof(Math))
      return null;
    // Rounding to a number of places is its own kind, since a dialect's table holds one form per kind.
    if (call.MethodName == nameof(Math.Round) && call.ArgumentCount == 2)
      return QueryMaker.NamedOp(NamedOpType.RoundTo, type, call.Argument(0), call.Argument(1));
    if (call.MethodName == nameof(Math.Pow) && call.ArgumentCount == 2)
      return QueryMaker.NamedOp(NamedOpType.Power, type, call.Argument(0), call.Argument(1));
    if (call.ArgumentCount != 1)
      return null;
    var op = call.MethodName switch {
      nameof(Math.Abs) => NamedOpType.Abs,
      nameof(Math.Round) => NamedOpType.Round,
      nameof(Math.Floor) => NamedOpType.Floor,
      nameof(Math.Ceiling) => NamedOpType.Ceiling,
      nameof(Math.Sqrt) => NamedOpType.Sqrt,
      nameof(Math.Sign) => NamedOpType.Sign,
      _ => NamedOpType.None,
    };
    return op == NamedOpType.None ? null : QueryMaker.NamedOp(op, type, call.Argument(0));
  }

  private static QueryNode? ConvertStringCall(MethodCallNode call, QueryNode text) {
    var count = call.ArgumentCount;
    switch (call.MethodName) {
      case nameof(string.ToUpper) when count == 0:
        return QueryMaker.NamedOp(NamedOpType.Upper, typeof(string), text);
      case nameof(string.ToLower) when count == 0:
        return QueryMaker.NamedOp(NamedOpType.Lower, typeof(string), text);
      case nameof(string.Trim) when count == 0:
        return QueryMaker.NamedOp(NamedOpType.Trim, typeof(string), text);
      case nameof(string.Replace) when count == 2:
        return QueryMaker.NamedOp(NamedOpType.Replace, typeof(string), text, call.Argument(0), call.Argument(1));

      // SQL counts from one and says 0 for absent; C# counts from zero and says -1. One subtraction reconciles both.
      case nameof(string.IndexOf) when count == 1:
        return TranslationHelper.MakeBinary(ExpressionType.Subtract,
          QueryMaker.NamedOp(NamedOpType.IndexOf, typeof(int), text, call.Argument(0)), QueryMaker.Constant(1));

      // C# counts from zero and SQL from one; the one is added here.
      case nameof(string.Substring) when count is 1 or 2: {
        var start = TranslationHelper.MakeBinary(ExpressionType.Add, call.Argument(0), QueryMaker.Constant(1));
        var length = count == 2 ? call.Argument(1) : QueryMaker.NamedOp(NamedOpType.Length, typeof(int), text);
        return QueryMaker.NamedOp(NamedOpType.Substring, typeof(string), text, start, length);
      }

      case nameof(string.StartsWith) when count == 1:
        return ConvertMatch(text, call.Argument(0), NamedOpType.StartsWith);
      case nameof(string.EndsWith) when count == 1:
        return ConvertMatch(text, call.Argument(0), NamedOpType.EndsWith);
      case nameof(string.Contains) when count == 1:
        return ConvertMatch(text, call.Argument(0), NamedOpType.Contains);

      default:
        return null;
    }
  }

  /// <summary>Converts a wildcard match over the raw term; the pattern is built where the SQL is written.</summary>
  /// <remarks>The parameter is named after the match it makes as well as the value it reads, since the same variable
  /// used with two matches is two patterns once the wildcards go on, and parameters are deduplicated by name.</remarks>
  private static QueryNode ConvertMatch(QueryNode text, QueryNode term, NamedOpType match) {
    if (term is QueryParameterNode parameter)
      parameter.Name += "_" + match;
    return QueryMaker.NamedOp(match, typeof(bool), text, term);
  }

  // ----- the model preprocessing: keys where the caller wrote entities ---------------------------------------------

  /// <summary>Converts a node written in terms of entities into key columns: the foreign-key shortcut, entity
  /// equality as key comparison, a list of entities as a list of keys, and the keys of a join; returns any other
  /// node as it is.</summary>
  private static QueryNode ConvertEntityShape(QueryNode node) =>
    node switch {
      MemberAccessNode member => ShortcutForeignKey(member) ?? member,
      BinaryNode { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } comparison =>
        ExpandEntityComparison(comparison) ?? comparison,
      MethodCallNode { MethodName: nameof(Enumerable.Contains), ArgumentCount: 2 } contains =>
        ExpandEntityContains(contains) ?? contains,
      MethodCallNode { MethodName: nameof(Queryable.Join) or nameof(Queryable.GroupJoin), ArgumentCount: 5 } join =>
        ExpandEntityJoinKeys(join),
      _ => node,
    };

  /// <summary>Converts a key of a reference's target that the owner's foreign key already holds, such as
  /// <c>person.Address.Id</c>, as the owner's column, read instead of joined for; or returns <see langword="null"/>
  /// for any other member.</summary>
  private static QueryNode? ShortcutForeignKey(MemberAccessNode member) {
    if (member is not { ModelMember: { } key, Target: MemberAccessNode { ModelMember.ReferenceInfo: { } reference } through })
      return null;
    var at = IndexOfMember(reference.ToMembers, key);
    if (at < 0 || at >= reference.FromMembers.Count)
      return null;
    var held = reference.FromMembers[at];
    return new MemberAccessNode(held.Name, dataType: held.DataType, source: member.Source) {
      ModelMember = held, Children = { through.Target! },
    };
  }

  /// <summary>Converts a comparison of entities, such as <c>p.Category == cat</c> or <c>p.Category == null</c>, as
  /// key comparisons joined by <c>AND</c> for a composite key; or returns <see langword="null"/> where neither side
  /// is an entity.</summary>
  private static QueryNode? ExpandEntityComparison(BinaryNode comparison) {
    var left = comparison.Left;
    var right = comparison.Right;
    if (!IsEntityValued(left) && !IsEntityValued(right))
      return null;
    // The entity side first; a comparison is symmetric.
    if (!IsEntityValued(left))
      (left, right) = (right, left);
    var (columns, targetKey) = BuildKeyColumns(left);
    if (columns.Count == 0)
      GetKeyMembers(left.GetEntity()!, "compare");

    // Against null, every key column is null, one IsNull per column.
    var terms = new QueryNode[columns.Count];
    for (var i = 0; i < columns.Count; i++)
      terms[i] = IsNullConstant(right)
        ? QueryMaker.NamedOp(NamedOpType.IsNull, typeof(bool), columns[i])
        : TranslationHelper.MakeBinary(ExpressionType.Equal, columns[i], BuildKeyValue(right, targetKey[i], i));
    var equality = terms.Length == 1 ? terms[0] : QueryMaker.And(terms);
    // x <> y and NOT (x = y) are the same in SQL, nulls included, and NOT is the only form that works for a
    // composite key.
    return comparison.NodeType == ExpressionType.NotEqual ? QueryMaker.Not(equality) : equality;
  }

  /// <summary>Converts a list of entities asked whether it holds a row's reference, such as
  /// <c>categories.Contains(p.Category)</c>, as a list of keys read off each entity at run time against the key
  /// column; or returns <see langword="null"/> where the item is not an entity.</summary>
  private static QueryNode? ExpandEntityContains(MethodCallNode contains) {
    var item = contains.Argument(1);
    if (!IsEntityValued(item))
      return null;
    var (columns, targetKey) = BuildKeyColumns(item);
    if (columns.Count != 1)
      throw new NotSupportedException(
        $"'{item.GetEntity()!.Name}' has a composite key, and a list of entities can only be matched on a single one.");
    if (contains.Argument(0) is not QueryParameterNode list)
      throw new NotSupportedException(
        "A list of ENTITIES is matched by their keys, which are read off the caller's objects when the query runs — "
        + "so it has to be a parameter, not a constant.");
    var key = targetKey[0];
    var toEntities = GetValueGetter(list);
    var keys = QueryMaker.Parameter($"{list.Name}_{key.Name}", (key.DataType ?? typeof(object)).MakeArrayType(), callCtx => {
      if (toEntities(callCtx) is not System.Collections.IEnumerable items)
        return null;
      var keyValues = new List<object?>();
      foreach (var entity in items)
        keyValues.Add(entity == null ? null : callCtx.ReadMember(entity, key));
      return keyValues;
    });
    keys.ValueSource = list.ValueSource;
    keys.ArgIndex = list.ArgIndex;
    keys.Tier = QueryTier.PreSql;
    return new MethodCallNode(nameof(Enumerable.Contains), dataType: typeof(bool), source: contains.Source) {
      DeclaringType = typeof(Enumerable), ReturnType = typeof(bool), Children = { keys, columns[0] },
    };
  }

  /// <summary>Converts the two keys of a join where one is written as an entity, such as
  /// <c>on c equals p.Category</c>, into the key columns the entities are matched by, part by part inside a
  /// <c>new { … }</c>; leaves keys that name no entity as they are.</summary>
  /// <remarks>A reference is read as its owner's foreign-key columns, so nothing joins the target table to match
  /// it; a row is read as its primary key.</remarks>
  /// <exception cref="NotSupportedException">An entity in a key has no key of its own to join by.</exception>
  private static MethodCallNode ExpandEntityJoinKeys(MethodCallNode join) {
    if (join.Argument(2) is not LambdaNode outer || join.Argument(3) is not LambdaNode inner)
      return join;
    var outerParts = GetKeyParts(outer.Body);
    var innerParts = GetKeyParts(inner.Body);
    if (outerParts.Count != innerParts.Count || !outerParts.Concat(innerParts).Any(IsEntityValued))
      return join;
    var outerColumns = new List<QueryNode>();
    var innerColumns = new List<QueryNode>();
    for (var i = 0; i < outerParts.Count; i++) {
      // The side that is an entity gives the columns, and the other side is lined up with them.
      var isOuterEntity = IsEntityValued(outerParts[i]);
      var (entity, other) = isOuterEntity ? (outerParts[i], innerParts[i]) : (innerParts[i], outerParts[i]);
      if (!IsEntityValued(entity)) {
        outerColumns.Add(outerParts[i]);
        innerColumns.Add(innerParts[i]);
        continue;
      }
      var (columns, targetKey) = BuildKeyColumns(entity);
      if (columns.Count == 0) {
        if (entity is MemberAccessNode reference)
          throw new NotSupportedException(
            $"Reference '{reference.MemberName}' has no key behind it, so there is nothing to join on.");
        GetKeyMembers(entity.GetEntity()!, "join");
      }
      for (var k = 0; k < columns.Count; k++) {
        (isOuterEntity ? outerColumns : innerColumns).Add(columns[k]);
        (isOuterEntity ? innerColumns : outerColumns).Add(BuildKeyValue(other, targetKey[k], k));
      }
    }
    outer.Children[outer.ParameterCount] = MakeJoinKey(outerColumns);
    inner.Children[inner.ParameterCount] = MakeJoinKey(innerColumns);
    return join;
  }

  /// <summary>Gets the parts of a join key: the values of a <c>new { … }</c>, or the key itself.</summary>
  private static IReadOnlyList<QueryNode> GetKeyParts(QueryNode key) =>
    key is NewNode composite ? composite.Children : [key];

  /// <summary>Makes a join key of its columns: the one column, or a <c>new { … }</c> of several.</summary>
  private static QueryNode MakeJoinKey(List<QueryNode> columns) {
    if (columns.Count == 1)
      return columns[0];
    var key = new NewNode(slotNames: null);
    key.Children.AddRange(columns);
    return key;
  }

  /// <summary>Determines whether a node is a null constant, through the conversions the typed path wraps one in.
  /// </summary>
  private static bool IsNullConstant(QueryNode node) => node switch {
    ConstantNode { Value: null } => true,
    ExpressionNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, ChildCount: 1 } convert =>
      IsNullConstant(convert.Children[0]),
    _ => false,
  };

  /// <summary>Determines whether a node stands for a whole entity rather than a value: a row, or a reference member.
  /// </summary>
  private static bool IsEntityValued(QueryNode node) =>
    node is LambdaParameterNode { Entity: not null }
    || node is MemberAccessNode { ModelMember.Kind: LinqMemberKind.Reference };

  /// <summary>Builds the columns that identify an entity, and the members of the far entity they line up with: the
  /// owner's foreign-key columns for a reference, so that nothing joins the target table; the primary key for a
  /// row.</summary>
  private static (IReadOnlyList<QueryNode> Columns, IReadOnlyList<ILinqEntityMember> TargetKey) BuildKeyColumns(QueryNode node) {
    if (node is MemberAccessNode { ModelMember.ReferenceInfo: { } reference } through)
      return (MakeColumns(through.Target!, reference.FromMembers), reference.ToMembers);
    var key = node.GetEntity()!.PrimaryKeyMembers;
    return (MakeColumns(node, key), key);
  }

  private static QueryNode[] MakeColumns(QueryNode owner, IReadOnlyList<ILinqEntityMember> members) {
    var columns = new QueryNode[members.Count];
    for (var i = 0; i < members.Count; i++)
      columns[i] = new MemberAccessNode(members[i].Name, dataType: members[i].DataType) {
        ModelMember = members[i], Children = { owner },
      };
    return columns;
  }

  /// <summary>Builds what one key column is compared against: a null, another entity's column, or the key read out
  /// of the object the caller captured.</summary>
  private static QueryNode BuildKeyValue(QueryNode other, ILinqEntityMember keyMember, int at) {
    // Against another entity, two sets of columns are lined up.
    if (IsEntityValued(other)) {
      var (columns, targetKey) = BuildKeyColumns(other);
      var keyAt = IndexOfMember(targetKey, keyMember);
      if (keyAt < 0 || columns.Count != targetKey.Count)
        throw new NotSupportedException(
          $"'{other.GetEntity()!.Name}' and the other side are keyed differently, so they cannot be compared.");
      return columns[keyAt];
    }

    // Against a captured entity, the getter gets to the entity and the host reads the key member off it at run time.
    if (other is not QueryParameterNode captured)
      throw new NotSupportedException(
        $"An entity can be compared to null, to another entity, or to a value the caller holds — not to a '{other}'.");
    var toEntity = GetValueGetter(captured);
    var key = QueryMaker.Parameter($"{captured.Name}_{keyMember.Name}", keyMember.DataType ?? typeof(object),
      callCtx => toEntity(callCtx) is { } entity ? callCtx.ReadMember(entity, keyMember) : null);
    key.ValueSource = captured.ValueSource;
    key.ArgIndex = captured.ArgIndex;
    key.Tier = QueryTier.PreSql;
    return key;
  }

  /// <summary>Gets the getter a key parameter derived from an entity parameter reads the entity through.</summary>
  private static QueryValueGetter GetValueGetter(QueryParameterNode parameter) =>
    parameter.ValueGetter ?? throw new InvalidOperationException(
      $"Parameter '{parameter.Name}' has no getter: the tree's parameters were not laid out before translation.");

  private static int IndexOfMember(IReadOnlyList<ILinqEntityMember> members, ILinqEntityMember wanted) {
    for (var i = 0; i < members.Count; i++)
      if (members[i].Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase))
        return i;
    return -1;
  }

  /// <summary>Converts a CLR member of a value, such as <c>name.Length</c>, <c>date.Year</c> or
  /// <c>when.HasValue</c>, into the operation, the null test or the value it spells; or returns
  /// <see langword="null"/> where it spells none.</summary>
  /// <summary>Makes the count of a collection's items as the <c>Count()</c> call over it, which the engine
  /// translates, where the collection is a member that holds values or a path into a JSON document.</summary>
  /// <returns>The call, or <see langword="null"/> for any other owner: a list of entities, a local collection.
  /// </returns>
  private static QueryNode? MakeItemCount(QueryNode owner, Expression? source) {
    var holdsValues = owner is MemberAccessNode { ModelMember.Kind: LinqMemberKind.Value }
      or NamedOpNode { OpType: NamedOpType.JsonValue };
    if (!holdsValues || owner.DataType is not { } type || JsonTranslationHelper.GetJsonElementType(type) == null)
      return null;
    return new MethodCallNode(nameof(Enumerable.Count), dataType: typeof(int), source: source) {
      DeclaringType = typeof(Enumerable), ReturnType = typeof(int), Children = { owner },
    };
  }

  private static QueryNode? ConvertClrMember(MemberAccessNode member) {
    var owner = member.Target;
    if (owner == null)
      return null;
    var declaring = member.ClrMember!.DeclaringType;

    // The Count of a list of values is the count of its items.
    if (member.MemberName == nameof(List<object>.Count) && MakeItemCount(owner, member.Source) is { } itemCount)
      return itemCount;

    // A property of a JSON document is a path into it, under the property's JSON name; a hop further down extends
    // the path.
    if (owner is MemberAccessNode { ModelMember: { Kind: LinqMemberKind.Value, DataType: { } ownerType } }
        && !ownerType.IsScalarType())
      return QueryMaker.JsonValue(owner, "$." + JsonTranslationHelper.GetJsonName(member.ClrMember), member.DataType);
    if (owner is NamedOpNode { OpType: NamedOpType.JsonValue, Children: [var document, ConstantNode { Value: string path }] })
      return QueryMaker.JsonValue(document, path + "." + JsonTranslationHelper.GetJsonName(member.ClrMember), member.DataType);

    // The database has no nullable: .Value is the column, and .HasValue is a null test.
    if (declaring != null && Nullable.GetUnderlyingType(declaring) != null)
      return member.MemberName switch {
        "Value" => owner,
        "HasValue" => QueryMaker.NamedOp(NamedOpType.IsNotNull, typeof(bool), owner),
        _ => null,
      };

    if (declaring == typeof(string) && member.MemberName == nameof(string.Length))
      return QueryMaker.NamedOp(NamedOpType.Length, typeof(int), owner);

    if (declaring == typeof(DateTime) || declaring == typeof(DateOnly))
      return member.MemberName switch {
        nameof(DateTime.Year) => QueryMaker.NamedOp(NamedOpType.Year, typeof(int), owner),
        nameof(DateTime.Month) => QueryMaker.NamedOp(NamedOpType.Month, typeof(int), owner),
        nameof(DateTime.Day) => QueryMaker.NamedOp(NamedOpType.Day, typeof(int), owner),
        nameof(DateTime.Date) => QueryMaker.NamedOp(NamedOpType.Date, typeof(DateOnly), owner),
        _ => null,
      };

    return null;
  }
}
