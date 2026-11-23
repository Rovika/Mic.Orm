using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Db.SqlServer;

namespace Mic.Tests.Orm;

// A VECTOR MEMBER, without a server: what [Vector] makes of a float[], what the member maps to, and what it refuses.
[TestClass]
public class VectorMemberTests {
  public interface IChunk {
    [PrimaryKey] Guid Id { get; set; }
    [Vector(3)] float[] Embedding { get; set; }
    [Vector(1536, Metric = VectorMetric.Euclidean), Index] float[]? Summary { get; set; }
    float[] Samples { get; set; }
  }

  public interface IBadChunk {
    [PrimaryKey] Guid Id { get; set; }
    [Vector(3)] double[] Doubles { get; set; }
    [Vector(3)] List<float> Listed { get; set; }
    [Vector(0)] float[] Empty { get; set; }
    [Vector(3), Json] float[] Document { get; set; }
    [Vector(3), Size(5)] float[] Sized { get; set; }
  }

  [TestMethod]
  public void VectorMember_MapsToTheServersVectorType() {
    var app = NewApp(typeof(IChunk));
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IChunk))!;
    var embedding = entity.GetMember("Embedding");
    Assert.IsTrue(embedding.IsVector);
    Assert.AreEqual(3, embedding.Size);
    Assert.IsNull(embedding.ArrayItemType, "a vector is not an array column");
    Assert.AreEqual(VectorMetric.Cosine, embedding.VectorMetric);
    Assert.AreEqual(VectorMetric.Euclidean, entity.GetMember("Summary").VectorMetric);
    // Without the attribute a float[] stays an array member.
    Assert.IsFalse(entity.GetMember("Samples").IsVector);
    Assert.AreEqual(typeof(float), entity.GetMember("Samples").ArrayItemType);

    // ----- pgvector's type, with the extension created first -----------------------------------------------------
    var postgres = app.MapToPostgres();
    var table = postgres.GetTable(entity)!;
    Assert.AreEqual("vector(3)", table.FindColumn("Embedding")!.TypeSpec);
    Assert.AreEqual("vector(1536)", table.FindColumn("Summary")!.TypeSpec);
    Assert.AreEqual("real[]", table.FindColumn("Samples")!.TypeSpec);
    Assert.IsTrue(table.FindColumn("Embedding")!.IsVector);
    Assert.IsFalse(table.FindColumn("Samples")!.IsVector);
    var ddl = new DbDdlBuilder(postgres.Provider).CreateModel(postgres);
    Assert.AreEqual(DdlKind.Prerequisite, ddl[0].Kind);
    Assert.AreEqual("CREATE EXTENSION IF NOT EXISTS vector", ddl[0].Text);
    var create = ddl.Single(s => s.Kind == DdlKind.CreateTable && s.ObjectName == "Chunk").Text;
    StringAssert.Contains(create, "\"Embedding\" vector(3) NOT NULL");
    StringAssert.Contains(create, "\"Summary\" vector(1536)");

    // ----- the index over a vector is HNSW, with the operator class of the member's metric ------------------------
    var index = table.Keys.Single(k => k.Columns is [{ Column.Name: "Summary" }]);
    Assert.AreEqual(DbIndexType.Hnsw, index.IndexType);
    Assert.AreEqual(VectorMetric.Euclidean, index.VectorMetric);
    StringAssert.Contains(ddl.Single(s => s.Kind == DdlKind.CreateIndex && s.ObjectName == index.Name).Text,
      " USING hnsw (\"Summary\" vector_l2_ops)");
    Assert.IsTrue(table.Keys.Where(k => k != index).All(k => k.VectorMetric == null));

    // The catalog's name for the type reads back as the same one, with the dimensions as its size.
    var registry = postgres.Provider.TypeRegistry;
    Assert.AreEqual("vector(3)", registry.Apply(registry.FindByName("vector")!, size: 3).TypeSpec);

    // ----- an error of the model where the server has no vector type ------------------------------------------------
    DbProvider[] others = [new SqliteProvider(), new SqlServerProvider()];
    foreach (var provider in others) {
      Assert.IsNull(provider.TypeRegistry.MapMember(embedding, out var error), provider.ServerKind.ToString());
      StringAssert.Contains(error, "no vector type");
      Assert.IsNull(new DbDdlBuilder(provider).VectorPrerequisite());
    }
  }

  [TestMethod]
  public void VectorMember_OnSqlServerWantsAProviderMadeFor2025() {
    var entity = NewApp(typeof(IChunk)).Model!.FindEntity(typeof(IChunk))!;
    var registry = new SqlServerProvider(SqlServerVersion.Sql2025).TypeRegistry;
    Assert.AreEqual("vector(3)", registry.MapMember(entity.GetMember("Embedding"), out var error)!.TypeSpec);
    Assert.IsNull(error);
    // A float[] with no mark is not taken for a vector: it is the array member the server has no type for.
    Assert.IsNull(registry.MapMember(entity.GetMember("Samples"), out error));
    StringAssert.Contains(error, "array type");
    // The provider made for 2022, the default, has no vector type, and the error says a later version may.
    Assert.IsNull(new SqlServerProvider().TypeRegistry.MapMember(entity.GetMember("Embedding"), out error));
    StringAssert.Contains(error, "a later version of the server");
  }

  [TestMethod]
  public void VectorDistance_InPlainCodeIsTheCosineDistance() {
    float[] east = [1f, 0f, 0f];
    Assert.AreEqual(0, VectorFunctions.Distance(east, [5f, 0f, 0f]), 1e-12, "the same direction, whatever the length");
    Assert.AreEqual(1, VectorFunctions.Distance(east, [0f, 1f, 0f]), 1e-12, "a right angle");
    Assert.AreEqual(2, VectorFunctions.Distance(east, [-1f, 0f, 0f]), 1e-12, "the opposite direction");
    Assert.AreEqual(1 - 0.9 / Math.Sqrt(0.82), VectorFunctions.Distance(east, [0.9f, 0.1f, 0f]), 1e-7);
    Assert.ThrowsExactly<ArgumentException>(() => VectorFunctions.Distance(east, [1f, 0f]));
  }

  [TestMethod]
  public void VectorMember_RefusesWhatItCannotBe() {
    var refused = NewApp(typeof(IBadChunk));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "member of type 'Double[]'; a vector is a float[]");
    StringAssert.Contains(report, "member of type 'List`1'; a vector is a float[]");
    StringAssert.Contains(report, "[Vector(0)] states no dimensions");
    StringAssert.Contains(report, "a vector or a JSON document, not both");
    StringAssert.Contains(report, "BadChunk.Sized");
  }

  static EntityApp NewApp(Type entity) {
    var app = new EntityApp("Vectors", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entity);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }
}
