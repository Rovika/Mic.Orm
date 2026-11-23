using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;

namespace Mic.Tests.Orm;

// A MEMBER THE DATABASE COMPUTES out of a JSON document, without a server: what it maps to, what its DDL says, what
// is refused, and how an upgrade tells a computed column from a plain one.
[TestClass]
public class FromJsonTests {
  [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
  [System.Text.Json.Serialization.JsonDerivedType(typeof(Circle), "circle")]
  public abstract class Shape {
    public int Size { get; set; }
  }

  public sealed class Circle : Shape { }

  public interface IDrawing {
    [PrimaryKey] Guid Id { get; set; }
    [Json(Discriminator = "Kind")] Shape Shape { get; set; }
    [FromJson("Shape", "$.kind"), Size(20), Index] string Kind { get; }
    [FromJson("Shape", "$.Size", Stored = true)] int Size { get; }
  }

  static EntityApp NewApp(params Type[] entities) {
    var app = new EntityApp("Computed", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entities.Length > 0 ? entities : [typeof(IDrawing)]);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void FromJson_MapsToAComputedColumnOverThePath() {
    var app = NewApp();
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IDrawing))!;
    var kind = entity.GetMember("Kind");
    Assert.IsTrue(kind.IsDbGenerated, "read-only, out of every insert and update");
    Assert.AreEqual("$.kind", kind.FromJson!.Path);
    Assert.IsNull(entity.GetMember("Shape").JsonDiscriminatorMember, "the serializer carries the discriminator; no column leads");

    var model = app.MapToPostgres();
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var table = model.GetTable(entity)!;
    var column = table.GetColumn(kind)!;
    Assert.IsTrue(column.IsComputed);
    Assert.IsTrue(column.IsNullable, "the server decides");
    StringAssert.Contains(column.ComputedExpression, "'$.kind'");
    Assert.IsFalse(table.InsertColumns.Contains(column), "never written");
    Assert.IsTrue(table.Keys.Any(k => k.Columns.Any(c => ReferenceEquals(c.Column, column))), "indexed like any column");

    // ----- the DDL: a generated column, stored on Postgres whatever was declared ----------------------------------
    var create = new DbDdlBuilder(model.Provider).CreateTable(table).Text;
    StringAssert.Contains(create, "\"Kind\" varchar(20) GENERATED ALWAYS AS (");
    StringAssert.Contains(create, "'$.kind'");
    StringAssert.Contains(create, ") STORED");
    StringAssert.Contains(create, "\"Size\" integer GENERATED ALWAYS AS (");
  }

  [TestMethod]
  public void FromJson_RefusesWhatItCannotRead() {
    StringAssert.Contains(Report(typeof(INoSuchDocument)), "names no JSON member");
    StringAssert.Contains(Report(typeof(IRootless)), "does not start at the document's root");
    StringAssert.Contains(Report(typeof(IWrongDiscriminatorPath)), "the path must be '$.kind'");
  }

  public interface INoSuchDocument {
    [PrimaryKey] Guid Id { get; set; }
    [Json] Shape Shape { get; set; }
    [FromJson("Nope", "$.kind")] string Kind { get; }
  }

  public interface IRootless {
    [PrimaryKey] Guid Id { get; set; }
    [Json] Shape Shape { get; set; }
    [FromJson("Shape", "kind")] string Kind { get; }
  }

  public interface IWrongDiscriminatorPath {
    [PrimaryKey] Guid Id { get; set; }
    [Json(Discriminator = "Kind")] Shape Shape { get; set; }
    [FromJson("Shape", "$.type")] string Kind { get; }
  }

  static string Report(Type entity) {
    var app = NewApp(entity);
    Assert.IsFalse(app.Initialized, "the model should have been refused");
    return app.Report();
  }

  [TestMethod]
  public void FromJson_IsComparedAsComputedAndRecreatedWhenItWasNot() {
    var app = NewApp();
    var target = app.MapToPostgres();
    var table = target.GetTable(app.Model!.FindEntity(typeof(IDrawing))!)!;

    // The same table as a database holds it: the computed columns computed, so nothing differs.
    var source = NewApp().MapToPostgres();
    var comparer = new DbModelComparer(target.Provider);
    var diff = comparer.Compare(source, target);
    Assert.AreEqual(0, diff.Items.Count, diff.Report());

    // The database holds Kind as a plain column: recreated, dropped before anything adds.
    var plain = source.GetTable(source.EntityModel!.FindEntity(typeof(IDrawing))!)!.GetColumn(table.Entity!.GetMember("Kind"))!;
    plain.DbFlags &= ~DbColumnFlags.Computed;
    diff = comparer.Compare(source, target);
    var change = diff.Items.Single();
    Assert.AreEqual(DbDiffKind.Modify, change.DiffKind);
    StringAssert.Contains(change.Description, "now computed");
    var script = new DbUpgradeScriptBuilder(target.Provider).Build(diff);
    CollectionAssert.AreEqual(
      new[] { DdlKind.DropIndex, DdlKind.DropColumn, DdlKind.AddColumn, DdlKind.Comment, DdlKind.CreateIndex },
      script.Steps.Select(s => s.Ddl!.Kind).ToList(),
      "the index over the column goes and comes back with it, and the expression's hash is written beside it");
    StringAssert.Contains(script.Steps[2].Text, "GENERATED ALWAYS AS");
    StringAssert.Contains(script.Steps[3].Text, "COMMENT ON COLUMN");
    StringAssert.Contains(script.Steps[3].Text, DefinitionHashHelper.Prefix);

    // ----- a changed expression is found through the hash, the server's text of it being its own ----------------
    plain.DbFlags |= DbColumnFlags.Computed;
    var kind = table.GetColumn(table.Entity!.GetMember("Kind"))!;
    // As a loader reads the column: computed, no expression, the hash MIC wrote beside it.
    plain.ComputedExpression = null;
    plain.StoredDefinitionHash = DefinitionHashHelper.Of(kind.ComputedExpression!);
    Assert.AreEqual(0, comparer.Compare(source, target).Items.Count, "the same expression by its hash");
    plain.StoredDefinitionHash = DefinitionHashHelper.Of("another expression");
    var changed = comparer.Compare(source, target).Items.Single();
    Assert.AreEqual(DbDiffKind.Modify, changed.DiffKind);
    StringAssert.Contains(changed.Description, "expression changed");

    // ----- a computed column with no hash beside it is one MIC cannot vouch for -----------------------------------
    plain.StoredDefinitionHash = null;
    StringAssert.Contains(comparer.Compare(source, target).Items.Single().Description, "no hash of the expression");
  }
}
