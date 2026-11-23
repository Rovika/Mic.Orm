using Mic.Common;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Db2;
using Mic.Orm.Db.MySql;
using Mic.Orm.Db.Oracle;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Db.SqlServer;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Extensions.SoftDelete;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using Mic.Orm.Services;

namespace Mic.Tests.Orm.Db;

// ONE MODEL FOR THE WHOLE DATA SUITE. Every entity any DML test needs, declared once and built once per vendor, rather
// than a model per aspect built per test. Constructing a model costs milliseconds; the SCHEMA it then needs costs
// seconds on the heavy vendors, and a suite that made one per test spent nearly all of its time there.
//
// It is a superset on purpose. A test that only wants a Person still gets Tickets and Tags in its database, which
// costs nothing to have and saves everything to share.
public static class Dml {
  /// <summary>A plain entity, and the reference every other test leans on.</summary>
  public interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    [Size(60)] string City { get; set; }

    /// <summary>A list with NO declared order, so it loads in primary-key order.</summary>
    IList<IPerson> Residents { get; }
  }

  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }

    /// <summary>A member declared as a QUERY, which owns no storage slot — placed SECOND, so every member after it has
    /// a position and a slot that differ, and every save, read and stub in this suite runs with the two apart.</summary>
    IQueryable<IPhone> PhoneQuery { get; }

    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
    IAddress? Address { get; set; }

    /// <summary>A SELF-REFERENCE, and the only reason the record level of the save sort ever runs: a type that points
    /// at itself is a component of one that IS a cycle, so its rows have to be ordered among themselves.</summary>
    IPerson? Manager { get; set; }

    /// <summary>A list with a declared order.</summary>
    [OrderBy("-Priority Number")] IList<IPhone> Phones { get; }

    /// <summary>A member a method computes on the client: no column, no setter.</summary>
    [Computed(nameof(PersonFunctions.GetDisplayName), typeof(PersonFunctions))]
    string DisplayName { get; }

    /// <summary>A computed member that keeps a column, written at every save, so a query can filter on it.</summary>
    [Computed(nameof(PersonFunctions.GetNameLength), typeof(PersonFunctions), Stored = true)]
    int NameLength { get; }
  }

  /// <summary>The rows of a table function over the people: those at or above a rank, keyed and so tracked.</summary>
  public interface IPersonAbove {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  /// <summary>The rows of a table function with no parameters and no key: how many people hold each rank.</summary>
  public interface IRankCount {
    int Rank { get; set; }
    long Count { get; set; }
  }

  /// <summary>A place on the map: a geographic point, required and optional.</summary>
  public interface IPlace {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
    [Index] GeoPoint Location { get; set; }
    GeoPoint? Pin { get; set; }
  }

  /// <summary>The functions behind the computed members.</summary>
  public static class PersonFunctions {
    public static string GetDisplayName(IPerson person) => $"{person.LastName} (#{person.Rank})";
    public static int GetNameLength(IPerson person) => person.LastName.Length;
  }

  /// <summary>The child of a one-to-many — what a list member holds.</summary>
  public interface IPhone {
    [PrimaryKey] Guid Id { get; set; }
    IPerson Person { get; set; }
    [Size(20)] string Number { get; set; }
    int Priority { get; set; }
  }

  // ----- a many-to-many, for the one case the save's phase order exists to survive -----------------------------------

  public interface IOwner {
    [PrimaryKey] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }

    /// <summary>A many-to-many list whose order is PERSISTED in the link, and loads by it.</summary>
    [ManyToMany(typeof(IOwnerTag)), PersistOrderIn("Position")] IList<ITag> Tags { get; }
  }

  public interface ITag {
    [PrimaryKey] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }
  }

  /// <summary>The LINK, and it is ISOLATED: it points at two entities and nothing points at it.</summary>
  /// <remarks>Which is what makes its rows movable to either end of a save — `SaveOrder` needs to put a delete and a
  /// re-insert of the same key at opposite ends.</remarks>
  public interface IOwnerTag {
    [PrimaryKey] Guid Id { get; set; }
    IOwner Owner { get; set; }
    ITag Tag { get; set; }
    int Position { get; set; }
  }

  public enum Grade { None = 0, Silver = 2, Gold = 7 }

  /// <summary>What a JSON column holds: a mutable object with a nested list, changed in place by the tests.</summary>
  public class EveryTypeSettings {
    public string Theme { get; set; } = "";
    public int PageSize { get; set; }
    public List<string> Tags { get; set; } = [];
  }

  /// <summary>A document of one of SEVERAL types. The serializer's own attribute serves the discriminator INSIDE the
  /// document (`Inline`); the one in another column is mapped on the application (`DmlApp`), which reads nothing off
  /// this type.</summary>
  [System.Text.Json.Serialization.JsonDerivedType(typeof(Circle), "circle")]
  [System.Text.Json.Serialization.JsonDerivedType(typeof(Square), "square")]
  public abstract class Shape {
    public string Color { get; set; } = "";
  }

  public sealed class Circle : Shape {
    public double Radius { get; set; }
  }

  public sealed class Square : Shape {
    public double Side { get; set; }
  }

  /// <summary>The discriminator column's type — an enum, mapped to the shapes by name on the application.</summary>
  public enum ShapeKind { None = 0, Circle = 1, Square = 2 }

  /// <summary>Two texts with their hashes beside them, one in an int and one in a long.</summary>
  public interface IBookmark {
    [PrimaryKey] Guid Id { get; set; }
    [Size(100)] string Name { get; set; }
    [HashFor(nameof(Name))] int NameHash { get; set; }
    [Size(400)] string Url { get; set; }
    [HashFor(nameof(Url)), Index] long UrlHash { get; set; }
  }

  /// <summary>An entity under the soft-delete extension: a delete stamps `DeletedOn` and the row is hidden.</summary>
  public interface IMemo : ISoftDeleteEntity {
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string Text { get; set; }
  }

  /// <summary>An entity under the multi-tenancy extension: a `TenantId` column found by name, read and written as the
  /// context's tenant's.</summary>
  public interface IJob {
    [PrimaryKey] Guid Id { get; set; }
    Guid TenantId { get; set; }
    [Size(80)] string Title { get; set; }
  }

  /// <summary>An entity on the shipped marker: bound through its `Tenant` reference, the condition and the stamp on
  /// the reference's key column.</summary>
  public interface IBooking : ITenantBound {
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string Title { get; set; }
  }

  /// <summary>A LEGACY table: the tenant is a company code in a column named for it, and no marker to inherit — what
  /// `CompanyTenancy` recognises by name.</summary>
  public interface ILegacyOrder {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Company { get; set; }
    [Size(80)] string Product { get; set; }
  }

  /// <summary>A LEGACY table soft-deleted by a bit, `IsDeleted`, and no marker — found by name, filtered as
  /// `= false`, and marked `true` because the member is a bool.</summary>
  public interface ILegacyNote {
    [PrimaryKey] Guid Id { get; set; }
    bool IsDeleted { get; set; }
    [Size(80)] string Text { get; set; }
  }

  /// <summary>Two JSON columns over the same base type: one told apart by the `Kind` column, one by a discriminator
  /// inside the document.</summary>
  public interface IDrawing {
    [PrimaryKey] Guid Id { get; set; }
    ShapeKind Kind { get; set; }
    [Json(Discriminator = "Kind")] Shape Shape { get; set; }
    [Json] Shape? Inline { get; set; }
  }

  /// <summary>Every type a value can be written into a statement AS.</summary>
  /// <remarks>THE MODEL BEHIND THE LITERAL ROUND TRIP. A multi-row insert writes simple values into the SQL rather
  /// than binding them, and a literal is spelled differently by every server — so the only thing that proves one is
  /// right is putting it in and reading it back, on each of them. Every member here is a type with its own spelling,
  /// and the two that are NOT are here to prove the fallback: a long string and a blob stay bound.</remarks>
  public interface IEveryType {
    [PrimaryKey] Guid Id { get; set; }
    [Size(80)] string Text { get; set; }
    [Size(80)] string? Missing { get; set; }
    bool Flag { get; set; }
    int Count { get; set; }
    long Big { get; set; }
    decimal Amount { get; set; }
    double Ratio { get; set; }
    Grade Grade { get; set; }
    DateTime Moment { get; set; }
    DateOnly Day { get; set; }

    /// <summary>Past `MaxLiteralLength`, so it must fall back to a parameter.</summary>
    [Unlimited] string Long { get; set; }

    /// <summary>A JSON column holding a typed object — the server's JSON type where it has one.</summary>
    [Json] EveryTypeSettings Settings { get; set; }

    /// <summary>A JSON column holding raw text, nullable.</summary>
    [Json] string? RawJson { get; set; }

    /// <summary>Binary, which falls back for the same reason at any real size.</summary>
    byte[]? Blob { get; set; }

    /// <summary>Binary that says its size: the server's bounded type where it has one.</summary>
    [Size(20)] byte[]? Token { get; set; }
  }

  // ----- a NATURAL COMPOSITE KEY, and a list whose owner two columns identify -------------------------------------------

  /// <summary>An entity keyed by two columns — what an imported schema is full of. Its list's owner is a PAIR, which
  /// no `IN` over one column can name; the smart load over it is the rows of a parameter.</summary>
  [PrimaryKey("Region,Code")]
  public interface IRoute {
    [Size(10)] string Region { get; set; }
    int Code { get; set; }
    [Size(40)] string Name { get; set; }

    [OrderBy("Position")] IList<IStop> Stops { get; }

    IQueryable<IStop> StopQuery { get; }
  }

  /// <summary>A child whose foreign key is two columns.</summary>
  public interface IStop {
    [PrimaryKey] Guid Id { get; set; }
    IRoute Route { get; set; }
    int Position { get; set; }
    [Size(40)] string Name { get; set; }
  }

  // ----- keys the SERVER makes, and keys MIC makes --------------------------------------------------------------------

  /// <summary>A key the server makes — the case Guid keys exist to avoid, and the one a ported model arrives with.
  /// </summary>
  /// <remarks>ONE Ticket, not two. The CRUD tests had a `long` one and the event tests an `int` one, both named
  /// Ticket, which two models could get away with and one cannot — and there was never a reason for the difference.
  /// </remarks>
  public interface ITicket {
    [PrimaryKey, Auto(AutoType.Identity)] long Id { get; set; }
    [Size(50)] string Title { get; set; }
  }

  /// <summary>A child of one — so a foreign key needs a value the server has not made yet.</summary>
  public interface IComment {
    [PrimaryKey, Auto(AutoType.Identity)] long Id { get; set; }
    [Size(100)] string Text { get; set; }
    ITicket Ticket { get; set; }
  }

  /// <summary>The key MIC promotes: made by the ORM, on the client, before anything is sent.</summary>
  public interface IInvoice {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(20)] string Number { get; set; }
  }

  /// <summary>A number the SERVER hands out from a sequence: drawn by the save and put on the record before its
  /// insert, so a bulk insert and a batch see it as any other value.</summary>
  public interface IReceipt {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Sequence("mic_receipt_numbers")] long Number { get; set; }
    [Size(20)] string Note { get; set; }
  }

  /// <summary>A row whose PRIMARY KEY is drawn from a sequence, with children referencing it: the key is on the parent
  /// before any statement is built, so a child's foreign key is copied from it like any key.</summary>
  public interface IShipment {
    [PrimaryKey, Sequence("mic_shipment_ids")] long Id { get; set; }
    [Size(20)] string Note { get; set; }
    IList<IShipmentItem> Items { get; }
  }

  public interface IShipmentItem {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    IShipment Shipment { get; set; }
    [Size(20)] string Sku { get; set; }
  }

  // ----- a column the database computes out of a JSON document -----------------------------------------------------

  /// <summary>A document whose discriminator the serializer writes under its own name, <c>kind</c>.</summary>
  [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
  [System.Text.Json.Serialization.JsonDerivedType(typeof(Arrow), "arrow")]
  [System.Text.Json.Serialization.JsonDerivedType(typeof(Label), "label")]
  public abstract class Marker {
    public int X { get; set; }
  }

  public sealed class Arrow : Marker {
    public int Length { get; set; }
  }

  public sealed class Label : Marker {
    public string Text { get; set; } = "";
  }

  /// <summary>An entity whose document's kind is a column the database computes, indexed, and the document's
  /// discriminator; and a number out of the document as a second computed column.</summary>
  public interface IMarked {
    [PrimaryKey] Guid Id { get; set; }
    [Json(Discriminator = "Kind")] Marker Marker { get; set; }
    [FromJson("Marker", "$.kind"), Size(20), Index] string Kind { get; }
    [FromJson("Marker", "$.X")] int X { get; }
  }

  /// <summary>An entity with a column the database computes from a SQL expression, one text serving every server
  /// through member placeholders.</summary>
  public interface ILineItem {
    [PrimaryKey] Guid Id { get; set; }
    int Price { get; set; }
    int Qty { get; set; }
    [DbComputed]
    [SqlExpression(DbServerKind.Postgres, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.MySql, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.MsSql, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.Oracle, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.SQLite, "{Price} * {Qty}")]
    [SqlExpression(DbServerKind.Db2, "{Price} * {Qty}")]
    int Total { get; }
  }

  /// <summary>A TEMP TABLE: the ids a session stages to join against, filled from objects or from queries, gone with
  /// the session. No key: its rows are read untracked.</summary>
  public interface ITempPersonIds {
    Guid Id { get; set; }
  }

  // ----- views: entities over rows the server derives, read-only ---------------------------------------------------

  /// <summary>A view DEFINED BY A TYPED QUERY: each address with how many people live there.</summary>
  public interface IAddressView {
    [PrimaryKey] Guid Id { get; set; }
    [Size(60)] string City { get; set; }
    int ResidentCount { get; set; }
  }

  /// <summary>A view DEFINED BY SQL, one text per server: a person's name.</summary>
  public interface IPersonName {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>A view WITHOUT A KEY: a person's name and rank, with no column that tells two rows apart.</summary>
  public interface INameRank {
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  /// <summary>A document spread over two tables, with a consistency rule between them: the header's total is the sum
  /// of its lines' values. The shape row locks exist for — a writer holds the header, and nobody else edits the
  /// document until it is saved.</summary>
  public interface IDocument {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    // Indexed: the queue test orders by it under a lock, and a locked sort over an unindexed column holds every row
    // it scanned on MySQL and SQL Server, not only the one returned.
    [Size(20), Index] string Name { get; set; }
    int Total { get; set; }
    // Counted by the ORM: an update matches on it and counts it up, so a stale save is refused.
    [RowVersion] int Version { get; set; }

    [OrderBy("Name")] IList<IDocLine> Lines { get; }
  }

  /// <summary>What an editor keeps of an article, held as a JSON document.</summary>
  public class ArticleNotes {
    public string Editor { get; set; } = "";
    public int Revision { get; set; }
  }

  /// <summary>An article with three heavy members, left out of a read of the entity: its text, which is required,
  /// its summary, which is not, and its notes, a JSON document.</summary>
  public interface IArticle {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(60)] string Title { get; set; }
    int Rank { get; set; }
    [OnDemand, Unlimited] string Body { get; set; }
    [OnDemand, Size(200)] string? Summary { get; set; }
    [OnDemand, Json] ArticleNotes? Notes { get; set; }
    [OnDemand] byte[]? Picture { get; set; }

    /// <summary>The article that continues this one: a reference to follow to the other's text.</summary>
    IArticle? Sequel { get; set; }
  }

  public interface IDocLine {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    IDocument Document { get; set; }
    [Size(10)] string Name { get; set; }
    int Value { get; set; }
  }

  /// <summary>The other four the ORM produces on the client — who and when, at both ends of a row's life.</summary>
  public interface IAuditedRow {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(20)] string Note { get; set; }
    [Auto(AutoType.CreatedOn)] DateTime CreatedOn { get; set; }
    [Auto(AutoType.UpdatedOn)] DateTime UpdatedOn { get; set; }
    [Auto(AutoType.CreatedBy), Size(50)] string CreatedBy { get; set; }
    [Auto(AutoType.UpdatedById)] Guid UpdatedById { get; set; }
  }

  // ----- what the connection and failure tests provoke ------------------------------------------------------------

  public interface IWidget {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
  }

  /// <summary>A unique key to violate, and a parent to point a bad foreign key at.</summary>
  public interface IParent {
    [PrimaryKey] Guid Id { get; set; }
    [Unique][Size(30)] string Code { get; set; }
  }

  public interface IChild {
    [PrimaryKey] Guid Id { get; set; }
    IParent Parent { get; set; }
    [Size(20)] string Label { get; set; }
  }

  /// <summary>A position no two rows of one parent share: a unique key over a reference and a number.</summary>
  [Unique("Parent,Position")]
  public interface ISlot {
    [PrimaryKey] Guid Id { get; set; }
    [CascadeDelete] IParent Parent { get; set; }
    int Position { get; set; }
  }

  /// <summary>A child that GOES WITH its parent — so it does not stand in the way of deleting one.</summary>
  public interface INote {
    [PrimaryKey] Guid Id { get; set; }
    [CascadeDelete] IParent Parent { get; set; }
    [Size(20)] string Text { get; set; }
  }

  /// <summary>A CACHED entity — small and static, held whole in memory — and a TENANT's, so its blocks are one per
  /// tenant: for the test of what a write does to its block, and does not do to another tenant's.</summary>
  public interface IColor {
    [PrimaryKey] Guid Id { get; set; }
    Guid TenantId { get; set; }
    [Size(30)] string Name { get; set; }
  }
}

