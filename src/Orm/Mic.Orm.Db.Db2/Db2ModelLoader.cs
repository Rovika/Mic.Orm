using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Db2;

/// <summary>Reads a Db2 schema into a database model.</summary>
/// <remarks>Db2 has no <c>information_schema</c>, so every query is replaced, aliasing the columns of the
/// <c>SYSCAT</c> views to the names <see cref="DbModelLoader"/> expects; the interpretation of the rows is
/// inherited. A materialized query table is read as a materialized view, an alias as a synonym and a distinct type
/// as a domain. The indexes read are the regular and the clustering ones; the indexes over XML and the block
/// indexes of a multidimensional table are left out.</remarks>
public class Db2ModelLoader(DbSettings settings) : DbModelLoader(settings) {
  // The types of SYSCAT.TABLES.TYPE: a table; a view and a materialized query table.
  const string TableTypes = "'T'";
  const string ViewTypes = "'V', 'S'";
  // The indexes that are an ordering over columns: regular and clustering. INDEXTYPE is CHAR(4), compared padded.
  const string ColumnIndexTypes = "'REG', 'CLUS'";

  protected override Task<InfoTable> GetSchemasAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(
      $"SELECT SCHEMANAME AS schema_name FROM SYSCAT.SCHEMATA WHERE {SchemaFilter("SCHEMANAME")}", cancellation);

  protected override Task<InfoTable> GetTablesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TABSCHEMA AS table_schema, TABNAME AS table_name
FROM SYSCAT.TABLES
WHERE TYPE IN ({TableTypes}) AND {SchemaFilter("TABSCHEMA")}", cancellation);

  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    GetColumnsOfAsync(TableTypes, cancellation);

  /// <summary>Asynchronously reads the primary, unique and foreign key constraints, spelling out the kind Db2 keeps
  /// as one letter.</summary>
  protected override Task<InfoTable> GetTableConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TABSCHEMA AS table_schema, TABNAME AS table_name, CONSTNAME AS constraint_name,
       CASE TYPE WHEN 'P' THEN 'PRIMARY KEY' WHEN 'U' THEN 'UNIQUE' WHEN 'F' THEN 'FOREIGN KEY' END AS constraint_type
FROM SYSCAT.TABCONST
WHERE TYPE IN ('P', 'U', 'F') AND {SchemaFilter("TABSCHEMA")}", cancellation);

  protected override Task<InfoTable> GetConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TABSCHEMA AS table_schema, TABNAME AS table_name, CONSTNAME AS constraint_name, COLNAME AS column_name
FROM SYSCAT.KEYCOLUSE
WHERE {SchemaFilter("TABSCHEMA")}
ORDER BY TABSCHEMA, TABNAME, CONSTNAME, COLSEQ", cancellation);

  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TABSCHEMA AS table_schema, TABNAME AS table_name, CONSTNAME AS constraint_name,
       REFTABSCHEMA AS target_schema, REFTABNAME AS target_table,
       CASE DELETERULE WHEN 'C' THEN 'CASCADE' WHEN 'N' THEN 'SET NULL' WHEN 'R' THEN 'RESTRICT' ELSE 'NO ACTION' END
         AS delete_rule
FROM SYSCAT.REFERENCES
WHERE {SchemaFilter("TABSCHEMA")}", cancellation);

  /// <summary>Asynchronously reads the foreign key columns, pairing both sides by their position within the key.
  /// </summary>
  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT r.TABSCHEMA AS table_schema, r.TABNAME AS table_name, r.CONSTNAME AS constraint_name,
       k.COLNAME AS column_name, t.COLNAME AS target_column
FROM SYSCAT.REFERENCES r
     JOIN SYSCAT.KEYCOLUSE k ON k.TABSCHEMA = r.TABSCHEMA AND k.TABNAME = r.TABNAME AND k.CONSTNAME = r.CONSTNAME
     JOIN SYSCAT.KEYCOLUSE t ON t.TABSCHEMA = r.REFTABSCHEMA AND t.TABNAME = r.REFTABNAME
                            AND t.CONSTNAME = r.REFKEYNAME AND t.COLSEQ = k.COLSEQ
WHERE {SchemaFilter("r.TABSCHEMA")}
ORDER BY r.TABSCHEMA, r.TABNAME, r.CONSTNAME, k.COLSEQ", cancellation);

