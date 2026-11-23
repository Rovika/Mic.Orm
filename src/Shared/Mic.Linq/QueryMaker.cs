using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Linq;

/// <summary>Builds a query by name against the model, as the unbound tree the expression converter builds from a typed
/// query.</summary>
/// <remarks>Nothing is bound here: which member of which entity a name is, a projection's shape and the vendor's
/// spelling are decided when the tree is translated, alike for a query however it was written. A row is passed to a
/// callback as one parameter node, and every use of it inside the callback is that same object. A bare value is
/// written as <see cref="Constant(object, Type)"/> or <see cref="Parameter(string, Type)"/>; there is no overload
/// that takes one as an object.</remarks>
public static class QueryMaker {

  // ----- sources ------------------------------------------------------------------------------------------------------

  /// <summary>Returns a source of all the records of an entity.</summary>
  /// <param name="entity">The entity.</param>
  /// <returns>A node that names the entity's records.</returns>
  public static EntitySetNode From(ILinqEntity entity) => new(entity);

  /// <summary>Returns the rows a function of the server returns, called with arguments, to select from like a
  /// table.</summary>
  /// <param name="table">The table of the function's rows, whose name is the function's.</param>
  /// <param name="arguments">The arguments, in the function's parameter order: parameters, constants or values
  /// read off an outer row.</param>
  /// <returns>A rowset node.</returns>
  public static TableFunctionNode TableFunction(ILinqTable table, params QueryNode[] arguments) {
    ArgumentNullException.ThrowIfNull(table);
    return new TableFunctionNode(table, arguments);
  }

  /// <summary>Returns a source of all the records of the entity with a name.</summary>
  /// <param name="model">The model that declares the entity.</param>
  /// <param name="entityName">The name of the entity.</param>
  /// <returns>A node that names the entity's records.</returns>
  public static EntitySetNode From(ILinqDataModel model, string entityName) {
    ArgumentNullException.ThrowIfNull(model);
    return From(model.FindEntity(entityName)
      ?? throw new KeyNotFoundException($"Model '{model.Name}' has no entity named '{entityName}'."));
  }

  /// <summary>Returns the template of a condition over a row of an entity, such as the filter an extension attaches:
  /// the condition built once over a placeholder row, to be instantiated over the row of whatever reads the entity.
  /// </summary>
  /// <param name="entity">The entity the condition is over.</param>
  /// <param name="build">A function that, given the placeholder row, builds the condition. Called once.</param>
  /// <returns>A template whose one placeholder, <see cref="QueryTreeTemplate.RowPlaceholder"/>, is the row.</returns>
  public static QueryTreeTemplate MakeFilter(ILinqEntity entity, Func<RowNode, QueryNode> build) {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(build);
    var row = From(entity).Row;
    return new QueryTreeTemplate(build(row), new Dictionary<string, QueryNode> { [QueryTreeTemplate.RowPlaceholder] = row });
  }

  /// <summary>Returns a source whose rows are the value of a parameter.</summary>
  /// <param name="parameter">The parameter whose value is a list of rows, each an array of values in column order.
  /// </param>
  /// <param name="columns">An array that contains the model members the columns are typed after.</param>
  /// <returns>A node that names the parameter's rows.</returns>
  public static JsonRowsNode Rows(QueryParameterNode parameter, params ILinqEntityMember[] columns) =>
    new(parameter, columns);

  /// <summary>Returns a source whose rows are the elements of a JSON array read off a row, with the element type's
  /// scalar properties as columns under their JSON names.</summary>
  /// <param name="document">The JSON column, or a path into one, that holds the array.</param>
  /// <param name="elementType">The type of the array's elements.</param>
  /// <returns>A node that names the array's rows.</returns>
  public static JsonRowsNode JsonRows(this QueryNode document, Type elementType) {
    ArgumentNullException.ThrowIfNull(document);
    return new JsonRowsNode(document, elementType) {
      IsScalar = elementType.IsScalarType(),
    };
  }

  /// <summary>Names the members of the query's rows to load together with the rows.</summary>
  /// <param name="query">The query.</param>
  /// <param name="paths">An array that contains the member paths of the query's result entity, such as
  /// <c>"Category"</c> or <c>"Category.Products"</c>: a reference, a list, a chain of them, or a member the host
  /// loads on demand, alone or at the end of a chain.</param>
  /// <returns>A node that names the query and its includes.</returns>
  public static QueryNode Include(this QueryNode query, params string[] paths) {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(paths);
    return new IncludeNode(query, paths.Select(path => new QueryInclude(path)).ToArray());
  }

