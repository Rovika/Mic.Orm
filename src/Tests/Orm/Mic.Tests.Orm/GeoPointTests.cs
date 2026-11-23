using Mic.Common;
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Sqlite;

namespace Mic.Tests.Orm;

// A GEOGRAPHIC POINT, without a server: its binary form both ways, and what a member of it maps to.
[TestClass]
public class GeoPointTests {
  public interface IPlace {
    [PrimaryKey] Guid Id { get; set; }
    [Index] GeoPoint Location { get; set; }
    GeoPoint? Pin { get; set; }
  }

  [TestMethod]
  public void GeoPoint_RoundTripsThroughWellKnownBinary() {
    var point = new GeoPoint(47.6062, -122.3321);
    var wkb = point.ToWkb();
    Assert.AreEqual(21, wkb.Length);
    Assert.AreEqual(1, wkb[0], "little-endian");
    Assert.AreEqual(point, GeoPoint.FromWkb(wkb));

    // ----- big-endian, as Oracle writes it -------------------------------------------------------------------------
    var bigEndian = new byte[21];
    bigEndian[0] = 0;
    bigEndian[4] = 1;
    BitConverter.GetBytes(point.Longitude).Reverse().ToArray().CopyTo(bigEndian, 5);
    BitConverter.GetBytes(point.Latitude).Reverse().ToArray().CopyTo(bigEndian, 13);
    Assert.AreEqual(point, GeoPoint.FromWkb(bigEndian));

    // ----- with the SRID PostGIS writes after the type ----------------------------------------------------------
    var extended = new byte[25];
    extended[0] = 1;
    BitConverter.GetBytes(0x20000001u).CopyTo(extended, 1);
    BitConverter.GetBytes(GeoPoint.Srid).CopyTo(extended, 5);
    wkb.AsSpan(5).CopyTo(extended.AsSpan(9));
    Assert.AreEqual(point, GeoPoint.FromWkb(extended));

    Assert.ThrowsExactly<FormatException>(() => GeoPoint.FromWkb(new byte[5]));
    Assert.AreEqual("47.6062, -122.3321", point.ToString());
  }

  [TestMethod]
  public void GeoDistance_IsTheGreatCircleDistanceInMetres() {
    var seattle = new GeoPoint(47.6062, -122.3321);
    var oslo = new GeoPoint(59.9139, 10.7522);
    Assert.AreEqual(0, GeoFunctions.Distance(seattle, seattle));
    // The great-circle figure on the mean-radius sphere, computed independently: 7,328,961 m.
    Assert.AreEqual(7_329_000, GeoFunctions.Distance(seattle, oslo), 1_000);
    Assert.AreEqual(GeoFunctions.Distance(seattle, oslo), GeoFunctions.Distance(oslo, seattle), 1e-6);
  }

  [TestMethod]
  public void GeoPoint_MapsToTheServersPointType() {
    var app = NewApp();
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IPlace))!;
    Assert.IsTrue(entity.GetMember("Location").Kind == Mic.Orm.Model.MemberKind.Column);

    // ----- PostGIS geography, with the extension created first -------------------------------------------------
    var postgres = app.MapToPostgres();
    var table = postgres.GetTable(entity)!;
    Assert.AreEqual("geography", table.FindColumn("Location")!.TypeSpec);
    Assert.IsTrue(table.FindColumn("Location")!.IsGeoPoint);
    Assert.IsTrue(table.FindColumn("Pin")!.IsNullable);
    var ddl = new DbDdlBuilder(postgres.Provider).CreateModel(postgres);
    Assert.AreEqual(DdlKind.Prerequisite, ddl[0].Kind);
    Assert.AreEqual("CREATE EXTENSION IF NOT EXISTS postgis", ddl[0].Text);
    var create = ddl.Single(s => s.Kind == DdlKind.CreateTable && s.ObjectName == "Place").Text;
    StringAssert.Contains(create, "\"Location\" geography NOT NULL");
    StringAssert.Contains(create, "\"Pin\" geography");
    // ----- the index over the point is GiST, however it was asked for ---------------------------------------------
    var index = table.Keys.Single(k => k.Columns is [{ Column.Name: "Location" }]);
    Assert.AreEqual(DbIndexType.Gist, index.IndexType);
    StringAssert.Contains(ddl.Single(s => s.Kind == DdlKind.CreateIndex && s.ObjectName == index.Name).Text, " USING gist (");

    // ----- bytes where the server has no point type ---------------------------------------------------------------
    var sqlite = NewApp();
    AppInitHelper.CreateDatabase(sqlite, new DbSettings(new SqliteProvider(), "Data Source=:memory:"));
    var blob = sqlite.MappedModel().GetTable(sqlite.Model!.FindEntity(typeof(IPlace))!)!.FindColumn("Location")!;
    Assert.AreEqual("blob", blob.TypeSpec);
    Assert.IsTrue(blob.IsGeoPoint, "known by the member, not the column type");
    Assert.IsNull(new DbDdlBuilder(new SqliteProvider()).GeoPointPrerequisite());
  }

  static EntityApp NewApp() {
    var app = new EntityApp("Geo", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPlace));
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }
}
