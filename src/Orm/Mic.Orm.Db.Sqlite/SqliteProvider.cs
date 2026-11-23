using System.Data.Common;
using Mic.Common;
using Mic.Linq;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
using Microsoft.Data.Sqlite;

namespace Mic.Orm.Db.Sqlite;

/// <summary>Provides the SQLite database provider: its feature set, its SQL dialect, its type registry and the
/// Microsoft.Data.Sqlite connections and commands.</summary>
/// <remarks>The type registry is a <see cref="SqliteTypeRegistry"/>, since SQLite marks an identity column by its
/// type name rather than by a keyword.</remarks>
public class SqliteProvider : DbProvider {
  /// <summary>Specifies the name of the function the provider gives every connection it creates: the distance in
  /// metres between two points held as well-known binary.</summary>
  public const string GeoDistanceFunction = "mic_geo_distance";

  public SqliteProvider(GuidScheme guidScheme = GuidScheme.V7)
    : base(DbServerKind.SQLite, new SqliteSqlDialect(), BuildTypeRegistry(), guidScheme) { }

  /// <summary>Creates a connection that enforces foreign keys and can measure the distance between two points.
  /// </summary>
  /// <remarks><para>SQLite enforces foreign keys per connection, and off by default; the library turns them on at
  /// every open when the connection string says <c>Foreign Keys=True</c>, so the provider says it where the string
  /// does not. A string that says <c>False</c> is left as written.</para>
  /// <para>SQLite has no spatial type and no function over a point's bytes, so the connection is given
  /// <see cref="GeoDistanceFunction"/>, which runs <see cref="GeoFunctions.Distance"/>. The function exists on
  /// connections this method creates and on no other: a tool that opens the same file cannot call it.</para>
  /// </remarks>
  /// <param name="connectionString">The connection string.</param>
  public override DbConnection CreateConnection(string connectionString) {
    var builder = new SqliteConnectionStringBuilder(connectionString);
    builder.ForeignKeys ??= true;
    var connection = new SqliteConnection(builder.ConnectionString);
    connection.CreateFunction<byte[]?, byte[]?, double?>(GeoDistanceFunction, GetGeoDistance, isDeterministic: true);
    return connection;
  }

  private static double? GetGeoDistance(byte[]? point, byte[]? other) =>
    point == null || other == null ? null : GeoFunctions.Distance(GeoPoint.FromWkb(point), GeoPoint.FromWkb(other));

  public override DbCommand CreateDbCommand() => new SqliteCommand();

  /// <summary>Gets the features SQLite supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.Schemas"/>, since a database is one namespace in a file;
  /// <see cref="DbFeatures.AlterColumn"/>, since a type or a nullability changes only by rebuilding the table;
  /// <see cref="DbFeatures.AddConstraintsAfterCreate"/>, since a primary or foreign key is part of
  /// <c>CREATE TABLE</c> or does not exist; <see cref="DbFeatures.Sequences"/>, since a rowid serves;
  /// <see cref="DbFeatures.SessionLocks"/>, so coordination falls back to a warning; and
  /// <see cref="DbFeatures.IncludeColumnsInIndexes"/>, <see cref="DbFeatures.ClusteredStorage"/>,
  /// <see cref="DbFeatures.ClusteringMarker"/> and <see cref="DbFeatures.DeferredConstraintCheck"/>.</remarks>
  public override DbFeatures Features =>
    DbFeatures.ReferentialConstraints
    | DbFeatures.HeapTables
    | DbFeatures.JsonRows
    | DbFeatures.OrderedColumnsInIndexes
    | DbFeatures.FilterInIndexes              // partial indexes, since 3.8
    | DbFeatures.Views
    | DbFeatures.DropColumn                   // since 3.35
    | DbFeatures.ServerPreservesComments      // keeps the CREATE text verbatim
    | DbFeatures.BinaryGuids                  // a blob, since there is no uuid type
    | DbFeatures.IdentityColumns              // INTEGER PRIMARY KEY
    | DbFeatures.BulkInsertAdvancesIdentity
    | DbFeatures.FullText                     // an FTS5 table beside the entity's, joined by the key
    | DbFeatures.IdentityIsImpliedSqlite;     // a plain int key is declared the same way and reads back as one

  /// <summary>Gets the case the server folds an unquoted identifier to; SQLite stores an identifier as written.
  /// </summary>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Preserve;

  public override string DefaultSchema => string.Empty;

  /// <summary>Gets the longest identifier name the server accepts.</summary>
  /// <remarks>SQLite's own limit is far past anything a name generator reaches, so the value stands for no limit.
  /// </remarks>
  public override int MaxNameLength => 1000;

  /// <summary>Gets the largest number of parameters one statement may bind.</summary>
  /// <remarks>SQLite's limit is 32766 since 3.32 and 999 before it. The lower one is respected, since the library MIC
  /// binds to is whichever one the host ships.</remarks>
  public override int MaxParameters => 999;

  /// <summary>Gets the name every primary key gets in the model, which is the name the model loader reports.</summary>
  /// <remarks>SQLite keeps no name for a primary key, so the model must use the one introspection reports or the
  /// comparer, which matches by name, reports a drop and an add on every startup. A constant is safe because keys
  /// are compared within their table.</remarks>
  public override string? PrimaryKeyName => "PK";

