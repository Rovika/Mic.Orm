using System.Data.Common;
using IBM.Data.Db2;
using Mic.Common;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Db2;

/// <summary>Provides the database provider for Db2 on Linux and Windows: its feature set, its SQL dialect, its type
/// registry and the connections and commands of IBM's driver.</summary>
/// <remarks>Db2 has no <c>information_schema</c>; <see cref="Db2ModelLoader"/> reads the <c>SYSCAT</c> views
/// instead. The driver comes in a package per operating system, <c>Net.IBM.Data.Db2</c> for Windows,
/// <c>Net.IBM.Data.Db2-lnx</c> for Linux and <c>Net.IBM.Data.Db2-osx</c> for macOS, and the application references
/// the one of the system it runs on.</remarks>
public class Db2Provider : DbProvider {
  /// <summary>Gets the features Db2 supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.SessionLocks"/>, since the server has no named lock outside a
  /// transaction, so the coordination of an upgrade falls back to a warning; and
  /// <see cref="DbFeatures.IdentityCarriedInBatch"/>, <see cref="DbFeatures.FullText"/>,
  /// <see cref="DbFeatures.FilterInIndexes"/> and <see cref="DbFeatures.MaterializedViews"/>.</remarks>
  public override DbFeatures Features =>
    DbFeatures.Schemas
    | DbFeatures.ReferentialConstraints
    | DbFeatures.NamedRefConstraints
    | DbFeatures.HeapTables
    | DbFeatures.OrderedColumnsInIndexes
    | DbFeatures.Sequences
    | DbFeatures.IdentityColumns
    | DbFeatures.FullAlterTable
    | DbFeatures.Views
    | DbFeatures.JsonRows                      // as XML: see SerializeRows
    | DbFeatures.RowLocks
    | DbFeatures.BulkInsertAdvancesIdentity
    | DbFeatures.BinaryGuids                   // binary(16), since there is no uuid type
    | DbFeatures.JsonRowsOfParameterOnlyDb2    // no JSON array is spread into rows
    | DbFeatures.NullGuidAsLiteralDb2;         // the driver sends an untyped null as text (SQL0301N)

  /// <summary>Gets the case the server folds an unquoted identifier to, which for Db2 is upper case.</summary>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Upper;

  /// <summary>Gets the default schema, which is empty: an unqualified name goes to the schema named as the
  /// connected user.</summary>
  public override string DefaultSchema => string.Empty;

  /// <summary>Gets the schemas Db2 creates in every database, which the model loader skips.</summary>
  public override IReadOnlyList<string> SystemSchemas =>
    ["SYSIBM", "SYSCAT", "SYSSTAT", "SYSFUN", "SYSPROC", "SYSIBMADM", "SYSIBMINTERNAL", "SYSIBMTS", "SYSPUBLIC",
     "SYSTOOLS", "NULLID", "SQLJ"];

  /// <summary>Gets the most parameters a statement takes.</summary>
  public override int MaxParameters => 32767;

  public Db2Provider(GuidScheme guidScheme = GuidScheme.V7)
    : base(DbServerKind.Db2, new Db2SqlDialect(), BuildTypeRegistry(), guidScheme) { }

  public override DbConnection CreateConnection(string connectionString) => new DB2Connection(connectionString);

  public override DbCommand CreateDbCommand() => new DB2Command();

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new Db2ModelLoader(settings);

  /// <summary>Adapts a parameter value to one the driver accepts: a <c>DateOnly</c> as a date-time at midnight and a
  /// <c>TimeOnly</c> as a time span.</summary>
  /// <param name="value">The value to bind.</param>
  /// <returns>The value the driver accepts.</returns>
  /// <remarks>The driver refuses both types with an invalid cast.</remarks>
  public override object AdaptParameterValue(object value) => value switch {
    DateOnly date => date.ToDateTime(TimeOnly.MinValue),
    TimeOnly time => time.ToTimeSpan(),
    _ => base.AdaptParameterValue(value),
  };

  /// <summary>Serializes the rows of a parameter as XML, which <see cref="Db2SqlDialect.JsonRowsSource"/> reads
  /// with <c>XMLTABLE</c>.</summary>
  /// <param name="columns">The columns of a row, in order.</param>
  /// <param name="rows">The rows, each an array with one value per column.</param>
  /// <returns>The XML text.</returns>
  public override string SerializeRows(IReadOnlyList<JsonRowsColumn> columns, IEnumerable<object?[]> rows) =>
    XmlRowsHelper.Serialize(this, columns, rows);

