using System.Collections.Concurrent;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE DATA HALF. Everything a running application does to rows: create, read back, update, delete, order a save,
// batch it, announce it, validate it — plus the connection and failure-classification plumbing underneath, which is
// what a data operation meets when something goes wrong.
//
// ONE DATABASE PER VENDOR, MADE ONCE. This is the suite's whole reason for existing apart from the DDL one. These
// tests are DELAYED by schema churn rather than about it, so they share a schema: `DmlApp` is built and connected on
// the first test of a vendor and kept for the rest. The measurement that forced it — Oracle at 6.0 seconds per test,
// almost all of it `CREATE USER` / `DROP USER` — is in the task doc.
//
// SHARING IS SAFE HERE BECAUSE OF THE PARALLELISM, not by luck: `ExecutionScope.ClassLevel` means tests within one
// vendor class run one at a time, so nothing races. If that is ever changed to method level, this breaks. And sharing
// does not become order-dependence because of two rules, which every test in this suite keeps:
//
//   * a test that READS uses rows it made itself, or the seeded ones, which nothing modifies;
//   * a test that WRITES makes its own rows — Guid or identity keys, so nothing collides — and leaves them behind.
//     Cleaning up costs round trips and buys nothing.
//
// Anything that cannot live with that belongs in `Mic.Tests.Orm.Ddl`, which still takes a database per test.
public abstract partial class DmlTests : DbTestsBase {
  /// <summary>The one application per vendor, with its database — made on that vendor's first test.</summary>
  /// <remarks>A DICTIONARY OF VALUES, and nothing cleverer. It used to be
  /// `ConcurrentDictionary&lt;DbServerKind, Lazy&lt;Task&lt;Shared&gt;&gt;&gt;`, which bought a guarantee this suite
  /// does not need and cost a defect it does.
  ///
  /// NOT NEEDED, because tests WITHIN a vendor run sequentially — class-level parallelism puts vendors in parallel and
  /// nothing else. So two tests never race to create the same entry, and the once-only guarantee `Lazy` exists for is
  /// already provided by the schedule. Different vendors use different KEYS, which is the only concurrency here and
  /// what the concurrent dictionary is still for.
  ///
  /// AND IT COST THIS: a `Lazy&lt;Task&lt;T&gt;&gt;` caches the task it produced, INCLUDING A FAULTED ONE. One
  /// transient failure creating a vendor's database — a busy container under a full-solution run — was then re-awaited
  /// by every later test in the class, each failing with the same stale exception. A single blip became forty-five
  /// failures, which is what made it look like noise rather than one event.
  ///
  /// Storing the VALUE means a failure stores nothing, so the next test simply tries again.</remarks>
  static readonly ConcurrentDictionary<DbServerKind, SharedDb> _shared = new();

  protected EntityApp App { get; private set; } = null!;

  sealed record SharedDb(TestDatabase Database, EntityApp App);

  [TestInitialize]
  public async Task UseSharedDatabaseAsync() {
    if (!_shared.TryGetValue(Fixture.ServerKind, out var shared))
      _shared[Fixture.ServerKind] = shared = await CreateSharedAsync(Fixture);
    Db = shared.Database;
    App = shared.App;
  }

  static async Task<SharedDb> CreateSharedAsync(ProviderFixture fixture) {
    var database = await fixture.CreateIsolatedAsync("dml");
    var app = new DmlApp(database.Schema);
    app.Init();

    // CONNECTED, not mapped-and-DDL'd by hand: the upgrade is what creates the tables, so the suite's own schema
    // arrives through the path an application uses.
    await app.ConnectToAsync(database.Settings);
    return new SharedDb(database, app);
  }

  /// <summary>Drop every shared schema at the end of the run.</summary>
  /// <remarks>Nothing else can: a shared database outlives every test that used it, so a `[TestCleanup]` would take it
  /// away from the tests still to come. The containers are ephemeral, so this is tidiness rather than correctness —
  /// but a developer running the suite forty times in a day would otherwise leave forty Oracle users behind.</remarks>
  internal static async Task DropSharedDatabasesAsync() {
    foreach (var shared in _shared.Values)
      await shared.Database.DisposeAsync();
    _shared.Clear();
  }

  /// <summary>A session on the shared application.</summary>
  protected EntitySession NewSession() => new(NewContext(App));

  /// <summary>A FRESH application over the same shared database — for a test that hooks events.</summary>
  /// <remarks>The schema is what costs seconds; an application over it costs milliseconds. So the database is shared
  /// and the application need not be, which matters for exactly one reason: handlers hooked on
  /// `EntityTypeInfo.Events` are part of the MODEL and are never unsubscribed, so a test that hooks one on the shared
  /// application has hooked it for every test after it.
  ///
  /// It maps rather than connects: the tables are already there, and an upgrade would find nothing to do and say so
  /// in a report nobody reads.</remarks>
  protected EntityApp NewApp() {
    var app = new DmlApp(Db.Schema);
    app.Init();
    app.MapTo(Db.Settings);
    return app;
  }

  /// <summary>The shared application's database, for the tests that drive the pieces directly.</summary>
  /// <remarks>What replaces `DatabaseFor(types)` and `CreateSchemaAsync(types)` in this suite: both of those BUILD a
  /// schema, and the schema is already there. Running their DDL against it failed with "schema already exists", which
  /// is the shared fixture telling a test it is not the only one here.</remarks>
  protected Database SharedDatabase => App.DataAccess.FindDatabase()!;
}
