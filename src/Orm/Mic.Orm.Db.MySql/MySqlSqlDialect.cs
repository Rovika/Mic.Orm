using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;

namespace Mic.Orm.Db.MySql;

/// <summary>Provides the MySQL SQL dialect.</summary>
public class MySqlSqlDialect : SqlDialect {
  public MySqlSqlDialect() {
    // No table-valued functions: a procedure's result set is not something a query reads from.
    TableFunctionSource = null;
    // SRID 4326 is latitude-first to MySQL, so the axis order is named on both sides, and the binary stays X = longitude.
    GeoPointRead = "ST_AsBinary({0}, 'axis-order=long-lat')";
    GeoPointWrite = "ST_GeomFromWKB({0}, 4326, 'axis-order=long-lat')";
    // Metres on a sphere; ST_Distance on SRID 4326 would be metres too, on the spheroid, but it is slower and 8.0.19+.
    FunctionTemplates[NamedOpType.GeoDistance] = "ST_Distance_Sphere({0}, {1})";
    CreateTempTable = "CREATE TEMPORARY TABLE {0} (\n{1}\n)";
    DropTempTable = "DROP TEMPORARY TABLE {0}";
    // The server re-renders a view's definition without its comments and has no view comments, so the hash is a
    // literal in a condition the definition keeps.
    ViewHashCarrier = ViewHashCarrier.Literal;
    BatchBeginTransaction = "START TRANSACTION;";
    BatchCommitTransaction = "COMMIT;";

    // A generated key stays on the server inside a batch, in a session variable read back by one SELECT at the end.
    // @id3 reaches the server as a variable only because the connection string says AllowUserVariables=True; without
    // it the driver reads it as a parameter nobody bound.
    BatchCaptureIdentity = "SET {0} = LAST_INSERT_ID()";
    BatchReadIdentities = "SELECT {0}";
    SelectServerTimeUtc = "SELECT UTC_TIMESTAMP(6)";
    SelectServerUser = "SELECT CURRENT_USER()";
    SelectServerDatabase = "SELECT DATABASE()";

    // The join comes before the SET in a multi-table update, and the assigned column is qualified, since a bare one
    // is looked up in every table named.
    UpdateFrom = "UPDATE {0} AS {1}\n    INNER JOIN {3} {4} ON {5}\n  SET {2}";
    UpdateAssignment = "{2}.{0} = {1}";

    // Backticks; double quotes work only under ANSI_QUOTES, a server setting not to assume.
    QuoteLeft = "`";
    QuoteRight = "`";
    QuoteEscape = "``";

    Identity = " AUTO_INCREMENT";

    // OFFSET needs a LIMIT before it and MySQL has no LIMIT ALL; the manual's answer is the largest count there is.
    PageSkipOnly = "{0} LIMIT 18446744073709551615 OFFSET {1}";

    // MySQL has no ALTER COLUMN … TYPE and no way to change nullability alone: MODIFY replaces the whole definition,
    // so the provider declares AlterColumnRestatesDefinition and the base emits one statement built from the column
    // spec.
    ModifyColumn = "ALTER TABLE {0} MODIFY COLUMN {1}";

    // A schema is a database; CREATE SCHEMA is a synonym MySQL accepts.
    CreateSchema = "CREATE SCHEMA {0}";
    DropSchema = "DROP SCHEMA {0}";

    // An index belongs to its table rather than to the schema, so dropping one names the table.
    DropIndex = "DROP INDEX {2} ON {1}";
    DropConstraint = "ALTER TABLE {0} DROP FOREIGN KEY {1}";

    // No COMMENT ON: a column's comment is a clause of its definition, and it carries the expression's hash.
    ComputedColumnSpec = "{0} {1} GENERATED ALWAYS AS ({2}){3} COMMENT '{4}'";
    CommentOnColumn = null;
    DropCheckConstraint = "ALTER TABLE {0} DROP CHECK {1}";

    // DROP PRIMARY KEY is the only spelling MySQL accepts; the name is formatted in and ignored, which keeps the
    // template's arguments the same everywhere.
    DropPrimaryKey = "ALTER TABLE {0} DROP PRIMARY KEY";

    RenameTable = "RENAME TABLE {0} TO {1}";

    // MySQL has no returning clause: the generated key is a second statement, which MySqlConnector sends in the same
    // command, so the whole insert is the template.
    InsertReturningIdentity = "INSERT INTO {0} ({1})\n  VALUES ({2});\nSELECT LAST_INSERT_ID()";

    SelectForUpdate = "\n  FOR UPDATE";
    SelectForUpdateSkipLocked = "\n  FOR UPDATE SKIP LOCKED";

    // GET_LOCK takes a name, not a number; the key is formatted into a string literal.
    TryAcquireSessionLock = "SELECT GET_LOCK('mic_{0}', 0)";
    ReleaseSessionLock = "SELECT RELEASE_LOCK('mic_{0}')";

    // || is logical OR unless the server runs with PIPES_AS_CONCAT.
    FunctionTemplates[NamedOpType.Concat] = "CONCAT({0}, {1})";

    // Positional arguments rather than the FROM … FOR form.
    FunctionTemplates[NamedOpType.Substring] = "SUBSTRING({0}, {1}, {2})";

    // LENGTH counts bytes on MySQL; CHAR_LENGTH counts characters, as string.Length does.
    FunctionTemplates[NamedOpType.Length] = "CHAR_LENGTH({0})";

  }