  /// <summary>Gets the index types SQLite offers beyond the ordinary index.</summary>
  /// <remarks>A full-text index is not an index but an FTS5 table of its own, kept in step by triggers; see
  /// <see cref="SqliteSqlDialect.BuildFullTextIndex"/>.</remarks>
  public override IReadOnlyList<DbIndexType> IndexTypes => [DbIndexType.FullText];

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new SqliteModelLoader(settings);

  /// <summary>Classifies a failure from SQLite's error codes, which replace the SQLSTATE the server does not report.
  /// </summary>
  /// <remarks>The extended code distinguishes a unique violation from a foreign-key one; the primary code says only
  /// that a constraint failed.</remarks>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    if (inner is not SqliteException sqlite)
      return;
    failure.ProviderErrorCode = sqlite.SqliteExtendedErrorCode.ToString();
    failure.SubType = sqlite.SqliteExtendedErrorCode switch {
      2067 or 1555 => DataAccessException.SubTypeUniqueViolation,       // UNIQUE, PRIMARYKEY
      787 => DataAccessException.SubTypeIntegrityViolation,             // FOREIGNKEY
      1299 => DataAccessException.SubTypeNotNullViolation,              // NOTNULL
      275 => DataAccessException.SubTypeCheckViolation,                 // CHECK
      _ => sqlite.SqliteErrorCode switch {
        19 => DataAccessException.SubTypeIntegrityViolation,            // CONSTRAINT, kind unstated
        1 => DataAccessException.SubTypeSyntaxOrMissingObject,          // ERROR: bad SQL, or no such table
        5 or 6 => DataAccessException.SubTypeDeadlock,                  // BUSY, LOCKED, the retryable ones
        14 or 26 => DataAccessException.SubTypeConnectionFailed,        // CANTOPEN, NOTADB
        23 => DataAccessException.SubTypeAccessDenied,                  // AUTH
        _ => null,
      },
    };
  }

  /// <summary>Builds the type registry, a <see cref="SqliteTypeRegistry"/>.</summary>
  private static DbTypeRegistry BuildTypeRegistry() {
    var registry = new SqliteTypeRegistry();
    // SQLite accepts any type name, maps it to one of five storage classes, and reports the name back exactly as
    // written through pragma table_info, so a declared size SQLite itself ignores survives the round trip.
    registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize,
      aliases: "character varying, nvarchar, character", specialType: DbSpecialType.String);
    registry.Add("text", typeof(string), DbTypeFlags.Unlimited, aliases: "clob", specialType: DbSpecialType.StringUnlimited);

    registry.Add("integer", typeof(int), aliases: "int, int4, mediumint");
    registry.Add("bigint", typeof(long), aliases: "int8");
    registry.Add("smallint", typeof(short), aliases: "int2");
    registry.Add("numeric", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "decimal", defaultPrecision: 18, defaultScale: 4);
    registry.Add("real", typeof(double), aliases: "double, double precision, float");
    registry.Add("single", typeof(float), aliases: "float4");

    // No boolean and no date type: SQLite stores an integer and a string. The declared names are kept for the round
    // trip, and columnInit is what a backfill writes.
    registry.Add("boolean", typeof(bool), aliases: "bool, bit", columnInit: "0");
    // A Guid is 16 bytes, and the declared type stays uuid: a declared type is an affinity, not a storage class, and
    // a BLOB value keeps its storage class under every affinity.
    registry.Add("uuid", typeof(Guid), aliases: "guid, uniqueidentifier",
      columnInit: "x'00000000000000000000000000000000'");
    registry.Add("datetime", typeof(DateTime), aliases: "timestamp");
    registry.Add("datetimeoffset", typeof(DateTimeOffset));
    registry.Add("date", typeof(DateOnly));
    registry.Add("time", typeof(TimeOnly));
    registry.Add("interval", typeof(TimeSpan));

    registry.Add("blob", typeof(byte[]), DbTypeFlags.Unlimited,
      aliases: "binary, varbinary", specialType: DbSpecialType.BinaryUnlimited);

    // No spatial type without SpatiaLite: a point is its well-known binary, stored and read as it is.
    registry.Prefer(typeof(GeoPoint), "blob");
    return registry;
  }
}

/// <summary>Provides the SQLite type registry, which maps every identity column to <c>INTEGER</c>.</summary>
/// <remarks>SQLite has no identity keyword: a single-column primary key whose declared type is exactly <c>INTEGER</c>
/// is an alias for the table's rowid, assigned on insert. The rule is textual, so <c>BIGINT</c> and <c>INT</c> do not
/// alias the rowid and SQLite reports no error for them; a <c>long</c> identity therefore maps to <c>integer</c> here
/// rather than <c>bigint</c>. A rowid is 64-bit, so an <c>int</c> identity is narrowed on the way back.</remarks>
public class SqliteTypeRegistry : DbTypeRegistry {
  public override DbTypeInfo? MapMember(EntityMemberInfo member, out string? error) {
    if (member.AutoValue != AutoType.Identity)
      return base.MapMember(member, out error);
    error = null;
    return Apply(FindByName("integer")!, size: 0, precision: 0, scale: 0);
  }
}