  /// <summary>Returns a source whose rows are the elements of a JSON array read off a row, with the columns declared
  /// by name and type.</summary>
  /// <param name="document">The JSON column, or a path into one, that holds the array.</param>
  /// <param name="columns">An array that contains the name and type of each column.</param>
  /// <returns>A node that names the array's rows.</returns>
  public static JsonRowsNode JsonRows(this QueryNode document, params (string Name, Type DataType)[] columns) {
    ArgumentNullException.ThrowIfNull(document);
    return new JsonRowsNode(document, MakeSpecs(columns));
  }

  /// <summary>Returns a source whose rows are the value of the parameter with a name, with the columns declared by
  /// name and type.</summary>
  /// <param name="parameterName">The name of the parameter, whose value the caller passes as the next argument.</param>
  /// <param name="columns">An array that contains the name and type of each column.</param>
  /// <returns>A node that names the parameter's rows.</returns>
  public static JsonRowsNode Rows(string parameterName, params (string Name, Type DataType)[] columns) {
    return new JsonRowsNode(Parameter(parameterName, typeof(object)), MakeSpecs(columns));
  }

  private static LinqMemberSpec[] MakeSpecs((string Name, Type DataType)[] columns) {
    ArgumentNullException.ThrowIfNull(columns);
    var specs = new LinqMemberSpec[columns.Length];
    for (var i = 0; i < specs.Length; i++) {
      var (name, type) = columns[i];
      specs[i] = new LinqMemberSpec(name, type, Nullable.GetUnderlyingType(type) != null || !type.IsValueType);
    }
    return specs;
  }

  // ----- values -------------------------------------------------------------------------------------------------------

  /// <summary>Returns a literal, which is part of the query's shape and is written into its text and its cache key.
  /// </summary>
  /// <param name="value">The value of the literal.</param>
  /// <param name="dataType">The type of the literal, or <see langword="null"/> to take the value's own type.</param>
  /// <returns>A constant node; a list constant for a collection value, carrying the element type.</returns>
  /// <remarks>A constant is for a value that is part of the query's shape, such as a status a filter is defined by.
  /// A value that varies from call to call, or that a user typed, is a <see cref="Parameter(string, Type)"/>;
  /// the difference decides whether the query cache holds one entry for the query or one per distinct value. A
  /// string is a value, not a collection of characters.</remarks>
  public static QueryNode Constant(object? value, Type? dataType = null) {
    var type = dataType ?? value?.GetType();
    return GetElementType(type) is { } element
      ? new ConstantNode(value, Nullable.GetUnderlyingType(element) ?? element) { Flags = NodeFlags.List }
      : new ConstantNode(value, type);
  }

  /// <summary>Returns a parameter whose value the caller passes, in order, when the query runs.</summary>
  /// <param name="name">The name of the parameter.</param>
  /// <param name="dataType">The type of the value. A nullable type is unwrapped; a collection type makes a list
  /// parameter carrying the element type.</param>
  /// <returns>A parameter node.</returns>
  public static QueryParameterNode Parameter(string name, Type dataType) => MakeParameter(name, dataType);

  /// <summary>Returns a parameter whose value is read from the run when the query runs.</summary>
  /// <param name="name">The name of the parameter.</param>
  /// <param name="dataType">The type of the value. A nullable type is unwrapped; a collection type makes a list
  /// parameter carrying the element type.</param>
  /// <param name="valueGetter">A function that reads the value from the run, such as a tenant id or the current user
  /// from its operation context.</param>
  /// <returns>A parameter node.</returns>
  public static QueryParameterNode Parameter(string name, Type dataType, QueryValueGetter valueGetter) {
    ArgumentNullException.ThrowIfNull(valueGetter);
    var node = MakeParameter(name, dataType);
    node.ValueSource = ValueSourceKind.Context;
    node.ValueGetter = valueGetter;
    return node;
  }

  /// <summary>Returns a parameter that carries its value, as a captured local does in a typed query.</summary>
  /// <typeparam name="T">The type of the value. A collection type makes a list parameter.</typeparam>
  /// <param name="name">The name of the parameter.</param>
  /// <param name="value">The value. An entity, or a list of entities, is compared by key.</param>
  /// <returns>A parameter node bound to the value.</returns>
  /// <remarks>The value is no part of the query's shape: a thousand values are one statement and one cache entry,
  /// unlike a <see cref="Constant(object, Type)"/>.</remarks>
  public static QueryParameterNode Parameter<T>(string name, T value) {
    var node = MakeParameter(name, typeof(T));
    node.SetArgValue(value);
    return node;
  }

