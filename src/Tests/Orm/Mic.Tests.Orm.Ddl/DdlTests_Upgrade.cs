using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.DbInfo;

namespace Mic.Tests.Orm.Db;

// BUILD MODEL → READ DATABASE → DIFF → APPLY OR REPORT, against a real server.
public abstract partial class DdlTests {
  static class Upgrade {
    [Entity(Name = "Person")]
    public interface IPersonV1 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }

    [Entity(Name = "Person")]
    public interface IPersonV2 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(80)] string LastName { get; set; }
      [Column(Default = "0")] int Score { get; set; }
    }

    /// <summary>V2 with the default taken away, and nothing else changed.</summary>
    [Entity(Name = "Person")]
    public interface IPersonV3 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(80)] string LastName { get; set; }
      int Score { get; set; }
    }
  }

  /// <summary>Connect at a version, with a settings change that lasts only for this call.</summary>
  /// <remarks>RESTORED IN A FINALLY, which the per-test version did not have to do: `Db.Settings` is ONE object, and a
  /// walk that sets `UpgradeMode.Never` for one step and leaves it set has quietly changed every step after it. That
  /// hazard arrives with the merge and is handled here rather than remembered at each call.</remarks>
  async Task<DbUpgradeReport> ConnectAsync(Version version, Type[] entities, Action<DbSettings>? configure = null,
      Action<EntityApp>? configureApp = null) {
    var settings = Db.Settings;
    var mode = settings.UpgradeMode;
    var dropUnknown = settings.DropUnknownTables;
    var ignored = settings.IgnoreDbObjects;
    var reportPath = settings.UpgradeReportPath;
    configure?.Invoke(settings);
    try {
      var app = BuildApp("UpgradeTest", version, configureApp, entities);
      await app.ConnectToAsync(settings);
      // What the connect READ, kept so a step can assert on the record rather than on raw columns — the two are
      // different claims, and only this one exercises the read path.
      _dbInfo = app.DataAccess.FindDatabase(settings.DatabaseAlias)?.DbInfo;
      return app.UpgradeFor(settings.DatabaseAlias)!;
    } finally {
      settings.UpgradeMode = mode;
      settings.DropUnknownTables = dropUnknown;
      settings.IgnoreDbObjects = ignored;
      settings.UpgradeReportPath = reportPath;
    }
  }

  DbInfoRecord? _dbInfo;

  Task<object?> DbInfoValueAsync(string column) =>
    Db.ScalarAsync($"SELECT {Db.Quote(column)} FROM {Db.GetTableRef("DbInfo")}");

  // ----- ONE database, walked from empty to upgraded ----------------------------------------------------------------
  //
  // This was ten test methods and ten databases, each creating a schema from scratch to reach the state it wanted to
  // assert one thing about. They are steps of a single story, and telling it once costs one schema instead of ten.
  //
  // The order is the story's own: created, re-run, refused for want of permission, refused for being production,
  // forced, altered, left alone, tidied, and finally refused for going backwards. Each step depends on the one before
  // it, which is what made them ten near-identical setups in the first place.
  [TestMethod]
  public async Task Upgrade_WalksFromEmptyToCurrentAndRefusesWhatItShould() {
    // ----- 1. an empty database is created from the model ---------------------------------------------------------
    var report = await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)]);

    Assert.IsTrue(report.IsApplied, report.Report());
    Assert.IsFalse(report.HasErrors, report.Report());
    Assert.IsTrue(await Db.TableExistsAsync("Person"));

    // MIC's own bookkeeping was created by the same mechanism as everything else — no special case anywhere.
    Assert.IsTrue(await Db.TableExistsAsync("DbInfo"));
    Assert.IsNull(report.PreviousVersion, "MIC had never managed this database");
    Assert.AreEqual("1.0", await DbInfoValueAsync("Version"));

    // ----- 2. running again against the same model changes NOTHING ------------------------------------------------
    //
    // The property the whole round trip was built to have: a second startup is a no-op, not a rebuild of everything
    // the server happened to describe differently.
    var second = await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)]);

    Assert.AreEqual(0, second.Changes!.Items.Count, second.Changes.Report());
    Assert.IsFalse(second.IsApplied);
    Assert.AreEqual(new Version(1, 0), second.PreviousVersion);

    // AND WHAT THE SECOND RUN READ is what the first one wrote — the round trip, which the raw-column assertions
    // above cannot make: they read the database directly and never go through `LoadDbInfoAsync`. The stored date is
    // the case that needs saying out loud, since a server without a date type keeps it as text.
    Assert.IsNotNull(_dbInfo);
    Assert.AreEqual(DbInstanceType.Development, _dbInfo.InstanceType, "the default for a row nobody has declared");
    Assert.IsNotNull(_dbInfo.LastUpgradedOn, "the first run applied DDL, so it stamped the date");
    Assert.AreEqual(DateTimeKind.Utc, _dbInfo.LastUpgradedOn.Value.Kind);
    Assert.IsTrue(_dbInfo.Details.ModuleVersions.ContainsKey(DbInfoModule.ModuleName),
      "module versions survive the JSON round trip");

    // A row, so that the upgrade below has data to preserve.
    await Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("Person")} ({Db.Quote("Id")}, {Db.Quote("LastName")}) "
      + $"VALUES ({Db.GuidLiteral(Guid.NewGuid())}, 'Larsen')");

    // ----- 3. told Never, the connect reads nothing: no comparison, no differences, no lock --------------------------
    //
    // The administrator keeps the schema, and an instance started under load spends no time on it. The differences
    // are the database tool's to show.
    var trusted = await ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)],
      settings => settings.UpgradeMode = DbUpgradeMode.Never);

    Assert.IsFalse(trusted.IsApplied);
    Assert.IsNull(trusted.Changes, "the database was not compared");
    Assert.IsNull(trusted.Script);
    Assert.IsFalse(trusted.HasWarnings, trusted.Report());
    Assert.IsNull(trusted.PreviousVersion, "and its record was not read");
    Assert.AreEqual("1.0", await DbInfoValueAsync("Version"), "nothing should have been written");

    // ----- 4. a database that says it is PRODUCTION is left alone --------------------------------------------------
    //
    // The point of storing the instance type IN the database: a connection string that accidentally points at
    // production cannot be talked past, because the answer does not come from the application's config.
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("DbInfo")} SET {Db.Quote("InstanceType")} = "
      + $"{(int)DbInstanceType.Production}");

    // It WARNS and carries on, and can write the script out. Not a refusal to run: an application that will not
    // start gives its developer nothing to work with.
    var scriptPath = Path.Combine(Path.GetTempPath(), $"mic_upgrade_{Guid.NewGuid():N}.sql");
    DbUpgradeReport production;
    try {
      production = await ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)],
        settings => settings.UpgradeReportPath = scriptPath);                              // NonProductionOnly
      StringAssert.Contains(await File.ReadAllTextAsync(scriptPath), "Score");
    } finally {
      File.Delete(scriptPath);
    }

    Assert.AreEqual(DbInstanceType.Production, production.InstanceType);
    Assert.IsFalse(production.IsApplied);
    Assert.IsTrue(production.HasWarnings);
    Assert.IsFalse(production.HasErrors, production.Report());
    Assert.IsTrue(production.Changes!.HasItems);
    Assert.IsTrue(production.Messages.Any(m => m.Message.Contains("PRODUCTION")), production.Report());
    // ENUMERATED, not summarised: "the database does not match the model" is useless when the application then
    // fails on a missing column.
    Assert.IsTrue(production.Messages.Any(m => m.Location != null && m.Message.Contains("Score")),
      "the individual changes were not reported:" + Environment.NewLine + production.Report());
    Assert.AreEqual("1.0", await DbInfoValueAsync("Version"), "nothing should have been written");
    Assert.IsNull((await Db.LoadAsync()).FindTable(Db.Schema, "Person")!.FindColumn("Score"),
      "the production database was changed");

    // ----- 5. and Always overrides that, upgrading and recording it ------------------------------------------------
    var applied = await ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)],
      settings => settings.UpgradeMode = DbUpgradeMode.Always);

    Assert.IsTrue(applied.IsApplied, applied.Report());
    Assert.AreEqual(new Version(1, 0), applied.PreviousVersion);
    Assert.AreEqual("1.1", await DbInfoValueAsync("Version"));
    Assert.AreEqual("Larsen", await Db.ScalarAsync($"SELECT {Db.Quote("LastName")} FROM {Db.GetTableRef("Person")}"),
      "the row did not survive its table being altered");

    // Back to a database MIC may manage, so the steps below are not answering the production question again.
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("DbInfo")} SET {Db.Quote("InstanceType")} = "
      + $"{(int)DbInstanceType.Development}");

    // ----- 6. a default that goes away is DROPPED ------------------------------------------------------------------
    //
    // Worth its own step because "drop the default" is not one statement across servers. Most treat it as a property
    // of the column; SQL Server makes it a CONSTRAINT with a name IT generated, droppable by that name and by nothing
    // else — so the name has to survive introspection into the model or the statement cannot be written at all.
    if (Db.Provider.Supports(DbFeatures.AlterColumn)) {
      Assert.IsNotNull((await Db.LoadAsync()).FindTable(Db.Schema, "Person")!.FindColumn("Score")!.DefaultExpression);

      var dropped = await ConnectAsync(new Version(1, 2), [typeof(Upgrade.IPersonV3)]);

      Assert.IsTrue(dropped.IsApplied, dropped.Report());
      Assert.IsNull((await Db.LoadAsync()).FindTable(Db.Schema, "Person")!.FindColumn("Score")!.DefaultExpression,
        "the default is still there");
    }

    // ----- 7. an unknown table is left alone, and is no difference: nothing is said about it ---------------------
    var current = Db.Provider.Supports(DbFeatures.AlterColumn)
      ? (new Version(1, 2), typeof(Upgrade.IPersonV3))
      : (new Version(1, 1), typeof(Upgrade.IPersonV2));
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("NotOurs")} ({Db.Quote("Id")} varchar(40) NOT NULL)");

    var untouched = await ConnectAsync(current.Item1, [current.Item2]);

    Assert.IsTrue(await Db.TableExistsAsync("NotOurs"), "a table MIC does not know about was dropped");
    Assert.IsFalse(untouched.Messages.Any(m => m.Message.Contains("NotOurs")), untouched.Report());
    if (Db.Provider.Supports(DbFeatures.AlterColumn))
      Assert.AreEqual(0, untouched.Changes!.Items.Count, untouched.Changes.Report());

    // ----- 8. …until DropUnknownTables lets it go -------------------------------------------------------------------
    if (Db.Provider.Supports(DbFeatures.DropColumn)) {   // a server that cannot alter cannot be asked to tidy up
      await ConnectAsync(current.Item1, [current.Item2], settings => settings.DropUnknownTables = true);
      Assert.IsFalse(await Db.TableExistsAsync("NotOurs"));
    }

    // ----- 9. a database AHEAD of the application — FOLLOWED here, refused where it matters -------------------------
    //
    // On a DEVELOPMENT database this is an ordinary morning: the developer reverted their own change, or pulled a
    // branch without it. The database follows the entity model, which is the principle the upgrade exists to serve —
    // and it does not acquire an exception when the model has moved backwards. So the schema comes back into line,
    // the dropped column with it.
    var behind = await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)]);
    Assert.IsTrue(behind.Messages.Any(m => m.Message.Contains("ahead of this application")), behind.Report());
    Assert.IsTrue(behind.Messages.Any(m => m.Message.Contains("development database")), behind.Report());
    Assert.IsTrue(behind.IsApplied, behind.Report());
    Assert.IsNull((await Db.LoadAsync()).FindTable(Db.Schema, "Person")!.FindColumn("Score"),
      "the schema should have followed the model back");
    Assert.AreEqual("1.0", await DbInfoValueAsync("Version"), "and the recorded version with it");

    // ----- 10. …and on a database that says it is PRODUCTION, the same finding STOPS everything ---------------------
    //
    // Where step 9 followed the model back, this refuses to: rolling a production schema backwards would destroy what
    // the newer deployment added. Ahead again first, since step 9 brought this one into line.
    await ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)],
      settings => settings.UpgradeMode = DbUpgradeMode.Always);
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("DbInfo")} SET {Db.Quote("InstanceType")} = "
      + $"{(int)DbInstanceType.Production}");

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)]));
    StringAssert.Contains(ex.Message, "ahead of this application");
    StringAssert.Contains(ex.Message, "never rolls back");

    // …and it stopped BEFORE touching anything, which is the point of stopping.
    Assert.IsNotNull((await Db.LoadAsync()).FindTable(Db.Schema, "Person")!.FindColumn("Score"));
  }

  // ----- and the one that needs a BROKEN database -------------------------------------------------------------------
  //
  // Its own database, because it deliberately leaves one half-upgraded — which is the state every step of the walk
  // above assumes it is not in.
  [TestMethod]
  public async Task AFailedUpgradeIsRECORDEDAndTheNextRunFinishesTheJob() {
    // The consequence of a non-transactional upgrade, made visible instead of left to be inferred.
    RequiresFeature(DbFeatures.AlterColumn);
    await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)]);

    // Something is in the way: a column of the right name and the wrong type, which the ALTER cannot get past.
    await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("Person")} ADD {Db.Quote("Score")} varchar(20)");
    await Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("Person")} "
      + $"({Db.Quote("Id")}, {Db.Quote("LastName")}, {Db.Quote("Score")}) "
      + $"VALUES ({Db.GuidLiteral(Guid.NewGuid())}, 'X', 'nope')");

    await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)]));

    Assert.IsTrue(await Db.ScalarBoolAsync($"SELECT {Db.Quote("LastUpgradeFailed")} FROM {Db.GetTableRef("DbInfo")}"));
    Assert.IsNotNull(await DbInfoValueAsync("LastUpgradeError"));

    // And the next run says so, rather than leaving somebody to work it out.
    await Db.ExecuteAsync($"DELETE FROM {Db.GetTableRef("Person")}");
    await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("Person")} DROP COLUMN {Db.Quote("Score")}");
    var recovered = await ConnectAsync(new Version(1, 1), [typeof(Upgrade.IPersonV2)]);

    Assert.IsTrue(recovered.Messages.Any(m => m.Message.Contains("FAILED part-way")), recovered.Report());
    Assert.IsTrue(recovered.IsApplied);
    Assert.IsFalse(await Db.ScalarBoolAsync($"SELECT {Db.Quote("LastUpgradeFailed")} FROM {Db.GetTableRef("DbInfo")}"));
  }

  // ----- a sequence with a start of its own survives a second connect -----------------------------------------------
  //
  // A server that keeps no record of a sequence's start, Oracle, reports none, and the comparer leaves the start out;
  // read as the minimum value instead, the sequence differed on every connect and the upgrade altered it each time.
  [TestMethod]
  public async Task ASequenceWithAStart_IsNotADifferenceOnTheNextConnect() {
    RequiresFeature(DbFeatures.Sequences);
    void Register(EntityApp app) => app.Modules[0].RegisterSequence("upgrade_numbers", start: 1000, increment: 10);

    var created = await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)], configureApp: Register);
    Assert.IsTrue(created.IsApplied, created.Report());

    var second = await ConnectAsync(new Version(1, 0), [typeof(Upgrade.IPersonV1)], configureApp: Register);
    Assert.AreEqual(0, second.Changes!.Items.Count, second.Changes.Report());
  }
}
