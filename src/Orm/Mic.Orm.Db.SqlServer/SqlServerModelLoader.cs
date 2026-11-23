using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.SqlServer;

/// <summary>Reads a SQL Server schema into a database model.</summary>
/// <remarks><c>information_schema</c> is complete for tables, columns and constraints, so that reading is inherited
/// from <see cref="DbModelLoader"/>. Indexes are not in it at all and come from <c>sys</c>, as do triggers, synonyms
/// and descriptions.</remarks>
public class SqlServerModelLoader(DbSettings settings) : DbModelLoader(settings) {
  /// <summary>The extended properties a definition hash is read from, as a list of literals: the ORM's own, and
  /// the description, where a database made before the ORM had a property of its own keeps the hash.</summary>
  const string HashProperties = $"N'{SqlServerSqlDialect.DefinitionHashProperty}', N'MS_Description'";

  /// <summary>The ordering of those properties that puts the ORM's own first.</summary>
  const string HashPropertyOrder =
    $"CASE WHEN ep.name = N'{SqlServerSqlDialect.DefinitionHashProperty}' THEN 0 ELSE 1 END";

  /// <summary>Asynchronously reads the columns, with the identity flag from <c>COLUMNPROPERTY</c>, <c>(max)</c>
  /// folded into the type name, and the name of each default constraint.</summary>
  /// <remarks>SQL Server reports a <c>(max)</c> column with a length of -1, so the length is folded back into the
  /// type name, where <c>(max)</c> belongs; a geography reports -1 too and takes no length. A vector reports its
  /// bytes, eight and four a dimension, and is given its dimensions as its length. The default's own name
  /// comes from <c>sys.default_constraints</c>: the server generates it, and it is the only handle for dropping the
  /// default.</remarks>
  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT c.table_schema, c.table_name, c.column_name,
       CASE WHEN c.character_maximum_length = -1 AND c.data_type IN ('varchar', 'nvarchar', 'varbinary')
            THEN c.data_type + '(max)' ELSE c.data_type END AS data_type,
       c.is_nullable, c.column_default,
       CASE WHEN c.data_type = 'vector' THEN (c.character_maximum_length - 8) / 4
            ELSE NULLIF(c.character_maximum_length, -1) END AS character_maximum_length,
       c.numeric_precision, c.numeric_scale,
       COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name)),
                      c.column_name, 'IsIdentity') AS is_identity,
       COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name)),
                      c.column_name, 'IsComputed') AS is_generated,
       (SELECT TOP 1 CAST(ep.value AS nvarchar(200)) FROM sys.extended_properties ep
         WHERE ep.class = 1 AND ep.name IN ({HashProperties})
           AND ep.major_id = OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name))
           AND ep.minor_id = COLUMNPROPERTY(
                 OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name)), c.column_name, 'ColumnId')
         ORDER BY {HashPropertyOrder})
         AS column_comment,
       dc.name AS default_constraint_name
FROM information_schema.columns c
     LEFT JOIN sys.default_constraints dc
       ON dc.parent_object_id = OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name))
      AND dc.parent_column_id = COLUMNPROPERTY(
            OBJECT_ID(QUOTENAME(c.table_schema) + '.' + QUOTENAME(c.table_name)), c.column_name, 'ColumnId')
