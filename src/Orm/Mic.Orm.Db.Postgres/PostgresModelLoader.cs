using System.Text.RegularExpressions;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Postgres;

/// <summary>Reads a PostgreSQL schema into a database model.</summary>
/// <remarks>PostgreSQL has a complete <c>information_schema</c>, so most of the reading is inherited from
/// <see cref="DbModelLoader"/>; the overrides cover what the server reports differently.</remarks>
public partial class PostgresModelLoader(DbSettings settings) : DbModelLoader(settings) {
  /// <summary>Strips the explicit cast PostgreSQL appends to a literal default, so that
  /// <c>'none'::character varying</c> reads back as <c>'none'</c>.</summary>
  /// <remarks>Only a whole literal followed by a cast is unwrapped. A cast inside a call, as in
  /// <c>nextval('things_id_seq'::regclass)</c>, is left as it is.</remarks>
  /// <param name="expression">The default expression as the server reports it.</param>
  /// <returns>The literal without its cast, or <paramref name="expression"/> unchanged when it is not a cast literal.
  /// </returns>
  protected override string? NormalizeDefault(string? expression) {
    if (expression == null)
      return null;
    var match = CastOfWholeLiteral().Match(expression.Trim());
    return match.Success ? match.Groups[1].Value : expression;
  }

  [GeneratedRegex(@"^('(?:[^']|'')*')::[A-Za-z_][A-Za-z0-9_ ]*(\(\d+(,\s*\d+)?\))?$")]
  private static partial Regex CastOfWholeLiteral();

  /// <summary>Asynchronously reads the columns, naming an extension's type, such as PostGIS's <c>geography</c>, and
  /// an array type by its own name where <c>information_schema</c> says only <c>USER-DEFINED</c> or
  /// <c>ARRAY</c>, and giving a pgvector column its dimensions as its length.</summary>
  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, column_name,
       CASE WHEN data_type IN ('USER-DEFINED', 'ARRAY') THEN udt_name ELSE data_type END AS data_type,
       is_nullable, column_default,
       CASE WHEN data_type = 'USER-DEFINED' AND udt_name = 'vector'
            THEN (SELECT NULLIF(a.atttypmod, -1) FROM pg_attribute a
                  WHERE a.attrelid = (quote_ident(table_schema) || '.' || quote_ident(table_name))::regclass
                    AND a.attname = column_name)
            ELSE character_maximum_length END AS character_maximum_length,
       numeric_precision, numeric_scale, is_identity,
       CASE WHEN is_generated = 'ALWAYS' THEN 'YES' ELSE 'NO' END AS is_generated,
       CASE WHEN is_generated = 'ALWAYS'
            THEN col_description((quote_ident(table_schema) || '.' || quote_ident(table_name))::regclass,
                                 ordinal_position) END AS column_comment
FROM information_schema.columns
WHERE {SchemaFilter("table_schema")}
ORDER BY table_schema, table_name, ordinal_position", cancellation);

  /// <summary>Asynchronously reads the foreign keys from <c>pg_constraint</c>.</summary>
  /// <remarks>PostgreSQL computes the <c>information_schema</c> views the base class joins for foreign keys
  /// slowly, in time that grows with the constraints of the whole database; the catalog holds the same facts in
  /// one table.</remarks>
  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, c.relname AS table_name, k.conname AS constraint_name,
       tn.nspname AS target_schema, tc.relname AS target_table,
       CASE k.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT'
                          WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END AS delete_rule
FROM pg_constraint k
     JOIN pg_class c ON c.oid = k.conrelid
     JOIN pg_namespace n ON n.oid = c.relnamespace
     JOIN pg_class tc ON tc.oid = k.confrelid
     JOIN pg_namespace tn ON tn.oid = tc.relnamespace
WHERE k.contype = 'f' AND {SchemaFilter("n.nspname")}", cancellation);

  /// <summary>Asynchronously reads the columns of the foreign keys from <c>pg_constraint</c>, each with the column
  /// it targets, in the order of the key.</summary>
  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, c.relname AS table_name, k.conname AS constraint_name,
       a.attname AS column_name, ta.attname AS target_column
FROM pg_constraint k
     JOIN pg_class c ON c.oid = k.conrelid
     JOIN pg_namespace n ON n.oid = c.relnamespace
     CROSS JOIN LATERAL unnest(k.conkey, k.confkey) WITH ORDINALITY AS u(col, target_col, ord)
     JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = u.col
     JOIN pg_attribute ta ON ta.attrelid = k.confrelid AND ta.attnum = u.target_col
WHERE k.contype = 'f' AND {SchemaFilter("n.nspname")}
ORDER BY n.nspname, c.relname, k.conname, u.ord", cancellation);

