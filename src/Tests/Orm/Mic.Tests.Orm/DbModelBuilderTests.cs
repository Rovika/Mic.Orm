using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Tests.Orm;

// The mapping: entity model + provider → db model. The middle hop of the round trip the db model was defined by, so
// everything that reaches a database passes through here.
[TestClass]
public class DbModelBuilderTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  [Entity(TableName = "people")]
  private interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string LastName { get; set; }
    string? MiddleName { get; set; }
    decimal Salary { get; set; }
    [Column("home_city")] string City { get; set; }
    [Unlimited] string Notes { get; set; }
    [Auto(AutoType.Identity)] int Seq { get; set; }
    IAddress Address { get; set; }
    IList<IPhone> Phones { get; }
    [NoColumn] string FullName { get; }
  }

  private interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    string City { get; set; }
  }

  private interface IPhone {
    [PrimaryKey] Guid Id { get; set; }
    IPerson Person { get; set; }
  }

  private static EntityApp Build(DbProvider provider, Action<EntityApp>? configure = null,
      params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types.Length > 0 ? types : [typeof(IPerson), typeof(IAddress), typeof(IPhone)]);
    app.AddModule(module);
    configure?.Invoke(app);
    app.Settings.IncludeDbInfoModule = false;
    app.CollectingLog();
    app.TryInit();
    if (app.Initialized)
      AppInitHelper.CreateDatabase(app, new DbSettings(provider, NoServer));
    return app;
  }

  private static EntityApp Postgres() => Build(new PostgresProvider());

  // ----- ONE mapped model, everything the mapping produces --------------------------------------------------------------
  //
  // Ten methods, each mapping the same three entities again to ask one question of the result.
  [TestMethod]
  public void Entities_BecomeTablesColumnsKeysConstraints() {
    var app = Postgres();
    Assert.IsTrue(app.Initialized, app.Report());
    var db = app.MappedModel();
    Assert.IsNotNull(db);
    Assert.AreEqual(DbServerKind.Postgres, db.Provider.ServerKind);
    Assert.AreSame(app.Model, db.EntityModel);

    // ----- entities become TABLES, and keep the link back -------------------------------------------------------------
    var table = db.FindTable("app", "people")!;           // [Entity(TableName = "people")] won over the convention
    Assert.IsNotNull(table);
    Assert.AreEqual("Person", table.Entity!.Name);
    Assert.IsNotNull(db.FindTable("app", "Address"));     // no TableName: the entity name stands

    // ----- members become COLUMNS with the provider's types ------------------------------------------------------------
    Assert.AreEqual("uuid", table.GetColumn("Id").TypeSpec);
    Assert.AreEqual("varchar(80)", table.GetColumn("LastName").TypeSpec);
    Assert.AreEqual("varchar(50)", table.GetColumn("MiddleName").TypeSpec);   // the app default size
    Assert.AreEqual("numeric(18,4)", table.GetColumn("Salary").TypeSpec);
    Assert.AreEqual("text", table.GetColumn("Notes").TypeSpec);               // [Unlimited]
    Assert.IsNotNull(table.FindColumn("home_city"), "an explicit column name wins");

    // …keeping their own link back, and their nullability.
    Assert.AreEqual("MiddleName", table.GetColumn("MiddleName").Member!.Name);
    Assert.IsTrue(table.GetColumn("MiddleName").IsNullable);
    Assert.IsFalse(table.GetColumn("LastName").IsNullable);

    // ----- while what has NO column gets none ----------------------------------------------------------------------------
    //
    // A list is a query; a transient is never stored; a reference has its generated FK columns instead of itself.
    Assert.IsNull(table.FindColumn("Phones"));
    Assert.IsNull(table.FindColumn("FullName"));
    Assert.IsNull(table.FindColumn("Address"));
    Assert.IsNotNull(table.FindColumn("Address_Id"));      // the reference's column, which IS a member

    // …and a database-generated member becomes an IDENTITY column.
    Assert.IsTrue(table.GetColumn("Seq").IsIdentity);

    // ----- what an INSERT writes is settled HERE, not worked out per statement -------------------------------------------
    //
    // The CRUD builder walked every member of the entity to derive this, ONCE PER STATEMENT — and a save of rows that
    // cannot be collapsed is one statement per row.
    CollectionAssert.DoesNotContain(table.InsertColumns.ToList(), table.GetColumn("Seq"));
    Assert.IsFalse(table.InsertColumns.Any(c => c.Member?.Kind == MemberKind.List));
    Assert.AreEqual(table.Columns.Count - 1, table.InsertColumns.Count, "only the identity column is excluded");

    // The joined string is those columns, quoted, in that order — the two cannot drift because one is built from the
    // other, and this is what says so.
    Assert.AreEqual(string.Join(", ", table.InsertColumns.Select(c => c.QuotedName)), table.InsertColumnList);
    StringAssert.Contains(table.InsertColumnList, "\"home_city\"", "quoted, and the mapped name rather than the member's");
    Assert.IsFalse(table.InsertColumnList.Contains("\"Seq\""));

    // ----- keys become keys OVER COLUMNS -----------------------------------------------------------------------------------
    var pk = table.PrimaryKey!;
    Assert.IsNotNull(pk);
    Assert.AreEqual("Id", pk.Columns.Single().Column.Name);
    Assert.AreSame(table.Entity!.PrimaryKey, pk.EntityKey);

    // ----- and a reference becomes a CONSTRAINT between two of them ----------------------------------------------------------
    var address = db.FindTable("app", "Address")!;
    var constraint = table.RefConstraints.Single();
    Assert.AreSame(table, constraint.FromTable);
    Assert.AreSame(address, constraint.ToTable);
    Assert.AreEqual("Address_Id", constraint.FromKey.Columns.Single().Column.Name);
    Assert.AreSame(address.PrimaryKey, constraint.ToKey);
    Assert.IsNotNull(constraint.Reference);
  }

  [TestMethod]
  public void AModelBuildsWithNoDatabaseAtAll() {
    // The entity model is still built and validated — mapping is what needs a provider, not the model.
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IAddress));
    app.AddModule(module);
    app.TryInit();

    Assert.IsTrue(app.Initialized, "the model needs no provider to be built and validated");
    Assert.IsNotNull(app.Model);
    Assert.IsFalse(app.HasDatabase(), "nothing was connected, so there is nothing to map to");
    Assert.IsFalse(app.IsConnected, "nothing has connected, and registering a database would not be connecting");

    // ----- and a BROKEN one is not mapped at all ----------------------------------------------------------------------
    //
    // Skipping work whose input already failed: mapping a broken model produces cascading noise, not information.
    var broken = Build(new PostgresProvider(), null, typeof(IKeylessRow));
    broken.CollectingLog();
    Assert.IsFalse(broken.Initialized);
    Assert.IsFalse(broken.HasDatabase(), "a broken model was mapped anyway");

    Assert.IsNull(AppInitHelper.CreateDatabase(broken, new DbSettings(new PostgresProvider(), NoServer)));
    Assert.IsTrue(broken.StartupErrors().Any(e => e.ToString().Contains("must run, and succeed")),
      broken.Report());
  }

  private interface IKeylessRow {
    string Name { get; set; }
  }

  [TestMethod]
  public void DatabasesOfOneProviderShareTheDbModel() {
    // A db model is a function of the entity model, the provider's type and the mapping options; a second database
    // that agrees on the three, a tenant's, is mapped once. The options or the provider differing: its own model.
    var app = Postgres();
    var first = app.DataAccess.FindDatabase()!;

    var tenant = AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), NoServer, databaseAlias: "t1"))!;
    Assert.AreSame(first.DbModel, tenant.DbModel, "the same provider type and options: one model");
    Assert.AreNotSame(first.QueryCache, tenant.QueryCache, "what hangs off the database stays per database");

    var plural = AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), NoServer, databaseAlias: "t2") {
      Options = DbOptions.Default | DbOptions.PluralizeTableNames,
    })!;
    Assert.AreNotSame(first.DbModel, plural.DbModel, "other options map to other names");

    var sqlite = AppInitHelper.CreateDatabase(app, new DbSettings(new SqliteProvider(), "Data Source=:memory:", databaseAlias: "t3"))!;
    Assert.AreNotSame(first.DbModel, sqlite.DbModel, "another provider maps to another model");
    Assert.AreEqual(4, app.DataAccess.Databases.Count);
  }

  // ----- tables and columns -------------------------------------------------------------------------------------------

  [TestMethod]
  public void AReferenceTheDatabaseDoesNotEnforceGetsNoConstraint() {
    var app = Build(new PostgresProvider(), null, typeof(ILooseRow), typeof(IAddress));
    Assert.IsTrue(app.Initialized, app.Report());
    var table = app.MappedModel().FindTable("app", "LooseRow")!;

    Assert.AreEqual(0, table.RefConstraints.Count);       // the model knows the relationship; the database is not told
    Assert.IsNotNull(table.FindColumn("Home_Id"));         // but the column is still there
  }

  private interface ILooseRow {
    [PrimaryKey] Guid Id { get; set; }
    [EntityRef(HasDbForeignKey = false)] IAddress Home { get; set; }
  }

  // ----- what a provider cannot do --------------------------------------------------------------------------------

  /// <summary>A deliberately poor server: no filtered indexes, no included columns, no ordered index columns, no
  /// clustering of either kind.</summary>
  private sealed class PoorProvider : ModelOnlyProvider {
    public PoorProvider() : base(DbServerKind.SQLite, BuildTypeRegistry()) { }
    public override DbFeatures Features => DbFeatures.Schemas | DbFeatures.ReferentialConstraints;
    private static DbTypeRegistry BuildTypeRegistry() {
      var registry = new DbTypeRegistry();
      registry.Add("text", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.StringUnlimited);
      registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
      registry.Add("blob", typeof(Guid));
      registry.Add("integer", typeof(int));
      registry.Add("numeric", typeof(decimal), DbTypeFlags.TakesPrecisionScale, defaultPrecision: 18, defaultScale: 4);
      return registry;
    }
  }

  [Index("Name -Rank", IncludeMembers = "Note")]
  [ClusteredIndex("Name")]
  private interface ILossyRow {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    int Rank { get; set; }
    string Note { get; set; }
  }

  [Index("Name", Filter = "Name IS NOT NULL")]
  private interface IFilteredRow {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  // GIN is PostgreSQL's and nobody else's, which is what makes it the access method worth declaring in a test: one
  // provider keeps it, every other degrades to its own default.
  [Index("Payload", IndexType = DbIndexType.Gin)]
  private interface ITypedIndex {
    [PrimaryKey] Guid Id { get; set; }
    string Payload { get; set; }
  }

  [TestMethod]
  public void PerformanceCost_IsDroppedWithAWarning() {
    // The database still holds the same data and enforces the same rules — the application is slower on this vendor,
    // which is tolerable for a model targeting several.
    var app = Build(new PoorProvider(), null, typeof(ILossyRow));
    Assert.IsTrue(app.Initialized, app.Report());

    var warnings = app.Diagnostics().Where(d => d.Severity == Severity.Warning).ToList();
    Assert.IsTrue(warnings.Any(w => w.Message.Contains("included columns")));
    Assert.IsTrue(warnings.Any(w => w.Message.Contains("descending")));
    Assert.IsTrue(warnings.Any(w => w.Message.Contains("clustered")));

    // And the indexes are still there, just plainer — the two-column one lost its includes and its direction, and
    // nothing anywhere kept a clustering designation.
    var table = app.MappedModel().FindTable("app", "LossyRow")!;
    var index = table.Keys.Single(k => k.Columns.Count == 2);
    Assert.AreEqual(0, index.IncludedColumns.Count);
    Assert.IsFalse(index.Columns[1].Descending);
    Assert.IsFalse(table.Keys.Any(k => k.Flags.HasFlag(KeyFlags.Clustered)));

    // ----- while PostgreSQL keeps every bit of it, and warns about none of it ------------------------------------------
    var capable = Build(new PostgresProvider(), null, typeof(ILossyRow));
    Assert.IsTrue(capable.Initialized, capable.Report());
    Assert.AreEqual(0, capable.Diagnostics().Count(d => d.Severity == Severity.Warning));

    var kept = capable.MappedModel().FindTable("app", "LossyRow")!.Keys
      .Single(k => k.Flags.HasFlag(KeyFlags.Index) && k.IncludedColumns.Count > 0);
    Assert.AreEqual("Note", kept.IncludedColumns.Single().Name);
    Assert.IsTrue(kept.Columns[1].Descending);

    // ----- an ACCESS METHOD degrades the same way, and resolves even when nobody asked ---------------------------------
    //
    // A GIN index on a server that has none is an ordinary one: slower for what GIN is good at, identical in what it
    // enforces. So it is a warning, like the rest of this method.
    var noGin = Build(new PoorProvider(), null, typeof(ITypedIndex));
    Assert.IsTrue(noGin.Initialized, noGin.Report());
    Assert.IsTrue(noGin.Diagnostics().Any(d => d.Severity == Severity.Warning && d.Message.Contains("Gin")));

    // AND WHAT IT DEGRADES TO IS CONCRETE, not `Default`. This is the assertion that matters: a key left holding
    // "nobody said" would differ from what a server reports back on every startup, which is the trap clustering fell
    // into. Both the degraded index and the ones that never named a type come out as the provider's own default.
    var plain = noGin.MappedModel().FindTable("app", "TypedIndex")!;
    Assert.IsTrue(plain.Keys.All(k => k.IndexType == new PoorProvider().DefaultIndexType));

    var withGin = Build(new PostgresProvider(), null, typeof(ITypedIndex));
    Assert.AreEqual(0, withGin.Diagnostics().Count(d => d.Severity == Severity.Warning));
    var ginTable = withGin.MappedModel().FindTable("app", "TypedIndex")!;
    Assert.AreEqual(DbIndexType.Gin, ginTable.Keys.Single(k => k.Flags.HasFlag(KeyFlags.Index)).IndexType);
    Assert.AreEqual(DbIndexType.BTree, ginTable.Keys.Single(k => k.IsPrimaryKey).IndexType);

    // ----- which is the one-to-many the whole design rests on -----------------------------------------------------------
    //
    // The entity model is vendor-neutral, and a DbModel is what it becomes FOR a server — down to the column types.
    var postgres = Build(new PostgresProvider(), null, typeof(IAddress)).MappedModel();
    var poor = Build(new PoorProvider(), null, typeof(IAddress)).MappedModel();

    Assert.AreEqual("uuid", postgres.FindTable("app", "Address")!.GetColumn("Id").TypeSpec);
    Assert.AreEqual("blob", poor.FindTable("app", "Address")!.GetColumn("Id").TypeSpec);
    Assert.AreNotEqual(postgres.Provider.ServerKind, poor.Provider.ServerKind);
  }

  [TestMethod]
  public void AFilteredIndexIsAnERRORNotAWarning() {
    // Different in kind: a unique filtered index is a CONSTRAINT, so dropping it silently would make the database stop
    // rejecting rows it used to reject. Not slower — wrong, and discovered as bad data much later.
    // AT MAPPING, which is where it can be known: whether a filtered index survives is a fact about the model AND
    // the provider, so it is `CreateDatabase` that refuses rather than `Init`.
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app", IncludeDbInfoModule = false });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IFilteredRow));
    app.AddModule(module);
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, "the model itself is sound; it is this SERVER that cannot hold it");

    Assert.IsNull(AppInitHelper.CreateDatabase(app, new DbSettings(new PoorProvider(), NoServer)),
      "a filtered index the server cannot hold was mapped anyway");
    Assert.IsTrue(app.Diagnostics().Any(d =>
      d.Severity == Severity.Error && d.Message.Contains("filtered index")));

    // ----- while a LEGACY type named outright is only warned about ---------------------------------------------------
    //
    // It never happens by accident — nothing maps onto a legacy type — so this only fires when someone wrote it.
    var legacy = Build(new LegacyProvider(), null, typeof(ILegacyTypesRow));
    Assert.IsTrue(legacy.Initialized, legacy.Report());
    Assert.IsTrue(legacy.Diagnostics().Any(d =>
      d.Severity == Severity.Warning && d.Message.Contains("superseded")));
  }

  private interface ILegacyTypesRow {
    [PrimaryKey] Guid Id { get; set; }
    [Column(DbTypeSpec = "ntext")] string Blurb { get; set; }
  }

  private sealed class LegacyProvider : ModelOnlyProvider {
    public LegacyProvider() : base(DbServerKind.MsSql, BuildTypeRegistry()) { }
    public override DbFeatures Features => DbFeatures.Schemas;
    private static DbTypeRegistry BuildTypeRegistry() {
      var registry = new DbTypeRegistry();
      registry.Add("nvarchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
      registry.Add("uniqueidentifier", typeof(Guid));
      registry.Add("ntext", typeof(string), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);
      return registry;
    }
  }

  // ----- naming ---------------------------------------------------------------------------------------------------

  [TestMethod]
  public void NamingPolicy_IsConsultedForTablesAndColumns() {
    var app = Build(new PostgresProvider(), app => app.Settings.NamingPolicy = new SnakePolicy(), typeof(IAddress));
    Assert.IsTrue(app.Initialized, app.Report());
    var table = app.MappedModel().FindTable("app", "address")!;
    Assert.IsNotNull(table);
    Assert.IsNotNull(table.FindColumn("city"));
  }

  private sealed class SnakePolicy : NamingPolicy {
    public override string GetTableName(EntityTypeInfo entity) => base.GetTableName(entity).ToLowerInvariant();
    public override string GetColumnName(EntityMemberInfo member) => base.GetColumnName(member).ToLowerInvariant();
  }
}
