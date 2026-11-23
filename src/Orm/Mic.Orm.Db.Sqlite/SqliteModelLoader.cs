using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Sqlite;

/// <summary>Reads a SQLite schema into a database model.</summary>
/// <remarks>SQLite has no <c>information_schema</c>: every query reads <c>sqlite_master</c>, which holds the CREATE
/// statements verbatim, and the <c>pragma</c> functions, and aliases their columns to the names
/// <see cref="DbModelLoader"/> expects. Views and triggers are read; SQLite has no stored routines, packages,
/// user-defined types, synonyms or comments, and the queries for those answer with nothing.</remarks>
public class SqliteModelLoader(DbSettings settings) : DbModelLoader(settings) {
  /// <summary>Specifies the query that lists the FTS5 tables <see cref="SqliteSqlDialect.BuildFullTextIndex"/>
  /// made as full-text indexes, each with the table it mirrors.</summary>
  /// <remarks>The mirrored table is read off the index's insert trigger, the one object that names both.</remarks>
  const string TextIndexes = @"
SELECT v.name AS index_name, g.tbl_name AS table_name
FROM sqlite_master v JOIN sqlite_master g ON g.type = 'trigger' AND g.name = v.name || '_ai'
WHERE v.type = 'table' AND v.sql LIKE 'CREATE VIRTUAL TABLE%USING fts5%'";

  // The hash of a computed column's expression, written as a comment after the column's name in the CREATE text:
  // the marker's prefix, and its length with the sixteen hex digits that follow.
  const string HashMarkerPrefix = Mic.Orm.Db.Model.Upgrades.DefinitionHashHelper.Prefix;
  const int HashMarkerLength = 22;

  /// <summary>Specifies the query that lists the tables a user made, each with its CREATE text.</summary>
  /// <remarks>Everything SQLite keeps for itself is named <c>sqlite_%</c>. A full-text index's FTS5 table and the
  /// five shadow tables FTS5 keeps under it are the index, not tables of the model. The CREATE text is the only place
  /// that says whether a table is <c>WITHOUT ROWID</c>, which <see cref="GetColumnsAsync"/> needs.</remarks>
  const string UserTables = $@"
SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
  AND name NOT IN (SELECT index_name FROM ({TextIndexes}))
  AND name NOT IN (SELECT index_name || s.suffix FROM ({TextIndexes}),
                   (SELECT '_data' AS suffix UNION ALL SELECT '_idx' UNION ALL SELECT '_content'
                    UNION ALL SELECT '_docsize' UNION ALL SELECT '_config') s)";

  /// <summary>Asynchronously reports the one nameless schema.</summary>
  /// <remarks>Returning a row keeps the base class's schema filtering meaningful.</remarks>
  protected override Task<InfoTable> GetSchemasAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync("SELECT '' AS schema_name", cancellation);

  protected override Task<InfoTable> GetTablesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($"SELECT '' AS table_schema, name AS table_name FROM ({UserTables})", cancellation);

  /// <summary>Asynchronously reads the columns, with the type as declared and an identity flag derived from the
  /// key.</summary>
  /// <remarks><c>pragma table_info</c> reports the type exactly as declared, <c>varchar(50)</c> rather than
  /// <c>TEXT</c>, so a size survives; <see cref="BuildColumn"/> parses it. SQLite has no identity flag: an identity
  /// column is a single-column primary key declared exactly <c>INTEGER</c> in a table that has a rowid.
  /// <c>WITHOUT ROWID</c> is found only in the CREATE text, since no pragma reports it.</remarks>
  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT '' AS table_schema, t.name AS table_name, c.name AS column_name, c.type AS data_type,
       CASE WHEN c.""notnull"" = 0 THEN 1 ELSE 0 END AS is_nullable,
       c.dflt_value AS column_default,
       CASE WHEN c.pk = 1 AND upper(trim(c.type)) = 'INTEGER'
                 AND (SELECT COUNT(*) FROM pragma_table_info(t.name) k WHERE k.pk > 0) = 1
                 AND upper(t.sql) NOT LIKE '%WITHOUT ROWID%'
            THEN 1 ELSE 0 END AS is_identity,
       CASE WHEN c.hidden IN (2, 3) THEN 1 ELSE 0 END AS is_generated,
       CASE WHEN c.hidden IN (2, 3) AND instr(t.sql, '""' || c.name || '"" /* {HashMarkerPrefix}') > 0
            THEN substr(t.sql, instr(t.sql, '""' || c.name || '"" /* {HashMarkerPrefix}') + length(c.name) + 6,
                        {HashMarkerLength})
            END AS column_comment
FROM ({UserTables}) t JOIN pragma_table_xinfo(t.name) c
WHERE c.hidden IN (0, 2, 3)
ORDER BY t.name, c.cid", cancellation);