/// <summary>The DML suite's application — written the way an application is MEANT to be written.</summary>
/// <remarks>A subclass that does its composition in its own constructor, which is the arrangement MIC is designed
/// around and which the tests did not use anywhere: 61 sites built an `EntityApp` by hand instead, testing the ORM
/// through a door no application goes through.</remarks>
public class DmlApp : EntityApp {
  /// <summary>The select of the SQL-defined view, written for every server MIC knows over the names this schema's
  /// tables have: the dialects spell the quoting, since a hand-written text is the module's to get right.</summary>
  private static Dictionary<DbServerKind, string> BuildPersonNameSql(string schema) {
    var dialects = new (DbServerKind Kind, SqlDialect Dialect)[] {
      (DbServerKind.Postgres, new PostgresSqlDialect()), (DbServerKind.MySql, new MySqlSqlDialect()),
      (DbServerKind.MsSql, new SqlServerSqlDialect()), (DbServerKind.Oracle, new OracleSqlDialect()),
      (DbServerKind.SQLite, new SqliteSqlDialect()), (DbServerKind.Db2, new Db2SqlDialect()),
    };
    var byServer = new Dictionary<DbServerKind, string>();
    foreach (var (kind, dialect) in dialects)
      byServer[kind] = $"SELECT {dialect.Quote("Id")}, {dialect.Quote("LastName")} FROM {dialect.QualifiedName(schema, "Person")}";
    return byServer;
  }

