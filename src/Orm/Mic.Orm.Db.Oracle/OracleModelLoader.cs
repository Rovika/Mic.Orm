using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Oracle;

/// <summary>Reads an Oracle schema into a database model.</summary>
/// <remarks>Oracle has no <c>information_schema</c>, so every query is replaced, aliasing the columns of the
/// <c>ALL_*</c> views to the names <see cref="DbModelLoader"/> expects; the interpretation of the rows is inherited.
/// Packages, synonyms and object types are read as well. A LONG column can be selected and nothing else: a view's
/// select, <c>ALL_VIEWS.TEXT</c>, and a check constraint's condition, <c>ALL_CONSTRAINTS.SEARCH_CONDITION</c>, are
/// read; a trigger's body, <c>ALL_TRIGGERS.TRIGGER_BODY</c>, is not, and is <see langword="null"/>.</remarks>
public class OracleModelLoader(DbSettings settings) : DbModelLoader(settings) {
  /// <summary>Asynchronously reads the schemas, which on Oracle are the users.</summary>
  protected override Task<InfoTable> GetSchemasAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync("SELECT username schema_name FROM all_users", cancellation);

  /// <summary>Asynchronously reads the tables, leaving out those the server made for itself; see
  /// <see cref="NotServerOwned"/>.</summary>
  protected override Task<InfoTable> GetTablesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT owner table_schema, table_name
FROM all_tables
WHERE {NotServerOwned("table_name")} AND {SchemaFilter("owner")}", cancellation);

  /// <summary>Asynchronously reads the columns, restoring the type name of an integral or Boolean column from the
  /// precision of Oracle's single numeric type.</summary>
  /// <remarks><c>NUMBER(10)</c> is an int, <c>NUMBER(19)</c> a long, <c>NUMBER(5)</c> a short and <c>NUMBER(1)</c> a
  /// bool, the names the type registry maps forward; any other NUMBER is a decimal and keeps its precision and scale.
  /// The size of a character type is <c>char_length</c>, since a size in the model means characters; RAW has only
  /// <c>data_length</c>.</remarks>
  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT owner table_schema, table_name, column_name,
       CASE WHEN data_type = 'NUMBER' AND NVL(data_scale, 0) = 0
                 AND data_precision IN (1, 5, 10, 19)
            THEN 'number(' || data_precision || ')'
            ELSE LOWER(data_type) END data_type,
       nullable is_nullable,
       data_default column_default,
       CASE WHEN data_type IN ('RAW', 'LONG RAW') THEN data_length ELSE char_length END character_maximum_length,
       data_precision numeric_precision, data_scale numeric_scale,
       identity_column is_identity, virtual_column is_generated,
       CASE WHEN virtual_column = 'YES'
            THEN (SELECT cc.comments FROM all_col_comments cc
                   WHERE cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name)
            END column_comment
FROM all_tab_cols c
WHERE hidden_column = 'NO' AND {NotServerOwned("table_name")} AND {SchemaFilter("owner")}
ORDER BY owner, table_name, column_id", cancellation);

  /// <summary>Trims a default's text and reads the literal <c>NULL</c> as no default.</summary>
  /// <remarks>Oracle keeps a default's text as written, padded with a trailing newline. There is no
  /// <c>DROP DEFAULT</c>: a default is removed by being set to <c>DEFAULT NULL</c>, which Oracle then stores and
  /// reports as the text <c>NULL</c>.</remarks>
  /// <param name="expression">The default as the server reports it.</param>
  /// <returns>The trimmed default, or <see langword="null"/> when there is none.</returns>
  protected override string? NormalizeDefault(string? expression) {
    var text = expression?.Trim();
    return string.IsNullOrEmpty(text) || text.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : text;
  }

