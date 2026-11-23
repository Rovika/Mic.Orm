using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

// Array columns are PostgreSQL's alone: a member that is an array or a List of scalars is an array column there and a
// model error on every other server, so these tests run on PostgreSQL only and build a model of their own.
[TestClass]
public sealed class PostgresArrays {
  static readonly PostgresFixture Fixture = new();
  TestDatabase _db = null!;

  public TestContext TestContext { get; set; } = null!;

  [TestInitialize]
  public async Task SetUpAsync() {
    if (!TestVendors.IsEnabled(Fixture.ServerKind))
      Assert.Inconclusive(TestVendors.WhyNot(Fixture.ServerKind));
    _db = await Fixture.CreateIsolatedAsync(TestContext.TestName ?? "pgarrays");
  }

  [TestCleanup]
  public async Task TearDownAsync() {
    if (_db != null)
      await _db.DisposeAsync();
  }

  public interface ISensor {
    [PrimaryKey] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }
    int[] Counts { get; set; }
    List<string> Tags { get; set; }
    double[]? Samples { get; set; }
  }

  [TestMethod]
  public async Task ArrayMember_IsAnArrayColumnSavedAndReadBack() {
    var app = BuildApp();
    var report = await app.ConnectToAsync(_db.Settings);
    Assert.IsTrue(report.IsApplied, report.Report());

    // The columns are the server's array types.
    var probe = app.OpenSession();
    var types = await probe.RunSqlQueryAsync<string>(
      "SELECT udt_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'Sensor' "
      + "AND data_type = 'ARRAY' ORDER BY column_name", ("schema", _db.Schema));
    CollectionAssert.AreEqual(new[] { "_int4", "_float8", "_text" }, types.ToArray());

    var id = Guid.NewGuid();
    { var session = app.OpenSession();
      var sensor = session.NewEntity<ISensor>();
      sensor.Id = id;
      sensor.Name = "north";
      sensor.Counts = [3, 1, 2];
      sensor.Tags = ["roof", "wind"];
      await session.SaveChangesAsync(); }

    { var session = app.OpenSession();
      var sensor = (await session.GetEntityAsync<ISensor>(id))!;
      CollectionAssert.AreEqual(new[] { 3, 1, 2 }, sensor.Counts);
      CollectionAssert.AreEqual(new[] { "roof", "wind" }, sensor.Tags);
      Assert.IsNull(sensor.Samples);

      // An assignment is the change; the items of the array held are not watched.
      sensor.Counts = [9];
      sensor.Samples = [0.5, 1.5];
      sensor.Tags = [];
      await session.SaveChangesAsync(); }

    { var session = app.OpenSession();
      var sensor = await session.EntitySet<ISensor>().Where(s => s.Name == "north").SingleAsync();
      CollectionAssert.AreEqual(new[] { 9 }, sensor.Counts);
      CollectionAssert.AreEqual(new[] { 0.5, 1.5 }, sensor.Samples);
      Assert.AreEqual(0, sensor.Tags.Count); }

    // A change inside the array held is not seen, so the save writes nothing.
    { var session = app.OpenSession();
      var sensor = (await session.GetEntityAsync<ISensor>(id))!;
      sensor.Counts[0] = 100;
      await session.SaveChangesAsync(); }
    { var session = app.OpenSession();
      Assert.AreEqual(9, (await session.GetEntityAsync<ISensor>(id))!.Counts[0]); }

    // The loader reads the columns back as the types the model has: a second connect finds nothing to change.
    var second = await BuildApp().ConnectToAsync(_db.Settings);
    Assert.AreEqual(0, second.Changes!.Items.Count, second.Changes.Report());
  }

  [TestMethod]
  public async Task ArrayMember_IsQueriedByItsItems() {
    var app = BuildApp();
    await app.ConnectToAsync(_db.Settings);
    { var session = app.OpenSession();
      Add(session, "north", [3, 1, 2], ["roof", "wind"]);
      Add(session, "south", [7], ["roof"]);
      Add(session, "spare", [], []);
      await session.SaveChangesAsync(); }
    var query = app.OpenSession();
    IQueryable<ISensor> sensors = query.EntitySet<ISensor>();

    // Contains: an item equal to a value.
    var roofed = await sensors.Where(s => s.Tags.Contains("roof")).OrderBy(s => s.Name).Select(s => s.Name)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "north", "south" }, roofed.ToArray());
    StringAssert.Contains(((Mic.Orm.Runtime.EntitySession)query).LastCommands.Single().Sql, "unnest(");

    // Any: with a condition on the item, and with none.
    var high = await sensors.Where(s => s.Counts.Any(c => c > 5)).Select(s => s.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { "south" }, high.ToArray());
    var filled = await sensors.Where(s => s.Counts.Any()).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { "north", "south" }, filled.ToArray());

    // Count(): the number of items, and of those a condition takes.
    var counted = await sensors.OrderBy(s => s.Name)
      .Select(s => new { s.Name, Items = s.Counts.Count(), Odd = s.Counts.Count(c => c % 2 == 1) }).ToListAsync();
    Assert.AreEqual("north:3:2 south:1:1 spare:0:0",
      string.Join(" ", counted.Select(c => $"{c.Name}:{c.Items}:{c.Odd}")));

    // SelectMany: the items spread beside their row.
    var spread = await sensors.SelectMany(s => s.Counts, (s, c) => new { s.Name, Item = c })
      .OrderBy(x => x.Name).ThenBy(x => x.Item).ToListAsync();
    Assert.AreEqual("north:1 north:2 north:3 south:7", string.Join(" ", spread.Select(x => $"{x.Name}:{x.Item}")));

    // An array's Length and a List's Count are the count of the items.
    var sized = await sensors.Where(s => s.Counts.Length >= 1 && s.Tags.Count == 2).Select(s => s.Name)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "north" }, sized.ToArray());
    var lengths = await sensors.OrderBy(s => s.Name).Select(s => new { s.Name, s.Counts.Length, s.Tags.Count })
      .ToListAsync();
    Assert.AreEqual("north:3:2 south:1:1 spare:0:0",
      string.Join(" ", lengths.Select(x => $"{x.Name}:{x.Length}:{x.Count}")));
  }

  private static void Add(IEntitySession session, string name, int[] counts, List<string> tags) {
    var sensor = session.NewEntity<ISensor>();
    sensor.Id = Guid.NewGuid();
    sensor.Name = name;
    sensor.Counts = counts;
    sensor.Tags = tags;
  }

  private EntityApp BuildApp() {
    var app = new EntityApp("PgArrays", new Version(1, 0), new EntityAppSettings { DefaultSchema = _db.Schema });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(ISensor));
    app.AddModule(module);
    app.Init();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }
}
