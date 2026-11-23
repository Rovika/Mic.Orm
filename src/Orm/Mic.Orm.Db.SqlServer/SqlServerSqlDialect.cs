using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;

namespace Mic.Orm.Db.SqlServer;

/// <summary>Provides the SQL Server SQL dialect.</summary>
/// <remarks>A rename is a stored procedure, a default is a constraint with its own name, and a comment is an extended
/// property. A full-text index has no name of its own; its catalog carries the name, and a rank is read off a
/// <c>CONTAINSTABLE</c> join rather than computed over the row.</remarks>
public class SqlServerSqlDialect : SqlDialect {
  /// <summary>Specifies the name of the extended property that holds the hash of a definition the ORM wrote: the
  /// expression of a computed column, the filter of an index.</summary>
  /// <remarks>A property of the ORM's own, so that <c>MS_Description</c> stays free for a description.</remarks>
  public const string DefinitionHashProperty = "MIC_DefinitionHash";

  public SqlServerSqlDialect() {
    // Used where the target has no json type; a null document passes, since ISJSON of null is null.
    JsonTextCheck = "ISJSON({0}) = 1";
    ConditionAsValue = "CAST(CASE WHEN {0} THEN 1 ELSE 0 END AS BIT)";
    // AVG over an integer column is an integer.
    IntegerAverage = "AVG(CAST({0} AS FLOAT))";
    GeoPointRead = "{0}.STAsBinary()";
    GeoPointWrite = "geography::STGeomFromWKB({0}, 4326)";
    FunctionTemplates[NamedOpType.GeoDistance] = "{0}.STDistance({1})";
    VectorCosineDistance = "VECTOR_DISTANCE('cosine', {0}, {1})";
    VectorEuclideanDistance = "VECTOR_DISTANCE('euclidean', {0}, {1})";
    VectorDotProductDistance = "VECTOR_DISTANCE('dot', {0}, {1})";
    LateralJoin = "CROSS APPLY {0}";
    // A temp table is any table whose name starts with #; it lives as long as the session.
    CreateTempTable = CreateTable;
    // A computed column declares no type; the expression gives one. PERSISTED is the stored kind.
    ComputedColumnSpec = "{0} AS ({2}){3}";
    ComputedStored = " PERSISTED";
    ComputedVirtual = "";
    QuoteLeft = "[";
    QuoteRight = "]";
    QuoteEscape = "]]";

    Identity = " IDENTITY(1,1)";

    // The ANSI form; SQL Server has no LIMIT. It is legal only after an ORDER BY.
    PageSkipTake = "{0} OFFSET {1} ROWS FETCH NEXT {2} ROWS ONLY";
    PageTakeOnly = "{0} OFFSET 0 ROWS FETCH NEXT {2} ROWS ONLY";
    PageSkipOnly = "{0} OFFSET {1} ROWS";

    // Recursion is worked out from the definition itself; there is no word for it.
    WithRecursive = "WITH";

    // The alias is said before the FROM: the form a delete with a join takes here, and the only one that takes an
    // alias at all.
    DeleteFrom = "DELETE {1} FROM {0} AS {1}";
    UpdateWhere = "UPDATE {1}\n  SET {2}\n  FROM {0} AS {1}";
    UpdateFrom = "UPDATE {1}\n  SET {2}\n  FROM {0} AS {1}\n    INNER JOIN {3} {4} ON {5}";

    // OUTPUT goes between the column list and VALUES, so the template is the whole insert.
    InsertReturningIdentity = "INSERT INTO {0} ({1})\n  OUTPUT INSERTED.{3}\n  VALUES ({2})";
    // NEXT VALUE FOR may not sit inside a subquery, so the rows come from a derived table and the function sits above it.
    SequenceNextValues = "SELECT NEXT VALUE FOR {0} FROM (SELECT TOP ({1}) 0 AS n FROM sys.all_columns) AS rows";
    SelectServerTimeUtc = "SELECT SYSUTCDATETIME()";
    SelectServerUser = "SELECT SUSER_SNAME()";
    SelectServerDatabase = "SELECT DB_NAME()";

    // SQL Server treats a constraint failure as a statement-level abort: the statement is undone, the batch carries
    // on and the transaction is still committable. XACT_ABORT makes it stop and roll back; it is a session setting,
    // so it is stated at the head of each batch.
    BatchBeginTransaction = "SET XACT_ABORT ON;\nBEGIN TRANSACTION;";

    // A generated key stays on the server inside a batch: @id3 is an output parameter of the command, which a later
    // statement writes where the key goes and the client reads when the batch has run.
    BatchCaptureIdentity = "SET {0} = SCOPE_IDENTITY()";

    // ALTER COLUMN restates the type and the nullability together and amends neither alone, so the provider declares
    // AlterColumnRestatesDefinition. It will not take a DEFAULT clause; a default is a constraint.
    ModifyColumn = "ALTER TABLE {0} ALTER COLUMN {1}";

    // A default is a constraint, added FOR a column and dropped by its own name. {3} is that name, which only
    // introspection can supply, since SQL Server generates it unless one is given.
    SetDefault = "ALTER TABLE {0} ADD DEFAULT {2} FOR {1}";
    DropDefault = "ALTER TABLE {0} DROP CONSTRAINT {3}";

    // A rename is a stored procedure whose object is a string, so the name is quoted as a literal; the new name is
    // bare because sp_rename moves nothing.
    RenameTable = "EXEC sp_rename '{0}', '{1}'";
    RenameColumn = "EXEC sp_rename '{0}.{1}', '{2}', 'COLUMN'";

    // An index belongs to its table.
    DropIndex = "DROP INDEX {2} ON {1}";

    // The lock is a hint on the table, not a clause after the select. ROWLOCK keeps it to the row; UPDLOCK lets a
    // second writer's read wait where two shared locks would both be granted and deadlock on the UPDATE after.
    TableHintForUpdate = " WITH (UPDLOCK, ROWLOCK)";
    TableHintForUpdateSkipLocked = " WITH (UPDLOCK, READPAST, ROWLOCK)";

    // No COMMENT ON; an extended property wants the object in three levelled pieces, which the template takes.
    CommentOnIndex =
      $"EXEC sp_addextendedproperty @name = N'{DefinitionHashProperty}', @value = N{{1}}, "
      + "@level0type = N'SCHEMA', @level0name = {2}, "
      + "@level1type = N'TABLE', @level1name = {3}, "
      + "@level2type = N'INDEX', @level2name = {4}";

    CommentOnColumn =
      $"EXEC sp_addextendedproperty @name = N'{DefinitionHashProperty}', @value = N{{1}}, "
      + "@level0type = N'SCHEMA', @level0name = {2}, "
      + "@level1type = N'TABLE', @level1name = {3}, "
      + "@level2type = N'COLUMN', @level2name = {4}";

    // sp_getapplock takes a name and a mode and returns a code: >= 0 is granted, negative is not. The comparison is
    // here so that the answer is one truthy value everywhere.
    TryAcquireSessionLock =
      "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'mic_{0}', @LockMode = 'Exclusive', "
      + "@LockOwner = 'Session', @LockTimeout = 0; SELECT CASE WHEN @r >= 0 THEN 1 ELSE 0 END";
    ReleaseSessionLock =
      "DECLARE @r int; EXEC @r = sp_releaseapplock @Resource = 'mic_{0}', @LockOwner = 'Session'; SELECT 1";

    // + concatenates and LEN measures; LEN drops trailing spaces, and DATALENGTH counts bytes.
    FunctionTemplates[NamedOpType.Concat] = "({0} + {1})";
    FunctionTemplates[NamedOpType.Length] = "LEN({0})";
    FunctionTemplates[NamedOpType.Substring] = "SUBSTRING({0}, {1}, {2})";

    // No EXTRACT. DATEPART takes the part as a keyword rather than a string, so it cannot be parameterised.
    FunctionTemplates[NamedOpType.Year] = "DATEPART(year, {0})";
    FunctionTemplates[NamedOpType.Month] = "DATEPART(month, {0})";
    FunctionTemplates[NamedOpType.Day] = "DATEPART(day, {0})";

    // ROUND takes two arguments here, and the second is not optional.
    FunctionTemplates[NamedOpType.Round] = "ROUND({0}, 0)";

    // CHARINDEX takes the needle first.
    FunctionTemplates[NamedOpType.IndexOf] = "CHARINDEX({1}, {0})";

    // A rank is a rowset here, read off a CONTAINSTABLE join.
    FullTextJoin = FullTextJoinKind.RankedRows;
  }