  /// <inheritdoc/>
  /// <remarks>A temporary table lives in a database, and a connection with no default database has to name it.
  /// </remarks>
  public override string GetTempTableRef(string schema, string name) => QualifiedName(schema, name);

  /// <summary>Gets the escape character of a <c>LIKE</c> pattern, a slash.</summary>
  /// <remarks>MySQL treats a backslash as an escape inside a string literal unless the server runs with
  /// <c>NO_BACKSLASH_ESCAPES</c>, so <c>ESCAPE '\'</c> would escape the closing quote. A slash is special under
  /// neither setting.</remarks>
  public override LikeEscaping LikeEscaping => new('/', "%_");

  /// <summary>Gets the prefix of an index type, <c>FULLTEXT</c> or <c>SPATIAL</c> before <c>INDEX</c>; the clause
  /// slot is unused.</summary>
  /// <param name="type">One of the enumeration values that specifies the index type.</param>
  /// <returns>The prefix and an empty clause.</returns>
  public override (string Prefix, string Clause) IndexTypeSyntax(DbIndexType type) => type switch {
    DbIndexType.FullText => ("FULLTEXT ", ""),
    DbIndexType.Spatial => ("SPATIAL ", ""),
    _ => ("", ""),
  };

  // ----- full-text search: MATCH (the indexed columns) AGAINST (the query, in a mode) -----------------------------------

  /// <summary>Builds the match predicate, <c>MATCH(columns) AGAINST(query IN BOOLEAN MODE)</c>.</summary>
  /// <remarks>The column list is exactly the index's: MySQL matches a <c>MATCH()</c> against a <c>FULLTEXT</c> index
  /// over precisely those columns and refuses any other list. Boolean mode is used for every kind, since
  /// natural-language mode means any word, ranked, and would answer differently from the other servers.</remarks>
  /// <param name="alias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The predicate.</returns>
  public override string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"MATCH({BuildMatchColumns(alias, index)}) AGAINST({query} IN BOOLEAN MODE)";

  /// <summary>Builds the rank expression, which is the match expression as a value: MySQL's relevance, zero for no
  /// match.</summary>
  /// <param name="alias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The rank expression.</returns>
  public override string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    BuildTextMatch(alias, index, query, kind, label);

