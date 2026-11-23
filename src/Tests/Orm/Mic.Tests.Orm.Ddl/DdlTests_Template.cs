using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

// THE PROJECT TEMPLATE: an application made from it for this vendor builds against the packed ORM; on PostgreSQL
// and SQLite, the servers every run has, its test passes, its host runs, and the database tool, run as a process
// of its own with the configuration the template ships, plans and applies the upgrade of an empty database.
public abstract partial class DdlTests {
  [TestMethod]
  public async Task Template_MakesAnApplicationThatBuildsAndRuns() {
    await TemplateHelper.EnsurePackedAsync();
    var kind = Fixture.ServerKind;
    var folder = TemplateHelper.RecreateFolder(Path.Combine(TemplateHelper.WorkRoot, "apps", kind.ToString()));

    // ----- 1. made and built, against the packages ----------------------------------------------------------------
    // No version is passed: the application references the version the template was packed with.
    await TemplateHelper.RunAppCommandAsync(
      $"new mic-app -n Shop --provider {kind} -o \"{folder}\" --debug:custom-hive \"{TemplateHelper.Hive}\"",
      folder);
    Assert.IsTrue(File.Exists(Path.Combine(folder, "Shop", "DatabaseHelper.cs")));
    TemplateHelper.WriteNugetConfig(folder);
    await TemplateHelper.RunAppCommandAsync($"build \"{Path.Combine(folder, "Shop.slnx")}\"", folder);
    Assert.IsTrue(File.Exists(Path.Combine(folder, "Shop", "bin", "Debug", "net10.0", "Shop.dll")));

    if (kind is not (DbServerKind.Postgres or DbServerKind.SQLite))
      return;

    // ----- 2. run: the host adds the first customer, the test saves one and reads it back ---------------------------
    //
    // The application's schema is `shop`, in the suite's database on PostgreSQL; dropped before and after, so it
    // is a first connect every time and leaves nothing. On SQLite the file is made in the application's folder.
    var environment = new Dictionary<string, string>();
    if (kind == DbServerKind.Postgres) {
      environment["SHOP_CONN"] = PostgresFixture.ConnectionString;
      await Db.ExecuteAsync("DROP SCHEMA IF EXISTS shop CASCADE");
    }
    try {
      var hostOutput = await TemplateHelper.RunAppCommandAsync(
        $"run --project \"{Path.Combine(folder, "Shop", "Shop.csproj")}\" --no-build", folder, environment);
      StringAssert.Contains(hostOutput, "Shop 1.0 is connected.");
      StringAssert.Contains(hostOutput, "Added the first customer and an order.");
      StringAssert.Contains(hostOutput, "1 customer(s):");

      var testOutput = await TemplateHelper.RunAppCommandAsync(
        $"test \"{Path.Combine(folder, "Shop.Tests", "Shop.Tests.csproj")}\" --no-build", folder, environment);
      StringAssert.Contains(testOutput, "Passed!");

      // ----- 3. the database tool over the built application, as the template's own config runs it ----------------
      //
      // The tool loads the model's assembly, the provider and the driver from the host's output folder. SQLite's
      // driver has a native library there, which is found through the host's dependency file.
      var micDb = Path.Combine(folder, "Shop", "mic-db");
      var toolEnvironment = new Dictionary<string, string>(environment);
      if (kind == DbServerKind.SQLite)
        toolEnvironment["SHOP_CONN"] = $"Data Source={Path.Combine(folder, "tool.db")}";
      else
        await Db.ExecuteAsync("DROP SCHEMA IF EXISTS shop CASCADE");
      var upgrade = $"upgrade \"{Path.Combine(micDb, "upgrade.json")}\"";
      var script = Path.Combine(micDb, "upgrade.sql");

      var planned = await TemplateHelper.RunToolAsync(upgrade, folder, toolEnvironment);
      StringAssert.Contains(planned, "Application: Shop 1.0");
      StringAssert.Contains(await File.ReadAllTextAsync(script), "CREATE TABLE");
      var applied = await TemplateHelper.RunToolAsync(upgrade + " --apply", folder, toolEnvironment);
      StringAssert.Contains(applied, "Applied;");
      await TemplateHelper.RunToolAsync(upgrade, folder, toolEnvironment);
      StringAssert.Contains(await File.ReadAllTextAsync(script), "matches the model");

      // The entities job reads the database back through the provider in the same folder. On SQLite only: on
      // PostgreSQL the config names no schema, and the suite's database holds every other test's.
      if (kind == DbServerKind.SQLite) {
        var entities = $"entities \"{Path.Combine(micDb, "entities.json")}\"";
        await TemplateHelper.RunToolAsync(entities, folder, toolEnvironment);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(micDb, "Shop.generated.cs")),
          "public interface ICustomer");
      }
    } finally {
      if (kind == DbServerKind.Postgres)
        await Db.ExecuteAsync("DROP SCHEMA IF EXISTS shop CASCADE");
    }
  }
}