  /// <summary>Builds a column from its row, parsing the declared type as a whole type spec.</summary>
  /// <remarks>SQLite reports the type string it was given rather than a type name and its arguments, so the spec is
  /// parsed instead of looked up by name.</remarks>
  /// <param name="row">The row of the column query.</param>
  /// <returns>The column.</returns>
  protected override DbColumnInfo BuildColumn(InfoRow row) {
    var name = row.GetString("column_name")!;
    var declared = row.GetString("data_type") ?? "";
    var typeInfo = declared.Length == 0 ? null : Provider.TypeRegistry.ParseSpec(declared, out _);

    var column = new DbColumnInfo(name, typeInfo?.ToTypeRef() ?? new DbTypeRef(declared),
        row.GetBool("is_nullable")) {
      Size = typeInfo?.Size ?? 0,
      Precision = typeInfo?.Precision ?? 0,
      Scale = typeInfo?.Scale ?? 0,
      DefaultExpression = NormalizeDefault(row.GetString("column_default")),
    };
    if (row.GetBool("is_identity"))
      column.DbFlags |= DbColumnFlags.Identity;
    if (row.GetBool("is_generated")) {
      column.DbFlags |= DbColumnFlags.Computed;
      column.StoredDefinitionHash =
        Mic.Orm.Db.Model.Upgrades.DefinitionHashHelper.FindMarker(row.GetString("column_comment"));
    }
    return column;
  }

  // ----- keys ---------------------------------------------------------------------------------------------------

  /// <summary>Asynchronously reports each table's primary key as a constraint named <c>PK</c>.</summary>
  /// <remarks>A primary key is a flag on a column, with <c>pk</c> holding the column's position in the key, and has
  /// no name. The base class groups constraint columns by table, so every table's key may carry the same name.
  /// </remarks>
  protected override Task<InfoTable> GetTableConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT DISTINCT '' AS table_schema, t.name AS table_name, 'PK' AS constraint_name,
       'PRIMARY KEY' AS constraint_type
FROM ({UserTables}) t JOIN pragma_table_info(t.name) c
WHERE c.pk > 0", cancellation);

  protected override Task<InfoTable> GetConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT '' AS table_schema, t.name AS table_name, 'PK' AS constraint_name, c.name AS column_name
FROM ({UserTables}) t JOIN pragma_table_info(t.name) c
WHERE c.pk > 0
ORDER BY t.name, c.pk", cancellation);

  /// <summary>Asynchronously reads the indexes made with <c>CREATE INDEX</c> and the full-text indexes.</summary>
  /// <remarks><c>origin</c> says where an index came from: <c>c</c> for <c>CREATE INDEX</c>, <c>pk</c> and <c>u</c>
  /// for the ones a constraint made, which are already read as constraints. The filter of a partial index is in no
  /// pragma, so it is cut out of the CREATE statement SQLite stored verbatim. A full-text index is read from the
  /// FTS5 table that is it: the table it mirrors is named by its insert trigger, and its columns are the FTS5 table's
  /// less the mirrored key.</remarks>
  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT '' AS table_schema, t.name AS table_name, i.name AS index_name,
       i.""unique"" AS is_unique, 0 AS is_primary, 0 AS is_clustered,
       CASE WHEN i.partial = 1 THEN
         substr(m.sql, instr(m.sql, ' WHERE ') + 7)
       ELSE NULL END AS filter,
       NULL AS index_type
FROM ({UserTables}) t
     JOIN pragma_index_list(t.name) i
     JOIN sqlite_master m ON m.type = 'index' AND m.name = i.name
WHERE i.origin = 'c'
UNION ALL
SELECT '' AS table_schema, f.table_name, f.index_name, 0 AS is_unique, 0 AS is_primary, 0 AS is_clustered,
       NULL AS filter, 'fulltext' AS index_type
FROM ({TextIndexes}) f", cancellation);