  /// <summary>Builds the boolean-mode query from the search words for a match kind: <c>+a +b</c> for all words,
  /// <c>a b</c> for any word, <c>"a b"</c> for a phrase, <c>+a* +b*</c> for prefixes and <c>"a b" @N</c> for words
  /// within a distance.</summary>
  /// <remarks>The words are cleaned to letters and digits before they enter boolean mode. The distance of
  /// <c>"a b" @N</c> counts from the first word's position, one more than the words between, so one is added.</remarks>
  /// <param name="words">The words to search for.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">The largest distance between the words, for a match within a distance.</param>
  /// <returns>The query text, an empty string when no word remains after cleaning, or <see langword="null"/> for a
  /// kind this dialect does not know.</returns>
  public override string? BuildTextQuery(string words, TextMatchKind kind, int distance) {
    var cleaned = SplitTextWords(words);
    if (cleaned.Length == 0)
      return "";
    return kind switch {
      TextMatchKind.AllWords => string.Join(" ", cleaned.Select(w => "+" + w)),
      TextMatchKind.AnyWord => string.Join(" ", cleaned),
      TextMatchKind.Phrase => "\"" + string.Join(" ", cleaned) + "\"",
      TextMatchKind.Prefix => string.Join(" ", cleaned.Select(w => "+" + w + "*")),
      TextMatchKind.Near => "\"" + string.Join(" ", cleaned) + "\" @" + (Math.Max(distance, 1) + 1),
      _ => null,
    };
  }

  private static string BuildMatchColumns(string alias, DbKeyInfo index) =>
    string.Join(", ", index.Columns.Select(c => alias + "." + c.Column.QuotedName));

  /// <summary>Builds a string literal, doubling the backslashes as well as the quotes.</summary>
  /// <remarks>MySQL treats a backslash as an escape inside a string literal unless the server runs with
  /// <c>NO_BACKSLASH_ESCAPES</c>, which is not the default. A value ending in a backslash would otherwise escape the
  /// closing quote, and what follows would be read as SQL.</remarks>
  /// <param name="value">The string to spell.</param>
  /// <returns>The literal.</returns>
  public override string GetLiteral(string value) =>
    StringQuote + value.Replace("\\", "\\\\").Replace("'", "''") + StringQuote;

  /// <summary>Builds a Boolean literal as <c>0</c> or <c>1</c>, which is what a <c>tinyint(1)</c> column holds.
  /// </summary>
  /// <remarks><c>TRUE</c> is an alias MySQL accepts.</remarks>
  /// <param name="value"><see langword="true"/> for the true literal; otherwise, <see langword="false"/>.</param>
  /// <returns>The literal.</returns>
  public override string BooleanLiteral(bool value) => value ? "1" : "0";

