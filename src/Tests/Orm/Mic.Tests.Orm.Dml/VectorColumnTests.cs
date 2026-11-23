using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.SqlServer;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A vector member, on the servers that have a vector type: a float[] marked [Vector] is a vector column, and the
// distance between two vectors is the server's. The tests build a model of their own, since the member is an error
// of the model everywhere else; a class for each server says what differs there.
public abstract class VectorColumnTests {
  protected TestDatabase _db = null!;

  public TestContext TestContext { get; set; } = null!;

  protected abstract ProviderFixture Fixture { get; }

  /// <summary>Gets the settings the tests connect with: the test database's own, unless the server wants a provider
  /// made for its vector type.</summary>
  protected virtual DbSettings Settings => _db.Settings;

  /// <summary>Gets the query that reads the types of the columns Embedding and Summary off the catalog, in that
  /// order, as <c>vector(n)</c>; its one parameter is the schema.</summary>
  protected abstract string ColumnTypesQuery { get; }

  /// <summary>Gets what the server says of a vector of another length than the column's.</summary>
  protected abstract string WrongLengthMessage { get; }

  /// <summary>Gets the text a statement has for the distance by a metric.</summary>
  protected abstract string GetMetricText(VectorMetric metric);

  /// <summary>Gets the cast a bound vector is written with, or <see langword="null"/> where it is bound as it is.
  /// </summary>
  protected virtual string? BoundVectorCast => null;

  /// <summary>Gets a value that indicates whether a row with no vector, which has no distance, sorts before the
  /// rows that have one.</summary>
  protected virtual bool SortsNoDistanceFirst => false;

  [TestInitialize]
  public async Task SetUpAsync() {
    if (!TestVendors.IsEnabled(Fixture.ServerKind))
      Assert.Inconclusive(TestVendors.WhyNot(Fixture.ServerKind));
    _db = await Fixture.CreateIsolatedAsync(TestContext.TestName ?? "vectors");
  }

  [TestCleanup]
  public async Task TearDownAsync() {
    if (_db != null)
      await _db.DisposeAsync();
  }