WHERE {SchemaFilter("c.table_schema")}
ORDER BY c.table_schema, c.table_name, c.ordinal_position", cancellation);

  protected override DbColumnInfo BuildColumn(InfoRow row) {
    var column = base.BuildColumn(row);
    column.DefaultConstraintName = row.GetString("default_constraint_name");
    return column;
  }

  /// <summary>Strips the parentheses SQL Server wraps a default in: <c>DEFAULT 0</c> is reported as <c>((0))</c>, and
  /// a string default as <c>('none')</c>.</summary>
  /// <remarks>One layer is peeled at a time, and only while the parentheses wrap the whole expression, so
  /// <c>(a) + (b)</c> is left alone.</remarks>
  /// <param name="expression">The default expression as the server reports it.</param>
  /// <returns>The expression without its wrapping parentheses.</returns>
  protected override string? NormalizeDefault(string? expression) {
    var text = expression;
    while (text != null && text.Length > 1 && text[0] == '(' && text[^1] == ')' && Wraps(text))
      text = text[1..^1];
    return text;
  }

  /// <summary>Determines whether the leading parenthesis is the one the trailing parenthesis closes.</summary>
  static bool Wraps(string text) {
    var depth = 0;
    for (var i = 0; i < text.Length; i++) {
      if (text[i] == '(')
        depth++;
      else if (text[i] == ')' && --depth == 0)
        return i == text.Length - 1;
    }
    return false;
  }

  // ----- indexes: the half that is not in information_schema at all --------------------------------------------------

  /// <summary>Asynchronously reads the indexes from <c>sys.indexes</c>, with the clustered one flagged, and the
  /// full-text indexes from <c>sys.fulltext_indexes</c>.</summary>
  /// <remarks><c>type = 1</c> is the clustered index and <c>type = 0</c> the heap itself, a row describing the table;
  /// <c>is_hypothetical</c> excludes the statistics-only indexes the tuning advisor leaves behind. The definition hash
  /// is an extended property at index level, <see cref="SqlServerSqlDialect.DefinitionHashProperty"/>, read through
  /// <c>sys.extended_properties</c> with <c>minor_id</c> the index's id. A full-text index has no name; its name
  /// here is the one its catalog carries, <c>{schema}_{index}</c> as
  /// <see cref="SqlServerSqlDialect.GetCatalogName"/> writes it, with the schema prefix taken off.</remarks>
  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.name AS table_schema, t.name AS table_name, i.name AS index_name,
       i.is_unique, i.is_primary_key AS is_primary,
       CASE WHEN i.type = 1 THEN 1 ELSE 0 END AS is_clustered,
       i.filter_definition AS filter,
       (SELECT TOP 1 CAST(ep.value AS nvarchar(400)) FROM sys.extended_properties ep
         WHERE ep.class = 7 AND ep.major_id = i.object_id AND ep.minor_id = i.index_id
           AND ep.name IN ({HashProperties})
         ORDER BY {HashPropertyOrder}) AS index_comment,
       CASE WHEN i.type = 4 THEN 'spatial' ELSE CAST(NULL AS nvarchar(20)) END AS index_type
FROM sys.indexes i
     JOIN sys.tables t ON t.object_id = i.object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE i.type > 0 AND i.is_hypothetical = 0 AND i.name IS NOT NULL AND {SchemaFilter("s.name")}
UNION ALL
SELECT s.name, t.name,
       CASE WHEN c.name LIKE s.name + '\_%' ESCAPE '\' THEN SUBSTRING(c.name, LEN(s.name) + 2, 128) ELSE c.name END,
       0, 0, 0, NULL, NULL, 'fulltext'
FROM sys.fulltext_indexes f
     JOIN sys.tables t ON t.object_id = f.object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.fulltext_catalogs c ON c.fulltext_catalog_id = f.fulltext_catalog_id
WHERE {SchemaFilter("s.name")}", cancellation);

  /// <summary>Asynchronously reads the index columns, with the INCLUDE list from <c>is_included_column</c>, and a
  /// full-text index's columns from <c>sys.fulltext_index_columns</c> under the name the index query gave it.</summary>
  protected override Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.name AS table_schema, t.name AS table_name, i.name AS index_name, c.name AS column_name,
       ic.is_descending_key AS is_descending, ic.is_included_column AS is_included, ic.key_ordinal AS seq
FROM sys.index_columns ic
     JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
     JOIN sys.tables t ON t.object_id = i.object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.type > 0 AND i.is_hypothetical = 0 AND i.name IS NOT NULL AND {SchemaFilter("s.name")}
UNION ALL
SELECT s.name, t.name,
       CASE WHEN cat.name LIKE s.name + '\_%' ESCAPE '\' THEN SUBSTRING(cat.name, LEN(s.name) + 2, 128) ELSE cat.name END,
       c.name, 0, 0, fc.column_id
FROM sys.fulltext_index_columns fc
     JOIN sys.fulltext_indexes f ON f.object_id = fc.object_id
     JOIN sys.tables t ON t.object_id = f.object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.fulltext_catalogs cat ON cat.fulltext_catalog_id = f.fulltext_catalog_id
     JOIN sys.columns c ON c.object_id = fc.object_id AND c.column_id = fc.column_id
