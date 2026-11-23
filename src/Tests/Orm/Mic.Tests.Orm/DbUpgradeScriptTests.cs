using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;

namespace Mic.Tests.Orm;

// A change set in, an ordered script out. These tests assert the ORDER, not the text — the text is ORM_09's subject and
// is pinned there. What matters here is that a statement never runs before the thing it needs.
[TestClass]
public class DbUpgradeScriptTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  private static readonly PostgresProvider Provider = new();

  private static DbModel ModelOf(params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module);
    app.TryInit();
    if (app.Initialized)
      AppInitHelper.CreateDatabase(app, new DbSettings(Provider, NoServer));
    Assert.IsTrue(app.Initialized, app.Report());
    return app.MappedModel();
  }

  private static DbUpgradeScript ScriptFor(Type[] before, Type[] after,
      params DbMigration[] migrations) {
    var changes = Provider.CreateDbModelComparer().Compare(ModelOf(before), ModelOf(after));
    Assert.IsFalse(changes.HasErrors, string.Join("; ", changes.Errors));
    return new DbUpgradeScriptBuilder(Provider).Build(changes, migrations);
  }

  /// <summary>Where a stage first appears in the script. -1 when it does not.</summary>
  private static int IndexOf(DbUpgradeScript script, DbUpgradeStage stage) {
    for (var i = 0; i < script.Steps.Count; i++)
      if (script.Steps[i].Stage == stage)
        return i;
    return -1;
  }

  // ----- the nullable sequence ---------------------------------------------------------------------------------------

  [Entity(Name = "Person")]
  private interface IPersonBefore {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  [Entity(Name = "Person")]
  private interface IPersonAfter {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Age { get; set; }                       // required, and nothing supplies a value
  }

  [Entity(Name = "Person")]
  private interface IPersonWithDefault {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    [Column(Default = "0")] int Age { get; set; }
  }

  [Entity(Name = "Person")]
  private interface IPersonWithRequiredBlob {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    byte[] Payload { get; set; }                // required, and no placeholder exists for a blob
  }
  [Entity(Name = "Child")]
  private interface IChildRequiredRef {
    [PrimaryKey] Guid Id { get; set; }
    IParent Parent { get; set; }               // required reference: a placeholder would point at nothing
  }

  [Entity(Name = "Child")]
  private interface IChildOptionalRef {
    [PrimaryKey] Guid Id { get; set; }
    IParent? Parent { get; set; }
  }
  private interface IParent {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  [Entity(Name = "Child")]
  private interface IChildWithRef {
    [PrimaryKey] Guid Id { get; set; }
    IParent Parent { get; set; }
  }

  [Entity(Name = "Child")]
  private interface IChildWithoutRef {
    [PrimaryKey] Guid Id { get; set; }
  }
  private interface IWasCalledThis {
    [PrimaryKey] Guid Id { get; set; }
    string Title { get; set; }
  }

  [OldNames("WasCalledThis")]
  private interface INowCalledThat {
    [PrimaryKey] Guid Id { get; set; }
    [OldNames("Title")] string Caption { get; set; }
  }
  [Entity(Name = "Filtered")]
  [Index("Name", Filter = "\"Name\" IS NOT NULL")]
  private interface IFilteredRowBefore {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  [Entity(Name = "Filtered")]
  [Index("Name", Filter = "\"Name\" <> ''")]
  private interface IFilteredRowAfter {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
  }

  // ----- ONE method per RULE, rather than one per assertion ----------------------------------------------------------
  //
  // Twelve methods became three. They were NOT duplicated by the five-vendor upgrade, which was the expectation going
  // in: that suite proves a script RUNS, and these prove what it SAYS — the stages it produces, the warnings it
  // raises, and the order it puts them in. A server can only tell you a statement failed; it cannot tell you that a
  // warning was the right warning, or that a drop was correctly judged non-destructive.
  //
  // So this is a merge and not a cull, and the three methods are the three questions: what a new column costs, what
  // must happen before what, and what the artifact reads like.

  [TestMethod]
  public void ANewCOLUMNIsAddedFilledAndTightenedOrElseWarnedAbout() {
    // ----- a required column is added nullable, filled, and then tightened -----------------------------------------
    //
    // All THREE steps, and the middle one is what makes the sequence work UNAIDED. Without it, the ordinary model
    // change — a new required column — is the one that fails, on any table that already has rows.
    var script = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonAfter)]);
    var steps = script.Steps.Where(s => s.Ddl?.ObjectName == "Age").ToList();

    Assert.AreEqual(3, steps.Count, script.ToText());
    Assert.AreEqual(DbUpgradeStage.AddColumns, steps[0].Stage);
    StringAssert.Contains(steps[0].Text, "ADD");
    Assert.IsFalse(steps[0].Text.Contains("NOT NULL"), "the column is added nullable: " + steps[0].Text);

    Assert.AreEqual(DbUpgradeStage.InitColumns, steps[1].Stage);
    StringAssert.Contains(steps[1].Text, "UPDATE");
    StringAssert.Contains(steps[1].Text, "IS NULL");     // fills only what is empty; never overwrites

    Assert.AreEqual(DbUpgradeStage.AlterColumns, steps[2].Stage);
    StringAssert.Contains(steps[2].Text, "SET NOT NULL");
    Assert.AreEqual(0, script.Warnings.Count, string.Join("; ", script.Warnings));

    // ----- unless it has a DEFAULT, in which case it goes in required outright --------------------------------------
    //
    // Nothing to wait for — the server fills the existing rows itself.
    var withDefault = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonWithDefault)])
      .Steps.Where(s => s.Ddl?.ObjectName == "Age").ToList();
    Assert.AreEqual(1, withDefault.Count);
    StringAssert.Contains(withDefault[0].Text, "NOT NULL");

    // ----- a migration runs BETWEEN the two halves -------------------------------------------------------------------
    //
    // Attached to a STAGE, running after it: after the columns exist, before anything tightens them.
    var fill = new DbMigration("fill-age", DbUpgradeStage.AddColumns, "UPDATE \"app\".\"Person\" SET \"Age\" = 0");
    var migrated = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonAfter)], fill).Steps.ToList();
    var add = migrated.FindIndex(s => s.Stage == DbUpgradeStage.AddColumns && s.Ddl != null);
    var migration = migrated.FindIndex(s => s.Migration != null);
    var tighten = migrated.FindIndex(s => s.Text.Contains("SET NOT NULL"));
    Assert.IsTrue(add >= 0 && migration > add && tighten > migration,
      $"add={add}, migration={migration}, tighten={tighten}");

    // ----- a type with NO placeholder is warned about rather than guessed at -----------------------------------------
    //
    // Said at generation time, where it can still be acted on — instead of discovered when the server rejects the
    // tightening. THIS is what a database cannot tell you: it would only ever report the failure.
    var blob = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonWithRequiredBlob)]);
    Assert.AreEqual(1, blob.Warnings.Count, blob.ToText());
    StringAssert.Contains(blob.Warnings[0], "Payload");
    StringAssert.Contains(blob.Warnings[0], "migration");
    Assert.IsFalse(blob.Steps.Any(s => s.Stage == DbUpgradeStage.InitColumns));
    Assert.IsTrue(blob.Steps.Any(s => s.Text.Contains("SET NOT NULL")), "the tightening is still attempted");

    // ----- and a FOREIGN KEY is never backfilled ---------------------------------------------------------------------
    //
    // The carve-out that matters most. A placeholder Guid points at nothing, so filling one would trade a NOT NULL
    // failure for a REFERENTIAL failure — which is worse: it fails later, and the value it leaves behind looks like
    // real data.
    var reference = ScriptFor(
      [typeof(IParent), typeof(IChildOptionalRef)], [typeof(IParent), typeof(IChildRequiredRef)]);
    Assert.IsFalse(reference.Steps.Any(s => s.Stage == DbUpgradeStage.InitColumns), reference.ToText());
    Assert.AreEqual(1, reference.Warnings.Count, reference.ToText());
    StringAssert.Contains(reference.Warnings[0], "FOREIGN KEY");
    StringAssert.Contains(reference.Warnings[0], "point at nothing");
  }

  [TestMethod]
  public void UpgradeScript_OrdersWhatMustHappenBeforeWhat() {
    // ----- renames run FIRST ---------------------------------------------------------------------------------------
    //
    // So that every later stage names the object as the model does. A rename that ran last would have every statement
    // before it referring to something under two names.
    var renamed = ScriptFor([typeof(IWasCalledThis)], [typeof(INowCalledThat)]);
    var renamedTable = IndexOf(renamed, DbUpgradeStage.RenameTables);
    var renamedColumn = IndexOf(renamed, DbUpgradeStage.RenameColumns);
    Assert.IsTrue(renamedTable >= 0 && renamedColumn == renamedTable + 1, renamed.ToText());
    Assert.IsFalse(renamed.Steps.Take(renamedColumn + 1).Any(s => s.IsDestructive));
    StringAssert.Contains(renamed.Steps[renamedTable].Text, "RENAME TO");

    // ----- a new table brings its keys and its constraints, in that order -------------------------------------------
    //
    // The diff reports ONE change for a new table and is right to — but a table is not just its columns.
    var added = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonBefore), typeof(IParent), typeof(IChildWithRef)]);
    var table = IndexOf(added, DbUpgradeStage.AddTables);
    var keys = IndexOf(added, DbUpgradeStage.AddKeys);
    var constraints = IndexOf(added, DbUpgradeStage.AddRefConstraints);
    Assert.IsTrue(table >= 0 && keys > table && constraints > keys,
      $"table={table}, keys={keys}, constraints={constraints}{Environment.NewLine}{added.ToText()}");
    Assert.IsTrue(added.Statements.Any(s => s.Kind == DdlKind.AddPrimaryKey));
    Assert.IsTrue(added.Statements.Any(s => s.Kind == DdlKind.AddRefConstraint));

    // ----- and going the other way, a foreign key goes before the key and the column it needs -----------------------
    //
    // The order that is load-bearing rather than tidy: a constraint references a key, a key names a column, and a
    // server refuses to drop either while something still points at it.
    var dropped = ScriptFor([typeof(IParent), typeof(IChildWithRef)], [typeof(IParent), typeof(IChildWithoutRef)]);
    var constraint = IndexOf(dropped, DbUpgradeStage.DropRefConstraints);
    var key = IndexOf(dropped, DbUpgradeStage.DropKeys);
    var column = IndexOf(dropped, DbUpgradeStage.DropColumns);
    Assert.IsTrue(constraint >= 0 && key > constraint && column > key,
      $"constraint={constraint}, key={key}, column={column}{Environment.NewLine}{dropped.ToText()}");

    // ----- everything DESTRUCTIVE comes after everything else -------------------------------------------------------
    //
    // Which is what lets a migration read from a column in the same script that later drops it.
    var mixed = ScriptFor([typeof(IParent), typeof(IChildWithRef)], [typeof(IPersonBefore)]).Steps.ToList();
    var lastConstructive = mixed.FindLastIndex(s => !s.IsDestructive);
    var firstDestructive = mixed.FindIndex(s => s.IsDestructive);
    Assert.IsTrue(firstDestructive > lastConstructive,
      $"first destructive={firstDestructive}, last constructive={lastConstructive}");

    // ----- a changed key is dropped and RECREATED, and that is not destructive ---------------------------------------
    //
    // One Modify from the comparer becomes two statements here: there is no incremental alter for an index. NOT
    // destructive, because the key comes back — the answer comes from the CHANGE, not from the statement, which reads
    // exactly like a drop.
    var rebuilt = ScriptFor([typeof(IFilteredRowBefore)], [typeof(IFilteredRowAfter)]);
    var droppedKey = rebuilt.Steps.Single(s => s.Stage == DbUpgradeStage.DropKeys);
    StringAssert.Contains(droppedKey.Text, "DROP INDEX");
    StringAssert.Contains(rebuilt.Steps.First(s => s.Stage == DbUpgradeStage.AddKeys).Text, "CREATE INDEX");
    Assert.IsFalse(droppedKey.IsDestructive);
    Assert.IsFalse(rebuilt.HasDestructive, rebuilt.ToText());
  }

  [TestMethod]
  public void UpgradeScript_IsAnArtifactSomebodyReads() {
    // Compare-and-report: everything the upgrade WOULD do, before anything runs.
    var fill = new DbMigration("fill-age", DbUpgradeStage.AddColumns, "UPDATE \"app\".\"Person\" SET \"Age\" = 0",
      "existing people get zero");
    var text = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonAfter)], fill).ToText();

    StringAssert.Contains(text, "-- ----- AddColumns -----");
    StringAssert.Contains(text, "-- migration: fill-age — existing people get zero");
    StringAssert.Contains(text, "SET NOT NULL;");

    // And an empty change set is an empty script, not a script of nothing.
    var empty = ScriptFor([typeof(IPersonBefore)], [typeof(IPersonBefore)]);
    Assert.IsTrue(empty.IsEmpty);
    Assert.AreEqual("", empty.ToText());
  }
}