  public interface IChunk {
    [PrimaryKey] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }
    int Tenant { get; set; }
    [Vector(3), Index] float[] Embedding { get; set; }
    [Vector(4)] float[]? Summary { get; set; }
    [Vector(3, Metric = VectorMetric.Euclidean)] float[]? Position { get; set; }
    [Vector(3, Metric = VectorMetric.DotProduct)] float[]? Weights { get; set; }
  }

  // A vector left out of a read of its entity: a query measures by it all the same.
  public interface IPassage {
    [PrimaryKey] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }
    [OnDemand, Vector(3)] float[] Embedding { get; set; }
  }

  [TestMethod]
  public async Task VectorMember_IsAVectorColumnSavedAndReadBack() {
    var app = BuildApp();
    var report = await app.ConnectToAsync(Settings);
    Assert.IsTrue(report.IsApplied, report.Report());

    // The columns are the server's vector type, with the dimensions the members state.
    var probe = app.OpenSession();
    var types = await probe.RunSqlQueryAsync<string>(ColumnTypesQuery, ("schema", _db.Schema));
    CollectionAssert.AreEqual(new[] { "vector(3)", "vector(4)" }, types.ToArray());

    var id = Guid.NewGuid();
    { var session = app.OpenSession();
      var chunk = session.NewEntity<IChunk>();
      chunk.Id = id;
      chunk.Name = "first";
      chunk.Embedding = [1f, 0.5f, -0.25f];
      // Several rows in one save, as a batch writes them.
      Add(session, "second", [0f, 1f, 0f], [0.1f, 0.2f, 0.3f, 0.4f]);
      Add(session, "third", [0f, 0f, 1f], null);
      await session.SaveChangesAsync(); }

    { var session = app.OpenSession();
      var chunk = (await session.GetEntityAsync<IChunk>(id))!;
      CollectionAssert.AreEqual(new[] { 1f, 0.5f, -0.25f }, chunk.Embedding);
      Assert.IsNull(chunk.Summary);

      // An assignment is the change, to a value and back to none.
      chunk.Embedding = [0.125f, 3e-20f, 1e20f];
      chunk.Summary = [1f, 2f, 3f, 4f];
      await session.SaveChangesAsync(); }

    { var session = app.OpenSession();
      var chunk = await session.EntitySet<IChunk>().Where(c => c.Name == "first").SingleAsync();
      CollectionAssert.AreEqual(new[] { 0.125f, 3e-20f, 1e20f }, chunk.Embedding);
      CollectionAssert.AreEqual(new[] { 1f, 2f, 3f, 4f }, chunk.Summary);
      chunk.Summary = null;
      await session.SaveChangesAsync(); }

    // A vector selected as a value, alone and beside others.
    { var session = app.OpenSession();
      Assert.IsNull((await session.GetEntityAsync<IChunk>(id))!.Summary);
      var rows = await session.EntitySet<IChunk>().OrderBy(c => c.Name)
        .Select(c => new { c.Name, c.Embedding, c.Summary }).ToListAsync();
      Assert.AreEqual("first second third", string.Join(" ", rows.Select(r => r.Name)));
      CollectionAssert.AreEqual(new[] { 0f, 1f, 0f }, rows[1].Embedding);
      CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, rows[1].Summary);
      Assert.IsNull(rows[2].Summary);
      var vectors = await session.EntitySet<IChunk>().Where(c => c.Name == "third").Select(c => c.Embedding)
        .ToListAsync();
      CollectionAssert.AreEqual(new[] { 0f, 0f, 1f }, vectors.Single()); }

    // A value of another length is the server's error.
    { var session = app.OpenSession();
      Add(session, "short", [1f, 2f], null);
      var refused = await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());
      StringAssert.Contains(refused.ToString(), WrongLengthMessage); }

    // The loader reads the columns back as the types the model has: a second connect finds nothing to change.
    var second = await BuildApp().ConnectToAsync(Settings);
    Assert.AreEqual(0, second.Changes!.Items.Count, second.Changes.Report());
  }

  [TestMethod]
  public async Task VectorDistance_OrdersFiltersAndProjects() {
    var app = BuildApp();
    await app.ConnectToAsync(Settings);
    float[] east = [1f, 0f, 0f], north = [0f, 1f, 0f], nearEast = [0.9f, 0.1f, 0f];
    { var session = app.OpenSession();
      foreach (var (name, vector) in new[] { ("east", east), ("north", north), ("near-east", nearEast) }) {
        var chunk = Add(session, name, vector, null);
        chunk.Position = vector;
        chunk.Weights = vector;
      }
      Add(session, "up", [0f, 0f, 1f], null);
      await session.SaveChangesAsync(); }
    var query = (EntitySession)app.OpenSession();
    IQueryable<IChunk> chunks = query.EntitySet<IChunk>();
    string LastSql() => query.LastCommands.Single().Sql;

    // ----- nearest first: the distance of the member's metric, the value bound as a vector ----------------------
    var nearest = await chunks.OrderBy(c => VectorFunctions.Distance(c.Embedding, east)).Take(2).Select(c => c.Name)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "east", "near-east" }, nearest.ToArray());
    StringAssert.Contains(LastSql(), GetMetricText(VectorMetric.Cosine));
    if (BoundVectorCast != null)
      StringAssert.Contains(LastSql(), BoundVectorCast);

    // ----- within a distance, with the vector first or second ----------------------------------------------------
    var within = await chunks.Where(c => VectorFunctions.Distance(c.Embedding, east) < 0.5).OrderBy(c => c.Name)
      .Select(c => c.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { "east", "near-east" }, within.ToArray());
    var flipped = await chunks.Where(c => VectorFunctions.Distance(east, c.Embedding) > 0.5).OrderBy(c => c.Name)
      .Select(c => c.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { "north", "up" }, flipped.ToArray());

    // ----- projected: the server's number is the one plain code computes ------------------------------------------
    var measured = (await chunks.Where(c => c.Name == "near-east")
      .Select(c => VectorFunctions.Distance(c.Embedding, east)).ToListAsync()).Single();
    Assert.AreEqual(VectorFunctions.Distance(nearEast, east), measured, 1e-6);
    Assert.IsTrue(measured is > 0.006 and < 0.0062, $"the cosine distance is {measured}");
    var page = await chunks.OrderBy(c => VectorFunctions.Distance(c.Embedding, north)).Take(3)
      .Select(c => new { c.Name, Distance = VectorFunctions.Distance(c.Embedding, north) }).ToListAsync();
    Assert.AreEqual("north", page[0].Name);
    Assert.AreEqual(0, page[0].Distance, 1e-6);
    Assert.AreEqual(3, page.Count);

    // ----- a vector written in the query, with no variable to hold it -------------------------------------------
    var written = await chunks.OrderBy(c => VectorFunctions.Distance(c.Embedding, new[] { 0f, 0f, 1f })).Take(1)
      .Select(c => c.Name).ToListAsync();
    Assert.AreEqual("up", written.Single());

    // ----- the other metrics, each by its member -----------------------------------------------------------------
    var straight = (await chunks.Where(c => c.Name == "north")
      .Select(c => VectorFunctions.Distance(c.Position!, east)).ToListAsync()).Single();
    Assert.AreEqual(Math.Sqrt(2), straight, 1e-6);
    StringAssert.Contains(LastSql(), GetMetricText(VectorMetric.Euclidean));
    var product = (await chunks.Where(c => c.Name == "near-east")
      .Select(c => VectorFunctions.Distance(c.Weights!, east)).ToListAsync()).Single();
    Assert.AreEqual(-0.9, product, 1e-6);
    StringAssert.Contains(LastSql(), GetMetricText(VectorMetric.DotProduct));

    // ----- two columns: the metric is the first member's, and nothing is bound -----------------------------------
    var between = (await chunks.Where(c => c.Name == "north")
      .Select(c => VectorFunctions.Distance(c.Position!, c.Embedding)).ToListAsync()).Single();
    Assert.AreEqual(0, between, 1e-6);
    StringAssert.Contains(LastSql(), GetMetricText(VectorMetric.Euclidean));
    if (BoundVectorCast != null)
      Assert.IsFalse(LastSql().Contains(BoundVectorCast), LastSql());

    // ----- a row with no vector has no distance, and sorts where the server puts a null --------------------------
    var ordered = await chunks.OrderBy(c => VectorFunctions.Distance(c.Position!, east)).Select(c => c.Name)
      .ToListAsync();
    Assert.AreEqual(SortsNoDistanceFirst ? "up east near-east north" : "east near-east north up",
      string.Join(" ", ordered));

    // ----- by name -----------------------------------------------------------------------------------------------
    var byName = await query.ExecuteQueryAsync(query.From("Chunk")
      .Where(row => row.Member("Embedding").VectorDistance(QueryMaker.Parameter("to", typeof(float[])))
        .Less(QueryMaker.Constant(0.001))), north);
    Assert.AreEqual("north", ((IChunk)byName.Single().EntityInstance).Name);
  }

  [TestMethod]
  public async Task OnDemandVector_IsMeasuredInAQueryAndLoadedOnARead() {
    var app = BuildApp();
    await app.ConnectToAsync(Settings);
    float[] east = [1f, 0f, 0f], north = [0f, 1f, 0f];
    { var writer = app.OpenSession();
      foreach (var (name, vector) in new[] { ("east", east), ("north", north) }) {
        var passage = writer.NewEntity<IPassage>();
        (passage.Id, passage.Name, passage.Embedding) = (Guid.NewGuid(), name, vector);
      }
      await writer.SaveChangesAsync(); }
    var embedding = app.Model!.FindEntity("Passage")!.GetMember("Embedding");

    // ----- the query measures by the member, and its rows come without it ---------------------------------------
    var session = (EntitySession)app.OpenSession();
    var nearest = await session.EntitySet<IPassage>().OrderBy(p => VectorFunctions.Distance(p.Embedding, north))
      .Take(2).ToListAsync();
    Assert.AreEqual("north east", string.Join(" ", nearest.Select(p => p.Name)));
    StringAssert.Contains(session.LastCommands.Single().Sql, GetMetricText(VectorMetric.Cosine));
    Assert.IsFalse(nearest.Any(Holds), "the vector is not in the rows");

    // ----- a read loads it, for the two in one statement ------------------------------------------------------------
    CollectionAssert.AreEqual(north, nearest[0].Embedding);
    var load = session.LastCommands.Single();
    CollectionAssert.AreEqual(east, nearest[1].Embedding);
    Assert.AreSame(load, session.LastCommands.Single(), "the other came with the first");

    // ----- an include brings it with the query ---------------------------------------------------------------------
    var other = (EntitySession)app.OpenSession();
    var included = (await other.EntitySet<IPassage>().Where(p => p.Name == "east").Include(p => p.Embedding)
      .ToListAsync()).Single();
    Assert.IsTrue(Holds(included));
    CollectionAssert.AreEqual(east, included.Embedding);

    // ----- local functions
    bool Holds(IPassage passage) => ((EntityBase)passage).Record.Loaded.IsSet(embedding.DataIndex);
  }

  protected static IChunk Add(IEntitySession session, string name, float[] embedding, float[]? summary) {
    var chunk = session.NewEntity<IChunk>();
    chunk.Id = Guid.NewGuid();
    chunk.Name = name;
    chunk.Embedding = embedding;
    chunk.Summary = summary;
    return chunk;
  }

  protected EntityApp BuildApp() {
    var app = new EntityApp("Vectors", new Version(1, 0), new EntityAppSettings { DefaultSchema = _db.Schema });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IChunk), typeof(IPassage));
    app.AddModule(module);
    app.Init();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }
}

