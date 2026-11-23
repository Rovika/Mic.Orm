using System.Data.Common;
using Mic.Common;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Sql;

namespace Mic.Orm.Db.Provider;

/// <summary>Defines the core behavior of a database provider, everything specific to one kind of server, and provides
/// a base for derived classes.</summary>
/// <remarks>A derived class declares its server's features, settings and types and supplies the driver's connection
/// and command objects. A vendor difference is expressed as a declaration or a dialect template, not as an override
/// with logic in it.</remarks>
public abstract class DbProvider {
  /// <summary>Called from constructors in derived classes to initialize the <see cref="DbProvider"/> class with the
  /// server's kind, dialect, type registry and Guid scheme.</summary>
  /// <exception cref="ArgumentException"><paramref name="guidScheme"/> is <see cref="GuidScheme.Auto"/>, which is
  /// the application's setting, not a layout.</exception>
  protected DbProvider(DbServerKind serverKind, SqlDialect sqlDialect, DbTypeRegistry typeRegistry,
      GuidScheme guidScheme = GuidScheme.V7) {
    ArgumentNullException.ThrowIfNull(sqlDialect);
    ArgumentNullException.ThrowIfNull(typeRegistry);
    if (guidScheme == GuidScheme.Auto)
      throw new ArgumentException("A provider's Guid scheme is a layout; Auto is the application's setting.",
        nameof(guidScheme));
    ServerKind = serverKind;
    SqlDialect = sqlDialect;
    TypeRegistry = typeRegistry;
    GuidScheme = guidScheme;
  }

  public DbServerKind ServerKind { get; }

  /// <summary>Gets the layout of the Guids minted for a database of this provider under
  /// <see cref="GuidScheme.Auto"/>: the one the server's ordering of a Guid makes time-ordered, unless the
  /// constructor was given another for a database that already holds keys of a different layout.</summary>
  public GuidScheme GuidScheme { get; }

  /// <summary>Gets the dialect this server's SQL is spelled with, one instance shared by every caller.</summary>
  public SqlDialect SqlDialect { get; }

  /// <summary>Gets the types this server has, and what each reads back as.</summary>
  public DbTypeRegistry TypeRegistry { get; }

  /// <summary>When overridden in a derived class, gets the features this server has, which the base gates its own
  /// calls on.</summary>
  public abstract DbFeatures Features { get; }

  public bool Supports(DbFeatures features) => (Features & features) == features;

  // ----- settings: the differences that are not boolean -------------------------------------------------------------

  /// <summary>Gets how the server folds an unquoted identifier.</summary>
  public virtual DbIdentifierCase IdentifierCase => DbIdentifierCase.Preserve;

  /// <summary>Gets the schema a table goes to when nothing says otherwise, such as <c>public</c> or <c>dbo</c>, or
  /// an empty string where the server has no schemas.</summary>
  public virtual string DefaultSchema => string.Empty;

  /// <summary>Gets the schemas the server owns, which introspection always excludes.</summary>
  public virtual IReadOnlyList<string> SystemSchemas => [];

  /// <summary>Gets the name the server insists a primary key has, such as MySQL's <c>PRIMARY</c>, or
  /// <see langword="null"/> where the name is the model's to choose.</summary>
  /// <remarks>The name is taken when the db model is built, so the comparer, which matches by name, finds the key.
  /// </remarks>
  public virtual string? PrimaryKeyName => null;

  /// <summary>Gets the access method the server uses when an index does not name one.</summary>
  /// <remarks>An index that names no type is resolved to this when the db model is built, so both sides of a
  /// comparison hold a concrete type.</remarks>
  public virtual DbIndexType DefaultIndexType => DbIndexType.BTree;

  /// <summary>Gets the access methods the server has beyond its default, which is not listed.</summary>
  /// <remarks>Asking for any other method degrades to the default with a warning. Most servers have one kind of index
  /// and list nothing.</remarks>
  public virtual IReadOnlyList<DbIndexType> IndexTypes => [];

