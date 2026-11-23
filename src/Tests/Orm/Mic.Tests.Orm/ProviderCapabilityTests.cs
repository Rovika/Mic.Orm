using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// WHAT A SERVER CANNOT DO, DECLARED — and the base gating on it. These are the flags that decide whether a second and
// third provider can be written as declarations, or have to be written as overrides with logic in them: Vita's SQLite
// driver expresses "cannot change a column" as four overrides whose bodies are empty.
//
// Proved against invented vendors, which is what this suite is for. No server can tell you what a server cannot do.
[TestClass]
public class ProviderCapabilityTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  // ----- the vendors -------------------------------------------------------------------------------------------------

  /// <summary>A SQLite-shaped server: no schemas, and a table is what it was created as.</summary>
  private sealed class RigidProvider : ModelOnlyProvider {
    public RigidProvider() : base(DbServerKind.SQLite, BuildTypeRegistry()) { }

    // Note what is NOT here: AlterColumn, DropColumn, AddConstraintsAfterCreate, Schemas.
    public override DbFeatures Features => DbFeatures.ReferentialConstraints;

    private static DbTypeRegistry BuildTypeRegistry() {
      var registry = new DbTypeRegistry();
      registry.Add("text", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.StringUnlimited);
      registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
      registry.Add("integer", typeof(int));
      registry.Add("blob", typeof(Guid));
      return registry;
    }
  }

  /// <summary>A MySQL-shaped server: it names every primary key itself.</summary>
  private sealed class OpinionatedProvider : ModelOnlyProvider {
    public OpinionatedProvider() : base(DbServerKind.MySql, BuildTypeRegistry()) { }
    public override DbFeatures Features => DbFeatures.Schemas | DbFeatures.ReferentialConstraints
      | DbFeatures.FullAlterTable;
    public override string? PrimaryKeyName => "PRIMARY";

    private static DbTypeRegistry BuildTypeRegistry() {
      var registry = new DbTypeRegistry();
      registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
      registry.Add("text", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.StringUnlimited);
      registry.Add("int", typeof(int));
      registry.Add("binary", typeof(Guid));
      return registry;
    }
  }

  // ----- the model ---------------------------------------------------------------------------------------------------

  [Entity(Name = "Person")]
  private interface IPersonV1 {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
    [Size(20)] string Extra { get; set; }
  }

  [Entity(Name = "Person")]
  private interface IPersonV2 {
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string Name { get; set; }        // widened — a column MODIFY
  }                                             // Extra gone   — a column DROP

  private interface IParent {
    [PrimaryKey] Guid Id { get; set; }
  }

  [Entity(PluralName = "Children")]
  private interface IChild {
    [PrimaryKey] Guid Id { get; set; }
    IParent Parent { get; set; }
  }

  [Entity(TableName = "tbl_box")]
  private interface IBox {
    [PrimaryKey] Guid Id { get; set; }
  }

  private static DbModel ModelOf(DbProvider provider, params Type[] types) =>
    ModelOf(new DbSettings(provider, NoServer), types);

  private static DbModel ModelOf(DbSettings settings, params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.CollectingLog();
    app.TryInit();
    AppInitHelper.CreateDatabase(app, settings);
    Assert.IsTrue(app.Initialized, app.Report());
    return app.MappedModel();
  }

  private static DbUpgradeScript ScriptFor(DbProvider provider, Type[] before, Type[] after) {
    var changes = provider.CreateDbModelComparer().Compare(ModelOf(provider, before), ModelOf(provider, after));
    return new DbUpgradeScriptBuilder(provider).Build(changes);
  }

  // ----- a server that cannot alter ------------------------------------------------------------------------------------

  [TestMethod]
  public void AChangeTheServerCannotTakeIsWITHHELDAndSaid() {
    // Skipped, never dropped quietly: the database will not match the model afterwards, and the one thing worse than
    // that is not knowing.
    var script = ScriptFor(new RigidProvider(), [typeof(IPersonV1)], [typeof(IPersonV2)]);

    Assert.IsFalse(script.Steps.Any(s => s.Stage is DbUpgradeStage.AlterColumns or DbUpgradeStage.DropColumns),
      script.ToText());
    Assert.AreEqual(2, script.Warnings.Count, string.Join(" | ", script.Warnings));
    Assert.IsTrue(script.Warnings.Any(w => w.Contains("cannot change a column in place")));
    Assert.IsTrue(script.Warnings.Any(w => w.Contains("cannot drop a column")));
    Assert.IsTrue(script.Warnings.All(w => w.Contains("will differ from the model")));

    // ----- while a server that CAN emits the very same changes ---------------------------------------------------------
    //
    // The pair is the point: one model, two vendors, and the only difference is what each declared.
    var capable = ScriptFor(new PostgresProvider(), [typeof(IPersonV1)], [typeof(IPersonV2)]);
    Assert.IsTrue(capable.Steps.Any(s => s.Stage == DbUpgradeStage.AlterColumns));
    Assert.IsTrue(capable.Steps.Any(s => s.Stage == DbUpgradeStage.DropColumns));
    Assert.AreEqual(0, capable.Warnings.Count, string.Join(" | ", capable.Warnings));
  }

  [TestMethod]
  public void Constraints_GoInsideCreateTableWhenTheyMust() {
    // The whole of SQLite's story in one statement. Everywhere else these are separate, because that is what lets an
    // upgrade change one later.
    var provider = new RigidProvider();
    var model = ModelOf(provider, typeof(IParent), typeof(IChild));
    var statements = new DbDdlBuilder(provider).CreateModel(model);

    var createChild = statements.Single(s => s.Kind == DdlKind.CreateTable && s.ObjectName == "Child");
    StringAssert.Contains(createChild.Text, "PRIMARY KEY");
    StringAssert.Contains(createChild.Text, "FOREIGN KEY");

    Assert.IsFalse(statements.Any(s => s.Kind is DdlKind.AddPrimaryKey or DdlKind.AddRefConstraint),
      "nothing should be added after the fact on a server that will not take it");

    // ----- and stay OUTSIDE it wherever they can be altered later --------------------------------------------------------
    var postgres = new PostgresProvider();
    var separate = new DbDdlBuilder(postgres).CreateModel(ModelOf(postgres, typeof(IParent), typeof(IChild)));

    Assert.IsFalse(separate.Single(s => s.Kind == DdlKind.CreateTable && s.ObjectName == "Child")
      .Text.Contains("FOREIGN KEY"));
    Assert.IsTrue(separate.Any(s => s.Kind == DdlKind.AddPrimaryKey));
    Assert.IsTrue(separate.Any(s => s.Kind == DdlKind.AddRefConstraint));
  }

  // ----- a server that names things itself -----------------------------------------------------------------------------

  [TestMethod]
  public void AServerAssignedPrimaryKeyNameIsTakenAtMAPPINGTime() {
    // Left unsaid, the model would call it something else, introspection would read back PRIMARY, and the comparer —
    // which matches by name — would report a drop plus an add on every single startup.
    var model = ModelOf(new OpinionatedProvider(), typeof(IPersonV1));
    Assert.AreEqual("PRIMARY", model.Tables.Single().PrimaryKey!.Name);

    // ----- and is not imposed on a server that does not care -----------------------------------------------------------
    var name = ModelOf(new PostgresProvider(), typeof(IPersonV1)).Tables.Single().PrimaryKey!.Name;
    Assert.AreNotEqual("PRIMARY", name);
    StringAssert.Contains(name, "Person");
  }

  // ----- names the server would silently truncate -----------------------------------------------------------------------

  [Entity(Name = "AnEntityWhoseNameIsLongEnoughToPushItsGeneratedIndexNamePastSixtyThreeCharacters")]
  private interface ILongNamedRow {
    [PrimaryKey] Guid Id { get; set; }
    [Size(10)] string Value { get; set; }
  }

  [TestMethod]
  public void ANameTooLongForTheServerIsAnERROR() {
    // PostgreSQL truncates at 63 SILENTLY, so two names differing only past the limit become one object and the
    // upgrade then fights itself forever. Caught at generation time, where the name can still be changed.
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(ILongNamedRow));
    app.AddModule(module);

    app.Settings.IncludeDbInfoModule = false;
    app.CollectingLog();

    app.TryInit();

    // MAPPING IS WHAT FAILS, and it now fails at CreateDatabase rather than at build: a name the server cannot hold
    // is a fact about the model AND the provider, which is exactly the pair this step brings together.
    Assert.IsNull(AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), NoServer)),
      "a name PostgreSQL cannot hold was accepted");
    Assert.IsTrue(app.Diagnostics().Any(d => d.Message.Contains("truncated")), app.Report());
    Assert.IsTrue(app.Diagnostics().Any(d => d.Message.Contains("63")), app.Report());
  }

  // ----- the two model options ------------------------------------------------------------------------------------------

  private sealed class HrModule : EntityModule {
    public HrModule() : base("Hr", new Version(1, 0)) => RegisterEntities(typeof(IPersonV1));
  }

  private sealed class SalesModule : EntityModule {
    public SalesModule() : base("Sales", new Version(1, 0)) => RegisterEntities(typeof(IParent));
  }

  [TestMethod]
  public void SchemalessServer_SchemaCanLiveInTheName() {
    // Otherwise two tables of the same name in different schemas silently become one — which the builder warns about
    // and now points at the fix.
    var app = new EntityApp("App", new Version(1, 0));
    app.AddModule(new HrModule(), "hr");
    app.AddModule(new SalesModule(), "sales");

    app.Settings.IncludeDbInfoModule = false;
    app.CollectingLog();

    app.TryInit();
    AppInitHelper.CreateDatabase(app,
      new DbSettings(new RigidProvider(), NoServer) { Options = DbOptions.Default | DbOptions.AddSchemaToTableNames });
    Assert.IsTrue(app.Initialized, app.Report());

    Assert.IsNotNull(app.MappedModel().FindTable("", "hr_Person"));
    Assert.IsNotNull(app.MappedModel().FindTable("", "sales_Parent"));
    Assert.IsFalse(app.Diagnostics().Any(d => d.Message.Contains("collide")));

    // ----- and where it does NOT, the warning names the option that fixes it --------------------------------------------
    var plain = new EntityApp("App", new Version(1, 0));
    plain.AddModule(new HrModule(), "hr");
    plain.AddModule(new SalesModule(), "sales");
    plain.Settings.IncludeDbInfoModule = false;
    plain.TryInit();
    AppInitHelper.CreateDatabase(plain, new DbSettings(new RigidProvider(), NoServer));

    Assert.IsTrue(plain.Diagnostics().Any(d => d.Message.Contains("AddSchemaToTableNames")),
      plain.Report());
  }

  [TestMethod]
  public void ForeignKeys_IndexedOnlyWhenAsked() {
    // Off by default and deliberately: an index on every foreign key is a write cost on every table, paid whether or
    // not anything queries that way.
    var provider = new PostgresProvider();
    var plain = ModelOf(provider, typeof(IParent), typeof(IChild));
    var fkKey = plain.FindTable("app", "Child")!.Keys.Single(k => k.IsForeignKey);
    Assert.IsFalse(fkKey.Flags.HasFlag(KeyFlags.Index));

    var settings = new DbSettings(provider, NoServer) { Options = DbOptions.Default | DbOptions.AutoIndexForeignKeys };
    var model = ModelOf(settings, typeof(IParent), typeof(IChild));
    var indexed = model.FindTable("app", "Child")!.Keys.Single(k => k.IsForeignKey);
    Assert.IsTrue(indexed.Flags.HasFlag(KeyFlags.Index));
    Assert.IsTrue(new DbDdlBuilder(provider).CreateModel(model)
      .Any(s => s.Kind == DdlKind.CreateIndex && s.ObjectName == indexed.Name));
  }

  [TestMethod]
  public void RefIntegrity_OffByOption_NoConstraintMadeOrDropped() {
    // A legacy database keeps its integrity in the applications: the references make no constraint, no warning says
    // so, and whatever constraints the database has are left alone.
    var provider = new PostgresProvider();
    var unenforced = ModelOf(new DbSettings(provider, NoServer) { Options = DbOptions.None },
      typeof(IParent), typeof(IChild));
    Assert.AreEqual(0, unenforced.FindTable("app", "Child")!.RefConstraints.Count);
    Assert.IsFalse(unenforced.BuildReport.Format().Contains("referential"), unenforced.BuildReport.Format());

    var enforced = ModelOf(provider, typeof(IParent), typeof(IChild));
    Assert.AreEqual(1, enforced.FindTable("app", "Child")!.RefConstraints.Count);
    var changes = provider.CreateDbModelComparer().Compare(enforced, unenforced);
    Assert.IsFalse(changes.Items.Any(c => c.ObjectKind == DbObjectKind.RefConstraint),
      "the database's constraints are not the model's business");
  }

  [TestMethod]
  public void TableNames_PluralizedWhenAsked() {
    var settings = new DbSettings(new PostgresProvider(), NoServer) {
      Options = DbOptions.Default | DbOptions.PluralizeTableNames,
    };
    var model = ModelOf(settings, typeof(IParent), typeof(IChild), typeof(IBox));
    Assert.IsNotNull(model.FindTable("app", "Parents"), "by the rule");
    Assert.IsNotNull(model.FindTable("app", "Children"), "by PluralName");
    Assert.IsNotNull(model.FindTable("app", "tbl_box"), "TableName as given");

    var singular = ModelOf(new PostgresProvider(), typeof(IParent), typeof(IChild));
    Assert.IsNotNull(singular.FindTable("app", "Parent"));
  }
}
