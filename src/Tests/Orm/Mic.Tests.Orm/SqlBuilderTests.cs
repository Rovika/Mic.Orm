using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using BeautyShop;

namespace Mic.Tests.Orm;

// THE SQL BUILDER — phase 4 of the new translator, in the ORM: the SQL tree written as a statement template for one
// vendor. Asserted against PostgreSQL's dialect, with the names read off the db model rather than spelled here, so
// the assertions say what the STRUCTURE is and would read the same on another vendor's quoting.
[TestClass]
public class SqlBuilderTests {
  static EntityApp? _app;
  static EntitySession? _session;
  static DbModel? _db;

  static (EntityApp App, EntitySession Session, DbModel Db) Setup() {
    if (_app == null) {
      var app = new PlainBeautyShopApp();
      app.CollectingLog();
      app.TryInit();
      AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
      _db = app.MappedModel();
      _session = new EntitySession(new OperationContext(app));
      _app = app;
    }
    return (_app, _session!, _db!);
  }

  static SqlTemplate Build(IQueryable query) {
    var (_, _, db) = Setup();
    var adapter = db;
    var info = ExpressionTreeScanner.Scan(query.Expression);
    return SqlBuilder.Build(QueryTranslator.Translate(info, ExpressionTreeConverter.Convert(query.Expression), adapter), db);
  }

  static SqlTemplate Build(QueryNode byName) {
    var (_, _, db) = Setup();
    return SqlBuilder.Build(QueryTranslator.Translate(QueryTreeScanner.Scan(byName), byName, db), db);
  }

  static SqlTemplate Build(System.Linq.Expressions.Expression call) {
    var (_, _, db) = Setup();
    var info = ExpressionTreeScanner.Scan(call);
    return SqlBuilder.Build(QueryTranslator.Translate(info, ExpressionTreeConverter.Convert(call), db), db);
  }

  /// <summary>The statement on ONE LINE — what these tests compare, so they assert what is written and not how it is
  /// laid out. A line break and its indentation are one space; a break after `(` or before `)` is nothing, which is
  /// what a bracketed block flattens back to.</summary>
  static string Flat(SqlTemplate template) {
    var text = System.Text.RegularExpressions.Regex.Replace(template.ToString(), @"\n *", " ");
    return text.Replace("( ", "(").Replace(" )", ")");
  }

  // ----- the names, off the db model --------------------------------------------------------------------------------

  static EntityTypeInfo Entity(string name) => (EntityTypeInfo)Setup().App.Model!.FindEntity(name)!;
  static DbTableInfo Table(string entity) => Setup().Db.GetTable(Entity(entity))!;
  static string T(string entity) => Table(entity).RefName;
  static string C(string entity, string member) => Table(entity).GetColumn((EntityMemberInfo)Entity(entity).FindMember(member)!)!.QuotedName;

  /// <summary>The whole row of an entity under an alias — every stored member, aliased where the column is called
  /// something else.</summary>
  static string Row(string alias, string entity) {
    var table = Table(entity);
    var columns = new List<string>();
    foreach (var member in Entity(entity).Members) {
      var column = table.GetColumn(member);
      if (column == null)
        continue;
      columns.Add($"{alias}.{column.QuotedName}" + (column.Name == member.Name ? "" : $" AS \"{member.Name}\""));
    }
    return string.Join(", ", columns);
  }