  /// <summary>Gets the access method an index over a geographic point takes, since an ordered index over a point
  /// means nothing; the default where the server has no spatial index.</summary>
  public virtual DbIndexType SpatialIndexType => DbIndexType.Default;

  /// <summary>Gets the access method an index over a vector takes, since an ordered index over a vector finds no
  /// nearest one; the default where the server has no vector index.</summary>
  public virtual DbIndexType VectorIndexType => DbIndexType.Default;

  /// <summary>Decides whether the server can be asked for an access method.</summary>
  /// <param name="type">One of the enumeration values that specifies the access method.</param>
  /// <returns><see langword="true"/> if the method is the default or one the server declares; otherwise,
  /// <see langword="false"/>.</returns>
  public bool SupportsIndexType(DbIndexType type) =>
    type == DbIndexType.Default || type == DefaultIndexType || IndexTypes.Contains(type);

  /// <summary>Gets the longest identifier the server accepts.</summary>
  /// <remarks>PostgreSQL truncates a longer name silently, so a generated name is checked against this when the db
  /// model is built.</remarks>
  public virtual int MaxNameLength => 128;

  /// <summary>Gets the most parameters one command may carry, which decides where a bulk statement is cut.</summary>
  /// <remarks>The limit is on the command, not the statement, so a batch of several statements counts against one
  /// budget. SQL Server's limit is 2100; the common answer is 65535.</remarks>
  public virtual int MaxParameters => 65535;

  /// <summary>Gets the isolation level at which a transaction reads the data as of its own start, with no lock on any
  /// row, which a read lock begins its transaction at.</summary>
  /// <remarks>PostgreSQL and MySQL take their snapshot at the first statement under repeatable read; Oracle needs
  /// serializable; SQL Server needs snapshot isolation, which the database must allow.</remarks>
  public virtual System.Data.IsolationLevel SnapshotIsolationLevel => System.Data.IsolationLevel.RepeatableRead;

  /// <summary>Gets the most rows one multi-row insert may carry, whatever the parameter budget allows.</summary>
  /// <remarks>SQL Server refuses more than 1000 rows in a <c>VALUES</c> clause.</remarks>
  public virtual int MaxRowsPerInsert => 1000;

  // ----- the driver ------------------------------------------------------------------------------------------------

  /// <summary>When overridden in a derived class, creates an unopened connection object for the server.</summary>
  /// <param name="connectionString">The connection string.</param>
  /// <returns>The new connection, not yet opened.</returns>
  public abstract DbConnection CreateConnection(string connectionString);

  /// <summary>When overridden in a derived class, creates a command object with no connection attached.</summary>
  /// <returns>The new command.</returns>
  /// <remarks>The command builders make commands with no connection in hand; the executor attaches one. This is also
  /// a provider's one hook over every command it runs, which Oracle uses to bind parameters by name.</remarks>
  public abstract DbCommand CreateDbCommand();

  // ----- parameters ----------------------------------------------------------------------------------------------

  /// <summary>Gets the prefix a bind parameter is written with in the SQL: an at-sign, or a colon on Oracle.</summary>
  public virtual string ParameterPrefix => "@";