  /// <summary>Asynchronously reads the indexes, marking the index a primary key or a unique constraint depends on,
  /// whose name may be one the server made.</summary>
  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT i.TABSCHEMA AS table_schema, i.TABNAME AS table_name, i.INDNAME AS index_name,
       CASE WHEN i.UNIQUERULE IN ('P', 'U') THEN 1 ELSE 0 END AS is_unique,
       CASE WHEN i.UNIQUERULE = 'P' OR EXISTS (
              SELECT 1 FROM SYSCAT.CONSTDEP d
              WHERE d.BTYPE = 'I' AND d.BSCHEMA = i.INDSCHEMA AND d.BNAME = i.INDNAME
                AND d.TABSCHEMA = i.TABSCHEMA AND d.TABNAME = i.TABNAME)
            THEN 1 ELSE 0 END AS is_primary,
       0 AS is_clustered,
       CAST(NULL AS VARCHAR(1)) AS ""filter"",
       i.REMARKS AS index_comment
FROM SYSCAT.INDEXES i
WHERE i.INDEXTYPE IN ({ColumnIndexTypes}) AND {SchemaFilter("i.TABSCHEMA")}", cancellation);

  /// <summary>Asynchronously reads the index columns; a column a unique index only carries is reported as included.
  /// </summary>
  protected override Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT i.TABSCHEMA AS table_schema, i.TABNAME AS table_name, i.INDNAME AS index_name, c.COLNAME AS column_name,
       CASE c.COLORDER WHEN 'D' THEN 1 ELSE 0 END AS is_descending,
       CASE c.COLORDER WHEN 'I' THEN 1 ELSE 0 END AS is_included
FROM SYSCAT.INDEXCOLUSE c
     JOIN SYSCAT.INDEXES i ON i.INDSCHEMA = c.INDSCHEMA AND i.INDNAME = c.INDNAME
WHERE i.INDEXTYPE IN ({ColumnIndexTypes}) AND {SchemaFilter("i.TABSCHEMA")}
ORDER BY i.TABSCHEMA, i.TABNAME, i.INDNAME, c.COLSEQ", cancellation);

  /// <summary>Asynchronously reads the sequences somebody created, leaving out those behind identity columns.
  /// </summary>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT SEQSCHEMA AS sequence_schema, SEQNAME AS sequence_name, START AS start_value, INCREMENT AS ""increment""
FROM SYSCAT.SEQUENCES
WHERE SEQTYPE = 'S' AND {SchemaFilter("SEQSCHEMA")}", cancellation);

  // ----- what a server holds beyond tables --------------------------------------------------------------------------

  /// <summary>Asynchronously reads the views and the materialized query tables, each with the statement that
  /// created it and its comment.</summary>
  protected override Task<InfoTable> GetViewsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT v.VIEWSCHEMA AS table_schema, v.VIEWNAME AS table_name, v.TEXT AS view_definition,
       CASE t.TYPE WHEN 'S' THEN 1 ELSE 0 END AS is_materialized, t.REMARKS AS view_comment
FROM SYSCAT.VIEWS v
     JOIN SYSCAT.TABLES t ON t.TABSCHEMA = v.VIEWSCHEMA AND t.TABNAME = v.VIEWNAME
WHERE {SchemaFilter("v.VIEWSCHEMA")}", cancellation);

  protected override Task<InfoTable> GetViewColumnsAsync(CancellationToken cancellation) =>
    GetColumnsOfAsync(ViewTypes, cancellation);

  /// <summary>Asynchronously loads the views, leaving a materialized query table with its query as its source.
  /// </summary>
  /// <remarks>The catalog keeps the whole statement, <c>CREATE TABLE … AS (query) DATA INITIALLY DEFERRED …</c>,
  /// whose options follow the query and are not part of a select.</remarks>
  protected override async Task LoadViewsAsync(CancellationToken cancellation) {
    await base.LoadViewsAsync(cancellation);
    foreach (var view in Model.Views)
      if (view.IsMaterialized && view.Source != null)
        view.Source = GetMaterializedQuery(view.Source);
  }

