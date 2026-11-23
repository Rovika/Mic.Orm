using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Logging;
using Mic.Orm.Model.Construction;

namespace Mic.Tests.Orm;

// The checks that need the WHOLE model to exist. The builder can only see what is in front of it: whether an entity
// ended up with a primary key, or whether a module's declared dependency is actually in the application, is not
// knowable until everything has been built.
//
// Each fixture here is wrong in exactly one way, so a failure names its cause.
[TestClass]
public class ModelValidatorTests {
  private interface IGoodRow {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  private static EntityApp Build(params Type[] types) => Build(null, types);

  private static EntityApp Build(Action<EntityApp>? configure, params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module, "s");
    configure?.Invoke(app);
    app.TryInit();
    return app;
  }

  private static bool Reports(EntityApp app, string fragment) =>
    app.Diagnostics().Any(d => d.Severity == Severity.Error && d.Message.Contains(fragment));

  private interface IKeylessRow {
    string Name { get; set; }
  }

  private interface INullableKey {
    [PrimaryKey][Nullable] Guid Id { get; set; }
  }

  private interface IOptionalRef {
    [PrimaryKey] Guid Id { get; set; }
    IGoodRow? Maybe { get; set; }
  }

  private interface IEmptyRow { }

  private interface ITwoKeys {
    [PrimaryKey] Guid Id { get; set; }
    [Index] string A { get; set; }
    [Index] string B { get; set; }
  }

  // ----- ONE method per RULE the validator enforces ---------------------------------------------------------------------
  //
  // Seven methods, each building one deliberately-wrong fixture and asserting one message. The FIXTURES stay one-fault
  // each — that is what makes a failure name its cause — so what merged is the assertions, not the models.
  [TestMethod]
  public void EveryEntity_NeedsMembersAndAnIdentifyingKey() {
    // ----- a sound model passes cleanly -----------------------------------------------------------------------------
    var good = Build(typeof(IGoodRow));
    Assert.IsTrue(good.Initialized, good.Report());

    // ----- a KEYLESS entity is an error -----------------------------------------------------------------------------
    //
    // Required whatever the vendor permits: the tracking map is keyed by identity, a stub carries it and nothing else,
    // smart loading fetches by it, and update and delete find their row by it.
    var keyless = Build(typeof(IKeylessRow));
    Assert.IsFalse(keyless.Initialized);
    Assert.IsTrue(Reports(keyless, "has no primary key"));

    // …and what an error MEANS: the model still built and can be inspected, but the application will not RUN on it.
    Assert.IsNotNull(keyless.Model);
    Assert.ThrowsExactly<InvalidOperationException>(() => keyless.ConnectToAsync(
      new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=x")).GetAwaiter().GetResult());

    // ----- a NULLABLE primary-key member is an error ------------------------------------------------------------------
    //
    // NULL is not equal to itself, so a nullable column cannot identify a row. Otherwise this is a database error at the
    // moment the table is created.
    var nullable = Build(typeof(INullableKey));
    Assert.IsFalse(nullable.Initialized);
    Assert.IsTrue(Reports(nullable, "cannot be nullable"));

    // …while a nullable FOREIGN key is ordinary: the check is about the PRIMARY key, not about nullability generally.
    var optional = Build(typeof(IOptionalRef), typeof(IGoodRow));
    Assert.IsTrue(optional.Initialized, optional.Report());

    // ----- an entity with NO MEMBERS is an error ------------------------------------------------------------------------
    //
    // Almost always a registration that named the wrong interface.
    var empty = Build(typeof(IEmptyRow));
    Assert.IsFalse(empty.Initialized);
    Assert.IsTrue(Reports(empty, "has no members"));

    // ----- and two keys of one entity may not share a NAME ---------------------------------------------------------------
    //
    // A naming policy that is not injective is exactly how this happens, and it is silent until the DDL fails.
    var colliding = Build(app => app.Settings.NamingPolicy = new OneNamePolicy(), typeof(ITwoKeys));
    Assert.IsFalse(colliding.Initialized);
    Assert.IsTrue(Reports(colliding, "keys named"));

    var distinct = Build(typeof(ITwoKeys));
    Assert.IsTrue(distinct.Initialized, distinct.Report());
  }

  private sealed class OneNamePolicy : NamingPolicy {
    public override string GetKeyName(string entityName, KeyFlags flags, IEnumerable<string> memberNames)
      => "the_only_name";
  }

  // ----- module dependencies -------------------------------------------------------------------------------------

  private class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) => RegisterEntities(typeof(IGoodRow));
  }

  private sealed class PayrollModule : EntityModule {
    public PayrollModule() : base("Payroll", new Version(1, 0)) {
      RegisterEntities(typeof(IPayslip));
      Requires<PeopleModule>();
    }
  }

  private interface IPayslip {
    [PrimaryKey] Guid Id { get; set; }
  }

  private static EntityApp BuildApp(params EntityModule[] modules) {
    var app = new EntityApp("App", new Version(1, 0));
    foreach (var module in modules)
      app.AddModule(module, "s");
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void ModuleDependency_IsCheckedAgainstTheApplication() {
    // Without this check the failure arrives later and in disguise — as a reference to an entity that is simply not
    // there — rather than as "this application is missing that module", which is what it is.
    var missing = BuildApp(new PayrollModule());
    Assert.IsFalse(missing.Initialized);
    Assert.IsTrue(Reports(missing, "requires module"));
    Assert.IsTrue(missing.Diagnostics().Any(d => d.Message.Contains("PeopleModule")));

    // ----- while a satisfied one passes, by the module itself or by anything DERIVED from it ---------------------------
    //
    // Requires<T> names a type; a module deriving from it is still that module.
    var satisfied = BuildApp(new PeopleModule(), new PayrollModule());
    Assert.IsTrue(satisfied.Initialized, satisfied.Report());

    var derived = BuildApp(new SpecialPeopleModule(), new PayrollModule());
    Assert.IsTrue(derived.Initialized, derived.Report());
  }

  private sealed class SpecialPeopleModule : PeopleModule { }

  // ----- and the whole point of collecting ------------------------------------------------------------------------

  [TestMethod]
  public void OneRun_ReportsEveryFault() {
    var app = Build(typeof(IKeylessRow), typeof(IEmptyRow), typeof(INullableKey));
    // IEmpty trips two checks — no members AND no primary key — which is correct: they are different faults, and a
    // developer fixing one still needs to hear about the other.
    Assert.IsTrue(Reports(app, "has no primary key"));
    Assert.IsTrue(Reports(app, "has no members"));
    Assert.IsTrue(Reports(app, "cannot be nullable"));
  }
}
