using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Postgres;

/// <summary>Provides the PostgreSQL SQL dialect.</summary>
/// <remarks>Full-text search matches a <c>tsvector</c> expression over the columns against a <c>tsquery</c>. The same
/// expression is written into the GIN index and into the statement, so the planner uses the index without a column
/// being added to the table.</remarks>
public class PostgresSqlDialect : SqlDialect {
  public PostgresSqlDialect() {
    // The items of an array column, one row each, typed as the array's item type.
    ArrayRowsSource = "unnest({0}) AS {1}({2})";
    // No virtual generated columns before 18: a computed column is stored.
    ComputedStorage = ComputedStorage.Stored;
    // A point is PostGIS geography, SRID 4326 by default; the extension has to be in the database first.
    GeoPointRead = "ST_AsBinary({0})";
    GeoPointWrite = "ST_GeogFromWKB({0})";
    GeoPointPrerequisite = "CREATE EXTENSION IF NOT EXISTS postgis";
    // pgvector casts its vector to an array of floats and back, and Npgsql binds and reads such an array itself.
    VectorRead = "{0}::real[]";
    VectorWrite = "{0}::vector";
    VectorPrerequisite = "CREATE EXTENSION IF NOT EXISTS vector";
    // The operators, not the functions of the same result: an index over a vector serves an ordering by its operator.
    VectorCosineDistance = "({0} <=> {1})";
    VectorEuclideanDistance = "({0} <-> {1})";
    VectorDotProductDistance = "({0} <#> {1})";
    // Metres on the spheroid, since both sides are geography.
    FunctionTemplates[NamedOpType.GeoDistance] = "ST_Distance({0}, {1})";
    // The server regenerates a view's text and drops its comments, so the hash is a comment on the view.
    ViewHashCarrier = ViewHashCarrier.Comment;
    BatchBeginTransaction = "START TRANSACTION;";
    BatchCommitTransaction = "COMMIT;";

    // PostgreSQL materializes a CTE read more than once by default.
    CteMaterialized = " MATERIALIZED";
    CteNotMaterialized = " NOT MATERIALIZED";

    // pg_try_advisory_lock returns immediately with true or false rather than waiting. The lock is held by the
    // session, so it goes when the connection does.
    TryAcquireSessionLock = "SELECT pg_try_advisory_lock({0})";
    ReleaseSessionLock = "SELECT pg_advisory_unlock({0})";

    InsertReturningIdentity = "INSERT INTO {0} ({1})\n  VALUES ({2})\n  RETURNING {3}";
    SequenceNextValues = "SELECT nextval('{0}') FROM generate_series(1, {1})";
    SelectServerTimeUtc = "SELECT now() AT TIME ZONE 'UTC'";
    SelectServerUser = "SELECT current_user";
    SelectServerDatabase = "SELECT current_database()";

    SelectForUpdate = "\n  FOR UPDATE";
    SelectForUpdateSkipLocked = "\n  FOR UPDATE SKIP LOCKED";

    // The access-method clause sits between the table and the column list, so {5} moves there; the {4} prefix form
    // stays unused.
    CreateIndex = "CREATE INDEX {0} ON {1}{5} ({2})";
    CreateUniqueIndex = "CREATE UNIQUE INDEX {0} ON {1}{5} ({2})";
  }

  /// <summary>Builds the membership test as <c>= ANY(…)</c>, which compares a value with an array parameter.
  /// </summary>
  /// <remarks><c>IN (@p)</c> would read the array as one scalar and match nothing. <c>ANY</c> takes an array and not a
  /// value list, so a list is always bound as a parameter on this provider.</remarks>
  /// <param name="value">The expression tested for membership.</param>
  /// <param name="list">The array parameter the value is compared with.</param>
  /// <returns>The test expression.</returns>
  public override string BuildInList(string value, string list) => $"{value} = ANY({list})";

  /// <summary>Builds a <c>bytea</c> literal as a hex-escaped string cast to the type.</summary>
  /// <remarks>Without the cast the value is <c>text</c> and an insert fails on the column type.</remarks>
  /// <param name="value">The bytes to spell.</param>
  /// <returns>The literal.</returns>
  public override string BinaryLiteral(byte[] value) =>
    "'\\x" + Convert.ToHexString(value) + "'::bytea";

  /// <summary>Builds the statement that records the clustering index of a table.</summary>
  /// <remarks><c>ALTER TABLE … CLUSTER ON …</c> records the choice in <c>pg_index.indisclustered</c> without moving
  /// any rows; a later bare <c>CLUSTER</c> on the table reorders them by it.
  /// <see cref="PgHelper.ApplyClusteringAsync"/> issues both, in that order.</remarks>
  /// <param name="table">The table to mark.</param>
  /// <param name="key">The index to cluster the table on.</param>
  /// <returns>The statement.</returns>
  public override string? ClusterTable(DbTableInfo table, DbKeyInfo key) =>
    $"ALTER TABLE {GetTableRef(table)} CLUSTER ON {Quote(key.Name)}";