  /// <summary>Reads a full-text index back from its definition.</summary>
  /// <remarks>PostgreSQL has no index kind for full text: MIC writes a <c>GIN</c> index over a <c>to_tsvector</c>
  /// expression, and the column query finds no columns under an expression, so the columns are taken from the
  /// definition in the order the vector lists them. An expression that is not MIC's is not read.</remarks>
  /// <param name="table">The table the index belongs to.</param>
  /// <param name="name">The name of the index.</param>
  /// <param name="flags">A bitwise combination of the enumeration values that describe the key.</param>
  /// <param name="row">The row of the index query, with the definition in <c>index_def</c>.</param>
  /// <returns>The key, or <see langword="null"/> when the definition is not a MIC full-text index.</returns>
  protected override DbKeyInfo? ReadExpressionIndex(DbTableInfo table, string name, KeyFlags flags, InfoRow row) {
    var definition = row.GetString("index_def");
    if (definition == null || !definition.Contains("to_tsvector", StringComparison.OrdinalIgnoreCase))
      return null;
    var key = new DbKeyInfo(name, table, flags) { IndexType = DbIndexType.FullText };
    foreach (Match coalesced in VectorColumn().Matches(definition)) {
      var column = table.FindColumn(coalesced.Groups[1].Value);
      if (column == null)
        return null;
      key.AddColumn(column);
    }
    return key.Columns.Count > 0 ? key : null;
  }

  [GeneratedRegex(@"COALESCE\(\(?""([^""]+)""")]
  private static partial Regex VectorColumn();

  // ----- indexes: pg_catalog, because information_schema has none ---------------------------------------------------

  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, t.relname AS table_name, i.relname AS index_name,
       ix.indisunique AS is_unique, ix.indisprimary AS is_primary, ix.indisclustered AS is_clustered,
       pg_get_expr(ix.indpred, ix.indrelid) AS filter,
       am.amname AS index_type,
       (SELECT CASE opc.opcname WHEN 'vector_cosine_ops' THEN 'Cosine' WHEN 'vector_l2_ops' THEN 'Euclidean'
                                WHEN 'vector_ip_ops' THEN 'DotProduct' END
        FROM pg_opclass opc WHERE opc.oid = ix.indclass[0]) AS vector_metric,
       pg_get_indexdef(ix.indexrelid) AS index_def,
       obj_description(i.oid, 'pg_class') AS index_comment
FROM pg_index ix
     JOIN pg_class i ON i.oid = ix.indexrelid
     JOIN pg_class t ON t.oid = ix.indrelid
     JOIN pg_namespace n ON n.oid = t.relnamespace
     JOIN pg_am am ON am.oid = i.relam
WHERE t.relkind = 'r' AND {SchemaFilter("n.nspname")}", cancellation);

  /// <summary>Asynchronously reads the index columns, with their INCLUDE and descending flags, from <c>pg_index</c>.
  /// </summary>
  /// <remarks>A column past <c>indnkeyatts</c> is an INCLUDE column, and bit 0 of its <c>indoption</c> entry is the
  /// descending flag; both vectors are zero-based.</remarks>
  protected override Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, t.relname AS table_name, i.relname AS index_name,
       a.attname AS column_name,
       ((ix.indoption[k.ord - 1] & 1) = 1) AS is_descending,
       (k.ord > ix.indnkeyatts) AS is_included
FROM pg_index ix
     JOIN pg_class i ON i.oid = ix.indexrelid
     JOIN pg_class t ON t.oid = ix.indrelid
     JOIN pg_namespace n ON n.oid = t.relnamespace
     CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
     JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
WHERE t.relkind = 'r' AND {SchemaFilter("n.nspname")}
ORDER BY n.nspname, t.relname, i.relname, k.ord", cancellation);

  /// <summary>Asynchronously reads the sequences the application declared, leaving out those an identity or serial
  /// column owns.</summary>
  /// <remarks><c>information_schema.sequences</c> reports a column-owned sequence like any other; the ownership is
  /// visible only in <c>pg_depend</c>, with deptype <c>i</c> for an identity column and <c>a</c> for a serial.
  /// </remarks>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS sequence_schema, c.relname AS sequence_name,
       s.seqstart AS start_value, s.seqincrement AS increment
FROM pg_class c
     JOIN pg_namespace n ON n.oid = c.relnamespace
     JOIN pg_sequence s ON s.seqrelid = c.oid
WHERE c.relkind = 'S' AND {SchemaFilter("n.nspname")}
  AND NOT EXISTS (SELECT 1 FROM pg_depend d
                  WHERE d.objid = c.oid AND d.classid = 'pg_class'::regclass AND d.deptype IN ('a', 'i'))",
      cancellation);

  /// <summary>Asynchronously reads the check constraints of the model's schemas from the catalog.</summary>
  /// <remarks><c>information_schema.check_constraints</c> also lists a constraint for every NOT NULL column of
  /// every schema, and takes a tenth of a second on a small database to do it.</remarks>
  protected override Task<InfoTable> GetCheckConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, t.relname AS table_name, k.conname AS constraint_name,
       pg_get_expr(k.conbin, k.conrelid) AS check_clause