  /// <summary>Adapts a CLR value to what the server's driver accepts: a Guid as bytes where the server stores Guids
  /// as bytes, an enum as its underlying integer, a geographic point as well-known binary, any other value as
  /// itself.</summary>
  /// <param name="value">The value to bind.</param>
  /// <returns>The adapted value.</returns>
  /// <remarks>The enum is converted to its own underlying type, not cast to an int, since the column is of that width.
  /// <see cref="ConvertReadValue"/> undoes the adaptation on the way back.</remarks>
  public virtual object AdaptParameterValue(object value) => value switch {
    Guid guid when Supports(DbFeatures.BinaryGuids) => ConvertHelper.GuidToBytes(guid),
    Enum => Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()),
      System.Globalization.CultureInfo.InvariantCulture),
    GeoPoint point => point.ToWkb(),
    _ => value,
  };

  /// <summary>Serializes the rows of a parameter as the one text value they are bound as, JSON by default, in the
  /// form the dialect's <see cref="SqlDialect.JsonRowsSource"/> reads back.</summary>
  /// <param name="columns">The columns of a row, in order.</param>
  /// <param name="rows">The rows, each an array with one value per column.</param>
  /// <returns>The text.</returns>
  /// <remarks>A provider whose server spreads no JSON array into rows overrides this together with the dialect's
  /// spelling, as Db2's does with XML.</remarks>
  public virtual string SerializeRows(IReadOnlyList<JsonRowsColumn> columns, IEnumerable<object?[]> rows) =>
    Sql.JsonRowsHelper.Serialize(this, columns, rows);

  /// <summary>Converts a value as it came out of a reader into the member's type: a Guid from text or bytes, a
  /// Boolean from a number, an enum from its integer, and an object from JSON text.</summary>
  /// <param name="value">The value the reader returned.</param>
  /// <param name="targetType">The type the member has.</param>
  /// <returns>The converted value.</returns>
  public virtual object? ConvertReadValue(object? value, Type targetType) {
    if (value is string jsonText) {
      var wanted = Nullable.GetUnderlyingType(targetType) ?? targetType;
      if (!wanted.IsScalarType())
        return Runtime.JsonMemberHelper.Deserialize(jsonText, wanted);
    }
    // An array column comes back as an array; a member declared a List takes a List of the items.
    if (value is Array and not byte[] && targetType.IsGenericType
        && targetType.GetGenericTypeDefinition() == typeof(List<>))
      return Activator.CreateInstance(targetType, value);
    return ConvertHelper.ConvertValue(value, targetType);
  }

  /// <summary>Adds a parameter to a command, with its value adapted for the driver.</summary>
  /// <param name="dbCommand">The command.</param>
  /// <param name="name">The parameter's name, without the prefix.</param>
  /// <param name="value">The value to bind.</param>
  /// <returns>The new parameter.</returns>
  public DbParameter AddParameter(DbCommand dbCommand, string name, object value) {
    var parameter = dbCommand.CreateParameter();
    parameter.ParameterName = name;
    SetParameterValue(parameter, AdaptParameterValue(value));
    dbCommand.Parameters.Add(parameter);
    return parameter;
  }

  /// <summary>Asynchronously writes rows into a table over the server's bulk-load channel.</summary>
  /// <param name="connection">The open connection to write on.</param>
  /// <param name="transaction">The transaction the write joins, or <see langword="null"/> for none.</param>
  /// <param name="bulk">The table, the columns and the rows.</param>
  /// <param name="runMode">One of the enumeration values that specifies whether the driver's synchronous or
  /// asynchronous call is made.</param>
  /// <param name="cancellation">The token to monitor for cancellation requests.</param>
  /// <returns>A task that represents the asynchronous operation. The task result is the number of rows written.
  /// </returns>
  /// <exception cref="NotSupportedException">The provider does not declare <see cref="DbFeatures.BulkInsert"/>.
  /// </exception>
  public virtual Task<int> BulkInsertAsync(DbConnection connection, DbTransaction? transaction, BulkInsertInfo bulk,
      RunMode runMode, CancellationToken cancellation) =>
    throw new NotSupportedException($"{ServerKind} has no bulk insert.");

  /// <summary>Puts an adapted value on a parameter: the text of a <see cref="JsonText"/>, the value itself otherwise.
  /// </summary>
  /// <remarks>A provider whose driver has to be told the parameter's type, as for JSON on PostgreSQL, overrides this.
  /// </remarks>
  protected virtual void SetParameterValue(DbParameter parameter, object value) =>
    parameter.Value = value is JsonText json ? json.Text : value;

  // ----- what went wrong ---------------------------------------------------------------------------------------------

  /// <summary>Wraps a driver failure as a classified <see cref="DataAccessException"/>.</summary>
  /// <param name="message">The message.</param>
  /// <param name="inner">The driver's exception, or <see langword="null"/>.</param>
  /// <param name="databaseAlias">The alias of the database the failure is about, or <see langword="null"/>.</param>
  /// <returns>The exception, classified by <see cref="ClassifyException"/> where there is an inner exception.</returns>
  public DataAccessException Fail(string message, Exception? inner, string? databaseAlias = null) {
    var failure = new DataAccessException(message, inner) { DatabaseAlias = databaseAlias };
    if (inner != null)
      ClassifyException(failure, inner);
    return failure;
  }

  /// <summary>Classifies a driver failure, setting the exception's <see cref="DataAccessException.SubType"/> and
  /// <see cref="DataAccessException.ProviderErrorCode"/> from the driver exception's SQLSTATE.</summary>
  /// <param name="failure">The exception to classify.</param>
  /// <param name="inner">The driver's exception.</param>
  /// <remarks>A provider overrides this for a server that reports error numbers instead of SQLSTATE, or to add what
  /// only its own exception type carries, such as the constraint name.</remarks>
  public virtual void ClassifyException(DataAccessException failure, Exception inner) {
    if (inner is not DbException dbException)
      return;
    failure.ProviderErrorCode = dbException.SqlState;
    failure.SubType ??= SubTypeForSqlState(dbException.SqlState);
  }

  /// <summary>Maps a SQLSTATE to one of the <see cref="DataAccessException"/> sub-type constants.</summary>
  /// <param name="sqlState">The SQLSTATE, or <see langword="null"/>.</param>
  /// <returns>The sub-type, or <see langword="null"/> if the state is unknown or empty.</returns>
  public virtual string? SubTypeForSqlState(string? sqlState) => sqlState switch {
    null or "" => null,
    "23505" => DataAccessException.SubTypeUniqueViolation,
    "23503" or "23000" => DataAccessException.SubTypeIntegrityViolation,
    "23502" => DataAccessException.SubTypeNotNullViolation,
    "23514" => DataAccessException.SubTypeCheckViolation,
    "40001" or "40P01" => DataAccessException.SubTypeDeadlock,
    "42501" => DataAccessException.SubTypeAccessDenied,
    // Class 08 is connection exception; 3D and 3F are "no such database / schema", which reach the caller the same way.
    _ when sqlState.StartsWith("08", StringComparison.Ordinal)
        || sqlState.StartsWith("3D", StringComparison.Ordinal)
        || sqlState.StartsWith("3F", StringComparison.Ordinal) => DataAccessException.SubTypeConnectionFailed,
    // Class 42 is syntax error or access rule violation — including "relation does not exist".
    _ when sqlState.StartsWith("42", StringComparison.Ordinal) => DataAccessException.SubTypeSyntaxOrMissingObject,
    // Class 23 is integrity constraint violation; anything else in it is still one.
    _ when sqlState.StartsWith("23", StringComparison.Ordinal) => DataAccessException.SubTypeIntegrityViolation,
    _ => null,
  };

  // ----- what a provider may replace ---------------------------------------------------------------------------------

  /// <summary>Creates the builder of insert, update and delete commands for a db model.</summary>
  /// <param name="dbModel">The db model the commands are written for.</param>
  /// <returns>A new builder.</returns>
  /// <remarks>One per save: the builder carries the batch's state, so a shared instance would mix two saves'
  /// generated-id slots.</remarks>
  public virtual RecordCommandBuilder CreateRecordCommandBuilder(DbModel dbModel) => new(dbModel);

  /// <summary>Creates the comparer of two db models.</summary>
  /// <returns>A new comparer.</returns>
  /// <remarks>A provider overrides this where its server reports objects in a way the base comparison does not
  /// expect.</remarks>
  public virtual DbModelComparer CreateDbModelComparer() => new(this);

  /// <summary>Creates the loader that reads a database's schema into a db model.</summary>
  /// <param name="settings">The settings of the database to read.</param>
  /// <returns>A new loader.</returns>
  public virtual DbModelLoader CreateDbModelLoader(DbSettings settings) => new(settings);

  public override string ToString() => $"{GetType().Name} ({ServerKind})";
}