  /// <summary>Gets the query that returns a routine's definition as <c>pg_get_functiondef</c> writes it: the whole
  /// <c>CREATE OR REPLACE FUNCTION</c>, with language, volatility and argument modes.</summary>
  /// <remarks>Only plain functions and procedures (<c>prokind</c> 'f' and 'p') are selected, since
  /// <c>pg_get_functiondef</c> raises on an aggregate or a window function rather than returning null. The rows are
  /// ordered by the argument list, so overloads come back in the same order each time.</remarks>
  public override string? RoutineDefinitionQuery =>
    @"SELECT pg_get_functiondef(p.oid) AS definition
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE n.nspname = {0} AND p.proname = {1} AND p.prokind IN ('f', 'p')
      ORDER BY pg_get_function_identity_arguments(p.oid)";

  /// <summary>Gets the <c>USING</c> clause of an index type, which PostgreSQL places between the table and the column
  /// list.</summary>
  /// <param name="type">One of the enumeration values that specifies the index type.</param>
  /// <returns>An empty prefix and the <c>USING</c> clause.</returns>
  public override (string Prefix, string Clause) IndexTypeSyntax(DbIndexType type) =>
    ("", " USING " + type switch {
      DbIndexType.Hash => "hash",
      DbIndexType.Gist => "gist",
      DbIndexType.Gin => "gin",
      DbIndexType.SpGist => "spgist",
      DbIndexType.Brin => "brin",
      // A full-text index is a GIN over the text-search vector of the columns.
      DbIndexType.FullText => "gin",
      DbIndexType.Hnsw => "hnsw",
      _ => "btree",
    });

  /// <summary>Builds the column of a vector index with the operator class of the index's metric, which is how
  /// pgvector is told the metric.</summary>
  /// <inheritdoc/>
  public override string? BuildVectorIndexColumns(DbKeyInfo index) =>
    index.Columns[0].Column.QuotedName + " " + index.VectorMetric switch {
      VectorMetric.Euclidean => "vector_l2_ops",
      VectorMetric.DotProduct => "vector_ip_ops",
      _ => "vector_cosine_ops",
    };

