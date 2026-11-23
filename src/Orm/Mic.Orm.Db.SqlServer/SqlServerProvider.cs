using System.Data.Common;
using Mic.Common;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace Mic.Orm.Db.SqlServer;

/// <summary>Provides the SQL Server database provider: its feature set, its SQL dialect, its type registry and the
/// Microsoft.Data.SqlClient connections and commands.</summary>
/// <remarks>The type registry depends on <see cref="TargetVersion"/>.</remarks>
public class SqlServerProvider : DbProvider {
  /// <summary>Initializes a new instance of the <see cref="SqlServerProvider"/> class for a SQL Server version, with
  /// the Guid layout the server's ordering of a <c>uniqueidentifier</c> makes time-ordered unless another is given.
  /// </summary>
  public SqlServerProvider(SqlServerVersion targetVersion = SqlServerVersion.Sql2022,
      GuidScheme guidScheme = GuidScheme.V8SqlServer)
      : base(DbServerKind.MsSql, new SqlServerSqlDialect(), BuildTypeRegistry(targetVersion), guidScheme) =>
    TargetVersion = targetVersion;

  /// <summary>Gets the SQL Server version the statements and the DDL are written for, rather than the one met at
  /// connect time.</summary>
  /// <remarks>A JSON member is <c>nvarchar(max)</c> on 2022 and the native <c>json</c> type from 2025. A vector
  /// member is the <c>vector</c> type from 2025 and an error of the model on 2022.</remarks>
  /// <value>The version. The default is <see cref="SqlServerVersion.Sql2022"/>.</value>
  public SqlServerVersion TargetVersion { get; }

  public override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

  public override DbCommand CreateDbCommand() => new SqlCommand();

  /// <summary>Adapts an array of floats to the driver's vector, the one thing the driver binds such an array as;
  /// any other value as the base does.</summary>
  /// <inheritdoc/>
  public override object AdaptParameterValue(object value) =>
    value is float[] vector ? new SqlVector<float>(vector) : base.AdaptParameterValue(value);

  /// <summary>Converts the driver's vector to the array of floats a vector member holds; any other value as the
  /// base does.</summary>
  /// <inheritdoc/>
  public override object? ConvertReadValue(object? value, Type targetType) =>
    value is SqlVector<float> vector ? vector.Memory.ToArray() : base.ConvertReadValue(value, targetType);

  /// <summary>Asynchronously writes rows into a table with <c>SqlBulkCopy</c>.</summary>
  /// <param name="connection">The open connection to write on.</param>
  /// <param name="transaction">The transaction the write joins, or <see langword="null"/> for none.</param>
  /// <param name="bulk">The table, the columns and the rows.</param>
  /// <param name="runMode">One of the enumeration values that specifies whether the driver's synchronous or
  /// asynchronous call is made.</param>
  /// <param name="cancellation">The token to monitor for cancellation requests.</param>
  /// <returns>A task that represents the asynchronous operation. The task result is the number of rows written.
  /// </returns>
  /// <remarks>The copy runs with the driver's default options: the table's triggers do not fire and its check
  /// constraints are not checked.</remarks>
  public override async Task<int> BulkInsertAsync(DbConnection connection, DbTransaction? transaction,
      BulkInsertInfo bulk, RunMode runMode, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(bulk);
    using var data = new System.Data.DataTable();
    for (var i = 0; i < bulk.ColumnNames.Count; i++)
      data.Columns.Add(bulk.ColumnNames[i], bulk.FindValueType(i) ?? typeof(string));
    foreach (var row in bulk.Rows)
      data.Rows.Add(row);

    using var copy = new SqlBulkCopy((SqlConnection)connection, SqlBulkCopyOptions.Default,
      (SqlTransaction?)transaction) { DestinationTableName = bulk.TableRef };
    foreach (var name in bulk.ColumnNames)
      copy.ColumnMappings.Add(name, name);
    if (runMode == RunMode.Sync)
      copy.WriteToServer(data);
    else
      await copy.WriteToServerAsync(data, cancellation);
    return bulk.Rows.Count;
  }

