using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;

namespace Mic.Tests.Orm.Db;

// A VECTOR COLUMN THROUGH THE UPGRADE LOOP, on the server that has one: added to a table that has rows, with the
// extension asked for first, and altered when the member's dimensions change.
public abstract partial class DdlTests {
  /// <summary>One table in three shapes: with no vector, with a vector of three dimensions, and of four.</summary>
  public static class Vec {
    [Entity(Name = "VecDoc")]
    public interface IPlain {
      [PrimaryKey] Guid Id { get; set; }
    }

    [Entity(Name = "VecDoc")]
    public interface IOfThree {
      [PrimaryKey] Guid Id { get; set; }
      [Vector(3)] float[]? Embedding { get; set; }
    }

    [Entity(Name = "VecDoc")]
    public interface IOfFour {
      [PrimaryKey] Guid Id { get; set; }
      [Vector(4)] float[]? Embedding { get; set; }
    }
  }

  /// <summary>One table in three shapes: its vector indexed by the cosine metric, by the Euclidean, and not
  /// indexed.</summary>
  public static class VecIx {
    [Entity(Name = "VecIxDoc")]
    public interface ICosine {
      [PrimaryKey] Guid Id { get; set; }
      [Vector(3), Index] float[]? Embedding { get; set; }
    }

    [Entity(Name = "VecIxDoc")]
    public interface IEuclidean {
      [PrimaryKey] Guid Id { get; set; }
      [Vector(3, Metric = VectorMetric.Euclidean), Index] float[]? Embedding { get; set; }
    }

    [Entity(Name = "VecIxDoc")]
    public interface IBare {
      [PrimaryKey] Guid Id { get; set; }
      [Vector(3)] float[]? Embedding { get; set; }
    }
  }

  [TestMethod]
  public async Task Vector_IndexIsOfTheMembersMetricAndRebuiltWhenTheMetricChanges() {
    if (Db.Settings.Provider.ServerKind != DbServerKind.Postgres)
      return;
    async Task<DbKeyInfo?> LoadIndexAsync() => (await Db.LoadAsync()).FindTable(Db.Schema, "VecIxDoc")!.Keys
      .SingleOrDefault(k => k.Columns is [{ Column.Name: "Embedding" }]);
    async Task<string?> ReadDefinitionAsync() => (string?)await Db.ScalarAsync(
      $"SELECT indexdef FROM pg_indexes WHERE schemaname = '{Db.Schema}' AND tablename = 'VecIxDoc' "
      + "AND indexdef LIKE '%Embedding%'");

    // ----- the index is HNSW with the operator class of the member's metric, and reads back as such --------------
    await AppFor("VecIxTest", new Version(1, 0), typeof(VecIx.ICosine)).ConnectToAsync(Db.Settings);
    var index = (await LoadIndexAsync())!;
    Assert.AreEqual(DbIndexType.Hnsw, index.IndexType);
    Assert.AreEqual(VectorMetric.Cosine, index.VectorMetric);
    StringAssert.Contains(await ReadDefinitionAsync(), "USING hnsw (\"Embedding\" vector_cosine_ops)");
    var again = await AppFor("VecIxTest", new Version(1, 0), typeof(VecIx.ICosine)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());

    // ----- another metric is another index: it is rebuilt -----------------------------------------------------------
    var rebuilt = await AppFor("VecIxTest", new Version(1, 1), typeof(VecIx.IEuclidean)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, rebuilt.Changes!.Items.Count, rebuilt.Changes.Report());
    StringAssert.Contains(rebuilt.Changes.Report(), "metric Cosine → Euclidean");
    Assert.AreEqual(VectorMetric.Euclidean, (await LoadIndexAsync())!.VectorMetric);
    StringAssert.Contains(await ReadDefinitionAsync(), "USING hnsw (\"Embedding\" vector_l2_ops)");

    // ----- and with no [Index] it goes -------------------------------------------------------------------------------
    var dropped = await AppFor("VecIxTest", new Version(1, 2), typeof(VecIx.IBare)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, dropped.Changes!.Items.Count, dropped.Changes.Report());
    Assert.IsNull(await LoadIndexAsync());
    Assert.IsNull(await ReadDefinitionAsync());
  }

  [TestMethod]
  public async Task Vector_ColumnIsAddedToATableWithRowsAndAlteredToNewDimensions() {
    if (Db.Settings.Provider.ServerKind != DbServerKind.Postgres)
      return;
    var id = Guid.NewGuid();
    var first = AppFor("VecTest", new Version(1, 0), typeof(Vec.IPlain));
    await first.ConnectToAsync(Db.Settings);
    { var session = NewContext(first).OpenSession();
      session.NewEntity<Vec.IPlain>().Id = id;
      await session.SaveChangesAsync(); }

    // ----- the column is added, after the statement that makes the server able to hold it ------------------------
    var second = AppFor("VecTest", new Version(1, 1), typeof(Vec.IOfThree));
    var added = await second.ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, added.Changes!.Items.Count, added.Changes.Report());
    var steps = added.Script!.Steps.Select(s => s.Text).ToList();
    Assert.AreEqual("CREATE EXTENSION IF NOT EXISTS vector", steps[0]);
    async Task<string> LoadSpecAsync() =>
      (await Db.LoadAsync()).FindTable(Db.Schema, "VecDoc")!.FindColumn("Embedding")!.TypeSpec;
    Assert.AreEqual("vector(3)", await LoadSpecAsync());
    { var session = NewContext(second).OpenSession();
      var doc = (await session.GetEntityAsync<Vec.IOfThree>(id))!;
      Assert.IsNull(doc.Embedding, "the row that was there has no vector");
      doc.Embedding = [1f, 2f, 3f];
      await session.SaveChangesAsync();
      doc.Embedding = null;
      await session.SaveChangesAsync(); }

    // ----- other dimensions are another type: the column is altered, which the rows with no vector allow ---------
    var third = AppFor("VecTest", new Version(1, 2), typeof(Vec.IOfFour));
    var altered = await third.ConnectToAsync(Db.Settings);
    Assert.AreEqual(1, altered.Changes!.Items.Count, altered.Changes.Report());
    Assert.AreEqual("vector(4)", await LoadSpecAsync());

    // ----- and read back, nothing to do ------------------------------------------------------------------------------
    var again = await AppFor("VecTest", new Version(1, 2), typeof(Vec.IOfFour)).ConnectToAsync(Db.Settings);
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());
  }
}