  /// <summary>The dialects a function's body is written for: the four servers with table-valued functions.
  /// </summary>
  private static readonly (DbServerKind Kind, SqlDialect Dialect)[] FunctionDialects = [
    (DbServerKind.Postgres, new PostgresSqlDialect()), (DbServerKind.MsSql, new SqlServerSqlDialect()),
    (DbServerKind.Oracle, new OracleSqlDialect()), (DbServerKind.Db2, new Db2SqlDialect()),
  ];

  /// <summary>The body of the function of the people at or above a rank, the parameter spelled as each server reads
  /// one inside a body.</summary>
  private static Dictionary<DbServerKind, string> BuildPersonAboveSql(string schema) {
    var byServer = new Dictionary<DbServerKind, string>();
    foreach (var (kind, dialect) in FunctionDialects) {
      var parameter = kind == DbServerKind.MsSql ? "@min_rank" : "min_rank";
      byServer[kind] = $"SELECT {dialect.Quote("Id")}, {dialect.Quote("LastName")}, {dialect.Quote("Rank")} "
        + $"FROM {dialect.QualifiedName(schema, "Person")} WHERE {dialect.Quote("Rank")} >= {parameter}";
    }
    return byServer;
  }

  /// <summary>The body of the function counting the people of each rank.</summary>
  private static Dictionary<DbServerKind, string> BuildRankCountsSql(string schema) {
    var byServer = new Dictionary<DbServerKind, string>();
    foreach (var (kind, dialect) in FunctionDialects)
      byServer[kind] = $"SELECT {dialect.Quote("Rank")}, COUNT(*) AS {dialect.Quote("Count")} "
        + $"FROM {dialect.QualifiedName(schema, "Person")} GROUP BY {dialect.Quote("Rank")}";
    return byServer;
  }

