using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Sqlite;

/// <summary>Provides the SQLite SQL dialect.</summary>
/// <remarks>SQLite has no <c>ALTER COLUMN</c> in any form, so the templates that change a column's type, nullability
/// or default are empty; the feature flags keep them from being used, and an empty template produces nothing rather
/// than a plausible wrong statement. A full-text index is an FTS5 table that mirrors the entity's text and is joined to
/// the entity's table by its key.</remarks>
public class SqliteSqlDialect : SqlDialect {
  public SqliteSqlDialect() {
    JsonTextCheck = "json_valid({0})";
    // No table-valued functions a user defines in SQL: one is a virtual-table module written in C.
    TableFunctionSource = null;
    // A stored generated column cannot be added to a table that exists, so every computed column is virtual.
    ComputedStorage = ComputedStorage.Virtual;
    BatchBeginTransaction = "BEGIN;";
    BatchCommitTransaction = "COMMIT;";
    SelectServerTimeUtc = "SELECT strftime('%Y-%m-%dT%H:%M:%f', 'now')";
    SelectServerDatabase = "SELECT file FROM pragma_database_list WHERE name = 'main'";

    // Nothing is written: a single-column primary key declared INTEGER is an alias for the rowid and is assigned on
    // insert, and SqliteTypeRegistry makes the type say so. AUTOINCREMENT is not needed; it only promises never to
    // reuse a deleted row's rowid, at the cost of a second table and a write per insert.
    Identity = "";

    // Since 3.35.
    InsertReturningIdentity = "INSERT INTO {0} ({1})\n  VALUES ({2})\n  RETURNING {3}";

    // No row locks: a write transaction locks the whole file, and a second writer's BEGIN gets BUSY rather than a
    // wait, so a locked read is a plain read and SelectForUpdate and SelectForUpdateSkipLocked stay empty.

    // OFFSET is not allowed without a LIMIT, and a negative LIMIT means no limit.
    PageSkipOnly = "{0} LIMIT -1 OFFSET {1}";

    // Since 3.35.
    CteMaterialized = " MATERIALIZED";
    CteNotMaterialized = " NOT MATERIALIZED";

    // The statements SQLite does not have: blank, not approximated.
    AlterColumnType = "";
    SetNotNull = "";
    DropNotNull = "";
    SetDefault = "";
    DropDefault = "";
    DropConstraint = "";

    FunctionTemplates[NamedOpType.Substring] = "SUBSTR({0}, {1}, {2})";

    // No EXTRACT and no date type: a date is text and strftime reads a part of it as text, so the result is cast back
    // to a number.
    FunctionTemplates[NamedOpType.Year] = "CAST(strftime('%Y', {0}) AS INTEGER)";
    FunctionTemplates[NamedOpType.Month] = "CAST(strftime('%m', {0}) AS INTEGER)";
    FunctionTemplates[NamedOpType.Day] = "CAST(strftime('%d', {0}) AS INTEGER)";
    FunctionTemplates[NamedOpType.Date] = "DATE({0})";

    // CEIL, FLOOR and the other maths exist only where the build enabled the math extension. Removed, so a query
    // using one is refused by name rather than failing at the server.
    FunctionTemplates.Remove(NamedOpType.Ceiling);
    FunctionTemplates.Remove(NamedOpType.Floor);
    FunctionTemplates.Remove(NamedOpType.Power);
    FunctionTemplates.Remove(NamedOpType.Sqrt);
    FunctionTemplates.Remove(NamedOpType.Sign);

    // INSTR is core, and takes the haystack first where the standard POSITION puts the needle first.
    FunctionTemplates[NamedOpType.IndexOf] = "INSTR({0}, {1})";

    // No spatial function of the server's: the one the provider registers on its connections.
    FunctionTemplates[NamedOpType.GeoDistance] = SqliteProvider.GeoDistanceFunction + "({0}, {1})";

    FullTextJoin = FullTextJoinKind.Mirror;

    // No comments on objects, and the CREATE text is kept as written: the expression's hash is a comment after the
    // column's name, where the loader looks for it.
    ComputedColumnSpec = "{0} /* {4} */ {1} GENERATED ALWAYS AS ({2}){3}";
    CommentOnColumn = null;
  }