  /// <summary>Builds a SQL function returning <c>TABLE(…)</c> over the table's columns, replaced in place, its body
  /// in dollar quotes.</summary>
  /// <inheritdoc/>
  public override string? BuildTableFunction(DbTableInfo table, string body) {
    var function = table.TableFunction!;
    var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name} {p.TypeSpec}"));
    var columns = string.Join(", ", table.Columns.Select(c => $"{Quote(c.Name)} {c.TypeSpec}"));
    return $"CREATE OR REPLACE FUNCTION {table.RefName}({parameters}) RETURNS TABLE({columns}) LANGUAGE sql STABLE AS $mic$\n"
      + body + "\n$mic$";
  }

  // ----- full-text search: a tsvector matched against a tsquery -------------------------------------------------------

  /// <summary>Specifies the text search configuration, the language whose stemming and stop words a vector and a
  /// query are built with.</summary>
  /// <remarks>There is one per database, since the vector's expression is written into the index. The default is
  /// <c>english</c>.</remarks>
  public string TextSearchConfiguration = "english";

  public override string BuildFullTextIndexColumns(DbKeyInfo index) =>
    BuildTextVector(index.Columns.Select(c => c.Column.QuotedName));

  public override string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"{BuildTextVector(index.Columns.Select(c => alias + "." + c.Column.QuotedName))} @@ {BuildTsQuery(query, kind)}";

  public override string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"ts_rank({BuildTextVector(index.Columns.Select(c => alias + "." + c.Column.QuotedName))}, {BuildTsQuery(query, kind)})";

  /// <summary>Builds the <c>to_tsquery</c> form of the search words for the match kinds that need one: <c>a | b</c>
  /// for any word, <c>a:* &amp; b:*</c> for prefixes, and phrase alternatives for words within a distance.</summary>
  /// <remarks>All words and a phrase take <c>plainto_tsquery</c> and <c>phraseto_tsquery</c>, which take the words as
  /// they are, so those kinds get <see langword="null"/>. The words are cleaned to letters and digits before they
  /// enter <c>to_tsquery</c>. PostgreSQL's <c>&lt;N&gt;</c> means exactly N apart in that order, so "within N, any
  /// order" is the alternatives <c>a &lt;1&gt; b | b &lt;1&gt; a | … | a &lt;N&gt; b | b &lt;N&gt; a</c>, for two
  /// words at most.</remarks>
  /// <param name="words">The words to search for.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">The largest distance between the words, for a match within a distance.</param>
  /// <returns>The query text, an empty string when no word remains after cleaning, or <see langword="null"/> when the
  /// kind takes the words as they are.</returns>
  /// <exception cref="NotSupportedException">the match is within a distance and there are more than two words.
  /// </exception>
  public override string? BuildTextQuery(string words, TextMatchKind kind, int distance) {
    if (kind is TextMatchKind.AllWords or TextMatchKind.Phrase)
      return null;
    var cleaned = SplitTextWords(words);
    if (cleaned.Length == 0)
      return "";
    switch (kind) {
      case TextMatchKind.AnyWord:
        return string.Join(" | ", cleaned);
      case TextMatchKind.Prefix:
        return string.Join(" & ", cleaned.Select(w => w + ":*"));
      case TextMatchKind.Near:
        if (cleaned.Length == 1)
          return cleaned[0];
        if (cleaned.Length > 2)
          throw new NotSupportedException(
            "PostgreSQL spells 'within a distance' as phrase alternatives, which multiply with every word — two words at most.");
        var pairs = new List<string>();
        for (var n = 1; n <= Math.Max(distance, 1); n++) {
          pairs.Add($"{cleaned[0]} <{n}> {cleaned[1]}");
          pairs.Add($"{cleaned[1]} <{n}> {cleaned[0]}");
        }
        return string.Join(" | ", pairs);
      default:
        return null;
    }
  }

  private string BuildTextVector(IEnumerable<string> columns) =>
    $"to_tsvector('{TextSearchConfiguration}', {string.Join(" || ' ' || ", columns.Select(c => $"coalesce({c}, '')"))})";

  private string BuildTsQuery(string query, TextMatchKind kind) => kind switch {
    TextMatchKind.AllWords => $"plainto_tsquery('{TextSearchConfiguration}', {query})",
    TextMatchKind.Phrase => $"phraseto_tsquery('{TextSearchConfiguration}', {query})",
    _ => $"to_tsquery('{TextSearchConfiguration}', {query})",
  };

  /// <summary>Builds the expression that reads the value at a path as text, with <c>jsonb_path_query_first</c> and
  /// <c>#&gt;&gt; '{}'</c>, cast to a type when one is given.</summary>
  /// <remarks>The path is cast to <c>jsonpath</c>, so a bound path works as a literal does.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="path">The expression that yields the path.</param>
  /// <param name="type">The type the text is cast to, or <see langword="null"/> to read the text itself.</param>
  /// <returns>The expression.</returns>
  public override string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    var text = $"(jsonb_path_query_first({document}, ({path})::jsonpath) #>> '{{}}')";
    return type == null ? text : BuildCast(text, type);
  }

  /// <summary>Builds the expression that reads the fragment at a path as <c>jsonb</c>, which
  /// <c>jsonb_to_recordset</c> reads.</summary>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="path">The expression that yields the path.</param>
  /// <returns>The expression.</returns>
  public override string BuildJsonFragment(string document, string path) =>
    $"jsonb_path_query_first({document}, ({path})::jsonpath)";

  /// <summary>Builds the table source for the rows of a JSON parameter, as <c>jsonb_to_recordset</c> with the
  /// record's columns declared in the column list.</summary>
  /// <remarks>The parameter is cast, since a text parameter is not <c>jsonb</c>. A Guid is <c>uuid</c> and needs no
  /// unwrapping. An array of scalars is read with <c>jsonb_array_elements_text</c>, each element as text.</remarks>
  /// <param name="parameter">The parameter that holds the JSON text.</param>
  /// <param name="columns">The columns of the rows.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  public override string JsonRowsSource(string parameter, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    // An array of scalars: each element comes out as text and is cast where it is read.
    if (columns is [{ IsElement: true } element])
      return $"jsonb_array_elements_text({parameter}::jsonb) AS {alias}({Quote(element.Name)})";
    var declared = string.Join(", ", columns.Select(c => $"{Quote(c.Name)} {c.Type.TypeSpec}"));
    return $"jsonb_to_recordset({parameter}::jsonb) AS {alias}({declared})";
  }

  /// <summary>Builds the expression that applies the patches to a column, as <c>jsonb_set</c> nested once per patch
  /// with each value bound as <c>jsonb</c>.</summary>
  /// <remarks>A missing path is created. A removal is the path-delete operator <c>#-</c>.</remarks>
  /// <param name="column">The column expression the patches apply to.</param>
  /// <param name="patches">The patches to apply, in order.</param>
  /// <param name="bind">A function that binds a value as a parameter and returns its placeholder.</param>
  /// <returns>The expression.</returns>
  public override string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) {
    var expression = column;
    foreach (var patch in patches) {
      var steps = string.Join(",", patch.Steps.Select(s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "''") + "\""));
      // #- deletes the path; the last argument of jsonb_set creates a missing path.
      expression = patch.IsRemoval
        ? $"({expression} #- '{{{steps}}}')"
        : $"jsonb_set({expression}, '{{{steps}}}', {bind(new JsonText(patch.Json))}, true)";
    }
    return expression;
  }

  /// <summary>Builds the reference to a column of a JSON rows source, casting an element of an array of scalars
  /// from text to the column's type.</summary>
  /// <remarks>A property column is declared typed by the source and needs no cast.</remarks>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="column">The column to reference.</param>
  /// <returns>The column expression.</returns>
  public override string JsonRowsColumn(string alias, JsonRowsColumn column) =>
    column.IsElement && column.Type.ClrType != typeof(string)
      ? BuildCast($"{alias}.{Quote(column.Name)}", column.Type)
      : $"{alias}.{Quote(column.Name)}";
}
