using Mic.Orm;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Db.SqlServer;

namespace Mic.Tests.Orm;

// The type registry — the answer to the one question the model layer could avoid: what does this become in a database?
// Exercised through the PostgreSQL provider, because a registry with no types in it proves nothing.
[TestClass]
public class DbTypeRegistryTests {
  private static readonly DbTypeRegistry Registry = new PostgresProvider().TypeRegistry;

  // ----- forward: CLR type → database type ---------------------------------------------------------------------------

  [TestMethod]
  public void Type_FormattedWithOrWithoutArguments() {
    Assert.AreEqual("uuid", Registry.Apply(Registry.FindByName("uuid")!).TypeSpec);
    Assert.AreEqual("integer", Registry.Apply(Registry.FindByName("integer")!).TypeSpec);
    Assert.AreEqual("boolean", Registry.Apply(Registry.FindByName("boolean")!).TypeSpec);

    Assert.AreEqual("varchar(50)", Registry.Apply(Registry.FindByName("varchar")!, size: 50).TypeSpec);
    Assert.AreEqual("numeric(18,4)",
      Registry.Apply(Registry.FindByName("numeric")!, precision: 18, scale: 4).TypeSpec);

    // numeric was registered with 18,4 — so a member that says nothing still gets a sane column.
    Assert.AreEqual("numeric(18,4)", Registry.Apply(Registry.FindByName("numeric")!).TypeSpec);

    // …and one that takes none is never given any.
    Assert.AreEqual("uuid", Registry.Apply(Registry.FindByName("uuid")!, size: 50).TypeSpec);
  }

  // ----- backward: database type name → type def ----------------------------------------------------------------------

  [TestMethod]
  public void Name_ResolvesThroughAliases() {
    // What makes introspection work at all: a server does not report the name you registered.
    Assert.AreSame(Registry.FindByName("varchar"), Registry.FindByName("character varying"));
    Assert.AreSame(Registry.FindByName("integer"), Registry.FindByName("int4"));
    Assert.AreSame(Registry.FindByName("bigint"), Registry.FindByName("int8"));
    Assert.AreSame(Registry.FindByName("boolean"), Registry.FindByName("bool"));

    // …however it was spelled.
    Assert.IsNotNull(Registry.FindByName("VARCHAR"));
    Assert.IsNotNull(Registry.FindByName("  Character Varying "));
    Assert.IsNull(Registry.FindByName("nvarchar"));      // a SQL Server type; not this provider's

    // And several database types read back as ONE CLR type — varchar, char and text are all `string` — which is
    // exactly why the two directions are separate maps. Collapsing them would make one of these unreachable.
    foreach (var name in new[] { "varchar", "char", "text" })
      Assert.AreEqual(typeof(string), Registry.FindByName(name)!.ClrType, name);
  }

  // ----- specs: parsed and VALIDATED -----------------------------------------------------------------------------------

  [TestMethod]
  public void Spec_ParsedKeptVerbatimAndValidated() {
    // ----- parsed into a type and its arguments ---------------------------------------------------------------------
    var sized = Registry.ParseSpec("varchar(80)", out var error);
    Assert.IsNull(error);
    Assert.AreEqual("varchar", sized!.TypeDef.Name);
    Assert.AreEqual(80, sized.Size);
    Assert.AreEqual("varchar(80)", sized.TypeSpec);

    var scaled = Registry.ParseSpec("numeric(12,3)", out error);
    Assert.IsNull(error);
    Assert.AreEqual((byte)12, scaled!.Precision);
    Assert.AreEqual((byte)3, scaled.Scale);

    // ----- and kept EXACTLY as written -------------------------------------------------------------------------------
    //
    // Rewriting it would be the round trip losing something for no reason — this is what the developer, or the
    // database, actually said.
    Assert.AreEqual("character varying(40)", Registry.ParseSpec("character varying(40)", out _)!.TypeSpec);

    // ----- what is not known is REPORTED rather than guessed at -------------------------------------------------------
    //
    // A mistake only the author can fix, and only now.
    Assert.IsNull(Registry.ParseSpec("nvarchar(50)", out error));
    Assert.IsTrue(error!.Contains("not known"));

    Assert.IsNull(Registry.ParseSpec("varchar(50", out error));
    Assert.IsTrue(error!.Contains("malformed"));

    // ----- while an EXOTIC type is recognised once a provider registers it ---------------------------------------------
    //
    // The DbTypeSpec case in full: a type the mapping would never choose, named outright and validated — registered
    // on the provider's own registry, which is what a PostGIS-aware provider would do in its constructor.
    var postGis = new PostgresProvider();
    postGis.TypeRegistry.Add("geography", typeof(byte[]));
    var geography = postGis.TypeRegistry.ParseSpec("geography", out error);
    Assert.IsNull(error);
    Assert.AreEqual("geography", geography!.TypeSpec);
  }

  // ----- mapping a member ----------------------------------------------------------------------------------------------

  private interface IWidget {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    [Unlimited] string Notes { get; set; }
    decimal Price { get; set; }
    [Column(Precision = 9, Scale = 2)] decimal Discount { get; set; }
    int Count { get; set; }
    Colour Shade { get; set; }
    byte[] Blob { get; set; }
    [Column(DbTypeSpec = "char(2)")] string Country { get; set; }
    [Column(DbTypeSpec = "nvarchar(10)")] string Wrong { get; set; }
    int[] Counts { get; set; }
    List<string>? Tags { get; set; }
    [Json] List<int> Document { get; set; }
  }

