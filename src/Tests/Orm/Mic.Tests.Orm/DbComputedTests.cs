using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Tests.Orm;

// A MEMBER THE DATABASE COMPUTES from a SQL expression written per server, without a server: what it maps to, what
// its DDL says, and what is refused.
[TestClass]
public class DbComputedTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  public interface ILine {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
    int Qty { get; set; }
    [DbComputed(Stored = true)]
    [SqlExpression(DbServerKind.Postgres, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.MySql, "`Price` * `Qty`")]
    int Total { get; }
  }

  static EntityApp NewApp(Type entity) {
    var app = new EntityApp("Computed", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entity);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void DbComputed_MapsToAComputedColumnOverTheServersExpression() {
    var app = NewApp(typeof(ILine));
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(ILine))!;
    var total = entity.GetMember("Total");
    Assert.IsTrue(total.IsDbGenerated);
    Assert.AreEqual(2, total.DbComputed!.SqlByServer.Count);

    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var table = model.GetTable(entity)!;
    var column = table.GetColumn(total)!;
    Assert.IsTrue(column.IsComputed);
    Assert.IsTrue(column.IsComputedStored);
    Assert.AreEqual("\"Price\" * \"Qty\"", column.ComputedExpression, "the placeholders are the columns, quoted as the server quotes");
    Assert.IsFalse(table.InsertColumns.Contains(column));
    StringAssert.Contains(new DbDdlBuilder(model.Provider).CreateTable(table).Text,
      "\"Total\" integer GENERATED ALWAYS AS (\"Price\" * \"Qty\") STORED");
  }

  [TestMethod]
  public void DbComputed_RefusesWhatItCannotCompute() {
    var noExpression = NewApp(typeof(INoExpression));
    Assert.IsFalse(noExpression.Initialized);
    StringAssert.Contains(noExpression.Report(), "needs the expression written with [SqlExpression");

    var stray = NewApp(typeof(IStrayExpression));
    Assert.IsFalse(stray.Initialized);
    StringAssert.Contains(stray.Report(), "is not [DbComputed]");

    // Against a database: no expression for its server, and a placeholder naming no member.
    var otherServer = NewApp(typeof(IOtherServer));
    Assert.IsTrue(otherServer.Initialized, otherServer.Report());
    var mapping = new Mic.Orm.Db.Model.Construction.DbModelBuilder(otherServer, otherServer.Model!, new Mic.Orm.Db.DbSettings(new Mic.Orm.Db.Postgres.PostgresProvider(), NoServer)).Build();
    StringAssert.Contains(mapping.BuildReport.Format(), "no [SqlExpression] is written for Postgres");

    var unknown = NewApp(typeof(IUnknownMember));
    Assert.IsTrue(unknown.Initialized, unknown.Report());
    mapping = new Mic.Orm.Db.Model.Construction.DbModelBuilder(unknown, unknown.Model!, new Mic.Orm.Db.DbSettings(new Mic.Orm.Db.Postgres.PostgresProvider(), NoServer)).Build();
    StringAssert.Contains(mapping.BuildReport.Format(), "names '{Cost}' in its expression");
  }

  public interface INoExpression {
    [PrimaryKey] Guid Id { get; set; }
    [DbComputed] int Total { get; }
  }

  public interface IStrayExpression {
    [PrimaryKey] Guid Id { get; set; }
    [SqlExpression(DbServerKind.Postgres, "1")] int Total { get; }
  }

  public interface IOtherServer {
    [PrimaryKey] Guid Id { get; set; }
    [DbComputed, SqlExpression(DbServerKind.MySql, "1")] int Total { get; }
  }

  public interface IUnknownMember {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
    [DbComputed, SqlExpression(DbServerKind.Postgres, "{Price} * {Cost}")] int Total { get; }
  }
}
