using System.Data.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// A DECLARED CHECK CONSTRAINT against a real server: created with its table, enforced, replaced under a new name as
// its condition changes, dropped with its declaration, and a constraint somebody else made left where it is.
public abstract partial class DdlTests {
  static class Checks {
    [Entity(Name = "Item")]
    [Check("Price", "{Price} >= 0")]
    public interface IItemV1 {
      [PrimaryKey] Guid Id { get; set; }
      int Price { get; set; }
      int Stock { get; set; }
    }

    /// <summary>The first condition changed, and a second check declared.</summary>
    [Entity(Name = "Item")]
    [Check("Price", "{Price} > 0")]
    [Check("Stock", "{Stock} >= 0")]
    public interface IItemV2 {
      [PrimaryKey] Guid Id { get; set; }
      int Price { get; set; }
      int Stock { get; set; }
    }

    /// <summary>No check declared.</summary>
    [Entity(Name = "Item")]
    public interface IItemV3 {
      [PrimaryKey] Guid Id { get; set; }
      int Price { get; set; }
      int Stock { get; set; }
    }

    /// <summary>A condition whose quoted text holds a braced word, and a doubled quote before another.</summary>
    [Entity(Name = "Label")]
    [Check("Code", "{Code} <> '{none}' AND {Code} <> 'it''s {Code}'")]
    public interface ILabel {
      [PrimaryKey] Guid Id { get; set; }
      [Size(20)] string Code { get; set; }
    }
  }

