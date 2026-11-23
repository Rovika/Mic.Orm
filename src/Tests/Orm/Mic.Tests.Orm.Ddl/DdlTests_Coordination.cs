using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// ONE INSTANCE UPGRADES; THE REST WAIT. The case worth testing is the one a row lock could not have covered: a
// BRAND-NEW database, where there is no row to lock and no table to hold one — which is exactly when several
// instances race, on a fresh deploy where three of them start at once.
public abstract partial class DdlTests {
  static class Lock {
    public interface IWidget {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string Name { get; set; }
    }
  }

  EntityApp LockAppFor(string appName) => AppFor(appName, new Version(1, 0), typeof(Lock.IWidget));
  Database LockDatabaseFor(string appName = "LockTest") => new(LockAppFor(appName).MappedModel(), Db.Settings);

  // ----- the lock itself, in one pass --------------------------------------------------------------------------------
  //
  // Four steps that were four methods. None of them needs a SCHEMA — the lock is taken against the server, and
  // `LockDatabaseFor` only maps a model — so four databases were created to run four assertions that used none of
  // them. Which is also the point being made: this works on a database with no tables at all, where a row lock could
  // never have.
  [TestMethod]
  public async Task UpgradeLock_IsHeldFreedAndSurvivesADeadInstance() {
    RequiresFeature(DbFeatures.SessionLocks);
    var database = LockDatabaseFor();

    // ----- 1. held against a second comer --------------------------------------------------------------------------
    var first = await DbUpgradeLock.TryAcquireAsync(database, TimeSpan.Zero);
    Assert.IsNotNull(first, "the first attempt should get it");

    await using (var second = await DbUpgradeLock.TryAcquireAsync(database, TimeSpan.FromMilliseconds(300)))
      Assert.IsNull(second, "a second connection wanting the same key should get nothing");

    // ----- 2. and free again once released -------------------------------------------------------------------------
    await first.DisposeAsync();
    var reacquired = await DbUpgradeLock.TryAcquireAsync(database, TimeSpan.Zero);
    Assert.IsNotNull(reacquired, "the lock was not released");

    // ----- 3. a DEAD instance releases it too ----------------------------------------------------------------------
    //
    // The reason this is a session LOCK and not a flag column: an instance that dies mid-upgrade must block nobody. A
    // flag would need clearing by hand after a crash, and would eventually wedge a deployment.
    if (await Fixture.TryKillSessionAsync(reacquired.Connection)) {
      // GENEROUS ON PURPOSE, and it costs nothing: the poll returns the moment the lock frees, so a long limit is only
      // ever spent when the test is about to fail anyway. A server noticing a killed session is not instant, and under
      // a five-vendor run with Oracle in it a short limit measures the load rather than the behaviour.
      await using var afterDeath = await DbUpgradeLock.TryAcquireAsync(database, TimeSpan.FromSeconds(30));
      Assert.IsNotNull(afterDeath, "a dead instance is still holding the lock");
    } else {
      await reacquired.DisposeAsync();     // this server cannot be made to kill one; the rest still applies
    }

    // ----- 4. two applications sharing a server do not wait on each other -------------------------------------------
    //
    // The key is derived from the application NAME, so an unrelated application starting up is not a queue.
    await using var mine = await DbUpgradeLock.TryAcquireAsync(LockDatabaseFor("AppOne"), TimeSpan.Zero);
    await using var theirs = await DbUpgradeLock.TryAcquireAsync(LockDatabaseFor("AppTwo"), TimeSpan.Zero);

    Assert.IsNotNull(mine);
    Assert.IsNotNull(theirs);
    Assert.AreNotEqual(mine.Key, theirs.Key);
  }

  // ----- the loop under contention -----------------------------------------------------------------------------------

