using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// A TEMP TABLE, without a server: how it is declared, what it maps to, what its DDL says, what is refused, and that
// an upgrade never sees it.
[TestClass]
public class TempTableTests {
  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  public interface ITempIds {
    [PrimaryKey] Guid Id { get; set; }
    int? Rank { get; set; }
  }

  static EntityApp NewApp(Action<EntityModule> declare, params Type[] entities) {
    var app = new EntityApp("Temp", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entities.Length > 0 ? entities : [typeof(IPerson)]);
    declare(module);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void TempTable_MapsToAnUnqualifiedTableOutsideTheUpgrade() {
    var app = NewApp(module => module.RegisterTempTable<ITempIds>());
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(ITempIds))!;
    Assert.IsTrue(entity.IsTempTable);
    Assert.IsTrue(entity.IsReadOnly);

    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var table = model.GetTable(entity)!;
    Assert.IsTrue(table.IsTemporary);
    Assert.AreEqual("\"TempIds\"", table.RefName, "referred to by its bare name: the session's, not a schema's");
    Assert.AreEqual(0, table.Keys.Count, "the key is the entity's alone");

    var ddl = new DbDdlBuilder(model.Provider);
    Assert.IsFalse(ddl.CreateModel(model).Any(s => s.ObjectName == "TempIds"), "never part of a model's DDL");
    var create = ddl.CreateTableScript(table).Single();
    StringAssert.StartsWith(create.Text, "CREATE TEMP TABLE \"TempIds\" (");
    StringAssert.Contains(create.Text, "\"Id\" uuid NOT NULL");
    StringAssert.Contains(create.Text, "\"Rank\" integer");
    StringAssert.StartsWith(ddl.DropTempTable(table).Text, "DROP TABLE \"TempIds\"");

    // ----- an upgrade compares nothing about it -----------------------------------------------------------------
    var database = NewApp(_ => { }).MapToPostgres();
    var diff = new DbModelComparer(model.Provider).Compare(database, model);
    Assert.AreEqual(0, diff.Items.Count, diff.Report());

    // ----- in a session: read-only, and only a declared temp table can be filled ---------------------------------
    var session = app.WithSqlite().OpenSession();
    Assert.ThrowsExactly<InvalidOperationException>(() => session.NewEntity<ITempIds>());
    Assert.ThrowsExactly<ArgumentException>(() => session.CreateTempTable<IPerson>([]));
  }

  [TestMethod]
  public void TempTable_RefusesWhatItCannotHold() {
    var refused = NewApp(module => module.RegisterTempTable<IBadTemp>(), typeof(IPerson), typeof(IReferrer));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "is a reference on a temp table");
    StringAssert.Contains(report, "list on a temp table");
    StringAssert.Contains(report, "index or unique key");
    StringAssert.Contains(report, "cannot be referenced");
  }

  public interface IBadTemp {
    [PrimaryKey] Guid Id { get; set; }
    IPerson Person { get; set; }
    [Index] int Rank { get; set; }
    IList<IPerson> People { get; }
  }

  public interface IReferrer {
    [PrimaryKey] Guid Id { get; set; }
    IBadTemp Temp { get; set; }
  }
}