  [TestMethod]
  public async Task Check_IsCreatedReplacedAndDroppedByNameAndAnotherIsLeftAlone() {
    const string byHand = "Item_by_hand";
    var altersConstraints = Db.Provider.Supports(DbFeatures.AddConstraintsAfterCreate);
    Task<int> InsertAsync(int price) =>
      Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("Item")} "
        + $"({Db.Quote("Id")}, {Db.Quote("Price")}, {Db.Quote("Stock")}) "
        + $"VALUES ({Db.GuidLiteral(Guid.NewGuid())}, {price}, 1)");
    async Task<IReadOnlyList<DbCheckConstraintInfo>> GetChecksAsync() {
      var loaded = await Db.LoadAsync(DbModelParts.Tables | DbModelParts.CheckConstraints);
      return loaded.Tables.Single(t => t.Name.EndsWith("Item")).CheckConstraints;
    }
    async Task<List<string>> GetCheckNamesAsync() => (await GetChecksAsync()).Select(c => c.Name).ToList();

    // ----- 1. created with the table, under a name of the ORM's making, and enforced --------------------------------
    var created = await ConnectAsync(new Version(1, 0), [typeof(Checks.IItemV1)]);
    Assert.IsFalse(created.HasErrors, created.Report());
    var first = (await GetCheckNamesAsync()).Single();
    StringAssert.Contains(first, "_Price_");
    StringAssert.StartsWith(first, "CK_");
    // The condition is read back with the name, in the server's own spelling of it.
    StringAssert.Contains((await GetChecksAsync()).Single().Expression, "Price");
    await InsertAsync(0);
    await Assert.ThrowsAsync<DbException>(() => InsertAsync(-1));

    // ----- 2. the same model again is no change ------------------------------------------------------------------
    var again = await ConnectAsync(new Version(1, 0), [typeof(Checks.IItemV1)]);
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());

    // A constraint made by hand, where the server adds one to a table that exists. The row of price 0 goes, since
    // the next condition refuses it.
    if (altersConstraints)
      await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("Item")} ADD CONSTRAINT {Db.Quote(byHand)} "
        + $"CHECK ({Db.Quote("Stock")} < 1000000)");
    await Db.ExecuteAsync($"DELETE FROM {Db.GetTableRef("Item")}");

    // ----- 3. a changed condition is a new name; a new check is added; the hand-made one stays ---------------------
    var changed = await ConnectAsync(new Version(1, 1), [typeof(Checks.IItemV2)]);
    Assert.IsFalse(changed.HasErrors, changed.Report());
    var names = await GetCheckNamesAsync();
    if (altersConstraints) {
      Assert.IsFalse(names.Contains(first), "the old condition's constraint is gone");
      Assert.AreEqual(1, names.Count(n => n.Contains("_Price_")));
      Assert.AreEqual(1, names.Count(n => n.Contains("_Stock_")));
      Assert.IsTrue(names.Contains(byHand), "a constraint under another name is not the ORM's to drop");
      await Assert.ThrowsAsync<DbException>(() => InsertAsync(0));
      await InsertAsync(1);

      // And the upgrade is done: the next connect finds nothing.
      var settled = await ConnectAsync(new Version(1, 1), [typeof(Checks.IItemV2)]);
      Assert.AreEqual(0, settled.Changes!.Items.Count, settled.Changes.Report());
    } else {
      // The table keeps what it was created with, and the script says what it withheld.
      CollectionAssert.AreEqual(new[] { first }, names);
      await InsertAsync(0);
    }

    // ----- 4. a check no longer declared is dropped; the hand-made one still stays ---------------------------------
    var dropped = await ConnectAsync(new Version(1, 2), [typeof(Checks.IItemV3)]);
    Assert.IsFalse(dropped.HasErrors, dropped.Report());
    if (altersConstraints) {
      CollectionAssert.AreEqual(new[] { byHand }, await GetCheckNamesAsync());
      await InsertAsync(-1);
    }
  }

  // A braced word outside quoted text is a member; inside, it is text the server gets as written.
  [TestMethod]
  public async Task Check_WithBracesInQuotedText_IsCreatedAndEnforced() {
    Task<int> InsertAsync(string code) =>
      Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("Label")} ({Db.Quote("Id")}, {Db.Quote("Code")}) "
        + $"VALUES ({Db.GuidLiteral(Guid.NewGuid())}, '{code.Replace("'", "''")}')");

    var created = await ConnectAsync(new Version(1, 0), [typeof(Checks.ILabel)]);
    Assert.IsFalse(created.HasErrors, created.Report());
    var loaded = await Db.LoadAsync(DbModelParts.Tables | DbModelParts.CheckConstraints);
    var condition = loaded.Tables.Single(t => t.Name.EndsWith("Label")).CheckConstraints.Single().Expression;
    StringAssert.Contains(condition, "{none}");
    StringAssert.Contains(condition, "{Code}");

    await InsertAsync("plain");
    await InsertAsync("{other}");
    await Assert.ThrowsAsync<DbException>(() => InsertAsync("{none}"));
    await Assert.ThrowsAsync<DbException>(() => InsertAsync("it's {Code}"));

    // The same model again is no change: the name carries the hash of the condition as it was written out.
    var again = await ConnectAsync(new Version(1, 0), [typeof(Checks.ILabel)]);
    Assert.AreEqual(0, again.Changes!.Items.Count, again.Changes.Report());
  }

  // SQLite keeps a check in the table's CREATE text and nowhere else. The condition is the text between the bracket
  // after CHECK and the one that closes it, whatever brackets stand in quoted text or a quoted name on the way.
  [TestMethod]
  public async Task Check_ConditionIsReadOutOfTheCreateTextOnSqlite() {
    if (Db.Provider.ServerKind != DbServerKind.SQLite)
      return;
    await Db.ExecuteAsync("CREATE TABLE \"Part\" (\"Id\" int NOT NULL PRIMARY KEY, \"Qty (pcs)\" int NOT NULL, "
      + "\"Note\" text NOT NULL, [Size] int CHECK ([Size] > 0), "
      + "CONSTRAINT \"Part_Qty\" CHECK (\"Qty (pcs)\" >= 0 AND (\"Qty (pcs)\" < 1000 OR \"Note\" = 'bulk')), "
      + "CONSTRAINT Part_Note CHECK (\"Note\" NOT IN (')', 'it''s (', \"Id\") AND [Size] <> 1),\n"
      + "  constraint \"Part_Empty\"  check\n  ( length(\"Note\") > 0 ))");

    var loaded = await Db.LoadAsync(DbModelParts.Tables | DbModelParts.CheckConstraints);
    var found = loaded.Tables.Single(t => t.Name == "Part").CheckConstraints
      .ToDictionary(c => c.Name, c => c.Expression);
    Assert.AreEqual(3, found.Count, "the column's own check has no name and is not read");
    Assert.AreEqual("\"Qty (pcs)\" >= 0 AND (\"Qty (pcs)\" < 1000 OR \"Note\" = 'bulk')", found["Part_Qty"]);
    Assert.AreEqual("\"Note\" NOT IN (')', 'it''s (', \"Id\") AND [Size] <> 1", found["Part_Note"]);
    Assert.AreEqual("length(\"Note\") > 0", found["Part_Empty"]);
  }

  // Oracle keeps NOT NULL as a check constraint. One it named itself is left out by the statement; one somebody
  // named is known by its condition, and is no check of the table either.
  [TestMethod]
  public async Task Check_NamedNotNull_IsNoCheckOnOracle() {
    if (Db.Provider.ServerKind != DbServerKind.Oracle)
      return;
    await Db.EnsureSchemaAsync();
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("Part")} ({Db.Quote("Id")} NUMBER(10) NOT NULL, "
      + $"{Db.Quote("Qty")} NUMBER(10) CONSTRAINT {Db.Quote("Part_Qty_NN")} NOT NULL, {Db.Quote("Price")} NUMBER(10), "
      + $"CONSTRAINT {Db.Quote("Part_Price_CK")} CHECK ({Db.Quote("Price")} >= 0 AND {Db.Quote("Price")} < 1000))");

    var loaded = await Db.LoadAsync(DbModelParts.Tables | DbModelParts.CheckConstraints);
    var check = loaded.Tables.Single(t => t.Name == "Part").CheckConstraints.Single();
    Assert.AreEqual("Part_Price_CK", check.Name);
    Assert.AreEqual("\"Price\" >= 0 AND \"Price\" < 1000", check.Expression);
  }
}