  /// <inheritdoc/>
  public override string GetTempTableName(string name) => "#" + name;

  // ----- full-text search: a FULLTEXT INDEX in a catalog, CONTAINS and CONTAINSTABLE ----------------------------------

  /// <summary>Builds the match predicate, <c>CONTAINS</c> over the index's columns under the row's alias.</summary>
  /// <param name="alias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The predicate.</returns>
  public override string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"CONTAINS(({BuildColumnList(alias, index)}), {query})";

  /// <summary>Builds the rank expression, <c>ISNULL(alias.[RANK], 0)</c>, read off the ranked rows the join brought.
  /// </summary>
  /// <param name="alias">The alias of the ranked rows in the join.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The rank expression.</returns>
  public override string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"ISNULL({alias}.[RANK], 0)";

  /// <summary>Builds the left outer join of <c>CONTAINSTABLE</c> over the index's columns to the entity's table on
  /// its key.</summary>
  /// <remarks>A rank is not an expression over the row but a rowset: <c>CONTAINSTABLE</c> answers <c>KEY</c> and
  /// <c>RANK</c> for the rows that match. The join is outer, so every row has a rank, zero for no match; there is one
  /// such join per query.</remarks>
  /// <param name="joinAlias">The alias the ranked rows get in the join.</param>
  /// <param name="tableAlias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <returns>The join clause.</returns>
  /// <exception cref="NotSupportedException">the table has no primary key, or its primary key is composite.</exception>
  public override string BuildTextJoin(string joinAlias, string tableAlias, DbKeyInfo index, string? query, TextMatchKind kind) {
    var key = GetTextKey(index);
    return $"LEFT OUTER JOIN CONTAINSTABLE({index.Table.RefName}, ({BuildColumnList(null, index)}), {query}) {joinAlias}"
      + $" ON {joinAlias}.[KEY] = {tableAlias}.{key.QuotedName}";
  }