  /// <summary>Asynchronously reads the procedures and functions somebody created, each with the statement that
  /// created it.</summary>
  /// <remarks>The specific name tells overloads apart, and is one the server made where the statement gave none.
  /// </remarks>
  protected override Task<InfoTable> GetRoutinesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT ROUTINESCHEMA AS routine_schema, ROUTINENAME AS routine_name, SPECIFICNAME AS specific_name,
       CASE ROUTINETYPE WHEN 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS routine_type,
       FUNCTIONTYPE AS function_type, CAST(NULL AS VARCHAR(1)) AS return_type, TEXT AS routine_definition
FROM SYSCAT.ROUTINES
WHERE ROUTINETYPE IN ('P', 'F') AND {SchemaFilter("ROUTINESCHEMA")}", cancellation);

  /// <summary>Gets the kind of routine a catalog row describes, telling a table function from a scalar one.</summary>
  /// <param name="row">The row of the routine query.</param>
  /// <returns>One of the enumeration values that specifies the kind.</returns>
  protected override DbRoutineKind GetRoutineKind(InfoRow row) {
    var kind = base.GetRoutineKind(row);
    if (kind == DbRoutineKind.Function && row.GetString("function_type") == "T")
      kind = DbRoutineKind.TableFunction;
    return kind;
  }

  /// <summary>Asynchronously reads the routine parameters, and the value a scalar function returns as the parameter
  /// at position 0.</summary>
  protected override Task<InfoTable> GetRoutineParametersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT p.ROUTINESCHEMA AS specific_schema, p.SPECIFICNAME AS specific_name, p.PARMNAME AS parameter_name,
       LOWER(p.TYPENAME) AS data_type,
       CASE p.ROWTYPE WHEN 'O' THEN 'OUT' WHEN 'B' THEN 'INOUT' ELSE 'IN' END AS parameter_mode,
       CASE p.ROWTYPE WHEN 'C' THEN 0 ELSE p.ORDINAL END AS ordinal_position
FROM SYSCAT.ROUTINEPARMS p
     JOIN SYSCAT.ROUTINES r ON r.ROUTINESCHEMA = p.ROUTINESCHEMA AND r.SPECIFICNAME = p.SPECIFICNAME
WHERE (p.ROWTYPE IN ('P', 'O', 'B') OR (p.ROWTYPE = 'C' AND r.FUNCTIONTYPE = 'S'))
  AND r.ROUTINETYPE IN ('P', 'F') AND {SchemaFilter("p.ROUTINESCHEMA")}
ORDER BY p.ROUTINESCHEMA, p.SPECIFICNAME, ordinal_position", cancellation);