  /// <summary>Gets the features SQL Server supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.ForeignKeysAutoIndexed"/>, since the server does not index a
  /// foreign key; <see cref="DbFeatures.ClusteringMarker"/>, since the clustered index is the storage, not a marker;
  /// <see cref="DbFeatures.DeferredConstraintCheck"/>, since constraints are checked per statement;
  /// <see cref="DbFeatures.ForeignKeyMustTargetPrimaryKey"/>, since any unique key may be targeted;
  /// <see cref="DbFeatures.IdentityRequiresInlineKey"/>, since an identity column need not be keyed; and
  /// <see cref="DbFeatures.ServerPreservesComments"/>, since the server stores a view's text verbatim but normalises
  /// an index filter (<c>"LastName" IS NOT NULL</c> comes back as <c>([LastName] IS NOT NULL)</c>), so a definition
  /// hash is kept in an extended property.</remarks>
  public override DbFeatures Features =>
    DbFeatures.Schemas
    | DbFeatures.ReferentialConstraints
    | DbFeatures.JsonRows
    | DbFeatures.NamedRefConstraints
    | DbFeatures.HeapTables                    // a table with no clustered index is one
    | DbFeatures.ClusteredStorage              // the clustered index, where there is one, is the table
    | DbFeatures.OrderedColumnsInIndexes
    | DbFeatures.IncludeColumnsInIndexes
    | DbFeatures.FilterInIndexes
    | DbFeatures.Sequences                     // since 2012
    | DbFeatures.IdentityColumns
    | DbFeatures.BulkInsertAdvancesIdentity
    | DbFeatures.Views
    | DbFeatures.FullAlterTable
    | DbFeatures.AlterColumnRestatesDefinition // ALTER COLUMN says type and nullability together, or loses one
    | DbFeatures.NamedDefaultConstraints       // a default is a constraint with its own name
    | DbFeatures.SessionLocks                  // sp_getapplock
    | DbFeatures.FullText                      // a FULLTEXT INDEX in a catalog, CONTAINS and CONTAINSTABLE
    | DbFeatures.IdentityCarriedInBatch        // SCOPE_IDENTITY() into an output parameter
    | DbFeatures.RowLocks                     // WITH (UPDLOCK, ROWLOCK) on the root select; SNAPSHOT for a consistent read
    | DbFeatures.BulkInsert;                   // SqlBulkCopy

  /// <summary>Gets the case the server folds an unquoted identifier to; SQL Server stores an identifier as written
  /// and compares case-insensitively under the default collation.</summary>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Preserve;

  public override string DefaultSchema => "dbo";

  /// <summary>Gets the index types SQL Server offers beyond the ordinary index: the full-text index, a nameless
  /// object of its table in a catalog that carries MIC's name for it.</summary>
  /// <remarks>Columnstore and XML indexes are not listed, being storage and a cartridge rather than an access method
  /// over a column list. See <see cref="SqlServerSqlDialect.BuildFullTextIndex"/>.</remarks>
  public override IReadOnlyList<DbIndexType> IndexTypes => [DbIndexType.FullText, DbIndexType.Spatial];

  public override DbIndexType SpatialIndexType => DbIndexType.Spatial;

  /// <summary>Gets the isolation level of a consistent read, <c>SNAPSHOT</c>.</summary>
  /// <remarks>The database has to allow row versioning with <c>ALTER DATABASE … SET ALLOW_SNAPSHOT_ISOLATION ON</c>,
  /// or the first statement of the transaction fails with error 3952, which <see cref="ClassifyException"/> names.
  /// </remarks>
  public override System.Data.IsolationLevel SnapshotIsolationLevel => System.Data.IsolationLevel.Snapshot;

  /// <summary>Gets the schemas SQL Server ships with, which the model loader skips.</summary>
  /// <remarks><c>dbo</c> is not one of them; it is where an application's own tables live by default.</remarks>
  public override IReadOnlyList<string> SystemSchemas =>
    ["sys", "INFORMATION_SCHEMA", "db_accessadmin", "db_backupoperator", "db_datareader", "db_datawriter",
     "db_ddladmin", "db_denydatareader", "db_denydatawriter", "db_owner", "db_securityadmin", "guest"];

  /// <summary>Gets the longest identifier name the server accepts.</summary>
  /// <remarks>The limit is 128 characters. SQL Server rejects a longer name.</remarks>
  public override int MaxNameLength => 128;

  /// <summary>Gets the largest number of parameters one statement may bind.</summary>
  /// <remarks>The limit is 2100.</remarks>
  public override int MaxParameters => 2100;

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new SqlServerModelLoader(settings);