  protected override Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT '' AS table_schema, t.name AS table_name, i.name AS index_name,
       x.name AS column_name, x.desc AS is_descending, 0 AS is_included, x.seqno AS seq
FROM ({UserTables}) t
     JOIN pragma_index_list(t.name) i
     JOIN pragma_index_xinfo(i.name) x
WHERE i.origin = 'c' AND x.key = 1
UNION ALL
SELECT '' AS table_schema, f.table_name, f.index_name, c.name AS column_name, 0 AS is_descending, 0 AS is_included,
       c.cid AS seq
FROM ({TextIndexes}) f JOIN pragma_table_info(f.index_name) c
WHERE c.name NOT IN (SELECT k.name FROM pragma_table_info(f.table_name) k WHERE k.pk > 0)
ORDER BY table_name, index_name, seq", cancellation);

  /// <summary>Asynchronously reports no sequences, since SQLite has none.</summary>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));

  // ----- foreign keys ------------------------------------------------------------------------------------------

  /// <summary>Specifies the query that lists the foreign keys with their columns and target tables, from
  /// <c>pragma foreign_key_list</c>.</summary>
  /// <remarks>SQLite keeps no name for a foreign key. The name is synthesised so the columns can be grouped and is
  /// not the name MIC generated; without <c>NamedRefConstraints</c> the comparer matches foreign keys by their
  /// columns and target rather than by name.</remarks>
  const string ForeignKeys = @"
SELECT '' AS table_schema, t.name AS table_name,
       'FK_' || t.name || '_' || f.id AS constraint_name,
       '' AS target_schema, f.""table"" AS target_table, f.on_delete AS delete_rule,
       f.""from"" AS column_name, f.""to"" AS target_column, f.seq AS seq
FROM ({0}) t JOIN pragma_foreign_key_list(t.name) f";

  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT DISTINCT table_schema, table_name, constraint_name, target_schema, target_table, delete_rule
FROM ({string.Format(ForeignKeys, UserTables)})", cancellation);

  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, constraint_name, column_name, target_column
FROM ({string.Format(ForeignKeys, UserTables)})
ORDER BY table_name, constraint_name, seq", cancellation);

  // ----- what a server holds beyond tables --------------------------------------------------------------------------