  /// <summary>Returns the parameter a bare value becomes where a node is wanted, named by the tree.</summary>
  internal static QueryParameterNode MakeValue(Type dataType, object? value) {
    var node = MakeParameter("#", dataType);
    node.SetArgValue(value);
    node.IsAutoNamed = true;
    return node;
  }

  /// <summary>Makes an argument parameter, unwrapping a nullable type and marking a collection type as a list of its
  /// element type.</summary>
  private static QueryParameterNode MakeParameter(string name, Type dataType) {
    ArgumentNullException.ThrowIfNull(dataType);
    var element = GetElementType(dataType);
    var type = element ?? dataType;
    var node = new QueryParameterNode(name, Nullable.GetUnderlyingType(type) ?? type);
    if (element != null)
      node.Flags |= NodeFlags.List;
    return node;
  }

  /// <summary>Returns a member of a row by name, or of a reference reached from one, such as
  /// <c>row.Member("Shop").Member("Name")</c>.</summary>
  /// <param name="source">The row, or a reference reached from one.</param>
  /// <param name="name">The name of the member.</param>
  /// <returns>A member access node, bound when the tree is translated.</returns>
  public static QueryNode Member(this QueryNode source, string name) {
    ArgumentNullException.ThrowIfNull(source);
    return new MemberAccessNode(name) { Children = { source } };
  }

  /// <summary>Returns a member of a row, or of a reference reached from one.</summary>
  /// <param name="source">The row, or a reference reached from one.</param>
  /// <param name="member">The model member.</param>
  /// <returns>A member access node, bound when the tree is translated.</returns>
  public static QueryNode Member(this QueryNode source, ILinqEntityMember member) {
    ArgumentNullException.ThrowIfNull(member);
    return source.Member(member.Name);
  }