  /// <summary>Builds a Boolean literal as <c>0</c> or <c>1</c>.</summary>
  /// <remarks><c>TRUE</c> is a keyword only since 3.23, and the column holds the number either way.</remarks>
  /// <param name="value"><see langword="true"/> for the true literal; otherwise, <see langword="false"/>.</param>
  /// <returns>The literal.</returns>
  public override string BooleanLiteral(bool value) => value ? "1" : "0";

  // ----- full-text search: an FTS5 virtual table beside the entity's, joined by the key ---------------------------------

  /// <summary>Builds the statements that create a full-text index: the FTS5 mirror table, its fill from the entity's
  /// table, and the three triggers that keep it in step after an insert, a delete or an update of the text columns.
  /// </summary>
  /// <remarks>SQLite has no index on a table's text; FTS5 is a virtual table holding a copy of it, which a statement
  /// MATCHes and ranks with <c>bm25</c>. The mirror is named as the index is and holds the entity's key as
  /// <c>UNINDEXED</c> columns beside the indexed text; <see cref="BuildTextJoin"/> joins it to the entity's table by
  /// that key. FTS5's external-content form is not used, since it keys the mirror by the rowid, which <c>VACUUM</c>
  /// may renumber for a table not keyed by an <c>INTEGER PRIMARY KEY</c>.</remarks>
  /// <param name="index">The full-text index to create.</param>
  /// <returns>The statements, in the order they run.</returns>
  /// <exception cref="NotSupportedException">the table has no primary key.</exception>
  public override IReadOnlyList<string>? BuildFullTextIndex(DbKeyInfo index) {
    var table = index.Table;
    var key = table.PrimaryKey
      ?? throw new NotSupportedException($"A full-text index on SQLite mirrors the table's rows by their key, and '{table.Name}' has none.");
    var mirror = Quote(index.Name);
    var keyColumns = key.Columns.Select(c => c.Column.QuotedName).ToList();
    var textColumns = index.Columns.Select(c => c.Column.QuotedName).ToList();
    var columns = string.Join(", ", keyColumns.Concat(textColumns));
    var newValues = string.Join(", ", keyColumns.Concat(textColumns).Select(c => "new." + c));
    var sameKey = string.Join(" AND ", keyColumns.Select(c => $"{c} = old.{c}"));
    var assignments = string.Join(", ", textColumns.Select(c => $"{c} = new.{c}"));
    return [
      $"CREATE VIRTUAL TABLE {mirror} USING fts5({string.Join(", ", keyColumns.Select(c => c + " UNINDEXED").Concat(textColumns))})",
      $"INSERT INTO {mirror} ({columns}) SELECT {columns} FROM {table.RefName}",
      $"CREATE TRIGGER {Quote(index.Name + "_ai")} AFTER INSERT ON {table.RefName} BEGIN INSERT INTO {mirror} ({columns}) VALUES ({newValues}); END",
      $"CREATE TRIGGER {Quote(index.Name + "_ad")} AFTER DELETE ON {table.RefName} BEGIN DELETE FROM {mirror} WHERE {sameKey}; END",
      $"CREATE TRIGGER {Quote(index.Name + "_au")} AFTER UPDATE OF {string.Join(", ", textColumns)} ON {table.RefName} BEGIN UPDATE {mirror} SET {assignments} WHERE {sameKey}; END",
    ];
  }

  public override IReadOnlyList<string>? BuildDropFullTextIndex(DbKeyInfo index) => [
    $"DROP TRIGGER IF EXISTS {Quote(index.Name + "_ai")}",
    $"DROP TRIGGER IF EXISTS {Quote(index.Name + "_ad")}",
    $"DROP TRIGGER IF EXISTS {Quote(index.Name + "_au")}",
    $"DROP TABLE IF EXISTS {Quote(index.Name)}",
  ];

