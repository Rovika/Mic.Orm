using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.DbInfo;
using Mic.Orm.Logging;
using Mic.Orm.Model.Construction;

namespace Mic.Tests.Orm;

// The seam: declaration → metadata. These tests drive it the way an application does — a real EntityApp with real
// modules and decorated interfaces — rather than by calling passes directly, because the path through the app is the
// one that has to work.
[TestClass]
public class ModelBuilderTests {
  // Every entity needs a primary key and at least one member — the validation pass says so, and these exist to be
  // built successfully.
  private interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
  }

  private interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
  }

  [Entity(Name = "Employee")]
  private interface IStaffMember {
    [PrimaryKey] Guid Id { get; set; }
  }

  private class NotAnInterface { }

  private sealed class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) {
      RegisterEntities(typeof(IPerson), typeof(IAddress));
    }
  }

  private sealed class HrModule : EntityModule {
    public HrModule() : base("HR", new Version(2, 1)) {
      RegisterEntities(typeof(IStaffMember));
    }
  }

  private static EntityApp BuiltApp(Action<EntityApp>? configure = null) {
    var app = new EntityApp("TestApp", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new PeopleModule(), "people");
    app.AddModule(new HrModule());
    configure?.Invoke(app);
    app.TryInit();
    return app;
  }

  // ----- ONE application, everything the build produces ------------------------------------------------------------------
  //
  // Seven methods, each rebuilding the same two modules to ask one question of the result.
  [TestMethod]
  public void Build_ProducesNamedEntitiesInResolvedSchemas() {
    var app = BuiltApp();
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.IsNotNull(app.Model);
    var model = app.Model!;
    Assert.AreEqual("TestApp", model.Name);
    Assert.AreEqual(new Version(1, 0), model.Version);

    // ----- the modules carry their VERSIONS into it ------------------------------------------------------------------
    Assert.AreEqual(2, model.Modules.Count(m => m.Name != DbInfoModule.ModuleName));
    Assert.AreEqual(new Version(2, 1), model.FindModule("HR")!.Version);

    // ----- schemas are MATERIALISED and shared by name ---------------------------------------------------------------
    //
    // A string when declared, an EntitySchemaInfo once built — and the app's default fills in for a module that named
    // none, which is what lets a reusable module leave the decision to the application. Asked of the ENTITIES, because
    // that is who carries the resolved object.
    Assert.AreEqual("people", model.FindEntity("Person")!.Schema!.Name);
    Assert.AreEqual("app", model.FindEntity("Employee")!.Schema!.Name);
    Assert.AreEqual(2, model.Schemas.Count);
    Assert.AreSame(model.FindSchema("people"), model.FindEntity("Person")!.Schema);

    // ----- entity names drop the interface prefix, unless one was written outright -------------------------------------
    Assert.IsNotNull(model.FindEntity("Person"));
    Assert.IsNotNull(model.FindEntity("Address"));
    Assert.IsNull(model.FindEntity("IPerson"));
    Assert.IsNotNull(model.FindEntity("Employee"));
    Assert.IsNull(model.FindEntity("StaffMember"));

    // ----- and each entity keeps its interface and its module ------------------------------------------------------------
    var person = model.FindEntity("Person")!;
    Assert.AreEqual(typeof(IPerson), person.InterfaceType);
    Assert.AreEqual("People", person.Module.Name);

    // ----- while MIC's own bookkeeping entities are added automatically ----------------------------------------------------
    //
    // Forgetting the module would mean versions silently going untracked — the kind of omission nobody notices until it
    // matters.
    Assert.IsNotNull(app.FindModule(DbInfoModule.ModuleName));
    Assert.IsTrue(model.Entities.Any(e => e.InterfaceType == typeof(IDbInfo)));
  }

  /// <summary>One schema, several modules, one line.</summary>
  /// <remarks>The layout is the APPLICATION's decision — a reusable module leaves its schema alone — so an application
  /// that groups modules is usually putting several in one place. Assigning after `AddModule` also has to work: the
  /// app may reconfigure before `Build`.</remarks>
  [TestMethod]
  public void SeveralModules_CanShareOneSchema() {
    var model = BuiltApp(app => app.AssignSchema("shared", app.FindModule("People")!, app.FindModule("HR")!)).Model!;

    Assert.AreEqual("shared", model.FindEntity("Person")!.Schema!.Name);
    Assert.AreEqual("shared", model.FindEntity("Employee")!.Schema!.Name,
      "the module that named no schema of its own took the assignment too, not the app default");
  }

  [TestMethod]
  public void Settings_AreTheApplicationsToTurn() {
    // The naming policy and the bookkeeping module: two dials, each its own build because that is what changing a
    // setting means.
    Assert.IsNotNull(BuiltApp(app => app.Settings.NamingPolicy = new PrefixingPolicy()).Model!.FindEntity("tbl_Person"));

    var without = new EntityApp("TestApp", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    without.AddModule(new PeopleModule(), "people");
    without.Settings.IncludeDbInfoModule = false;
    without.TryInit();
    Assert.IsNull(without.FindModule(DbInfoModule.ModuleName));
  }

  private sealed class PrefixingPolicy : NamingPolicy {
    public override string GetEntityName(Type entityInterface) => "tbl_" + base.GetEntityName(entityInterface);
  }

  [TestMethod]
  public void Registration_ThatCannotBecomeAnEntityIsReported() {
    // ----- a type that is not an interface --------------------------------------------------------------------------
    //
    // The shape check the declaration tier deliberately did not make: registration RECORDS a type, and deciding whether
    // it can be an entity belongs where it can be said with a location and a reason.
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(NotAnInterface));
    app.AddModule(module, "s");

    app.TryInit();                                   // no exception

    Assert.IsFalse(app.Initialized);
    Assert.IsTrue(app.Diagnostics().Any(d =>
      d.Severity == Severity.Error && d.Message.Contains("interface")));
    Assert.IsTrue(app.Diagnostics().Any(d => d.Location!.Contains("NotAnInterface")));

    // ----- and EVERY fault is collected, rather than the first -------------------------------------------------------
    //
    // The reason diagnostics are collected rather than thrown: one run reports everything wrong. A developer fixing
    // several mistakes wants one list, not one run per mistake.
    var several = new EntityApp("App", new Version(1, 0));
    var many = new EntityModule("M", new Version(1, 0));
    many.RegisterEntities(typeof(NotAnInterface), typeof(int), typeof(string));
    several.AddModule(many, "s");
    several.TryInit();

    Assert.AreEqual(3, several.Diagnostics().Count(d => d.Severity == Severity.Error));

    // ----- as is the same entity NAME arriving twice ------------------------------------------------------------------
    var clashing = new EntityApp("App", new Version(1, 0));
    var moduleA = new EntityModule("A", new Version(1, 0));
    moduleA.RegisterEntities(typeof(IPerson));
    var moduleB = new EntityModule("B", new Version(1, 0));
    moduleB.RegisterEntities(typeof(IPerson));
    clashing.AddModule(moduleA, "s");
    clashing.AddModule(moduleB, "s");
    clashing.TryInit();

    Assert.IsFalse(clashing.Initialized);
    Assert.IsTrue(clashing.Diagnostics().Any(d => d.Message.Contains("named 'Person'")));
  }

  [OldNames("IWorker,IStaff")]
  private interface IEmployee {
    [PrimaryKey] Guid Id { get; set; }
  }

  [OldNames("")]
  private interface IBadOldNames {
    [PrimaryKey] Guid Id { get; set; }
  }

  private static EntityApp BuildWith(params Type[] entityTypes) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entityTypes);
    app.AddModule(module, "s");
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void Attribute_AppliesItselfInItsOwnPhase() {
    // The mechanism end to end: the builder runs three times over the attributes and OldNames acts on the Early one.
    // Nothing in the builder mentions OldNames — the attribute carries its own behaviour in its other half.
    var app = BuildWith(typeof(IEmployee), typeof(IPerson));
    Assert.IsTrue(app.Initialized, app.Report());
    CollectionAssert.AreEqual(new[] { "IWorker", "IStaff" }, app.Model!.FindEntity("Employee")!.OldNames.ToArray());
    Assert.AreEqual(0, app.Model.FindEntity("Person")!.OldNames.Count, "and one that carries none has an empty list");

    // ----- and an attribute's OWN diagnostics reach the same log -------------------------------------------------------
    //
    // The attribute parses its member list and hands the findings to the context — so a fault inside an attribute is
    // reported the same way as one the builder found, rather than throwing out of the middle of a build.
    var bad = BuildWith(typeof(IBadOldNames));
    Assert.IsFalse(bad.Initialized);
    Assert.IsTrue(bad.Diagnostics().Any(d => d.Severity == Severity.Error));
  }

  [TestMethod]
  public async Task Connecting_RefusesBeforeBuildAndAfterAFailedOne() {
    // A broken model is a readable report, not an exception from somewhere inside startup — and connecting on one is
    // refused rather than attempted.
    var settings = new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=x");
    var app = new EntityApp("App", new Version(1, 0));
    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => app.ConnectToAsync(settings));

    var broken = new EntityModule("M", new Version(1, 0));
    broken.RegisterEntities(typeof(NotAnInterface));
    app.AddModule(broken, "s");
    app.TryInit();
    // AND IT FAILS WITH THE REASON IT DID NOT INITIALISE, not with "not initialised" — which would tell a caller
    // only that they should have looked somewhere else.
    var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => app.ConnectToAsync(settings));
    StringAssert.Contains(ex.Message, "did not initialise");
    StringAssert.Contains(ex.Message, "interface", "the model fault itself, not a pointer to it");
  }

}
