using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// THE ROUND TRIP, which is the only honest test of introspection — and the acceptance test a new provider either
// passes or does not: build a database from a model, read it back, compare, expect NOTHING. Anything MIC generates
// but cannot read back identically shows up as a spurious change, which is the failure that otherwise presents as
// "the upgrade wants to alter the same column on every startup".
public abstract partial class DdlTests {
  static class Intro {
    public interface IWidget {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string Name { get; set; }
      [Size(200)] string? Description { get; set; }
      [Unlimited] string Notes { get; set; }
      /// <summary>A JSON column: the server's JSON type where it has one, its unlimited text where it has not — and
      /// it has to read back as the same type.</summary>
      [Json] Dictionary<string, int>? Counters { get; set; }
      int Count { get; set; }
      long BigCount { get; set; }
      decimal Amount { get; set; }
      double Ratio { get; set; }
      bool IsActive { get; set; }
      DateTime CreatedOn { get; set; }
      DateOnly? DueOn { get; set; }
      byte[]? Blob { get; set; }
      /// <summary>A binary that says its size: the server's bounded type where it has one, which an index takes.
      /// </summary>
      [Size(20), Index] byte[]? Token { get; set; }
    }

    public interface IGadget {
      [PrimaryKey] Guid Id { get; set; }
      [Size(30)] string Code { get; set; }
    }

    // The two places a server is most likely to hand back something other than what it was given.
    //
    // NOTE WHAT IS NOT HERE: a boolean default. `[Column(Default = …)]` is RAW SQL — a deliberate escape hatch — and
    // there is no spelling of "true" that every server takes: PostgreSQL rejects `1`, MySQL normalises `true` to `1`
    // and reports the `1` back. A model meant to run on several servers cannot hard-code one, and a suite that tested
    // otherwise would be asserting a portability MIC does not offer.
    public interface IDefaultedRow {
      [PrimaryKey] Guid Id { get; set; }
      [Column(Default = "0")] int Score { get; set; }
      [Size(20)][Column(Default = "'none'")] string Status { get; set; }
      bool IsOn { get; set; }
    }

    public interface IIdentityRow {
      [PrimaryKey][Identity] int Id { get; set; }
      [Size(30)] string Code { get; set; }
    }

    /// <summary>An int key the application gives, with no identity declared.</summary>
    public interface IPlainIntKeyRow {
      [PrimaryKey] int Id { get; set; }
      [Size(30)] string Name { get; set; }
    }

    // Keys of every shape the model can express.
    [Index("Name")]
    [Index("Name -Code", IncludeMembers = "Score")]
    [Unique("Code")]
    public interface IKeyedRow {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string Name { get; set; }
      [Size(20)] string Code { get; set; }
      int Score { get; set; }
    }

    // A FILTERED INDEX IS ITS OWN ENTITY, because not every server has them — and where one is missing that is an
    // ERROR rather than a warning, deliberately: a unique filtered index is a CONSTRAINT, and dropping it silently
    // would change what the database enforces. So the model carrying one is only ever BUILT where it can exist.
    [Index("Score", Filter = "\"Score\" > 0")]
    public interface IFilteredRow {
      [PrimaryKey] Guid Id { get; set; }
      int Score { get; set; }
    }

    [Entity(Name = "FilteredRow")]
    [Index("Score", Filter = "\"Score\" > 100")]      // same index, different filter
    public interface IRefilteredRow {
      [PrimaryKey] Guid Id { get; set; }
      int Score { get; set; }
    }

    // AN ACCESS METHOD IS ONE ENTITY PER VENDOR, for the same reason a filtered index is its own: no type is shared.
    // PostgreSQL has hash, MySQL has FULLTEXT, Oracle has BITMAP, and SQL Server and SQLite have only their default —
    // so the walk adds whichever of these the provider declares, and on two vendors adds none of them.
    [Index("Score", IndexType = DbIndexType.Hash)]
    public interface IHashIndexedRow {
      [PrimaryKey] Guid Id { get; set; }
      int Score { get; set; }
    }

    [Index("Label", IndexType = DbIndexType.FullText)]
    public interface ITextIndexedRow {
      [PrimaryKey] Guid Id { get; set; }
      [Size(100)] string Label { get; set; }
    }