  /// <summary>Builds the inner join of the full-text mirror to the entity's table on the key the mirror carries.
  /// </summary>
  /// <param name="joinAlias">The alias the mirror gets in the join.</param>
  /// <param name="tableAlias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression, or <see langword="null"/>; the join does not use it.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <returns>The join clause.</returns>
  /// <exception cref="NotSupportedException">the table has no primary key.</exception>
  public override string BuildTextJoin(string joinAlias, string tableAlias, DbKeyInfo index, string? query, TextMatchKind kind) {
    var key = index.Table.PrimaryKey
      ?? throw new NotSupportedException($"'{index.Table.Name}' has no primary key for its full-text index to be joined by.");
    var onKey = string.Join(" AND ", key.Columns.Select(c => $"{joinAlias}.{c.Column.QuotedName} = {tableAlias}.{c.Column.QuotedName}"));
    return $"INNER JOIN {Quote(index.Name)} {joinAlias} ON {onKey}";
  }

  /// <summary>Builds the match predicate, a <c>MATCH</c> against the mirror's hidden column that is named as the
  /// table is.</summary>
  /// <remarks>A <c>MATCH</c> stands in a <c>WHERE</c> under <c>AND</c> and nowhere else: SQLite answers it through
  /// the virtual table's own lookup and refuses it under <c>NOT</c> or <c>OR</c>, at the server.</remarks>
  /// <param name="alias">The alias of the mirror in the join.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The predicate.</returns>
  public override string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"{alias}.{Quote(index.Name)} MATCH {query}";

  /// <summary>Builds the match predicate without a join: the row's key among the keys of the mirror's rows that
  /// match.</summary>
  /// <param name="tableAlias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <returns>The predicate, a row value on each side for a key of several columns.</returns>
  /// <exception cref="NotSupportedException">the table has no primary key.</exception>
  public override string? BuildTextMatchByKey(string tableAlias, DbKeyInfo index, string query) {
    var key = index.Table.PrimaryKey
      ?? throw new NotSupportedException(
        $"'{index.Table.Name}' has no primary key for its full-text index to be read by.");
    var mirror = Quote(index.Name);
    var own = string.Join(", ", key.Columns.Select(c => $"{tableAlias}.{c.Column.QuotedName}"));
    var mirrored = string.Join(", ", key.Columns.Select(c => c.Column.QuotedName));
    if (key.Columns.Count > 1)
      own = $"({own})";
    return $"{own} IN (SELECT {mirrored} FROM {mirror} WHERE {mirror} MATCH {query})";
  }

  /// <summary>Builds the rank expression, <c>-bm25</c> of the mirror.</summary>
  /// <remarks>FTS5's rank is a cost, lower for a better match, so it is negated to read higher-is-better as on the
  /// other servers. It is the rank of the statement's <c>MATCH</c> on the same mirror, and zero where there is none;
  /// the query given here is not what SQLite ranks by.</remarks>
  /// <param name="alias">The alias of the mirror in the join.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query expression.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="label">The number of the match within the statement.</param>
  /// <returns>The rank expression.</returns>
  public override string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    $"-bm25({alias}.{Quote(index.Name)})";

