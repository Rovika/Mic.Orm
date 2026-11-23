using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A VIEW THROUGH THE UPGRADE LOOP: created from its query, kept while the query is the same, recreated when it
// changes, left alone when declared as existing — all decided by the hash MIC stores beside the view.
public abstract partial class DdlTests {
  public static class Views {
    public interface IPerson {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      int Rank { get; set; }
    }

    /// <summary>The view's first shape: the ranked people, by name.</summary>
    [Entity(Name = "RankedPerson")]
    public interface IRankedV1 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }

    /// <summary>The same view with a column more, over a different condition.</summary>
    [Entity(Name = "RankedPerson")]
    public interface IRankedV2 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      int Rank { get; set; }
    }

    /// <summary>IPerson with a wider name: the same table, a column's type changed.</summary>
    [Entity(Name = "Person")]
    public interface IPersonWide {
      [PrimaryKey] Guid Id { get; set; }
      [Size(80)] string LastName { get; set; }
      int Rank { get; set; }
    }

    /// <summary>A view over IPerson: the people above a rank.</summary>
    [Entity(Name = "HighRanked")]
    public interface IHighRanked {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      int Rank { get; set; }
    }

    /// <summary>A view over the view: the names alone.</summary>
    [Entity(Name = "HighRankedName")]
    public interface IHighRankedName {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }
  }

  [TestMethod]
  public async Task View_IsCreatedKeptAndRecreatedAsItsQueryChanges() {
    // ----- 1. created from its query, and read ------------------------------------------------------------------
    var (first, firstReport) = await ConnectWithViewAsync(typeof(Views.IRankedV1), module =>
      module.RegisterView<Views.IRankedV1>(
        from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 0 select new { p.Id, p.LastName }));
    Assert.IsTrue(firstReport.IsApplied, firstReport.Report());
    Assert.IsFalse(firstReport.HasErrors, firstReport.Report());

    { var session = NewContext(first).OpenSession();
      foreach (var (name, rank) in new[] { ("Unranked", 0), ("Low", 1), ("High", 5) }) {
        var person = session.NewEntity<Views.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = name;
        person.Rank = rank;
      }
      await session.SaveChangesAsync();
      var ranked = await session.EntitySet<Views.IRankedV1>().OrderBy(r => r.LastName).ToListAsync();
      CollectionAssert.AreEqual(new[] { "High", "Low" }, ranked.Select(r => r.LastName).ToList());
    }

    // ----- 2. the same query again: nothing to do, since the stored hash matches --------------------------------
    var (_, secondReport) = await ConnectWithViewAsync(typeof(Views.IRankedV1), module =>
      module.RegisterView<Views.IRankedV1>(
        from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 0 select new { p.Id, p.LastName }));
    Assert.AreEqual(0, secondReport.Changes!.Items.Count, secondReport.Changes.Report());
    Assert.IsFalse(secondReport.IsApplied);

    // ----- 3. a changed query: the view is dropped and created again ---------------------------------------------
    var (third, thirdReport) = await ConnectWithViewAsync(typeof(Views.IRankedV2), module =>
      module.RegisterView<Views.IRankedV2>(
        from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 1 select new { p.Id, p.LastName, p.Rank }));
    var change = thirdReport.Changes!.Items.Single(c => c.ObjectKind == DbObjectKind.View);
    Assert.AreEqual(DbDiffKind.Modify, change.DiffKind, change.ToString());
    Assert.IsTrue(thirdReport.IsApplied, thirdReport.Report());
    { var session = NewContext(third).OpenSession();
      var ranked = await session.EntitySet<Views.IRankedV2>().ToListAsync();
      Assert.AreEqual(1, ranked.Count);
      Assert.AreEqual(5, ranked[0].Rank);
    }

    // ----- 4. declared as existing: checked, read, never touched ------------------------------------------------
    var (fourth, fourthReport) = await ConnectWithViewAsync(typeof(Views.IRankedV2), module =>
      module.RegisterExistingView<Views.IRankedV2>());
    Assert.AreEqual(0, fourthReport.Changes!.Items.Count, fourthReport.Changes.Report());
    Assert.IsFalse(fourthReport.HasErrors, fourthReport.Report());
    {
      var session = NewContext(fourth).OpenSession();
      Assert.AreEqual(1, await session.EntitySet<Views.IRankedV2>().CountAsync());
    }

    // ----- 5. declared as existing and absent: reported, and the connect refused ------------------------------------
    // The model no longer declares RankedPerson, which MIC made, so this run also drops it.
    var absent = await Assert.ThrowsExactlyAsync<DataAccessException>(() => ConnectWithViewAsync(typeof(Views.IRankedV1),
      module => module.RegisterExistingView<Views.IRankedV1>(viewName: "NoSuchView")));
    StringAssert.Contains(absent.Message, "NoSuchView");

    // ----- 6. materialized where the server has it, and refreshed through the session -----------------------------
    var materialized = Db.Provider.Supports(DbFeatures.MaterializedViews);
    var (sixth, sixthReport) = await ConnectWithViewAsync(typeof(Views.IRankedV2), module =>
      module.RegisterView<Views.IRankedV2>(
        from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 1 select new { p.Id, p.LastName, p.Rank },
        ViewOptions.Materialized));
    Assert.IsTrue(sixthReport.IsApplied, sixthReport.Report());
    var loaded = (await Db.LoadAsync(DbModelParts.Tables | DbModelParts.Views)).Views.Single(v => v.Name == "RankedPerson");
    Assert.AreEqual(materialized, loaded.IsMaterialized, "materialized where the server has it, ordinary elsewhere");
    Assert.IsNotNull(loaded.StoredDefinitionHash, "the hash is read back off a materialized view too");
    Assert.IsNull(loaded.Source, "no source text was asked for");
    var withSource = (await Db.LoadAsync(DbModelParts.Tables | DbModelParts.Views | DbModelParts.SourceText))
      .Views.Single(v => v.Name == "RankedPerson");
    StringAssert.Contains(withSource.Source ?? "", "LastName", "and its select, when asked for");

    { var session = NewContext(sixth).OpenSession();
      var person = session.NewEntity<Views.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = "Later";
      person.Rank = 7;
      await session.SaveChangesAsync();
      // A materialized view holds what it held at creation until it is refreshed; an ordinary one is live, and the
      // refresh does nothing there.
      Assert.AreEqual(materialized ? 1 : 2, await session.EntitySet<Views.IRankedV2>().CountAsync());
      await session.RefreshMaterializedViewAsync<Views.IRankedV2>();
      Assert.AreEqual(2, await session.EntitySet<Views.IRankedV2>().CountAsync());
      await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.RefreshMaterializedViewAsync<Views.IPerson>(),
        "a table has nothing to refresh");
    }
  }

  /// <summary>Connects an application over the people and one view, declared on the module before the build.
  /// </summary>
  async Task<(EntityApp App, DbUpgradeReport Report)> ConnectWithViewAsync(Type view, Action<EntityModule> declare) {
    var app = BuildApp("ViewTest", new Version(1, 0), a => declare(a.Modules[0]), typeof(Views.IPerson), view);
    await app.ConnectToAsync(Db.Settings);
    return (app, app.UpgradeFor(Db.Settings.DatabaseAlias)!);
  }

  [TestMethod]
  public async Task ViewOverAView_IsCreatedAfterItAndRecreatedWithIt() {
    // The dependent is registered FIRST, so model order alone would create it before the view it reads.
    void Declare(EntityModule module, int minRank) {
      module.RegisterView<Views.IHighRankedName>(
        from h in ViewHelper.EntitySet<Views.IHighRanked>() select new { h.Id, h.LastName },
        dependsOn: [typeof(Views.IHighRanked)]);
      // Two literal queries: a view's query is fixed text, and a captured local would be a parameter.
      module.RegisterView<Views.IHighRanked>(minRank == 1
        ? from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 1 select new { p.Id, p.LastName, p.Rank }
        : from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 2 select new { p.Id, p.LastName, p.Rank });
    }
    async Task<(EntityApp App, DbUpgradeReport Report)> ConnectAsync(int minRank) {
      var app = BuildApp("ViewTest", new Version(1, 0), a => Declare(a.Modules[0], minRank),
        typeof(Views.IPerson), typeof(Views.IHighRanked), typeof(Views.IHighRankedName));
      await app.ConnectToAsync(Db.Settings);
      return (app, app.UpgradeFor(Db.Settings.DatabaseAlias)!);
    }

    // ----- 1. both created, the read one first ------------------------------------------------------------------
    var (first, firstReport) = await ConnectAsync(minRank: 1);
    Assert.IsTrue(firstReport.IsApplied, firstReport.Report());
    Assert.IsFalse(firstReport.HasErrors, firstReport.Report());
    { var session = NewContext(first).OpenSession();
      foreach (var (name, rank) in new[] { ("Low", 1), ("Mid", 2), ("High", 5) }) {
        var person = session.NewEntity<Views.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = name;
        person.Rank = rank;
      }
      await session.SaveChangesAsync();
      var names = await session.EntitySet<Views.IHighRankedName>().OrderBy(n => n.LastName).Select(n => n.LastName).ToListAsync();
      CollectionAssert.AreEqual(new[] { "High", "Mid" }, names.ToList());
    }

    // ----- 2. the read view changes: the dependent is recreated with it, dropped first ----------------------------
    var (second, secondReport) = await ConnectAsync(minRank: 2);
    var changes = secondReport.Changes!.Items.Where(c => c.ObjectKind == DbObjectKind.View).ToList();
    Assert.AreEqual(2, changes.Count, secondReport.Changes.Report());
    Assert.IsTrue(changes.All(c => c.DiffKind == DbDiffKind.Modify), secondReport.Changes.Report());
    Assert.IsTrue(secondReport.IsApplied, secondReport.Report());
    { var session = NewContext(second).OpenSession();
      var names = await session.EntitySet<Views.IHighRankedName>().Select(n => n.LastName).ToListAsync();
      CollectionAssert.AreEqual(new[] { "High" }, names.ToList());
    }

    // ----- 3. and the same model again finds nothing to do --------------------------------------------------------
    var (_, thirdReport) = await ConnectAsync(minRank: 2);
    Assert.AreEqual(0, thirdReport.Changes!.Items.Count, thirdReport.Changes.Report());
  }

  [TestMethod]
  public async Task ViewOverATable_IsRecreatedWhenItsColumnChangesType() {
    // PostgreSQL refuses ALTER COLUMN TYPE under a view; the view is dropped before and recreated after.
    var (first, firstReport) = await ConnectWithViewAsync(typeof(Views.IRankedV1), module =>
      module.RegisterView<Views.IRankedV1>(
        from p in ViewHelper.EntitySet<Views.IPerson>() where p.Rank > 0 select new { p.Id, p.LastName }));
    Assert.IsTrue(firstReport.IsApplied, firstReport.Report());
    { var session = NewContext(first).OpenSession();
      var person = session.NewEntity<Views.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = "Wide";
      person.Rank = 3;
      await session.SaveChangesAsync();
    }

    // The same view text over the same table; only the column is wider now.
    var app = BuildApp("ViewTest", new Version(1, 0), a => a.Modules[0].RegisterView<Views.IRankedV1>(
        from p in ViewHelper.EntitySet<Views.IPersonWide>() where p.Rank > 0 select new { p.Id, p.LastName }),
      typeof(Views.IPersonWide), typeof(Views.IRankedV1));
    await app.ConnectToAsync(Db.Settings);
    var report = app.UpgradeFor(Db.Settings.DatabaseAlias)!;
    var changes = report.Changes!;
    Assert.IsTrue(changes.Items.Any(c => c.ObjectKind == DbObjectKind.Column && c.DiffKind == DbDiffKind.Modify), changes.Report());
    var view = changes.Items.Single(c => c.ObjectKind == DbObjectKind.View);
    Assert.AreEqual(DbDiffKind.Modify, view.DiffKind, view.ToString());
    StringAssert.Contains(view.ToString(), "whose column changes");
    Assert.IsFalse(report.HasErrors, report.Report());
    { var session = NewContext(app).OpenSession();
      var ranked = await session.EntitySet<Views.IRankedV1>().ToListAsync();
      Assert.AreEqual(1, ranked.Count);
      Assert.AreEqual("Wide", ranked[0].LastName);
    }
  }
}
