using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;

namespace Mic.Tests.Orm;

// Two models in, a list of changes out. Tested against pairs built from real entity models, which is why the plan
// puts this BEFORE introspection: every case can be produced on demand rather than waited for.
[TestClass]
public class DbModelComparerTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  private static readonly PostgresProvider Provider = new();

  private static DbModel ModelOf(params Type[] types) => ModelOf(Provider, types);

  private static DbModel ModelOf(DbProvider provider, params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    if (app.Initialized)
      AppInitHelper.CreateDatabase(app, new DbSettings(provider, NoServer));
    Assert.IsTrue(app.Initialized, app.Report());
    return app.MappedModel();
  }

  private static DbModelDiffInfo Compare(DbModel oldModel, DbModel newModel) =>
    Provider.CreateDbModelComparer().Compare(oldModel, newModel);

  private static DbObjectDiffInfo Single(DbModelDiffInfo set, DbObjectKind kind, DbDiffKind change) =>
    set.Items.Single(c => c.ObjectKind == kind && c.DiffKind == change);

  // ----- the shapes being compared ---------------------------------------------------------------------------------

  [Entity(Name = "Person")]
  private interface IPersonV1 {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    string? Note { get; set; }
  }

  [Entity(Name = "Person")]
  private interface IPersonV2 {          // same table, LastName widened, Note now required
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string LastName { get; set; }
    string Note { get; set; }
  }

  private interface IExtraRow {
    [PrimaryKey] Guid Id { get; set; }
  }

  // ----- the ordinary diff, in one pass ----------------------------------------------------------------------------------
  //
  // Five methods over three pairs of models: nothing changed, something changed, something appeared, something went.
  [TestMethod]
  public void Diff_ReportsChangesAndWhichAreDestructive() {
    // ----- identical models produce nothing ---------------------------------------------------------------------------
    var noDiff = Compare(ModelOf(typeof(IPersonV1)), ModelOf(typeof(IPersonV1)));
    Assert.IsFalse(noDiff.HasItems, noDiff.Report());
    Assert.IsFalse(noDiff.HasErrors);

    // ----- a changed COLUMN is a Modify that says what changed ---------------------------------------------------------
    //
    // Both objects are on the change, so nothing has to decompose the difference — but the DESCRIPTION is what a report
    // shows, written where the comparer found it.
    var altered = Compare(ModelOf(typeof(IPersonV1)), ModelOf(typeof(IPersonV2)));
    var columns = altered.Items.Where(c => c.ObjectKind == DbObjectKind.Column).ToList();

    var widened = columns.Single(c => c.Name == "LastName");
    StringAssert.Contains(widened.Description!, "varchar(50) → varchar(80)");
    Assert.IsNotNull(widened.Source);
    Assert.IsNotNull(widened.Target);
    StringAssert.Contains(columns.Single(c => c.Name == "Note").Description!, "now NOT NULL");

    // …and the whole set reads back as text, which is the compare-and-report mode the vision calls for: everything the
    // upgrade WOULD do, with nothing executed.
    var report = altered.Report();
    StringAssert.Contains(report, "Modify Column 'LastName'");
    StringAssert.Contains(report, "varchar(50) → varchar(80)");

    // ----- an ADDED table is an Add with no old side --------------------------------------------------------------------
    var added = Compare(ModelOf(typeof(IPersonV1)), ModelOf(typeof(IPersonV1), typeof(IExtraRow)));
    Assert.AreEqual("ExtraRow", Single(added, DbObjectKind.Table, DbDiffKind.Add).Name);
    Assert.IsNull(Single(added, DbObjectKind.Table, DbDiffKind.Add).Source);

    // ----- while a DROPPED one is destructive, and says so ----------------------------------------------------------------
    //
    // The diff reports both; what actually runs is a policy decision made with the database's own kind in hand.
    var removed = Compare(ModelOf(typeof(IPersonV1), typeof(IExtraRow)), ModelOf(typeof(IPersonV1)));
    var dropped = Single(removed, DbObjectKind.Table, DbDiffKind.Drop);

    Assert.IsTrue(dropped.IsDestructive);
    Assert.AreEqual("ExtraRow", dropped.Name);
    CollectionAssert.Contains(removed.Destructive.ToList(), dropped);
  }

  // ----- renames ---------------------------------------------------------------------------------------------------

  private interface IOldNamedRow {
    [PrimaryKey] Guid Id { get; set; }
    string Title { get; set; }
  }

  [OldNames("OldNamedRow")]
  private interface IRenamedRow {
    [PrimaryKey] Guid Id { get; set; }
    [OldNames("Title")] string Caption { get; set; }
  }

  [TestMethod]
  public void OldNames_TurnDropPlusAddIntoRename() {
    // The difference between keeping the data and losing it.
    var set = Compare(ModelOf(typeof(IOldNamedRow)), ModelOf(typeof(IRenamedRow)));

    var table = Single(set, DbObjectKind.Table, DbDiffKind.Rename);
    StringAssert.Contains(table.Description!, "'OldNamedRow' → 'RenamedRow'");

    var column = Single(set, DbObjectKind.Column, DbDiffKind.Rename);
    StringAssert.Contains(column.Description!, "'Title' → 'Caption'");

    // Nothing holding data is dropped, which is the whole point. The KEYS are a different matter: they are named for
    // their entity, so renaming the table renames its constraints, and a constraint is recreated rather than renamed.
    Assert.AreEqual(0, set.Items.Count(c =>
      c.DiffKind == DbDiffKind.Drop && c.ObjectKind is DbObjectKind.Table or DbObjectKind.Column));

    // ----- but matching by CURRENT name beats an OldNames claim -------------------------------------------------------
    //
    // Which is what lets an OldNames attribute be left in place indefinitely: once the database has caught up, a stale
    // claim must not start hijacking the object it used to refer to.
    var before = ModelOf(typeof(IRenamedRow2));                       // a table called "Renamed2"
    var after = ModelOf(typeof(IRenamedRow2), typeof(ISurvivorRow));   // ...plus one CLAIMING to have been "Renamed"

    var stale = Compare(before, after);
    Assert.AreEqual(0, stale.Items.Count(c => c.DiffKind == DbDiffKind.Rename));
    Assert.AreEqual("SurvivorRow", Single(stale, DbObjectKind.Table, DbDiffKind.Add).Name);

    // ----- and TWO objects claiming ONE old name is an error -------------------------------------------------------------
    //
    // Unresolvable rather than ambiguous: one existing object cannot become two, and guessing loses data.
    var contested = Compare(ModelOf(typeof(IOriginalRow)), ModelOf(typeof(IClaimantA), typeof(IClaimantB)));
    Assert.IsTrue(contested.HasErrors);
    StringAssert.Contains(contested.Errors[0], "cannot become two");
  }

  [OldNames("RenamedRow")]
  private interface ISurvivorRow {
    [PrimaryKey] Guid Id { get; set; }
  }

  private interface IRenamedRow2 {
    [PrimaryKey] Guid Id { get; set; }
  }

  [OldNames("OriginalRow")]
  private interface IClaimantA {
    [PrimaryKey] Guid Id { get; set; }
  }

  [OldNames("OriginalRow")]
  private interface IClaimantB {
    [PrimaryKey] Guid Id { get; set; }
  }

  private interface IOriginalRow {
    [PrimaryKey] Guid Id { get; set; }
  }

  // ----- case, and what is not compared -----------------------------------------------------------------------------

  [TestMethod]
  public void Matching_IsCaseInsensitive() {
    // A server folds identifiers and each folds differently — a case-sensitive match would report a rename every time
    // the same model met PostgreSQL.
    var lower = ModelOf(typeof(IPersonV1));
    var upper = ModelOf(typeof(IPersonV1));
    var table = upper.Tables.First();
    var renamed = new DbTableInfo(table.Name.ToUpperInvariant(), table.Schema) { Entity = table.Entity };
    foreach (var column in table.Columns)
      renamed.AddColumn(new DbColumnInfo(column.Name.ToUpperInvariant(), column.TypeRef, column.IsNullable));

    var shuffled = new DbModel("App", new PostgresProvider());
    shuffled.GetOrAddSchema(table.Schema.Name);
    shuffled.AddTable(renamed);

    var set = Compare(lower, shuffled);
    Assert.AreEqual(0, set.Items.Count(c => c.ObjectKind == DbObjectKind.Table && c.DiffKind != DbDiffKind.Add));

    // ----- and an [AsIs] column is never compared at all ---------------------------------------------------------------
    //
    // The one deliberate exclusion: some columns are not the ORM's to reshape, whatever they look like.
    var asIs = Compare(ModelOf(typeof(IAsIsRowV1)), ModelOf(typeof(IAsIsRowV2)));
    Assert.IsFalse(asIs.HasItems, asIs.Report());
  }

  [Entity(Name = "AsIsThing")]
  private interface IAsIsRowV1 {
    [PrimaryKey] Guid Id { get; set; }
    [AsIs][Size(50)] string Managed { get; set; }
  }

  [Entity(Name = "AsIsThing")]
  private interface IAsIsRowV2 {
    [PrimaryKey] Guid Id { get; set; }
    [AsIs][Size(200)] string Managed { get; set; }
  }

  // ----- keys ------------------------------------------------------------------------------------------------------

  [Entity(Name = "Indexed")]
  [Index("Name")]
  private interface IIndexedRowV1 {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    string Other { get; set; }
  }

  [Entity(Name = "Indexed")]
  [Index("Name -Other")]
  private interface IIndexedRowV2 {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    string Other { get; set; }
  }

  [TestMethod]
  public void AKeyWhoseColumnsChangedIsReported() {
    // The index has the same NAME in neither model, so this comes out as a drop plus an add — which is correct: an
    // index named for its columns cannot survive its columns changing.
    var set = Compare(ModelOf(typeof(IIndexedRowV1)), ModelOf(typeof(IIndexedRowV2)));
    Assert.IsTrue(set.Items.Any(c => c.ObjectKind == DbObjectKind.Key && c.DiffKind == DbDiffKind.Drop));
    Assert.IsTrue(set.Items.Any(c => c.ObjectKind == DbObjectKind.Key && c.DiffKind == DbDiffKind.Add));

    // ----- while a changed FILTER keeps its name, so it comes out as a REBUILD ------------------------------------------
    //
    // Compared through the HASH, because the server rewrites the filter — and a difference there has nothing to alter
    // incrementally.
    var oldFiltered = ModelOf(typeof(IFilteredRowV1));
    oldFiltered.Tables.First().Keys.First(k => k.Filter != null);   // sanity: the filter survived mapping

    var rebuilt = Single(Compare(oldFiltered, ModelOf(typeof(IFilteredRowV2))), DbObjectKind.Key, DbDiffKind.Modify);
    StringAssert.Contains(rebuilt.Description!, "rebuilt");
  }

  [Entity(Name = "Filtered")]
  [Index("Name", Filter = "\"Name\" IS NOT NULL")]
  private interface IFilteredRowV1 {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  [Entity(Name = "Filtered")]
  [Index("Name", Filter = "\"Name\" <> ''")]
  private interface IFilteredRowV2 {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  // ----- an identity the server implies ----------------------------------------------------------------------------
  //
  // SQLite marks no identity: a single-column INTEGER primary key is the row id, so a plain int key is declared as an
  // identity is, and the loader reads both as one. The database's side of each comparison is the identity model.

  [Entity(Name = "Counter")]
  private interface ICounterWithIdentity {
    [PrimaryKey][Identity] int Id { get; set; }
    int Value { get; set; }
  }

  [Entity(Name = "Counter")]
  private interface ICounterPlain {
    [PrimaryKey] int Id { get; set; }
    int Value { get; set; }
  }

  [TestMethod]
  public void AnImpliedIdentity_IsNoChangeWhereTheModelDeclaresNone() {
    var sqlite = new SqliteProvider();
    var read = ModelOf(sqlite, typeof(ICounterWithIdentity));
    var declared = ModelOf(sqlite, typeof(ICounterPlain));
    Assert.AreEqual(0, sqlite.CreateDbModelComparer().Compare(read, declared).Items.Count);

    // The other way a statement is owed, and it is reported: the model wants an identity the table has not got.
    var owed = sqlite.CreateDbModelComparer().Compare(declared, read);
    StringAssert.Contains(Single(owed, DbObjectKind.Column, DbDiffKind.Modify).Description, "now identity");

    // A server with a marker reads what was declared, and the difference is one.
    var marked = Compare(ModelOf(typeof(ICounterWithIdentity)), ModelOf(typeof(ICounterPlain)));
    StringAssert.Contains(Single(marked, DbObjectKind.Column, DbDiffKind.Modify).Description, "no longer identity");
  }
}