  /// <summary>Builds the <c>CONTAINS</c> query from the search words for a match kind.</summary>
  /// <remarks>Every word is quoted, since a bare word that is an operator (<c>and</c>, <c>or</c>, <c>near</c>) is
  /// read as one; a prefix is <c>"ste*"</c>, with the star inside the quotes. Any word is <c>ISABOUT("a", "b")</c>
  /// rather than <c>OR</c>: both answer the rows holding any of the words, but <c>ISABOUT</c> ranks more words matched
  /// higher. <c>NEAR((a, b), N)</c> counts the words between, where the distance given counts the positions apart, so
  /// one is taken off; it takes any number of words, unordered.</remarks>
  /// <param name="words">The words to search for.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">The largest distance between the words, for a match within a distance.</param>
  /// <returns>The query text, an empty string when no word remains after cleaning, or <see langword="null"/> for a
  /// kind this dialect does not know.</returns>
  public override string? BuildTextQuery(string words, TextMatchKind kind, int distance) {
    var cleaned = SplitTextWords(words);
    if (cleaned.Length == 0)
      return "";
    var quoted = cleaned.Select(w => "\"" + w + "\"").ToArray();
    return kind switch {
      TextMatchKind.AllWords => string.Join(" AND ", quoted),
      TextMatchKind.AnyWord => $"ISABOUT({string.Join(", ", quoted)})",
      TextMatchKind.Phrase => "\"" + string.Join(" ", cleaned) + "\"",
      TextMatchKind.Prefix => string.Join(" AND ", cleaned.Select(w => "\"" + w + "*\"")),
      TextMatchKind.Near => cleaned.Length == 1 ? quoted[0] : $"NEAR(({string.Join(", ", cleaned)}), {Math.Max(distance, 1) - 1})",
      _ => null,
    };
  }

  /// <summary>Builds the statements that create a full-text index: the catalog, if it does not exist yet, and the
  /// index in it, keyed by the primary key and change-tracked.</summary>
  /// <remarks>A full-text index has no name: a table has one, declared over its columns and keyed by a unique
  /// single-column index, in a catalog. The catalog carries the name, <c>{schema}_{index}</c> since catalog names are
  /// database-wide, and the model loader reads it back as the index's name. <c>CHANGE_TRACKING AUTO</c> keeps the
  /// index in step with the rows asynchronously, a moment behind a write.</remarks>
  /// <param name="index">The full-text index to create.</param>
  /// <returns>The statements, in the order they run.</returns>
  /// <exception cref="NotSupportedException">the table has no primary key, or its primary key is composite.</exception>
  public override IReadOnlyList<string>? BuildFullTextIndex(DbKeyInfo index) {
    var key = GetTextKey(index);
    var catalog = Quote(GetCatalogName(index));
    return [
      $"IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = {GetLiteral(GetCatalogName(index))}) CREATE FULLTEXT CATALOG {catalog}",
      $"CREATE FULLTEXT INDEX ON {index.Table.RefName} ({BuildColumnList(null, index)}) KEY INDEX {Quote(key.Table!.PrimaryKey!.Name)}"
        + $" ON {catalog} WITH CHANGE_TRACKING AUTO",
    ];
  }

