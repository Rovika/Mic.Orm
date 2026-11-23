using Mic.Common;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Oracle;

/// <summary>Provides the Oracle SQL dialect.</summary>
/// <remarks>A schema is a user, a batch is an anonymous PL/SQL block, and a session lock is a <c>DBMS_LOCK</c> call.
/// Full-text search is Oracle Text's <c>CONTEXT</c> index, matched with <c>CONTAINS</c> and ranked with
/// <c>SCORE</c>.</remarks>
public class OracleSqlDialect : SqlDialect {
  public OracleSqlDialect() {
    // No AS before a source alias, a function's included.
    TableFunctionSource = "{0}({1}) {2}";
    // A SQL macro called over the outer row, by CROSS APPLY, LATERAL or a left-correlated comma join alike, ends the
    // server process on Oracle 23 Free (ORA-03113), so the call is refused rather than written.
    LateralJoin = null;
    // A Boolean column is a number. A condition is a BOOLEAN from 23 on, which a CASE refuses beside a number
    // (ORA-00932), and no value before 23.
    ConditionAsValue = "CASE WHEN {0} THEN 1 ELSE 0 END";
    // The driver binds bytes as RAW, which SDO_UTIL wants as a BLOB; the server's own binary comes back big-endian,
    // and a coordinate, held as a NUMBER, comes back within an ulp of the double that went in.
    GeoPointRead = "SDO_UTIL.TO_WKBGEOMETRY({0})";
    GeoPointWrite = "SDO_UTIL.FROM_WKBGEOMETRY(TO_BLOB({0}), 4326)";
    // Metres on the spheroid; the tolerance is the usual 5 mm for geodetic data.
    FunctionTemplates[NamedOpType.GeoDistance] = "SDO_GEOM.SDO_DISTANCE({0}, {1}, 0.005, 'unit=M')";
    // A private temporary table, 18c and on: its name carries the server's prefix, and it lives as long as the session
    // rather than the transaction.
    CreateTempTable = "CREATE PRIVATE TEMPORARY TABLE {0} (\n{1}\n) ON COMMIT PRESERVE DEFINITION";
    // A computed column is a virtual column; there is no stored kind.
    ComputedStorage = ComputedStorage.Virtual;
    // No type on a virtual column: the expression gives one, and a declared type JSON_VALUE's 4000 characters would not
    // fit refuses the column.
    ComputedColumnSpec = "{0} GENERATED ALWAYS AS ({2}){3}";
    // A view takes a table comment, which an upgrade reads without the view's select.
    ViewHashCarrier = ViewHashCarrier.Comment;
    CommentOnView = "COMMENT ON TABLE {0} IS {1}";
    RefreshMaterializedView = "BEGIN DBMS_MVIEW.REFRESH('{0}'); END;";
    // No multi-row VALUES: INSERT ALL repeats the table and the column list on every row and needs a trailing SELECT
    // from dual to be a statement.
    InsertMany = "INSERT ALL\n{2}\n  SELECT * FROM dual";

    // Every SELECT needs a FROM, so a query that reads no table reads dual.
    SelectOne = "SELECT 1 FROM dual";
    SelectValue = "SELECT {0} FROM dual";
    SequenceNextValues = "SELECT {0}.NEXTVAL FROM dual CONNECT BY LEVEL <= {1}";
    SelectServerTimeUtc = "SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) FROM dual";
    SelectServerUser = "SELECT USER FROM dual";
    SelectServerDatabase = "SELECT SYS_CONTEXT('USERENV', 'DB_NAME') FROM dual";

    // No AS before a table alias in a delete or an update (ORA-00933).
    DeleteFrom = "DELETE FROM {0} {1}";
    UpdateWhere = "UPDATE {0} {1}\n  SET {2}";
    // No UPDATE … FROM: the values of a query are written onto a table through MERGE.
    UpdateFrom = "MERGE INTO {0} {1}\n  USING {3} {4}\n  ON ({5})\n  WHEN MATCHED THEN UPDATE SET {2}";

    // The ANSI form, since 12c.
    PageSkipTake = "{0} OFFSET {1} ROWS FETCH NEXT {2} ROWS ONLY";
    PageTakeOnly = "{0} OFFSET 0 ROWS FETCH NEXT {2} ROWS ONLY";
    PageSkipOnly = "{0} OFFSET {1} ROWS";

    // Recursion is worked out from the definition itself; there is no word for it.
    WithRecursive = "WITH";
    // Without this the server raises ORA-32044 on a cycle in the data.
    RecursiveCteSuffix = " CYCLE {0} SET \"__cyc\" TO 1 DEFAULT 0";

    SelectForUpdate = "\n  FOR UPDATE";
    SelectForUpdateSkipLocked = "\n  FOR UPDATE SKIP LOCKED";

    // No statement list: a batch is an anonymous PL/SQL block, where the transaction is implicit, so only the COMMIT
    // is written and the block rolls back on an unhandled exception.
    BatchBegin = "BEGIN\n";
    BatchEnd = "END;";
    BatchBeginTransaction = "";
    BatchCommitTransaction = "COMMIT;";
    InsertManyRow = "    INTO {0} ({1}) VALUES ({2})";
    InsertManyRowSeparator = "\n";

    // A schema is a user; CREATE SCHEMA exists in Oracle and creates nothing. NO AUTHENTICATION makes the user a
    // namespace rather than a login: the application connects as itself and qualifies its names.
    // The grant lets the user own the table a materialized view keeps its rows in, which Oracle creates as the schema's
    // user however privileged the creator is.
    // CREATE SEQUENCE is for a spatial index, whose R-tree is built as sequences and tables of the schema's user. The
    // INHERIT PRIVILEGES grant lets a function of the schema's run for the connected user, which a privileged user
    // such as SYSTEM withholds from everyone by default.
    CreateSchema = "BEGIN EXECUTE IMMEDIATE 'CREATE USER {0} NO AUTHENTICATION QUOTA UNLIMITED ON USERS'; "
      + "EXECUTE IMMEDIATE 'GRANT CREATE TABLE, CREATE MATERIALIZED VIEW, CREATE SEQUENCE TO {0}'; "
      + "EXECUTE IMMEDIATE 'GRANT INHERIT PRIVILEGES ON USER ' || USER || ' TO {0}'; END;";
    DropSchema = "DROP USER {0} CASCADE";

    // MODIFY, with the parentheses required, amends rather than restates, so the base's separate statements are
    // correct and the column keeps what is not mentioned.
    AlterColumnType = "ALTER TABLE {0} MODIFY ({1} {2})";
    SetNotNull = "ALTER TABLE {0} MODIFY ({1} NOT NULL)";
    DropNotNull = "ALTER TABLE {0} MODIFY ({1} NULL)";

    // A default is part of the column and goes by being set to nothing; there is no DROP DEFAULT.
    SetDefault = "ALTER TABLE {0} MODIFY ({1} DEFAULT {2})";
    DropDefault = "ALTER TABLE {0} MODIFY ({1} DEFAULT NULL)";

    AddColumn = "ALTER TABLE {0} ADD ({1})";

    // An index must be qualified: an unqualified index name does not follow its table, and Oracle creates the index
    // in the connected user's schema and reports success. {4} is the access-method prefix (CREATE BITMAP INDEX),
    // {5} the clause a domain index ends with (INDEXTYPE IS …).
    CreateIndex = "CREATE {4}INDEX {3} ON {1} ({2}){5}";
    CreateUniqueIndex = "CREATE UNIQUE {4}INDEX {3} ON {1} ({2}){5}";

    // The identity clause comes before NOT NULL; the other order is ORA-03076.
    ColumnModifiers = "{2}{1}{0}";

    // CASCADE CONSTRAINTS because a foreign key pointing at this table would otherwise refuse the drop, and PURGE
    // because without it the table goes to the recycle bin and its name stays taken.
    DropTable = "DROP TABLE {0} CASCADE CONSTRAINTS PURGE";

    RenameTable = "ALTER TABLE {0} RENAME TO {1}";
    RenameColumn = "ALTER TABLE {0} RENAME COLUMN {1} TO {2}";

    // DBMS_LOCK's id-based REQUEST is a function that returns its status, so it fits in a SELECT; the name-based one
    // needs ALLOCATE_UNIQUE first, a PL/SQL block that returns nothing to ExecuteScalar. Status 0 is granted and 4 is
    // already held by this session. MOD/ABS because the id must fit in 0 .. 2^30-1 and the key is a 64-bit hash; a
    // collision only makes two applications wait on each other for a moment. release_on_commit stays FALSE: every
    // DDL statement commits implicitly, and TRUE would drop the lock at the first CREATE TABLE. Needs EXECUTE on
    // DBMS_LOCK, a grant a deployment has to make; without it the request fails with ORA-00904 and the upgrade runs
    // uncoordinated, with a warning naming the grant.
    TryAcquireSessionLock =
      "SELECT CASE WHEN DBMS_LOCK.REQUEST(MOD(ABS({0}), 1073741823), 6, 0) IN (0, 4) THEN 1 ELSE 0 END FROM DUAL";
    ReleaseSessionLock = "SELECT DBMS_LOCK.RELEASE(MOD(ABS({0}), 1073741823)) FROM DUAL";
    SessionLockGrantHint = "GRANT EXECUTE ON DBMS_LOCK TO the application's user";

    FunctionTemplates[NamedOpType.Substring] = "SUBSTR({0}, {1}, {2})";

    // CEIL, not CEILING.
    FunctionTemplates[NamedOpType.Ceiling] = "CEIL({0})";

    // A DATE already carries a time, so dropping it is a truncation: CAST(x AS DATE) keeps the time of day.
    FunctionTemplates[NamedOpType.Date] = "TRUNC({0})";

    // No % operator.
    FunctionTemplates[NamedOpType.Modulo] = "MOD({0}, {1})";

    // No POSITION; INSTR takes the haystack first.
    FunctionTemplates[NamedOpType.IndexOf] = "INSTR({0}, {1})";
  }