/// <summary>PostgreSQL, where pgvector holds a vector: the operators, the cast of a bound array, and the HNSW index.
/// </summary>
[TestClass]
public sealed class PostgresVectors : VectorColumnTests {
  static readonly PostgresFixture PostgresServer = new();

  protected override ProviderFixture Fixture => PostgresServer;

  protected override string ColumnTypesQuery =>
    "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a "
    + "WHERE a.attrelid = (quote_ident(@schema) || '.\"Chunk\"')::regclass AND a.attname IN ('Embedding', 'Summary') "
    + "ORDER BY a.attname";

  protected override string WrongLengthMessage => "expected 3 dimensions, not 2";

  protected override string? BoundVectorCast => "::vector";

  protected override string GetMetricText(VectorMetric metric) => metric switch {
    VectorMetric.Euclidean => " <-> ",
    VectorMetric.DotProduct => " <#> ",
    _ => " <=> ",
  };

  [TestMethod]
  public async Task VectorIndex_ServesTheNearestRowsQuery() {
    var app = BuildApp();
    await app.ConnectToAsync(Settings);
    var session = (EntitySession)app.OpenSession();
    // Enough rows for the planner to prefer the index, written by the server.
    await session.RunSqlNonQueryAsync(
      $"INSERT INTO \"{_db.Schema}\".\"Chunk\" (\"Id\", \"Name\", \"Tenant\", \"Embedding\") "
      + "SELECT gen_random_uuid(), 'row ' || g, g % 5, ARRAY[random(), random(), random()]::real[]::vector "
      + "FROM generate_series(1, 5000) g");
    await session.RunSqlNonQueryAsync($"ANALYZE \"{_db.Schema}\".\"Chunk\"");
    Add(session, "east", [1f, 0f, 0f], null);
    await session.SaveChangesAsync();
    IQueryable<IChunk> chunks = session.EntitySet<IChunk>();
    float[] east = [1f, 0f, 0f];
    async Task<string> ExplainLastAsync(float[] vector) => string.Join("\n", await session.RunSqlQueryAsync<string>(
      "EXPLAIN (COSTS OFF) " + session.LastCommands.Single().Sql, ("p0", vector)));

    // ----- the nearest rows: the statement is the one the index serves ------------------------------------------
    var nearest = await chunks.OrderBy(c => VectorFunctions.Distance(c.Embedding, east)).Take(5).Select(c => c.Name)
      .ToListAsync();
    Assert.AreEqual("east", nearest[0]);
    Assert.AreEqual(5, nearest.Count);
    var plan = await ExplainLastAsync(east);
    StringAssert.Contains(plan, "Index Scan using", plan);
    StringAssert.Contains(plan, "Order By: (\"Embedding\" <=> ", plan);

    // ----- the entities themselves, nearest first, and a page with the distance beside the name ------------------
    var rows = await chunks.OrderBy(c => VectorFunctions.Distance(c.Embedding, east)).Take(3).ToListAsync();
    Assert.AreEqual("east", rows[0].Name);
    StringAssert.Contains(await ExplainLastAsync(east), "Index Scan using");

    // ----- farthest first is not what the index holds, and the statement is answered without it ------------------
    await chunks.OrderByDescending(c => VectorFunctions.Distance(c.Embedding, east)).Take(5).Select(c => c.Name)
      .ToListAsync();
    Assert.IsFalse((await ExplainLastAsync(east)).Contains("Index Scan using"));

    // ----- a filter beside the index: the index hands over its nearest candidates and the filter keeps some, so
    // the page comes short; with pgvector's iterative scan, set by the application for its transaction, it is whole.
    var ofOneTenant = chunks.Where(c => c.Tenant == 3).OrderBy(c => VectorFunctions.Distance(c.Embedding, east))
      .Take(50).Select(c => c.Name);
    await session.BeginTransactionAsync();
    await session.RunSqlNonQueryAsync("SET LOCAL enable_seqscan = off");
    await session.RunSqlNonQueryAsync("SET LOCAL enable_bitmapscan = off");
    var cut = await ofOneTenant.ToListAsync();
    Assert.IsTrue(cut.Count is > 0 and < 50, $"{cut.Count} rows of the 50 asked for");
    await session.RunSqlNonQueryAsync("SET LOCAL hnsw.iterative_scan = strict_order");
    var whole = await ofOneTenant.ToListAsync();
    Assert.AreEqual(50, whole.Count);
    await session.RollbackTransactionAsync();
  }
}