  /// <summary>Returns the value read out of a JSON document by path.</summary>
  /// <param name="document">The JSON document, or a member holding one.</param>
  /// <param name="path">The path of the value, such as <c>$.a.b</c>.</param>
  /// <param name="dataType">The type the server casts the value to, or <see langword="null"/> to read it as text.
  /// </param>
  /// <returns>A node whose value is the value at the path, or <see langword="null"/> if the path names nothing.
  /// </returns>
  public static QueryNode JsonValue(this QueryNode document, string path, Type? dataType = null) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentException.ThrowIfNullOrEmpty(path);
    return NamedOp(NamedOpType.JsonValue, dataType ?? typeof(string), document, Constant(path));
  }

  /// <summary>Returns the distance in metres between two geographic points along the Earth's surface.</summary>
  /// <param name="point">The first point: a member, a parameter or a constant.</param>
  /// <param name="other">The second point.</param>
  /// <returns>A node whose value is the distance.</returns>
  public static QueryNode GeoDistance(this QueryNode point, QueryNode other) {
    ArgumentNullException.ThrowIfNull(point);
    ArgumentNullException.ThrowIfNull(other);
    return NamedOp(NamedOpType.GeoDistance, typeof(double), point, other);
  }

  /// <summary>Returns the distance between two vectors, by the metric the vector member among them declares.
  /// </summary>
  /// <param name="vector">The first vector: a member, a parameter or a constant.</param>
  /// <param name="other">The second vector.</param>
  /// <returns>A node whose value is the distance, smaller for a nearer vector.</returns>
  public static QueryNode VectorDistance(this QueryNode vector, QueryNode other) {
    ArgumentNullException.ThrowIfNull(vector);
    ArgumentNullException.ThrowIfNull(other);
    return NamedOp(NamedOpType.VectorDistance, typeof(double), vector, other);
  }

  /// <summary>Returns a named operation over its arguments.</summary>
  /// <param name="opType">One of the enumeration values that specifies the operation.</param>
  /// <param name="dataType">The type the operation produces, which is the operation's own and not its argument's:
  /// the length of a string is an integer, as is the year of a date.</param>
  /// <param name="arguments">An array that contains the arguments, at least one.</param>
  /// <returns>A named operation node.</returns>
  public static QueryNode NamedOp(NamedOpType opType, Type dataType, params QueryNode[] arguments) {
    ArgumentNullException.ThrowIfNull(dataType);
    ArgumentNullException.ThrowIfNull(arguments);
    if (arguments.Length == 0)
      throw new ArgumentException($"{opType} needs at least one argument.", nameof(arguments));
    return new NamedOpNode(opType, dataType, arguments);
  }


  /// <summary>Names an expression, for a projection or a grouping that returns it.</summary>
  /// <param name="node">The expression to name.</param>
  /// <param name="name">The name.</param>
  /// <returns>The same node, named.</returns>
  public static QueryNode As(this QueryNode node, string name) {
    ArgumentNullException.ThrowIfNull(node);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    node.Name = name;
    return node;
  }

  // ----- operators ----------------------------------------------------------------------------------------------------

  public static QueryNode Equal(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.Equal, left, right);
  public static QueryNode NotEqual(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.NotEqual, left, right);
  public static QueryNode Less(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.LessThan, left, right);
  public static QueryNode LessOrEqual(this QueryNode left, QueryNode right) =>
    MakeBinary(ExpressionType.LessThanOrEqual, left, right);
  public static QueryNode Greater(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.GreaterThan, left, right);
  public static QueryNode GreaterOrEqual(this QueryNode left, QueryNode right) =>
    MakeBinary(ExpressionType.GreaterThanOrEqual, left, right);

  public static QueryNode Add(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.Add, left, right);
  public static QueryNode Subtract(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.Subtract, left, right);
  public static QueryNode Multiply(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.Multiply, left, right);
  public static QueryNode Divide(this QueryNode left, QueryNode right) => MakeBinary(ExpressionType.Divide, left, right);

  public static QueryNode Not(this QueryNode operand) {
    ArgumentNullException.ThrowIfNull(operand);
    return new ExpressionNode(ExpressionType.Not, typeof(bool)) { Children = { operand } };
  }

  /// <summary>Returns the conjunction of the operands, written as nested pairs as a typed query writes
  /// <c>a &amp;&amp; b &amp;&amp; c</c>.</summary>
  /// <param name="operands">An array that contains the operands, at least two.</param>
  /// <returns>A node that is true when every operand is.</returns>
  public static QueryNode And(params QueryNode[] operands) => Fold(ExpressionType.AndAlso, operands);

  /// <summary>Returns the disjunction of the operands, written as nested pairs.</summary>
  /// <param name="operands">An array that contains the operands, at least two.</param>
  /// <returns>A node that is true when any operand is.</returns>
  public static QueryNode Or(params QueryNode[] operands) => Fold(ExpressionType.OrElse, operands);

  /// <summary>Determines whether a value is one of a collection, or one of the values another query returns, as
  /// <c>list.Contains(x)</c> does.</summary>
  /// <param name="value">The value.</param>
  /// <param name="list">The collection, or the query.</param>
  /// <returns>A node that is true when the collection holds the value.</returns>
  public static QueryNode In(this QueryNode value, QueryNode list) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(list);
    return MakeCall("Contains", typeof(Enumerable), typeof(bool), list, value);
  }

  /// <summary>Determines whether a list has any row, or any row that satisfies a condition.</summary>
  /// <param name="list">A list member of the row, or a query.</param>
  /// <param name="condition">A function that, given a row, returns the condition, or <see langword="null"/> to ask
  /// for any row at all.</param>
  /// <returns>A node that is true when the list has such a row.</returns>
  public static QueryNode Any(this QueryNode list, Func<QueryNode, QueryNode>? condition = null) {
    ArgumentNullException.ThrowIfNull(list);
    return condition == null
      ? MakeOperation("Any", typeof(bool), list)
      : MakeOperation("Any", typeof(bool), list, MakeLambda(condition));
  }

  // ----- set operations -----------------------------------------------------------------------------------------------

  /// <summary>Filters the rows of a query by a condition.</summary>
  /// <param name="source">The query.</param>
  /// <param name="condition">A function that, given a row, returns the condition.</param>
  /// <returns>A node that names the rows that satisfy the condition.</returns>
  public static QueryNode Where(this QueryNode source, Func<QueryNode, QueryNode> condition) =>
    MakeOperation("Where", source, MakeLambda(condition));

  /// <summary>Orders the rows of a query by a term. Called on an ordered query, it appends the term.</summary>
  /// <param name="source">The query.</param>
  /// <param name="term">A function that, given a row, returns the term to order by.</param>
  /// <param name="descending"><see langword="true"/> to order from the greatest term down; otherwise,
  /// <see langword="false"/>.</param>
  /// <param name="isDefault"><see langword="true"/> if the term is the model's default order rather than the caller's;
  /// otherwise, <see langword="false"/>. A later caller's term replaces a default order instead of appending to it.
  /// </param>
  /// <returns>A node that names the ordered rows.</returns>
  public static QueryNode OrderBy(this QueryNode source, Func<QueryNode, QueryNode> term, bool descending = false,
      bool isDefault = false) {
    var ordered = source is MethodCallNode { MethodName: "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" } prior
      && (isDefault || !prior.Flags.HasFlag(NodeFlags.DefaultOrder));
    var name = (ordered ? "ThenBy" : "OrderBy") + (descending ? "Descending" : "");
    var call = MakeOperation(name, source, MakeLambda(term));
    if (isDefault)
      call.Flags |= NodeFlags.DefaultOrder;
    return call;
  }

  /// <summary>Projects each row of a query into the named slots of a projection.</summary>
  /// <param name="source">The query.</param>
  /// <param name="projection">A function that, given a row, returns the slots. A member keeps its own name; any other
  /// expression is named with <see cref="As"/>.</param>
  /// <returns>A node that names the projected rows.</returns>
  public static QueryNode Select(this QueryNode source, Func<QueryNode, QueryNode[]> projection) =>
    MakeOperation("Select", source, MakeLambda(row => MakeNew(projection(row))));

  /// <summary>Groups the rows of a query by keys. The keys are the result until a <see cref="Select"/> adds
  /// aggregates to them.</summary>
  /// <param name="source">The query.</param>
  /// <param name="keys">A function that, given a row, returns the keys.</param>
  /// <returns>A node that names the groups.</returns>
  public static QueryNode GroupBy(this QueryNode source, Func<QueryNode, QueryNode[]> keys) =>
    MakeOperation("GroupBy", source, MakeLambda(row => {
      var made = keys(row);
      ArgumentNullException.ThrowIfNull(made);
      // One key is the key itself; several are a composite named by their slots, as the typed path produces them.
      return made.Length == 1 && string.IsNullOrEmpty(made[0].Name) ? made[0] : MakeNew(made);
    }));

  /// <summary>Joins another query in on a condition and projects each pair of rows.</summary>
  /// <param name="source">The outer query.</param>
  /// <param name="inner">The inner query.</param>
  /// <param name="on">A function that, given the outer row and the inner one, returns the condition they are joined
  /// on.</param>
  /// <param name="select">A function that, given the same two rows, returns the slots of a projection, or one of the
  /// two rows to return whole; or <see langword="null"/> to return the outer rows.</param>
  /// <param name="outer"><see langword="true"/> to keep an outer row that matched nothing, with the inner side
  /// <see langword="null"/>, as a left outer join; otherwise, <see langword="false"/>.</param>
  /// <returns>A node that names the joined rows.</returns>
  public static QueryNode Join(this QueryNode source, QueryNode inner,
      Func<QueryNode, QueryNode, QueryNode> on, Func<QueryNode, QueryNode, QueryNode[]>? select = null,
      bool outer = false) {
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(on);
    var node = select == null
      ? MakeOperation("Join", source, inner, MakeLambda2(on))
      : MakeOperation("Join", source, inner, MakeLambda2(on),
          MakeLambda2((o, i) => { var slots = select(o, i); return slots.Length == 1 ? slots[0] : MakeNew(slots); }));
    if (outer)
      node.Flags |= NodeFlags.Outer;
    return node;
  }

  /// <summary>Returns the rows of a list for every row of a query, one result row per pair.</summary>
  /// <param name="source">The query.</param>
  /// <param name="list">A function that, given a row, returns the list.</param>
  /// <returns>A node that names the list's rows.</returns>
  public static QueryNode SelectMany(this QueryNode source, Func<QueryNode, QueryNode> list) =>
    MakeOperation("SelectMany", source, MakeLambda(list));

  /// <summary>Returns the rows of a list for every row of a query and projects each pair, as
  /// <c>from o in owners from x in o.Orders select new { … }</c> does.</summary>
  /// <param name="source">The query.</param>
  /// <param name="list">A function that, given a row, returns the list.</param>
  /// <param name="select">A function that, given the query's row and the list's, returns the slots of a projection,
  /// or one of the two rows to return whole.</param>
  /// <returns>A node that names the projected pairs.</returns>
  public static QueryNode SelectMany(this QueryNode source, Func<QueryNode, QueryNode> list,
      Func<QueryNode, QueryNode, QueryNode[]> select) {
    ArgumentNullException.ThrowIfNull(select);
    return MakeOperation("SelectMany", source, MakeLambda(list),
      MakeLambda2((row, item) => { var slots = select(row, item); return slots.Length == 1 ? slots[0] : MakeNew(slots); }));
  }

  public static QueryNode Union(this QueryNode left, QueryNode right) => MakeOperation("Union", left, right);
  public static QueryNode Concat(this QueryNode left, QueryNode right) => MakeOperation("Concat", left, right);
  public static QueryNode Intersect(this QueryNode left, QueryNode right) => MakeOperation("Intersect", left, right);
  public static QueryNode Except(this QueryNode left, QueryNode right) => MakeOperation("Except", left, right);

  /// <summary>Skips a number of rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="count">The number of rows to skip, usually a parameter such as
  /// <c>Parameter("skip", typeof(int))</c>.</param>
  /// <returns>A node that names the remaining rows.</returns>
  public static QueryNode Skip(this QueryNode source, QueryNode count) => MakeOperation("Skip", source, count);

  /// <summary>Takes a number of rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="count">The number of rows to take.</param>
  /// <returns>A node that names the taken rows.</returns>
  public static QueryNode Take(this QueryNode source, QueryNode count) => MakeOperation("Take", source, count);

  /// <summary>Removes the duplicate rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <returns>A node that names the distinct rows.</returns>
  public static QueryNode Distinct(this QueryNode source) => MakeOperation("Distinct", source);

  // ----- common table expressions --------------------------------------------------------------------------------------

  /// <summary>Names a query so that the statement defines it once in its <c>WITH</c> list however often it is read.
  /// </summary>
  /// <param name="source">The query to name.</param>
  /// <param name="name">The name of the common table expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the common table expression is written.
  /// </param>
  /// <returns>A node whose rows are the rows of <paramref name="source"/>.</returns>
  public static QueryNode AsCte(this QueryNode source, string name, CteKind kind = CteKind.Default) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    return MakeCall(nameof(CteQueryableExtensions.AsCte), typeof(CteQueryableExtensions), typeof(IQueryable),
      source, Constant(name), Constant(kind));
  }

  /// <summary>Writes a recursive query: the anchor's rows, and then repeatedly the rows <paramref name="next"/>
  /// reaches from the rows found so far.</summary>
  /// <param name="anchor">The query whose rows start the recursion.</param>
  /// <param name="name">The name of the common table expression.</param>
  /// <param name="next">A function that, given the rows found so far, returns the rows that come next.</param>
  /// <param name="maxDepth">The greatest number of steps from the anchor.</param>
  /// <param name="select">A function that, given a row and the level it was found at, returns the slots to answer
  /// with; or <see langword="null"/> to answer with the rows themselves.</param>
  /// <returns>A node that names the anchor's rows and every row reached from them.</returns>
  public static QueryNode AsRecursiveCte(this QueryNode anchor, string name, Func<QueryNode, QueryNode> next,
      int maxDepth = 100, Func<QueryNode, QueryNode, QueryNode[]>? select = null) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    var arguments = new List<QueryNode> { anchor, Constant(name), MakeLambda(next) };
    if (select != null)
      arguments.Add(MakeLambda2((row, level) => MakeNew(select(row, level))));
    arguments.Add(Constant(maxDepth));
    return MakeCall(nameof(CteQueryableExtensions.AsRecursiveCte), typeof(CteQueryableExtensions), typeof(IQueryable),
      arguments.ToArray());
  }

  // ----- full-text search ------------------------------------------------------------------------------------------------

  /// <summary>Determines whether a row matches a full-text query over its entity's full-text index, as
  /// <see cref="FullText.Matches{T}"/> does.</summary>
  /// <param name="row">The row to match.</param>
  /// <param name="query">The words to match, as a parameter or a constant.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">For <see cref="TextMatchKind.Near"/>, the greatest distance between the words.</param>
  /// <returns>A node that is true when the row matches.</returns>
  public static QueryNode TextMatches(this QueryNode row, QueryNode query, TextMatchKind kind = TextMatchKind.AllWords,
      int distance = 0) =>
    NamedOp(NamedOpType.TextMatch, typeof(bool), row, query, Constant(kind), Constant(distance));

  /// <summary>Returns how well a row matches a full-text query, as <see cref="FullText.Rank{T}"/> does.</summary>
  /// <param name="row">The row to rank.</param>
  /// <param name="query">The words to match, as a parameter or a constant.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">For <see cref="TextMatchKind.Near"/>, the greatest distance between the words.</param>
  /// <returns>A node whose value is higher for a better match.</returns>
  public static QueryNode TextRank(this QueryNode row, QueryNode query, TextMatchKind kind = TextMatchKind.AllWords,
      int distance = 0) =>
    NamedOp(NamedOpType.TextRank, typeof(double), row, query, Constant(kind), Constant(distance));

  // ----- mutations ------------------------------------------------------------------------------------------------------

  /// <summary>Deletes the rows the query finds, which are rows of the entity the query is written over.</summary>
  /// <param name="source">The query that names the rows to delete.</param>
  /// <returns>A node that marks the mutation, for <see cref="MutationQueryableExtensions"/> to run.</returns>
  /// <remarks>What a mutation does and does not do is described on <see cref="MutationQueryableExtensions"/> and
  /// holds here alike.</remarks>
  public static QueryNode Delete(this QueryNode source) =>
    MakeCall(nameof(MutationQueryableExtensions.Delete), typeof(MutationQueryableExtensions), typeof(int), source);

  /// <summary>Deletes the rows of an entity whose key the query brings.</summary>
  /// <param name="source">A query over any entity that projects the target's key: one value for a single-column key,
  /// or slots named for the key's members.</param>
  /// <param name="target">The entity whose rows are deleted.</param>
  /// <returns>A node that marks the mutation.</returns>
  public static QueryNode Delete(this QueryNode source, ILinqEntity target) =>
    MakeMutation(nameof(MutationQueryableExtensions.DeleteByKey), source, target);

  /// <summary>Updates the rows of an entity with the values the query brings.</summary>
  /// <param name="source">A query that projects slots named for the members they write. A query not over the
  /// target's own rows also projects the key's members, which name the rows.</param>
  /// <param name="target">The entity whose rows are updated.</param>
  /// <returns>A node that marks the mutation.</returns>
  public static QueryNode Update(this QueryNode source, ILinqEntity target) =>
    MakeMutation(nameof(MutationQueryableExtensions.Update), source, target);

  /// <summary>Inserts the rows the query brings into an entity.</summary>
  /// <param name="source">A query that projects slots named for the target's members; a reference is written by a
  /// row of the query.</param>
  /// <param name="target">The entity the rows are inserted into.</param>
  /// <returns>A node that marks the mutation.</returns>
  public static QueryNode Insert(this QueryNode source, ILinqEntity target) =>
    MakeMutation(nameof(MutationQueryableExtensions.Insert), source, target);

  private static MethodCallNode MakeMutation(string marker, QueryNode source, ILinqEntity target) {
    ArgumentNullException.ThrowIfNull(target);
    var call = MakeCall(marker, typeof(MutationQueryableExtensions), typeof(int), source);
    call.Entity = target;
    return call;
  }

  // ----- aggregates ---------------------------------------------------------------------------------------------------

  /// <summary>Returns the number of rows of a query as a 64-bit integer.</summary>
  /// <param name="source">The query.</param>
  /// <returns>A node whose value is the number of rows.</returns>
  public static QueryNode Count(this QueryNode source) => MakeOperation("LongCount", typeof(long), source);

  /// <summary>Returns the sum of the values a selector produces over the rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that, given a row, returns the value.</param>
  /// <returns>A node whose value is the sum.</returns>
  public static QueryNode Sum(this QueryNode source, Func<QueryNode, QueryNode> selector) =>
    MakeOperation("Sum", source, MakeLambda(selector));
  /// <summary>Returns the least value a selector produces over the rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that, given a row, returns the value.</param>
  /// <returns>A node whose value is the least value.</returns>
  public static QueryNode Min(this QueryNode source, Func<QueryNode, QueryNode> selector) =>
    MakeOperation("Min", source, MakeLambda(selector));
  /// <summary>Returns the greatest value a selector produces over the rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that, given a row, returns the value.</param>
  /// <returns>A node whose value is the greatest value.</returns>
  public static QueryNode Max(this QueryNode source, Func<QueryNode, QueryNode> selector) =>
    MakeOperation("Max", source, MakeLambda(selector));
  /// <summary>Returns the average of the values a selector produces over the rows of a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that, given a row, returns the value.</param>
  /// <returns>A node whose value is the average.</returns>
  public static QueryNode Average(this QueryNode source, Func<QueryNode, QueryNode> selector) =>
    MakeOperation("Average", source, MakeLambda(selector));

  // ----- the pieces ---------------------------------------------------------------------------------------------------

  /// <summary>Makes a call node named for the operation, with the source first and the declaring type decided by the
  /// shape of the source.</summary>
  /// <remarks>A call over a member access, a parameter, a row, or a call that was itself over one of those is a call
  /// over a row's list and binds to <c>Enumerable</c>; any other is a query and binds to <c>Queryable</c>.</remarks>
  private static MethodCallNode MakeOperation(string name, params QueryNode[] arguments) =>
    MakeOperation(name, null, arguments);

  private static MethodCallNode MakeOperation(string name, Type? returnType, params QueryNode[] arguments) {
    ArgumentNullException.ThrowIfNull(arguments);
    var declaring = arguments.Length > 0 && IsRowSide(arguments[0]) ? typeof(Enumerable) : typeof(Queryable);
    return MakeCall(name, declaring, returnType, arguments);
  }

  private static bool IsRowSide(QueryNode receiver) =>
    receiver is MemberAccessNode or LambdaParameterNode
    || receiver is RowNode
    || receiver is MethodCallNode { DeclaringType: var declaring } && declaring == typeof(Enumerable);

  private static MethodCallNode MakeCall(string name, Type declaring, Type? returnType, params QueryNode[] arguments) {
    foreach (var argument in arguments)
      ArgumentNullException.ThrowIfNull(argument);
    var call = new MethodCallNode(name, dataType: returnType) { DeclaringType = declaring, ReturnType = returnType };
    call.Children.AddRange(arguments);
    return call;
  }

  /// <summary>Makes a lambda node of one parameter, handing the callback one parameter node so that every use of it
  /// is the same object.</summary>
  private static LambdaNode MakeLambda(Func<QueryNode, QueryNode> body) {
    ArgumentNullException.ThrowIfNull(body);
    var row = new LambdaParameterNode("row");
    var built = body(row) ?? throw new ArgumentException("A lambda's body cannot be null.", nameof(body));
    return new LambdaNode(1) { Children = { row, built } };
  }

  private static LambdaNode MakeLambda2(Func<QueryNode, QueryNode, QueryNode> body) {
    ArgumentNullException.ThrowIfNull(body);
    var outer = new LambdaParameterNode("outer");
    var inner = new LambdaParameterNode("inner");
    var built = body(outer, inner) ?? throw new ArgumentException("A lambda's body cannot be null.", nameof(body));
    return new LambdaNode(2) { Children = { outer, inner, built } };
  }

  /// <summary>Makes a projection node whose slots are named by each expression's own name.</summary>
  private static NewNode MakeNew(QueryNode[] slots) {
    ArgumentNullException.ThrowIfNull(slots);
    foreach (var slot in slots)
      ArgumentNullException.ThrowIfNull(slot);
    var node = new NewNode([.. slots.Select(s => string.IsNullOrEmpty(s.Name) ? null : s.Name)]);
    node.Children.AddRange(slots);
    return node;
  }

  private static QueryNode MakeBinary(ExpressionType type, QueryNode left, QueryNode right) =>
    TranslationHelper.MakeBinary(type, left, right);

  private static QueryNode Fold(ExpressionType type, QueryNode[] operands) {
    ArgumentNullException.ThrowIfNull(operands);
    if (operands.Length < 2)
      throw new ArgumentException($"{type} needs at least two operands.", nameof(operands));
    var folded = operands[0];
    for (var i = 1; i < operands.Length; i++)
      folded = MakeBinary(type, folded, operands[i]);
    return folded;
  }

  /// <summary>Gets the element type of a collection type, or <see langword="null"/> for a value type, a string or
  /// a byte array.</summary>
  /// <remarks>A byte array is one binary value, as a string is one text and not its characters.</remarks>
  private static Type? GetElementType(Type? type) {
    if (type == null || type == typeof(string) || type == typeof(byte[]))
      return null;
    if (type.IsArray)
      return type.GetElementType();
    foreach (var candidate in type.GetInterfaces().Append(type))
      if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        return candidate.GetGenericArguments()[0];
    return null;
  }
}