FROM pg_constraint k
     JOIN pg_class t ON t.oid = k.conrelid
     JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE k.contype = 'c' AND {SchemaFilter("n.nspname")}", cancellation);

  // ----- what a server holds beyond tables --------------------------------------------------------------------------

  /// <summary>Asynchronously reads the views and the materialized views, each with its comment, which holds MIC's
  /// definition hash.</summary>
  /// <remarks><c>information_schema.views</c> does not report materialized views, so both kinds are read from
  /// <c>pg_class</c>.</remarks>
  protected override Task<InfoTable> GetViewsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, c.relname AS table_name, pg_get_viewdef(c.oid) AS view_definition,
       CASE WHEN c.relkind = 'm' THEN 1 ELSE 0 END AS is_materialized,
       obj_description(c.oid, 'pg_class') AS view_comment
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('v', 'm') AND {SchemaFilter("n.nspname")}", cancellation);

  /// <summary>Asynchronously reads the columns of the views and the materialized views, from <c>pg_attribute</c>,
  /// since <c>information_schema</c> has no columns for a materialized view.</summary>
  /// <remarks>The type is the bare type name, as <c>information_schema.columns</c> reports it for a table, with the
  /// length, precision and scale taken off the type modifier.</remarks>
  protected override Task<InfoTable> GetViewColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT n.nspname AS table_schema, c.relname AS table_name, a.attname AS column_name,
       format_type(a.atttypid, NULL) AS data_type,
       CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END AS is_nullable,
       NULL AS column_default,
       CASE WHEN a.atttypid IN (1042, 1043) AND a.atttypmod > 4 THEN a.atttypmod - 4 END AS character_maximum_length,
       CASE WHEN a.atttypid = 1700 AND a.atttypmod > 4 THEN ((a.atttypmod - 4) >> 16) & 65535 END AS numeric_precision,
       CASE WHEN a.atttypid = 1700 AND a.atttypmod > 4 THEN (a.atttypmod - 4) & 65535 END AS numeric_scale,
       'NO' AS is_identity
FROM pg_attribute a
     JOIN pg_class c ON c.oid = a.attrelid
     JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('v', 'm') AND a.attnum > 0 AND NOT a.attisdropped
  AND {SchemaFilter("n.nspname")}
ORDER BY n.nspname, c.relname, a.attnum", cancellation);

  /// <summary>Asynchronously reads the routines, naming a returned table, composite type or extension's type by its
  /// own name where <c>information_schema</c> says only <c>USER-DEFINED</c>.</summary>
  protected override Task<InfoTable> GetRoutinesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT routine_schema, routine_name, specific_name, routine_type,
       CASE WHEN data_type = 'USER-DEFINED' THEN type_udt_name ELSE data_type END AS return_type,
       routine_definition
