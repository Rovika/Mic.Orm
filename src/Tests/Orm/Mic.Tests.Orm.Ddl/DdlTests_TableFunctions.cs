using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A TABLE FUNCTION THROUGH THE UPGRADE LOOP: created from its body, kept while the body is the same, recreated when
// it changes, left alone when declared as existing, dropped when the model no longer declares it — all decided by
// the hash MIC writes into the body.
public abstract partial class DdlTests {
  public static class Functions {
    public interface IPerson {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      int Rank { get; set; }
    }

    /// <summary>The function's rows: the people at or above a rank.</summary>
    public interface IRanked {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }
  }

  [TestMethod]
  public async Task TableFunction_IsCreatedKeptAndRecreatedAsItsBodyChanges() {
    if (Db.Settings.Provider.SqlDialect.TableFunctionSource == null) {
      // ----- a server without table-valued functions: the model builds with a warning, nothing is created ---------
      var (app, report) = await ConnectWithFunctionAsync(module => module.RegisterTableFunction<Functions.IRanked>(
        "RankedAbove", BuildBody(">="), ("min_rank", typeof(int))));
      Assert.AreEqual(0, report.Changes!.Items.Count(c => c.ObjectKind == DbObjectKind.TableFunction), report.Changes.Report());
      Assert.IsTrue(app.Diagnostics().Any(d => d.Message.Contains("no table-valued functions")), app.Report());
      return;
    }

    // ----- 1. created from its body, and read -----------------------------------------------------------------------
    var (first, firstReport) = await ConnectWithFunctionAsync(module => module.RegisterTableFunction<Functions.IRanked>(
      "RankedAbove", BuildBody(">="), ("min_rank", typeof(int))));
    Assert.IsTrue(firstReport.IsApplied, firstReport.Report());
    Assert.IsFalse(firstReport.HasErrors, firstReport.Report());
    { var session = NewContext(first).OpenSession();
      foreach (var (name, rank) in new[] { ("Unranked", 0), ("Low", 1), ("High", 5) }) {
        var person = session.NewEntity<Functions.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = name;
        person.Rank = rank;
      }
      await session.SaveChangesAsync();
      var ranked = await session.TableFunction<Functions.IRanked>(1).OrderBy(r => r.LastName).ToListAsync();
      CollectionAssert.AreEqual(new[] { "High", "Low" }, ranked.Select(r => r.LastName).ToList());
    }

    // ----- 2. the same body again: nothing to do, since the stored hash matches -----------------------------------
    var (_, secondReport) = await ConnectWithFunctionAsync(module => module.RegisterTableFunction<Functions.IRanked>(
      "RankedAbove", BuildBody(">="), ("min_rank", typeof(int))));
    Assert.AreEqual(0, secondReport.Changes!.Items.Count, secondReport.Changes.Report());

    // ----- 3. a changed body: the function is dropped and created again -----------------------------------------
    var (third, thirdReport) = await ConnectWithFunctionAsync(module => module.RegisterTableFunction<Functions.IRanked>(
      "RankedAbove", BuildBody(">"), ("min_rank", typeof(int))));
    var change = thirdReport.Changes!.Items.Single(c => c.ObjectKind == DbObjectKind.TableFunction);
    Assert.AreEqual(DbDiffKind.Modify, change.DiffKind, change.ToString());
    Assert.IsTrue(thirdReport.IsApplied, thirdReport.Report());
    {
      var session = NewContext(third).OpenSession();
      Assert.AreEqual("High", (await session.TableFunction<Functions.IRanked>(1).ToListAsync()).Single().LastName);
    }

    // ----- 4. declared as existing: checked, read, never touched ------------------------------------------------
    var (fourth, fourthReport) = await ConnectWithFunctionAsync(module =>
      module.RegisterExistingTableFunction<Functions.IRanked>("RankedAbove", ("min_rank", typeof(int))));
    Assert.AreEqual(0, fourthReport.Changes!.Items.Count, fourthReport.Changes.Report());
    Assert.IsFalse(fourthReport.HasErrors, fourthReport.Report());
    {
      var session = NewContext(fourth).OpenSession();
      Assert.AreEqual(2, await session.TableFunction<Functions.IRanked>(0).CountAsync());
    }

    // ----- 5. declared as existing and absent: reported, and the connect refused ----------------------------------
    // The model no longer declares RankedAbove, which MIC made, so this run also drops it.
    var absent = await Assert.ThrowsExactlyAsync<DataAccessException>(() => ConnectWithFunctionAsync(module =>
      module.RegisterExistingTableFunction<Functions.IRanked>("NoSuchFunction")));
    StringAssert.Contains(absent.Message, "NoSuchFunction");
    var routines = (await Db.LoadAsync(DbModelParts.Tables | DbModelParts.Routines)).Routines;
    Assert.IsFalse(routines.Any(r => r.Name == "RankedAbove"), "dropped once the model stopped declaring it");
  }

  /// <summary>The body per server: the people whose rank passes the comparison with the parameter.</summary>
  private Dictionary<DbServerKind, string> BuildBody(string comparison) {
    var dialect = Db.Settings.Provider.SqlDialect;
    var parameter = Db.Settings.Provider.ServerKind == DbServerKind.MsSql ? "@min_rank" : "min_rank";
    return new Dictionary<DbServerKind, string> {
      [Db.Settings.Provider.ServerKind] = $"SELECT {dialect.Quote("Id")}, {dialect.Quote("LastName")} "
        + $"FROM {dialect.QualifiedName(Db.Schema, "Person")} WHERE {dialect.Quote("Rank")} {comparison} {parameter}",
    };
  }

  /// <summary>Connects an application over the people and one function, declared on the module before the build.
  /// </summary>
  async Task<(EntityApp App, DbUpgradeReport Report)> ConnectWithFunctionAsync(Action<EntityModule> declare) {
    var app = BuildApp("FunctionTest", new Version(1, 0), a => declare(a.Modules[0]), typeof(Functions.IPerson), typeof(Functions.IRanked));
    await app.ConnectToAsync(Db.Settings);
    return (app, app.UpgradeFor(Db.Settings.DatabaseAlias)!);
  }
}
