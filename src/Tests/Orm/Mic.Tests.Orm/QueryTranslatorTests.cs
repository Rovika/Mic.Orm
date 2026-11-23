using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using BeautyShop;

namespace Mic.Tests.Orm;

// THE NEW TRANSLATOR'S HARNESS — what each step of LINQ_06 asserts against as the phases arrive. It asserts TREES, not
// SQL: the readable form (`AsText`) of what a phase produced, so a test says "this query becomes this tree" and reads
// the same whichever vendor would eventually render it. SQL text is the SQL builder's to assert, in the ORM.
//
// Against the Beauty Shop model, as `QueryMakerTests` is, and through BOTH front ends: a tree built by name is
// the same tree the typed front end builds, and a typed one needs only a session to write the expression against.
[TestClass]
public class QueryTranslatorTests {
  static ILinqDataModel? _model;

  static ILinqDataModel Model {
    get {
      if (_model == null) {
        var app = new PlainBeautyShopApp();
        app.CollectingLog();
        app.TryInit();
        Assert.IsTrue(app.Initialized, app.Report());
        _model = app.MapToPostgres();
      }
      return _model;
    }
  }

  [TestMethod]
  public void CacheTier_IsOffUnderASkipAndUnderALock() {
    // A sub-query over a cached entity inside a query for the server: a list parameter answered from memory, unless
    // the caller skips the entity cache or locks its rows. Each translation takes a tree of its own, since a
    // translation prepares its tree in place.
    var caching = MakeModelCaching("Employee");
    QueryNode MakeQuery() => QueryMaker.From(caching, "Person")
      .Where(p => p.Member("FirstName").In(
        QueryMaker.From(caching, "Employee")
          .Where(e => e.Member("HourlyRate").Greater(QueryMaker.Constant(25m)))
          .Select(e => [e.Member("HiredOn")])));

    var usual = MakeQuery();
    var translated = QueryTranslator.Translate(QueryTreeScanner.Scan(usual), usual, caching);
    CollectionAssert.AreEqual(new[] { "Employee" }, translated.SqlCachedEntities.Select(e => e.Name).ToArray());

    var skipping = MakeQuery();
    var skipped = QueryTranslator.Translate(QueryTreeScanner.Scan(skipping), skipping, caching, skipEntityCache: true);
    Assert.AreEqual(0, skipped.SqlCachedEntities.Count, "the statement takes nothing from memory");
    Assert.AreEqual(0, skipped.CachedEntities.Count);

    var locking = MakeQuery();
    var locked = QueryTranslator.Translate(QueryTreeScanner.Scan(locking, null, QueryOptions.LockForUpdate), locking,
      caching);
    Assert.AreEqual(0, locked.SqlCachedEntities.Count, "a lock is on the server's rows");

    // A query the cache would answer whole has no plan under a skip, only its statement.
    var whole = QueryMaker.From(caching, "Employee");
    Assert.IsTrue(QueryTranslator.Translate(QueryTreeScanner.Scan(whole), whole, caching).CanRunInCache);
    var wholeAgain = QueryMaker.From(caching, "Employee");
    Assert.IsFalse(QueryTranslator.Translate(QueryTreeScanner.Scan(wholeAgain), wholeAgain, caching,
      skipEntityCache: true).CanRunInCache);
  }

  /// <summary>A model of its own with one entity declared CACHED — for the tiering tests, which must not change what
  /// the shared model says of that entity to every other test.</summary>
  static ILinqDataModel MakeModelCaching(string entityName) {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    app.Model!.FindEntity(entityName)!.CacheType = CacheType.Server;
    return app.MapToPostgres();
  }

  /// <summary>The host a test plays at run time: rows are whatever the test made them — dictionaries, anonymous
  /// objects, records — read as the test says; a row of a shape is the ORM's.</summary>
  sealed class TestCallContext(IReadOnlyList<object?> arguments) : ILinqCallContext {
    public IReadOnlyList<object?> Arguments => arguments;
    public object? OperationContext => null;
    public IEnumerable<object?>? ResultRows { get; init; }
    public Func<object, ILinqEntityMember, object?>? Reader { get; init; }
    public Func<ILinqEntity, IReadOnlyList<object>>? Cached { get; init; }
    public object? ReadMember(object row, ILinqEntityMember member) =>
      (Reader ?? throw new InvalidOperationException("The test gave the run no member reader."))(row, member);
    public ILinqRow CreateRow(ILinqEntity entity) => new ProjectionData((TransientEntityInfo)entity);
    public IReadOnlyList<object>? GetCachedRows(ILinqEntity entity) => Cached?.Invoke(entity);
  }

