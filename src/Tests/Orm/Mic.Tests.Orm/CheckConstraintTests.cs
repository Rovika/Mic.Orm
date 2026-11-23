using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Db.SqlServer;

namespace Mic.Tests.Orm;

// A declared check constraint, without a server: the name it takes, the declaration picked for a server, the
// comparison by name and the statements an upgrade writes.
[TestClass]
public class CheckConstraintTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  [Entity(Name = "Item")]
  [Check("Price", "{Price} >= 0")]
  [Check(DbServerKind.SQLite, "Price", "{Price} >= 1")]
  [Check(DbServerKind.Oracle, "OracleOnly", "{Stock} > 0")]
  private interface IItem {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
    int Stock { get; set; }
  }

  [Entity(Name = "Item")]
  [Check("Price", "{Price} > 0")]
  [Check("Stock", "{Stock} >= 0")]
  private interface IItemChanged {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
    int Stock { get; set; }
  }

  [Entity(Name = "Item")]
  [Check("Cost", "{Cost} >= 0")]
  private interface IItemWithUnknownMember {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
  }

  [Entity(Name = "Item")]
  [Check("Price", "{Price} >= 0")]
  [Check("price", "{Price} >= 1")]
  private interface IItemDeclaredTwice {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
  }

  [Entity(Name = "Parcel")]
  [Check("Zip", "{Zip} <> '{none}' AND {Zip} NOT LIKE 'it''s {Zip}' AND length({Zip}) = 5")]
  private interface IParcel {
    [PrimaryKey] Guid Id { get; set; }
    [Size(10)] string Zip { get; set; }
  }

  [Entity(Name = "Parcel")]
  [Check("Zip", "{Zip} <> '{none}' AND {none} = 1")]
  private interface IParcelWithUnknownMember {
    [PrimaryKey] Guid Id { get; set; }
    [Size(10)] string Zip { get; set; }
  }

  private static EntityApp NewApp(params Type[] types) {
    var app = new EntityApp("Checks", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  private static DbModel MapFor(DbProvider provider, params Type[] types) {
    var app = NewApp(types);
    Assert.IsTrue(app.Initialized, app.Report());
    return new DbModelBuilder(app, app.Model!, new DbSettings(provider, NoServer)).Build();
  }

  [TestMethod]
  public void Check_IsNamedByItsConditionAndPickedForTheServer() {
    // ----- the declaration for every server, its members written as columns ----------------------------------------
    var postgres = MapFor(new PostgresProvider(), typeof(IItem));
    Assert.IsFalse(postgres.BuildReport.HasErrors, postgres.BuildReport.Format());
    var table = postgres.Tables.Single(t => t.Name == "Item");
    var check = table.CheckConstraints.Single();
    Assert.AreEqual("\"Price\" >= 0", check.Expression);
    Assert.AreEqual(DbCheckConstraintInfo.GetManagedName("Item", "Price", "\"Price\" >= 0"), check.Name);
    StringAssert.StartsWith(check.Name, "CK_Item_Price_");
    Assert.AreEqual("CK_Item_Price_".Length + 8, check.Name.Length, "eight hex digits of the hash");

    // ----- the name is recognised as the ORM's, for its table only -------------------------------------------------
    Assert.IsTrue(DbCheckConstraintInfo.IsManagedName(check.Name, "Item"));
    Assert.IsTrue(DbCheckConstraintInfo.IsManagedName(check.Name.ToUpperInvariant(), "item"),
      "a server may fold case");
    Assert.IsFalse(DbCheckConstraintInfo.IsManagedName(check.Name, "Order"));
    Assert.IsFalse(DbCheckConstraintInfo.IsManagedName("Item_by_hand", "Item"));
    Assert.IsFalse(DbCheckConstraintInfo.IsManagedName("CK_Item_Price", "Item"), "no hash");
    Assert.IsFalse(DbCheckConstraintInfo.IsManagedName("CK_Item_Price_notahash", "Item"));

    // ----- another condition is another name ------------------------------------------------------------------------
    Assert.AreNotEqual(check.Name, DbCheckConstraintInfo.GetManagedName("Item", "Price", "\"Price\" > 0"));

    // ----- a declaration written for one server takes the place of the general one there ---------------------------
    var sqlite = MapFor(new SqliteProvider(), typeof(IItem));
    Assert.IsFalse(sqlite.BuildReport.HasErrors, sqlite.BuildReport.Format());
    var sqliteCheck = sqlite.Tables.Single(t => t.Name.EndsWith("Item")).CheckConstraints.Single();
    StringAssert.EndsWith(sqliteCheck.Expression, ">= 1");

    // ----- the CREATE TABLE carries it --------------------------------------------------------------------------------
    var create = new DbDdlBuilder(new PostgresProvider()).CreateTable(table).Text;
    StringAssert.Contains(create, $"CONSTRAINT \"{check.Name}\" CHECK (\"Price\" >= 0)");
  }

  [TestMethod]
  public void Check_LeavesABracedWordInQuotedTextAsWritten() {
    // ----- a member outside quoted text becomes its column; a braced word inside is text, a doubled quote passed ---
    var model = MapFor(new PostgresProvider(), typeof(IParcel));
    Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
    var check = model.Tables.Single(t => t.Name == "Parcel").CheckConstraints.Single();
    Assert.AreEqual("\"Zip\" <> '{none}' AND \"Zip\" NOT LIKE 'it''s {Zip}' AND length(\"Zip\") = 5",
      check.Expression);

    // ----- outside quoted text a word that is no member is still refused, and that one only ----------------------
    var errors = MapFor(new PostgresProvider(), typeof(IParcelWithUnknownMember)).BuildReport.Format();
    StringAssert.Contains(errors, "names '{none}' in its expression");
    Assert.AreEqual(2, errors.Split("names '{").Length, errors);

    // ----- what the entities job asks before it offers a declaration ---------------------------------------------
    Assert.IsFalse(DbModelBuilder.HasBracedNames("\"Zip\" ~ '^[0-9]{5}$' AND \"Zip\" <> '{none}'"));
    Assert.IsTrue(DbModelBuilder.HasBracedNames("\"Zip\" <> '{none}' AND {none} = 1"));
    Assert.IsFalse(DbModelBuilder.HasBracedNames("\"Zip\" > 0"));
  }

  [TestMethod]
  public void Check_IsComparedByNameAndAnotherNameIsLeftAlone() {
    var provider = new PostgresProvider();
    var before = MapFor(provider, typeof(IItem));
    var after = MapFor(provider, typeof(IItemChanged));
    var existing = before.Tables.Single(t => t.Name == "Item");
    var old = existing.CheckConstraints.Single();
    existing.AddCheckConstraint(new DbCheckConstraintInfo("Item_by_hand", existing, "(\"Stock\" < 1000000)"));

    // ----- the changed condition is a drop and an add, the new check an add, the hand-made one nothing -------------
    var changes = provider.CreateDbModelComparer().Compare(before, after);
    var checks = changes.Items.Where(c => c.ObjectKind == DbObjectKind.CheckConstraint).ToList();
    Assert.AreEqual(3, checks.Count, changes.Report());
    Assert.AreEqual(old.Name, checks.Single(c => c.DiffKind == DbDiffKind.Drop).Source!.Name);
    Assert.AreEqual(2, checks.Count(c => c.DiffKind == DbDiffKind.Add));

    // ----- the same model twice is no change ------------------------------------------------------------------------
    var none = provider.CreateDbModelComparer()
      .Compare(MapFor(provider, typeof(IItem)), MapFor(provider, typeof(IItem)));
    Assert.IsFalse(none.Items.Any(c => c.ObjectKind == DbObjectKind.CheckConstraint), none.Report());

    // ----- the script drops before it adds --------------------------------------------------------------------------
    var script = new DbUpgradeScriptBuilder(provider).Build(changes);
    var statements = script.Statements.Select(s => s.Text).ToList();
    var drop = statements.FindIndex(s => s.Contains($"DROP CONSTRAINT \"{old.Name}\""));
    var add = statements.FindIndex(s =>
      s.Contains("ADD CONSTRAINT \"CK_Item_Stock_") && s.Contains("CHECK (\"Stock\" >= 0)"));
    Assert.IsTrue(drop >= 0 && add > drop, string.Join("\n", statements));
    Assert.IsFalse(statements.Any(s => s.Contains("Item_by_hand")));

    // ----- a server that takes a constraint only in CREATE TABLE is told what it missed ----------------------------
    var sqlite = new SqliteProvider();
    var withheld = new DbUpgradeScriptBuilder(sqlite).Build(
      sqlite.CreateDbModelComparer().Compare(MapFor(sqlite, typeof(IItem)), MapFor(sqlite, typeof(IItemChanged))));
    Assert.IsFalse(withheld.Statements.Any(s => s.Text.Contains("CHECK")));
    Assert.IsTrue(withheld.Warnings.Any(w => w.Contains("check constraint")), string.Join("\n", withheld.Warnings));
  }

  [TestMethod]
  public void Check_ReportsAnUnknownMemberAndANameDeclaredTwice() {
    var unknown = MapFor(new PostgresProvider(), typeof(IItemWithUnknownMember));
    StringAssert.Contains(unknown.BuildReport.Format(), "names '{Cost}' in its expression");

    var twice = NewApp(typeof(IItemDeclaredTwice));
    StringAssert.Contains(twice.Report(), "declares the check 'price' twice");
  }

  [Entity(Name = "Product")]
  private interface IProduct {
    [PrimaryKey] Guid Id { get; set; }
    [Json] Dictionary<string, int>? Spec { get; set; }
    [Json] string? Raw { get; set; }
    [Json, Column(DbTypeSpec = "nvarchar(400)")] string? Short { get; set; }
  }

  /// <summary>The check a developer could write by hand, in the words the ORM uses for its own.</summary>
  [Entity(Name = "Product")]
  [Check(DbServerKind.MsSql, "SpecIsJson", "ISJSON({Spec}) = 1")]
  private interface IProductDeclared {
    [PrimaryKey] Guid Id { get; set; }
    [Json] Dictionary<string, int>? Spec { get; set; }
  }

  // A JSON MEMBER HELD AS TEXT GETS A CHECK THAT THE TEXT IS JSON, since the server has no type to refuse other text
  // with: SQL Server before 2025, and SQLite. A server with a JSON type gets none.
  [TestMethod]
  public void JsonMember_HeldAsText_GetsACheckThatItIsJson() {
    static List<DbCheckConstraintInfo> GetChecks(DbProvider provider, Type type) {
      var model = MapFor(provider, type);
      Assert.IsFalse(model.BuildReport.HasErrors, model.BuildReport.Format());
      return [.. model.Tables.Single(t => t.Name.EndsWith("Product")).CheckConstraints];
    }

    // ----- text on SQL Server 2022: one check per member, and none for a member that names its own column type -----
    var checks = GetChecks(new SqlServerProvider(), typeof(IProduct));
    Assert.AreEqual(2, checks.Count, string.Join(", ", checks.Select(c => c.ToString())));
    var spec = checks.Single(c => c.Name.StartsWith("CK_Product_SpecIsJson_"));
    Assert.AreEqual("ISJSON([Spec]) = 1", spec.Expression);
    Assert.AreEqual("SpecIsJson", DbCheckConstraintInfo.GetCheckName(spec.Name, "Product"));
    Assert.IsTrue(checks.Any(c => c.Name.StartsWith("CK_Product_RawIsJson_")));

    // ----- text on SQLite --------------------------------------------------------------------------------------------
    var sqlite = GetChecks(new SqliteProvider(), typeof(IProductDeclared));
    Assert.AreEqual("json_valid(\"Spec\")", sqlite.Single().Expression);

    // ----- a JSON type refuses other text itself: PostgreSQL, and SQL Server from 2025 ---------------------------
    Assert.AreEqual(0, GetChecks(new PostgresProvider(), typeof(IProductDeclared)).Count);
    Assert.AreEqual(0, GetChecks(new SqlServerProvider(SqlServerVersion.Sql2025), typeof(IProduct)).Count);

    // ----- the same check declared by hand is the one check ---------------------------------------------------------
    Assert.AreEqual(spec.Name, GetChecks(new SqlServerProvider(), typeof(IProductDeclared)).Single().Name);
  }
}