  public override IReadOnlyList<string>? BuildDropFullTextIndex(DbKeyInfo index) => [
    $"DROP FULLTEXT INDEX ON {index.Table.RefName}",
    $"DROP FULLTEXT CATALOG {Quote(GetCatalogName(index))}",
  ];

  /// <summary>Builds the query that counts the changes change tracking has not yet propagated plus the population
  /// still running; both are zero once the index answers for every committed row.</summary>
  /// <param name="table">The table whose full-text index is asked about.</param>
  /// <returns>The query.</returns>
  public override string BuildFullTextPendingQuery(DbTableInfo table) {
    var objectName = GetLiteral(table.RefName);
    return $"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID({objectName}), 'TableFullTextPendingChanges') AS int)"
      + $" + CAST(OBJECTPROPERTYEX(OBJECT_ID({objectName}), 'TableFullTextPopulateStatus') AS int)";
  }

  /// <summary>Gets the name of a full-text index's catalog, <c>{schema}_{index}</c>.</summary>
  /// <remarks>A catalog is named database-wide, and the same index name recurs across schemas.</remarks>
  /// <param name="index">The full-text index.</param>
  /// <returns>The catalog name.</returns>
  public static string GetCatalogName(DbKeyInfo index) =>
    index.Table.Schema.Name.Length > 0 ? index.Table.Schema.Name + "_" + index.Name : index.Name;

  /// <summary>Gets the column a full-text index is keyed by, the primary key's single column; a composite key is not
  /// one a full-text index can take.</summary>
  private static DbColumnInfo GetTextKey(DbKeyInfo index) {
    var key = index.Table.PrimaryKey
      ?? throw new NotSupportedException($"A full-text index on SQL Server is keyed by the table's primary key, and '{index.Table.Name}' has none.");
    if (key.Columns.Count != 1)
      throw new NotSupportedException(
        $"A full-text index on SQL Server is keyed by a single-column unique index, and '{index.Table.Name}' has a composite primary key.");
    return key.Columns[0].Column;
  }

  private string BuildColumnList(string? alias, DbKeyInfo index) =>
    string.Join(", ", index.Columns.Select(c => alias == null ? c.Column.QuotedName : alias + "." + c.Column.QuotedName));

  /// <summary>Builds an inline table-valued function, created or altered in place, its parameters prefixed as the
  /// server names them.</summary>
  /// <inheritdoc/>
  public override string? BuildTableFunction(DbTableInfo table, string body) {
    var function = table.TableFunction!;
    var parameters = string.Join(", ", function.Parameters.Select(p => $"@{p.Name} {p.TypeSpec}"));
    return $"CREATE OR ALTER FUNCTION {table.RefName}({parameters}) RETURNS TABLE AS RETURN (\n{body}\n)";
  }

  /// <summary>Gets the escape character of a <c>LIKE</c> pattern and the characters it escapes, which here include
  /// <c>[</c>.</summary>
  /// <remarks>T-SQL reads <c>[abc]</c> as a character class, so a search for <c>[draft]</c> would otherwise match a
  /// single letter. <c>]</c> needs no escaping; it is meaningful only after an unescaped <c>[</c>.</remarks>
  public override LikeEscaping LikeEscaping => new('\\', "%_[");

  /// <summary>Gets the statement the schema connection runs when it opens, which makes it the one to lose a
  /// deadlock.</summary>
  /// <remarks>SQL Server's readers take locks by default, so a catalog read contends with concurrent DDL. Snapshot
  /// isolation does not help: it versions data, and a schema change has no older version to keep. The setting is
  /// session-scoped, so it applies to the schema connection and nothing else.</remarks>
  public override string? SchemaConnectionSetup => "SET DEADLOCK_PRIORITY LOW";

  /// <summary>Builds a Boolean literal as <c>0</c> or <c>1</c>, which a <c>bit</c> column holds; <c>TRUE</c> is not a
  /// value it takes.</summary>
  /// <param name="value"><see langword="true"/> for the true literal; otherwise, <see langword="false"/>.</param>
  /// <returns>The literal.</returns>
  public override string BooleanLiteral(bool value) => value ? "1" : "0";

  /// <summary>Builds a string literal, with the <c>N</c> prefix where the text has a character outside ASCII.
  /// </summary>
  /// <remarks>The server reads a literal without the prefix in the code page of the database's collation and turns
  /// a character the page lacks into a question mark.</remarks>
  /// <param name="value">The string to spell.</param>
  /// <returns>The literal.</returns>
  public override string GetLiteral(string value) =>
    System.Text.Ascii.IsValid(value) ? base.GetLiteral(value) : "N" + base.GetLiteral(value);