    [Index("Score", IndexType = DbIndexType.Bitmap)]
    public interface IBitmapIndexedRow {
      [PrimaryKey] Guid Id { get; set; }
      int Score { get; set; }
    }

    public interface IChild {
      [PrimaryKey] Guid Id { get; set; }
      [CascadeDelete] IKeyedRow Owner { get; set; }
      IKeyedRow? Backup { get; set; }
    }

    [PrimaryKey("PartA,PartB")]
    public interface ICompositeKeyRow {
      int PartA { get; set; }
      [Size(10)] string PartB { get; set; }
      [Size(40)] string Payload { get; set; }
    }
  }

  // ----- ONE schema, ONE round trip, every shape ---------------------------------------------------------------------
  //
  // This was ELEVEN test methods and eleven schemas: each created a database for one subset of the types above, read
  // it back, and asserted one thing about it. Creating that schema is the whole cost — six seconds of it on Oracle —
  // and the assertions cost nothing. So it is one schema now, carrying every shape at once, walked through in order.
  //
  // WHERE A FEATURE IS MISSING THE SECTION IS SKIPPED, not the method. Eleven methods could each say
  // `RequiresFeature` and go inconclusive on the vendor that lacks it; one method saying it would take every other
  // shape down with it. So the gates are `if (Supports(…))` and the rest of the walk carries on.
  // A plain int key, with no identity declared. SQLite makes the row id of it and reads an identity back, which no
  // statement could change; the second connect finds nothing to report there, as on a server with a marker.
  [TestMethod]
  public async Task IntKeyWithoutIdentity_IsNoChangeOnTheSecondConnect() {
    var created = await ConnectAsync(new Version(1, 0), [typeof(Intro.IPlainIntKeyRow)]);
    Assert.IsFalse(created.HasErrors, created.Report());
    var again = await ConnectAsync(new Version(1, 0), [typeof(Intro.IPlainIntKeyRow)]);
    Assert.IsFalse(again.HasErrors, again.Report());
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());