/// <summary>SQL Server 2025, which has a vector type and <c>VECTOR_DISTANCE</c>; the ORM creates no vector index
/// there, so the index the model asks for is left out with a warning.</summary>
[TestClass]
public sealed class SqlServerVectors : VectorColumnTests {
  static readonly SqlServerFixture SqlServer = new();

  protected override ProviderFixture Fixture => SqlServer;

  // The fixture's provider is made for SQL Server 2022, which has no vector type.
  protected override DbSettings Settings =>
    new(new SqlServerProvider(SqlServerVersion.Sql2025), SqlServerFixture.ConnectionString);

  protected override string ColumnTypesQuery =>
    "SELECT t.name + '(' + CAST(c.vector_dimensions AS varchar(10)) + ')' FROM sys.columns c "
    + "JOIN sys.types t ON t.user_type_id = c.user_type_id "
    + "WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.Chunk') AND c.name IN ('Embedding', 'Summary') "
    + "ORDER BY c.name";

  protected override string WrongLengthMessage => "The vector dimensions 2 and 3 do not match";

  protected override bool SortsNoDistanceFirst => true;

  protected override string GetMetricText(VectorMetric metric) => metric switch {
    VectorMetric.Euclidean => "VECTOR_DISTANCE('euclidean', ",
    VectorMetric.DotProduct => "VECTOR_DISTANCE('dot', ",
    _ => "VECTOR_DISTANCE('cosine', ",
  };

  [TestMethod]
  public async Task VectorIndex_IsLeftOutWithAWarning() {
    var app = BuildApp();
    var report = await app.ConnectToAsync(Settings);
    Assert.IsTrue(report.IsApplied, report.Report());
    var indexes = await app.OpenSession().RunSqlQueryAsync<string>(
      "SELECT i.name FROM sys.indexes i WHERE i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.Chunk') "
      + "AND i.is_primary_key = 0 AND i.type > 0", ("schema", _db.Schema));
    Assert.AreEqual(0, indexes.Count, string.Join(", ", indexes));
  }
}
