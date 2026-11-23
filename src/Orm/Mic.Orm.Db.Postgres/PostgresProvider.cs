using System.Data.Common;
using Mic.Common;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Npgsql;

namespace Mic.Orm.Db.Postgres;

/// <summary>Provides the PostgreSQL database provider: its feature set, its SQL dialect, its type registry and the
/// Npgsql connections and commands.</summary>
/// <remarks>Npgsql is referenced only in this assembly; the rest of the framework sees <c>System.Data.Common</c>
/// objects.</remarks>
public class PostgresProvider : DbProvider {
  public PostgresProvider(GuidScheme guidScheme = GuidScheme.V7)
    : base(DbServerKind.Postgres, new PostgresSqlDialect(), BuildTypeRegistry(), guidScheme) { }

  public override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

  public override DbCommand CreateDbCommand() => new NpgsqlCommand();

  /// <summary>Sets a parameter's value, binding JSON text as <c>jsonb</c>.</summary>
  /// <remarks>Npgsql sends an untyped string as <c>text</c>, which a <c>jsonb</c> column refuses.</remarks>
  /// <param name="parameter">The parameter to set.</param>
  /// <param name="value">The value to bind.</param>
  protected override void SetParameterValue(DbParameter parameter, object value) {
    if (value is JsonText json) {
      ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
      parameter.Value = json.Text;
    } else
      base.SetParameterValue(parameter, value);
  }

  /// <summary>Gets the features PostgreSQL supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.ClusteredStorage"/>, since a clustering index reorders the table
  /// once and nothing maintains the order afterwards; <see cref="DbFeatures.ForeignKeysAutoIndexed"/>, since the
  /// server does not index a foreign key; <see cref="DbFeatures.ServerPreservesComments"/>, since the server
  /// regenerates a view definition and strips its comments, so a definition hash is attached with <c>COMMENT ON</c>;
  /// <see cref="DbFeatures.ForeignKeyMustTargetPrimaryKey"/>, since any unique key may be targeted.</remarks>
  public override DbFeatures Features =>
    DbFeatures.Schemas
    | DbFeatures.ReferentialConstraints
    | DbFeatures.JsonRows
    | DbFeatures.NamedRefConstraints
    | DbFeatures.HeapTables                    // every table is a heap
    | DbFeatures.ClusteringMarker              // CLUSTER … USING …, remembered in pg_index.indisclustered
    | DbFeatures.OrderedColumnsInIndexes
    | DbFeatures.IncludeColumnsInIndexes       // INCLUDE, since 11
    | DbFeatures.FilterInIndexes               // partial indexes
    | DbFeatures.Sequences
    | DbFeatures.IdentityColumns
    | DbFeatures.BulkInsertAdvancesIdentity
    | DbFeatures.Views
    | DbFeatures.MaterializedViews
    | DbFeatures.FullAlterTable
    | DbFeatures.SessionLocks                  // pg_try_advisory_lock
    | DbFeatures.DeferredConstraintCheck       // DEFERRABLE INITIALLY DEFERRED
    | DbFeatures.ArrayParameters               // a collection binds as one array, matched with = ANY(…)
    | DbFeatures.FullText                      // a GIN over a tsvector, matched with @@
    | DbFeatures.RowLocks;                     // FOR UPDATE on the root select; REPEATABLE READ for a consistent read

  /// <summary>Gets the index types PostgreSQL offers besides the default b-tree.</summary>
  /// <remarks>Full text is MIC's kind, not PostgreSQL's: the dialect creates it as a GIN index over the text-search
  /// vector of the columns, and the loader reads such an index back as a full-text index.</remarks>
  public override IReadOnlyList<DbIndexType> IndexTypes =>
    [DbIndexType.Hash, DbIndexType.Gist, DbIndexType.Gin, DbIndexType.SpGist, DbIndexType.Brin, DbIndexType.FullText,
      DbIndexType.Hnsw];

  /// <summary>Gets GiST, which is what PostGIS indexes a geography with.</summary>
  public override DbIndexType SpatialIndexType => DbIndexType.Gist;

  /// <summary>Gets HNSW, pgvector's graph index.</summary>
  public override DbIndexType VectorIndexType => DbIndexType.Hnsw;

  /// <summary>Gets the case the server folds an unquoted identifier to, which for PostgreSQL is lower case.</summary>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Lower;

  public override string DefaultSchema => "public";

  /// <summary>Gets the schemas the server owns, which the model loader skips.</summary>
  /// <remarks>The per-session <c>pg_temp_*</c> and <c>pg_toast_temp_*</c> schemas never appear in
  /// <c>information_schema.schemata</c>, so they are not listed.</remarks>
  public override IReadOnlyList<string> SystemSchemas => ["pg_catalog", "information_schema", "pg_toast"];

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new PostgresModelLoader(settings);

