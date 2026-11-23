using Mic.Common;
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

// A GEOGRAPHIC POINT in the server's own type: written by the single insert, the multi-row insert and the update,
// read back whole, as a projection, and raw through the server's own function.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task GeoPoint_IsStoredInTheServersTypeAndReadBack() {
    var seattle = new GeoPoint(47.6062, -122.3321);
    var oslo = new GeoPoint(59.9139, 10.7522);
    var sydney = new GeoPoint(-33.8688, 151.2093);

    // ----- one record: the single insert -----------------------------------------------------------------------
    await using var writer = NewSession();
    var first = writer.NewEntity<Dml.IPlace>();
    first.Id = Guid.NewGuid();
    first.Name = "Seattle";
    first.Location = seattle;
    await writer.SaveChangesAsync();

    // ----- raw, through the server's own reading of the column -----------------------------------------------
    var read = string.Format(Db.Settings.Provider.SqlDialect.GeoPointRead, Db.Quote("Location"));
    var wkb = await ProbeScalarAsync<byte[]>(
      $"SELECT {read} FROM {Db.GetTableRef("Place")} WHERE {Db.Quote("Id")} = @id", ("id", first.Id));
    AssertSamePoint(seattle, GeoPoint.FromWkb(wkb!));

    // ----- two records: the multi-row insert, one with the optional point ------------------------------------
    var second = writer.NewEntity<Dml.IPlace>();
    second.Id = Guid.NewGuid();
    second.Name = "Oslo";
    second.Location = oslo;
    second.Pin = sydney;
    var third = writer.NewEntity<Dml.IPlace>();
    third.Id = Guid.NewGuid();
    third.Name = "Sydney";
    third.Location = sydney;
    await writer.SaveChangesAsync();

    // ----- read back whole and as a projection ------------------------------------------------------------------
    await using var reader = NewSession();
    var loaded = await reader.GetEntityAsync<Dml.IPlace>(second.Id);
    AssertSamePoint(oslo, loaded.Location);
    AssertSamePoint(sydney, loaded.Pin!.Value);
    Assert.IsNull((await reader.GetEntityAsync<Dml.IPlace>(first.Id)).Pin);
    var projected = await reader.EntitySet<Dml.IPlace>().Where(p => p.Id == third.Id)
      .Select(p => new { p.Name, p.Location }).ToListAsync();
    AssertSamePoint(sydney, projected.Single().Location);

    // ----- the update ---------------------------------------------------------------------------------------------
    loaded.Location = seattle;
    loaded.Pin = null;
    await reader.SaveChangesAsync();
    await using var again = NewSession();
    var moved = await again.GetEntityAsync<Dml.IPlace>(second.Id);
    AssertSamePoint(seattle, moved.Location);
    Assert.IsNull(moved.Pin);
  }

  [TestMethod]
  public async Task GeoPoint_DistanceFiltersOrdersAndProjects() {
    var tag = "geo-" + Guid.NewGuid().ToString("N")[..6];
    var seattle = new GeoPoint(47.6062, -122.3321);
    var oslo = new GeoPoint(59.9139, 10.7522);
    var sydney = new GeoPoint(-33.8688, 151.2093);
    await using var session = NewSession();
    foreach (var (name, point) in new[] { ("Seattle", seattle), ("Oslo", oslo), ("Sydney", sydney) }) {
      var place = session.NewEntity<Dml.IPlace>();
      place.Id = Guid.NewGuid();
      place.Name = $"{tag} {name}";
      place.Location = point;
    }
    await session.SaveChangesAsync();
    var places = session.EntitySet<Dml.IPlace>().Where(p => p.Name.StartsWith(tag));

    // DB2 HOLDS A POINT AS BYTES and computes no distance: the query is refused by name at translation.
    if (Fixture.ServerKind == DbServerKind.Db2) {
      var refused = await Assert.ThrowsAsync<NotSupportedException>(
        () => places.Where(p => GeoFunctions.Distance(p.Location, oslo) < 1_000_000).ToListAsync());
      StringAssert.Contains(refused.Message, "GeoDistance");
      return;
    }

    // ----- within a radius: a thousand kilometres of Oslo is Oslo alone ------------------------------------------
    var near = await places.Where(p => GeoFunctions.Distance(p.Location, oslo) < 1_000_000).ToListAsync();
    Assert.AreEqual($"{tag} Oslo", near.Single().Name);

    // ----- nearest first ---------------------------------------------------------------------------------------------
    var ordered = await places.OrderBy(p => GeoFunctions.Distance(p.Location, oslo)).Select(p => p.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { $"{tag} Oslo", $"{tag} Seattle", $"{tag} Sydney" }, ordered.ToList());

    // ----- projected: the server's figure of the Earth beside the engine's sphere --------------------------------
    var measured = await places.Where(p => p.Name.EndsWith("Seattle"))
      .Select(p => GeoFunctions.Distance(p.Location, oslo)).ToListAsync();
    var expected = GeoFunctions.Distance(seattle, oslo);
    Assert.IsTrue(expected is > 7_000_000 and < 7_800_000, $"Seattle to Oslo is {expected} m");
    Assert.AreEqual(expected, measured.Single(), expected * 0.005, "the server's distance within half a percent");
  }

  /// <summary>Asserts two points are the same place: Oracle holds a coordinate as a NUMBER, so a double comes back
  /// within an ulp, a difference far under a micrometre.</summary>
  private static void AssertSamePoint(GeoPoint expected, GeoPoint actual) {
    Assert.AreEqual(expected.Latitude, actual.Latitude, 1e-9, $"latitude of {actual}");
    Assert.AreEqual(expected.Longitude, actual.Longitude, 1e-9, $"longitude of {actual}");
  }
}