  [TestMethod]
  public void ASelectIsWrittenClauseByClauseWithTheWholeRowJoinsAndAPage() {
    var (_, session, db) = Setup();
    var minRate = 20m;
    var template = Build(session.EntitySet<IEmployee>()
      .Where(e => e.HourlyRate >= minRate && e.HomeShop.Name == "Downtown")
      .OrderByDescending(e => e.HiredOn)
      .Take(20));
    var select = $"SELECT {Row("t0", "Employee")} FROM {T("Employee")} t0 LEFT OUTER JOIN {T("Shop")} t1"
      + $" ON t0.{C("Employee", "HomeShop_Id")} = t1.{C("Shop", "Id")}"
      + $" WHERE (t0.{C("Employee", "HourlyRate")} >= {{p0}} AND t1.{C("Shop", "Name")} = 'Downtown')"
      + $" ORDER BY t0.{C("Employee", "HiredOn")} DESC";
    Assert.AreEqual(db.Provider.SqlDialect.BuildPage(select, null, "20"), Flat(template));
    Assert.AreEqual(QueryResultKind.Entities, template.ResultKind);

    // AND LAID OUT FOR A READER: a clause per line, a join on its own line under the FROM, the page on its own line,
    // a long select list wrapped and indented — with `\n`, the same on every machine.
    var lines = template.ToString().Split('\n');
    Assert.IsTrue(lines.Length >= 6, template.ToString());
    StringAssert.StartsWith(lines[0], "SELECT t0.");
    StringAssert.StartsWith(lines[1], "  t0.", "the wide row wraps onto an indented continuation");
    var from = Array.FindIndex(lines, l => l.StartsWith("  FROM "));
    Assert.IsTrue(from > 0, "the clauses sit two spaces in from the SELECT");
    StringAssert.StartsWith(lines[from + 1], "    LEFT OUTER JOIN ");
    StringAssert.StartsWith(lines[from + 2], "  WHERE ");
    StringAssert.StartsWith(lines[from + 3], "  ORDER BY ");
    StringAssert.StartsWith(lines[from + 4], "  LIMIT ");
    Assert.IsFalse(template.ToString().Contains('\r') || template.ToString().Contains(" \n"), "no CR, no trailing space");
    Assert.AreEqual(1, template.ArgumentCount, "the captured display class");
    Assert.AreEqual(0, template.Placeholders[0].ArgumentIndex);
    Assert.IsNotNull(template.Result);
    CollectionAssert.AreEqual(template.ResultMembers.Select(m => m.Name).ToArray(), template.Result!.Members.Select(m => m.Name).ToArray());
  }