  [TestMethod]
  public async Task Contention_OneUpgradeHappensAndTheRestWait() {
    RequiresFeature(DbFeatures.SessionLocks);

    // ----- 1. an upgrade that cannot get the lock STOPS -------------------------------------------------------------
    //
    // A KNOWN difference warns and continues; an UNKNOWN state stops. "Somebody may be half-way through changing this
    // schema" is the unknown case — diffing against a database in motion is worse than not starting.
    var settings = Db.Settings;
    var wait = settings.UpgradeLockWait;
    try {
      await using (var blocker = await DbUpgradeLock.TryAcquireAsync(LockDatabaseFor(), TimeSpan.Zero)) {
        Assert.IsNotNull(blocker);
        settings.UpgradeLockWait = TimeSpan.FromMilliseconds(500);

        var blocked = LockAppFor("LockTest");
        var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(() => blocked.ConnectToAsync(settings));
        StringAssert.Contains(ex.Message, "another instance is upgrading");
        // The failure is the UPGRADE's, so it is on the database's report — not the model's, which built fine.
        Assert.IsTrue(blocked.UpgradeFor(settings.DatabaseAlias)?.HasErrors == true);
        Assert.IsFalse(blocked.IsConnected, "a database whose upgrade failed is not one to serve from");
      }

      // ----- 2. and three instances starting at once produce ONE upgrade -------------------------------------------
      //
      // The whole point, against the case that matters: a fresh deploy, an empty database, nothing to lock but the
      // application's own name. Without this, all three create everything and two die on a duplicate object. The
      // database is still empty — the blocked attempt above stopped before it applied anything.
      // GENEROUS ON PURPOSE, and it is not the thing under test: part 1 above proves what happens when the wait runs
      // out, with 500ms and no race to make it uncertain. THIS part is about the coordination — one applies, the rest
      // find nothing to do — and the wait only has to outlast the winner's upgrade.
      //
      // It was 30 seconds, and that is enough on four of the five servers and not always on ORACLE: creating a schema
      // there is CREATE USER, which is seconds rather than milliseconds, and three instances racing on a loaded
      // machine can leave a waiter short. The suite then failed for load rather than for logic — and intermittently,
      // which is the worst way to be told. A budget chosen to be comfortably longer than the work says what the test
      // means.
      settings.UpgradeLockWait = TimeSpan.FromMinutes(2);
      var apps = Enumerable.Range(0, 3).Select(_ => LockAppFor("RaceTest")).ToList();

      await Task.WhenAll(apps.Select(a => a.ConnectToAsync(settings)));
      var reports = apps.Select(a => a.UpgradeFor(settings.DatabaseAlias)!).ToList();

      Assert.AreEqual(1, reports.Count(r => r.IsApplied),
        "exactly one instance should have applied the upgrade:" + Environment.NewLine
        + string.Join(Environment.NewLine + "---" + Environment.NewLine, reports.Select(r => r.Report())));
      Assert.IsTrue(reports.All(r => !r.HasErrors), string.Join(" | ", reports.SelectMany(r => r.Messages)));

      // And the ones that waited found nothing left to do, rather than failing on what the winner had already made.
      Assert.AreEqual(2, reports.Count(r => !r.IsApplied));
    } finally {
      settings.UpgradeLockWait = wait;
    }
  }

  [TestMethod]
  public async Task WithoutSessionLocks_ItSaysSoAndProceeds() {
    // A server with no lock to take is not a server MIC refuses to manage — but it must not look coordinated either.
    // Its own method because its gate is the OPPOSITE of every other one here.
    if (Db.Provider.Supports(DbFeatures.SessionLocks))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has session locks; this is about the servers that do not.");

    var uncoordinated = LockAppFor("Uncoordinated");
    await uncoordinated.ConnectToAsync(Db.Settings);
    var report = uncoordinated.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.IsTrue(report.IsApplied, report.Report());
    Assert.IsTrue(report.Messages.Any(m => m.Message.Contains("not coordinated with other")), report.Report());
  }
}
