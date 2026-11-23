using Mic.DbTool;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// OBJECTS THE UPGRADE IS TOLD TO IGNORE against a real server: an index and a column made by hand on a table of the
// model stay under a pattern of DbSettings.IgnoreDbObjects, a change the model asks for is withheld under one, and
// without the patterns the upgrade does what it did before.
public abstract partial class DdlTests {
  static class Ignored {
    [Entity(Name = "Part")]
    public interface IPartV1 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string Name { get; set; }
      int Code { get; set; }
    }

    /// <summary>The name made longer.</summary>
    [Entity(Name = "Part")]
    public interface IPartV2 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(80)] string Name { get; set; }
      int Code { get; set; }
    }
  }

  [TestMethod]
  public async Task IgnoredObjects_AreNeitherDroppedNorChanged() {
    const string handIndex = "IX_Part_ByHand";
    const string handColumn = "Extra";
    var dialect = Db.Provider.SqlDialect;
    // The second pattern in another case: a pattern is compared ignoring case.
    string[] patterns = [$"*Part.{handIndex}", $"*part.{handColumn.ToLowerInvariant()}"];
    async Task<DbTableInfo> LoadPartAsync() => (await Db.LoadAsync()).Tables.Single(t => t.Name.EndsWith("Part"));
    static bool HasName(DbModelObject obj, string name) =>
      string.Equals(obj.Name, name, StringComparison.OrdinalIgnoreCase);

    // ----- 1. the table, then an index and a column the model does not declare -------------------------------------
    var created = await ConnectAsync(new Version(1, 0), [typeof(Ignored.IPartV1)]);
    Assert.IsFalse(created.HasErrors, created.Report());
    await Db.ExecuteAsync(string.Format(dialect.CreateIndex, Db.Quote(handIndex), Db.GetTableRef("Part"),
      Db.Quote("Code"), dialect.QualifiedName(Db.Schema, handIndex), "", ""));
    await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("Part")} ADD {Db.Quote(handColumn)} int");

    // ----- 2. under the patterns both stay, and neither is a difference: the report says nothing of them ----------
    var kept = await ConnectAsync(new Version(1, 0), [typeof(Ignored.IPartV1)],
      settings => settings.IgnoreDbObjects = [.. patterns]);
    Assert.IsFalse(kept.HasErrors, kept.Report());
    Assert.AreEqual(0, kept.Changes!.Items.Count, kept.Changes.Report());
    Assert.IsFalse(kept.Messages.Any(m => m.Message.Contains(handIndex) || m.Message.Contains(handColumn)),
      kept.Report());
    Assert.IsTrue(kept.Script == null || kept.Script.IsEmpty, kept.Report());
    var part = await LoadPartAsync();
    Assert.IsTrue(part.Keys.Any(k => HasName(k, handIndex)), "the index made by hand was dropped");
    Assert.IsTrue(part.Columns.Any(c => HasName(c, handColumn)), "the column made by hand was dropped");

    // ----- 3. a change the model asks for is withheld under a pattern, and applied without it ----------------------
    if (Db.Provider.Supports(DbFeatures.AlterColumn)) {
      var withheld = await ConnectAsync(new Version(1, 1), [typeof(Ignored.IPartV2)],
        settings => settings.IgnoreDbObjects = [.. patterns, "*Part.Name"]);
      Assert.IsTrue(withheld.Script == null || withheld.Script.IsEmpty, withheld.Report());
      Assert.AreEqual(0, withheld.Changes!.Items.Count, withheld.Changes.Report());

      var applied = await ConnectAsync(new Version(1, 1), [typeof(Ignored.IPartV2)],
        settings => settings.IgnoreDbObjects = [.. patterns]);
      Assert.IsFalse(applied.HasErrors, applied.Report());
      Assert.IsTrue(applied.Script is { IsEmpty: false }, "the longer name was still to be applied");
      var settled = await ConnectAsync(new Version(1, 1), [typeof(Ignored.IPartV2)],
        settings => settings.IgnoreDbObjects = [.. patterns]);
      Assert.IsTrue(settled.Script == null || settled.Script.IsEmpty, settled.Report());
    }

    // ----- 4. without the patterns the index and the column go ------------------------------------------------------
    if (Db.Provider.Supports(DbFeatures.DropColumn)) {
      var latest = Db.Provider.Supports(DbFeatures.AlterColumn)
        ? (new Version(1, 1), typeof(Ignored.IPartV2))
        : (new Version(1, 0), typeof(Ignored.IPartV1));
      var dropped = await ConnectAsync(latest.Item1, [latest.Item2]);
      Assert.IsFalse(dropped.HasErrors, dropped.Report());
      part = await LoadPartAsync();
      Assert.IsFalse(part.Keys.Any(k => HasName(k, handIndex)));
      Assert.IsFalse(part.Columns.Any(c => HasName(c, handColumn)));
    }
  }

  // AN IGNORED INDEX OVER A COLUMN THE UPGRADE RECREATES. To the upgrade an ignored object does not exist: the script
  // that drops a computed column and adds it again says nothing of the index made by hand over it. What the server
  // then does with that index, or with the drop of the column, is the server's.
  [TestMethod]
  public async Task IgnoredIndex_OverARecreatedColumn_IsNotInTheScript() {
    const string handIndex = "IX_Marked_ByHand";
    var dialect = Db.Provider.SqlDialect;
    await AppFor("ComputedTest", new Version(1, 0), typeof(Computed.IMarkedV2)).ConnectToAsync(Db.Settings);
    // Over two columns: Oracle takes no second index over the column list the model's own index has.
    await Db.ExecuteAsync(string.Format(dialect.CreateIndex, Db.Quote(handIndex), Db.GetTableRef("Marked"),
      $"{Db.Quote("Kind")}, {Db.Quote("Id")}", dialect.QualifiedName(Db.Schema, handIndex), "", ""));

    // The next version computes the column from another path, so the upgrade recreates it.
    var next = AppFor("ComputedTest", new Version(1, 1), typeof(Computed.IMarkedV3));
    var ignored = Db.Settings.IgnoreDbObjects;
    try {
      Db.Settings.IgnoreDbObjects = [$"*Marked.{handIndex}"];
      var plan = await DbToolHelper.PlanUpgradeAsync(next, Db.Settings);
      Assert.IsFalse(plan.HasErrors, plan.Report());
      var steps = plan.Script!.Steps.Select(s => s.Text).ToList();
      Assert.IsTrue(steps.Any(s => s.Contains("DROP COLUMN")), string.Join(Environment.NewLine, steps));
      Assert.IsTrue(steps.Any(s => s.Contains("IX_Marked_Kind")), "the model's own index goes and comes back");
      Assert.IsFalse(steps.Any(s => s.Contains(handIndex)), string.Join(Environment.NewLine, steps));
      Assert.IsFalse(plan.Messages.Any(m => m.Message.Contains(handIndex)), plan.Report());
    } finally {
      Db.Settings.IgnoreDbObjects = ignored;
    }

    // Without the pattern the index is a difference of its own, and the script drops it.
    var plain = await DbToolHelper.PlanUpgradeAsync(next, Db.Settings);
    Assert.IsTrue(plain.Script!.Steps.Any(s => s.Text.Contains(handIndex)), plain.Report());
  }
}
