using Mic.Linq.ExpressionTree;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sql;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Runtime;
using BeautyShop;

namespace Mic.Tests.Orm;

// The Beauty Shop sample model, checked the only way that matters before a database exists: it must BUILD.
[TestClass]
public class BeautyShopTests {
  [TestMethod]
  public void BeautyShopModel_BuildsAndMapsToPostgres() {
    var app = new BeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.IsFalse(app.Model!.BuildReport.HasErrors, app.Report());

    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    Assert.IsTrue(app.HasDatabase(), app.Report());
    var db = app.MappedModel();
    Assert.IsFalse(db.BuildReport.HasErrors, app.Report());

    // 32 of the model's own, the ORM's tenant among them, three views, the function's rows, plus MIC's `DbInfo` —
    // every application carries that one, and counting it here rather than subtracting it is what keeps the number
    // honest.
    Assert.AreEqual(37, app.Model.Entities.Count, app.Report());
    // 33 tables in the schema: the model's 32 plus MIC's own `DbInfo`, which lands here rather than in `public` because
    // `beauty` is the application's DEFAULT schema and not merely the module's. Then the views and the function.
    var inSchema = db.Tables.Where(t => t.Schema.Name == "beauty").ToList();
    Assert.AreEqual(33, inSchema.Count(t => !t.IsView && !t.IsTableFunction));
    Assert.AreEqual(3, inSchema.Count(t => t.IsView));
    Assert.IsTrue(inSchema.Single(t => t.Name == "mv_LowStock").View!.IsMaterialized);
    Assert.AreEqual(1, inSchema.Count(t => t.IsTableFunction));

    // EIGHT CLUSTERED DESIGNATIONS, which the model records and `PgHelper.ApplyClusteringAsync` issues. The
    // comparer ignores them on a marker-provider, so an upgrade converges whether or not they have been applied.
    Assert.AreEqual(8, db.Tables.SelectMany(t => t.Keys).Count(k => k.Flags.HasFlag(KeyFlags.Clustered)));
    Assert.IsTrue(db.Schemas.Any(s => s.Name == "beauty"));

    // The shapes this model exists to exercise, each asserted once — a self-reference, a nullable reference to a
    // DIFFERENT table, a two-column unique key, and a filtered index.
    var employee = db.FindTable("beauty", "Employee")!;
    Assert.IsTrue(employee.RefConstraints.Any(c => c.ToTable == employee), "the manager self-reference");

    var user = db.FindTable("beauty", "User")!;
    Assert.IsTrue(user.GetColumn("Company_Id").IsNullable, "a super-admin belongs to no company — from the `?` alone");
    Assert.IsFalse(user.GetColumn("Tenant_Id").IsNullable, "…but to a tenant, as every row does");
    Assert.IsTrue(db.FindTable("beauty", "Employee")!.GetColumn("User_Id").IsNullable, "a junior may never sign in");

    var link = db.FindTable("beauty", "ShopEmployee")!;
    CollectionAssert.AreEqual(new[] { "Shop_Id", "Employee_Id" },
      link.Keys.Single(k => k.IsPrimaryKey).Columns.Select(c => c.Column.Name).ToArray(), "the pair is the key");
    Assert.AreEqual(1, link.Keys.Count(k => k.IsPrimaryKey || k.IsUnique), "and the only unique key");

    var stock = db.FindTable("beauty", "StockLevel")!;
    Assert.IsTrue(stock.Keys.Any(k => k.IsUnique && k.Columns.Count == 2), "one row per location per product");

    var appointment = db.FindTable("beauty", "Appointment")!;
    Assert.IsTrue(appointment.Keys.Any(k => k.Filter != null), "the diary is read by what is still coming");
  }

  [TestMethod]
  public void BeautyShopModel_FiltersEveryEntityByTenant() {
    var app = new BeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var db = app.MappedModel();
    Assert.IsFalse(db.BuildReport.HasErrors, app.Report());

    // Every entity with a tenant member carries the extension's filter: the tables through `Tenant`, the views through
    // their `[TenantId]` column. The tenant table, the function's rows and MIC's own `DbInfo` have none, and no view is
    // left unbound, so the build report carries no warning.
    var model = app.Model!;
    Assert.IsFalse(model.BuildReport.HasWarnings, app.Report());
    var unbound = model.Entities.Where(e => e.Filters.Count == 0).Select(e => e.Name).Order().ToList();
    Assert.AreEqual(3, unbound.Count, string.Join(", ", unbound));
    CollectionAssert.Contains(unbound, "Tenant");
    CollectionAssert.Contains(unbound, "ShopDaySlot");
    Assert.IsTrue(model.Entities.Where(e => e.Filters.Count > 0)
      .All(e => e.Filters.Single().Owner == typeof(MultiTenantExtension)));
    Assert.AreEqual(1, model.FindEntity("Company")!.Filters.Count, "the company is a tenant's record like any other");
    Assert.AreEqual(1, model.FindEntity("UpcomingAppointment")!.Filters.Count, "a view, through its [TenantId] column");

    // The filter in a statement: the tenant's key column against the context's tenant. A view's select stays free of
    // it: one view for every tenant.
    var session = new EntitySession(new OperationContext(app));
    var query = session.EntitySet<IAppointment>();
    var sql = SqlBuilder.Build(QueryTranslator.Translate(ExpressionTreeScanner.Scan(query.Expression),
      ExpressionTreeConverter.Convert(query.Expression), db), db).ToString();
    StringAssert.Contains(sql, "\"Tenant_Id\" = ", sql);
    var plain = new PlainBeautyShopApp();
    plain.CollectingLog();
    plain.TryInit();
    AppInitHelper.CreateDatabase(plain, new DbSettings(new PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var plainDb = plain.MappedModel();
    foreach (var view in db.Tables.Where(t => t.IsView))
      Assert.AreEqual(plainDb.FindTable(view.Schema.Name, view.Name)!.View!.Source, view.View!.Source, view.Name);
  }
}
