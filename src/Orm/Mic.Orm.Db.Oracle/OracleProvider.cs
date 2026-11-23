using System.Data.Common;
using Mic.Common;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Oracle.ManagedDataAccess.Client;

namespace Mic.Orm.Db.Oracle;

/// <summary>Provides the Oracle database provider: its feature set, its SQL dialect, its type registry and the
/// Oracle.ManagedDataAccess connections and commands.</summary>
/// <remarks>Oracle has no <c>information_schema</c>; <see cref="OracleModelLoader"/> reads the <c>ALL_*</c> views
/// instead. The type registry is an <see cref="OracleTypeRegistry"/> and the CRUD command builder an
/// <see cref="OracleRecordCommandBuilder"/>.</remarks>
public class OracleProvider : DbProvider {
  public OracleProvider(GuidScheme guidScheme = GuidScheme.V7)
    : base(DbServerKind.Oracle, new OracleSqlDialect(), BuildTypeRegistry(), guidScheme) { }

  public override DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);

  /// <summary>Gets the features Oracle supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.FilterInIndexes"/>, since a function-based index is a different
  /// thing; <see cref="DbFeatures.IncludeColumnsInIndexes"/>; <see cref="DbFeatures.ForeignKeysAutoIndexed"/>, since
  /// the server does not index a foreign key; <see cref="DbFeatures.AlterColumnRestatesDefinition"/>, since
  /// <c>MODIFY</c> amends and the base class's separate statements are correct;
  /// <see cref="DbFeatures.NamedDefaultConstraints"/>, since a default is part of the column and is unset by being
  /// set to NULL; and <see cref="DbFeatures.ClusteringMarker"/>,
  /// <see cref="DbFeatures.ForeignKeyMustTargetPrimaryKey"/>, <see cref="DbFeatures.ServerPreservesComments"/> and
  /// <see cref="DbFeatures.IdentityRequiresInlineKey"/>.</remarks>
  public override DbFeatures Features =>
    DbFeatures.Schemas                         // a schema is a user
    | DbFeatures.ReferentialConstraints
    | DbFeatures.JsonRows
    | DbFeatures.NamedRefConstraints
    | DbFeatures.HeapTables
    | DbFeatures.ClusteredStorage              // index-organized tables
    | DbFeatures.OrderedColumnsInIndexes
    | DbFeatures.Sequences
    | DbFeatures.IdentityColumns               // since 12c
    | DbFeatures.Views
    | DbFeatures.MaterializedViews
    | DbFeatures.FullAlterTable
    | DbFeatures.DeferredConstraintCheck       // DEFERRABLE INITIALLY DEFERRED
    | DbFeatures.SessionLocks                  // DBMS_LOCK
    | DbFeatures.BinaryGuids                    // raw(16), since there is no uuid type
    | DbFeatures.FullText                      // Oracle Text: a CONTEXT index, CONTAINS and SCORE
    | DbFeatures.IdentityCarriedInBatch        // RETURNING … INTO a bind of the PL/SQL block
    | DbFeatures.RowLocks                      // FOR UPDATE on the root select; SERIALIZABLE for a consistent read
    | DbFeatures.NoRowLimitWithLock            // FOR UPDATE over OFFSET … FETCH is ORA-02014
    | DbFeatures.BulkInsert                    // one insert, its parameters bound as arrays
    | DbFeatures.NoTempTableNotNullOracle;     // a private temporary table takes no constraint (ORA-14451)

  /// <summary>Gets the case the server folds an unquoted identifier to, which for Oracle is upper case.</summary>
  /// <remarks>MIC quotes every identifier it writes, so the folding matters for the names a database MIC did not
  /// create reports back, which arrive upper-cased.</remarks>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Upper;

  /// <summary>Gets the default schema, which is empty: a schema is a user, and the one that applies is whichever user
  /// the application names, the connected user being only the commonest case.</summary>
  public override string DefaultSchema => string.Empty;

  /// <summary>Gets the schemas Oracle ships with, which the model loader skips.</summary>
  /// <remarks>Every internal account is a schema; reading them would mean introspecting several thousand tables.
  /// </remarks>
  public override IReadOnlyList<string> SystemSchemas =>
    ["SYS", "SYSTEM", "SYSAUX", "OUTLN", "DBSNMP", "APPQOSSYS", "GSMADMIN_INTERNAL", "XDB", "WMSYS", "CTXSYS",
     "ORDSYS", "ORDDATA", "ORDPLUGINS", "MDSYS", "OLAPSYS", "LBACSYS", "DVSYS", "AUDSYS", "GGSYS", "REMOTE_SCHEDULER_AGENT",
     "DBSFWUSER", "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "SYS$UMF", "ANONYMOUS", "PUBLIC", "DIP", "ORACLE_OCM",
     "GSMCATUSER", "GSMUSER", "GSMROOTUSER", "SYSTEM_PRIVILEGE_MAP", "MDDATA", "XS$NULL", "OJVMSYS", "PDBADMIN"];

  /// <summary>Gets the longest identifier name the server accepts.</summary>
  /// <remarks>The limit is 128 characters since 12.2, and 30 before it.</remarks>
  public override int MaxNameLength => 128;

  /// <summary>Gets the index types Oracle offers beyond the b-tree: the bitmap index, and the full-text index, which
  /// is Oracle Text's <c>CONTEXT</c> domain index with a datastore over the columns.</summary>
  /// <remarks>Function-based indexes and other domain indexes are not listed, since one indexes an expression and
  /// the other hands the work to a cartridge; neither is an access method over a column list. See
  /// <see cref="OracleSqlDialect.BuildFullTextIndex"/>.</remarks>
  public override IReadOnlyList<DbIndexType> IndexTypes => [DbIndexType.Bitmap, DbIndexType.FullText, DbIndexType.Spatial];

  public override DbIndexType SpatialIndexType => DbIndexType.Spatial;

  /// <summary>Gets the isolation level of a consistent read, <c>SERIALIZABLE</c>, which on Oracle is the
  /// transaction-level snapshot; read-only work never fails to serialize.</summary>
  public override System.Data.IsolationLevel SnapshotIsolationLevel => System.Data.IsolationLevel.Serializable;

  /// <summary>Creates a command that fetches a LONG column whole and binds its parameters by name.</summary>
  /// <remarks>The driver's default LONG fetch size is zero bytes, and <c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>, which
  /// every column default is read from, is a LONG; -1 fetches the whole value. The driver binds parameters in the
  /// order they were added unless told otherwise, so a statement whose parameters are added in another order than
  /// they are written would silently bind the wrong values.</remarks>
  /// <returns>The command.</returns>
  public override DbCommand CreateDbCommand() => new OracleCommand {
    InitialLONGFetchSize = -1,

    // The driver binds in the order parameters were added unless told otherwise.
    BindByName = true,
  };

  /// <summary>Asynchronously writes rows into a table with one insert whose parameters are bound as arrays, a
  /// value for every row.</summary>
  /// <param name="connection">The open connection to write on.</param>
  /// <param name="transaction">The transaction the write joins, or <see langword="null"/> for none.</param>
  /// <param name="bulk">The table, the columns and the rows.</param>
  /// <param name="runMode">One of the enumeration values that specifies whether the driver's synchronous or
  /// asynchronous call is made.</param>
  /// <param name="cancellation">The token to monitor for cancellation requests.</param>
  /// <returns>A task that represents the asynchronous operation. The task result is the number of rows written.
  /// </returns>
  /// <remarks><c>INSERT ALL</c>, which a multi-row insert is on Oracle, is parsed as a statement of every row; an
  /// array bind is one statement run for each.</remarks>
  public override async Task<int> BulkInsertAsync(DbConnection connection, DbTransaction? transaction,
      BulkInsertInfo bulk, RunMode runMode, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(bulk);
    var names = bulk.ColumnNames;
    using var command = (OracleCommand)CreateDbCommand();
    command.Connection = (OracleConnection)connection;
    command.Transaction = (OracleTransaction?)transaction;
    command.CommandText = $"INSERT INTO {bulk.TableRef} ({string.Join(", ", names.Select(n => SqlDialect.Quote(n)))}) "
      + $"VALUES ({string.Join(", ", names.Select((_, i) => ":c" + i))})";
    command.ArrayBindCount = bulk.Rows.Count;
    for (var i = 0; i < names.Count; i++)
      command.Parameters.Add(MakeArrayParameter("c" + i, bulk, i));
    return runMode == RunMode.Sync ? command.ExecuteNonQuery() : await command.ExecuteNonQueryAsync(cancellation);
  }

  /// <summary>Makes the parameter that holds one column's values as an array of their type, which the driver types
  /// the bind from.</summary>
  /// <remarks>An array of a value type cannot hold a null, so a null there is marked in the bind status.</remarks>
  private static OracleParameter MakeArrayParameter(string name, BulkInsertInfo bulk, int column) {
    var type = bulk.FindValueType(column) ?? typeof(string);
    var values = Array.CreateInstance(type, bulk.Rows.Count);
    OracleParameterStatus[]? statuses = null;
    for (var r = 0; r < bulk.Rows.Count; r++) {
      var value = bulk.Rows[r][column];
      if (value != null)
        values.SetValue(value.GetType() == type ? value
          : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture), r);
      else if (type.IsValueType)
        (statuses ??= new OracleParameterStatus[bulk.Rows.Count])[r] = OracleParameterStatus.NullInsert;
    }
    var parameter = new OracleParameter { ParameterName = name, Value = values };
    if (statuses != null)
      parameter.ArrayBindStatus = statuses;
    return parameter;
  }

  /// <summary>Sets a parameter's value, binding a string longer than a <c>VARCHAR2</c> bind carries as a
  /// <c>CLOB</c>.</summary>
  /// <remarks>The driver binds a string as <c>VARCHAR2</c>, which carries 32,767 bytes. The server refuses a longer
  /// one wherever the statement reads it rather than stores it: the rows of a parameter under <c>JSON_TABLE</c>
  /// fail with ORA-01460, a JSON document with ORA-40441.</remarks>
  /// <param name="parameter">The parameter to set.</param>
  /// <param name="value">The value to bind.</param>
  protected override void SetParameterValue(DbParameter parameter, object value) {
    base.SetParameterValue(parameter, value);
    // A character is three bytes of UTF-8 at most, so a short string is not measured.
    if (parameter.Value is string text && text.Length > MaxStringBindBytes / 3
        && System.Text.Encoding.UTF8.GetByteCount(text) > MaxStringBindBytes)
      ((OracleParameter)parameter).OracleDbType = OracleDbType.Clob;
  }

  const int MaxStringBindBytes = 32_767;

  /// <summary>Gets the bind prefix, a colon; Oracle takes no other.</summary>
  public override string ParameterPrefix => ":";

  /// <summary>Adapts a parameter value to one the driver accepts: a Boolean as 0 or 1, and a <c>DateOnly</c> as a
  /// date-time at midnight.</summary>
  /// <remarks>The driver refuses a <c>bool</c> and a <c>DateOnly</c> rather than converting them ("value does not
  /// fall within the expected range"). A bool is stored as <c>number(1)</c>; a <c>DateOnly</c> is a <c>date</c>,
  /// which carries a time.</remarks>
  /// <param name="value">The value to bind.</param>
  /// <returns>The value the driver accepts.</returns>
  public override object AdaptParameterValue(object value) => value switch {
    bool flag => flag ? 1 : 0,
    DateOnly date => date.ToDateTime(TimeOnly.MinValue),
    _ => base.AdaptParameterValue(value),
  };

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new OracleModelLoader(settings);

  /// <summary>Creates the CRUD command builder, an <see cref="OracleRecordCommandBuilder"/>, since
  /// <c>RETURNING … INTO</c> needs an output parameter no template can declare.</summary>
  /// <param name="dbModel">The database model the commands are built for.</param>
  /// <returns>The builder.</returns>
  public override RecordCommandBuilder CreateRecordCommandBuilder(DbModel dbModel) => new OracleRecordCommandBuilder(dbModel);

  /// <summary>Classifies a failure from Oracle's error number, which replaces the class-level answer the SQLSTATE
  /// gives.</summary>
  /// <remarks>Oracle reports no usable SQLSTATE; its driver synthesises one.</remarks>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    base.ClassifyException(failure, inner);
    if (inner is not OracleException ora)
      return;
    failure.ProviderErrorCode = ora.Number.ToString();
    failure.SubType = ora.Number switch {
      1 => DataAccessException.SubTypeUniqueViolation,                  // ORA-00001: unique constraint violated
      2291 or 2292 => DataAccessException.SubTypeIntegrityViolation,    // parent not found; child records exist
      1400 or 1407 => DataAccessException.SubTypeNotNullViolation,      // cannot insert NULL; cannot update to NULL
      2290 => DataAccessException.SubTypeCheckViolation,
      60 or 54 or 4020 => DataAccessException.SubTypeDeadlock,          // deadlock; resource busy; library-cache lock
      942 or 904 or 900 or 955 => DataAccessException.SubTypeSyntaxOrMissingObject,
      1017 or 1031 => DataAccessException.SubTypeAccessDenied,
      12154 or 12541 or 12514 or 1034 => DataAccessException.SubTypeConnectionFailed,
      _ => failure.SubType,
    };
  }

  /// <summary>Builds the type registry, an <see cref="OracleTypeRegistry"/>.</summary>
  private static DbTypeRegistry BuildTypeRegistry() {
    var registry = new OracleTypeRegistry();
    // VARCHAR2, not varchar: varchar is a synonym Oracle warns against and reports back as VARCHAR2. CHAR is the
    // length in characters rather than bytes, which is what a model means by a size.
    registry.Add("varchar2", typeof(string), DbTypeFlags.TakesSize, aliases: "varchar, nvarchar2, char, nchar",
      specialType: DbSpecialType.String);
    registry.Add("clob", typeof(string), DbTypeFlags.Unlimited, aliases: "nclob",
      specialType: DbSpecialType.StringUnlimited);
    // A JSON member is the native JSON type (21c and later), a binary form the server validates and its JSON
    // functions read directly; textual JSON bound as a string converts on the way in.
    registry.Add("json", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.Json, columnInit: "'{}'");

    // One numeric type: int, bigint and smallint are all NUMBER with a precision. They are registered as separate
    // defs so forward mapping can pick one per CLR type, and the loader turns NUMBER(p,s) back into whichever of them
    // it came from.
    registry.Add("number(10)", typeof(int));
    registry.Add("number(19)", typeof(long));
    registry.Add("number(5)", typeof(short));
    registry.Add("number(1)", typeof(bool), columnInit: "0");
    registry.Add("number", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "decimal, numeric", defaultPrecision: 18, defaultScale: 4);
    registry.Add("binary_float", typeof(float));
    registry.Add("binary_double", typeof(double));

    // raw is the bounded binary type, for a byte[] that says its size: a RAW compares and indexes, which a BLOB does
    // not. A byte[] that says nothing about its length means unlimited, which is blob.
    registry.Add("raw", typeof(byte[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Binary);

    // No Guid type, so a Guid is raw(16), what Oracle stores one as internally. A second registration under one name:
    // forward mapping is keyed by CLR type, so byte[] keeps the first raw and Guid gets this one; backward mapping is
    // keyed by name, so a raw column reads back as a Guid.
    registry.Add("raw", typeof(Guid), DbTypeFlags.TakesSize, defaultSize: 16,
      columnInit: "HEXTORAW('00000000000000000000000000000000')");

    // DATE carries a time and no fractional seconds; TIMESTAMP is the one that means what DateTime means.
    registry.Add("timestamp(7)", typeof(DateTime), aliases: "timestamp");
    registry.Add("timestamp(7) with time zone", typeof(DateTimeOffset), aliases: "timestamp with time zone");
    registry.Add("date", typeof(DateOnly));
    registry.Add("interval day(2) to second(6)", typeof(TimeSpan), aliases: "interval day to second");

    registry.Add("blob", typeof(byte[]), DbTypeFlags.Unlimited, specialType: DbSpecialType.BinaryUnlimited);

    // Oracle Spatial's object type, reachable by its public synonym; the loader lowercases what the catalog reports.
    registry.Add("sdo_geometry", typeof(GeoPoint));
    return registry;
  }
}

/// <summary>Provides the Oracle type registry, whose placeholder for a string column is a single space.</summary>
/// <remarks>The empty string is null in Oracle, so <c>''</c> as the placeholder would fill the column with nulls and
/// the NOT NULL step that follows would fail. A single space is the smallest value that is not null.</remarks>
public class OracleTypeRegistry : DbTypeRegistry {
  public override string? GetDefaultColumnInit(Type clrType) {
    var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
    return type == typeof(string) ? "' '" : base.GetDefaultColumnInit(clrType);
  }
}