FROM information_schema.routines
WHERE {SchemaFilter("routine_schema")}", cancellation);

  /// <summary>Asynchronously loads the enum types in addition to the domains the base class reads.</summary>
  /// <remarks>The values are listed in the server's <c>enumsortorder</c>; a list in a different order would describe
  /// a different type.</remarks>
  protected override async Task LoadCustomTypesAsync(CancellationToken cancellation) {
    await base.LoadCustomTypesAsync(cancellation);

    var enums = new Dictionary<string, DbCustomTypeInfo>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in (await ExecuteSelectAsync(@"
SELECT n.nspname AS type_schema, t.typname AS type_name, e.enumlabel AS value
FROM pg_type t
     JOIN pg_namespace n ON n.oid = t.typnamespace
     JOIN pg_enum e ON e.enumtypid = t.oid
ORDER BY n.nspname, t.typname, e.enumsortorder", cancellation)).Rows) {
      var schemaName = row.GetString("type_schema");
      var name = row.GetString("type_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;

      var key = $"{schemaName}.{name}";
      if (!enums.TryGetValue(key, out var type)) {
        type = new DbCustomTypeInfo(name, Model.GetOrAddSchema(schemaName), DbCustomTypeKind.Enum);
        Model.AddCustomType(type);
        enums[key] = type;
      }
      type.AddEnumValue(row.GetString("value") ?? "");
    }
  }

  /// <summary>Asynchronously loads the comments on tables and columns.</summary>
  /// <remarks>Comments live in <c>pg_description</c> and are reached through <c>obj_description</c> and
  /// <c>col_description</c>, not through a standard view.</remarks>
  protected override async Task LoadCommentsAsync(CancellationToken cancellation) {
    foreach (var row in (await ExecuteSelectAsync(@"
SELECT n.nspname AS table_schema, c.relname AS table_name, obj_description(c.oid) AS comment
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r' AND obj_description(c.oid) IS NOT NULL", cancellation)).Rows) {
      if (FindTable(row.GetString("table_schema"), row.GetString("table_name")) is { } table)
        table.Comment = row.GetString("comment");
    }

    foreach (var row in (await ExecuteSelectAsync(@"
SELECT n.nspname AS table_schema, c.relname AS table_name, a.attname AS column_name,
       col_description(c.oid, a.attnum) AS comment
FROM pg_class c
     JOIN pg_namespace n ON n.oid = c.relnamespace
     JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0
WHERE c.relkind = 'r' AND col_description(c.oid, a.attnum) IS NOT NULL", cancellation)).Rows) {
      var column = FindTable(row.GetString("table_schema"), row.GetString("table_name"))
        ?.FindColumn(row.GetString("column_name") ?? "");
      if (column != null)
        column.Comment = row.GetString("comment");
    }
  }

  /// <summary>Asynchronously marks each set-returning function as a table function and loads the columns of the
  /// rows it returns.</summary>
  /// <remarks>The columns are read where the function declares them. A <c>RETURNS TABLE(…)</c> shape is a list of
  /// OUT parameters in <c>information_schema.parameters</c>, which the base class has already read. The rows of a
  /// table or of a composite type have their columns in <c>pg_attribute</c>. A set of single values is one column,
  /// which PostgreSQL names after the function. A function declared <c>RETURNS SETOF record</c> with no OUT
  /// parameter leaves the columns to each query that calls it, so its result columns stay <see langword="null"/>.
  /// <c>pg_proc.proretset</c> says which routines return a set, since a function with OUT parameters and no set
  /// returns one row. Routines are matched on the server's specific name, which PostgreSQL forms as
  /// <c>name_oid</c>, so overloads are told apart. The function keeps the parameters a caller passes: an INOUT
  /// parameter is a result column and a parameter both.</remarks>
  protected override async Task LoadRoutineResultsAsync(CancellationToken cancellation) {
    var rows = (await ExecuteSelectAsync($@"
SELECT n.nspname AS specific_schema, p.proname || '_' || p.oid AS specific_name,
       t.typtype::text AS return_kind, format_type(p.prorettype, NULL) AS return_type,
       a.attname AS column_name, format_type(a.atttypid, NULL) AS column_type, NOT a.attnotnull AS is_nullable
FROM pg_proc p
     JOIN pg_namespace n ON n.oid = p.pronamespace
     JOIN pg_type t ON t.oid = p.prorettype
     LEFT JOIN pg_attribute a ON t.typrelid <> 0 AND a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped
WHERE p.proretset AND {SchemaFilter("n.nspname")}
ORDER BY p.oid, a.attnum", cancellation)).Rows;

    foreach (var group in rows.GroupBy(r => (r.GetString("specific_schema"), r.GetString("specific_name")))) {
      if (FindRoutineBySpecificName(group.Key.Item1, group.Key.Item2) is not { } routine)
        continue;

      // A set-returning function is a table function whatever information_schema.routines called it.
      routine.RoutineKind = DbRoutineKind.TableFunction;
      routine.ResultColumns = BuildResultColumns(routine, [.. group]);
      routine.RemoveParameters(p => p.Direction == DbParameterDirection.Out);
    }
  }

  /// <summary>Builds the result columns of a set-returning function from its OUT parameters or, where it has none,
  /// from the catalog rows of its return type.</summary>
  /// <param name="routine">The function, with its parameters loaded.</param>
  /// <param name="rows">The rows of the return type: one per column of a table or a composite type, one otherwise.
  /// </param>
  /// <returns>The columns, or <see langword="null"/> where the return type names none.</returns>
  private IReadOnlyList<DbColumnInfo>? BuildResultColumns(DbRoutineInfo routine, IReadOnlyList<InfoRow> rows) {
    var declared = routine.Parameters
      .Where(p => p.Direction is DbParameterDirection.Out or DbParameterDirection.InOut).ToList();
    if (declared.Count > 0)
      return [.. declared.Select(p => BuildResultColumn(p.Name, p.TypeSpec))];

    return rows[0].GetString("return_kind") switch {
      // A table or a composite type.
      "c" => [.. rows.Where(r => r.GetString("column_name") != null).Select(r => BuildResultColumn(
        r.GetString("column_name")!, r.GetString("column_type") ?? "", r.GetBool("is_nullable", true)))],
      // A pseudo-type, which is record here: the columns are named by the query that calls the function.
      "p" => null,
      _ => [BuildResultColumn(routine.Name, rows[0].GetString("return_type") ?? "")],
    };
  }
}