  /// <summary>Asynchronously loads the result columns of the table functions, which the catalog lists among the
  /// function's parameters.</summary>
  protected override async Task LoadRoutineResultsAsync(CancellationToken cancellation) {
    var rows = await ExecuteSelectAsync($@"
SELECT p.ROUTINESCHEMA AS specific_schema, p.SPECIFICNAME AS specific_name, p.PARMNAME AS column_name,
       LOWER(p.TYPENAME) AS type_name
FROM SYSCAT.ROUTINEPARMS p
     JOIN SYSCAT.ROUTINES r ON r.ROUTINESCHEMA = p.ROUTINESCHEMA AND r.SPECIFICNAME = p.SPECIFICNAME
WHERE p.ROWTYPE = 'R' AND r.FUNCTIONTYPE = 'T' AND {SchemaFilter("p.ROUTINESCHEMA")}
ORDER BY p.ROUTINESCHEMA, p.SPECIFICNAME, p.ORDINAL", cancellation);
    foreach (var group in rows.Rows.GroupBy(r => (r.GetString("specific_schema"), r.GetString("specific_name")))) {
      var routine = FindRoutineBySpecificName(group.Key.Item1, group.Key.Item2);
      if (routine == null)
        continue;
      routine.ResultColumns = group
        .Select(r => BuildResultColumn(r.GetString("column_name") ?? "", r.GetString("type_name") ?? ""))
        .ToList();
    }
  }

  /// <summary>Asynchronously reads the triggers, one row for each event a trigger fires on, each with the statement
  /// that created it.</summary>
  protected override Task<InfoTable> GetTriggersAsync(CancellationToken cancellation) {
    (string Column, string Name)[] events =
      [("EVENTINSERT", "INSERT"), ("EVENTUPDATE", "UPDATE"), ("EVENTDELETE", "DELETE")];
    var selects = events.Select(ev => $@"
SELECT TRIGNAME AS trigger_name, TABSCHEMA AS table_schema, TABNAME AS table_name,
       CASE TRIGTIME WHEN 'B' THEN 'BEFORE' WHEN 'I' THEN 'INSTEAD OF' ELSE 'AFTER' END AS action_timing,
       '{ev.Name}' AS event_manipulation,
       CASE GRANULARITY WHEN 'R' THEN 'ROW' ELSE 'STATEMENT' END AS action_orientation,
       TEXT AS action_statement
FROM SYSCAT.TRIGGERS
WHERE {ev.Column} = 'Y' AND {SchemaFilter("TABSCHEMA")}");
    return ExecuteSelectAsync(string.Join("\nUNION ALL", selects), cancellation);
  }

  /// <summary>Asynchronously reads the check constraints somebody declared, each with its condition.</summary>
  protected override Task<InfoTable> GetCheckConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TABSCHEMA AS table_schema, TABNAME AS table_name, CONSTNAME AS constraint_name, TEXT AS check_clause
FROM SYSCAT.CHECKS
WHERE TYPE = 'C' AND {SchemaFilter("TABSCHEMA")}", cancellation);

  /// <summary>Asynchronously reads the distinct types as domains, each with the built-in type it is made from.
  /// </summary>
  protected override Task<InfoTable> GetDomainsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT TYPESCHEMA AS domain_schema, TYPENAME AS domain_name, LOWER(SOURCENAME) AS data_type
FROM SYSCAT.DATATYPES
WHERE METATYPE = 'T' AND {SchemaFilter("TYPESCHEMA")}", cancellation);

  /// <summary>Asynchronously loads the aliases as synonyms.</summary>
  protected override async Task LoadSynonymsAsync(CancellationToken cancellation) {
    var rows = await ExecuteSelectAsync($@"
SELECT TABSCHEMA, TABNAME, BASE_TABSCHEMA, BASE_TABNAME
FROM SYSCAT.TABLES
WHERE TYPE = 'A' AND {SchemaFilter("TABSCHEMA")}", cancellation);
    foreach (var row in rows.Rows) {
      var schemaName = row.GetString("TABSCHEMA");
      var name = row.GetString("TABNAME");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      var synonym = new DbSynonymInfo(name, Model.GetOrAddSchema(schemaName), row.GetString("BASE_TABNAME") ?? "") {
        TargetSchema = row.GetString("BASE_TABSCHEMA"),
      };
      Model.AddSynonym(synonym);
    }
  }

  /// <summary>Asynchronously loads the comments on tables and columns from the <c>REMARKS</c> of the catalog.
  /// </summary>
  protected override async Task LoadCommentsAsync(CancellationToken cancellation) {
    var tableRows = await ExecuteSelectAsync($@"
SELECT TABSCHEMA, TABNAME, REMARKS FROM SYSCAT.TABLES
WHERE REMARKS IS NOT NULL AND TYPE IN ({TableTypes}) AND {SchemaFilter("TABSCHEMA")}", cancellation);
    foreach (var row in tableRows.Rows) {
      var table = FindTable(row.GetString("TABSCHEMA"), row.GetString("TABNAME"));
      if (table != null)
        table.Comment = row.GetString("REMARKS");
    }

    var columnRows = await ExecuteSelectAsync($@"
SELECT TABSCHEMA, TABNAME, COLNAME, REMARKS FROM SYSCAT.COLUMNS
WHERE REMARKS IS NOT NULL AND {SchemaFilter("TABSCHEMA")}", cancellation);
    foreach (var row in columnRows.Rows) {
      var table = FindTable(row.GetString("TABSCHEMA"), row.GetString("TABNAME"));
      var column = table?.FindColumn(row.GetString("COLNAME") ?? "");
      if (column != null)
        column.Comment = row.GetString("REMARKS");
    }
  }

  /// <summary>Gets the query of a materialized query table out of the statement that created it: what the
  /// parentheses after <c>AS</c> hold.</summary>
  /// <param name="statement">The statement, as the catalog keeps it.</param>
  /// <returns>The query; the statement unchanged where it has no such part.</returns>
  private static string GetMaterializedQuery(string statement) {
    var queryStart = FindQueryStart(statement);
    if (queryStart < 0)
      return statement;
    var depth = 1;
    var inLiteral = false;
    for (var i = queryStart; i < statement.Length; i++) {
      var ch = statement[i];
      if (ch == '\'')
        inLiteral = !inLiteral;
      else if (!inLiteral && ch == '(')
        depth++;
      else if (!inLiteral && ch == ')' && --depth == 0)
        return statement[queryStart..i].Trim();
    }
    return statement;
  }

  /// <summary>Finds where the query starts in the statement of a materialized query table: after the parenthesis
  /// that follows the word <c>AS</c>.</summary>
  /// <returns>The index of the query's first character, or -1 if the statement has no <c>AS (</c>.</returns>
  private static int FindQueryStart(string statement) {
    for (var i = 1; i + 2 < statement.Length; i++) {
      var isWordStart = char.IsWhiteSpace(statement[i - 1]) || statement[i - 1] == ')';
      if (!isWordStart || !statement.AsSpan(i, 2).Equals("AS", StringComparison.OrdinalIgnoreCase))
        continue;
      var position = i + 2;
      while (position < statement.Length && char.IsWhiteSpace(statement[position]))
        position++;
      if (position < statement.Length && statement[position] == '(')
        return position + 1;
    }
    return -1;
  }

  /// <summary>Asynchronously reads the columns of the tables of the given kinds, ordered by position, in the row
  /// contract of <see cref="DbModelLoader.GetColumnsAsync"/>.</summary>
  /// <remarks>A character type declared <c>FOR BIT DATA</c> has code page 0 and is reported as the binary type of
  /// its length. The length is in the column's own units, bytes unless the column says otherwise. A hidden column
  /// is left out.</remarks>
  private Task<InfoTable> GetColumnsOfAsync(string tableTypes, CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT c.TABSCHEMA AS table_schema, c.TABNAME AS table_name, c.COLNAME AS column_name,
       CASE WHEN c.TYPENAME = 'CHARACTER' AND c.CODEPAGE = 0 THEN 'binary'
            WHEN c.TYPENAME IN ('VARCHAR', 'LONG VARCHAR') AND c.CODEPAGE = 0 THEN 'varbinary'
            ELSE LOWER(c.TYPENAME) END AS data_type,
       c.NULLS AS is_nullable, c.""DEFAULT"" AS column_default,
       CASE WHEN c.TYPENAME IN ('CHARACTER', 'VARCHAR', 'GRAPHIC', 'VARGRAPHIC', 'BINARY', 'VARBINARY')
            THEN c.LENGTH END AS character_maximum_length,
       CASE WHEN c.TYPENAME = 'DECIMAL' THEN c.LENGTH END AS numeric_precision,
       CASE WHEN c.TYPENAME = 'DECIMAL' THEN c.SCALE END AS numeric_scale,
       c.IDENTITY AS is_identity,
       CASE WHEN c.GENERATED <> ' ' AND c.IDENTITY = 'N' THEN 1 ELSE 0 END AS is_generated,
       c.REMARKS AS column_comment
FROM SYSCAT.COLUMNS c
     JOIN SYSCAT.TABLES t ON t.TABSCHEMA = c.TABSCHEMA AND t.TABNAME = c.TABNAME
WHERE t.TYPE IN ({tableTypes}) AND c.HIDDEN = ' ' AND {SchemaFilter("c.TABSCHEMA")}
ORDER BY c.TABSCHEMA, c.TABNAME, c.COLNO", cancellation);
}