WHERE {SchemaFilter("s.name")}
ORDER BY table_schema, table_name, index_name, is_included, seq", cancellation);

  // ----- foreign keys: information_schema is here, and incomplete -----------------------------------------------------

  /// <summary>Asynchronously reads the foreign keys from <c>sys.foreign_keys</c>.</summary>
  /// <remarks><c>key_column_usage.position_in_unique_constraint</c>, by which the base class pairs a foreign key's
  /// columns with the ones they target, does not exist on SQL Server; <c>sys.foreign_key_columns</c> carries both sides
  /// in one row, so the foreign keys and their columns are both read from <c>sys</c>.</remarks>
  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.name AS table_schema, t.name AS table_name, f.name AS constraint_name,
       ts.name AS target_schema, tt.name AS target_table,
       CASE f.delete_referential_action WHEN 1 THEN 'CASCADE' ELSE 'NO ACTION' END AS delete_rule
FROM sys.foreign_keys f
     JOIN sys.tables t ON t.object_id = f.parent_object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.tables tt ON tt.object_id = f.referenced_object_id
     JOIN sys.schemas ts ON ts.schema_id = tt.schema_id
WHERE {SchemaFilter("s.name")}", cancellation);

  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.name AS table_schema, t.name AS table_name, f.name AS constraint_name,
       pc.name AS column_name, rc.name AS target_column
FROM sys.foreign_key_columns fc
     JOIN sys.foreign_keys f ON f.object_id = fc.constraint_object_id
     JOIN sys.tables t ON t.object_id = fc.parent_object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.columns pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
     JOIN sys.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
WHERE {SchemaFilter("s.name")}
ORDER BY s.name, t.name, f.name, fc.constraint_column_id", cancellation);

  /// <summary>Asynchronously reads the sequences from <c>sys.sequences</c>, since <c>information_schema.sequences</c>
  /// omits the increment.</summary>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.name AS sequence_schema, q.name AS sequence_name,
       CAST(q.start_value AS bigint) AS start_value, CAST(q.increment AS bigint) AS increment
FROM sys.sequences q
     JOIN sys.schemas s ON s.schema_id = q.schema_id
