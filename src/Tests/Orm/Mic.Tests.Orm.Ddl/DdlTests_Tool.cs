using System.Reflection;
using Mic.DbTool;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.DbInfo;
using Mic.Orm.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mic.Tests.Orm.Db;

// THE DATABASE TOOL'S UPGRADE JOB: the script planned against a database and written out, nothing applied; then
// nothing to write once the database matches.
public abstract partial class DdlTests {
  public static class Tool {
    [Entity(Name = "ToolPerson")]
    public interface IPerson {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }

    /// <summary>The same table with a column added, which every server can do to a table that exists.</summary>
    [Entity(Name = "ToolPerson")]
    public interface IPersonNext {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      [Column(Default = "0")] int Score { get; set; }
    }
  }

  [TestMethod]
  public void Tool_LoadsEveryProviderFromAFolder() {
    // The command line names a server kind and a folder; the provider is constructed with its defaults.
    DbServerKind[] kinds = [DbServerKind.Postgres, DbServerKind.MySql, DbServerKind.MsSql, DbServerKind.Oracle,
      DbServerKind.SQLite, DbServerKind.Db2];
    foreach (var kind in kinds)
      Assert.AreEqual(kind, ProviderLoader.Load(kind, AppContext.BaseDirectory).ServerKind);
  }

  [TestMethod]
  public async Task Tool_PlansTheScriptAndAppliesNothing() {
    // ----- 1. a table the database lacks: the script creates it, and the database is untouched --------------------
    var app = AppFor("ToolTest", new Version(1, 0), typeof(Tool.IPerson));
    var report = await DbToolHelper.PlanUpgradeAsync(app, Db.Settings);
    Assert.IsFalse(report.HasErrors, report.Report());
    Assert.IsFalse(report.IsApplied);
    Assert.IsNotNull(report.Script);
    var script = DbToolHelper.FormatScript(report, Db.Settings, app);
    StringAssert.Contains(script, "CREATE TABLE");
    StringAssert.Contains(script, "ToolPerson");
    StringAssert.Contains(script, "ToolTest 1.0");
    StringAssert.Contains(script, DbToolHelper.UpgradeRecordHeading);
    Assert.IsFalse(await Db.TableExistsAsync("ToolPerson"), "planned, not applied");

    // ----- 2. applied, the file holds the script that ran, and the plan has nothing left --------------------------
    var scriptPath = Path.Combine(Path.GetTempPath(), $"mic-tool-{Guid.NewGuid():N}.sql");
    try {
      await File.WriteAllTextAsync(scriptPath, script);
      var applied = await DbToolHelper.ApplyUpgradeAsync(app, Db.Settings, scriptPath);
      Assert.IsTrue(applied.IsApplied, applied.Report());
      var ran = await File.ReadAllTextAsync(scriptPath);
      StringAssert.Contains(ran, DbToolHelper.AppliedNote);
      StringAssert.Contains(ran, "CREATE TABLE");
      StringAssert.Contains(ran, "ToolPerson");
      Assert.IsFalse(script.Contains(DbToolHelper.AppliedNote), "a plan does not say it ran");
      Assert.IsFalse(ran.Contains(DbToolHelper.UpgradeRecordHeading), "the connect that applied it wrote the row");
    } finally {
      File.Delete(scriptPath);
    }
    Assert.IsTrue(await Db.TableExistsAsync("ToolPerson"));
    var again = await DbToolHelper.PlanUpgradeAsync(app, Db.Settings);
    Assert.IsNull(again.Script, again.Report());
    StringAssert.Contains(DbToolHelper.FormatScript(again, Db.Settings, app), "matches the model");
  }

