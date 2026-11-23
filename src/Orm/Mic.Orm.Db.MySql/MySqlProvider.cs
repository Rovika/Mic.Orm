using System.Data.Common;
using Mic.Common;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using MySqlConnector;

namespace Mic.Orm.Db.MySql;

/// <summary>Provides the MySQL database provider: its feature set, its SQL dialect, its type registry and the
/// MySqlConnector connections and commands.</summary>
/// <remarks>InnoDB indexes every foreign key and names every primary key <c>PRIMARY</c> itself; the model and the
/// model loader recognise both rather than report them as differences.</remarks>
public class MySqlProvider : DbProvider {
  public MySqlProvider(GuidScheme guidScheme = GuidScheme.V7)
    : base(DbServerKind.MySql, new MySqlSqlDialect(), BuildTypeRegistry(), guidScheme) { }

  public override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

  public override DbCommand CreateDbCommand() => new MySqlCommand();

  /// <summary>Gets the features MySQL supports.</summary>
  /// <remarks>Not included: <see cref="DbFeatures.HeapTables"/>, since InnoDB always clusters on the primary key;
  /// <see cref="DbFeatures.FilterInIndexes"/>, since there are no partial indexes, which the model builder reports as
  /// an error because a unique filtered index is a constraint; <see cref="DbFeatures.IncludeColumnsInIndexes"/>;
  /// <see cref="DbFeatures.Sequences"/>, since <c>AUTO_INCREMENT</c> belongs to a column; and
  /// <see cref="DbFeatures.ClusteringMarker"/>, <see cref="DbFeatures.DeferredConstraintCheck"/> and
  /// <see cref="DbFeatures.ServerPreservesComments"/>.</remarks>
  public override DbFeatures Features =>
    DbFeatures.Schemas                         // a schema is a database
    | DbFeatures.ReferentialConstraints
    | DbFeatures.JsonRows
    | DbFeatures.NamedRefConstraints
    | DbFeatures.ClusteredStorage              // InnoDB: the primary key is the table's physical order
    | DbFeatures.OrderedColumnsInIndexes       // DESC indexes, since 8.0
    | DbFeatures.Views
    | DbFeatures.IdentityColumns               // AUTO_INCREMENT
    | DbFeatures.BulkInsertAdvancesIdentity
    | DbFeatures.IdentityRequiresInlineKey     // only on a column keyed at creation
    | DbFeatures.ForeignKeysAutoIndexed        // InnoDB makes one for every foreign key
    | DbFeatures.FullAlterTable
    | DbFeatures.AlterColumnRestatesDefinition // MODIFY replaces the column rather than amending it
    | DbFeatures.SessionLocks                  // GET_LOCK / RELEASE_LOCK
    | DbFeatures.BinaryGuids                    // binary(16), since there is no uuid type
    | DbFeatures.FullText                      // a FULLTEXT index, matched with MATCH … AGAINST
    | DbFeatures.IdentityCarriedInBatch       // LAST_INSERT_ID() into a session variable
    | DbFeatures.RowLocks                      // FOR UPDATE on the root select; REPEATABLE READ for a consistent read
    | DbFeatures.ParameterPrefixSharedWithVariables    // @total is a user variable, written like a parameter
    | DbFeatures.DeleteSubQueryWrappedMySql            // a sub-query may not read the delete's table (error 1093)
    | DbFeatures.CountsJsonRowsForExistsMySql          // EXISTS over a JSON_TABLE off the outer row answers nothing
    | DbFeatures.SpatialIndexNeedsNotNullMySql;        // "All parts of a SPATIAL index must be NOT NULL"

  /// <summary>Gets the case the server folds an unquoted identifier to.</summary>
  /// <remarks>A table name folds to lower case on a server configured that way and is preserved on another, so
  /// nothing is assumed. Everything MIC writes is quoted, and names are matched case-insensitively.</remarks>
  public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Preserve;

  /// <summary>Gets the default schema, which is empty: MySQL has no schema within a database, <c>SCHEMA</c> being a
  /// synonym for <c>DATABASE</c>, so the schema is whichever database the application names.</summary>
  public override string DefaultSchema => string.Empty;

  /// <summary>Gets the name every primary key gets in the model, <c>PRIMARY</c>, which is what MySQL calls every
  /// primary key whatever the CREATE statement says.</summary>
  /// <remarks>The name is taken at mapping time. A model that called the key anything else would see the comparer,
  /// which matches by name, report a drop and an add on every startup.</remarks>
  public override string? PrimaryKeyName => "PRIMARY";

  public override IReadOnlyList<string> SystemSchemas =>
    ["information_schema", "mysql", "performance_schema", "sys"];

  /// <summary>Gets the index types MySQL offers beyond the b-tree: <c>FULLTEXT</c> and <c>SPATIAL</c>.</summary>
  /// <remarks>Hash is not listed: MySQL accepts <c>USING HASH</c> on an InnoDB table and silently creates a b-tree,
  /// which introspection then reads back as btree. Only MEMORY tables have hash indexes, and MIC does not model a
  /// storage engine.</remarks>
  public override IReadOnlyList<DbIndexType> IndexTypes => [DbIndexType.FullText, DbIndexType.Spatial];

