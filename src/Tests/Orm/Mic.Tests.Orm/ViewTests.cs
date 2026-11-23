using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// A VIEW ENTITY, without a server: how it is declared, what it maps to, what its DDL says, and how an upgrade tells
// a view it made from one it did not.
[TestClass]
public class ViewTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  /// <summary>Defined by a typed query.</summary>
  public interface IRankedPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>Defined by a tree built by name.</summary>
  public interface IPersonRank {
    [PrimaryKey] Guid Id { get; set; }
    int Rank { get; set; }
  }

  /// <summary>Defined by SQL.</summary>
  public interface IPersonName {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>Existing in the database.</summary>
  public interface IOldPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>Without a key.</summary>
  public interface INameRank {
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  const string PersonNameSql = "SELECT \"Id\", \"LastName\" FROM \"app\".\"Person\"";

  static EntityApp NewApp(Action<EntityModule> declare, params Type[] entities) {
    var app = new EntityApp("Views", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entities.Length > 0 ? entities : [typeof(IPerson)]);
    declare(module);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  static void DeclareViews(EntityModule module) {
    module.RegisterView<IRankedPerson>(
      from p in ViewHelper.EntitySet<IPerson>() where p.Rank > 0 select new { p.Id, p.LastName });
    module.RegisterView<IPersonRank>(model =>
      QueryMaker.From(model, "Person")
        .Where(row => row.Member("Rank").GreaterOrEqual(QueryMaker.Constant(1)))
        .Select(row => [row.Member("Id"), row.Member("Rank")]));
    module.RegisterView<IPersonName>(new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = PersonNameSql });
    module.RegisterExistingView<IOldPerson>();
  }

  static DbTableInfo ViewTable(EntityApp app, DbModel model, Type view) =>
    model.GetTable(app.Model!.FindEntity(view)!) ?? throw new AssertFailedException($"{view.Name} has no table");

  [TestMethod]
  public void View_MapsToATableWithoutKeysAndWithItsSelect() {
    var app = NewApp(DeclareViews);
    Assert.IsTrue(app.Initialized, app.Report());
    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());

    // ----- by a typed query: the select, and the columns in the select's order --------------------------------------
    var ranked = ViewTable(app, model, typeof(IRankedPerson));
    Assert.IsTrue(ranked.IsView);
    Assert.AreEqual(0, ranked.Keys.Count, "a view's key is the entity's alone");
    Assert.IsTrue(ranked.View!.IsManaged);
    StringAssert.Contains(ranked.View.Source, "WHERE");
    StringAssert.Contains(ranked.View.Source, "> 0", "the constant is written in, not bound");
    CollectionAssert.AreEqual(new[] { "Id", "LastName" }, ranked.View.ColumnNames!.ToList());

    // ----- by a tree built by name ------------------------------------------------------------------------------
    var byName = ViewTable(app, model, typeof(IPersonRank));
    StringAssert.Contains(byName.View!.Source, ">= 1");
    CollectionAssert.AreEqual(new[] { "Id", "Rank" }, byName.View.ColumnNames!.ToList());

    // ----- by SQL: the text as written, no column list ------------------------------------------------------------
    var bySql = ViewTable(app, model, typeof(IPersonName));
    Assert.AreEqual(PersonNameSql, bySql.View!.Source);
    Assert.IsNull(bySql.View.ColumnNames);

    // ----- existing: nothing to build ----------------------------------------------------------------------------
    var existing = ViewTable(app, model, typeof(IOldPerson));
    Assert.IsFalse(existing.View!.IsManaged);
    Assert.IsNull(existing.View.Source);

    // ----- the DDL: a CREATE VIEW each, with the hash as a comment on the view, since Postgres rewrites the text ---
    var ddl = new DbDdlBuilder(model.Provider).CreateModel(model);
    var create = ddl.Single(s => s.Kind == DdlKind.CreateView && s.ObjectName == "RankedPerson");
    StringAssert.StartsWith(create.Text, "CREATE VIEW \"app\".\"RankedPerson\" (\"Id\", \"LastName\") AS");
    var comment = ddl.Single(s => s.Kind == DdlKind.Comment && s.ObjectName == "RankedPerson");
    StringAssert.StartsWith(comment.Text, "COMMENT ON VIEW \"app\".\"RankedPerson\" IS '" + DefinitionHashHelper.Prefix);
    StringAssert.Contains(ddl.Single(s => s.Kind == DdlKind.CreateView && s.ObjectName == "PersonName").Text,
      "\"PersonName\" AS\n" + PersonNameSql);
    Assert.IsFalse(ddl.Any(s => s.ObjectName == "OldPerson"), "an existing view is never created");
    Assert.IsTrue(ddl.Select(s => s.Kind).SkipWhile(k => k != DdlKind.CreateView).All(k => k is DdlKind.CreateView or DdlKind.Comment),
      "the views come after everything they may read");
  }

  [TestMethod]
  public void View_MayHaveNoKey() {
    static void DeclareView(EntityModule module) => module.RegisterView<INameRank>(
      from p in ViewHelper.EntitySet<IPerson>() select new { p.LastName, p.Rank });

    // ----- a view without a key builds and maps as any view --------------------------------------------------------
    var app = NewApp(DeclareView);
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.IsNull(app.Model!.FindEntity(typeof(INameRank))!.PrimaryKey);
    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var ddl = new DbDdlBuilder(model.Provider).CreateModel(model);
    StringAssert.StartsWith(ddl.Single(s => s.Kind == DdlKind.CreateView && s.ObjectName == "NameRank").Text,
      "CREATE VIEW \"app\".\"NameRank\" (\"LastName\", \"Rank\") AS");

    // ----- cached, it is refused: the cache holds an entity's rows by key -----------------------------------------
    var cached = new EntityApp("Views", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPerson));
    DeclareView(module);
    cached.AddModule(module);
    cached.SetCached(CacheType.Server, [typeof(INameRank)]);
    cached.TryInit();
    Assert.IsFalse(cached.Initialized);
    StringAssert.Contains(cached.Report(), "is cached and has no primary key");
  }

  [TestMethod]
  public void View_BindsASetHeldInALocal() {
    // A sub-query over another set is written over a local, as the engine asks; the local is a field of the
    // closure, and the view's builder binds the set behind it like one written in the query.
    var people = ViewHelper.EntitySet<IPerson>();
    var app = NewApp(module => module.RegisterView<IPersonRank>(
      from p in ViewHelper.EntitySet<IPerson>() select new { p.Id, Rank = people.Count(o => o.Rank > p.Rank) }));
    Assert.IsTrue(app.Initialized, app.Report());
    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var view = ViewTable(app, model, typeof(IPersonRank)).View!;
    StringAssert.Contains(view.Source, "COUNT(");
    CollectionAssert.AreEqual(new[] { "Id", "Rank" }, view.ColumnNames!.ToList());
  }

  [TestMethod]
  public void View_RefusesWhatItCannotHave() {
    // ----- on the entity: a list, an auto value, an index, and a reference pointing at it -------------------------
    var refused = NewApp(module => module.RegisterView<IBadView>(
        from p in ViewHelper.EntitySet<IPerson>() select new { p.Id, p.Rank }),
      typeof(IPerson), typeof(IReferrer));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "list on a view");
    StringAssert.Contains(report, "auto value on a view");
    StringAssert.Contains(report, "index or unique key");
    StringAssert.Contains(report, "cannot be referenced");

    // ----- against a database: no SQL for the server, a value the view has no member for, a parameter ------------
    var noSql = NewApp(module => module.RegisterView<IPersonName>(new Dictionary<DbServerKind, string> { [DbServerKind.MySql] = "SELECT 1" }));
    StringAssert.Contains(MapErrors(noSql), "no SQL is written for Postgres");

    var extra = NewApp(module => module.RegisterView<IPersonName>(
      from p in ViewHelper.EntitySet<IPerson>() select new { p.Id, p.LastName, p.Rank }));
    StringAssert.Contains(MapErrors(extra), "selects 'Rank', which the view has no member for");

    var missing = NewApp(module => module.RegisterView<IPersonSummary>(
      from p in ViewHelper.EntitySet<IPerson>() select new { p.Id, p.LastName }));
    StringAssert.Contains(MapErrors(missing), "selects nothing for 'Rank'");

    var minimum = 3;
    var parameterized = NewApp(module => module.RegisterView<IRankedPerson>(
      from p in ViewHelper.EntitySet<IPerson>() where p.Rank > minimum select new { p.Id, p.LastName }));
    StringAssert.Contains(MapErrors(parameterized), "takes a parameter");

    // ----- in a session: no new row, no delete ----------------------------------------------------------------------
    var app = NewApp(module => module.RegisterView<IRankedPerson>(
      from p in ViewHelper.EntitySet<IPerson>() where p.Rank > 0 select new { p.Id, p.LastName })).WithSqlite();
    var session = app.OpenSession();
    Assert.ThrowsExactly<InvalidOperationException>(() => session.NewEntity<IRankedPerson>());

    // ----- nor a statement over it: refused at translation, before any server ---------------------------------------
    var ranked = session.EntitySet<IRankedPerson>();
    var refusedDelete = Assert.ThrowsExactly<NotSupportedException>(
      () => session.ExecuteDelete<IRankedPerson>(ranked));
    StringAssert.Contains(refusedDelete.Message, "read-only");
    Assert.ThrowsExactly<NotSupportedException>(() =>
      session.ExecuteUpdate<IRankedPerson>(ranked.Select(r => new { LastName = "x" })));
  }

  public interface IBadView {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Index] int Rank { get; set; }
    IList<IPerson> People { get; }
  }

  /// <summary>Three members, for a query that selects two.</summary>
  public interface IPersonSummary {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  public interface IReferrer {
    [PrimaryKey] Guid Id { get; set; }
    IBadView View { get; set; }
  }

  static string MapErrors(EntityApp app) {
    Assert.IsTrue(app.Initialized, app.Report());
    var mapping = new DbModelBuilder(app, app.Model!, new DbSettings(new PostgresProvider(), NoServer)).Build();
    Assert.IsTrue(mapping.BuildReport.HasErrors, "the mapping should have been refused");
    return mapping.BuildReport.Format();
  }

  [TestMethod]
  public void View_IsComparedByTheHashStoredBesideIt() {
    var app = NewApp(DeclareViews);
    var target = app.MapToPostgres();
    var source = NewApp(_ => { }).MapToPostgres();     // the same tables, as a database with no views yet
    var comparer = new DbModelComparer(target.Provider);
    var ranked = ViewTable(app, target, typeof(IRankedPerson));
    var schema = source.GetOrAddSchema("app");

    // ----- the database has no views: the managed ones are added, the existing one is missing --------------------
    var diff = comparer.Compare(source, target);
    Assert.AreEqual(3, diff.OfKind(DbObjectKind.View).Count(d => d.DiffKind == DbDiffKind.Add), diff.Report());
    StringAssert.Contains(string.Join("; ", diff.Errors), "OldPerson");

    // ----- the database has them, each with the hash MIC stored: nothing to do -----------------------------------
    foreach (var view in new[] { typeof(IRankedPerson), typeof(IPersonRank), typeof(IPersonName) }) {
      var table = ViewTable(app, target, view);
      source.AddView(new DbViewInfo(table.Name, schema) { StoredDefinitionHash = DefinitionHashHelper.Of(table.View!.Source!) });
    }
    var old = new DbViewInfo("OldPerson", schema);
    foreach (var column in ViewTable(app, target, typeof(IOldPerson)).Columns)
      old.AddColumn(new DbColumnInfo(column.Name, column.TypeRef));
    source.AddView(old);
    diff = comparer.Compare(source, target);
    Assert.AreEqual(0, diff.OfKind(DbObjectKind.View).Count(), diff.Report());
    Assert.AreEqual(0, diff.Errors.Count, string.Join("; ", diff.Errors));

    // ----- a different hash is a changed definition; a view MIC made and the model dropped goes; a foreign one stays
    var changed = NewApp(_ => { }).MapToPostgres();
    changed.AddView(new DbViewInfo("RankedPerson", changed.GetOrAddSchema("app")) { StoredDefinitionHash = DefinitionHashHelper.Of("something else") });
    changed.AddView(new DbViewInfo("Forgotten", changed.GetOrAddSchema("app")) { StoredDefinitionHash = DefinitionHashHelper.Of("x") });
    changed.AddView(new DbViewInfo("Theirs", changed.GetOrAddSchema("app")));
    diff = comparer.Compare(changed, target);
    var byName = diff.OfKind(DbObjectKind.View).ToDictionary(d => d.Name, d => d.DiffKind);
    Assert.AreEqual(DbDiffKind.Modify, byName["RankedPerson"]);
    Assert.AreEqual(DbDiffKind.Drop, byName["Forgotten"]);
    Assert.IsFalse(byName.ContainsKey("Theirs"), "a view MIC did not make is left alone");

    // ----- the script drops first and creates last --------------------------------------------------------------
    var script = new DbUpgradeScriptBuilder(target.Provider).Build(diff);
    Assert.AreEqual(DdlKind.DropView, script.Steps[0].Ddl!.Kind, script.Steps[0].Text);
    Assert.IsTrue(script.Steps[^1].Ddl!.Kind is DdlKind.CreateView or DdlKind.Comment, script.Steps[^1].Text);
    // The drops run in reverse arrival order, a dependent before the view it reads; the undeclared view, which the
    // comparer lists last, is dropped first.
    Assert.AreEqual("Forgotten", script.Steps[0].Ddl!.ObjectName);
    Assert.AreEqual(ranked.Name, script.Steps[1].Ddl!.ObjectName);
  }

  /// <summary>A view over IRankedPerson.</summary>
  public interface IRankedName {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  [TestMethod]
  public void ViewOverAView_IsOrderedByItsDeclaredDependency() {
    // ----- the dependent registered first comes after the view it reads in every database's tables ---------------
    void Declare(EntityModule module) {
      module.RegisterView<IRankedName>(
        from r in ViewHelper.EntitySet<IRankedPerson>() select new { r.Id, r.LastName },
          dependsOn: [typeof(IRankedPerson)]);
      module.RegisterView<IRankedPerson>(
        from p in ViewHelper.EntitySet<IPerson>() where p.Rank > 0 select new { p.Id, p.LastName });
    }
    var app = NewApp(Declare, typeof(IPerson), typeof(IRankedName), typeof(IRankedPerson));
    Assert.IsTrue(app.Initialized, app.Report());
    var ranked = app.Model!.FindEntity(typeof(IRankedPerson))!;
    CollectionAssert.AreEqual(new[] { ranked }, app.Model.FindEntity(typeof(IRankedName))!.View!.DependsOn.ToList());
    foreach (var provider in new DbProvider[] { new PostgresProvider(), new SqliteProvider() }) {
      var mapping = new DbModelBuilder(app, app.Model, new DbSettings(provider, NoServer)).Build();
      Assert.IsFalse(mapping.BuildReport.HasErrors, mapping.BuildReport.Format());
      var views = mapping.Tables.Where(t => t.IsView).Select(t => t.Entity!.Name).ToList();
      CollectionAssert.AreEqual(new[] { "RankedPerson", "RankedName" }, views, provider.ServerKind.ToString());
      // The tables a query reads are known from its translation: the table, not the view, behind the dependent.
      CollectionAssert.AreEqual(new[] { "Person" },
        ViewTable(app, mapping, typeof(IRankedPerson)).View!.ReadsTables.Select(t => t.Name).ToList());
      Assert.AreEqual(0, ViewTable(app, mapping, typeof(IRankedName)).View!.ReadsTables.Count);
    }

    // ----- an SQL view's tables come from dependsOn, since its text is not read ------------------------------------
    var sql = NewApp(module => module.RegisterView<IPersonName>(
        new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = PersonNameSql }, dependsOn: [typeof(IPerson)]),
      typeof(IPerson), typeof(IPersonName));
    Assert.IsTrue(sql.Initialized, sql.Report());
    var sqlMapping = sql.MapToPostgres();
    CollectionAssert.AreEqual(new[] { "Person" },
      ViewTable(sql, sqlMapping, typeof(IPersonName)).View!.ReadsTables.Select(t => t.Name).ToList());

    // ----- a dependency that is no view, and a cycle, are build errors -------------------------------------------
    var notAView = NewApp(module => module.RegisterView<IRankedPerson>(
        from p in ViewHelper.EntitySet<IPerson>() where p.Rank > 0 select new { p.Id, p.LastName },
        dependsOn: [typeof(string)]),
      typeof(IPerson), typeof(IRankedPerson));
    Assert.IsFalse(notAView.Initialized);
    StringAssert.Contains(notAView.Report(), "depends on 'String', which is not a table or a view");

    var cycle = NewApp(module => {
        module.RegisterView<IRankedName>(
          from r in ViewHelper.EntitySet<IRankedPerson>() select new { r.Id, r.LastName },
            dependsOn: [typeof(IRankedPerson)]);
        module.RegisterView<IRankedPerson>(
          from p in ViewHelper.EntitySet<IPerson>() where p.Rank > 0 select new { p.Id, p.LastName },
          dependsOn: [typeof(IRankedName)]);
      }, typeof(IPerson), typeof(IRankedName), typeof(IRankedPerson));
    Assert.IsFalse(cycle.Initialized);
    StringAssert.Contains(cycle.Report(), "depends on itself");
  }
}