  [TestMethod]
  public void SubSelectsAggregatesAndMatchesAreWrittenAsTheDialectSpellsThem() {
    var (app, session, db) = Setup();
    var model = db;

    // A LIST IS A CORRELATED SUB-SELECT under EXISTS, selecting 1; a count of it a scalar sub-query.
    Assert.AreEqual(
      $"SELECT {Row("t0", "Shop")} FROM {T("Shop")} t0"
      + $" WHERE EXISTS (SELECT 1 FROM {T("Employee")} t1 WHERE t1.{C("Employee", "HomeShop_Id")} = t0.{C("Shop", "Id")} AND t1.{C("Employee", "HourlyRate")} > 50)"
      + $" AND (SELECT COUNT(*) FROM {T("Employee")} t2 WHERE t2.{C("Employee", "HomeShop_Id")} = t0.{C("Shop", "Id")}) > 2",
      Flat(Build(QueryMaker.From(model, "Shop")
        .Where(s => s.Member("Employees").Any(e => e.Member("HourlyRate").Greater(QueryMaker.Constant(50m))))
        .Where(s => s.Member("Employees").Count().Greater(QueryMaker.Constant(2L))))));

    // A MATCH: the wildcards go onto the VALUE — a parameter keeps its value and gains the pattern-building step —
    // and the statement declares the escape.
    var prefix = "Dow";
    var like = Build(session.EntitySet<IEmployee>().Where(e => e.HomeShop.Name.StartsWith(prefix)));
    var escape = db.Provider.SqlDialect.GetLiteral(db.Provider.SqlDialect.LikeEscaping.Escape.ToString());
    StringAssert.EndsWith(Flat(like), $" WHERE (t1.{C("Shop", "Name")} LIKE {{p0}} ESCAPE {escape})");

    // ONE VALUE: a count is the select's list; a question is a condition selected as a value.
    var employees = session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10);
    var count = Build(System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(IEmployee)], employees.Expression));
    Assert.AreEqual($"SELECT COUNT(*) FROM {T("Employee")} t0 WHERE t0.{C("Employee", "HourlyRate")} > 10", Flat(count));
    Assert.AreEqual(QueryResultKind.Scalar, count.ResultKind);
    var any = Build(System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Any), [typeof(IEmployee)], employees.Expression));
    Assert.AreEqual(
      db.Provider.SqlDialect.BuildValueSelect($"CASE WHEN EXISTS (SELECT 1 FROM {T("Employee")} t0 WHERE t0.{C("Employee", "HourlyRate")} > 10) THEN 1 ELSE 0 END"),
      Flat(any));
    Assert.AreEqual(typeof(bool), any.ScalarType);
  }

  [TestMethod]
  public void ProjectionsGroupingsDerivedTablesAndSetOperationsAreWritten() {
    var (_, session, db) = Setup();

    // A GROUPED PROJECTION: the keys in the GROUP BY, the projection as the list, each value named.
    var grouped = Build(session.EntitySet<IEmployee>()
      .GroupBy(e => e.HomeShop.Id)
      .Select(g => new { ShopId = g.Key, N = g.Count() }));
    Assert.AreEqual(
      $"SELECT t0.{C("Employee", "HomeShop_Id")} AS \"ShopId\", COUNT(*) AS \"N\" FROM {T("Employee")} t0 GROUP BY t0.{C("Employee", "HomeShop_Id")}",
      Flat(grouped));
    Assert.AreEqual(QueryResultKind.Rows, grouped.ResultKind);
    CollectionAssert.AreEqual(new[] { "ShopId", "N" }, grouped.ResultMembers.Select(m => m.Name).ToArray());

    // A DERIVED TABLE: the page inside, every member exposed by name, the outer row read by member name.
    var paged = Build(session.EntitySet<IEmployee>().OrderBy(e => e.HiredOn).Take(5).Where(e => e.HourlyRate > 10));
    var inner = db.Provider.SqlDialect.BuildPage(
      $"SELECT t0.{C("Employee", "HourlyRate")} AS \"HourlyRate\"", null, "5");
    StringAssert.StartsWith(Flat(paged), "SELECT t1.\"");
    StringAssert.Contains(Flat(paged), $" FROM (SELECT t0.{C("Employee", "HourlyRate")} AS \"HourlyRate\", t0.");
    StringAssert.EndsWith(Flat(paged), $") t1 WHERE t1.\"HourlyRate\" > 10");
    Assert.AreEqual(QueryResultKind.Entities, paged.ResultKind);

    // A SET OPERATION, the dialect's word between two whole selects.
    var union = Build(session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 50)
      .Union(session.EntitySet<IEmployee>().Where(e => e.Status == RecordStatus.Active)));
    Assert.AreEqual(
      $"SELECT {Row("t0", "Employee")} FROM {T("Employee")} t0 WHERE t0.{C("Employee", "HourlyRate")} > 50"
      + $" UNION SELECT {Row("t1", "Employee")} FROM {T("Employee")} t1 WHERE t1.{C("Employee", "Status")} = 0",
      Flat(union));

    // A BUNDLE AT THE ROOT: two rows' columns side by side, and the statement carries the slots for the reader.
    var pair = Build(session.EntitySet<IShop>().SelectMany(s => s.Employees, (s, e) => new { Shop = s, Emp = e }));
    StringAssert.StartsWith(Flat(pair), "SELECT t0.");
    StringAssert.Contains(Flat(pair), $"t0.{C("Shop", "Id")} AS \"Shop_Id\", ");
    StringAssert.Contains(Flat(pair), $", t1.{C("Employee", "Id")} AS \"Emp_Id\", ");
    StringAssert.Contains(Flat(pair), $" FROM {T("Shop")} t0 INNER JOIN {T("Employee")} t1 ON t1.{C("Employee", "HomeShop_Id")} = t0.{C("Shop", "Id")}");
    Assert.AreEqual(2, pair.Result!.Slots!.Count);
    Assert.AreEqual(pair.Result.Members.Count, pair.Result.Slots[1].Offset + pair.Result.Slots[1].Count);
  }
}