WHERE {SchemaFilter("s.name")}", cancellation);

  // ----- what a server holds beyond tables --------------------------------------------------------------------------

  /// <summary>Asynchronously reads the triggers from <c>sys.triggers</c>, one row per event, since
  /// <c>information_schema.triggers</c> does not exist on this server.</summary>
  /// <remarks>Every trigger is statement-level: SQL Server has no row-level triggers.</remarks>
  protected override Task<InfoTable> GetTriggersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(@"
SELECT tr.name AS trigger_name, s.name AS table_schema, t.name AS table_name,
       CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS action_timing,
       te.type_desc AS event_manipulation,
       'STATEMENT' AS action_orientation,
       CAST(NULL AS nvarchar(4000)) AS action_statement,
       tr.is_disabled AS is_disabled
FROM sys.triggers tr
     JOIN sys.tables t ON t.object_id = tr.parent_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.trigger_events te ON te.object_id = tr.object_id", cancellation);

  /// <summary>Asynchronously loads the synonyms.</summary>
  /// <remarks>The target is kept as the server wrote it: <c>base_object_name</c> is a quoted, possibly four-part name,
  /// and a synonym commonly points outside this database, where there is nothing local to resolve against.</remarks>
  protected override async Task LoadSynonymsAsync(CancellationToken cancellation) {
    foreach (var row in (await ExecuteSelectAsync(@"
SELECT s.name AS synonym_schema, sy.name AS synonym_name, sy.base_object_name AS target
FROM sys.synonyms sy JOIN sys.schemas s ON s.schema_id = sy.schema_id", cancellation)).Rows) {
      var schemaName = row.GetString("synonym_schema");
      var name = row.GetString("synonym_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      Model.AddSynonym(new DbSynonymInfo(name, Model.GetOrAddSchema(schemaName), row.GetString("target") ?? ""));
    }
  }

  /// <summary>Asynchronously loads the descriptions of tables and columns, which SQL Server keeps as extended
  /// properties.</summary>
  /// <remarks><c>MS_Description</c> is the conventional property name; SQL Server enforces none, so a database using
  /// another convention reports no descriptions here.</remarks>
  protected override async Task LoadCommentsAsync(CancellationToken cancellation) {
    foreach (var row in (await ExecuteSelectAsync(@"
SELECT s.name AS table_schema, t.name AS table_name, CAST(ep.value AS nvarchar(4000)) AS comment
FROM sys.extended_properties ep
     JOIN sys.tables t ON t.object_id = ep.major_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = 'MS_Description'", cancellation)).Rows) {
      if (FindTable(row.GetString("table_schema"), row.GetString("table_name")) is { } table)
        table.Comment = row.GetString("comment");
    }

    foreach (var row in (await ExecuteSelectAsync(@"
SELECT s.name AS table_schema, t.name AS table_name, c.name AS column_name,
       CAST(ep.value AS nvarchar(4000)) AS comment
FROM sys.extended_properties ep
     JOIN sys.tables t ON t.object_id = ep.major_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
     JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.class = 1 AND ep.minor_id > 0 AND ep.name = 'MS_Description'", cancellation)).Rows) {
      var column = FindTable(row.GetString("table_schema"), row.GetString("table_name"))
        ?.FindColumn(row.GetString("column_name") ?? "");
      if (column != null)
        column.Comment = row.GetString("comment");
    }
  }

  /// <summary>Asynchronously loads what a routine returns as a row set: the declared columns of a table-valued
  /// function, and the inferred first result set of a procedure.</summary>
  /// <remarks>A table-valued function has its columns in <c>sys.columns</c>, as a table does. A procedure's result set
  /// is whatever its last SELECT produces, so <c>sys.dm_exec_describe_first_result_set_for_object</c> infers the first
  /// result set from the body. Where the shape is not statically knowable (dynamic SQL, a conditionally filled temp
  /// table, branches with different columns) the function reports an <c>error_number</c> rather than throwing, and the
  /// routine's result columns stay <see langword="null"/>.</remarks>
  protected override async Task LoadRoutineResultsAsync(CancellationToken cancellation) {
    // ----- table-valued functions: real columns, certain ------------------------------------------------------------
    foreach (var group in (await ExecuteSelectAsync(@"
SELECT s.name AS routine_schema, o.name AS routine_name, c.name AS column_name,
       t.name AS type_name, c.is_nullable AS is_nullable
FROM sys.objects o
     JOIN sys.schemas s ON s.schema_id = o.schema_id
     JOIN sys.columns c ON c.object_id = o.object_id
     JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE o.type IN ('IF', 'TF')
ORDER BY s.name, o.name, c.column_id", cancellation)).Rows
        .GroupBy(r => (r.GetString("routine_schema"), r.GetString("routine_name")))) {
      if (FindRoutine(group.Key.Item1, group.Key.Item2) is not { } routine)
        continue;
      routine.RoutineKind = DbRoutineKind.TableFunction;
      routine.ResultColumns = [.. group.Select(r =>
        BuildResultColumn(r.GetString("column_name") ?? "", r.GetString("type_name") ?? "",
          r.GetBool("is_nullable")))];
    }

    // ----- procedures: inferred, and only where the server manages it ------------------------------------------------
    foreach (var group in (await ExecuteSelectAsync(@"
SELECT s.name AS routine_schema, p.name AS routine_name, r.name AS column_name,
       r.system_type_name AS type_name, r.is_nullable AS is_nullable
FROM sys.procedures p
     JOIN sys.schemas s ON s.schema_id = p.schema_id
     CROSS APPLY sys.dm_exec_describe_first_result_set_for_object(p.object_id, 0) r
WHERE r.error_number IS NULL
ORDER BY s.name, p.name, r.column_ordinal", cancellation)).Rows
        .GroupBy(r => (r.GetString("routine_schema"), r.GetString("routine_name")))) {
      if (FindRoutine(group.Key.Item1, group.Key.Item2) is not { } routine)
        continue;
      routine.ResultColumns = [.. group.Select(r =>
        BuildResultColumn(r.GetString("column_name") ?? "", r.GetString("type_name") ?? "",
          r.GetBool("is_nullable")))];
    }
  }
}