  /// <summary>How long a block of colors is trusted before a read refills it.</summary>
  public static readonly TimeSpan ColorCacheExpiration = TimeSpan.FromMinutes(30);

  /// <summary>The tenancy over a `TenantId` column found by name, a Guid, under the default context key.</summary>
  public MultiTenantExtension Tenancy { get; } = new("TenantId");

  /// <summary>The tenancy over the shipped marker, `IBooking.Tenant`, under a context key of its own, so that a Guid
  /// set for the other does not reach a short column.</summary>
  public MultiTenantExtension BoundTenancy { get; } = new() { TenantIdKey = "Tenant" };

  /// <summary>The legacy soft delete beside the stamped one: an `IsDeleted` bit found by name, over a different
  /// entity.</summary>
  public SoftDeleteExtension LegacySoftDelete { get; } = new("IsDeleted");

  /// <summary>The legacy company tenancy beside them: a string code in a column named `Company`, found by name, the
  /// context naming it under `Company` too.</summary>
  public MultiTenantExtension LegacyTenancy { get; } = new("Company") { TenantIdKey = "Company" };

  public DmlApp(string schema)
    : base("DmlTests", new Version(1, 0), new EntityAppSettings { DefaultSchema = schema }) {
    var module = new EntityModule("Dml", new Version(1, 0));
    module.RegisterEntities(
      typeof(Dml.IAddress), typeof(Dml.IPerson), typeof(Dml.IPhone),
      typeof(Dml.IOwner), typeof(Dml.ITag), typeof(Dml.IOwnerTag),
      typeof(Dml.ITicket), typeof(Dml.IComment), typeof(Dml.IInvoice), typeof(Dml.IAuditedRow),
      typeof(Dml.IWidget), typeof(Dml.IParent), typeof(Dml.IChild), typeof(Dml.INote), typeof(Dml.ISlot),
      typeof(Dml.IEveryType), typeof(Dml.IRoute), typeof(Dml.IStop), typeof(Dml.IDrawing), typeof(Dml.IMemo),
      typeof(Dml.IJob), typeof(Dml.ILegacyOrder), typeof(Dml.ILegacyNote), typeof(Dml.IColor),
      typeof(ITenant), typeof(Dml.IBooking),
      typeof(Dml.IDocument), typeof(Dml.IDocLine), typeof(Dml.IReceipt), typeof(Dml.IShipment), typeof(Dml.IShipmentItem),
      typeof(Dml.IMarked), typeof(Dml.ILineItem), typeof(Dml.IPlace), typeof(Dml.IPersonAbove), typeof(Dml.IRankCount),
      typeof(Dml.IBookmark), typeof(Dml.IArticle));
    // The discriminators by NAME, for a column that is an enum: the builder converts them to it.
    module.MapJsonTypes<Dml.Shape>(("Circle", typeof(Dml.Circle)), ("Square", typeof(Dml.Square)));
    // The sequence `IReceipt.Number` draws from, declared with a start and a step the test can see.
    module.RegisterSequence("mic_receipt_numbers", start: 1000, increment: 10);
    // The sequence `IShipment.Id`, a primary key, draws from.
    module.RegisterSequence("mic_shipment_ids", start: 1, increment: 1);
    // THE VIEWS: one defined by a typed query over the sets `ViewHelper` gives, one by SQL written per server.
    module.RegisterView<Dml.IAddressView>(
      from a in ViewHelper.EntitySet<Dml.IAddress>()
      select new { a.Id, a.City, ResidentCount = a.Residents.Count() });
    module.RegisterView<Dml.IPersonName>(BuildPersonNameSql(schema));
    module.RegisterView<Dml.INameRank>(
      from p in ViewHelper.EntitySet<Dml.IPerson>() select new { p.LastName, p.Rank });
    // THE TEMP TABLE: declared like a view, created by a session when it fills one.
    module.RegisterTempTable<Dml.ITempPersonIds>();
    // THE TABLE FUNCTIONS: one with a parameter and keyed rows, one with neither, both defined by SQL per server.
    module.RegisterTableFunction<Dml.IPersonAbove>("PersonAbove", BuildPersonAboveSql(schema), ("min_rank", typeof(int)));
    module.RegisterTableFunction<Dml.IRankCount>("RankCounts", BuildRankCountsSql(schema));
    AddModule(module);
    // COLOR IS CACHED, with an expiration, for the test of a write reaching its block — and for every other test, which
    // must still read what the database holds. THE CLOCK CAN BE MOVED, for the expiration to be tested without waiting.
    SetCached(CacheType.Server, [typeof(Dml.IColor)], ColorCacheExpiration);
    RegisterService<ITimeService>(new AdjustableTimeService(this));
    // The extensions apply to the entities carrying their markers — `IMemo`, `IJob` — and to nothing else.
    AddExtension(new SoftDeleteExtension());
    AddExtension(Tenancy);
    AddExtension(BoundTenancy);
    AddExtension(LegacyTenancy);
    AddExtension(LegacySoftDelete);
  }
}
