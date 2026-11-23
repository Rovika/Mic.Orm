using Mic.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A GEOGRAPHIC POINT COLUMN THROUGH THE UPGRADE LOOP: created in the server's type, with whatever the server needs
// first, and read back as itself so that the next run leaves it alone.
public abstract partial class DdlTests {
  public static class Geo {
    [Entity(Name = "GeoPlace")]
    public interface IPlace {
      [PrimaryKey] Guid Id { get; set; }
      [Index] GeoPoint Location { get; set; }
      GeoPoint? Pin { get; set; }
    }
  }

  /// <summary>One table in three shapes: its point indexed, its point not indexed, and not there at all.</summary>
  public static class GeoMeta {
    [Entity(Name = "GeoMetaPlace")]
    public interface IIndexed {
      [PrimaryKey] Guid Id { get; set; }
      [Index] GeoPoint Location { get; set; }
    }

    [Entity(Name = "GeoMetaPlace")]
    public interface IPlain {
      [PrimaryKey] Guid Id { get; set; }
      GeoPoint Location { get; set; }
    }

    [Entity(Name = "GeoMetaOther")]
    public interface IOther {
      [PrimaryKey] Guid Id { get; set; }
    }
  }

  [TestMethod]
  public async Task GeoPoint_IsCreatedInTheServersTypeAndReadBackAsItself() {
    var first = AppFor("GeoTest", new Version(1, 0), typeof(Geo.IPlace));
    await first.ConnectToAsync(Db.Settings);
    { var session = NewContext(first).OpenSession();
      var place = session.NewEntity<Geo.IPlace>();
      place.Id = Guid.NewGuid();
      place.Location = new GeoPoint(51.5074, -0.1278);
      await session.SaveChangesAsync();
    }

    // ----- read back: nothing to do ------------------------------------------------------------------------------
    var second = AppFor("GeoTest", new Version(1, 0), typeof(Geo.IPlace));
    await second.ConnectToAsync(Db.Settings);
    var again = second.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());
    var table = (await Db.LoadAsync()).FindTable(Db.Schema, "GeoPlace")!;
    var loaded = table.FindColumn("Location")!;
    // SQLite and Db2 hold a point as bytes, and the loader reports the bytes.
    if (Db.Settings.Provider.ServerKind is not (DbServerKind.SQLite or DbServerKind.Db2))
      Assert.IsTrue(loaded.IsGeoPoint, $"the loader reports the column as a point, not as '{loaded.TypeSpec}'");

    // ----- the index over it is the server's spatial index, read back as such ------------------------------------
    var provider = Db.Settings.Provider;
    var index = table.Keys.Single(k => k.Columns is [{ Column.Name: "Location" }]);
    var expected = provider.SpatialIndexType == DbIndexType.Default ? provider.DefaultIndexType : provider.SpatialIndexType;
    Assert.AreEqual(expected, index.IndexType, "the spatial index as the loader reports it");
  }

  [TestMethod]
  public async Task GeoPoint_ColumnWithoutItsSrid_IsAlteredToTheModels() {
    // MySQL's catalog calls every point column `point` and keeps the SRID beside it. A column somebody made with no
    // SRID takes MIC's values and no spatial index serves it, so it is read as the type it is and the upgrade alters
    // it to the model's.
    if (Db.Settings.Provider.ServerKind != DbServerKind.MySql)
      return;
    var first = AppFor("GeoSridTest", new Version(1, 0), typeof(Geo.IPlace));
    await first.ConnectToAsync(Db.Settings);
    var dialect = Db.Settings.Provider.SqlDialect;
    async Task<DbColumnInfo> LoadPinAsync() =>
      (await Db.LoadAsync()).FindTable(Db.Schema, "GeoPlace")!.FindColumn("Pin")!;
    Assert.AreEqual("point SRID 4326", (await LoadPinAsync()).TypeSpec, ignoreCase: true);

    await Db.ExecuteAsync(
      $"ALTER TABLE {dialect.QualifiedName(Db.Schema, "GeoPlace")} MODIFY COLUMN {dialect.Quote("Pin")} point NULL");
    var bare = await LoadPinAsync();
    Assert.AreEqual("point", bare.TypeSpec, ignoreCase: true);
    Assert.IsTrue(bare.IsGeoPoint, "still a point to whoever reads the database");

    var second = AppFor("GeoSridTest", new Version(1, 1), typeof(Geo.IPlace));
    await second.ConnectToAsync(Db.Settings);
    var changes = second.UpgradeFor(Db.Settings.DatabaseAlias)!.Changes!;
    Assert.AreEqual(1, changes.Items.Count, changes.Report());
    StringAssert.Contains(changes.Report(), "Pin");
    Assert.AreEqual("point SRID 4326", (await LoadPinAsync()).TypeSpec, ignoreCase: true);
  }

  [TestMethod]
  public async Task GeoPoint_OracleMetadataRow_GoesWithItsIndexAndItsTable() {
    // Oracle wants a column registered in its spatial metadata before a spatial index is built, and keeps the row
    // when the index or the table is dropped. The upgrade removes it with either.
    if (Db.Settings.Provider.ServerKind != DbServerKind.Oracle)
      return;
    async Task<int> CountRowsAsync() => Convert.ToInt32(await Db.ScalarAsync(
      "SELECT COUNT(*) FROM MDSYS.SDO_GEOM_METADATA_TABLE WHERE SDO_TABLE_NAME LIKE '%GeoMetaPlace%'"));

    await AppFor("GeoMetaTest", new Version(1, 0), typeof(GeoMeta.IIndexed)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, await CountRowsAsync(), "registered for the index");

    await AppFor("GeoMetaTest", new Version(1, 1), typeof(GeoMeta.IPlain)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(0, await CountRowsAsync(), "gone with the index");

    await AppFor("GeoMetaTest", new Version(1, 2), typeof(GeoMeta.IIndexed)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, await CountRowsAsync(), "registered again with the index");

    var settings = Db.Settings;
    var dropUnknown = settings.DropUnknownTables;
    settings.DropUnknownTables = true;
    try {
      await AppFor("GeoMetaTest", new Version(1, 3), typeof(GeoMeta.IOther)).ConnectToAsync(settings);
    } finally {
      settings.DropUnknownTables = dropUnknown;
    }
    Assert.IsFalse(await Db.TableExistsAsync("GeoMetaPlace"));
    Assert.AreEqual(0, await CountRowsAsync(), "gone with the table");
  }
}