  /// <summary>Classifies a failure by the SQLSTATE of the first error the driver reports.</summary>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  /// <remarks>The driver's exception leaves <see cref="DbException.SqlState"/> empty and keeps the state on each of
  /// its errors.</remarks>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    base.ClassifyException(failure, inner);
    if (inner is not DB2Exception db2Exception || db2Exception.Errors.Count == 0)
      return;
    var sqlState = db2Exception.Errors[0].SQLState;
    if (string.IsNullOrEmpty(sqlState))
      return;
    failure.ProviderErrorCode = sqlState;
    failure.SubType = SubTypeForSqlState(sqlState) ?? failure.SubType;
  }

  /// <summary>Maps a SQLSTATE to a failure sub-type, reading 23513, Db2's state for a check constraint, as a check
  /// violation.</summary>
  /// <param name="sqlState">The SQLSTATE, or <see langword="null"/>.</param>
  /// <returns>The sub-type, or <see langword="null"/> if the state is unknown or empty.</returns>
  public override string? SubTypeForSqlState(string? sqlState) =>
    sqlState == "23513" ? DataAccessException.SubTypeCheckViolation : base.SubTypeForSqlState(sqlState);

  /// <summary>Builds the type registry, under the names the catalog reports in lower case.</summary>
  private static DbTypeRegistry BuildTypeRegistry() {
    var registry = new Db2TypeRegistry();
    registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize, aliases: "character varying",
      specialType: DbSpecialType.String);
    registry.Add("clob", typeof(string), DbTypeFlags.Unlimited, aliases: "long varchar",
      specialType: DbSpecialType.StringUnlimited);
    // The fixed-length type, which a key of an older database commonly is; a value comes back padded with spaces.
    registry.Add("char", typeof(string), DbTypeFlags.TakesSize, aliases: "character");
    // The double-byte character types.
    registry.Add("vargraphic", typeof(string), DbTypeFlags.TakesSize);
    registry.Add("graphic", typeof(string), DbTypeFlags.TakesSize);
    registry.Add("dbclob", typeof(string), DbTypeFlags.Unlimited, aliases: "long vargraphic");
    registry.Add("xml", typeof(string), DbTypeFlags.Unlimited);

    registry.Add("integer", typeof(int), aliases: "int");
    registry.Add("bigint", typeof(long));
    registry.Add("smallint", typeof(short));
    registry.Add("decimal", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "numeric, dec", defaultPrecision: 18, defaultScale: 4);
    registry.Add("decfloat", typeof(decimal));
    registry.Add("real", typeof(float));
    registry.Add("double", typeof(double), aliases: "float, double precision");
    registry.Add("boolean", typeof(bool), columnInit: "FALSE");

    registry.Add("varbinary", typeof(byte[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Binary);
    // No Guid type, so a Guid is binary(16). The loader reports CHAR FOR BIT DATA, the older spelling, as binary.
    registry.Add("binary", typeof(Guid), DbTypeFlags.TakesSize, defaultSize: 16,
      columnInit: "BX'00000000000000000000000000000000'");
    registry.Add("blob", typeof(byte[]), DbTypeFlags.Unlimited, specialType: DbSpecialType.BinaryUnlimited);

    // TIMESTAMP holds microseconds by default, and there is no type with a time zone.
    registry.Add("timestamp", typeof(DateTime));
    registry.Add("date", typeof(DateOnly));
    registry.Add("time", typeof(TimeOnly));
    return registry;
  }
}

/// <summary>Provides the Db2 type registry, which maps a geographic point to the bytes of its well-known binary.
/// </summary>
/// <remarks>The server holds no geographic type without its spatial extension, so a point is
/// <c>varbinary(21)</c>, the length of a point's well-known binary, stored and read as it is. No distance is
/// computed on the server.</remarks>
public class Db2TypeRegistry : DbTypeRegistry {
  /// <summary>Specifies the length of a point's well-known binary: a byte of order, four of type and two doubles.
  /// </summary>
  public const int PointSize = 21;

  /// <inheritdoc/>
  public override DbTypeInfo? MapMember(EntityMemberInfo member, out string? error) {
    if (member.DbTypeSpec != null || member.DataType != typeof(GeoPoint))
      return base.MapMember(member, out error);
    error = null;
    return Apply(FindByName("varbinary")!, size: PointSize);
  }
}