  /// <summary>Builds the expression that reads the value at a path as text, with <c>JSON_EXTRACT</c> and
  /// <c>JSON_UNQUOTE</c>, cast to a type when one is given.</summary>
  /// <remarks><c>CAST</c> takes few type names here: <c>SIGNED</c>, <c>DECIMAL</c>, <c>DOUBLE</c>, <c>DATETIME</c>
  /// and <c>DATE</c>. A Boolean is compared with the JSON <c>true</c> itself, since <c>CAST('true' AS SIGNED)</c> is
  /// zero.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="path">The expression that yields the path.</param>
  /// <param name="type">The type the text is cast to, or <see langword="null"/> to read the text itself.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="NotSupportedException">a JSON value cannot be read as the type on MySQL.</exception>
  public override string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    var extracted = $"JSON_EXTRACT({document}, {path})";
    var text = $"JSON_UNQUOTE({extracted})";
    if (type == null)
      return text;
    var clrType = Nullable.GetUnderlyingType(type.TypeDef.ClrType) ?? type.TypeDef.ClrType;
    if (clrType == typeof(bool))
      return $"({extracted} = true)";
    if (clrType == typeof(string))
      return text;
    var castType = clrType == typeof(decimal) ? $"DECIMAL({(type.Precision == 0 ? 18 : type.Precision)},{type.Scale})"
      : clrType == typeof(double) || clrType == typeof(float) ? "DOUBLE"
      : clrType == typeof(DateTime) ? "DATETIME"
      : clrType == typeof(DateOnly) ? "DATE"
      : clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short) || clrType == typeof(byte) ? "SIGNED"
      : throw new NotSupportedException($"A JSON value cannot be read as '{clrType.Name}' on MySQL.");
    return $"CAST({text} AS {castType})";
  }

  /// <summary>Builds the table source for the rows of a JSON document, a bare <c>JSON_TABLE</c> with the columns
  /// declared.</summary>
  /// <remarks>A bare <c>JSON_TABLE</c> may read a column of the row it stands beside, which an inline view around it
  /// could not. A Guid is declared as its hex text and turned back where the column is read.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="columns">The columns of the rows.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  public override string JsonRowsSource(string document, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    // The element itself is at the path $: an array of scalars, one column.
    var declared = string.Join(", ", columns.Select(c =>
      $"{Quote(c.Name)} {(c.Type.ClrType == typeof(Guid) ? "CHAR(32)" : c.Type.TypeSpec)} PATH {(c.IsElement ? "'$'" : $"'$.\"{c.Name}\"'")}"));
    return $"JSON_TABLE({document}, '$[*]' COLUMNS ({declared})) AS {alias}";
  }

  /// <inheritdoc/>
  /// <remarks>A Guid is staged as the hex text the spelling reads, since the column is read through <c>UNHEX</c>.
  /// </remarks>
  public override IReadOnlyList<JsonRowsTempColumn> GetJsonRowsTempColumns(IReadOnlyList<JsonRowsColumn> columns) =>
    [.. columns.Select(c => c.Type.ClrType == typeof(Guid)
      ? new JsonRowsTempColumn(c.Name, "CHAR(32)", RowsTempValueForm.GuidText)
      : new JsonRowsTempColumn(c.Name, c.Type.TypeSpec, RowsTempValueForm.Value))];

  /// <summary>Builds the reference to a column of a JSON rows source, turning a Guid back from its hex text with
  /// <c>UNHEX</c>.</summary>
  /// <remarks>A Guid is <c>binary(16)</c>, which cannot be read out of JSON text, so it travels as hex declared
  /// <c>CHAR(32)</c>.</remarks>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="column">The column to reference.</param>
  /// <returns>The column expression.</returns>
  public override string JsonRowsColumn(string alias, JsonRowsColumn column) =>
    column.Type.ClrType == typeof(Guid) ? $"UNHEX({alias}.{Quote(column.Name)})" : $"{alias}.{Quote(column.Name)}";

  public override string BuildJsonFragment(string document, string path) => $"JSON_EXTRACT({document}, {path})";

  /// <summary>Builds the expression that applies the patches to a column: <c>JSON_SET</c> with every path and its
  /// value, each bound as text and cast to JSON, then <c>JSON_REMOVE</c> with every removed path.</summary>
  /// <param name="column">The column expression the patches apply to.</param>
  /// <param name="patches">The patches to apply.</param>
  /// <param name="bind">A function that binds a value as a parameter and returns its placeholder.</param>
  /// <returns>The expression.</returns>
  public override string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) {
    // The values are set in one call and the removals in one; each function takes every path at once.
    var expression = column;
    var set = patches.Where(p => !p.IsRemoval).ToList();
    if (set.Count > 0)
      expression = $"JSON_SET({expression}, {string.Join(", ", set.Select(p => $"'{p.Path.Replace("'", "''")}', CAST({bind(new JsonText(p.Json))} AS JSON)"))})";
    var removed = patches.Where(p => p.IsRemoval).ToList();
    if (removed.Count > 0)
      expression = $"JSON_REMOVE({expression}, {string.Join(", ", removed.Select(p => $"'{p.Path.Replace("'", "''")}'"))})";
    return expression;
  }
}