  /// <summary>Builds a column from its row, clearing the default of an identity column.</summary>
  /// <remarks>An identity column reports its hidden sequence as its default, <c>"app"."ISEQ$$_73930".nextval</c>,
  /// which the comparer would otherwise offer to remove on every startup. It is cleared here rather than in the query
  /// because <c>DATA_DEFAULT</c> is a LONG, which Oracle will not have inside an expression.</remarks>
  /// <param name="row">The row of the column query.</param>
  /// <returns>The column.</returns>
  protected override DbColumnInfo BuildColumn(InfoRow row) {
    var column = base.BuildColumn(row);
    if (column.IsIdentity)
      column.DefaultExpression = null;
    return column;
  }

  /// <summary>Asynchronously reads the primary, unique and foreign key constraints, spelling out the kind Oracle
  /// keeps as one letter.</summary>
  protected override Task<InfoTable> GetTableConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT owner table_schema, table_name, constraint_name,
       CASE constraint_type WHEN 'P' THEN 'PRIMARY KEY' WHEN 'U' THEN 'UNIQUE' WHEN 'R' THEN 'FOREIGN KEY' END
         constraint_type
FROM all_constraints
WHERE constraint_type IN ('P', 'U', 'R') AND {NotServerOwned("table_name")} AND {SchemaFilter("owner")}", cancellation);

  protected override Task<InfoTable> GetConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT owner table_schema, table_name, constraint_name, column_name
FROM all_cons_columns
WHERE {SchemaFilter("owner")}
ORDER BY owner, table_name, constraint_name, position", cancellation);

  /// <summary>Asynchronously reads the foreign keys, finding each target table from the constraint Oracle names in
  /// <c>r_constraint_name</c>.</summary>
  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT c.owner table_schema, c.table_name, c.constraint_name,
       t.owner target_schema, t.table_name target_table, c.delete_rule
FROM all_constraints c
     JOIN all_constraints t ON t.owner = c.r_owner AND t.constraint_name = c.r_constraint_name
WHERE c.constraint_type = 'R' AND {SchemaFilter("c.owner")}", cancellation);

  /// <summary>Asynchronously reads the foreign key columns, pairing both sides by their position within the key.
  /// </summary>
  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT k.owner table_schema, k.table_name, k.constraint_name,
       k.column_name, r.column_name target_column
FROM all_constraints c
     JOIN all_cons_columns k ON k.owner = c.owner AND k.constraint_name = c.constraint_name
     JOIN all_cons_columns r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
                            AND r.position = k.position
WHERE c.constraint_type = 'R' AND {SchemaFilter("c.owner")}
ORDER BY k.owner, k.table_name, k.constraint_name, k.position", cancellation);

  /// <summary>Asynchronously reads the indexes, reporting an index-organized table (<c>IOT - TOP</c>) as clustered,
  /// a <c>CONTEXT</c> domain index of Oracle Text as a full-text index and a Spatial domain index as a spatial one.
  /// </summary>
  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT i.owner table_schema, i.table_name, i.index_name,
       CASE i.uniqueness WHEN 'UNIQUE' THEN 1 ELSE 0 END is_unique,
       CASE WHEN c.constraint_name IS NULL THEN 0 ELSE 1 END is_primary,
       CASE i.index_type WHEN 'IOT - TOP' THEN 1 ELSE 0 END is_clustered,
       CAST(NULL AS VARCHAR2(1)) ""filter"",
       CASE WHEN i.index_type = 'DOMAIN' AND i.ityp_owner = 'CTXSYS' AND i.ityp_name = 'CONTEXT' THEN 'fulltext'
            WHEN i.index_type = 'DOMAIN' AND i.ityp_owner = 'MDSYS' AND i.ityp_name = 'SPATIAL_INDEX' THEN 'spatial'
            ELSE i.index_type END index_type
FROM all_indexes i
     LEFT JOIN all_constraints c ON c.owner = i.owner AND c.constraint_name = i.index_name
                               AND c.constraint_type = 'P'
WHERE {NotServerOwned("i.table_name")} AND {SchemaFilter("i.owner")}", cancellation);