  /// <summary>Asynchronously reads the views from <c>sqlite_master</c>.</summary>
  /// <remarks>The definition is the whole CREATE statement rather than just the query, since SQLite stores what it
  /// was given.</remarks>
  protected override Task<InfoTable> GetViewsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(@"
SELECT '' AS table_schema, name AS table_name, sql AS view_definition, 0 AS is_materialized
FROM sqlite_master WHERE type = 'view'", cancellation);

  /// <summary>Asynchronously reads the columns of the views, from the same pragma a table's columns come from.
  /// </summary>
  protected override Task<InfoTable> GetViewColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(@"
SELECT '' AS table_schema, v.name AS table_name, c.name AS column_name, c.type AS data_type,
       CASE WHEN c.""notnull"" = 0 THEN 1 ELSE 0 END AS is_nullable,
       c.dflt_value AS column_default, 0 AS is_identity
FROM sqlite_master v JOIN pragma_table_info(v.name) c
WHERE v.type = 'view'
ORDER BY v.name, c.cid", cancellation);

  /// <summary>Asynchronously reads the triggers from <c>sqlite_master</c>, leaving out those of the full-text
  /// indexes.</summary>
  /// <remarks>The timing and the events are not reported: <c>sqlite_master</c> keeps a trigger's name, its table and
  /// the CREATE text, and nothing else. Both are left empty rather than parsed out of the SQL, which carries them for
  /// a reader.</remarks>
  protected override Task<InfoTable> GetTriggersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT name AS trigger_name, '' AS table_schema, tbl_name AS table_name,
       '' AS action_timing, '' AS event_manipulation, 'ROW' AS action_orientation,
       sql AS action_statement
FROM sqlite_master WHERE type = 'trigger'
  AND name NOT IN (SELECT index_name || s.suffix FROM ({TextIndexes}),
                   (SELECT '_ai' AS suffix UNION ALL SELECT '_ad' UNION ALL SELECT '_au') s)", cancellation);

  // SQLite has none of these; an empty result lets the base's interpretation run and find nothing.
  protected override Task<InfoTable> GetRoutinesAsync(CancellationToken cancellation) => Empty();
  protected override Task<InfoTable> GetRoutineParametersAsync(CancellationToken cancellation) => Empty();
  protected override Task<InfoTable> GetDomainsAsync(CancellationToken cancellation) => Empty();

  /// <summary>Asynchronously reads the named check constraints out of each table's CREATE text, the only place
  /// SQLite keeps them.</summary>
  /// <remarks>A check with no name of its own, a column's or a table's, is not read: nothing could name it.</remarks>
  protected override async Task<InfoTable> GetCheckConstraintsAsync(CancellationToken cancellation) {
    var checks = new InfoTable(["table_schema", "table_name", "constraint_name", "check_clause"]);
    foreach (var table in (await ExecuteSelectAsync($"SELECT name, sql FROM ({UserTables})", cancellation)).Rows) {
      var createText = table.GetString("sql") ?? "";
      foreach (System.Text.RegularExpressions.Match named in System.Text.RegularExpressions.Regex.Matches(
          createText, @"CONSTRAINT\s+(?:""([^""]+)""|(\w+))\s+CHECK",
          System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        checks.AddRow(["", table.GetString("name"),
          named.Groups[1].Success ? named.Groups[1].Value : named.Groups[2].Value,
          ReadBracketedText(createText, named.Index + named.Length)]);
    }
    return checks;
  }

  private static Task<InfoTable> Empty() => Task.FromResult(new InfoTable([]));

  /// <summary>Reads the text between the bracket that follows a position and the bracket that closes it.</summary>
  /// <param name="sql">The statement.</param>
  /// <param name="start">The position the opening bracket follows, white space apart.</param>
  /// <returns>The text between the two brackets, trimmed; an empty string if no bracket follows or none closes
  /// it.</returns>
  /// <remarks>A bracket inside quoted text or a quoted name closes nothing.</remarks>
  private static string ReadBracketedText(string sql, int start) {
    var open = start;
    while (open < sql.Length && char.IsWhiteSpace(sql[open]))
      open++;
    if (open >= sql.Length || sql[open] != '(')
      return "";
    var depth = 0;
    for (var i = open; i >= 0 && i < sql.Length; i++)
      switch (sql[i]) {
        case '\'' or '"' or '`':
          i = FindQuoteEnd(sql, i);
          break;
        case '[':
          i = sql.IndexOf(']', i);
          break;
        case '(':
          depth++;
          break;
        case ')':
          if (--depth == 0)
            return sql[(open + 1)..i].Trim();
          break;
      }
    return "";
  }

  /// <summary>Finds the quote that closes the one at a position, passing a doubled quote as a character of the
  /// text.</summary>
  /// <returns>The position of the closing quote; -1 if the text is not closed.</returns>
  private static int FindQuoteEnd(string sql, int start) {
    var quote = sql[start];
    for (var i = start + 1; i < sql.Length; i++) {
      if (sql[i] != quote)
        continue;
      if (i + 1 < sql.Length && sql[i + 1] == quote)
        i++;
      else
        return i;
    }
    return -1;
  }
}