  /// <summary>Builds a binary literal as <c>0x…</c>; SQL Server does not accept the standard <c>X'…'</c>.</summary>
  /// <param name="value">The bytes to spell.</param>
  /// <returns>The literal.</returns>
  public override string BinaryLiteral(byte[] value) => "0x" + Convert.ToHexString(value);

  /// <summary>Gets the prefix of a spatial index, <c>SPATIAL</c> before <c>INDEX</c>; a geography needs no bounding
  /// box.</summary>
  /// <param name="type">One of the enumeration values that specifies the index type.</param>
  /// <returns>The prefix and an empty clause.</returns>
  public override (string Prefix, string Clause) IndexTypeSyntax(DbIndexType type) =>
    type == DbIndexType.Spatial ? ("SPATIAL ", "") : ("", "");

  /// <summary>Builds a date-time literal in ISO 8601 form with the <c>T</c>, the one form SQL Server reads the same
  /// way under every language and <c>DATEFORMAT</c> setting.</summary>
  /// <remarks>A bare <c>'2024-03-04 …'</c> is March 4th or April 3rd depending on the session.</remarks>
  /// <param name="value">The instant to spell.</param>
  /// <returns>The literal.</returns>
  public override string DateTimeLiteral(DateTime value) =>
    StringQuote + value.ToString("yyyy-MM-ddTHH:mm:ss.fff", Invariant) + StringQuote;

  /// <summary>Builds the join of a JSON rows source to the row it stands beside, with <c>APPLY</c>, since a
  /// <c>JOIN</c> may not read what precedes it here.</summary>
  /// <remarks>A filtered spread has nowhere to put its <c>ON</c>, so the source is wrapped and filtered inside, and
  /// named again outside.</remarks>
  /// <param name="source">The rows source.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="condition">The filter on the rows, or <see langword="null"/> for none.</param>
  /// <param name="outer"><see langword="true"/> to keep a row with no matching JSON rows; otherwise,
  /// <see langword="false"/>.</param>
  /// <returns>The join clause.</returns>
  public override string JoinJsonRows(string source, string alias, string? condition, bool outer) {
    var apply = outer ? "OUTER APPLY" : "CROSS APPLY";
    return condition == null ? $"{apply} {source}" : $"{apply} (SELECT * FROM {source} WHERE {condition}) AS {alias}";
  }

  /// <summary>Builds the table source for the rows of a JSON parameter, <c>OPENJSON … WITH</c>, each column declared
  /// with its type and its path.</summary>
  /// <remarks>A Guid is <c>uniqueidentifier</c> here and is read from its text.</remarks>
  /// <param name="parameter">The parameter that holds the JSON text.</param>
  /// <param name="columns">The columns of the rows.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  public override string JsonRowsSource(string parameter, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    // The element itself is at the path $: an array of scalars, one column.
    var declared = string.Join(", ", columns.Select(c => $"{Quote(c.Name)} {c.Type.TypeSpec} {(c.IsElement ? "'$'" : $"'$.\"{c.Name}\"'")}"));
    return $"OPENJSON({parameter}) WITH ({declared}) AS {alias}";
  }

  /// <summary>Builds the expression that applies the patches to a column, as <c>JSON_MODIFY</c> nested once per
  /// patch: a scalar bound as itself, a Boolean as a <c>bit</c>, a fragment as <c>JSON_QUERY</c> of its text, and a
  /// removal as <c>NULL</c>.</summary>
  /// <remarks><c>JSON_MODIFY</c> with a <c>NULL</c> value removes the path, so a JSON null cannot be set; the whole
  /// document is written then.</remarks>
  /// <param name="column">The column expression the patches apply to.</param>
  /// <param name="patches">The patches to apply, in order.</param>
  /// <param name="bind">A function that binds a value as a parameter and returns its placeholder.</param>
  /// <returns>The expression, or <see langword="null"/> when a patch sets a JSON null.</returns>
  public override string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) {
    // JSON_MODIFY with NULL removes the path, so a JSON null cannot be set.
    if (patches.Any(p => !p.IsRemoval && p.IsScalar && p.Scalar == null))
      return null;
    var expression = column;
    foreach (var patch in patches) {
      var value = patch.IsRemoval ? "NULL"
        : !patch.IsScalar ? $"JSON_QUERY({bind(new JsonText(patch.Json))})"
        : patch.Scalar is bool flag ? $"CAST({bind(flag)} AS bit)"
        : bind(patch.Scalar);
      expression = $"JSON_MODIFY({expression}, '{patch.Path.Replace("'", "''")}', {value})";
    }
    return expression;
  }
}