    // The key is the application's to give: a row goes in with the value it was given.
    await Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("PlainIntKeyRow")} ({Db.Quote("Id")}, {Db.Quote("Name")}) "
      + "VALUES (41, 'x')");
    Assert.AreEqual(41L, Convert.ToInt64(await Db.ScalarAsync(
      $"SELECT MAX({Db.Quote("Id")}) FROM {Db.GetTableRef("PlainIntKeyRow")}")));
  }

  [TestMethod]
  public async Task RoundTrip_IsClosedOnEveryShape() {
    var shapes = new List<Type> {
      typeof(Intro.IWidget), typeof(Intro.IGadget), typeof(Intro.IDefaultedRow), typeof(Intro.IIdentityRow),
      typeof(Intro.IKeyedRow), typeof(Intro.IChild), typeof(Intro.ICompositeKeyRow),
    };
    if (Db.Provider.Supports(DbFeatures.FilterInIndexes))
      shapes.Add(typeof(Intro.IFilteredRow));

    // Whichever access method this server has, if it has one at all.
    var typedIndexes = new (DbIndexType Type, Type Shape)[] {
      (DbIndexType.Hash, typeof(Intro.IHashIndexedRow)),
      (DbIndexType.FullText, typeof(Intro.ITextIndexedRow)),
      (DbIndexType.Bitmap, typeof(Intro.IBitmapIndexedRow)),
    };
    foreach (var (type, shape) in typedIndexes)
      if (Db.Provider.IndexTypes.Contains(type))
        shapes.Add(shape);

    var generated = (await CreateSchemaAsync(shapes.ToArray())).DbModel;
    // With the check constraints, as an upgrade reads them: a JSON member held as text has one.
    var loaded = await Db.LoadAsync(DbModelParts.Tables | DbModelParts.CheckConstraints);

    // ----- what was created is what comes back --------------------------------------------------------------------
    Assert.AreEqual(generated.Tables.Count, loaded.Tables.Count);
    foreach (var table in generated.Tables) {
      var loadedTable = loaded.FindTable(Db.Schema, table.Name);
      Assert.IsNotNull(loadedTable, $"table '{table.Name}' was not read back");
      Assert.AreEqual(table.Columns.Count, loadedTable.Columns.Count, $"column count for '{table.Name}'");
    }

    // ----- and the comparer finds NOTHING -------------------------------------------------------------------------
    //
    // The acceptance criterion, and the whole reason a provider can be trusted. Old model = what the database HAS,
    // new model = what the application WANTS — the direction an upgrade asks in, so the direction to test in.
    var changes = Db.Provider.CreateDbModelComparer().Compare(loaded, generated);
    Assert.AreEqual(0, changes.Items.Count,
      "the round trip is not closed:" + Environment.NewLine
      + string.Join(Environment.NewLine, changes.Items.Select(c => c.ToString())));
    Assert.IsFalse(changes.HasErrors, string.Join("; ", changes.Errors));

    // ----- index shape survives -----------------------------------------------------------------------------------
    var keyed = loaded.FindTable(Db.Schema, "KeyedRow")!;
    Assert.IsTrue(keyed.Keys.Any(k => k.IsPrimaryKey), "no primary key");
    Assert.IsTrue(keyed.Keys.Any(k => k.IsUnique && !k.IsPrimaryKey), "no unique index");

    if (Db.Provider.Supports(DbFeatures.IncludeColumnsInIndexes)) {
      var covering = keyed.Keys.Single(k => k.IncludedColumns.Count > 0);
      Assert.AreEqual("Score", covering.IncludedColumns[0].Name);
      Assert.AreEqual(2, covering.Columns.Count);
    }
    if (Db.Provider.Supports(DbFeatures.OrderedColumnsInIndexes))
      Assert.IsTrue(keyed.Keys.Any(k => k.Columns.Any(c => c.Descending)),
        "the descending column came back ascending");

    // ----- foreign keys come back with their target and their cascade ---------------------------------------------
    if (Db.Provider.Supports(DbFeatures.ReferentialConstraints)) {
      var child = loaded.FindTable(Db.Schema, "Child")!;
      Assert.AreEqual(2, child.RefConstraints.Count);
      Assert.IsTrue(child.RefConstraints.All(c => c.ToTable.Name == "KeyedRow"));
      Assert.IsTrue(child.RefConstraints.All(c => c.ToKey.IsPrimaryKey));
      Assert.AreEqual(1, child.RefConstraints.Count(c => c.CascadeDelete));
      Assert.AreEqual(2, child.Keys.Count(k => k.IsForeignKey), "a constraint's own column set is a key too");
    }

    // ----- a composite primary key keeps its column ORDER ---------------------------------------------------------
    var pk = loaded.FindTable(Db.Schema, "CompositeKeyRow")!.PrimaryKey!;
    Assert.AreEqual(2, pk.Columns.Count);
    Assert.AreEqual("PartA", pk.Columns[0].Column.Name);
    Assert.AreEqual("PartB", pk.Columns[1].Column.Name);

    // ----- an identity column's hidden sequence is NOT reported ---------------------------------------------------
    //
    // A server may create a sequence OWNED by an identity column and report it like any other. Taken at face value,
    // every model with an identity column comes back with a sequence nobody declared — and the upgrade offers to drop
    // it, forever.
    if (Db.Provider.Supports(DbFeatures.IdentityColumns)) {
      Assert.AreEqual(0, loaded.Sequences.Count,
        "found: " + string.Join(", ", loaded.Sequences.Select(s => s.Name)));
      Assert.IsTrue(loaded.FindTable(Db.Schema, "IdentityRow")!.GetColumn("Id").IsIdentity);
    }

    // ----- an ACCESS METHOD survives the round trip, and an ordinary index reads back CONCRETE ---------------------
    //
    // The second half is the one that would have caught the clustering defect a year of startups carried: every index
    // MIC did not give a type to must come back as the provider's DEFAULT and never as `Default`, or the comparer
    // above would report a difference no upgrade could ever settle.
    Assert.IsTrue(loaded.Tables.SelectMany(t => t.Keys).All(k => k.IndexType != DbIndexType.Default),
      "an index came back with no access method resolved");

    foreach (var (type, shape) in typedIndexes.Where(t => Db.Provider.IndexTypes.Contains(t.Type))) {
      var table = loaded.FindTable(Db.Schema, shape.Name[1..])!;
      var typed = table.Keys.Single(k => k.Flags.HasFlag(KeyFlags.Index) && !k.IsPrimaryKey);
      Assert.AreEqual(type, typed.IndexType, $"the {type} index on '{table.Name}' did not read back as one");
    }

    // ----- a filtered index keeps its filter, and a CHANGED one is still found -------------------------------------
    //
    // The second half keeps the first honest: matching on a stored hash could just as easily mean the comparer has
    // stopped looking, so a filter that really did change must still come back as a rebuild.
    if (Db.Provider.Supports(DbFeatures.FilterInIndexes)) {
      var filtered = loaded.FindTable(Db.Schema, "FilteredRow")!.Keys.Single(k => k.Filter != null);

      // The hash is only ATTACHED where the server REWRITES what it was given; one that keeps the text verbatim needs
      // none, and the filter itself is then the comparison.
      if (!Db.Provider.Supports(DbFeatures.ServerPreservesComments))
        Assert.IsNotNull(filtered.StoredDefinitionHash, "the hash MIC wrote beside the index was not read back");

      var refiltered = DatabaseFor(typeof(Intro.IRefilteredRow)).DbModel;
      var rebuilt = Db.Provider.CreateDbModelComparer().Compare(loaded, refiltered).Items
        .SingleOrDefault(c => c.ObjectKind == DbObjectKind.Key && c.DiffKind == DbDiffKind.Modify);
      Assert.IsNotNull(rebuilt, "a changed filter must still be found");
      StringAssert.Contains(rebuilt.Description!, "filter changed");
    }

    // ----- system schemas never come back --------------------------------------------------------------------------
    //
    // Twice over, because the two ways of asking are different code paths: WITH a schema list, and without one — the
    // DbFirst and migration case, where there is no application model to filter by and the catalogs must not arrive
    // as part of the answer.
    if (Db.Provider.Supports(DbFeatures.Schemas)) {
      Assert.IsFalse(loaded.Schemas.Any(s => Db.Provider.SystemSchemas.Contains(s.Name)));
      Assert.AreEqual(1, loaded.Schemas.Count);

      var unfiltered = await Db.Provider.CreateDbModelLoader(Db.Settings).LoadModelAsync();
      Assert.IsFalse(unfiltered.Schemas.Any(s => Db.Provider.SystemSchemas.Contains(s.Name)));
      Assert.IsTrue(unfiltered.Schemas.Any(s => s.Name == Db.Schema), "the schema just created should be there");
    }

    // ----- and an introspected model has NO entity links -----------------------------------------------------------
    //
    // It cannot have any — there is no entity model behind a database. Everything reading these copes with null, and
    // this is the case that proves it.
    Assert.IsNull(loaded.EntityModel);
    var gadget = loaded.FindTable(Db.Schema, "Gadget")!;
    Assert.IsNull(gadget.Entity);
    Assert.IsTrue(gadget.Columns.All(c => c.Member == null));
    Assert.IsTrue(loaded.Schemas.All(s => s.EntitySchema == null));
  }

  // ----- the objects a database holds beyond tables -------------------------------------------------------------------
  //
  // A VIEW IS THE ONE KIND EVERY VENDOR HAS, and the only one whose DDL is portable enough to assert identically on all
  // five — routines, triggers and types each need per-vendor syntax, and a test written five ways proves less than one
  // written once. So this asserts the two things that are true everywhere: the PARTS GATE, and that a view read back
  // carries its columns.
  //
  // THE GATE IS THE HALF THAT MATTERS MOST. It is what keeps every application's startup from paying for objects an
  // upgrade will never compare, and it fails silently in the direction that costs — nobody notices a loader doing too
  // much until a schema is large.
  [TestMethod]
  public async Task SchemaObjects_LoadedOnlyWhenAskedFor() {
    await Db.EnsureSchemaAsync();
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("Widget")} ({Db.Quote("Id")} int NOT NULL, "
      + $"{Db.Quote("Name")} varchar(50) NULL)");
    await Db.ExecuteAsync($"CREATE VIEW {Db.GetTableRef("WidgetView")} AS SELECT {Db.Quote("Id")}, {Db.Quote("Name")} "
      + $"FROM {Db.GetTableRef("Widget")}");

    // ----- the default reads what an upgrade compares, and nothing else ---------------------------------------------
    var lean = await Db.LoadAsync();
    Assert.AreEqual(DbModelParts.Default, lean.LoadedParts);
    Assert.IsNotNull(lean.FindTable(Db.Schema, "Widget"));
    Assert.AreEqual(0, lean.Views.Count, "the default must not pay for views: it runs at every startup");
    Assert.AreEqual(0, lean.Routines.Count);

    // ----- asking for everything gets it ------------------------------------------------------------------------------
    var full = await Db.LoadAsync(DbModelParts.All);
    Assert.AreEqual(DbModelParts.All, full.LoadedParts, "what was READ has to travel with the model");

    var view = full.Views.SingleOrDefault(v => string.Equals(v.Name, "WidgetView", StringComparison.OrdinalIgnoreCase));
    Assert.IsNotNull(view, "the view was not read back:" + Environment.NewLine
      + string.Join(", ", full.Views.Select(v => v.ToString())));
    Assert.IsFalse(view.IsMaterialized);
    StringAssert.Contains(view.Source ?? "", "Widget", "a view read with its source text carries its select");

    // A view's columns come back through the same type resolution a table's do, which is what makes them showable
    // beside each other.
    Assert.AreEqual(2, view.Columns.Count, "a view presents columns, and a server reports them as it does a table's");
    Assert.IsNotNull(view.Columns.FirstOrDefault(c => string.Equals(c.Name, "Name", StringComparison.OrdinalIgnoreCase)));

    // ----- and a table still knows nothing it was not asked about ------------------------------------------------------
    Assert.AreEqual(0, full.FindTable(Db.Schema, "Widget")!.Triggers.Count, "nothing created a trigger");
  }

  // ----- what a routine RETURNS, where the server can say ---------------------------------------------------------
  //
  // THE ONE THING WORTH ASSERTING IS THE DISTINCTION, not a column list: null means the server cannot say, empty would
  // mean it returns nothing, and the three vendors that answer must not collapse into the two that cannot. So this
  // creates a table function on each server that has one, and asserts its shape came back.
  //
  // FIVE DIALECTS FOR ONE IDEA is unavoidable here — a table function is spelled differently everywhere and does not
  // exist on two of them — which is why this is the only test written per vendor rather than once.
  [TestMethod]
  public async Task RoutineResultShape_ReadWhereDeclared() {
    await Db.EnsureSchemaAsync();
    var sql = TableFunctionSql();
    if (sql == null) {
      // SQLite has no routines at all, and MySQL records no result shape for one. Both are ordinary answers.
      var none = await Db.LoadAsync(DbModelParts.All);
      Assert.IsTrue(none.Routines.All(r => r.ResultColumns == null),
        "a server that cannot describe a result set must leave it NULL, never an empty list");
      return;
    }

    foreach (var statement in sql)
      await Db.ExecuteAsync(statement);

    var model = await Db.LoadAsync(DbModelParts.All);
    var routine = model.Routines.SingleOrDefault(r =>
      string.Equals(r.Name, "shape_fn", StringComparison.OrdinalIgnoreCase));
    Assert.IsNotNull(routine, "the routine was not read back: "
      + string.Join(", ", model.Routines.Select(r => r.Name)));

    Assert.IsNotNull(routine.ResultColumns, "this server DECLARES the shape, so it must not come back unknown");
    Assert.AreEqual(2, routine.ResultColumns.Count);
    Assert.IsTrue(routine.ResultColumns.Any(c => string.Equals(c.Name, "nm", StringComparison.OrdinalIgnoreCase)),
      "columns: " + string.Join(", ", routine.ResultColumns.Select(c => c.Name)));
    Assert.AreEqual(DbRoutineKind.TableFunction, routine.RoutineKind);

    // PostgreSQL's catalog lists the columns of the row set as OUT parameters. The routine keeps what a caller passes.
    if (Db.Provider.ServerKind == DbServerKind.Postgres)
      Assert.AreEqual("min_id", routine.Parameters.Single().Name,
        "parameters: " + string.Join(", ", routine.Parameters.Select(p => p.Name)));

    // Without the shapes a routine is read by name alone, which is all an upgrade compares.
    var byName = (await Db.LoadAsync(DbModelParts.Routines)).Routines.Single(r =>
      string.Equals(r.Name, "shape_fn", StringComparison.OrdinalIgnoreCase));
    Assert.IsNull(byName.ResultColumns, "no result columns were asked for");
    Assert.AreEqual(0, byName.Parameters.Count, "and no parameters");
  }

  /// <summary>A function returning a row set, spelled for this server — or null where the server has none.</summary>
  private string[]? TableFunctionSql() => Db.Provider.ServerKind switch {
    DbServerKind.Postgres => [
      $"CREATE FUNCTION {Db.GetTableRef("shape_fn")}(min_id int) RETURNS TABLE(id int, nm varchar(50)) "
      + "AS $$ SELECT 1, 'a'::varchar WHERE 1 >= min_id $$ LANGUAGE sql"],
    DbServerKind.MsSql => [
      $"CREATE FUNCTION {Db.GetTableRef("shape_fn")}() RETURNS TABLE AS "
      + "RETURN (SELECT CAST(1 AS int) AS id, CAST('a' AS varchar(50)) AS nm)"],
    DbServerKind.Oracle => [
      $"CREATE OR REPLACE TYPE {Db.GetTableRef("shape_rec")} AS OBJECT (id NUMBER, nm VARCHAR2(50))",
      $"CREATE OR REPLACE TYPE {Db.GetTableRef("shape_tab")} AS TABLE OF {Db.GetTableRef("shape_rec")}",
      $"CREATE OR REPLACE FUNCTION {Db.GetTableRef("shape_fn")} RETURN {Db.GetTableRef("shape_tab")} PIPELINED IS "
      + $"BEGIN PIPE ROW({Db.GetTableRef("shape_rec")}(1, 'a')); RETURN; END;"],
    _ => null,          // SQLite has no routines; MySQL declares no result shape for one
  };

  // ----- what a function takes, and what it returns ------------------------------------------------------------------
  //
  // Three servers list the returned value among the parameters, at position 0 and with no name, and two of them name
  // a routine in the parameter catalog by its name alone. So a function of the same name sits in a second schema with
  // other parameters, and the one that was loaded has to report its own two and a return type.
  [TestMethod]
  public async Task RoutineParameters_AreTheOnesACallerPasses() {
    if (GetScalarFunctionSql(Db, "a", "b") is not { } sql)
      return;
    await Db.EnsureSchemaAsync();
    await Db.ExecuteAsync(sql);

    await using var other = await Fixture.CreateIsolatedAsync("otherfn");
    await other.EnsureSchemaAsync();
    await other.ExecuteAsync(GetScalarFunctionSql(other, "x", "y", "z")!);

    var model = await Db.LoadAsync(DbModelParts.RoutineShapes);
    var function = model.Routines.Single(r => string.Equals(r.Name, "scalar_fn", StringComparison.OrdinalIgnoreCase));
    Assert.AreEqual(DbRoutineKind.Function, function.RoutineKind);
    var names = function.Parameters.OrderBy(p => p.Position).Select(p => p.Name.TrimStart('@').ToLowerInvariant());
    Assert.AreEqual("a,b", string.Join(",", names));
    Assert.AreEqual("1,2", string.Join(",", function.Parameters.OrderBy(p => p.Position).Select(p => p.Position)));
    Assert.IsTrue(function.Parameters.All(p => p.Direction == DbParameterDirection.In));
    Assert.IsFalse(string.IsNullOrEmpty(function.ReturnTypeSpec), "the return type is the function's, on every server");
  }

  /// <summary>A function returning one value, spelled for the server of a database; or null where the server has no
  /// routines.</summary>
  private static string? GetScalarFunctionSql(TestDatabase database, params string[] parameters) {
    var name = database.GetTableRef("scalar_fn");
    return database.Provider.ServerKind switch {
      DbServerKind.Postgres =>
        $"CREATE FUNCTION {name}({Declare("{0} int")}) RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql",
      DbServerKind.MsSql => $"CREATE FUNCTION {name}({Declare("@{0} int")}) RETURNS int AS BEGIN RETURN 1 END",
      DbServerKind.MySql => $"CREATE FUNCTION {name}({Declare("{0} int")}) RETURNS int DETERMINISTIC RETURN 1",
      DbServerKind.Oracle =>
        $"CREATE OR REPLACE FUNCTION {name}({Declare("{0} NUMBER")}) RETURN NUMBER IS BEGIN RETURN 1; END;",
      _ => null,
    };

    // ----- local functions
    string Declare(string format) => string.Join(", ", parameters.Select(p => string.Format(format, p)));
  }

  // ----- a PostgreSQL function returning a set of something it does not spell out -----------------------------------
  //
  // RETURNS TABLE lists its columns as OUT parameters. SETOF names a type and lists nothing, so the columns are the
  // type's: a table's, without the one it dropped; a composite type's; one column for a set of values. A set of
  // record with no OUT parameter is the one the server cannot describe, and null is the answer for it, not empty.
  [TestMethod]
  public async Task RoutineResultShape_OfASetOfAType_IsTheTypesColumns() {
    if (Db.Provider.ServerKind != DbServerKind.Postgres)
      return;
    await Db.EnsureSchemaAsync();
    var thing = Db.GetTableRef("thing");
    var pair = Db.GetTableRef("pair");
    string[] statements = [
      $"CREATE TABLE {thing} (id int NOT NULL, nm varchar(50) NULL, gone int NULL)",
      $"ALTER TABLE {thing} DROP COLUMN gone",
      $"CREATE TYPE {pair} AS (a int, b text)",
      $"CREATE FUNCTION {Db.GetTableRef("f_table")}(min_id int) RETURNS SETOF {thing} "
        + $"AS $$ SELECT * FROM {thing} WHERE id >= min_id $$ LANGUAGE sql",
      $"CREATE FUNCTION {Db.GetTableRef("f_type")}() RETURNS SETOF {pair} AS $$ SELECT 1, 'x'::text $$ LANGUAGE sql",
      $"CREATE FUNCTION {Db.GetTableRef("f_values")}() RETURNS SETOF int AS $$ SELECT 1 $$ LANGUAGE sql",
      $"CREATE FUNCTION {Db.GetTableRef("f_record")}() RETURNS SETOF record "
        + "AS $$ SELECT 1, 'x'::text $$ LANGUAGE sql",
      $"CREATE FUNCTION {Db.GetTableRef("f_row")}() RETURNS {thing} "
        + $"AS $$ SELECT * FROM {thing} LIMIT 1 $$ LANGUAGE sql",
    ];
    foreach (var statement in statements)
      await Db.ExecuteAsync(statement);

    var routines = (await Db.LoadAsync(DbModelParts.RoutineShapes)).Routines.ToDictionary(r => r.Name);

    Assert.AreEqual("id integer NOT NULL, nm varchar", GetShape("f_table"));
    Assert.AreEqual("min_id", routines["f_table"].Parameters.Single().Name);
    Assert.AreEqual("thing", routines["f_table"].ReturnTypeSpec, "the returned type by its name");
    Assert.AreEqual("a integer, b text", GetShape("f_type"));
    Assert.AreEqual("f_values integer", GetShape("f_values"), "one column, named as the server names it");
    Assert.IsNull(GetShape("f_record"), "the query that calls it names the columns; the catalog has none");
    foreach (var name in new[] { "f_table", "f_type", "f_values", "f_record" })
      Assert.AreEqual(DbRoutineKind.TableFunction, routines[name].RoutineKind, name);

    // One row of a table is a value and not a set.
    Assert.AreEqual(DbRoutineKind.Function, routines["f_row"].RoutineKind);
    Assert.IsNull(GetShape("f_row"));
    Assert.AreEqual("thing", routines["f_row"].ReturnTypeSpec);

    // ----- local functions
    string? GetShape(string name) => routines[name].ResultColumns is { } columns
      ? string.Join(", ", columns.Select(c => $"{c.Name} {c.TypeRef.TypeSpec}{(c.IsNullable ? "" : " NOT NULL")}"))
      : null;
  }
}
