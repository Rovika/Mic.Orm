using System.Collections.Concurrent;
using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using Mic.Tests.Orm.Db;
using static Mic.Linq.AsyncQueryableExtensions;
using static Mic.Linq.CteQueryableExtensions;

namespace Mic.Tests.Orm.Linq;

// THE QUERY SUITE. What the engine produces, run against a real server on every vendor — where
// `Mic.Tests.Orm`'s translator tests check what the SQL LOOKS LIKE with no server at all, and the DML suite proves the
// by-key read every ORM operation goes through.
//
// ONE SCHEMA AND ONE SEEDING PER VENDOR, made on that vendor's first test and kept — the same arrangement `DmlTests`
// uses and for the same measured reason. It is safe for the same reason too: class-level parallelism runs vendors in
// parallel and the tests within a vendor one at a time. And it is safer here than there, because NOTHING IN THIS SUITE
// WRITES: every test reads the seeded rows, so there is no order dependence to keep out.
public abstract partial class LinqTests : DbTestsBase {
  static readonly ConcurrentDictionary<DbServerKind, SharedDb> _shared = new();

  sealed record SharedDb(TestDatabase Database, EntityApp App);

  protected EntityApp App { get; private set; } = null!;

  [TestInitialize]
  public async Task UseSharedDatabaseAsync() {
    if (!_shared.TryGetValue(Fixture.ServerKind, out var shared))
      _shared[Fixture.ServerKind] = shared = await CreateSharedAsync(Fixture);
    Db = shared.Database;
    App = shared.App;
  }

  async Task<SharedDb> CreateSharedAsync(ProviderFixture fixture) {
    var database = await fixture.CreateIsolatedAsync("linq");
    var app = new LinqApp(database.Schema);
    // CATEGORY IS CACHED for the whole suite, not only for the test about caching — which is the strongest thing that
    // can be said about a cache that must never change an answer. Every test here reads categories, by key and
    // through references, and all of them still expect what the database holds.
    app.SetCached(CacheType.Server, [typeof(Linq.ICategory)]);
    app.Init();
    await app.ConnectToAsync(database.Settings);
    await LinqSeed.FillAsync(app, NewContext(app));
    return new SharedDb(database, app);
  }

  internal static async Task DropSharedDatabasesAsync() {
    foreach (var shared in _shared.Values)
      await shared.Database.DisposeAsync();
    _shared.Clear();
  }

  protected EntitySession NewSession() => new(NewContext(App));

  /// <summary>A FRESH application over the same shared schema, for the tests that declare something at startup.
  /// </summary>
  /// <remarks>The schema is what costs seconds; an application over it costs milliseconds. Filters and caching are
  /// startup declarations, so a test about them needs its own application — and must not put one on the application
  /// every other test is using. It MAPS rather than connects: the tables are already there.
  ///
  /// DECLARED BEFORE `Init`, because the model builder is what applies them.</remarks>
  protected EntityApp NewApp(Action<EntityApp> declare) {
    var app = new LinqApp(Db.Schema);
    declare(app);
    app.Init();
    app.MapTo(Db.Settings);
    return app;
  }

  /// <summary>The build report for declarations that are expected to be REFUSED.</summary>
  /// <remarks>A bad declaration is a model DIAGNOSTIC rather than an exception — it is the builder that inspects one,
  /// and the builder reports every fault of a build rather than throwing on the first.</remarks>
  protected string RefusedApp(Action<EntityApp> declare) {
    var app = new LinqApp(Db.Schema);
    declare(app);
    Assert.ThrowsExactly<InvalidOperationException>(() => app.Init(), "the declaration should have been refused");
    return app.Report();
  }

  // ----- the tests are in the partial files beside this one, one per subject ----------------------------------------
  //
  // ONE METHOD PER SUBJECT, each walking the whole of it. `TESTING.md`: a method that tells a story across a couple of
  // hundred lines costs one fixture and reads as a narrative, where twenty methods proving one fact each cost twenty
  // fixtures and read as a list.

  // The seeded ranks that satisfy a predicate, in rank order — what every expectation is written against.
  private static List<int> Ranks(Func<int, bool> predicate) =>
    [.. Enumerable.Range(1, LinqSeed.ProductCount).Where(predicate)];

  private static List<int> RanksOf(IReadOnlyList<EntityRecord> records) =>
    [.. records.Select(r => ((Linq.IProduct)r.EntityInstance).Rank)];

  private static List<string> NamesOf(IReadOnlyList<EntityRecord> records) =>
    [.. records.Select(r => ((Linq.ICategory)r.EntityInstance).Name).Order()];
}
