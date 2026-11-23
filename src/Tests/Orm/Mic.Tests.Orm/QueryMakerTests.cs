using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db;
using BeautyShop;

namespace Mic.Tests.Orm;

// THE UNTYPED FRONT END BUILDS A NAKED TREE, and the binding makes it the logical one — the same two stages the typed
// path goes through, meeting at the same tree. Nothing runs here; what is checked is that `QueryMaker` decides
// nothing, that the binding decides everything, and that one walker covers the result.
//
// Against the Beauty Shop model rather than a hand-built one, because the point of an untyped builder is that names
// are resolved against a model that already exists — resolved by the BINDING, not by the builder.
//
// THE BINDING IS IMPORTED BY ALIAS, NOT BY NAMESPACE, on purpose: `Mic.Linq.ExpressionTree` also holds the logical
// builder, and importing it beside `Mic.Linq` would make every `.Where` and `.Member` here ambiguous, since both
// builders offer them. An alias brings in the one type and nothing else.
[TestClass]
public class QueryMakerTests {
  [TestMethod]
  public void Builder_MakesATreeByNameLoweringMakesItLogical() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    ILinqDataModel model = app.MapToPostgres();

    // A query with everything the first phase offers in it: a filter of three conditions, a REFERENCE WALKED THROUGH
    // (`row.Shop.Name`), a parameter, two constants, two orderings and a limit.
    var byName = QueryMaker.From(model, "Employee")
      .Where(row => QueryMaker.And(
        row.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))),
        row.Member("HomeShop").Member("Name").Equal(QueryMaker.Constant("Downtown")),
        row.Member("TerminatedOn").Equal(QueryMaker.Constant(null))))
      .OrderBy(row => row.Member("HiredOn"), descending: true)
      .OrderBy(row => row.Member("Id"))
      .Take(QueryMaker.Constant(20));

    // ----- what the BUILDER made: calls named after LINQ's own, lambdas, and members by NAME ----------------------------
    //
    // Nothing is bound. A member access carries a name and no model member; a call carries a name and the type it
    // stands in for; the entity set at the root is the one leaf that is already logical, because it IS its entity.
    var take = (MethodCallNode)byName;
    Assert.AreEqual("Take", take.Name);
    Assert.AreEqual(typeof(Queryable), take.DeclaringType);
    Assert.IsNull(take.Method, "built by name — there is no MethodInfo to carry");

    // A SECOND ORDERING IS SAID AS `ThenBy`, which is what appending means in LINQ's vocabulary and what the binding
    // accepts; a second `OrderBy` there would mean re-sorting, and is refused.
    var thenBy = (MethodCallNode)take.Argument(0);
    Assert.AreEqual("ThenBy", thenBy.Name);
    var orderBy = (MethodCallNode)thenBy.Argument(0);
    Assert.AreEqual("OrderByDescending", orderBy.Name);

    var where = (MethodCallNode)orderBy.Argument(0);
    Assert.AreEqual(NodeKind.EntitySet, where.Argument(0).Kind, "the root is already logical");
    var lambda = (LambdaNode)where.Argument(1);
    // `And` FOLDS LEFT — `AndAlso(AndAlso(c1, c2), c3)` — so the second condition is `[0][1]`.
    var shopName = (MemberAccessNode)((ExpressionNode)lambda.Body).Children[0].Children[1].Children[0];
    Assert.AreEqual("Name", shopName.Name);
    Assert.IsNull(shopName.ModelMember, "a name, not a model member — binding is the binding's");
    Assert.IsNull(shopName.DataType, "and nothing knows its type yet");

    // THE ROW IS ONE NODE: every use inside the callback is the same object, which is the identity the engine binds on.
    var rowMentions = new List<QueryNode>();
    lambda.ForEach(node => { if (ReferenceEquals(node, lambda.Children[0])) rowMentions.Add(node); });
    Assert.AreEqual(4, rowMentions.Count, "the parameter itself, and three member accesses on it");

    // ----- the one mistake a by-name API invites is found by the TRANSLATOR's resolution, not the builder -------------
    var employee = model.FindEntity("Employee")!;
    var misspelled = QueryMaker.From(employee).Where(row => row.Member("HourlyRateX").Equal(QueryMaker.Constant(1)));
    var refused = Assert.ThrowsExactly<NotSupportedException>(() => QueryTranslator.Translate(QueryTreeScanner.Scan(misspelled), misspelled, model));
    StringAssert.Contains(refused.Message, "HourlyRateX");

    // ----- a collection is a LIST, told by its type rather than by a second factory --------------------------------------
    var list = QueryMaker.Constant(new[] { 1, 2, 3 });
    Assert.IsTrue(list.Flags.HasFlag(NodeFlags.List));
    Assert.AreEqual(typeof(int), list.DataType, "the ELEMENT type is what the node carries");
    Assert.IsFalse(QueryMaker.Constant("text").Flags.HasFlag(NodeFlags.List), "a string is a value, not characters");
    Assert.IsTrue(QueryMaker.Parameter("ids", typeof(Guid[])).Flags.HasFlag(NodeFlags.List));
  }

  [TestMethod]
  public void Rewrite_ReplacesInTheParentVisitsASharedRowOnce() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    var employee = app.Model!.FindEntity("Employee")!;
    // Two member accesses over ONE lambda parameter: `row.Id` and `row.HourlyRate` share the parameter node, which
    // is the identity the translator binds a row on — so a transform offered it twice would hand each access a
    // different replacement.
    var query = QueryMaker.From(employee).Where(row => QueryMaker.And(
      row.Member("Id").Equal(QueryMaker.Constant(1)),
      row.Member("HourlyRate").Equal(QueryMaker.Constant(2))));
    var lambda = (LambdaNode)((MethodCallNode)query).Argument(1);

    var rowsOffered = 0;
    var replacementRow = new LambdaParameterNode("r");
    var constants = 0;
    var result = query.Transform(node => {
      if (node is LambdaParameterNode) { rowsOffered++; return replacementRow; }
      // A CONSTANT BECOMES A PARAMETER, which is a different class: the parent's slot is what changes.
      if (node is ConstantNode { Value: int n }) { constants++; return QueryMaker.Parameter("p" + n, typeof(int)); }
      return node;
    });
    Assert.AreSame(query, result, "the root was not replaced, so it comes back as itself");
    Assert.AreEqual(1, rowsOffered, "one parameter, three references, one visit");
    Assert.AreEqual(2, constants);
    Assert.AreSame(replacementRow, lambda.Children[0], "the lambda's own slot got it");
    foreach (var comparison in lambda.Body.Children) {
      Assert.AreSame(replacementRow, comparison.Children[0].Children[0], "every reference got the SAME replacement");
      Assert.IsInstanceOfType<QueryParameterNode>(comparison.Children[1]);
    }

    // TOP-DOWN WALKS THE REPLACEMENT: a node expanded into a subtree has that subtree's children transformed too.
    // BOTTOM-UP derives from transformed children: the And sees its comparisons already replaced.
    var seenUnderReplacement = false;
    QueryMaker.Constant(true).Transform(node => node is ConstantNode { Value: true }
      ? QueryMaker.Not(QueryMaker.Constant(false))
      : node is ConstantNode { Value: false } ? MarkSeen() : node);
    Assert.IsTrue(seenUnderReplacement);
    QueryNode MarkSeen() { seenUnderReplacement = true; return QueryMaker.Constant(false); }

    var replacedRoot = query.Transform(node => node == query ? QueryMaker.Constant(0) : node, bottomUp: true);
    Assert.IsInstanceOfType<ConstantNode>(replacedRoot, "a replaced root is the return value");
  }

  [TestMethod]
  public void CyclicTree_IsRefusedByName() {
    // A node under its own descendant — the mistake a builder makes once. Every recursion on `QueryNode` is guarded by
    // depth, and the guard has to be depth: the row node is shared on purpose, so "seen before" is not "cycle".
    var inner = new LogicalOpNode(LogicalOpType.Not);
    var loop = new LogicalOpNode(LogicalOpType.Not, inner);
    inner.Children.Add(loop);
    var refused = Assert.ThrowsExactly<InvalidOperationException>(() => loop.GetCacheKey());
    StringAssert.Contains(refused.Message, "cycle");
    Assert.ThrowsExactly<InvalidOperationException>(() => loop.ForEach(_ => { }));
    Assert.ThrowsExactly<InvalidOperationException>(() => loop.AsText);
  }
}