  /// <inheritdoc/>
  /// <remarks>A private temporary table's name must start with the server's prefix, <c>ORA$PTT_</c> by default.</remarks>
  public override string GetTempTableName(string name) => "ORA$PTT_" + name;

  /// <summary>Builds a SQL macro returning a table: a function whose text the server inlines at the call, replaced
  /// in place; the parameters typed without a length, since PL/SQL takes none on a parameter, and no parentheses
  /// where there are none.</summary>
  /// <inheritdoc/>
  public override string? BuildTableFunction(DbTableInfo table, string body) {
    var function = table.TableFunction!;
    var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name} {StripLength(p.TypeSpec)}"));
    var signature = parameters.Length == 0 ? "" : $"({parameters})";
    return $"CREATE OR REPLACE FUNCTION {table.RefName}{signature} RETURN VARCHAR2 SQL_MACRO(TABLE) IS\nBEGIN\n"
      + $"  RETURN q'[\n{body}\n]';\nEND;";
  }

  private static string StripLength(string typeSpec) {
    var open = typeSpec.IndexOf('(');
    return open < 0 ? typeSpec : typeSpec[..open];
  }

  /// <summary>Gets the keyword of a set operation; Oracle spells <c>EXCEPT</c> as <c>MINUS</c>.</summary>
  /// <param name="opType">One of the enumeration values that specifies the set operation.</param>
  /// <returns>The keyword.</returns>
  public override string GetSetOperator(SetOperationType opType) =>
    opType == SetOperationType.Except ? "MINUS" : base.GetSetOperator(opType);

  /// <summary>Gets the prefix of an index type, <c>BITMAP</c> before <c>INDEX</c>; there is no clause form.</summary>
  /// <param name="type">One of the enumeration values that specifies the index type.</param>
  /// <returns>The prefix and an empty clause.</returns>
  public override (string Prefix, string Clause) IndexTypeSyntax(DbIndexType type) => type switch {
    DbIndexType.Bitmap => ("BITMAP ", ""),
    DbIndexType.Spatial => ("", " INDEXTYPE IS MDSYS.SPATIAL_INDEX"),
    _ => ("", ""),
  };

  /// <summary>Builds the registration of the index's column in Oracle Spatial's metadata, which a spatial index
  /// wants before it is built: the whole globe in degrees at a 5 mm tolerance, SRID 4326, under the table's owner,
  /// the names quoted as the table declares them.</summary>
  /// <inheritdoc/>
  public override IReadOnlyList<string>? BuildSpatialIndexPrerequisites(DbKeyInfo index) {
    var (owner, tableName, columnName) = GetSpatialMetadataKey(index);
    return [
      $"MERGE INTO MDSYS.SDO_GEOM_METADATA_TABLE m\n"
      + $"USING (SELECT {owner} AS sdo_owner, {tableName} AS sdo_table_name, {columnName} AS sdo_column_name FROM dual) s\n"
      + "ON (m.SDO_OWNER = s.sdo_owner AND m.SDO_TABLE_NAME = s.sdo_table_name AND m.SDO_COLUMN_NAME = s.sdo_column_name)\n"
      + "WHEN NOT MATCHED THEN INSERT (SDO_OWNER, SDO_TABLE_NAME, SDO_COLUMN_NAME, SDO_DIMINFO, SDO_SRID)\n"
      + "VALUES (s.sdo_owner, s.sdo_table_name, s.sdo_column_name, MDSYS.SDO_DIM_ARRAY("
      + "MDSYS.SDO_DIM_ELEMENT('X', -180, 180, 0.005), MDSYS.SDO_DIM_ELEMENT('Y', -90, 90, 0.005)), 4326)",
    ];
  }

  /// <summary>Builds the removal of the column's registration in Oracle Spatial's metadata, which the server keeps
  /// when the index, the column or the table is dropped.</summary>
  /// <inheritdoc/>
  public override IReadOnlyList<string>? BuildSpatialIndexCleanup(DbKeyInfo index) {
    var (owner, tableName, columnName) = GetSpatialMetadataKey(index);
    return [
      "DELETE FROM MDSYS.SDO_GEOM_METADATA_TABLE\n"
      + $"WHERE SDO_OWNER = {owner} AND SDO_TABLE_NAME = {tableName} AND SDO_COLUMN_NAME = {columnName}",
    ];
  }

  /// <summary>Gets the three values that name a column in Oracle Spatial's metadata, as literals: the table's owner,
  /// and the table and the column quoted as the table declares them.</summary>
  private (string Owner, string Table, string Column) GetSpatialMetadataKey(DbKeyInfo index) {
    var table = index.Table;
    var owner = table.Schema.Name.Length > 0 ? GetLiteral(table.Schema.Name) : "USER";
    return (owner, GetLiteral(Quote(table.Name)), GetLiteral(index.Columns[0].Column.QuotedName));
  }

  /// <summary>Builds a Boolean literal as <c>0</c> or <c>1</c>; Oracle has no Boolean type in SQL, and a bool column
  /// is <c>number(1)</c>.</summary>
  /// <param name="value"><see langword="true"/> for the true literal; otherwise, <see langword="false"/>.</param>
  /// <returns>The literal.</returns>
  public override string BooleanLiteral(bool value) => value ? "1" : "0";

  // ----- full-text search: Oracle Text's CONTEXT index, CONTAINS and SCORE ----------------------------------------------

  /// <summary>Builds the match predicate, <c>CONTAINS(alias.column, query, label) &gt; 0</c>, naming the column the
  /// index is on; the datastore brings the rest.</summary>
  /// <remarks>There is one label per query: Oracle refuses two <c>CONTAINS</c> with the same label in one statement,
  /// and <c>SCORE</c> names its match by it.</remarks>
  /// <param name="alias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The predicate.</returns>
  public override string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"CONTAINS({alias}.{index.Columns[0].Column.QuotedName}, {query}, {label}) > 0";

  /// <summary>Builds the rank expression, <c>SCORE(label)</c>, of the <c>CONTAINS</c> with that label.</summary>
  /// <remarks>The statement must hold that match; a rank with no match of the same query is refused by Oracle
  /// (ORA-29908).</remarks>
  /// <param name="alias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The rank expression.</returns>
  public override string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"SCORE({label})";

  /// <summary>Builds the Oracle Text query from the search words for a match kind.</summary>
  /// <remarks>Every word is braced (<c>{steel}</c>), since a bare word that is an operator (<c>and</c>, <c>near</c>,
  /// <c>within</c>, <c>about</c>) is read as one; a prefix goes bare, since braces would make the <c>%</c> a
  /// character. Any word is ACCUM (<c>a, b</c>) rather than <c>OR</c>: both answer the rows holding any of the words,
  /// but <c>OR</c> scores a row by its best word alone, where ACCUM scores more words matched higher.
  /// <c>NEAR((a, b), N)</c> counts the words between, where the distance given counts the positions apart, so one is
  /// taken off; it takes any number of words, unordered.</remarks>
  /// <param name="words">The words to search for.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">The largest distance between the words, for a match within a distance.</param>
  /// <returns>The query text, an empty string when no word remains after cleaning, or <see langword="null"/> for a
  /// kind this dialect does not know.</returns>
  public override string? BuildTextQuery(string words, TextMatchKind kind, int distance) {
    var cleaned = SplitTextWords(words);
    if (cleaned.Length == 0)
      return "";
    var braced = cleaned.Select(w => "{" + w + "}").ToArray();
    return kind switch {
      TextMatchKind.AllWords => string.Join(" AND ", braced),
      TextMatchKind.AnyWord => string.Join(", ", braced),
      TextMatchKind.Phrase => string.Join(" ", braced),
      TextMatchKind.Prefix => string.Join(" AND ", cleaned.Select(w => w + "%")),
      TextMatchKind.Near => cleaned.Length == 1 ? braced[0] : $"NEAR(({string.Join(", ", braced)}), {Math.Max(distance, 1) - 1})",
      _ => null,
    };
  }

  /// <summary>Builds the statements that create a full-text index: the grant the index's owner needs, the datastore
  /// preference where the index is over more than one column, and the <c>CONTEXT</c> index itself.</summary>
  /// <remarks>A <c>CONTEXT</c> index is a domain index over one column, the one <c>CONTAINS</c> names; a multi-column
  /// index reads the rest through a <c>MULTI_COLUMN_DATASTORE</c> preference made by <c>CTX_DDL</c>, whose name is a
  /// hash of the index's since a preference name is capped at 30 bytes. <c>SYNC (ON COMMIT)</c> keeps the index in
  /// step with the rows at every commit. A deployment grants the connecting user <c>CTXAPP</c> (or EXECUTE on
  /// <c>CTXSYS.CTX_DDL</c>) to make the preference and SELECT on <c>CTXSYS.CTX_INDEX_VALUES</c> for the model loader
  /// to read the columns back; the index's owner needs CREATE TABLE, which the first statement grants, since the
  /// cartridge creates the index's <c>DR$</c> tables under it.</remarks>
  /// <param name="index">The full-text index to create.</param>
  /// <returns>The statements, in the order they run.</returns>
  public override IReadOnlyList<string>? BuildFullTextIndex(DbKeyInfo index) {
    var statements = new List<string>();
    var schema = index.Table.Schema.Name;
    if (schema.Length > 0)
      statements.Add($"GRANT CREATE TABLE TO {Quote(schema)}");
    var parameters = "SYNC (ON COMMIT)";
    if (index.Columns.Count > 1) {
      var preference = GetDatastoreName(index);
      var columns = string.Join(", ", index.Columns.Select(c => c.Column.QuotedName)).Replace("'", "''");
      // Dropped first in case a previous index left it: a preference outlives its index.
      statements.Add($"BEGIN\n  BEGIN CTXSYS.CTX_DDL.drop_preference('{preference}'); EXCEPTION WHEN OTHERS THEN NULL; END;\n"
        + $"  CTXSYS.CTX_DDL.create_preference('{preference}', 'MULTI_COLUMN_DATASTORE');\n"
        + $"  CTXSYS.CTX_DDL.set_attribute('{preference}', 'COLUMNS', '{columns}');\nEND;");
      parameters = $"DATASTORE {preference} " + parameters;
    }
    statements.Add($"CREATE INDEX {QualifiedName(schema, index.Name)} ON {index.Table.RefName} ({index.Columns[0].Column.QuotedName})"
      + $" INDEXTYPE IS CTXSYS.CONTEXT PARAMETERS ('{parameters}')");
    return statements;
  }

  public override IReadOnlyList<string>? BuildDropFullTextIndex(DbKeyInfo index) {
    var statements = new List<string> { string.Format(DropIndex, QualifiedName(index.Table.Schema.Name, index.Name), index.Table.RefName) };
    if (index.Columns.Count > 1)
      statements.Add($"BEGIN CTXSYS.CTX_DDL.drop_preference('{GetDatastoreName(index)}'); EXCEPTION WHEN OTHERS THEN NULL; END;");
    return statements;
  }

  /// <summary>Gets the name of the datastore preference, owned by the index's schema: a hash of the index's name,
  /// since a preference name is capped at 30 bytes.</summary>
  private string GetDatastoreName(DbKeyInfo index) {
    var name = "MIC_DS_" + Fnv1aHash.ComputeHex(index.Name);
    var schema = index.Table.Schema.Name;
    return schema.Length > 0 ? $"{Quote(schema)}.{name}" : name;
  }

  /// <summary>Builds a binary literal as <c>HEXTORAW</c> of the hex string; <c>raw</c> takes neither <c>X'…'</c> nor
  /// <c>0x…</c>.</summary>
  /// <param name="value">The bytes to spell.</param>
  /// <returns>The literal.</returns>
  public override string BinaryLiteral(byte[] value) => "HEXTORAW('" + Convert.ToHexString(value) + "')";

  /// <summary>Builds a date-time literal in the ANSI <c>TIMESTAMP '…'</c> form, which is unambiguous whatever the
  /// session's <c>NLS_DATE_FORMAT</c> says.</summary>
  /// <remarks>A bare quoted string is read against <c>NLS_DATE_FORMAT</c>, which belongs to the session, so the same
  /// literal would mean different instants on two connections.</remarks>
  /// <param name="value">The instant to spell.</param>
  /// <returns>The literal.</returns>
  public override string DateTimeLiteral(DateTime value) =>
    "TIMESTAMP '" + value.ToString("yyyy-MM-dd HH:mm:ss.fff", Invariant) + "'";

  /// <summary>Builds a literal, spelling a <c>DateOnly</c> in the <c>DATE '…'</c> form so the format is stated rather
  /// than inherited from the session.</summary>
  /// <param name="value">The value to spell.</param>
  /// <returns>The literal.</returns>
  public override string? GetLiteral(object? value) => value is DateOnly date
    ? "DATE '" + date.ToString("yyyy-MM-dd", Invariant) + "'"
    : base.GetLiteral(value);

  /// <summary>Builds the expression that reads the value at a path with <c>JSON_VALUE</c>, typed with
  /// <c>RETURNING</c> when a type is given.</summary>
  /// <remarks><c>RETURNING</c> parses a number, a timestamp or a date from the document's ISO 8601 text, where a
  /// <c>CAST</c> of the text would want an NLS format. A Boolean is the text compared with <c>true</c>, answering the
  /// number a Boolean column holds.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="path">The expression that yields the path.</param>
  /// <param name="type">The type the value is read as, or <see langword="null"/> to read the text itself.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="NotSupportedException">a JSON value cannot be read as the type on Oracle.</exception>
  public override string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    var text = $"JSON_VALUE({document}, {path})";
    if (type == null)
      return text;
    var clrType = Nullable.GetUnderlyingType(type.TypeDef.ClrType) ?? type.TypeDef.ClrType;
    if (clrType == typeof(bool))
      return $"CASE WHEN {text} = 'true' THEN 1 ELSE 0 END";
    if (clrType == typeof(string))
      return text;
    var returning = clrType == typeof(DateTime) ? "TIMESTAMP"
      : clrType == typeof(DateOnly) ? "DATE"
      : clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float)
        || clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short) || clrType == typeof(byte) ? "NUMBER"
      : throw new NotSupportedException($"A JSON value cannot be read as '{clrType.Name}' on Oracle.");
    return $"JSON_VALUE({document}, {path} RETURNING {returning})";
  }

  /// <summary>Builds the table source for the rows of a JSON document, a bare <c>JSON_TABLE</c> with the columns
  /// declared.</summary>
  /// <remarks>A bare <c>JSON_TABLE</c> may read a column of the row it stands beside, which an inline view around it
  /// could not. The COLUMNS clause takes NUMBER, VARCHAR2, DATE and TIMESTAMP: a Guid is declared as its hex text in
  /// <c>VARCHAR2(32)</c>, a timestamp without a precision, and a string as <c>VARCHAR2(4000)</c>.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="columns">The columns of the rows.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  public override string JsonRowsSource(string document, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    static string Declared(JsonRowsColumn column) =>
      column.Type.ClrType == typeof(Guid) ? "VARCHAR2(32)"
      : column.Type.ClrType == typeof(DateTime) ? "TIMESTAMP"
      : column.Type.ClrType == typeof(string) ? "VARCHAR2(4000)"
      : column.Type.TypeSpec;
    // The element itself is at the path $: an array of scalars, one column.
    var declared = string.Join(", ", columns.Select(c => $"{Quote(c.Name)} {Declared(c)} PATH {(c.IsElement ? "'$'" : $"'$.\"{c.Name}\"'")}"));
    return $"JSON_TABLE({document}, '$[*]' COLUMNS ({declared})) {alias}";
  }

  /// <inheritdoc/>
  /// <remarks>The columns as <c>JSON_TABLE</c> declares them: a Guid as the hex text <c>HEXTORAW</c> reads, a
  /// <c>DateTime</c> as a timestamp, a string as <c>VARCHAR2(4000)</c>.</remarks>
  public override IReadOnlyList<JsonRowsTempColumn> GetJsonRowsTempColumns(IReadOnlyList<JsonRowsColumn> columns) =>
    [.. columns.Select(c =>
      c.Type.ClrType == typeof(Guid) ? new JsonRowsTempColumn(c.Name, "VARCHAR2(32)", RowsTempValueForm.GuidText)
      : c.Type.ClrType == typeof(DateTime) ? new JsonRowsTempColumn(c.Name, "TIMESTAMP", RowsTempValueForm.Value)
      : c.Type.ClrType == typeof(string) ? new JsonRowsTempColumn(c.Name, "VARCHAR2(4000)", RowsTempValueForm.Value)
      : new JsonRowsTempColumn(c.Name, c.Type.TypeSpec, RowsTempValueForm.Value))];

  /// <inheritdoc/>
  /// <remarks>Oracle takes no <c>AS</c> before a table alias.</remarks>
  public override string JsonRowsTempSource(string tableRef, string alias) => $"{tableRef} {alias}";

  /// <summary>Builds the reference to a column of a JSON rows source, turning a Guid back from its hex text with
  /// <c>HEXTORAW</c>.</summary>
  /// <remarks>A Guid is <c>raw(16)</c>, which cannot be read out of JSON text.</remarks>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="column">The column to reference.</param>
  /// <returns>The column expression.</returns>
  public override string JsonRowsColumn(string alias, JsonRowsColumn column) =>
    column.Type.ClrType == typeof(Guid) ? $"HEXTORAW({alias}.{Quote(column.Name)})" : $"{alias}.{Quote(column.Name)}";

  /// <summary>Builds the expression that applies the patches to a column, one <c>JSON_TRANSFORM</c> with a
  /// <c>SET</c> per path, the value bound as text and read as JSON, and a <c>REMOVE</c> per removed path.</summary>
  /// <param name="column">The column expression the patches apply to.</param>
  /// <param name="patches">The patches to apply.</param>
  /// <param name="bind">A function that binds a value as a parameter and returns its placeholder.</param>
  /// <returns>The expression.</returns>
  public override string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) {
    var operations = string.Join(", ", patches.Select(p => p.IsRemoval
      ? $"REMOVE '{p.Path.Replace("'", "''")}'"
      : $"SET '{p.Path.Replace("'", "''")}' = {bind(new JsonText(p.Json))} FORMAT JSON"));
    return $"JSON_TRANSFORM({column}, {operations})";
  }
}