  // THE ROW FOLLOWS THE DATABASE WHERE NO CONNECT APPLIED THE DDL: a version that changes no schema, and a
  // production database upgraded by the tool's script, run by hand. The second is the case the guard on a database
  // ahead of the application exists for, and the guard reads the row.
  [TestMethod]
  public async Task DbInfo_FollowsAVersionBumpAndAScriptRunByHand() {
    Task<bool> IsFailedAsync() =>
      Db.ScalarBoolAsync($"SELECT {Db.Quote("LastUpgradeFailed")} FROM {Db.GetTableRef("DbInfo")}");

    // ----- 1. created at 1.0 by a connect, which stamps the time -----------------------------------------------------
    var created = await ConnectAsync(new Version(1, 0), [typeof(Tool.IPerson)]);
    Assert.IsTrue(created.IsApplied, created.Report());
    var createdOn = _dbInfo!.LastUpgradedOn!.Value;

    // ----- 2. a new version with the same schema: nothing applied, the version follows, the time stays ---------------
    // A table the model does not know is no difference, so the row follows over it as well.
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("NotOurs")} ({Db.Quote("Id")} int NOT NULL)");
    var bumped = await ConnectAsync(new Version(1, 1), [typeof(Tool.IPerson)]);
    Assert.IsFalse(bumped.IsApplied);
    Assert.AreEqual(0, bumped.Changes!.Items.Count, bumped.Changes.Report());
    Assert.AreEqual("1.1", await DbInfoValueAsync("Version"));
    Assert.AreEqual(new Version(1, 1), _dbInfo!.Version);
    Assert.IsTrue((_dbInfo.LastUpgradedOn!.Value - createdOn).Duration() < TimeSpan.FromSeconds(1),
      "no DDL ran, so the time of the last upgrade is not this connect's");