  /// <summary>Classifies a failure from its SQLSTATE and adds the constraint, table and column the server named.
  /// </summary>
  /// <remarks>Npgsql carries the constraint, table and column as fields on <see cref="PostgresException"/>, so the
  /// names do not depend on the server's message locale.</remarks>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    base.ClassifyException(failure, inner);
    if (inner is not PostgresException pg)
      return;
    failure.KeyName = Blank(pg.ConstraintName);
    failure.TableName = Blank(pg.TableName);
    failure.ColumnName = Blank(pg.ColumnName);
  }

  static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

  /// <summary>Gets the longest identifier name the server accepts.</summary>
  /// <remarks>The limit is 63 characters. PostgreSQL truncates a longer name silently rather than failing, so a
  /// generated name is checked against this limit before it is used.</remarks>
  public override int MaxNameLength => 63;

  private static DbTypeRegistry BuildTypeRegistry() {
    var registry = new DbTypeRegistry();
    // Introspection reads what the server calls a type: PostgreSQL reports `character varying` for varchar and
    // `int4` for integer, so the aliases matter.

    // Text. varchar is registered before text so it is the preferred type for string; text serves the unlimited case.
    registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize,
      aliases: "character varying", specialType: DbSpecialType.String);
    registry.Add("text", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.StringUnlimited);
    // jsonb is what a JSON member is; plain json reads back only for a table MIC did not make.
    registry.Add("jsonb", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.Json, columnInit: "'{}'");
    registry.Add("json", typeof(string), DbTypeFlags.Unlimited, columnInit: "'{}'");
    registry.Add("char", typeof(string), DbTypeFlags.TakesSize, aliases: "character, bpchar");

    // Numbers.
    registry.Add("integer", typeof(int), aliases: "int, int4");
    registry.Add("bigint", typeof(long), aliases: "int8");
    registry.Add("smallint", typeof(short), aliases: "int2");
    registry.Add("numeric", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "decimal", defaultPrecision: 18, defaultScale: 4);
    registry.Add("real", typeof(float), aliases: "float4");
    registry.Add("double precision", typeof(double), aliases: "float8");

    // PostgreSQL will not take an integer for a boolean, and the init literal runs as-is when a required column is
    // backfilled.
    registry.Add("boolean", typeof(bool), aliases: "bool", columnInit: "false");
    registry.Add("uuid", typeof(Guid));

    // Dates and times. timestamp is timestamp without time zone; the one with a zone reads back as DateTimeOffset.
    registry.Add("timestamp", typeof(DateTime), aliases: "timestamp without time zone");
    registry.Add("timestamptz", typeof(DateTimeOffset), aliases: "timestamp with time zone");
    registry.Add("date", typeof(DateOnly));
    registry.Add("time", typeof(TimeOnly), aliases: "time without time zone");
    registry.Add("interval", typeof(TimeSpan));

    registry.Add("bytea", typeof(byte[]), DbTypeFlags.Unlimited, specialType: DbSpecialType.BinaryUnlimited);

    // PostGIS geography, SRID 4326 unless said otherwise; the catalog reports the bare type name.
    registry.Add("geography", typeof(GeoPoint));

    // Array types, one per scalar an array member may hold. The alias is the name the catalog gives the type, the
    // item type's internal name after an underscore. An empty array fills a required column added to a table.
    AddArrayType(registry, "integer[]", typeof(int[]), "_int4");
    AddArrayType(registry, "bigint[]", typeof(long[]), "_int8");
    AddArrayType(registry, "smallint[]", typeof(short[]), "_int2");
    AddArrayType(registry, "double precision[]", typeof(double[]), "_float8");
    AddArrayType(registry, "real[]", typeof(float[]), "_float4");
    AddArrayType(registry, "numeric[]", typeof(decimal[]), "_numeric");
    AddArrayType(registry, "boolean[]", typeof(bool[]), "_bool");
    AddArrayType(registry, "text[]", typeof(string[]), "_text");
    AddArrayType(registry, "uuid[]", typeof(Guid[]), "_uuid");
    AddArrayType(registry, "timestamp[]", typeof(DateTime[]), "_timestamp");
    AddArrayType(registry, "date[]", typeof(DateOnly[]), "_date");

    // pgvector's type, for a member marked [Vector]; its size is the number of dimensions. A float[] member with no
    // mark stays real[].
    registry.Add("vector", typeof(float[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Vector);
    return registry;
  }

  private static void AddArrayType(DbTypeRegistry registry, string name, Type clrType, string catalogName) =>
    registry.Add(name, clrType, aliases: catalogName, columnInit: "'{}'");
}