  /// <summary>Asynchronously reads the index columns, restoring the real name of a descending column and the
  /// datastore columns of a full-text index.</summary>
  /// <remarks>Oracle implements a descending index column as a function-based index over a hidden column, and
  /// <c>ALL_IND_COLUMNS</c> names that column (<c>SYS_NC00004$</c>); the real name is in <c>ALL_IND_EXPRESSIONS</c>,
  /// quoted, in a LONG column that can be selected and nothing else, so the substitution happens on the rows. A
  /// genuine function-based index still falls through with an expression the base class cannot resolve.</remarks>
  protected override async Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) {
    var rows = await ExecuteSelectAsync($@"
SELECT ic.index_owner table_schema, ic.table_name, ic.index_name, ic.column_name,
       e.column_expression column_expression,
       CASE ic.descend WHEN 'DESC' THEN 1 ELSE 0 END is_descending,
       0 is_included
FROM all_ind_columns ic
     LEFT JOIN all_ind_expressions e ON e.index_owner = ic.index_owner AND e.index_name = ic.index_name
                                    AND e.column_position = ic.column_position
WHERE {NotServerOwned("ic.table_name")} AND {SchemaFilter("ic.index_owner")}
ORDER BY ic.index_owner, ic.table_name, ic.index_name, ic.column_position", cancellation);

    // ALL_IND_COLUMNS names only the column a full-text index is on; where the index has a datastore, its COLUMNS
    // attribute stands in for that one row.
    var datastores = await GetTextDatastoresAsync(cancellation);
    var resolved = new InfoTable(
      ["table_schema", "table_name", "index_name", "column_name", "is_descending", "is_included"]);
    foreach (var row in rows.Rows) {
      var schema = row.GetString("table_schema");
      var table = row.GetString("table_name");
      var index = row.GetString("index_name");
      if (datastores.TryGetValue((schema, index), out var columns)) {
        foreach (var column in columns)
          resolved.AddRow([schema, table, index, column, 0, 0]);
        continue;
      }
      // The expression stands in only for a hidden column Oracle made, as for a descending column; an index over a
      // virtual column is function-based too, and names the column itself.
      var columnName = row.GetString("column_name") ?? "";
      var expression = row.GetString("column_expression")?.Trim();
      var name = columnName.StartsWith("SYS_NC", StringComparison.Ordinal) && expression is { Length: > 0 }
        ? expression.Trim('"')
        : columnName;
      resolved.AddRow([schema, table, index, name, row.GetBool("is_descending") ? 1 : 0, 0]);
    }
    return resolved;
  }

  /// <summary>Asynchronously gets the columns of every multi-column full-text index, by schema and index name, from
  /// the <c>COLUMNS</c> attribute of its datastore preference as <c>CTX_INDEX_VALUES</c> reports it.</summary>
  /// <remarks>Oracle Text's catalog is read only when <c>ALL_INDEXES</c> shows a <c>CONTEXT</c> index, since reading
  /// it needs a grant (<c>SELECT ON CTXSYS.CTX_INDEX_VALUES</c>) a deployment without full-text search has no reason
  /// to make.</remarks>
  private async Task<Dictionary<(string? Schema, string? Index), string[]>> GetTextDatastoresAsync(CancellationToken cancellation) {
    var datastores = new Dictionary<(string?, string?), string[]>();
    var contextIndexes = await ExecuteSelectAsync($@"
SELECT COUNT(*) index_count FROM all_indexes i
WHERE i.index_type = 'DOMAIN' AND i.ityp_owner = 'CTXSYS' AND i.ityp_name = 'CONTEXT' AND {SchemaFilter("i.owner")}", cancellation);
    if (contextIndexes.Rows.Count == 0 || contextIndexes.Rows[0].GetInt("index_count") == 0)
      return datastores;
    var values = await ExecuteSelectAsync($@"
SELECT v.ixv_index_owner table_schema, v.ixv_index_name index_name, v.ixv_value column_list
FROM ctxsys.ctx_index_values v
WHERE v.ixv_class = 'DATASTORE' AND v.ixv_attribute = 'COLUMNS' AND {SchemaFilter("v.ixv_index_owner")}", cancellation);
    foreach (var row in values.Rows) {
      var columns = (row.GetString("column_list") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.Trim('"')).Where(c => c.Length > 0).ToArray();
      if (columns.Length > 0)
        datastores[(row.GetString("table_schema"), row.GetString("index_name"))] = columns;
    }
    return datastores;
  }

  /// <summary>Asynchronously reads the sequences, leaving out the <c>ISEQ$$_…</c> sequences behind identity columns
  /// and the <c>MDRS_…$</c> ones behind spatial indexes, which are the server's and not the model's.</summary>
  /// <remarks>The start is reported as null: Oracle keeps no record of a sequence's <c>START WITH</c>, and
  /// <c>min_value</c>, which stays at 1, is not it.</remarks>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT sequence_owner sequence_schema, sequence_name, CAST(NULL AS NUMBER) start_value, increment_by ""increment""
FROM all_sequences
WHERE sequence_name NOT LIKE 'ISEQ$$%' AND sequence_name NOT LIKE 'MDRS\_%$' ESCAPE '\'
  AND {SchemaFilter("sequence_owner")}", cancellation);


  // ----- what a server holds beyond tables --------------------------------------------------------------------------

  /// <summary>Asynchronously reads the views and the materialized views, each with its comment, which holds MIC's
  /// definition hash, and with its select where source text is asked for.</summary>
  /// <remarks>The select is a LONG, which Oracle refuses in a set operation, so each kind has a query of its own.
  /// A load without source text leaves it on the server: an upgrade finds the hash in the comment.</remarks>
  protected override async Task<InfoTable> GetViewsAsync(CancellationToken cancellation) {
    var withSource = Parts.HasFlag(DbModelParts.SourceText);
    const string noSource = "CAST(NULL AS VARCHAR2(1))";
    var views = await ExecuteSelectAsync($@"
SELECT v.OWNER AS table_schema, v.VIEW_NAME AS table_name,
       {(withSource ? "v.TEXT" : noSource)} AS view_definition, 0 AS is_materialized, c.COMMENTS AS view_comment
FROM ALL_VIEWS v
     LEFT JOIN ALL_TAB_COMMENTS c ON c.OWNER = v.OWNER AND c.TABLE_NAME = v.VIEW_NAME AND c.TABLE_TYPE = 'VIEW'
WHERE {SchemaFilter("v.OWNER")}", cancellation);
    var materialized = await ExecuteSelectAsync($@"
SELECT m.OWNER AS table_schema, m.MVIEW_NAME AS table_name,
       {(withSource ? "m.QUERY" : noSource)} AS view_definition, 1 AS is_materialized, c.COMMENTS AS view_comment
FROM ALL_MVIEWS m
     LEFT JOIN ALL_MVIEW_COMMENTS c ON c.OWNER = m.OWNER AND c.MVIEW_NAME = m.MVIEW_NAME
WHERE {SchemaFilter("m.OWNER")}", cancellation);

    foreach (var row in materialized.Rows)
      views.AddRow([.. views.Columns.Select(column => row[column])]);
    return views;
  }

  protected override Task<InfoTable> GetViewColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT c.OWNER AS table_schema, c.TABLE_NAME AS table_name, c.COLUMN_NAME AS column_name,
       c.DATA_TYPE AS data_type,
       CASE WHEN c.NULLABLE = 'Y' THEN 'YES' ELSE 'NO' END AS is_nullable,
       CAST(NULL AS VARCHAR2(1)) AS column_default,
       c.CHAR_LENGTH AS character_maximum_length, c.DATA_PRECISION AS numeric_precision,
       c.DATA_SCALE AS numeric_scale, 'NO' AS is_identity
FROM ALL_TAB_COLS c
     JOIN (SELECT OWNER, VIEW_NAME AS NAME FROM ALL_VIEWS
           UNION ALL SELECT OWNER, MVIEW_NAME FROM ALL_MVIEWS) v ON v.OWNER = c.OWNER AND v.NAME = c.TABLE_NAME
WHERE {SchemaFilter("c.OWNER")}
ORDER BY c.OWNER, c.TABLE_NAME, c.COLUMN_ID", cancellation);

  /// <summary>Asynchronously reads the standalone procedures and functions; those inside a package belong to the
  /// package and are not listed here. The definition is the one line of the source that carries MIC's hash marker,
  /// where there is one.</summary>
  protected override Task<InfoTable> GetRoutinesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT o.OWNER AS routine_schema, o.OBJECT_NAME AS routine_name,
       o.OBJECT_NAME AS specific_name,
       o.OBJECT_TYPE AS routine_type,
       CAST(NULL AS VARCHAR2(1)) AS return_type,
       (SELECT MAX(s.TEXT) FROM ALL_SOURCE s
        WHERE s.OWNER = o.OWNER AND s.NAME = o.OBJECT_NAME AND s.TYPE = o.OBJECT_TYPE AND s.TEXT LIKE '%mic:h=%') AS routine_definition
FROM ALL_OBJECTS o
WHERE o.OBJECT_TYPE IN ('PROCEDURE', 'FUNCTION') AND {SchemaFilter("o.OWNER")}", cancellation);

  /// <summary>Asynchronously reads the parameters of the standalone routines.</summary>
  /// <remarks><c>ALL_ARGUMENTS</c> reports a function's return as an argument with position 0 and no name, which the
  /// base class takes as the return type.</remarks>
  protected override Task<InfoTable> GetRoutineParametersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT OWNER AS specific_schema, OBJECT_NAME AS specific_name, ARGUMENT_NAME AS parameter_name,
       DATA_TYPE AS data_type, IN_OUT AS parameter_mode, POSITION AS ordinal_position
FROM ALL_ARGUMENTS
WHERE PACKAGE_NAME IS NULL AND {SchemaFilter("OWNER")}
ORDER BY OWNER, OBJECT_NAME, POSITION", cancellation);

  protected override Task<InfoTable> GetTriggersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(@"
SELECT OWNER AS trigger_schema, TRIGGER_NAME AS trigger_name,
       TABLE_OWNER AS table_schema, TABLE_NAME AS table_name,
       CASE WHEN TRIGGER_TYPE LIKE 'BEFORE%' THEN 'BEFORE'
            WHEN TRIGGER_TYPE LIKE 'INSTEAD%' THEN 'INSTEAD OF' ELSE 'AFTER' END AS action_timing,
       TRIGGERING_EVENT AS event_manipulation,
       CASE WHEN TRIGGER_TYPE LIKE '%EACH ROW' THEN 'ROW' ELSE 'STATEMENT' END AS action_orientation,
       CAST(NULL AS VARCHAR2(1)) AS action_statement,
       STATUS AS status
FROM ALL_TRIGGERS", cancellation);

  /// <summary>Asynchronously reads the check constraints somebody named, each with its condition.</summary>
  /// <remarks>Oracle records NOT NULL as a check constraint under a name of its own making, one for every required
  /// column of the database; those are left out. A NOT NULL somebody named is dropped by the base class, which
  /// knows it by its condition. The condition, <c>SEARCH_CONDITION</c>, is a LONG.</remarks>
  protected override Task<InfoTable> GetCheckConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT OWNER AS table_schema, TABLE_NAME AS table_name, CONSTRAINT_NAME AS constraint_name,
       SEARCH_CONDITION AS check_clause
FROM ALL_CONSTRAINTS
WHERE CONSTRAINT_TYPE = 'C' AND GENERATED = 'USER NAME' AND {SchemaFilter("OWNER")}", cancellation);

  /// <summary>Asynchronously reports no domains, since a PL/SQL subtype is not a schema object.</summary>
  protected override Task<InfoTable> GetDomainsAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));

  /// <summary>Asynchronously loads the object types with their attributes.</summary>
  protected override async Task LoadCustomTypesAsync(CancellationToken cancellation) {
    var types = new Dictionary<string, DbCustomTypeInfo>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in (await ExecuteSelectAsync(
        "SELECT OWNER, TYPE_NAME FROM ALL_TYPES WHERE TYPECODE = 'OBJECT'", cancellation)).Rows) {
      var schemaName = row.GetString("OWNER");
      var name = row.GetString("TYPE_NAME");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      var type = new DbCustomTypeInfo(name, Model.GetOrAddSchema(schemaName), DbCustomTypeKind.Composite);
      Model.AddCustomType(type);
      types[$"{schemaName}.{name}"] = type;
    }

    foreach (var row in (await ExecuteSelectAsync(@"
SELECT OWNER, TYPE_NAME, ATTR_NAME, ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
FROM ALL_TYPE_ATTRS ORDER BY OWNER, TYPE_NAME, ATTR_NO", cancellation)).Rows) {
      if (!types.TryGetValue($"{row.GetString("OWNER")}.{row.GetString("TYPE_NAME")}", out var type))
        continue;
      type.AddAttribute(new DbColumnInfo(row.GetString("ATTR_NAME") ?? "",
        new DbTypeRef(row.GetString("ATTR_TYPE_NAME") ?? "")));
    }
  }

  /// <summary>Asynchronously loads the packages and the routines they own.</summary>
  /// <remarks><c>ALL_PROCEDURES</c> reports a package's members with <c>OBJECT_NAME</c> as the package and
  /// <c>PROCEDURE_NAME</c> as the member; a standalone routine has a null <c>PROCEDURE_NAME</c>. Members are added to
  /// the package and not to the model.</remarks>
  protected override async Task LoadPackagesAsync(CancellationToken cancellation) {
    var packages = new Dictionary<string, DbPackageInfo>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in (await ExecuteSelectAsync(
        "SELECT OWNER, OBJECT_NAME FROM ALL_OBJECTS WHERE OBJECT_TYPE = 'PACKAGE'", cancellation)).Rows) {
      var schemaName = row.GetString("OWNER");
      var name = row.GetString("OBJECT_NAME");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      var package = new DbPackageInfo(name, Model.GetOrAddSchema(schemaName));
      Model.AddPackage(package);
      packages[$"{schemaName}.{name}"] = package;
    }

    foreach (var row in (await ExecuteSelectAsync(@"
SELECT OWNER, OBJECT_NAME, PROCEDURE_NAME FROM ALL_PROCEDURES
WHERE PROCEDURE_NAME IS NOT NULL AND OBJECT_TYPE = 'PACKAGE'", cancellation)).Rows) {
      if (!packages.TryGetValue($"{row.GetString("OWNER")}.{row.GetString("OBJECT_NAME")}", out var package))
        continue;
      var member = row.GetString("PROCEDURE_NAME");
      if (member != null)
        package.AddRoutine(new DbRoutineInfo(member, package.Schema));
    }
  }

  /// <summary>Asynchronously loads the synonyms, keeping each target by name even where it resolves locally.</summary>
  /// <remarks>A synonym commonly points into another schema or across a database link.</remarks>
  protected override async Task LoadSynonymsAsync(CancellationToken cancellation) {
    foreach (var row in (await ExecuteSelectAsync(@"
SELECT OWNER, SYNONYM_NAME, TABLE_OWNER, TABLE_NAME, DB_LINK FROM ALL_SYNONYMS", cancellation)).Rows) {
      var schemaName = row.GetString("OWNER");
      var name = row.GetString("SYNONYM_NAME");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      Model.AddSynonym(new DbSynonymInfo(name, Model.GetOrAddSchema(schemaName),
          row.GetString("TABLE_NAME") ?? "") {
        TargetSchema = row.GetString("TABLE_OWNER"),
        TargetDatabase = row.GetString("DB_LINK"),
      });
    }
  }

  /// <summary>Asynchronously loads the comments on tables and columns from <c>ALL_TAB_COMMENTS</c> and
  /// <c>ALL_COL_COMMENTS</c>.</summary>
  protected override async Task LoadCommentsAsync(CancellationToken cancellation) {
    foreach (var row in (await ExecuteSelectAsync(
        "SELECT OWNER, TABLE_NAME, COMMENTS FROM ALL_TAB_COMMENTS WHERE COMMENTS IS NOT NULL",
        cancellation)).Rows) {
      if (FindTable(row.GetString("OWNER"), row.GetString("TABLE_NAME")) is { } table)
        table.Comment = row.GetString("COMMENTS");
    }

    foreach (var row in (await ExecuteSelectAsync(
        "SELECT OWNER, TABLE_NAME, COLUMN_NAME, COMMENTS FROM ALL_COL_COMMENTS WHERE COMMENTS IS NOT NULL",
        cancellation)).Rows) {
      var column = FindTable(row.GetString("OWNER"), row.GetString("TABLE_NAME"))
        ?.FindColumn(row.GetString("COLUMN_NAME") ?? "");
      if (column != null)
        column.Comment = row.GetString("COMMENTS");
    }
  }

  /// <summary>Asynchronously loads the result columns of the table functions; a procedure's cursor yields nothing
  /// the server will describe.</summary>
  /// <remarks>A function returning a declared collection type, a pipelined or table function, is the one case Oracle
  /// answers: <c>ALL_ARGUMENTS</c> gives the return as <c>DATA_TYPE = 'TABLE'</c> with the collection's name,
  /// <c>ALL_COLL_TYPES</c> the element type and <c>ALL_TYPE_ATTRS</c> its attributes. A REF CURSOR is opaque even
  /// when strongly typed, since Oracle records only that the argument is a cursor, so such a routine keeps its result
  /// columns <see langword="null"/>.</remarks>
  protected override async Task LoadRoutineResultsAsync(CancellationToken cancellation) {
    foreach (var group in (await ExecuteSelectAsync(@"
SELECT a.OWNER AS routine_schema, a.OBJECT_NAME AS routine_name,
       att.ATTR_NAME AS column_name, att.ATTR_TYPE_NAME AS type_name
FROM ALL_ARGUMENTS a
     JOIN ALL_COLL_TYPES ct ON ct.OWNER = a.TYPE_OWNER AND ct.TYPE_NAME = a.TYPE_NAME
     JOIN ALL_TYPE_ATTRS att ON att.OWNER = ct.ELEM_TYPE_OWNER AND att.TYPE_NAME = ct.ELEM_TYPE_NAME
WHERE a.POSITION = 0 AND a.DATA_LEVEL = 0 AND a.DATA_TYPE = 'TABLE' AND a.PACKAGE_NAME IS NULL
ORDER BY a.OWNER, a.OBJECT_NAME, att.ATTR_NO", cancellation)).Rows
        .GroupBy(r => (r.GetString("routine_schema"), r.GetString("routine_name")))) {
      if (FindRoutine(group.Key.Item1, group.Key.Item2) is not { } routine)
        continue;
      routine.RoutineKind = DbRoutineKind.TableFunction;
      routine.ResultColumns = [.. group.Select(r =>
        BuildResultColumn(r.GetString("column_name") ?? "", r.GetString("type_name") ?? ""))];
    }
  }

  /// <summary>Gets the condition that a table is the model's and not the server's own.</summary>
  /// <remarks><c>BIN$…</c> is the recycle bin: a dropped table is renamed and still answers every catalog query until
  /// it is purged. <c>DR$…</c> are the tables a <c>CONTEXT</c> index keeps under its owner, and <c>MDRT_…$</c> the
  /// R-tree of a spatial index, which are the index and not tables of the model.</remarks>
  private static string NotServerOwned(string column) =>
    $"{column} NOT LIKE 'BIN$%' AND {column} NOT LIKE 'DR$%' AND {column} NOT LIKE 'MDRT\\_%$' ESCAPE '\\'";
}