  /// <summary>Builds the FTS5 query from the search words for a match kind.</summary>
  /// <remarks>Every word is quoted, since a bare one that happens to be <c>AND</c>, <c>OR</c>, <c>NOT</c> or
  /// <c>NEAR</c> would be read as the operator. <c>NEAR(a b, N)</c> counts the tokens between the words, where the
  /// distance given counts the positions apart, so one is taken off.</remarks>
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
      TextMatchKind.AllWords => string.Join(" ", quoted),
      TextMatchKind.AnyWord => string.Join(" OR ", quoted),
      TextMatchKind.Phrase => "\"" + string.Join(" ", cleaned) + "\"",
      TextMatchKind.Prefix => string.Join(" ", quoted.Select(w => w + "*")),
      TextMatchKind.Near => cleaned.Length == 1 ? quoted[0] : $"NEAR({string.Join(" ", quoted)}, {Math.Max(distance, 1) - 1})",
      _ => null,
    };
  }

  /// <summary>Builds the expression that reads the value at a path with <c>json_extract</c>, converted only where
  /// SQLite's affinity would not do it.</summary>
  /// <remarks><c>json_extract</c> answers with the value as the JSON holds it: a number as a number, a Boolean as 0 or
  /// 1, text as text. A date-time's <c>T</c> becomes the space a bound <c>DateTime</c> is written with, since a date
  /// is text compared as text.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="path">The expression that yields the path.</param>
  /// <param name="type">The type the value is read as, or <see langword="null"/> to read it as the JSON holds it.
  /// </param>
  /// <returns>The expression.</returns>
  public override string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    var text = $"json_extract({document}, {path})";
    if (type == null)
      return text;
    var clrType = Nullable.GetUnderlyingType(type.TypeDef.ClrType) ?? type.TypeDef.ClrType;
    if (clrType == typeof(DateTime))
      return $"replace({text}, 'T', ' ')";
    if (clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float))
      return $"CAST({text} AS REAL)";
    if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short) || clrType == typeof(byte))
      return $"CAST({text} AS INTEGER)";
    return text;
  }

  /// <summary>Builds the table source for the rows of a JSON document, <c>json_each</c>, which hands out each
  /// element whole under <c>value</c>.</summary>
  /// <remarks>A column is read out of the element where it is used, in <see cref="JsonRowsColumn"/>, since a
  /// sub-select around the function could not read a column of the row it stands beside. The types are the JSON's
  /// own.</remarks>
  /// <param name="document">The expression that yields the JSON document.</param>
  /// <param name="columns">The columns of the rows.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  public override string JsonRowsSource(string document, IReadOnlyList<JsonRowsColumn> columns, string alias) =>
    $"json_each({document}) AS {alias}";

  /// <inheritdoc/>
  /// <remarks>The spelling reads each row out of <c>value</c>, as <c>json_each</c> yields it: a row is staged as its
  /// JSON object, an element as itself.</remarks>
  public override IReadOnlyList<JsonRowsTempColumn> GetJsonRowsTempColumns(IReadOnlyList<JsonRowsColumn> columns) =>
    [new JsonRowsTempColumn("value", "TEXT",
      columns is [{ IsElement: true, Type.ClrType: var element }] ? (element == typeof(Guid) ? RowsTempValueForm.GuidText : RowsTempValueForm.Value)
      : RowsTempValueForm.RowJson)];

  /// <summary>Builds the read of a column out of a <c>json_each</c> row: the element itself for an array of scalars,
  /// a property extracted from it otherwise.</summary>
  /// <remarks>A Guid is a blob, so it travels as hex and is turned back with <c>unhex</c>; the other values take the
  /// affinity of what they are compared with.</remarks>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="column">The column to read.</param>
  /// <returns>The column expression.</returns>
  public override string JsonRowsColumn(string alias, JsonRowsColumn column) {
    var read = column.IsElement ? $"{alias}.value" : $"json_extract({alias}.value, '$.\"{column.Name}\"')";
    return column.Type.ClrType == typeof(Guid) ? $"unhex({read})" : read;
  }

  public override string BuildJsonFragment(string document, string path) => $"json_extract({document}, {path})";

  /// <summary>Builds the expression that applies the patches to a column: <c>json_set</c> with every path and its
  /// value, each bound as text and read as JSON by <c>json()</c>, then <c>json_remove</c> with every removed path.
  /// </summary>
  /// <param name="column">The column expression the patches apply to.</param>
  /// <param name="patches">The patches to apply.</param>
  /// <param name="bind">A function that binds a value as a parameter and returns its placeholder.</param>
  /// <returns>The expression.</returns>
  public override string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) {
    // The values are set in one call and the removals in one; each function takes every path at once.
    var expression = column;
    var set = patches.Where(p => !p.IsRemoval).ToList();
    if (set.Count > 0)
      expression = $"json_set({expression}, {string.Join(", ", set.Select(p => $"'{p.Path.Replace("'", "''")}', json({bind(new JsonText(p.Json))})"))})";
    var removed = patches.Where(p => p.IsRemoval).ToList();
    if (removed.Count > 0)
      expression = $"json_remove({expression}, {string.Join(", ", removed.Select(p => $"'{p.Path.Replace("'", "''")}'"))})";
    return expression;
  }
}