    // ----- 3. production: a connect withholds the next version's changes and leaves the row as it is -----------------
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("DbInfo")} SET {Db.Quote("InstanceType")} = "
      + $"{(int)DbInstanceType.Production}");
    var withheld = await ConnectAsync(new Version(2, 0), [typeof(Tool.IPersonNext)]);
    Assert.IsFalse(withheld.IsApplied);
    Assert.IsTrue(withheld.Changes!.HasItems);
    Assert.AreEqual("1.1", await DbInfoValueAsync("Version"), "a database with differences keeps its row");

    // ----- 4. the tool's script, run statement by statement as the server's own client would ----------------------
    var app = AppFor("UpgradeTest", new Version(2, 0), typeof(Tool.IPersonNext));
    var plan = await DbToolHelper.PlanUpgradeAsync(app, Db.Settings);
    var record = DbInfoModule.BuildUpgradeRecordSql(app.DataAccess.FindDatabase(Db.Settings.DatabaseAlias)!);
    Assert.IsNotNull(record);
    var script = DbToolHelper.FormatScript(plan, Db.Settings, app);
    StringAssert.Contains(script, DbToolHelper.UpgradeRecordHeading);
    StringAssert.EndsWith(script.TrimEnd(), record + Db.Provider.SqlDialect.StatementEnd,
      "the record is the last statement");
    foreach (var step in plan.Script!.Steps)
      await Db.ExecuteAsync(step.Text);
    await Db.ExecuteAsync(record);
    Assert.AreEqual("2.0", await DbInfoValueAsync("Version"));

    // ----- 5. the next connect finds the database and its row as the model wants them -----------------------------
    var settled = await ConnectAsync(new Version(2, 0), [typeof(Tool.IPersonNext)]);
    Assert.AreEqual(0, settled.Changes!.Items.Count, settled.Changes.Report());
    Assert.AreEqual(new Version(2, 0), settled.PreviousVersion);
    Assert.AreEqual(DbInstanceType.Production, _dbInfo!.InstanceType, "the administrator's mark survives the script");
    Assert.IsTrue(_dbInfo.Details.ModuleVersions.ContainsKey(DbInfoModule.ModuleName), "the details as a literal");
    Assert.IsNotNull(_dbInfo.Details.GuidScheme);
    // The server's clock, in UTC: a local time would be hours off.
    Assert.IsTrue((_dbInfo.LastUpgradedOn!.Value - DateTime.UtcNow).Duration() < TimeSpan.FromMinutes(30),
      $"stamped {_dbInfo.LastUpgradedOn:o}, now {DateTime.UtcNow:o}");

    // ----- 6. an older build is refused, since the row now says where the database is -------------------------------
    var refused = await Assert.ThrowsAsync<DataAccessException>(
      () => ConnectAsync(new Version(1, 1), [typeof(Tool.IPerson)]));
    StringAssert.Contains(refused.Message, "ahead");

    // ----- 7. a failed upgrade repaired by hand: the note goes once the database matches --------------------------
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("DbInfo")} SET {Db.Quote("LastUpgradeFailed")} = "
      + Db.Provider.SqlDialect.BooleanLiteral(true));
    Assert.IsTrue(await IsFailedAsync());
    await ConnectAsync(new Version(2, 0), [typeof(Tool.IPersonNext)]);
    Assert.IsFalse(await IsFailedAsync());

    // ----- 8. a row the server will not take is a warning, and the connect stands ------------------------------------
    //
    // A constraint made by hand refuses the next version, as a login without the right to write would refuse any.
    if (Db.Provider.Supports(DbFeatures.AddConstraintsAfterCreate)) {
      await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("DbInfo")} ADD CONSTRAINT {Db.Quote("DbInfo_by_hand")} "
        + $"CHECK ({Db.Quote("Version")} <> '2.1')");
      var unrecorded = await ConnectAsync(new Version(2, 1), [typeof(Tool.IPersonNext)]);
      Assert.IsFalse(unrecorded.HasErrors, unrecorded.Report());
      Assert.IsTrue(unrecorded.Messages.Any(m => m.Severity == Severity.Warning && m.Message.Contains("2.1")),
        unrecorded.Report());
      Assert.AreEqual("2.0", await DbInfoValueAsync("Version"));
    }
  }

  // THE ENTITIES JOB: the declarations read back off a database MIC made are the declarations it was made from —
  // the keys on their members, the reference in place of its column, the composite key on the interface — and a
  // table outside the selection leaves a plain column.
  public static class ToolDb {
    [Entity]
    public interface IToolOrg {
      [PrimaryKey] short Id { get; set; }
      [Size(100), Unique] string Name { get; set; }
    }

    [Entity, Unique("Org,Login")]
    public interface IToolUser {
      [PrimaryKey, Auto] Guid Id { get; set; }
      [Index] IToolOrg Org { get; set; }
      [Size(100)] string Login { get; set; }
      [Column(Precision = 9, Scale = 2)] decimal Rate { get; set; }
      DateTime? LastOn { get; set; }
      [Unlimited] string? Notes { get; set; }
    }

    [Entity]
    public interface IToolLog {
      [PrimaryKey, Identity] int Id { get; set; }
      IToolUser? User { get; set; }
      [Size(200)] string Text { get; set; }
      [Json] string? Payload { get; set; }
      [Size(20)] byte[]? Token { get; set; }
    }

    /// <summary>A view over the orgs, which comes back with its select in the module.</summary>
    [Entity]
    public interface IToolRanked {
      [PrimaryKey] short Id { get; set; }
      [Size(100)] string Name { get; set; }
    }
  }

  [TestMethod]
  public async Task Tool_WritesTheEntitiesBack() {
    var app = BuildApp("ToolDbTest", new Version(1, 0),
      a => a.Modules[0].RegisterView<ToolDb.IToolRanked>(
        from o in ViewHelper.EntitySet<ToolDb.IToolOrg>() select new { o.Id, o.Name }),
      typeof(ToolDb.IToolOrg), typeof(ToolDb.IToolUser), typeof(ToolDb.IToolLog), typeof(ToolDb.IToolRanked));
    await app.ConnectToAsync(Db.Settings);

    // A check over two columns, made by hand where the server adds one to a table that exists. The model does not
    // declare it, and the database keeps it.
    const string byHand = "ToolUser_rate_login";
    var hasCheck = Db.Provider.Supports(DbFeatures.AddConstraintsAfterCreate);
    if (hasCheck)
      await Db.ExecuteAsync($"ALTER TABLE {Db.GetTableRef("ToolUser")} ADD CONSTRAINT {Db.Quote(byHand)} "
        + $"CHECK ({Db.Quote("Rate")} >= 0 AND {Db.Quote("Login")} <> 'x')");
    string[] schemas = Db.Schema.Length == 0 ? [] : [Db.Schema];
    var model = await DbToolHelper.LoadModelAsync(Db.Settings, schemas);

    // ----- 1. the whole selection: the declarations come back as they were written ------------------------------
    var writer = new EntitySourceWriter(model, Db.Settings, new EntitySourceOptions {
      Namespace = "Tool.Test", AppClassName = "ToolTestApp", Tables = ["Tool*"], AddDbObjectsNames = false,
    });
    var source = writer.Write();
    Assert.AreEqual(hasCheck ? 1 : 0, writer.Warnings.Count, string.Join(Environment.NewLine, writer.Warnings));

    // The check is a comment above its interface: its name, its condition with both columns, and the declaration
    // that would make it the application's.
    var checkLine = $"// [Check(DbServerKind.{Db.Provider.ServerKind}, \"ToolUserRateLogin\", \"";
    if (hasCheck) {
      var comment = source.Split(Environment.NewLine).Single(l => l.StartsWith($"// Check constraint {byHand}: "));
      StringAssert.Contains(comment, "Rate");
      StringAssert.Contains(comment, "Login");
      AssertHasLine(source, "// The database keeps it, and an upgrade leaves it alone.");
      AssertHasLine(source, $"// To have MIC own it, uncomment, and drop {byHand}:");
      AssertHasLine(source, checkLine);
    }
    AssertHasLine(source, "namespace Tool.Test;");
    AssertHasLine(source, "public interface IToolOrg {");
    AssertHasMember(source, "[PrimaryKey]", "short Id { get; set; }");
    AssertHasLine(source, "public interface IToolLog {" + Environment.NewLine + "  [PrimaryKey, Identity]");   // the key first
    AssertHasMember(source, "[Size(100), Unique]", "string Name { get; set; }");
    AssertHasLine(source, "[Entity, Unique(\"Org,Login\")]");
    AssertHasMember(source, "[PrimaryKey, Auto]", "Guid Id { get; set; }");
    AssertHasMember(source, "[Index]", "IToolOrg Org { get; set; }");
    AssertHasMember(source, "[Column(Precision = 9, Scale = 2)]", "decimal Rate { get; set; }");
    AssertHasMember(source, null, "DateTime? LastOn { get; set; }");
    AssertHasMember(source, "[Unlimited]", "string? Notes { get; set; }");
    AssertHasMember(source, "[PrimaryKey, Identity]", "int Id { get; set; }");
    AssertHasMember(source, null, "IToolUser? User { get; set; }");
    // A JSON column, whether the server's JSON type or text under the check MIC made for it: the attribute alone,
    // and the check neither a comment nor a warning. On Db2, which has neither the type nor a test for the check,
    // the column is the unlimited text it is declared as.
    AssertHasMember(source, Db.Provider.ServerKind == DbServerKind.Db2 ? "[Unlimited]" : "[Json]",
      "string? Payload { get; set; }");
    Assert.IsFalse(source.Contains("IsJson"), source);
    // A binary that says its size: the size where the server has a bounded binary type, unlimited where it has not.
    var hasBoundedBinary = Db.Provider.TypeRegistry.FindSpecial(DbSpecialType.Binary) != null;
    AssertHasMember(source, hasBoundedBinary ? "[Size(20)]" : "[Unlimited]", "byte[]? Token { get; set; }");
    AssertHasLine(source, "  // Lists: uncomment, delete, or replace with IQueryable<T>");
    AssertHasLine(source, "  // IList<IToolUser> ToolUsers { get; }");
    AssertHasLine(source, "  // IList<IToolLog> ToolLogs { get; }");
    AssertHasLine(source, "RegisterEntities(typeof(IToolLog), typeof(IToolOrg), typeof(IToolUser));");
    AssertHasLine(source, "public class ToolTestApp : EntityApp {");
    Assert.IsFalse(source.Contains("DbInfo"), "MIC's own table is not an entity");

    // The view: marked as one, no key declared since a view defines none, its select a constant the module
    // registers it with.
    AssertHasLine(source, "[Entity, View]");
    var viewStart = source.IndexOf("public interface IToolRanked {", StringComparison.Ordinal);
    var viewSource = source[viewStart..source.IndexOf(Environment.NewLine + "}", viewStart, StringComparison.Ordinal)];
    StringAssert.Contains(viewSource, " Id { get; set; }");
    Assert.IsFalse(viewSource.Contains("PrimaryKey"),
      "a key was declared on the view:" + Environment.NewLine + viewSource);
    AssertHasLine(source, "  public const string ToolRankedSql = \"\"\"");
    AssertHasLine(source, $"RegisterView<IToolRanked>(DbServerKind.{Db.Provider.ServerKind}, ViewDefinitions.ToolRankedSql);");

    // ----- 2. the round trip: compiled, constructed, and planned against the same database — nothing to do -------
    var generated = CompileAndConstruct(source, "ToolTestApp");
    var plan = await DbToolHelper.PlanUpgradeAsync(generated, Db.Settings);
    Assert.IsFalse(plan.HasErrors, plan.Report());
    AssertNothingButViews(plan);

    // ----- 3. every name pinned, for the renaming that follows a database-first start: still nothing to do --------
    var pinned = new EntitySourceWriter(model, Db.Settings, new EntitySourceOptions {
      Namespace = "Tool.Pinned", AppClassName = "PinnedApp", Tables = ["Tool*"],
    });
    var pinnedSource = pinned.Write();
    AssertHasLine(pinnedSource, "[Entity(TableName = \"ToolOrg\")]");
    AssertHasMember(pinnedSource, "[Size(100), Column(\"Name\"), Unique(Name = \"UQ_ToolOrg_Name\")]",
      "string Name { get; set; }");
    AssertHasLine(pinnedSource, "[Entity(TableName = \"ToolUser\"), Unique(\"Org,Login\", Name = \"UQ_ToolUser_Org_Login\")]");
    // A server that keeps no constraint names, SQLite, gets no foreign-key name to pin.
    var foreignKeyName = Db.Provider.Supports(DbFeatures.NamedRefConstraints)
      ? ", ForeignKeyName = \"FK_ToolUser_Org_Id\"" : "";
    AssertHasMember(pinnedSource, $"[EntityRef(KeyColumns = \"Org_Id\"{foreignKeyName}), Index(Name = \"IX_ToolUser_Org\")]",
      "IToolOrg Org { get; set; }");
    AssertHasMember(pinnedSource, "[Column(\"Rate\", Precision = 9, Scale = 2)]", "decimal Rate { get; set; }");
    AssertHasMember(pinnedSource,
      Db.Provider.ServerKind == DbServerKind.Db2 ? "[Unlimited, Column(\"Payload\")]" : "[Json, Column(\"Payload\")]",
      "string? Payload { get; set; }");
    AssertHasLine(pinnedSource, "rename any identifier freely");
    AssertHasLine(pinnedSource, "[Entity, View(\"ToolRanked\")]");
    var pinnedPlan = await DbToolHelper.PlanUpgradeAsync(CompileAndConstruct(pinnedSource, "PinnedApp"), Db.Settings);
    AssertNothingButViews(pinnedPlan);

    // ----- 4. a subset: the reference to the table left out is a plain column, and said so -----------------------
    var subset = new EntitySourceWriter(model, Db.Settings, new EntitySourceOptions {
      Tables = ["ToolLog"], Lists = false, AddDbObjectsNames = false,
    });
    var subsetSource = subset.Write();
    Assert.AreEqual(1, subset.Warnings.Count, string.Join(Environment.NewLine, subset.Warnings));
    AssertHasMember(subsetSource, "[Column(\"User_Id\")]",
      "Guid? UserId { get; set; }   // references ToolUser, which is outside the selection");
    StringAssert.Contains(subsetSource, "1 foreign key(s) point outside the selection");
    Assert.IsFalse(subsetSource.Contains("IToolUser"), "the entity left out is not mentioned as a type");

    // ----- 5. a table with no primary key: a unique key over required columns stands in, or the table is left out --
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("ToolByCode")} ({Db.Quote("Code")} int NOT NULL, "
      + $"{Db.Quote("Label")} varchar(20))");
    await Db.ExecuteAsync(string.Format(Db.Provider.SqlDialect.CreateUniqueIndex, Db.Quote("ToolByCode_code"),
      Db.GetTableRef("ToolByCode"), Db.Quote("Code"), Db.GetTableRef("ToolByCode_code"), "", ""));
    await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("ToolKeyless")} ({Db.Quote("Code")} int NOT NULL)");
    var keyless = new EntitySourceWriter(await DbToolHelper.LoadModelAsync(Db.Settings, schemas), Db.Settings,
      new EntitySourceOptions {
        Namespace = "Tool.Keyless", AppClassName = "ToolKeylessApp", Tables = ["ToolByCode", "ToolKeyless"],
        Lists = false,
      });
    var keylessSource = keyless.Write();
    AssertHasLine(keylessSource, "[PrimaryKey(Name = \"ToolByCode_code\")");
    Assert.IsFalse(keylessSource.Contains("ToolKeyless\""), "the table with no key at all is not written");
    Assert.IsTrue(keyless.Warnings.Any(w => w.StartsWith("ToolByCode:") && w.Contains("as the primary key")),
      string.Join(Environment.NewLine, keyless.Warnings));
    Assert.IsTrue(keyless.Warnings.Any(w => w.StartsWith("ToolKeyless:") && w.Contains("not written")),
      string.Join(Environment.NewLine, keyless.Warnings));
    // The model builds: the stand-in is a primary key the entity model accepts.
    var keylessApp = CompileAndConstruct(keylessSource, "ToolKeylessApp");
    Assert.IsTrue(keylessApp.Model!.Entities.Any(e => e.Name == "ToolByCode"));

    // PostgreSQL's array columns: an array member each, and a model that builds with them.
    if (Db.Provider.ServerKind == DbServerKind.Postgres) {
      await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("ToolArrays")} ({Db.Quote("Id")} int NOT NULL PRIMARY KEY, "
        + $"{Db.Quote("Counts")} integer[] NOT NULL, {Db.Quote("Tags")} text[])");
      var arrays = new EntitySourceWriter(await DbToolHelper.LoadModelAsync(Db.Settings, schemas), Db.Settings,
        new EntitySourceOptions {
          Namespace = "Tool.Arrays", AppClassName = "ToolArraysApp", Tables = ["ToolArrays"], Lists = false,
        });
      var arraysSource = arrays.Write();
      AssertHasLine(arraysSource, "  int[] Counts { get; set; }");
      AssertHasLine(arraysSource, "  string[]? Tags { get; set; }");
      Assert.AreEqual(0, arrays.Warnings.Count, string.Join(Environment.NewLine, arrays.Warnings));
      var arraysApp = CompileAndConstruct(arraysSource, "ToolArraysApp");
      var arraysEntity = arraysApp.Model!.Entities.Single(e => e.Name == "ToolArrays");
      Assert.AreEqual(typeof(int), arraysEntity.GetMember("Counts").ArrayItemType);

      // pgvector's columns: a [Vector] member each, the metric of the index over one on its attribute, and the
      // index with no access method stated.
      await Db.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");
      await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("ToolVectors")} ({Db.Quote("Id")} int NOT NULL PRIMARY KEY, "
        + $"{Db.Quote("Embedding")} vector(3) NOT NULL, {Db.Quote("Position")} vector(4), {Db.Quote("Loose")} vector)");
      await Db.ExecuteAsync($"CREATE INDEX {Db.Quote("ToolVectors_Embedding")} ON {Db.GetTableRef("ToolVectors")} "
        + $"USING hnsw ({Db.Quote("Embedding")} vector_cosine_ops)");
      await Db.ExecuteAsync($"CREATE INDEX {Db.Quote("ToolVectors_Position")} ON {Db.GetTableRef("ToolVectors")} "
        + $"USING hnsw ({Db.Quote("Position")} vector_l2_ops)");
      var vectors = new EntitySourceWriter(await DbToolHelper.LoadModelAsync(Db.Settings, schemas), Db.Settings,
        new EntitySourceOptions {
          Namespace = "Tool.Vectors", AppClassName = "ToolVectorsApp", Tables = ["ToolVectors"], Lists = false,
          AddDbObjectsNames = false,
        });
      var vectorsSource = vectors.Write();
      AssertHasLine(vectorsSource, "  [Vector(3), Index]");
      AssertHasLine(vectorsSource, "  float[] Embedding { get; set; }");
      AssertHasLine(vectorsSource, "  [Vector(4, Metric = VectorMetric.Euclidean), Index]");
      AssertHasLine(vectorsSource, "  float[]? Position { get; set; }");
      Assert.IsFalse(vectorsSource.Contains("Loose {"), "a vector with no dimensions is not written");
      Assert.IsTrue(vectors.Warnings.Single().Contains("Loose: it is a vector with no dimensions"),
        string.Join(Environment.NewLine, vectors.Warnings));
      var vectorsEntity = CompileAndConstruct(vectorsSource, "ToolVectorsApp").Model!.Entities
        .Single(e => e.Name == "ToolVectors");
      Assert.IsTrue(vectorsEntity.GetMember("Embedding").IsVector);
      Assert.AreEqual(VectorMetric.Euclidean, vectorsEntity.GetMember("Position").VectorMetric);
    }
    if (!hasCheck) {
      // SQLite makes a check with its table; the comment and the offered declaration are the same as elsewhere.
      await Db.ExecuteAsync($"CREATE TABLE {Db.GetTableRef("ToolNote")} ({Db.Quote("Id")} int NOT NULL PRIMARY KEY, "
        + $"{Db.Quote("Rank")} int NOT NULL, CONSTRAINT {Db.Quote("ToolNote_rank")} CHECK ({Db.Quote("Rank")} >= 0))");
      var notes = new EntitySourceWriter(await DbToolHelper.LoadModelAsync(Db.Settings, schemas), Db.Settings,
        new EntitySourceOptions { Tables = ["ToolNote"], Lists = false, AddDbObjectsNames = false });
      var notesSource = notes.Write();
      AssertHasLine(notesSource, "// Check constraint ToolNote_rank: \"Rank\" >= 0");
      AssertHasLine(notesSource, "// The database keeps it, and an upgrade leaves it alone.");
      AssertHasLine(notesSource, "// [Check(DbServerKind.SQLite, \"ToolNoteRank\", \"");
      return;
    }

    // ----- 6. the offered declaration, uncommented: it compiles, and its condition is SQL the server takes --------
    var declared = CompileAndConstruct(source.Replace(checkLine, checkLine[3..]), "ToolTestApp");
    var declaredPlan = await DbToolHelper.PlanUpgradeAsync(declared, Db.Settings);
    Assert.IsFalse(declaredPlan.HasErrors, declaredPlan.Report());
    var added = declaredPlan.Script!.Steps.Single(s => s.Change?.ObjectKind == DbObjectKind.CheckConstraint);
    Assert.AreEqual(DbDiffKind.Add, added.Change!.DiffKind, "MIC's constraint beside the one made by hand");
    await Db.ExecuteAsync(added.Text);

    // ----- 7. a check under MIC's own name, and one whose condition holds braces in quoted text -------------------
    var log = Db.GetTableRef("ToolLog");
    await Db.ExecuteAsync($"ALTER TABLE {log} ADD CONSTRAINT {Db.Quote("CK_ToolLog_Text_0a1b2c3d")} "
      + $"CHECK ({Db.Quote("Text")} <> 'none')");
    await Db.ExecuteAsync($"ALTER TABLE {log} ADD CONSTRAINT {Db.Quote("ToolLog_braces")} "
      + $"CHECK ({Db.Quote("Text")} <> '{{none}}')");
    var checks = new EntitySourceWriter(await DbToolHelper.LoadModelAsync(Db.Settings, schemas), Db.Settings,
      new EntitySourceOptions { Tables = ["ToolLog"], Lists = false, AddDbObjectsNames = false });
    var checksSource = checks.Write();
    AssertHasLine(checksSource, "// It carries the name MIC gives a check it declares, so an upgrade over this model "
      + "drops it unless it is declared.");
    AssertHasLine(checksSource, $"// [Check(DbServerKind.{Db.Provider.ServerKind}, \"Text\", \"");
    // Braces inside quoted text are text: the declaration is offered as for any other condition.
    AssertHasLine(checksSource, $"// [Check(DbServerKind.{Db.Provider.ServerKind}, \"ToolLogBraces\", \"");
    Assert.IsFalse(checksSource.Contains("reads as a member's name"), checksSource);
    Assert.IsTrue(checks.Warnings.Any(w => w.Contains("CK_ToolLog_Text_0a1b2c3d") && w.Contains("drops it")),
      string.Join(Environment.NewLine, checks.Warnings));
  }

  /// <summary>Asserts that a plan over a generated model has nothing to do but recreate views: a view the server
  /// reported is registered with the server's rewrite of its select, whose hash is not the one MIC stored, so the
  /// first upgrade recreates it once.</summary>
  private static void AssertNothingButViews(DbUpgradeReport plan) {
    var others = plan.Changes?.Items.Where(i => i.ObjectKind != DbObjectKind.View).ToList() ?? [];
    Assert.AreEqual(0, others.Count, "the generated model differs from the database:" + Environment.NewLine + plan.Report());
  }

  /// <summary>Asserts that the source contains the text, showing the whole source when it does not.</summary>
  private static void AssertHasLine(string source, string text) =>
    Assert.IsTrue(source.Contains(text, StringComparison.Ordinal),
      $"expected the source to contain: {text}{Environment.NewLine}{source}");

  /// <summary>Asserts that the source declares a member as written: its attributes on the line above, or none, and
  /// the declaration indented on its own line.</summary>
  private static void AssertHasMember(string source, string? attributes, string declaration) =>
    AssertHasLine(source, (attributes == null ? "" : $"  {attributes}{Environment.NewLine}") + $"  {declaration}");

  /// <summary>Compiles generated source in memory against everything this test process has loaded, loads it, and
  /// constructs the application class of the given name, initialized.</summary>
  private static EntityApp CompileAndConstruct(string source, string appClassName) {
    var references = AppDomain.CurrentDomain.GetAssemblies()
      .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
      .Select(a => MetadataReference.CreateFromFile(a.Location))
      .ToList();
    var compilation = CSharpCompilation.Create($"Generated_{Guid.NewGuid():N}",
      [CSharpSyntaxTree.ParseText(source)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    using var output = new MemoryStream();
    var emitted = compilation.Emit(output);
    var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}").ToList();
    Assert.IsTrue(emitted.Success, "the generated source does not compile:" + Environment.NewLine
      + string.Join(Environment.NewLine, errors) + Environment.NewLine + source);

    var assembly = Assembly.Load(output.ToArray());
    var appType = assembly.GetTypes().Single(t => t.Name == appClassName);
    var app = (EntityApp)Activator.CreateInstance(appType)!;
    app.Init();
    return app;
  }
}
