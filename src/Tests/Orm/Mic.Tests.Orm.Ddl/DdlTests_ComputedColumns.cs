using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A COMPUTED COLUMN THROUGH THE UPGRADE LOOP: added to a table that exists, with its index, read back as computed so
// that the next run leaves it alone.
public abstract partial class DdlTests {
  public static class Computed {
    [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(Arrow), "arrow")]
    public abstract class Marker {
      public int X { get; set; }
    }

    public sealed class Arrow : Marker { }

    [Entity(Name = "Marked")]
    public interface IMarkedV1 {
      [PrimaryKey] Guid Id { get; set; }
      [Json] Marker Marker { get; set; }
    }

    [Entity(Name = "Marked")]
    public interface IMarkedV2 {
      [PrimaryKey] Guid Id { get; set; }
      [Json(Discriminator = "Kind")] Marker Marker { get; set; }
      [FromJson("Marker", "$.kind"), Size(20), Index] string Kind { get; }
    }

    /// <summary>The column computed from another path of the document.</summary>
    [Entity(Name = "Marked")]
    public interface IMarkedV3 {
      [PrimaryKey] Guid Id { get; set; }
      [Json] Marker Marker { get; set; }
      [FromJson("Marker", "$.X"), Size(20), Index] string Kind { get; }
    }
  }

  [TestMethod]
  public async Task ComputedColumn_IsAddedWithItsIndexAndReadBackAsComputed() {
    // ----- 1. the table without the column, with a row ----------------------------------------------------------
    var first = AppFor("ComputedTest", new Version(1, 0), typeof(Computed.IMarkedV1));
    await first.ConnectToAsync(Db.Settings);
    { var session = NewContext(first).OpenSession();
      var marked = session.NewEntity<Computed.IMarkedV1>();
      marked.Id = Guid.NewGuid();
      marked.Marker = new Computed.Arrow { X = 1 };
      await session.SaveChangesAsync();
    }

    // ----- 2. the column and its index are added over the row that is there --------------------------------------
    var second = AppFor("ComputedTest", new Version(1, 1), typeof(Computed.IMarkedV2));
    await second.ConnectToAsync(Db.Settings);
    var report = second.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.IsTrue(report.IsApplied, report.Report());
    Assert.IsTrue(report.Changes!.Items.Any(c => c.ObjectKind == DbObjectKind.Column && c.DiffKind == DbDiffKind.Add), report.Changes.Report());
    Assert.IsTrue(report.Changes.Items.Any(c => c.ObjectKind == DbObjectKind.Key && c.DiffKind == DbDiffKind.Add), report.Changes.Report());
    { var session = NewContext(second).OpenSession();
      var marked = await session.EntitySet<Computed.IMarkedV2>().Where(m => m.Kind == "arrow").ToListAsync();
      Assert.AreEqual(1, marked.Count);
    }

    // ----- 3. read back as computed: nothing to do --------------------------------------------------------------
    var third = AppFor("ComputedTest", new Version(1, 1), typeof(Computed.IMarkedV2));
    await third.ConnectToAsync(Db.Settings);
    var again = third.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());
    var loaded = (await Db.LoadAsync()).FindTable(Db.Schema, "Marked")!.FindColumn("Kind")!;
    Assert.IsTrue(loaded.IsComputed, "the loader reports the column as the server's");
    Assert.IsNotNull(loaded.StoredDefinitionHash, "with the hash of its expression read back from beside it");

    // ----- 4. a changed expression: the column is dropped and added again, with its index -----------------------
    var fourth = AppFor("ComputedTest", new Version(1, 2), typeof(Computed.IMarkedV3));
    await fourth.ConnectToAsync(Db.Settings);
    var changed = fourth.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.IsTrue(changed.IsApplied, changed.Report());
    var modified = changed.Changes!.Items.Single(c => c.ObjectKind == DbObjectKind.Column);
    Assert.AreEqual(DbDiffKind.Modify, modified.DiffKind, changed.Changes.Report());
    StringAssert.Contains(modified.Description!, "expression changed");
    { var session = NewContext(fourth).OpenSession();
      Assert.AreEqual(1, (await session.EntitySet<Computed.IMarkedV3>().Where(m => m.Kind == "1").ToListAsync()).Count,
        "the row's value is the new expression's");
    }

    // ----- 5. and that is the end of it -----------------------------------------------------------------------------
    var fifth = AppFor("ComputedTest", new Version(1, 2), typeof(Computed.IMarkedV3));
    await fifth.ConnectToAsync(Db.Settings);
    var settled = fifth.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.AreEqual(0, settled.Changes!.Items.Count, settled.Changes.Report());

    // ----- 6. a description written by hand: the hash is still read, and nothing is recreated ---------------------
    //
    // SQL Server keeps the hash in a property of the ORM's own, so a description has MS_Description to itself, on
    // the column and on its index. PostgreSQL and Oracle have one comment for a column: the description goes into
    // it beside the hash.
    var described = (await Db.LoadAsync()).FindTable(Db.Schema, "Marked")!;
    var hash = described.FindColumn("Kind")!.StoredDefinitionHash!;
    var index = described.Keys.Single(k => k.Columns is [{ Column.Name: "Kind" }]).Name;
    string[] descriptions = Db.Provider.ServerKind switch {
      DbServerKind.MsSql => [DescribeOnSqlServer("COLUMN", "Kind"), DescribeOnSqlServer("INDEX", index)],
      DbServerKind.Postgres or DbServerKind.Oracle =>
        [$"COMMENT ON COLUMN {Db.GetTableRef("Marked")}.{Db.Quote("Kind")} IS 'the kind of the marker {hash}'"],
      _ => [],
    };
    foreach (var description in descriptions)
      await Db.ExecuteAsync(description);
    var sixth = AppFor("ComputedTest", new Version(1, 2), typeof(Computed.IMarkedV3));
    await sixth.ConnectToAsync(Db.Settings);
    var kept = sixth.UpgradeFor(Db.Settings.DatabaseAlias)!;
    Assert.AreEqual(0, kept.Changes!.Items.Count, kept.Changes.Report());

    // ----- local functions
    string DescribeOnSqlServer(string level, string name) =>
      "EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'described by hand', "
      + $"@level0type = N'SCHEMA', @level0name = N'{Db.Schema}', @level1type = N'TABLE', @level1name = N'Marked', "
      + $"@level2type = N'{level}', @level2name = N'{name}'";
  }
}