  private enum Colour { Red, Green }

  private static readonly Mic.Orm.Model.EntityTypeInfo Widget = BuildWidget();

  private static Mic.Orm.Model.EntityTypeInfo BuildWidget() {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IWidget));
    app.AddModule(module, "s");
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    return app.Model!.FindEntity("Widget")!;
  }

  private static string Spec(string member) {
    var info = Registry.MapMember(Widget.GetMember(member), out var error);
    Assert.IsNull(error, error);
    return info!.TypeSpec;
  }

  // ----- ONE entity, every way a member reaches a column type ----------------------------------------------------------
  //
  // Seven methods, each of which built the entity again to map one member of it.
  [TestMethod]
  public void Member_MapsThroughFacetsOrSaysWhyNot() {
    Assert.AreEqual("uuid", Spec("Id"));
    Assert.AreEqual("varchar(50)", Spec("Name"));        // the application's default size, applied by the builder
    Assert.AreEqual("numeric(18,4)", Spec("Price"));
    Assert.AreEqual("numeric(9,2)", Spec("Discount"));
    Assert.AreEqual("integer", Spec("Count"));

    // ----- [Unlimited] picks the UNBOUNDED type, not a bigger bounded one --------------------------------------------
    //
    // The semantic layer at work: the base asks for *the unlimited string type* and never spells `text` itself.
    Assert.AreEqual("text", Spec("Notes"));

    // ----- and a vendor need not have BOTH forms ---------------------------------------------------------------------
    //
    // PostgreSQL's only binary type is `bytea`, which is unbounded — so an ordinary byte[] has nowhere else to go.
    // Falling back to the other form is what keeps that from being a special case in every provider lacking a pair.
    Assert.AreEqual("bytea", Spec("Blob"));

    // ----- an enum maps as its underlying integer --------------------------------------------------------------------
    Assert.AreEqual("integer", Spec("Shade"));

    // ----- DbTypeSpec overrides the mapping entirely -----------------------------------------------------------------
    Assert.AreEqual("char(2)", Spec("Country"));

    // ----- an unrecognised one is reported AGAINST THE MEMBER ---------------------------------------------------------
    Assert.IsNull(Registry.MapMember(Widget.GetMember("Wrong"), out var error));
    Assert.IsTrue(error!.Contains("nvarchar"));

    // ----- as is a CLR type the registry has nothing for ---------------------------------------------------------------
    var empty = new DbTypeRegistry();                  // nothing registered at all
    Assert.IsNull(empty.MapMember(Widget.GetMember("Id"), out error));
    Assert.IsTrue(error!.Contains("no database type"));
  }

  // An array or a List of scalars is an array column on PostgreSQL. Another server has no type for it and the error
  // says what to do there. The same type under [Json] stays a document.
  [TestMethod]
  public void ArrayMember_IsAnArrayTypeOnPostgresAndAnErrorElsewhere() {
    Assert.AreEqual(typeof(int), Widget.GetMember("Counts").ArrayItemType);
    Assert.AreEqual(typeof(string), Widget.GetMember("Tags").ArrayItemType);
    Assert.IsNull(Widget.GetMember("Document").ArrayItemType);
    Assert.IsNull(Widget.GetMember("Blob").ArrayItemType);

    Assert.AreEqual("integer[]", Spec("Counts"));
    Assert.AreEqual("text[]", Spec("Tags"));
    Assert.AreEqual("jsonb", Spec("Document"));
    // The catalog's name for the type reads back as the same one.
    Assert.AreEqual("integer[]", Registry.FindByName("_int4")!.Name);

    Assert.IsNull(new SqliteProvider().TypeRegistry.MapMember(Widget.GetMember("Counts"), out var error));
    StringAssert.Contains(error, "[Json]");
    Assert.IsNull(new SqlServerProvider().TypeRegistry.MapMember(Widget.GetMember("Tags"), out error));
    StringAssert.Contains(error, "array type");
  }

  // A binary that says its size takes the server's bounded type where it has one; with no size, or marked unlimited,
  // it takes the unlimited one, since a bounded binary type with no size is one byte or no statement at all.
  [TestMethod]
  public void SizedBinary_TakesTheBoundedTypeWhereTheServerHasOne() {
    var sqlServer = new SqlServerProvider().TypeRegistry;
    Assert.AreEqual("varbinary(32)", sqlServer.MapType(typeof(byte[]), size: 32)!.TypeSpec);
    Assert.AreEqual("varbinary(max)", sqlServer.MapType(typeof(byte[]), size: 0)!.TypeSpec);
    Assert.AreEqual("varbinary(max)", sqlServer.MapType(typeof(byte[]), size: 32, unlimited: true)!.TypeSpec);
    Assert.AreEqual("varbinary(max)", sqlServer.MapType(typeof(byte[]))!.TypeSpec);

    // PostgreSQL and SQLite have one binary type, which takes no size.
    Assert.AreEqual("bytea", Registry.MapType(typeof(byte[]), size: 32)!.TypeSpec);
    Assert.AreEqual("blob", new SqliteProvider().TypeRegistry.MapType(typeof(byte[]), size: 32)!.TypeSpec);
  }
}