  /// <summary>Translate a query built by name and hand back its prepared tree's text — the one line every assertion below
  /// starts from.</summary>
  /// <remarks>OVER THE MODEL WITH NO DATABASE: a projection's entity is made by the model, and a model with no
  /// storage behind it prepares the tree and builds no statement.</remarks>
  static TranslatedQuery Translate(QueryNode byName) =>
    QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, Model);

  [TestMethod]
  public void Resolving_GivesTheTreeEntitiesMembersTypes() {
    // THE UNTYPED TREE: members by name, a parameter with no idea what it stands for, an arithmetic node with no type.
    var byName = QueryMaker.From(Model, "Employee")
      .Where(row => QueryMaker.And(
        row.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))),
        row.Member("HomeShop").Member("Name").Equal(QueryMaker.Constant("Downtown"))))
      .OrderBy(row => row.Member("HourlyRate").Multiply(QueryMaker.Constant(2)), descending: true)
      .Take(QueryMaker.Constant(20));
    var before = byName.AsText;

    var translation = Translate(byName);

    // SHAPED AS IT CAME — phase 1 resolves in place and replaces nothing; the SQL tree is built beside it.
    Assert.AreSame(byName, translation.PreparedRoot);
    Assert.AreEqual(before, translation.PreparedRoot.AsText);
    Assert.IsNotNull(translation.SqlRoot);

    var take = (MethodCallNode)byName;
    var orderBy = (MethodCallNode)take.Argument(0);
    var where = (MethodCallNode)orderBy.Argument(0);
    Assert.AreEqual("Employee", where.Entity?.Name, "a filter produces rows of its source");
    Assert.AreEqual("Employee", take.Entity?.Name, "and so does a page");

    var whereLambda = (LambdaNode)where.Argument(1);
    Assert.AreEqual("Employee", ((LambdaParameterNode)whereLambda.Children[0]).Entity?.Name, "the row is a row of the source");
    var and = (BinaryNode)whereLambda.Body;
    var rate = (MemberAccessNode)((BinaryNode)and.Left).Left;
    Assert.AreEqual("HourlyRate", rate.ModelMember?.Name);
    Assert.AreEqual(typeof(decimal), rate.DataType, "a value member takes the model's type");
    var shopName = (MemberAccessNode)((BinaryNode)and.Right).Left;
    var shop = (MemberAccessNode)shopName.Target!;
    Assert.AreEqual("Shop", shop.TargetEntity?.Name, "a reference member reaches the far entity");
    Assert.AreEqual("Name", shopName.ModelMember?.Name);
    Assert.AreEqual("Shop", shopName.ModelMember?.Entity?.Name, "…and the next hop resolves against it");
    Assert.AreEqual(typeof(bool), and.DataType);

    var doubled = (BinaryNode)((LambdaNode)orderBy.Argument(1)).Body;
    Assert.AreEqual(typeof(decimal), doubled.DataType, "arithmetic takes its operand's type once that is known");

    // A MEMBER THE ENTITY DOES NOT HAVE is refused by name, where the untyped path can only find out.
    var wrong = QueryMaker.From(Model, "Employee").Where(row => row.Member("Salary").Equal(QueryMaker.Constant(1)));
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => Translate(wrong)).Message, "'Salary'");
  }

  [TestMethod]
  public void Resolving_TreatsTheTypedTreeTheSameWay() {
    // THE TYPED TREE arrives with CLR types and a closure to read `minRate` from, and no model members at all.
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    app.WithSqlite();
    var session = app.OpenSession();
    var minRate = 20m;
    var query = session.EntitySet<IEmployee>()
      .Where(e => e.HourlyRate >= minRate && e.HomeShop.Name == "Downtown")
      .OrderByDescending(e => e.HourlyRate * 2)
      .Select(e => new { e.HiredOn, Shop = e.HomeShop.Name });
    var (translation, _) = TranslateTyped(query, app.MappedModel());

    var select = (MethodCallNode)translation.PreparedRoot;
    Assert.AreEqual("EmployeeRow", select.Entity?.Name, "a projection produces rows of the shape it defines");
    CollectionAssert.AreEqual(new[] { "HiredOn", "Shop" }, select.Entity!.Members.Select(m => m.Name).ToArray());
    var where = (MethodCallNode)((MethodCallNode)select.Argument(0)).Argument(0);
    Assert.AreEqual("Employee", where.Entity?.Name);
    var condition = (BinaryNode)((LambdaNode)where.Argument(1)).Body;
    var rate = (MemberAccessNode)((BinaryNode)condition.Left).Left;
    Assert.AreEqual("HourlyRate", rate.ModelMember?.Name, "the same member, resolved the same way");
  }

  [TestMethod]
  public void PreSql_TakesTheCallersValuesAsParameters() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    app.WithSqlite();
    var session = app.OpenSession();
    var minRate = 20m;
    var rates = new[] { 25m, 30m };
    // THREE THINGS THE CALLER BROUGHT INSIDE THE LAMBDA: a captured local, a static read, and a collection under a
    // span conversion — beside a bare constant, which is shape and stays. `Take(5)` outside any lambda is a CONSTANT
    // in the expression however it was written: C# evaluates the argument before `Queryable.Take` ever sees it.
    var query = session.EntitySet<IEmployee>()
      .Where(e => e.HourlyRate >= minRate && e.HiredOn < DateOnly.FromDateTime(DateTime.Today) && rates.Contains(e.CommissionPercent)
        && e.HomeShop.Name == "Downtown")
      .OrderBy(e => e.Id).Take(5);
    var (translation, scan) = TranslateTyped(query, app.MappedModel());

    var take = (MethodCallNode)translation.PreparedRoot;
    Assert.IsInstanceOfType<ConstantNode>(take.Argument(1));
    Assert.AreEqual(QueryTier.Sql, take.Tier, "the query itself stays on the Sql tier");

    // Under the page: the ordering a page needs, then the filter.
    var where = (MethodCallNode)((MethodCallNode)take.Argument(0)).Argument(0);
    var and = (BinaryNode)((LambdaNode)where.Argument(1)).Body;
    var comparisons = new List<QueryNode>();
    QueryNode walk = and;
    while (walk is BinaryNode { NodeType: System.Linq.Expressions.ExpressionType.AndAlso } both) {
      comparisons.Insert(0, both.Right);
      walk = both.Left;
    }
    comparisons.Insert(0, walk);

    var min = (QueryParameterNode)((BinaryNode)comparisons[0]).Right;
    Assert.AreEqual("minRate", min.Name);
    Assert.AreEqual(typeof(decimal), min.DataType);
    Assert.AreEqual(ValueSourceKind.LocalVar, min.ValueSource, "read from the closure the scan found");
    Assert.IsTrue(min.ArgIndex >= 0, "…which is one of the arguments");
    var callCtx = new TestCallContext(scan.Arguments);
    Assert.AreEqual(20m, min.ValueGetter!(callCtx), "…and the getter reads it out of the run");

    var today = (QueryParameterNode)((BinaryNode)comparisons[1]).Right;
    Assert.AreEqual("FromDateTime", today.Name, "named after the read it makes");
    Assert.AreEqual(ValueSourceKind.Context, today.ValueSource, "a static read consumes no argument");
    Assert.AreEqual(-1, today.ArgIndex);
    Assert.AreEqual(DateOnly.FromDateTime(DateTime.Today), today.ValueGetter!(callCtx));

    // `rates.Contains(x)` binds to a SPAN overload since .NET 9; the conversion is taken off and the array is the
    // list parameter — the row member beside it is untouched.
    var contains = (MethodCallNode)comparisons[2];
    var list = (QueryParameterNode)contains.Argument(0);
    Assert.IsTrue(list.Flags.HasFlag(NodeFlags.List));
    Assert.AreEqual(typeof(decimal), list.DataType, "a list parameter carries its ELEMENT type");
    CollectionAssert.AreEqual(rates, (decimal[])list.ValueGetter!(callCtx)!);
    Assert.IsInstanceOfType<MemberAccessNode>(contains.Argument(1));

    Assert.IsInstanceOfType<ConstantNode>(((BinaryNode)comparisons[3]).Right, "a bare constant is shape and stays");
    Assert.IsInstanceOfType<MemberAccessNode>(((BinaryNode)comparisons[3]).Left, "a row member is never PreSql");
  }

  [TestMethod]
  public void ACachedEntityIsPreSqlAndTheQueryRunsInMemory() {
    // ROWS IN MEMORY, as dictionaries: the evaluator reads members through the run and never asks what a row is.
    static Dictionary<string, object?> Employee(decimal rate, int hiredYear) =>
      new() { ["HourlyRate"] = rate, ["HiredOn"] = new DateOnly(hiredYear, 1, 1) };
    var employees = new List<object> { Employee(10, 2020), Employee(20, 2022), Employee(30, 2021), Employee(40, 2019) };
    var callCtx = new TestCallContext([15m]) {
      Reader = (row, member) => ((Dictionary<string, object?>)row).GetValueOrDefault(member.Name),
      Cached = _ => employees,
    };
    var caching = MakeModelCaching("Employee");

    // THE WHOLE QUERY over a cached entity: filtered, ordered, paged — nothing here becomes SQL.
    var whole = QueryMaker.From(caching, "Employee")
      .Where(row => row.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))))
      .OrderBy(row => row.Member("HiredOn"), descending: true)
      .Take(QueryMaker.Constant(2));
    var translation = QueryTranslator.Translate(QueryTreeScanner.Scan(whole), whole, caching);
    Assert.IsTrue(translation.CanRunInCache);
    // THE CACHE MARKS COME OFF once the evaluator is built, so the same translation can still say what the SQL would
    // be — the shape needs its statement the moment a caller skips the cache.
    Assert.AreEqual(QueryTier.Sql, translation.PreparedRoot.Tier);
    CollectionAssert.AreEqual(new[] { "Employee" }, translation.CachedEntities.Select(e => e.Name).ToArray());
    Assert.AreEqual(0, translation.SqlCachedEntities.Count, "the statement itself reads nothing from memory");
    CollectionAssert.AreEqual(new[] { "minRate" }, translation.CacheEvaluator!.ParameterNames.ToArray());
    var rows = (List<object?>)translation.CacheEvaluator.Evaluate(callCtx)!;
    CollectionAssert.AreEqual(new[] { 20m, 30m },
      rows.Select(r => ((Dictionary<string, object?>)r!)["HourlyRate"]).ToArray(),
      "rate >= 15, newest hired first, two of them");

    // A REFERENCE WALKED — `row.Shop.Name` — is not something a detached row can answer, so the whole chain goes back
    // to the server: nothing is left on the PreSql tier, the entity set included.
    var walking = QueryMaker.From(caching, "Employee")
      .Where(row => row.Member("HomeShop").Member("Name").Equal(QueryMaker.Constant("Downtown")));
    var toServer = QueryTranslator.Translate(QueryTreeScanner.Scan(walking), walking, caching);
    Assert.IsFalse(toServer.CanRunInCache);
    var tiers = new List<QueryTier>();
    toServer.PreparedRoot.ForEach(n => tiers.Add(n.Tier));
    Assert.IsTrue(tiers.All(t => t == QueryTier.Sql), "reverted, whole chain");
    Assert.AreEqual(0, toServer.CachedEntities.Count, "…though the set was cached, nothing reads it in memory");

    // AN INNER SUB-QUERY over a cached entity inside a query that goes to the server becomes a LIST PARAMETER whose
    // getter runs the sub-query out of the run.
    var inner = QueryMaker.From(caching, "Person")
      .Where(p => p.Member("FirstName").In(
        QueryMaker.From(caching, "Employee")
          .Where(e => e.Member("HourlyRate").Greater(QueryMaker.Constant(25m)))
          .Select(e => [e.Member("HiredOn")])));
    var mixed = QueryTranslator.Translate(QueryTreeScanner.Scan(inner), inner, caching);
    Assert.IsFalse(mixed.CanRunInCache);
    CollectionAssert.AreEqual(new[] { "Employee" }, mixed.CachedEntities.Select(e => e.Name).ToArray());
    var contains = (MethodCallNode)((LambdaNode)((MethodCallNode)mixed.PreparedRoot).Argument(1)).Body;
    var list = (QueryParameterNode)contains.Argument(0);
    Assert.AreEqual("cached0", list.Name);
    Assert.IsTrue(list.Flags.HasFlag(NodeFlags.List));
    Assert.AreEqual(typeof(DateOnly), list.DataType, "a list of what was selected");
    Assert.AreEqual(QueryTier.PreSql, list.Tier);
    var dates = (List<object?>)list.ValueGetter!(callCtx)!;
    CollectionAssert.AreEquivalent(new object[] { new DateOnly(2021, 1, 1), new DateOnly(2019, 1, 1) }, dates);
  }

  [TestMethod]
  public void Spellings_BecomeNamedOperationsPostSqlMarked() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    app.WithSqlite();
    var session = app.OpenSession();
    var prefix = "Jo";

    // C#'S SPELLINGS: a string call, a CLR member of a date, a nullable's test — one vocabulary afterwards.
    var spelled = session.EntitySet<IEmployee>()
      .Where(e => e.Person.FirstName.ToUpper().StartsWith(prefix) && e.HiredOn.Year > 2020 && e.TerminatedOn.HasValue);
    var (translation, _) = TranslateTyped(spelled, app.MappedModel());
    var where = (MethodCallNode)translation.PreparedRoot;
    var body = (BinaryNode)((LambdaNode)where.Argument(1)).Body;
    var hasValue = (NamedOpNode)body.Right;
    Assert.AreEqual(NamedOpType.IsNotNull, hasValue.OpType, "`HasValue` is a null test");
    Assert.IsInstanceOfType<MemberAccessNode>(hasValue.Children[0]);
    var year = (BinaryNode)((BinaryNode)body.Left).Right;
    Assert.AreEqual(NamedOpType.Year, ((NamedOpNode)year.Left).OpType, "`.Year` is a named operation");
    var starts = (NamedOpNode)((BinaryNode)body.Left).Left;
    Assert.AreEqual(NamedOpType.StartsWith, starts.OpType);
    Assert.AreEqual(NamedOpType.Upper, ((NamedOpNode)starts.Children[0]).OpType, "…over the inner call, converted first");
    Assert.AreEqual("prefix_StartsWith", starts.Children[1].Name, "the term is named after the match it makes");
    Assert.AreEqual(QueryTier.Sql, where.Tier);

    // POSTSQL ON THE SELECT LIST: a call the server has no word for is marked, the select is not.
    var projected = session.EntitySet<IEmployee>().Select(e => new { e.HiredOn, Label = e.HiredOn.ToString() });
    var (post, _) = TranslateTyped(projected, app.MappedModel());
    var select = (MethodCallNode)post.PreparedRoot;
    Assert.AreEqual(QueryTier.Sql, select.Tier, "the select still runs, projecting what the C# needs");
    var slots = (NewNode)((LambdaNode)select.Argument(1)).Body;
    Assert.AreEqual(QueryTier.Sql, slots.Children[0].Tier);
    Assert.AreEqual(QueryTier.PostSql, slots.Children[1].Tier, "`ToString()` runs in the reader");
    Assert.AreEqual(QueryTier.Sql, slots.Children[1].Children[0].Tier, "…over a column the server returns");

    // POSTSQL ON THE CHAIN: a grouping returned as groups is done in C# over the source rows, and so is everything
    // above it.
    var grouped = session.EntitySet<IEmployee>().GroupBy(e => e.HomeShop).Take(3);
    var (chain, _) = TranslateTyped(grouped, app.MappedModel());
    Assert.AreEqual(QueryTier.PostSql, chain.PreparedRoot.Tier);
    Assert.AreEqual(QueryTier.PostSql, ((MethodCallNode)chain.PreparedRoot).Argument(0).Tier);
    Assert.AreEqual(QueryTier.Sql, ((MethodCallNode)((MethodCallNode)chain.PreparedRoot).Argument(0)).Argument(0).Tier,
      "the source rows come from the server");

    // POSTSQL INSIDE A CONDITION is refused by name.
    var refused = session.EntitySet<IEmployee>().Where(e => e.HiredOn.ToString() == "2020");
    var message = Assert.ThrowsExactly<NotSupportedException>(() => TranslateTyped(refused, app.MappedModel())).Message;
    StringAssert.Contains(message, "ToString");
    StringAssert.Contains(message, "condition");
  }

  [TestMethod]
  public void EntityShapes_BecomeKeyColumns() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    app.WithSqlite();
    var session = app.OpenSession();
    ILinqEntity employee = app.Model!.FindEntity("Employee")!;
    var shopReference = employee.FindMember("HomeShop")!;
    var heldKey = shopReference.ReferenceInfo!.FromMembers[0];
    var shop = session.NewEntity<IShop>();
    var shops = new[] { shop };
    var shopId = Guid.NewGuid();

    // THREE SHAPES: a key read through a reference, an entity compared, an entity looked for in a list.
    var query = session.EntitySet<IEmployee>()
      .Where(e => e.HomeShop.Id == shopId && e.HomeShop == shop && shops.Contains(e.HomeShop) && e.Manager != null);
    var (translation, scan) = TranslateTyped(query, app.MappedModel());
    var terms = new List<QueryNode>();
    QueryNode walk = ((LambdaNode)((MethodCallNode)translation.PreparedRoot).Argument(1)).Body;
    while (walk is BinaryNode { NodeType: System.Linq.Expressions.ExpressionType.AndAlso } both) {
      terms.Insert(0, both.Right);
      walk = both.Left;
    }
    terms.Insert(0, walk);

    // `e.HomeShop.Id` IS THE OWNER'S FOREIGN-KEY COLUMN — no reference in the chain, no join to come.
    var shortcut = (MemberAccessNode)((BinaryNode)terms[0]).Left;
    Assert.AreEqual(heldKey.Name, shortcut.ModelMember?.Name);
    Assert.IsInstanceOfType<LambdaParameterNode>(shortcut.Target, "read straight off the row");

    // `e.HomeShop == shop` IS THAT COLUMN AGAINST THE KEY READ OFF THE CAPTURED OBJECT, when the query runs.
    var compared = (BinaryNode)terms[1];
    Assert.AreEqual(heldKey.Name, ((MemberAccessNode)compared.Left).ModelMember?.Name);
    var key = (QueryParameterNode)compared.Right;
    Assert.AreEqual("shop_Id", key.Name);
    Assert.AreEqual(ValueSourceKind.LocalVar, key.ValueSource, "derived from the captured entity, it reads where it did");
    Assert.IsTrue(key.ArgIndex >= 0);
    var callCtx = new TestCallContext(scan.Arguments) {
      Reader = (row, member) => row.GetType().GetProperty(member.Name)?.GetValue(row),
    };
    Assert.AreEqual(shop.Id, key.ValueGetter!(callCtx), "…through the run's member reader, not the engine's");

    // `shops.Contains(e.HomeShop)` IS A LIST OF KEYS against that column.
    var contains = (MethodCallNode)terms[2];
    var keys = (QueryParameterNode)contains.Argument(0);
    Assert.AreEqual("shops_Id", keys.Name);
    Assert.IsTrue(keys.Flags.HasFlag(NodeFlags.List));
    Assert.AreEqual(typeof(Guid), keys.DataType);
    CollectionAssert.AreEqual(new object?[] { shop.Id }, (List<object?>)keys.ValueGetter!(callCtx)!);
    Assert.AreEqual(heldKey.Name, ((MemberAccessNode)contains.Argument(1)).ModelMember?.Name);

    // `e.Manager != null` IS THE FOREIGN-KEY COLUMN AGAINST NULL, negated the way SQL means it.
    var notNull = terms[3];
    Assert.AreEqual(System.Linq.Expressions.ExpressionType.Not, ((ExpressionNode)notNull).NodeType);
    var isNull = (NamedOpNode)notNull.Children[0];
    Assert.AreEqual(NamedOpType.IsNull, isNull.OpType);
    Assert.AreEqual(employee.FindMember("Manager")!.ReferenceInfo!.FromMembers[0].Name, ((MemberAccessNode)isNull.Children[0]).ModelMember?.Name);
  }

  [TestMethod]
  public void Mapping_AnswersTablesAndColumnsOverTheDbModel() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    ILinqDataModel mapping = app.MappedModel();
    ILinqEntity employee = app.Model!.FindEntity("Employee")!;

    // THE SAME OBJECTS THE ORM ALREADY HOLDS, seen through the engine's three interfaces: a table knows its entity, a
    // column its member, and the mapping is the two lookups the runtime path already makes.
    var table = mapping.FindTable(employee)!;
    Assert.AreEqual("Employee", table.Name);
    Assert.AreEqual("beauty", table.Schema);
    Assert.AreSame(employee, table.Entity);
    var rate = employee.FindMember("HourlyRate")!;
    var column = mapping.FindColumn(employee, rate)!;
    Assert.AreEqual("HourlyRate", column.Name);
    Assert.AreEqual(typeof(decimal), column.DataType);
    Assert.AreSame(rate, column.Member);
    Assert.AreSame(table, column.Table);
    Assert.IsTrue(table.Columns.Contains(column));
    // A REFERENCE HAS NO COLUMN OF ITS OWN — its foreign-key members do — and a reference asked for answers null.
    Assert.IsNull(mapping.FindColumn(employee, employee.FindMember("HomeShop")!));
    Assert.IsNotNull(mapping.FindColumn(employee, employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0]));
  }

  [TestMethod]
  public void SqlTree_ReadsLikeItsStatement() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    ILinqDataModel mapping = app.MappedModel();
    ILinqEntity employee = app.Model!.FindEntity("Employee")!;
    ILinqEntity shop = app.Model!.FindEntity("Shop")!;

    // BUILT BY HAND, the way the transform will build it: a select over a table, a join to another, a column of each row,
    // a condition, an ordering, a page, and one value selected out.
    var employees = new TableNode(mapping.FindTable(employee)!) { Alias = "t0" };
    var shops = new TableNode(mapping.FindTable(shop)!) { Alias = "t1" };
    var select = new SelectNode(employees, employee);
    var rate = new ColumnNode(employees.Row, mapping.FindColumn(employee, employee.FindMember("HourlyRate")!)!);
    var shopKey = new ColumnNode(employees.Row, mapping.FindColumn(employee, employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0])!);
    var shopId = new ColumnNode(shops.Row, mapping.FindColumn(shop, shop.FindMember("Id")!)!);
    var shopName = new ColumnNode(shops.Row, mapping.FindColumn(shop, shop.FindMember("Name")!)!);
    select.From.AddChild(new JoinNode(shops, TranslationHelper.MakeBinary(System.Linq.Expressions.ExpressionType.Equal, shopKey, shopId), JoinType.LeftOuter));
    select.Where.AddChild(TranslationHelper.MakeBinary(System.Linq.Expressions.ExpressionType.GreaterThanOrEqual, rate,
      QueryMaker.Parameter("minRate", typeof(decimal))));
    select.OrderBy.AddChild(new ColumnNode(employees.Row, mapping.FindColumn(employee, employee.FindMember("HiredOn")!)!) {
      Flags = NodeFlags.Descending,
    });
    select.Take.AddChild(QueryMaker.Constant(20));
    select.SelectList.AddChild(new SelectedValueNode(shopName, "Shop"));

    Assert.AreEqual(
      "SELECT t1.Name AS Shop FROM Employee t0 LEFT JOIN Shop t1 ON (t0." + shopKey.Name + " = t1.Id)"
      + " WHERE (t0.HourlyRate >= @minRate) ORDER BY t0.HiredOn DESC TAKE 20",
      select.AsText);
    Assert.AreSame(select, employees.OwnerSelect, "a source knows the select it is a source of");
    Assert.AreSame(employee, select.Entity);
    Assert.AreEqual(8, select.ChildCount, "eight clauses, always, empty or not");
    Assert.IsTrue(select.GroupBy.IsEmpty);
  }

  [TestMethod]
  public void Rewrite_MakesAFilteredSetOneSelect() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();

    // TWO FILTERS OVER A TABLE ARE ONE SELECT WITH TWO CONDITIONS; `&&` inside one is the engine's N-ary AND; the row
    // the lambdas were written over is the table's row, and every member of it a column of that row.
    var byName = QueryMaker.From(adapter, "Employee")
      .Where(row => QueryMaker.And(
        row.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))),
        row.Member("TerminatedOn").Equal(QueryMaker.Constant(null))))
      .Where(row => row.Member("CommissionPercent").Less(QueryMaker.Constant(10m)).Not());
    var translation = QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, adapter);

    Assert.AreEqual(
      "SELECT * FROM Employee t0 WHERE ((t0.HourlyRate >= @minRate) AND (t0.TerminatedOn IS NULL)) AND NOT (t0.CommissionPercent < 10)",
      translation.SqlRoot.AsText);
    var select = (SelectNode)translation.SqlRoot;
    Assert.AreEqual("Employee", select.Entity?.Name, "rows of the entity, until a projection says otherwise");
    Assert.IsTrue(select.SelectList.IsEmpty, "the select list is built on demand, by the readers");
    Assert.AreSame(select, select.Source.OwnerSelect);
    var rate = (ColumnNode)((BinaryNode)select.Where.Children[0].Children[0]).Left;
    Assert.AreSame(select.Source.Row, rate.Row, "a column of the table's own row");
    Assert.AreEqual("HourlyRate", rate.Column?.Member?.Name);

    // THE SAME QUERY IN C# makes the same tree.
    var session = app.OpenSession();
    var minRate = 20m;
    var typed = session.EntitySet<IEmployee>()
      .Where(e => e.HourlyRate >= minRate && e.TerminatedOn == null)
      .Where(e => !(e.CommissionPercent < 10));
    var scan = ExpressionTreeScanner.Scan(typed.Expression);
    var fromCSharp = QueryTranslator.Translate(scan, ExpressionTreeConverter.Convert(typed.Expression), adapter);
    Assert.AreEqual(translation.SqlRoot.AsText, fromCSharp.SqlRoot.AsText);
  }

  [TestMethod]
  public void Rewrite_OrdersPagesAndCutsDerivedTables() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    string TextOf(QueryNode byName) => QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, adapter).SqlRoot.AsText;

    // FILTER, ORDER, PAGE, in the order a select applies them: one select. A second ordering appends.
    Assert.AreEqual(
      "SELECT DISTINCT * FROM Employee t0 WHERE (t0.HourlyRate >= @minRate) ORDER BY t0.HiredOn DESC, t0.Id SKIP @skip TAKE 20",
      TextOf(QueryMaker.From(adapter, "Employee")
        .Where(row => row.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))))
        .OrderBy(row => row.Member("HiredOn"), descending: true)
        .OrderBy(row => row.Member("Id"))
        .Distinct()
        .Skip(QueryMaker.Parameter("skip", typeof(int)))
        .Take(QueryMaker.Constant(20))));

    // THE CHAIN GOES BACKWARDS — a filter over a page — and the page becomes a DERIVED TABLE; the column the outer
    // filter names is projected OUT of it on demand, FIRST — and since the result is whole rows and the inner list
    // has started naming values, the readers pass follows with every other stored member.
    var derivedRoot = QueryMaker.From(adapter, "Employee")
      .OrderBy(row => row.Member("Id"))
      .Take(QueryMaker.Constant(5))
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(RecordStatus.Active)));
    var derived = QueryTranslator.Translate(QueryTreeScanner.Scan(derivedRoot), derivedRoot, adapter);
    var derivedText = derived.SqlRoot.AsText;
    StringAssert.StartsWith(derivedText, "SELECT * FROM (SELECT t0.Status AS Status, t0.");
    StringAssert.EndsWith(derivedText, " FROM Employee t0 ORDER BY t0.Id TAKE 5) t1 WHERE (t1.Status = Active)");
    var outer = (SelectNode)derived.SqlRoot;
    var inner = ((DerivedRowSetNode)outer.Source).Select;
    Assert.AreEqual(derived.ResultInfo.Members.Count, inner.SelectList.ChildCount, "every stored member, once");
    Assert.AreSame(outer, outer.Source.OwnerSelect);
    Assert.AreEqual("Employee", outer.Entity?.Name, "still rows of the entity");
    var status = (ColumnNode)((BinaryNode)outer.Where.Children[0]).Left;
    Assert.AreSame(inner.SelectList.Children[0], status.Selected, "the outer column IS the inner output value");

    // THE REFUSALS, each by name: a page of an unordered set, a page of a page, an ordering with nothing to cut.
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => TextOf(
      QueryMaker.From(adapter, "Employee").Take(QueryMaker.Constant(5)))).Message, "ordering");
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => TextOf(
      QueryMaker.From(adapter, "Employee").OrderBy(r => r.Member("Id")).Take(QueryMaker.Constant(5)).Skip(QueryMaker.Constant(2)))).Message,
      "page of a page");
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => TextOf(
      QueryMaker.From(adapter, "Employee").OrderBy(r => r.Member("Id")).Where(r => r.Member("Status").Equal(QueryMaker.Constant(0))))).Message,
      "derived table");
  }

  [TestMethod]
  public void Rewrite_JoinsAReferenceAndSubSelectsAList() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    var model = app.Model!;
    ILinqEntity employee = model.FindEntity("Employee")!;
    var shopKey = employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0].Name;
    var managerKey = employee.FindMember("Manager")!.ReferenceInfo!.FromMembers[0].Name;
    string TextOf(QueryNode byName) => QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, adapter).SqlRoot.AsText;

    // A REFERENCE WALKED IS A JOIN, one per path however often the path is named; a NULLABLE reference is an outer
    // join, and everything reached through it is outer too.
    Assert.AreEqual(
      $"SELECT * FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id) LEFT JOIN Employee t2 ON (t0.{managerKey} = t2.Id)"
      + $" LEFT JOIN Shop t3 ON (t2.{shopKey} = t3.Id)"
      + " WHERE ((t1.Name = 'Downtown') AND (t2.HourlyRate > t0.HourlyRate)) ORDER BY t3.Name, t1.TimeZone",
      TextOf(QueryMaker.From(adapter, "Employee")
        .Where(r => QueryMaker.And(
          r.Member("HomeShop").Member("Name").Equal(QueryMaker.Constant("Downtown")),
          r.Member("Manager").Member("HourlyRate").Greater(r.Member("HourlyRate"))))
        .OrderBy(r => r.Member("Manager").Member("HomeShop").Member("Name"))
        .OrderBy(r => r.Member("HomeShop").Member("TimeZone"))));

    // A LIST IS A SUB-SELECT AND NEVER A JOIN — correlated to the owner through the inverse's key pairing read the
    // other way — asked whether it has any, counted, or summed; a filter over it is its own WHERE.
    Assert.AreEqual(
      $"SELECT * FROM Shop t0 WHERE EXISTS (SELECT * FROM Employee t1 WHERE (t1.{shopKey} = t0.Id) AND (t1.HourlyRate > 50))"
      + $" AND ((SELECT COUNT(*) FROM Employee t2 WHERE (t2.{shopKey} = t0.Id)) > 2)"
      + $" AND ((SELECT SUM(t3.HourlyRate) FROM Employee t3 WHERE (t3.{shopKey} = t0.Id) AND (t3.TerminatedOn IS NULL)) < 1000)",
      TextOf(QueryMaker.From(adapter, "Shop")
        .Where(s => s.Member("Employees").Any(e => e.Member("HourlyRate").Greater(QueryMaker.Constant(50m))))
        .Where(s => s.Member("Employees").Count().Greater(QueryMaker.Constant(2L)))
        .Where(s => s.Member("Employees").Where(e => e.Member("TerminatedOn").Equal(QueryMaker.Constant(null)))
          .Sum(e => e.Member("HourlyRate")).Less(QueryMaker.Constant(1000m)))));
  }

  [TestMethod]
  public void Rewrite_ProjectsGroupsAggregatesTakesOneRow() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    var model = app.Model!;
    ILinqEntity employee = model.FindEntity("Employee")!;
    var shopKey = employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0].Name;
    TranslatedQuery Translate(QueryNode byName) => QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, adapter);
    string TextOf(QueryNode byName) => Translate(byName).SqlRoot.AsText;

    // A PROJECTION REPLACES THE SELECT LIST and is outermost of its select: filter, ordering and page under it apply
    // in the same select; its slots are named, and a reference walked inside one is a join like any other.
    var projected = Translate(QueryMaker.From(adapter, "Employee")
      .Where(r => r.Member("HourlyRate").GreaterOrEqual(QueryMaker.Parameter("minRate", typeof(decimal))))
      .OrderBy(r => r.Member("HiredOn"), descending: true)
      .Take(QueryMaker.Constant(20))
      .Select(r => [r.Member("HiredOn"), r.Member("HomeShop").Member("Name").As("Shop"),
        r.Member("HourlyRate").Multiply(QueryMaker.Constant(2)).As("Doubled")]));
    Assert.AreEqual(
      $"SELECT t0.HiredOn AS HiredOn, t1.Name AS Shop, (t0.HourlyRate * 2) AS Doubled FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id)"
      + " WHERE (t0.HourlyRate >= @minRate) ORDER BY t0.HiredOn DESC TAKE 20",
      projected.SqlRoot.AsText);
    CollectionAssert.AreEqual(new[] { "HiredOn", "Shop", "Doubled" },
      ((SelectNode)projected.SqlRoot).Entity!.Members.Select(m => m.Name).ToArray(), "rows of the shape the projection defined");

    // ONE ROW SELECTED WHOLE is a change of which table the rows come from, not a select list.
    var whole = (SelectNode)Translate(QueryMaker.From(adapter, "Employee").Select(r => [r.Member("HomeShop")])).SqlRoot;
    Assert.AreEqual($"SELECT * FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id)", whole.AsText);
    Assert.AreEqual("Shop", whole.Entity?.Name);
    Assert.AreEqual("t1", whole.ResultSet.Alias);

    // A GROUPING: the keys in the GROUP BY and on the select list; a filter over the groups is a HAVING; a projection
    // over the groups names its keys and aggregates over the group's source rows; the key is an expression, copied,
    // since three servers will not group by a select-list alias.
    Assert.AreEqual(
      $"SELECT t1.Name AS Shop, COUNT(*) AS Count, SUM(t0.HourlyRate) AS Total FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id)"
      + " WHERE (t0.TerminatedOn IS NULL) GROUP BY t1.Name HAVING (COUNT(*) > 1)",
      TextOf(QueryMaker.From(adapter, "Employee")
        .Where(r => r.Member("TerminatedOn").Equal(QueryMaker.Constant(null)))
        .GroupBy(r => [r.Member("HomeShop").Member("Name").As("Shop")])
        .Where(g => g.Count().Greater(QueryMaker.Constant(1L)))
        .Select(g => [g.Member("Shop"), g.Count().As("Count"), g.Sum(x => x.Member("HourlyRate")).As("Total")])));

    // THE SAME GROUPING IN C#, with `g.Key` and an entity key.
    var session = app.OpenSession();
    var typed = session.EntitySet<IEmployee>()
      .GroupBy(e => e.HomeShop)
      .Select(g => new { Shop = g.Key.Name, Count = g.Count(), Best = g.Max(x => x.HourlyRate) });
    var scan = ExpressionTreeScanner.Scan(typed.Expression);
    Assert.AreEqual(
      $"SELECT t1.Name AS Shop, COUNT(*) AS Count, MAX(t0.HourlyRate) AS Best FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id)"
      + " GROUP BY t1.Id",
      QueryTranslator.Translate(scan, ExpressionTreeConverter.Convert(typed.Expression), adapter).SqlRoot.AsText,
      "an entity as a key groups by its key columns");

    // AN AGGREGATE OVER THE WHOLE QUERY is the root as one value; a page under it is cut off into a derived table,
    // since `COUNT(*) … TAKE 5` would count the whole table.
    Assert.AreEqual("SELECT COUNT(*) FROM Employee t0 WHERE (t0.HourlyRate > 10)",
      TextOf(QueryMaker.From(adapter, "Employee").Where(r => r.Member("HourlyRate").Greater(QueryMaker.Constant(10m))).Count()));
    Assert.AreEqual("SELECT COUNT(*) FROM (SELECT * FROM Employee t0 ORDER BY t0.Id TAKE 5) t1",
      TextOf(QueryMaker.From(adapter, "Employee").OrderBy(r => r.Member("Id")).Take(QueryMaker.Constant(5)).Count()));
    Assert.AreEqual("EXISTS (SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 10))",
      TextOf(QueryMaker.From(adapter, "Employee").Where(r => r.Member("HourlyRate").Greater(QueryMaker.Constant(10m))).Any()));

    // ONE ROW: unordered, ordered by the primary key so every server agrees which; `Single` takes two so "more than
    // one" can be reported; `Last` reverses the ordering.
    var first = (SelectNode)Translate(QueryMaker.From(adapter, "Employee").Where(r => r.Member("HourlyRate").Greater(QueryMaker.Constant(10m)))
      .OrderBy(r => r.Member("HiredOn")).Take(QueryMaker.Constant(1))).SqlRoot;
    Assert.AreEqual("SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 10) ORDER BY t0.HiredOn TAKE 1", first.AsText);
    // `First`/`Last`/`Single` EXECUTE when called on an IQueryable, so their expressions are made by hand here.
    var ordered = session.EntitySet<IEmployee>().OrderBy(e => e.HiredOn);
    System.Linq.Expressions.Expression<Func<IEmployee, bool>> wellPaid = e => e.HourlyRate > 10;
    var lastCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.LastOrDefault), [typeof(IEmployee)],
      ordered.Expression, System.Linq.Expressions.Expression.Quote(wellPaid));
    var last = (SelectNode)QueryTranslator.Translate(ExpressionTreeScanner.Scan(lastCall), ExpressionTreeConverter.Convert(lastCall), adapter).SqlRoot;
    Assert.AreEqual("SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 10) ORDER BY t0.HiredOn DESC TAKE 1", last.AsText);
    Assert.AreEqual(nameof(Queryable.LastOrDefault), last.OneRow, "the reader has to know which question was asked");
    var singleCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Single), [typeof(IEmployee)],
      session.EntitySet<IEmployee>().Expression);
    Assert.AreEqual("SELECT * FROM Employee t0 ORDER BY t0.Id TAKE 2",
      QueryTranslator.Translate(ExpressionTreeScanner.Scan(singleCall), ExpressionTreeConverter.Convert(singleCall), adapter).SqlRoot.AsText,
      "unordered, ordered by the key; two rows so that a second one can be reported");
  }

  [TestMethod]
  public void ASubQueryIsALocalVariableHoldingAQueryAndNeverACallInsideTheLambda() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    ILinqEntity employee = app.Model!.FindEntity("Employee")!;
    var shopKey = employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0].Name;
    var session = app.OpenSession();

    // THE SUB-QUERY IS A LOCAL: the converter copies its expression in place of the variable, the scanner hashes it —
    // its own captured `zone` included — and it becomes an uncorrelated sub-select: an `IN` list, and a scalar.
    var zone = "UTC";
    var activeShops = session.EntitySet<IShop>().Where(s => s.Status == RecordStatus.Active && s.TimeZone == zone).Select(s => s.Id);
    var query = session.EntitySet<IEmployee>().Where(e => activeShops.Contains(e.HomeShop.Id) && activeShops.Count() > 2);
    var scan = ExpressionTreeScanner.Scan(query.Expression);
    var translation = QueryTranslator.Translate(scan, ExpressionTreeConverter.Convert(query.Expression), adapter);
    Assert.AreEqual(
      $"SELECT * FROM Employee t0 WHERE ((t0.{shopKey} IN (SELECT t1.Id AS Id FROM Shop t1 WHERE ((t1.Status = 0) AND (t1.TimeZone = @zone))))"
      + " AND ((SELECT COUNT(*) FROM (SELECT t2.Id AS Id FROM Shop t2 WHERE ((t2.Status = 0) AND (t2.TimeZone = @zone))) t3) > 2))",
      translation.SqlRoot.AsText, "the projection under the count is cut off, as it is under any aggregate");
    Assert.IsTrue(scan.Arguments.Any(c => c?.GetType().Name.Contains("DisplayClass") == true), "the sub-query's closure is an argument of this query");

    // A DIFFERENT SUB-QUERY IN THE SAME VARIABLE IS A DIFFERENT SHAPE, since the scanner walked into it.
    var otherShops = session.EntitySet<IShop>().Where(s => s.Status == RecordStatus.Inactive).Select(s => s.Id);
    var otherQuery = session.EntitySet<IEmployee>().Where(e => otherShops.Contains(e.HomeShop.Id) && otherShops.Count() > 2);
    Assert.AreNotEqual(scan.CacheKey, ExpressionTreeScanner.Scan(otherQuery.Expression).CacheKey);

    // `session.EntitySet<T>()` INSIDE THE LAMBDA is refused by name, saying what to write instead.
    var inline = session.EntitySet<IEmployee>().Where(e => session.EntitySet<IShop>().Any(s => s.Id == e.HomeShop.Id));
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => TranslateTyped(inline, app.MappedModel())).Message, "local variable");
  }

  [TestMethod]
  public void ABundleIsResolvedAwayAndSpreadsJoinsAndSetOperationsAreBuilt() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    var model = app.Model!;
    ILinqEntity employee = model.FindEntity("Employee")!;
    var shopKey = employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0].Name;
    var session = app.OpenSession();
    string TextOf(IQueryable query) => QueryTranslator.Translate(
      ExpressionTreeScanner.Scan(query.Expression), ExpressionTreeConverter.Convert(query.Expression), adapter).SqlRoot.AsText;
    string TextOfByName(QueryNode byName) => QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, adapter).SqlRoot.AsText;

    // A SPREAD OF A LIST IS A JOIN, and the pair the result selector bundles is RESOLVED AWAY: `x.Shop` and `x.Emp`
    // are the two rows, read in place; nothing of the bundle reaches the SQL tree.
    Assert.AreEqual(
      $"SELECT t0.Name AS Name, t1.HiredOn AS HiredOn FROM Shop t0 JOIN Employee t1 ON (t1.{shopKey} = t0.Id)"
      + " WHERE (t0.TimeZone = 'UTC') AND (t1.HourlyRate > 10)",
      TextOf(session.EntitySet<IShop>()
        .Where(s => s.TimeZone == "UTC")
        .SelectMany(s => s.Employees, (s, e) => new { Shop = s, Emp = e })
        .Where(x => x.Emp.HourlyRate > 10)
        .Select(x => new { x.Shop.Name, x.Emp.HiredOn })));

    // A PAGE OF THE LIST, SPREAD, IS A PAGE PER ROW: the list's rows numbered within the key that tied them to the
    // shop, in a derived table, joined on the tie and on the numbers the page asked for — and the number trails the
    // statement's ordering, so each shop's rows come back in the page's order.
    Assert.AreEqual(
      $"SELECT t0.Name AS Name, t2.HiredOn AS HiredOn FROM Shop t0 JOIN (SELECT ROW_NUMBER() OVER (PARTITION BY"
      + $" t1.{shopKey} ORDER BY t1.HiredOn DESC) AS __rn, t1.{shopKey} AS __k0, t1.HiredOn AS HiredOn FROM Employee t1"
      + $" WHERE (t1.HourlyRate > 10)) t2 ON ((t2.__k0 = t0.Id) AND (t2.__rn <= 3)) ORDER BY t2.__rn",
      TextOf(session.EntitySet<IShop>()
        .SelectMany(s => s.Employees.Where(e => e.HourlyRate > 10).OrderByDescending(e => e.HiredOn).Take(3),
          (s, e) => new { s.Name, e.HiredOn })));

    // WHAT THE OUTER SELECT ASKS OF THE TIED VALUE IS ASKED INSIDE TOO, before the rows are numbered: the server
    // numbers that shop's employees and no others. A condition on anything else of the shop stays outside alone.
    var shopId = Guid.NewGuid();
    var filtered = TextOf(session.EntitySet<IShop>()
      .Where(s => s.Id == shopId && s.TimeZone == "UTC")
      .SelectMany(s => s.Employees.OrderByDescending(e => e.HiredOn).Take(3), (s, e) => new { s.Name, e.HiredOn }));
    StringAssert.Contains(filtered, $"FROM Employee t1 WHERE (t1.{shopKey} = ",
      "the tie's condition, on the inner side");
    StringAssert.Contains(filtered, "(t0.Id = ", "and still on the outer");
    Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(filtered, "TimeZone").Count,
      "a condition on another column is not carried");

    // A NAMED QUERY is defined beside the root — the statement's `WITH` list — and read by name where it was used;
    // what it exposes is what its readers asked for, as a derived table's would be.
    string WithTextOf(IQueryable query) {
      var translation = QueryTranslator.Translate(
        ExpressionTreeScanner.Scan(query.Expression), ExpressionTreeConverter.Convert(query.Expression), adapter);
      return string.Join(", ", translation.Ctes.Select(c => c.AsText)) + " | " + translation.SqlRoot.AsText;
    }
    var paid = session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10).AsCte("paid");
    Assert.AreEqual(
      $"paid AS (SELECT t1.{shopKey} AS {shopKey}, t1.HiredOn AS HiredOn FROM Employee t1 WHERE (t1.HourlyRate > 10))"
      + $" | SELECT t0.Name AS Name, t2.HiredOn AS HiredOn FROM Shop t0 JOIN paid t2 ON (t0.Id = t2.{shopKey})",
      WithTextOf(session.EntitySet<IShop>().Join(paid, s => s.Id, e => e.HomeShop.Id, (s, e) => new { s.Name, e.HiredOn })));

    // A RECURSIVE ONE is its anchor and its step as one UNION ALL, both carrying the level — zero, then one more —
    // and the step reads the query itself, joined INNER to the rows it reaches, bounded by the level.
    var managerKey = employee.FindMember("Manager")!.ReferenceInfo!.FromMembers[0].Name;
    var chain = WithTextOf(session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10)
      .AsRecursiveCte("chain", found => found.Where(e => e.Manager != null).Select(e => e.Manager!), maxDepth: 7)
      .Select(e => e.HiredOn));
    StringAssert.StartsWith(chain, "chain AS (SELECT 0 AS __lvl, ");
    StringAssert.Contains(chain, " UNION ALL SELECT (t1.__lvl + 1) AS __lvl, ");
    StringAssert.Contains(chain, $" FROM chain t1 JOIN Employee t2 ON (t1.{managerKey} = t2.Id) WHERE ");
    StringAssert.Contains(chain, "(t1.__lvl < 7)");
    StringAssert.EndsWith(chain, " | SELECT t3.HiredOn AS HiredOn FROM chain t3");

    // A `let` IS A BUNDLE OF ONE ROW AND A VALUE: the value's expression is read where the name is.
    Assert.AreEqual(
      "SELECT t0.HiredOn AS HiredOn FROM Employee t0 WHERE ((t0.HourlyRate * 2) > 20)",
      TextOf(session.EntitySet<IEmployee>()
        .Select(e => new { e, rate = e.HourlyRate * 2 })
        .Where(x => x.rate > 20)
        .Select(x => x.e.HiredOn)));

    // A JOIN THE CALLER WROTE, on two key selectors; the result selector's bundle is the pair, and selecting one
    // row of it whole is selecting that table's rows.
    Assert.AreEqual(
      $"SELECT * FROM Employee t0 JOIN Shop t1 ON (t0.{shopKey} = t1.Id) WHERE (t1.TimeZone = 'UTC')",
      TextOf(session.EntitySet<IEmployee>()
        .Join(session.EntitySet<IShop>(), e => e.HomeShop.Id, s => s.Id, (e, s) => new { e, s })
        .Where(x => x.s.TimeZone == "UTC")
        .Select(x => x.e)));

    // `GroupJoin` PUTS THE INNER ROWS IN SCOPE AS A CORRELATED SUB-SELECT: spread with `DefaultIfEmpty()` they are
    // a LEFT JOIN — the pattern C# writes for `join … into … from … in ….DefaultIfEmpty()` — and counted they are a
    // scalar sub-query on the select list. The second bundle nests the first.
    Assert.AreEqual(
      $"SELECT t0.Name AS Name, t1.HiredOn AS HiredOn FROM Shop t0 LEFT JOIN Employee t1 ON (t0.Id = t1.{shopKey})"
      + " WHERE ((t1.HourlyRate > 10) OR (t0.TimeZone = 'UTC'))",
      TextOf(session.EntitySet<IShop>()
        .GroupJoin(session.EntitySet<IEmployee>(), s => s.Id, e => e.HomeShop.Id, (s, emps) => new { s, emps })
        .SelectMany(x => x.emps.DefaultIfEmpty(), (x, e) => new { x.s, e })
        .Where(y => y.e.HourlyRate > 10 || y.s.TimeZone == "UTC")
        .Select(y => new { y.s.Name, y.e.HiredOn })));
    Assert.AreEqual(
      $"SELECT t0.Name AS Name, (SELECT COUNT(*) FROM Employee t1 WHERE (t0.Id = t1.{shopKey})) AS n FROM Shop t0",
      TextOf(session.EntitySet<IShop>()
        .GroupJoin(session.EntitySet<IEmployee>(), s => s.Id, e => e.HomeShop.Id, (s, emps) => new { s, n = emps.Count() })
        .Select(x => new { x.s.Name, x.n })));

    // A SET OPERATION IS TWO SELECTS COMBINED, and one read from is a derived table with an alias.
    var wellPaid = session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 50);
    var active = session.EntitySet<IEmployee>().Where(e => e.Status == RecordStatus.Active);
    Assert.AreEqual(
      "SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 50) UNION SELECT * FROM Employee t1 WHERE (t1.Status = 0)",
      TextOf(wellPaid.Union(active)));
    var countCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(IEmployee)],
      wellPaid.Concat(active).Expression);
    Assert.AreEqual(
      "SELECT COUNT(*) FROM (SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 50) UNION ALL SELECT * FROM Employee t1 WHERE (t1.Status = 0)) t2",
      QueryTranslator.Translate(ExpressionTreeScanner.Scan(countCall), ExpressionTreeConverter.Convert(countCall), adapter).SqlRoot.AsText);

    // BY NAME: a join on a condition of the caller's own, outer, with a projection over the pair; and a spread of
    // a list alone, whose rows are then the far side's.
    Assert.AreEqual(
      $"SELECT t1.Name AS Name, t0.HiredOn AS HiredOn FROM Employee t0 LEFT JOIN Shop t1 ON (t0.{shopKey} = t1.Id)",
      TextOfByName(QueryMaker.From(adapter, "Employee").Join(QueryMaker.From(adapter, "Shop"),
        (e, s) => e.Member("HomeShop").Member("Id").Equal(s.Member("Id")),
        (e, s) => [s.Member("Name"), e.Member("HiredOn")], outer: true)));
    Assert.AreEqual(
      $"SELECT * FROM Shop t0 JOIN Employee t1 ON (t1.{shopKey} = t0.Id) WHERE (t1.HourlyRate > 10)",
      TextOfByName(QueryMaker.From(adapter, "Shop")
        .SelectMany(s => s.Member("Employees"))
        .Where(e => e.Member("HourlyRate").Greater(QueryMaker.Constant(10m)))));

    // A BUNDLE CANNOT END UP INSIDE A DERIVED TABLE — its rows would be whole rows on an inner select list.
    var paged = session.EntitySet<IEmployee>().OrderBy(e => e.HiredOn).Take(5)
      .Select(e => new { e, rate = e.HourlyRate * 2 }).Where(x => x.rate > 20);
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => TextOf(paged)).Message, "whole rows");
  }

  [TestMethod]
  public void Readers_DescribeTheRootsOutput() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    ILinqDataModel adapter = app.MappedModel();
    var model = app.Model!;
    var session = app.OpenSession();
    TranslatedQuery Typed(IQueryable query) => QueryTranslator.Translate(
      ExpressionTreeScanner.Scan(query.Expression), ExpressionTreeConverter.Convert(query.Expression), adapter);
    string[] StoredOf(ILinqEntity entity) =>
      [.. entity.Members.Where(m => adapter.FindColumn(entity, m) != null).Select(m => m.Name)];
    ILinqEntity employee = model.FindEntity("Employee")!;
    ILinqEntity shop = model.FindEntity("Shop")!;
    var employeeStored = StoredOf(employee);
    var shopStored = StoredOf(shop);

    // ENTITIES OFF A TABLE: the list stays empty — the whole row — and the members are the stored ones, in the
    // model's order, which is what the SQL builder writes for an empty list.
    var entities = Typed(session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10));
    Assert.AreEqual(QueryResultKind.Entities, entities.ResultInfo.Kind);
    Assert.AreSame(employee, entities.ResultInfo.Entity);
    CollectionAssert.AreEqual(employeeStored, entities.ResultInfo.Members.Select(m => m.Name).ToArray());
    Assert.IsTrue(((SelectNode)entities.SqlRoot).SelectList.IsEmpty);

    // ENTITIES THROUGH A DERIVED TABLE that already names a value — the outer filter asked for one column — so every
    // other member is projected through as well, and the outer row is whole again.
    var paged = Typed(session.EntitySet<IEmployee>().OrderBy(e => e.HiredOn).Take(5).Where(e => e.HourlyRate > 10));
    Assert.AreEqual(QueryResultKind.Entities, paged.ResultInfo.Kind);
    var outer = (SelectNode)paged.SqlRoot;
    var inner = ((DerivedRowSetNode)outer.Source).Select;
    Assert.IsTrue(outer.SelectList.IsEmpty);
    Assert.AreEqual("HourlyRate", inner.SelectList.Children[0].Name, "the column the filter asked for came first");
    CollectionAssert.AreEquivalent(employeeStored, inner.SelectList.Children.Select(c => c.Name).ToArray());

    // ROWS OF A PROJECTION, and VALUES where it has one column: the shape's fields are the members.
    var rows = Typed(session.EntitySet<IEmployee>().Select(e => new { e.HiredOn, Shop = e.HomeShop.Name }));
    Assert.AreEqual(QueryResultKind.Rows, rows.ResultInfo.Kind);
    CollectionAssert.AreEqual(new[] { "HiredOn", "Shop" }, rows.ResultInfo.Members.Select(m => m.Name).ToArray());
    var values = Typed(session.EntitySet<IEmployee>().Select(e => e.HourlyRate));
    Assert.AreEqual(QueryResultKind.Values, values.ResultInfo.Kind);
    Assert.AreEqual(typeof(decimal), values.ResultInfo.ScalarType);

    // ONE VALUE.
    var countCall = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(IEmployee)],
      session.EntitySet<IEmployee>().Expression);
    var count = QueryTranslator.Translate(ExpressionTreeScanner.Scan(countCall), ExpressionTreeConverter.Convert(countCall), adapter);
    Assert.AreEqual(QueryResultKind.Scalar, count.ResultInfo.Kind);
    Assert.AreEqual(typeof(int), count.ResultInfo.ScalarType);

    // A BUNDLE AT THE ROOT: rows of the bundle's shape, the list written out slot by slot — two whole rows' columns
    // and a value — and where each slot lies, for the reader that materializes an entity out of each run.
    var pair = Typed(session.EntitySet<IShop>().Where(s => s.TimeZone == "UTC")
      .SelectMany(s => s.Employees, (s, e) => new { Shop = s, Emp = e, Rate = e.HourlyRate * 2 }));
    Assert.AreEqual(QueryResultKind.Rows, pair.ResultInfo.Kind);
    Assert.IsNotNull(pair.ResultInfo.Slots, "a bundle: the result says where each row lies");
    var slots = pair.ResultInfo.Slots!;
    Assert.AreEqual(3, slots.Count);
    Assert.AreEqual(("Shop", "Shop", 0, shopStored.Length), (slots[0].Member.Name, slots[0].Entity!.Name, slots[0].Offset, slots[0].Count));
    Assert.AreEqual(("Emp", "Employee", shopStored.Length, employeeStored.Length), (slots[1].Member.Name, slots[1].Entity!.Name, slots[1].Offset, slots[1].Count));
    Assert.AreEqual(("Rate", shopStored.Length + employeeStored.Length, 1, typeof(decimal)), (slots[2].Member.Name, slots[2].Offset, slots[2].Count, slots[2].ValueType));
    var list = ((SelectNode)pair.SqlRoot).SelectList;
    Assert.AreEqual(pair.ResultInfo.Members.Count, list.ChildCount);
    Assert.AreEqual($"t0.{shopStored[0]} AS Shop_{shopStored[0]}", list.Children[0].AsText);
    Assert.AreEqual("(t1.HourlyRate * 2) AS Rate", list.Children[^1].AsText);

    // A BUNDLE INSIDE A BUNDLE at the root is refused by name — flatten it.
    var nested = session.EntitySet<IShop>()
      .GroupJoin(session.EntitySet<IEmployee>(), s => s.Id, e => e.HomeShop.Id, (s, emps) => new { s, emps })
      .Select(x => new { x, n = 1 });
    StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => Typed(nested)).Message, "projection inside a projection");
  }

  [TestMethod]
  public void PostSqlGrouping_RunsInCSharp() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var adapter = app.MappedModel();
    var model = app.Model!;
    var session = app.OpenSession();
    TranslatedQuery Typed(IQueryable query) => QueryTranslator.Translate(
      ExpressionTreeScanner.Scan(query.Expression), ExpressionTreeConverter.Convert(query.Expression), adapter);
    ILinqEntity employee = model.FindEntity("Employee")!;
    var shopKey = employee.FindMember("HomeShop")!.ReferenceInfo!.FromMembers[0].Name;

    // A GROUPING RETURNED AS GROUPS IS PostSql: the statement returns the filtered rows — entities — and the
    // grouping, the filter over the groups and the ordering run in C# over them, through the PostSql evaluator.
    var grouped = Typed(session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10)
      .GroupBy(e => new { ShopId = e.HomeShop.Id, e.Status })
      .Where(g => g.Count() > 1)
      .OrderByDescending(g => g.Count()));
    Assert.AreEqual(QueryResultKind.Entities, grouped.ResultInfo.Kind, "the statement returns the source rows");
    Assert.AreEqual("SELECT * FROM Employee t0 WHERE (t0.HourlyRate > 10)", grouped.SqlRoot.AsText);
    Assert.IsNotNull(grouped.PostSqlEvaluator);

    // RUN IT the way the executor will: the statement's rows are the run's, read as the host reads its rows.
    var shopA = Guid.NewGuid();
    var shopB = Guid.NewGuid();
    Dictionary<string, object?> Row(Guid shop, RecordStatus status, decimal rate) =>
      new() { [shopKey] = shop, ["Status"] = status, ["HourlyRate"] = rate };
    var rows = new object?[] {
      Row(shopA, RecordStatus.Active, 20), Row(shopA, RecordStatus.Active, 30), Row(shopB, RecordStatus.Active, 40),
      Row(shopA, RecordStatus.Inactive, 50), Row(shopB, RecordStatus.Active, 60), Row(shopB, RecordStatus.Active, 70),
    };
    var callCtx = new TestCallContext([]) {
      Reader = (row, member) => ((Dictionary<string, object?>)row)[member.Name],
      ResultRows = rows,
    };
    var groups = ((IEnumerable<object?>)grouped.PostSqlEvaluator!.Evaluate(callCtx)!).Cast<IGrouping<object?, object?>>().ToList();
    Assert.AreEqual(2, groups.Count, "the group of one — a/Inactive — was filtered out");
    Assert.AreEqual(3, groups[0].Count(), "b/Active first, being the larger group");
    var key = (ILinqRow)groups[0].Key!;
    Assert.AreEqual(shopB, key.GetValue("ShopId"));
    Assert.AreEqual(RecordStatus.Active, key.GetValue("Status"));

    // ONE KEY: the key is the value itself. (By name a grouping is always SQL's — `QueryMaker`'s `GroupBy` says the
    // keys ARE the result — so returning groups is the typed path's alone.)
    var byStatus = Typed(session.EntitySet<IEmployee>().GroupBy(e => e.Status));
    var statusGroups = ((IEnumerable<object?>)byStatus.PostSqlEvaluator!.Evaluate(callCtx)!).Cast<IGrouping<object?, object?>>().ToList();
    CollectionAssert.AreEqual(new object[] { RecordStatus.Active, RecordStatus.Inactive }, statusGroups.Select(g => g.Key).ToArray());
    Assert.AreEqual(5, statusGroups[0].Count());
  }

  /// <summary>Convert a typed query the way the session does — scan first, then the tree — and translate it.</summary>
  static (TranslatedQuery Translation, QueryInfo Scan) TranslateTyped(IQueryable query, ILinqDataModel model) {
    var scan = ExpressionTreeScanner.Scan(query.Expression);
    return (QueryTranslator.Translate(scan, ExpressionTreeConverter.Convert(query.Expression), model), scan);
  }
}