  public override DbIndexType SpatialIndexType => DbIndexType.Spatial;

  /// <summary>Gets the longest identifier name the server accepts.</summary>
  /// <remarks>The limit is 64 characters. MySQL rejects a longer name rather than truncating it.</remarks>
  public override int MaxNameLength => 64;

  public override DbModelLoader CreateDbModelLoader(DbSettings settings) => new MySqlModelLoader(settings);

  /// <summary>Classifies a failure from MySQL's own error number, which replaces the class-level answer the SQLSTATE
  /// gives.</summary>
  /// <remarks>MySQL reports SQLSTATE <c>23000</c> for a duplicate key, a missing parent row and a null in a NOT NULL
  /// column alike, so the error number decides the sub-type.</remarks>
  /// <param name="failure">The failure to classify.</param>
  /// <param name="inner">The exception the driver threw.</param>
  public override void ClassifyException(DataAccessException failure, Exception inner) {
    base.ClassifyException(failure, inner);
    if (inner is MySqlException my)
      failure.SubType = (MySqlErrorCode)my.Number switch {
        MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.DuplicateKey =>
          DataAccessException.SubTypeUniqueViolation,
        MySqlErrorCode.NoReferencedRow or MySqlErrorCode.NoReferencedRow2 or MySqlErrorCode.RowIsReferenced
          or MySqlErrorCode.RowIsReferenced2 => DataAccessException.SubTypeIntegrityViolation,
        // NoDefaultForField is what a strict server says when a required column is left out of an INSERT;
        // ColumnCannotBeNull is only for an explicit NULL.
        MySqlErrorCode.ColumnCannotBeNull or MySqlErrorCode.NoDefaultForField =>
          DataAccessException.SubTypeNotNullViolation,
        MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout => DataAccessException.SubTypeDeadlock,
        MySqlErrorCode.NoSuchTable or MySqlErrorCode.ParseError or MySqlErrorCode.BadFieldError =>
          DataAccessException.SubTypeSyntaxOrMissingObject,
        _ => failure.SubType,      // nothing recognised: keep the SQLSTATE class's answer
      };
  }

  private static DbTypeRegistry BuildTypeRegistry() {
    var registry = new DbTypeRegistry();
    // Text. longtext is the unlimited one, of 4 GB; text holds 65,535 bytes and is read as the same type, so a
    // column made as text stays as it is. varchar carries a length that MySQL enforces in bytes, not characters,
    // which the model has no way to state.
    registry.Add("varchar", typeof(string), DbTypeFlags.TakesSize, aliases: "char", specialType: DbSpecialType.String);
    registry.Add("longtext", typeof(string), DbTypeFlags.Unlimited,
      aliases: "mediumtext, text, tinytext", specialType: DbSpecialType.StringUnlimited);
    // A JSON member is the native json type, validated on write, which the JSON functions read without a cast.
    registry.Add("json", typeof(string), DbTypeFlags.Unlimited, specialType: DbSpecialType.Json, columnInit: "'{}'");

    registry.Add("int", typeof(int), aliases: "integer, mediumint");
    registry.Add("bigint", typeof(long));
    registry.Add("smallint", typeof(short));
    registry.Add("decimal", typeof(decimal), DbTypeFlags.TakesPrecisionScale,
      aliases: "numeric", defaultPrecision: 18, defaultScale: 4);
    registry.Add("float", typeof(float));
    registry.Add("double", typeof(double));

    // No boolean type: bool is an alias for tinyint(1), and information_schema reports tinyint.
    registry.Add("tinyint", typeof(bool), aliases: "bool, boolean", columnInit: "0");

    // No Guid type either, so a Guid is 16 bytes rather than 36 characters: a Guid key sits on every primary and
    // foreign key, so the difference is paid on every index. binary is its own type rather than an alias of blob,
    // which is what lets a fixed 16 be asked for; varbinary stays with blob, where a maximum length belongs.
    registry.Add("binary", typeof(Guid), DbTypeFlags.TakesSize, defaultSize: 16,
      columnInit: "x'00000000000000000000000000000000'");

    registry.Add("datetime", typeof(DateTime));
    registry.Add("timestamp", typeof(DateTimeOffset));
    registry.Add("date", typeof(DateOnly));
    registry.Add("time", typeof(TimeOnly), aliases: "interval");

    // longblob for the reason longtext is the unlimited text. varbinary is the bounded binary type, for a byte[] that
    // says its size: it indexes whole, which a blob does not.
    registry.Add("longblob", typeof(byte[]), DbTypeFlags.Unlimited,
      aliases: "mediumblob, blob, tinyblob", specialType: DbSpecialType.BinaryUnlimited);
    registry.Add("varbinary", typeof(byte[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Binary);

    // A point restricted to its SRID, which is what a model declares. The loader spells the SRID the catalog reports
    // into the type, so a column with no SRID is the bare `point`: a type of its own, read and never chosen, and one
    // with another SRID is a type nothing maps.
    registry.Add("point SRID 4326", typeof(GeoPoint));
    registry.Add("point", typeof(GeoPoint));
    return registry;
  }
}