  /// <summary>Classifies a failure from SQL Server's error number, which replaces the class-level answer the SQLSTATE
  /// gives.</summary>
  /// <remarks>The driver derives the SQLSTATE from the error number, so the number is what carries the meaning.
  /// </remarks>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    base.ClassifyException(failure, inner);
    if (inner is not SqlException sql)
      return;
    failure.ProviderErrorCode = sql.Number.ToString();
    // Error 3952: the database refuses snapshot isolation at the first statement until told once; the fix is one
    // statement, so the hint names it.
    if (sql.Number == 3952)
      failure.Hint = "Snapshot isolation is off for this database: run ALTER DATABASE [<name>] SET ALLOW_SNAPSHOT_ISOLATION ON once.";
    failure.SubType = sql.Number switch {
      2627 or 2601 => DataAccessException.SubTypeUniqueViolation,       // constraint, and unique index
      547 => DataAccessException.SubTypeIntegrityViolation,             // FK or CHECK, either direction
      515 => DataAccessException.SubTypeNotNullViolation,
      1205 => DataAccessException.SubTypeDeadlock,
      -2 or 1222 => DataAccessException.SubTypeDeadlock,                // timeout, and lock request timeout
      208 or 207 or 102 or 2714 => DataAccessException.SubTypeSyntaxOrMissingObject,
      18456 or 229 or 230 => DataAccessException.SubTypeAccessDenied,   // login failed, permission denied
      53 or 40615 or 4060 => DataAccessException.SubTypeConnectionFailed,
      _ => failure.SubType,
    };
  }

  private static DbTypeRegistry BuildTypeRegistry(SqlServerVersion targetVersion) {
    var registry = new DbTypeRegistry();
    // Text. nvarchar is the preferred string type, being the Unicode one; the rest are registered so a database MIC
    // did not create still reads back. (max) is part of the name, not a size: SQL Server reports such a column as
    // nvarchar with a length of -1, which the loader turns back into this name.
    registry.Add("nvarchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
    registry.Add("nvarchar(max)", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.StringUnlimited);
    // The native json type arrived with 2025; for an older target a JSON member has no special type and takes
    // nvarchar(max), which every JSON function reads.
    if (targetVersion >= SqlServerVersion.Sql2025)
      registry.Add("json", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.Json, columnInit: "'{}'");
    registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize);
    registry.Add("varchar(max)", typeof(string), DbTypeFlags.Unlimited);
    registry.Add("nchar", typeof(string), DbTypeFlags.TakesSize);
    registry.Add("char", typeof(string), DbTypeFlags.TakesSize);

    // Superseded by their own vendor; reachable by name for a schema that has one, never chosen.
    registry.Add("ntext", typeof(string), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);
    registry.Add("text", typeof(string), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);

    registry.Add("int", typeof(int));
    registry.Add("bigint", typeof(long));
    registry.Add("smallint", typeof(short));
    registry.Add("decimal", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "numeric, money, smallmoney", defaultPrecision: 18, defaultScale: 4);
    registry.Add("real", typeof(float));
    registry.Add("float", typeof(double));

    // bit takes 0 and 1 and nothing else.
    registry.Add("bit", typeof(bool), columnInit: "0");

    registry.Add("uniqueidentifier", typeof(Guid));

    registry.Add("datetime2", typeof(DateTime), aliases: "datetime, smalldatetime");
    registry.Add("datetimeoffset", typeof(DateTimeOffset));
    registry.Add("date", typeof(DateOnly));
    registry.Add("time", typeof(TimeOnly));

    // varbinary is not the bounded binary type: a bare varbinary means varbinary(1) to SQL Server, so a byte[] member
    // that named no size would be created one byte wide and read back as a difference. A byte[] with no size means
    // unlimited, as everywhere else; a width is still reachable by asking for one.
    registry.Add("varbinary(max)", typeof(byte[]), DbTypeFlags.Unlimited, specialType: DbSpecialType.BinaryUnlimited);
    registry.Add("varbinary", typeof(byte[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Binary);
    registry.Add("binary", typeof(byte[]), DbTypeFlags.TakesSize);
    registry.Add("image", typeof(byte[]), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);

    registry.Add("geography", typeof(GeoPoint));

    // The vector type arrived with 2025, for a member marked [Vector]; its size is the number of dimensions.
    if (targetVersion >= SqlServerVersion.Sql2025)
      registry.Add("vector", typeof(float[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Vector);
    return registry;
  }
}

/// <summary>Specifies the SQL Server version a provider is written for, as its year.</summary>
public enum SqlServerVersion {
  Sql2022 = 2022,
  Sql2025 = 2025,
}
