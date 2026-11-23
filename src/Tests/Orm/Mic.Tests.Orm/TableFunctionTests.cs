using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// A TABLE FUNCTION, without a server: how it is declared, what it maps to, what its DDL says, that its key is
// optional, and what is refused.
[TestClass]
public class TableFunctionTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  /// <summary>Keyed rows of a function with a parameter.</summary>
  public interface IPersonAbove {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>Keyless rows of a function with no parameters.</summary>
  public interface IRankCount {
    int Rank { get; set; }
    long Count { get; set; }
  }

  /// <summary>Rows of a function that exists in the database.</summary>
  public interface IOldRows {
    [PrimaryKey] Guid Id { get; set; }
  }

  const string AboveSql = "SELECT \"Id\", \"LastName\" FROM \"app\".\"Person\" WHERE \"Rank\" >= min_rank";
  const string CountsSql = "SELECT \"Rank\", COUNT(*) AS \"Count\" FROM \"app\".\"Person\" GROUP BY \"Rank\"";

  static EntityApp NewApp(Action<EntityModule> declare, params Type[] entities) {
    var app = new EntityApp("Functions", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entities.Length > 0 ? entities : [typeof(IPerson)]);
    declare(module);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  static void DeclareFunctions(EntityModule module) {
    module.RegisterTableFunction<IPersonAbove>("PersonAbove",
      new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = AboveSql }, ("min_rank", typeof(int)));
    module.RegisterTableFunction<IRankCount>("RankCounts",
      new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = CountsSql });
    module.RegisterExistingTableFunction<IOldRows>("OldRows", ("since", typeof(DateTime)));
  }

  [TestMethod]
  public void TableFunction_MapsToAFunctionOfItsNameWithTheRowsAsColumns() {
    var app = NewApp(DeclareFunctions);
    Assert.IsTrue(app.Initialized, app.Report());
    var above = app.Model!.FindEntity(typeof(IPersonAbove))!;
    var counts = app.Model.FindEntity(typeof(IRankCount))!;
    Assert.IsTrue(above.IsTableFunction);
    Assert.IsTrue(above.IsReadOnly);
    Assert.IsNull(counts.PrimaryKey, "a function's rows need no identity");
    Assert.AreEqual("a table function", counts.ReadOnlyKindName);

    var mapping = new DbModelBuilder(app, app.Model, new DbSettings(new PostgresProvider(), NoServer)).Build();
    Assert.IsFalse(mapping.BuildReport.HasErrors, mapping.BuildReport.Format());
    var model = app.MapToPostgres();
    var table = model.GetTable(above)!;
    Assert.AreEqual("PersonAbove", table.Name, "named as the function, not by the policy");
    Assert.AreEqual(0, table.Keys.Count);
    Assert.AreEqual("min_rank", table.TableFunction!.Parameters.Single().Name);
    Assert.AreEqual("integer", table.TableFunction.Parameters.Single().TypeSpec);
    Assert.AreEqual(AboveSql, table.TableFunction.Body);

    // ----- the DDL: created last, replaced in place, the hash in the body ------------------------------------------
    var ddl = new DbDdlBuilder(model.Provider).CreateModel(model);
    var creates = ddl.Where(s => s.Kind == DdlKind.CreateFunction).ToList();
    Assert.AreEqual(2, creates.Count, "the existing one is never created");
    var create = creates.Single(s => s.ObjectName == "PersonAbove").Text;
    StringAssert.StartsWith(create, "CREATE OR REPLACE FUNCTION \"app\".\"PersonAbove\"(min_rank integer) RETURNS TABLE(\"Id\" uuid, \"LastName\" varchar(50)) LANGUAGE sql STABLE AS $mic$");
    StringAssert.Contains(create, "/* mic:h=");
    StringAssert.Contains(create, AboveSql);
    Assert.IsTrue(ddl.ToList().IndexOf(creates[0]) > ddl.Count - 3, "after every table");
    Assert.AreEqual(0, new DbDdlBuilder(model.Provider).CreateTableScript(model.GetTable(app.Model.FindEntity(typeof(IOldRows))!)!).Count);

    // ----- an upgrade: nothing to compare against a database with no routines read ---------------------------------
    var database = NewApp(_ => { }).MapToPostgres();
    var diff = new DbModelComparer(model.Provider).Compare(database, model);
    Assert.AreEqual(2, diff.Items.Count(c => c.ObjectKind == DbObjectKind.TableFunction && c.DiffKind == DbDiffKind.Add), diff.Report());
    Assert.IsTrue(diff.Errors.Any(e => e.Contains("OldRows")), "an existing function the database lacks is reported");

    // ----- where the server has no functions: a warning, and the call refused in the session --------------------
    var sqlite = NewApp(DeclareFunctions);
    var sqliteMapping = new DbModelBuilder(sqlite, sqlite.Model!, new DbSettings(new SqliteProvider(), NoServer)).Build();
    Assert.IsFalse(sqliteMapping.BuildReport.HasErrors, sqliteMapping.BuildReport.Format());
    StringAssert.Contains(sqliteMapping.BuildReport.Format(), "no table-valued functions");
    AppInitHelper.CreateDatabase(sqlite, new DbSettings(new SqliteProvider(), "Data Source=:memory:"));
    var session = sqlite.OpenSession();
    Assert.ThrowsExactly<InvalidOperationException>(() => session.NewEntity<IPersonAbove>());
    Assert.ThrowsExactly<ArgumentException>(() => session.TableFunction<IPersonAbove>(), "one argument wanted");
    Assert.ThrowsExactly<ArgumentException>(() => session.TableFunction<IPerson>(), "not a function's rows");
  }

  [TestMethod]
  public void TableFunction_RefusesWhatItCannotBe() {
    var refused = NewApp(module => {
      module.RegisterTableFunction<IBadRows>("Bad", new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = CountsSql });
      module.RegisterTableFunction<IPersonAbove>("PersonAbove", new Dictionary<DbServerKind, string> { [DbServerKind.MsSql] = AboveSql });
      module.RegisterView<IRankCount>(new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = CountsSql });
      module.RegisterTableFunction<IRankCount>("RankCounts", new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = CountsSql });
    }, typeof(IPerson), typeof(IBadRows));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "is a list on a table function");
    StringAssert.Contains(report, "both as a view and as a table function");

    // A function with no body for the server: the db model's build reports it.
    var app = NewApp(module => module.RegisterTableFunction<IPersonAbove>("PersonAbove",
      new Dictionary<DbServerKind, string> { [DbServerKind.MsSql] = AboveSql }, ("min_rank", typeof(int))));
    Assert.IsTrue(app.Initialized, app.Report());
    var mapping = new DbModelBuilder(app, app.Model!, new DbSettings(new PostgresProvider(), NoServer)).Build();
    StringAssert.Contains(mapping.BuildReport.Format(), "no body written for Postgres");
  }

  public interface IBadRows {
    int Rank { get; set; }
    IList<IPerson> People { get; }
  }
}
