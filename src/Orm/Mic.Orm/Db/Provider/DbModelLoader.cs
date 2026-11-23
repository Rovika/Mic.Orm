using System.Data.Common;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Logging;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Provider;

/// <summary>Reads a live database's schema into a <see cref="DbModel"/>.</summary>
/// <remarks>Each <c>Get…</c> method returns an <see cref="InfoTable"/> and states the columns it must contain; the
/// base's own SQL reads <c>information_schema</c>, and a provider whose server has none, such as Oracle, overrides the
/// queries and aliases its catalog's columns to the names stated. The base turns the rows into model objects.</remarks>
public class DbModelLoader {
  public DbModelLoader(DbSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    Settings = settings;
  }

  public DbSettings Settings { get; }
  public DbProvider Provider => Settings.Provider;

  /// <summary>Gets the schemas being read; empty means every schema except the system ones.</summary>
  protected IReadOnlyList<string> Schemas { get; private set; } = [];

  /// <summary>Gets the model being built, for an override that runs a second pass.</summary>
  protected DbModel Model { get; private set; } = null!;

  /// <summary>Gets the record MIC keeps about the database just read, or <see langword="null"/> if the database has
  /// none.</summary>
  /// <remarks>The record is read as part of the load, so whether the metadata table exists is answered by the model.
  /// It is exposed here rather than on the model because it describes a database, not a schema; a caller puts it on
  /// the <see cref="Database"/> it uses.</remarks>
  public DbInfoRecord? DbInfo { get; private set; }

  /// <summary>Gets the parts this load was asked for, so a provider can skip work its own catalogs would otherwise
  /// do eagerly.</summary>
  protected DbModelParts Parts { get; private set; } = DbModelParts.Default;

  DataConnection _connection = null!;
  readonly Dictionary<string, DbRoutineInfo> _routinesBySpecificName = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Specifies how many times a deadlocked read is attempted before it is allowed to fail.</summary>
  public const int DeadlockRetries = 3;

  /// <summary>Asynchronously reads a database's schema into a model, retrying the whole read with a jittered backoff
  /// when it is chosen as a deadlock victim.</summary>
  /// <param name="schemas">The schemas to read, normally the application's own; empty or <see langword="null"/> to
  /// read every schema the server has. The system schemas are excluded either way.</param>
  /// <param name="parts">A bitwise combination of the enumeration values that specifies which parts to read. The
  /// default is what an upgrade compares.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the model.</returns>
  /// <remarks>The whole read is retried rather than the failed statement, since a model is built up in pieces and a
  /// statement failing halfway leaves it half-built. A retried read adds a warning to the model's build report.
  /// </remarks>
  public virtual async Task<DbModel> LoadModelAsync(IReadOnlyList<string>? schemas = null,
      DbModelParts parts = DbModelParts.Default, CancellationToken cancellation = default) {
    var attempted = 0;
    while (true) {
      try {
        var model = await LoadModelOnceAsync(schemas, parts, cancellation);
        if (attempted > 0)
          model.BuildReport.Add([new MessageLogEntry(default, DateTime.UtcNow,
            $"The schema read deadlocked and succeeded on attempt {attempted + 1}. Another connection was changing "
            + "the schema at the same time.", Severity.Warning)]);
        return model;
      } catch (DataAccessException failure)
          when (failure.SubType == DataAccessException.SubTypeDeadlock && attempted < DeadlockRetries - 1) {
        attempted++;
        await Task.Delay(TimeSpan.FromMilliseconds(50 * attempted + Random.Shared.Next(100)), cancellation);
      }
    }
  }

  /// <summary>Asynchronously reads the schema once, into a fresh model.</summary>
  protected virtual async Task<DbModel> LoadModelOnceAsync(IReadOnlyList<string>? schemas,
      DbModelParts parts, CancellationToken cancellation) {
    Schemas = schemas ?? [];
    // The shapes are of the routines, so asking for them asks for the routines.
    if (parts.HasFlag(DbModelParts.RoutineShapes))
      parts |= DbModelParts.Routines;
    Parts = parts;
    Model = new DbModel(Settings.DatabaseAlias, Provider) { LoadedParts = parts };

    var database = new Database(Model, Settings);
    _connection = database.CreateConnection(forSchema: true);
    try {
      await _connection.OpenAsync(RunMode.Async, cancellation);

      // The reader volunteers as the deadlock victim, since a losing upgrade leaves a schema half-changed.
      if (Provider.SqlDialect.SchemaConnectionSetup is { Length: > 0 } setup)
        await ExecuteSetupAsync(setup, cancellation);

      // Schemas are always read; every other kind is owned by one.
      await LoadSchemasAsync(cancellation);

      if (Parts.HasFlag(DbModelParts.Tables)) {
        await LoadTablesAsync(cancellation);
        await LoadColumnsAsync(cancellation);
        await LoadConstraintKeysAsync(cancellation);
        await LoadIndexesAsync(cancellation);
        await LoadRefConstraintsAsync(cancellation);
        await LoadSequencesAsync(cancellation);
      }

      // Triggers and check constraints attach to tables, and packages own routines, so each kind runs after what it
      // needs; a kind whose owner was not loaded adds nothing.
      if (Parts.HasFlag(DbModelParts.Views))
        await LoadViewsAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.Routines))
        await LoadRoutinesAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.Packages))
        await LoadPackagesAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.CustomTypes))
        await LoadCustomTypesAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.Synonyms))
        await LoadSynonymsAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.Triggers))
        await LoadTriggersAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.CheckConstraints))
        await LoadCheckConstraintsAsync(cancellation);
      if (Parts.HasFlag(DbModelParts.Comments))
        await LoadCommentsAsync(cancellation);

      await OnModelLoadedAsync(cancellation);
      DbModelBuilderHelper.CompleteSetup(Model);

      // The metadata table is looked up in the model just built, so no probe is needed.
      if (database.FindTable(Settings.DbInfoTableName) != null)
        DbInfo = await database.LoadDbInfoAsync(cancellation);
    } finally {
      await _connection.DisposeAsync();
    }
    return Model;
  }

  /// <summary>When overridden in a derived class, asynchronously runs a second pass for facts the first queries do
  /// not return, such as Oracle's computed columns. The base does nothing.</summary>
  protected virtual Task OnModelLoadedAsync(CancellationToken cancellation) => Task.CompletedTask;

  // ----- interpretation: written once -------------------------------------------------------------------------------

  protected virtual async Task LoadSchemasAsync(CancellationToken cancellation) {
    foreach (var row in (await GetSchemasAsync(cancellation)).Rows) {
      var name = row.GetString("schema_name");
      if (name != null && Include(name))
        Model.GetOrAddSchema(name);
    }
  }

  protected virtual async Task LoadTablesAsync(CancellationToken cancellation) {
    foreach (var row in (await GetTablesAsync(cancellation)).Rows) {
      var schemaName = row.GetString("table_schema");
      var tableName = row.GetString("table_name");
      if (schemaName == null || tableName == null || !Include(schemaName))
        continue;
      Model.AddTable(new DbTableInfo(tableName, Model.GetOrAddSchema(schemaName)));
    }
  }

  protected virtual async Task LoadColumnsAsync(CancellationToken cancellation) {
    foreach (var row in (await GetColumnsAsync(cancellation)).Rows) {
      var table = FindTable(row.GetString("table_schema"), row.GetString("table_name"));
      table?.AddColumn(BuildColumn(row));
    }
  }

  /// <summary>Builds a column from a catalog row, resolving its type through the registry so the spec is formatted as
  /// generation formats it.</summary>
  /// <param name="row">The catalog row.</param>
  /// <returns>The column.</returns>
  protected virtual DbColumnInfo BuildColumn(InfoRow row) {
    var name = row.GetString("column_name")!;
    var nullable = row.GetBool("is_nullable");
    // A length no int holds is a server's way of saying there is none: MySQL reports 4,294,967,295 for longtext.
    var length = row.GetLong("character_maximum_length");
    var size = length > int.MaxValue ? 0 : (int)length;
    var precision = (byte)row.GetInt("numeric_precision");
    var scale = (byte)row.GetInt("numeric_scale");

    var typeName = row.GetString("data_type") ?? "";
    var def = Provider.TypeRegistry.FindByName(typeName);
    var typeInfo = def == null ? null : Provider.TypeRegistry.Apply(def, size, precision, scale);

    // A type nothing maps to is kept as a type reference with no CLR type.
    var typeRef = typeInfo?.ToTypeRef() ?? new DbTypeRef(typeName);

    var column = new DbColumnInfo(name, typeRef, nullable) {
      Size = typeInfo?.Size ?? size,
      Precision = typeInfo?.Precision ?? precision,
      Scale = typeInfo?.Scale ?? scale,
      DefaultExpression = NormalizeDefault(row.GetString("column_default")),
    };
    if (row.GetBool("is_identity"))
      column.DbFlags |= DbColumnFlags.Identity;
    if (row.GetBool("is_generated")) {
      column.DbFlags |= DbColumnFlags.Computed;
      column.StoredDefinitionHash = DefinitionHashHelper.FindMarker(row.GetString("column_comment"));
    }
    return column;
  }

  // ----- keys, indexes and constraints ------------------------------------------------------------------------------

  /// <summary>Asynchronously loads the primary keys and unique constraints, the keys a catalog reports as constraints
  /// rather than as indexes.</summary>
  /// <remarks>MIC emits a unique key as a unique index, which <see cref="LoadIndexesAsync"/> reads; a database MIC did
  /// not create may use constraints, and both read back the same way.</remarks>
  protected virtual async Task LoadConstraintKeysAsync(CancellationToken cancellation) {
    var columnsByConstraint = GroupConstraintColumns(await GetConstraintColumnsAsync(cancellation));

    foreach (var row in (await GetTableConstraintsAsync(cancellation)).Rows) {
      var kind = row.GetString("constraint_type") ?? "";
      var flags = kind switch {
        "PRIMARY KEY" => KeyFlags.PrimaryKey,
        "UNIQUE" => KeyFlags.Index | KeyFlags.Unique,
        _ => KeyFlags.None,                        // foreign keys are loaded with their constraint, not here
      };
      if (flags == KeyFlags.None)
        continue;

      var table = FindTable(row.GetString("table_schema"), row.GetString("table_name"));
      var name = row.GetString("constraint_name");
      if (table == null || name == null)
        continue;
      var key = BuildKey(table, name, flags, columnsByConstraint, GetQualifiedIndexName(table, name));
      if (key != null)
        table.AddKey(key);
    }
  }

  /// <summary>Asynchronously loads the indexes from the provider's index queries.</summary>
  protected virtual async Task LoadIndexesAsync(CancellationToken cancellation) {
    var columns = await GetIndexColumnsAsync(cancellation);
    if (columns.Rows.Count == 0)
      return;
    var byIndex = GroupIndexColumns(columns);

    foreach (var row in (await GetIndexesAsync(cancellation)).Rows) {
      // The index backing a primary key or unique constraint was already produced by the constraint.
      if (row.GetBool("is_primary"))
        continue;
      var table = FindTable(row.GetString("table_schema"), row.GetString("table_name"));
      var name = row.GetString("index_name");
      if (table == null || name == null || table.Keys.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
        continue;

      var flags = KeyFlags.Index;
      if (row.GetBool("is_unique"))
        flags |= KeyFlags.Unique;
      if (row.GetBool("is_clustered"))
        flags |= KeyFlags.Clustered;

      // An index over an expression has no columns to read; the provider reads what it can off the definition.
      var key = BuildKey(table, name, flags, byIndex, GetQualifiedIndexName(table, name));
      if (key != null) {
        key.IndexType = ParseIndexType(row.GetString("index_type"));
        // The metric of a vector index, where the provider's query reports one by the enumeration's name.
        if (Enum.TryParse<VectorMetric>(row.GetString("vector_metric"), out var metric))
          key.VectorMetric = metric;
      } else
        key = ReadExpressionIndex(table, name, flags, row);   // which sets the index type itself
      if (key == null)
        continue;
      key.Filter = row.GetString("filter");

      // The hash is read rather than recomputed, since the server reports its own rewrite of the filter.
      var comment = row.GetString("index_comment");
      if (comment != null && comment.StartsWith(DefinitionHashHelper.Prefix, StringComparison.Ordinal))
        key.StoredDefinitionHash = comment;

      table.AddKey(key);
    }
  }

  /// <summary>When overridden in a derived class, reads an index over an expression, which the column query found no
  /// columns for, off what the provider's index query reports of its definition. The base returns
  /// <see langword="null"/>.</summary>
  /// <returns>The key, with its index type set, or <see langword="null"/> to skip the index.</returns>
  protected virtual DbKeyInfo? ReadExpressionIndex(DbTableInfo table, string name, KeyFlags flags, InfoRow row) => null;

  /// <summary>Parses the access method as the server spells it into the model's own vocabulary.</summary>
  /// <param name="raw">The server's spelling, or <see langword="null"/>.</param>
  /// <returns>One of the enumeration values that specifies the access method; the provider's default for anything
  /// unrecognized, <see langword="null"/> included.</returns>
  protected virtual DbIndexType ParseIndexType(string? raw) =>
    raw?.Trim().ToLowerInvariant() switch {
      "btree" or "normal" => DbIndexType.BTree,
      "hash" => DbIndexType.Hash,
      "gist" => DbIndexType.Gist,
      "gin" => DbIndexType.Gin,
      "spgist" => DbIndexType.SpGist,
      "brin" => DbIndexType.Brin,
      "fulltext" => DbIndexType.FullText,
      "spatial" => DbIndexType.Spatial,
      "bitmap" => DbIndexType.Bitmap,
      "hnsw" => DbIndexType.Hnsw,
      _ => Provider.DefaultIndexType,
    };

  /// <summary>Asynchronously loads the foreign keys, each identified by the table it points at and the columns on
  /// both sides.</summary>
  /// <remarks>The target is named by table rather than by constraint because not every server names its constraints
  /// or keeps the names unique.</remarks>
  protected virtual async Task LoadRefConstraintsAsync(CancellationToken cancellation) {
    var columns = GroupRefColumns(await GetRefConstraintColumnsAsync(cancellation));

    foreach (var row in (await GetReferentialConstraintsAsync(cancellation)).Rows) {
      var fromTable = FindTable(row.GetString("table_schema"), row.GetString("table_name"));
      var toTable = FindTable(row.GetString("target_schema"), row.GetString("target_table"));
      var name = row.GetString("constraint_name");
      if (fromTable == null || toTable == null || name == null)
        continue;

      var key = $"{fromTable.Schema.Name}.{fromTable.Name}.{name}";
      if (!columns.TryGetValue(key, out var pairs) || pairs.Count == 0)
        continue;

      var toKey = FindTargetKey(toTable, [.. pairs.Select(p => p.Target)]);
      if (toKey == null)
        continue;

      // The column set on this side is an ordinary key flagged as a foreign key, the shape generation produces.
      var fromKey = new DbKeyInfo(name, fromTable, KeyFlags.ForeignKey)
        { IndexType = Provider.DefaultIndexType };
      var resolved = true;
      foreach (var pair in pairs) {
        var column = fromTable.FindColumn(pair.From);
        if (column == null) {
          resolved = false;
          break;
        }
        fromKey.AddColumn(column);
      }
      if (!resolved)
        continue;

      fromTable.AddKey(fromKey);
      fromTable.AddRefConstraint(new DbRefConstraintInfo(name, fromKey, toKey) {
        CascadeDelete = string.Equals(row.GetString("delete_rule"), "CASCADE", StringComparison.OrdinalIgnoreCase),
      });
    }
  }

  /// <summary>Finds the unique key of a table that covers the given columns, falling back to the primary key.</summary>
  private static DbKeyInfo? FindTargetKey(DbTableInfo table, IReadOnlyList<string> columns) =>
    table.Keys.FirstOrDefault(k => k.IsUnique
      && k.Columns.Count == columns.Count
      && k.Columns.Select(c => c.Column.Name)
        .SequenceEqual(columns, StringComparer.OrdinalIgnoreCase))
    ?? table.PrimaryKey;

  private static Dictionary<string, List<(string From, string Target)>> GroupRefColumns(InfoTable table) {
    var grouped = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in table.Rows) {
      var schema = row.GetString("table_schema");
      var tableName = row.GetString("table_name");
      var name = row.GetString("constraint_name");
      var from = row.GetString("column_name");
      var target = row.GetString("target_column");
      if (schema == null || tableName == null || name == null || from == null || target == null)
        continue;
      var key = $"{schema}.{tableName}.{name}";
      if (!grouped.TryGetValue(key, out var list))
        grouped[key] = list = [];
      list.Add((from, target));
    }
    return grouped;
  }

  protected virtual async Task LoadSequencesAsync(CancellationToken cancellation) {
    foreach (var row in (await GetSequencesAsync(cancellation)).Rows) {
      var schemaName = row.GetString("sequence_schema");
      var name = row.GetString("sequence_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      Model.AddSequence(new DbSequenceInfo(name, Model.GetOrAddSchema(schemaName)) {
        StartValue = row.GetNullableLong("start_value"),
        Increment = row.GetLong("increment", 1),
      });
    }
  }

  /// <summary>Builds a key from its grouped columns, or returns <see langword="null"/> when there are none or one
  /// cannot be resolved, as for an index over an expression.</summary>
  private DbKeyInfo? BuildKey(DbTableInfo table, string name, KeyFlags flags,
      Dictionary<string, List<(string Column, bool Descending, bool Included)>> grouped, string groupKey) {
    if (!grouped.TryGetValue(groupKey, out var columns) || columns.Count == 0)
      return null;

    // The access method starts out concrete: a key read from a constraint never reaches the index query, and an
    // unset type would differ from the built model's on every startup.
    var key = new DbKeyInfo(name, table, flags) { IndexType = Provider.DefaultIndexType };
    foreach (var (columnName, descending, included) in columns) {
      var column = table.FindColumn(columnName);
      if (column == null)
        return null;
      if (included)
        key.AddIncludedColumn(column);
      else
        key.AddColumn(column, descending);
    }
    return key;
  }

  private static Dictionary<string, List<(string, bool, bool)>> GroupConstraintColumns(InfoTable table) {
    var grouped = new Dictionary<string, List<(string, bool, bool)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in table.Rows) {
      var schema = row.GetString("table_schema");
      var tableName = row.GetString("table_name");
      var name = row.GetString("constraint_name");
      var column = row.GetString("column_name");
      if (schema == null || tableName == null || name == null || column == null)
        continue;
      var groupKey = $"{schema}.{tableName}.{name}";
      if (!grouped.TryGetValue(groupKey, out var list))
        grouped[groupKey] = list = [];
      list.Add((column, false, false));
    }
    return grouped;
  }

  private Dictionary<string, List<(string, bool, bool)>> GroupIndexColumns(InfoTable table) {
    var grouped = new Dictionary<string, List<(string, bool, bool)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in table.Rows) {
      var schemaName = row.GetString("table_schema");
      var tableName = row.GetString("table_name");
      var indexName = row.GetString("index_name");
      var column = row.GetString("column_name");
      if (schemaName == null || tableName == null || indexName == null || column == null)
        continue;
      var groupKey = $"{schemaName}.{tableName}.{indexName}";
      if (!grouped.TryGetValue(groupKey, out var list))
        grouped[groupKey] = list = [];
      list.Add((column, row.GetBool("is_descending"), row.GetBool("is_included")));
    }
    return grouped;
  }

  private static string GetQualifiedIndexName(DbTableInfo table, string indexName) =>
    $"{table.Schema.Name}.{table.Name}.{indexName}";

  /// <summary>When overridden in a derived class, undoes what the server added to a default expression, such as the
  /// cast PostgreSQL appends, so what comes back is what was sent. The base returns the expression unchanged.</summary>
  protected virtual string? NormalizeDefault(string? expression) => expression;

  // ----- the queries: what a provider replaces ----------------------------------------------------------------------

  /// <summary>Asynchronously reads the schemas. Row contract: <c>schema_name</c>.</summary>
  protected virtual Task<InfoTable> GetSchemasAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(
      $"SELECT schema_name FROM information_schema.schemata WHERE {SchemaFilter("schema_name")}", cancellation);

  /// <summary>Asynchronously reads the base tables, not the views. Row contract: <c>table_schema</c>,
  /// <c>table_name</c>.</summary>
  protected virtual Task<InfoTable> GetTablesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name
FROM information_schema.tables
WHERE table_type = 'BASE TABLE' AND {SchemaFilter("table_schema")}", cancellation);

  /// <summary>Asynchronously reads the columns, ordered by position. Row contract: <c>table_schema</c>,
  /// <c>table_name</c>, <c>column_name</c>, <c>data_type</c>, <c>is_nullable</c>, <c>column_default</c>,
  /// <c>character_maximum_length</c>, <c>numeric_precision</c>, <c>numeric_scale</c>, <c>is_identity</c>,
  /// <c>is_generated</c>, and <c>column_comment</c> where the server keeps the hash of a computed column's
  /// expression in the column's comment.</summary>
  protected virtual Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, column_name, data_type, is_nullable, column_default,
       character_maximum_length, numeric_precision, numeric_scale, is_identity,
       CASE WHEN is_generated = 'ALWAYS' THEN 'YES' ELSE 'NO' END AS is_generated
FROM information_schema.columns
WHERE {SchemaFilter("table_schema")}
ORDER BY table_schema, table_name, ordinal_position", cancellation);

  /// <summary>Asynchronously reads the table constraints. Row contract: <c>table_schema</c>, <c>table_name</c>,
  /// <c>constraint_name</c>, <c>constraint_type</c> as <c>PRIMARY KEY</c>, <c>UNIQUE</c> or <c>FOREIGN KEY</c>.
  /// </summary>
  protected virtual Task<InfoTable> GetTableConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, constraint_name, constraint_type
FROM information_schema.table_constraints
WHERE constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY') AND {SchemaFilter("table_schema")}", cancellation);

  /// <summary>Asynchronously reads the columns of the table constraints, ordered by position within the constraint.
  /// Row contract: <c>table_schema</c>, <c>table_name</c>, <c>constraint_name</c>, <c>column_name</c>.</summary>
  /// <remarks>The rows are qualified by table because constraint names are not unique across a database on every
  /// server.</remarks>
  protected virtual Task<InfoTable> GetConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, constraint_name, column_name
FROM information_schema.key_column_usage
WHERE {SchemaFilter("table_schema")}
ORDER BY table_schema, table_name, constraint_name, ordinal_position", cancellation);

  /// <summary>Asynchronously reads the foreign key constraints. Row contract: <c>table_schema</c> and
  /// <c>table_name</c> of the referencing table, <c>constraint_name</c>, <c>target_schema</c>, <c>target_table</c>,
  /// <c>delete_rule</c>.</summary>
  protected virtual Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT DISTINCT tc.table_schema, tc.table_name, tc.constraint_name,
       ccu.table_schema AS target_schema, ccu.table_name AS target_table, rc.delete_rule
FROM information_schema.referential_constraints rc
     JOIN information_schema.table_constraints tc
       ON tc.constraint_schema = rc.constraint_schema AND tc.constraint_name = rc.constraint_name
     JOIN information_schema.constraint_column_usage ccu
       ON ccu.constraint_schema = rc.unique_constraint_schema
      AND ccu.constraint_name = rc.unique_constraint_name
WHERE {SchemaFilter("tc.table_schema")}", cancellation);

  /// <summary>Asynchronously reads the columns of the foreign key constraints, ordered by position within the key.
  /// Row contract: <c>table_schema</c>, <c>table_name</c>, <c>constraint_name</c>, <c>column_name</c> on the
  /// referencing side and <c>target_column</c> on the other.</summary>
  protected virtual Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT kcu.table_schema, kcu.table_name, kcu.constraint_name,
       kcu.column_name, ccu.column_name AS target_column
FROM information_schema.referential_constraints rc
     JOIN information_schema.key_column_usage kcu
       ON kcu.constraint_schema = rc.constraint_schema AND kcu.constraint_name = rc.constraint_name
     JOIN information_schema.key_column_usage ccu
       ON ccu.constraint_schema = rc.unique_constraint_schema
      AND ccu.constraint_name = rc.unique_constraint_name
      AND ccu.ordinal_position = kcu.position_in_unique_constraint
WHERE {SchemaFilter("kcu.table_schema")}
ORDER BY kcu.table_schema, kcu.table_name, kcu.constraint_name, kcu.ordinal_position", cancellation);

  /// <summary>When overridden in a derived class, asynchronously reads the indexes. Row contract: <c>table_schema</c>,
  /// <c>table_name</c>, <c>index_name</c>, <c>is_unique</c>, <c>is_primary</c>, <c>is_clustered</c>, <c>filter</c>,
  /// <c>index_type</c> as the server spells it, omitted where a server has one kind, and <c>index_comment</c> where the
  /// server keeps one, which holds MIC's definition hash.</summary>
  /// <remarks>The base returns no rows; <c>information_schema</c> has no index information.</remarks>
  protected virtual Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));

  /// <summary>When overridden in a derived class, asynchronously reads the index columns, ordered by position within
  /// the index. Row contract: <c>table_schema</c>, <c>table_name</c>, <c>index_name</c>, <c>column_name</c>,
  /// <c>is_descending</c>, <c>is_included</c>.</summary>
  /// <remarks>The base returns no rows.</remarks>
  protected virtual Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));

  /// <summary>Asynchronously reads the sequences. Row contract: <c>sequence_schema</c>, <c>sequence_name</c>,
  /// <c>start_value</c>, <c>increment</c>.</summary>
  protected virtual Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT sequence_schema, sequence_name, start_value, increment
FROM information_schema.sequences
WHERE {SchemaFilter("sequence_schema")}", cancellation);

  // ----- what a server holds beyond tables ---------------------------------------------------------------------------

  /// <summary>Asynchronously loads the views, and the materialized views where a provider reports them, with their
  /// columns and the definition hash MIC stored beside each one it made.</summary>
  /// <remarks>The hash is looked for in the view's comment and in its definition, whichever the server kept it in.
  /// </remarks>
  protected virtual async Task LoadViewsAsync(CancellationToken cancellation) {
    foreach (var row in (await GetViewsAsync(cancellation)).Rows) {
      var schemaName = row.GetString("table_schema");
      var name = row.GetString("table_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      var definition = row.GetString("view_definition");
      Model.AddView(new DbViewInfo(name, Model.GetOrAddSchema(schemaName)) {
        IsMaterialized = row.GetBool("is_materialized"),
        Source = Parts.HasFlag(DbModelParts.SourceText) ? definition : null,
        StoredDefinitionHash = DefinitionHashHelper.FindMarker(row.GetString("view_comment"))
          ?? DefinitionHashHelper.FindMarker(definition),
      });
    }

    foreach (var row in (await GetViewColumnsAsync(cancellation)).Rows) {
      var view = FindView(row.GetString("table_schema"), row.GetString("table_name"));
      view?.AddColumn(BuildColumn(row));
    }
  }

  /// <summary>Asynchronously loads the procedures and functions, with their parameters and result shapes.</summary>
  protected virtual async Task LoadRoutinesAsync(CancellationToken cancellation) {
    _routinesBySpecificName.Clear();

    foreach (var row in (await GetRoutinesAsync(cancellation)).Rows) {
      var schemaName = row.GetString("routine_schema");
      var name = row.GetString("routine_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;

      var definition = row.GetString("routine_definition");
      var routine = new DbRoutineInfo(name, Model.GetOrAddSchema(schemaName), GetRoutineKind(row)) {
        ReturnTypeSpec = row.GetString("return_type"),
        Source = Parts.HasFlag(DbModelParts.SourceText) ? definition : null,
        StoredDefinitionHash = DefinitionHashHelper.FindMarker(definition),
      };
      Model.AddRoutine(routine);

      // Keyed by the schema and the server's specific name, which the parameter rows join on.
      _routinesBySpecificName[GetRoutineKey(schemaName, row.GetString("specific_name") ?? name)] = routine;
    }

    // An upgrade compares names and hashes; the parameters and the result columns are read for whoever shows them.
    if (!Parts.HasFlag(DbModelParts.RoutineShapes))
      return;

    foreach (var row in (await GetRoutineParametersAsync(cancellation)).Rows) {
      var routine = FindRoutineBySpecificName(row.GetString("specific_schema"), row.GetString("specific_name"));
      if (routine == null)
        continue;
      var typeSpec = row.GetString("data_type") ?? "";
      var position = row.GetInt("ordinal_position");

      // Position 0 is the value a function returns, which SQL Server, MySQL and Oracle report as a parameter with
      // no name. Oracle gives the return type nowhere else.
      if (position == 0) {
        if (string.IsNullOrEmpty(routine.ReturnTypeSpec))
          routine.ReturnTypeSpec = typeSpec;
        continue;
      }
      routine.AddParameter(new DbParameterInfo(row.GetString("parameter_name") ?? "", typeSpec) {
        Direction = ParseDirection(row.GetString("parameter_mode")),
        Position = position,
      });
    }

    await LoadRoutineResultsAsync(cancellation);
  }

  /// <summary>When overridden in a derived class, asynchronously loads the row set each routine returns, where the
  /// server can say. The base does nothing, since <c>information_schema</c> has no such information.</summary>
  protected virtual Task LoadRoutineResultsAsync(CancellationToken cancellation) => Task.CompletedTask;

  /// <summary>Finds a routine loaded in this pass by its schema and the server's specific name, which tells
  /// overloads apart.</summary>
  /// <param name="schema">The schema of the routine.</param>
  /// <param name="specificName">The specific name, as the routine statement reported it.</param>
  /// <returns>The routine, or <see langword="null"/> if this pass loaded none of that name.</returns>
  /// <remarks>The specific name is the routine's own name on SQL Server and MySQL, where two schemas may each hold
  /// a routine of one name, so the schema is part of the key.</remarks>
  protected DbRoutineInfo? FindRoutineBySpecificName(string? schema, string? specificName) =>
    schema != null && specificName != null
      && _routinesBySpecificName.TryGetValue(GetRoutineKey(schema, specificName), out var routine)
        ? routine
        : null;

  private static string GetRoutineKey(string schema, string specificName) => $"{schema}.{specificName}";

  /// <summary>Finds a loaded routine by schema and name.</summary>
  /// <remarks>The answer is ambiguous where a server allows overloads; a caller that has the specific name uses
  /// <see cref="FindRoutineBySpecificName"/>.</remarks>
  protected DbRoutineInfo? FindRoutine(string? schema, string? name) =>
    schema == null || name == null
      ? null
      : Model.Routines.FirstOrDefault(r =>
          string.Equals(r.Schema.Name, schema, StringComparison.OrdinalIgnoreCase)
          && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

  /// <summary>Builds a result column from a type name alone, resolving the type through the registry as
  /// <see cref="BuildColumn"/> does and keeping an unmapped type as text.</summary>
  protected DbColumnInfo BuildResultColumn(string name, string typeName, bool nullable = true) {
    var def = Provider.TypeRegistry.FindByName(typeName);
    var typeInfo = def == null ? null : Provider.TypeRegistry.Apply(def, 0, 0, 0);
    return new DbColumnInfo(name, typeInfo?.ToTypeRef() ?? new DbTypeRef(typeName), nullable);
  }

  /// <summary>Gets the kind of routine a catalog row describes, a procedure or a function.</summary>
  /// <remarks>A provider that can tell a table-valued function from a scalar one overrides this.</remarks>
  protected virtual DbRoutineKind GetRoutineKind(InfoRow row) =>
    string.Equals(row.GetString("routine_type"), "PROCEDURE", StringComparison.OrdinalIgnoreCase)
      ? DbRoutineKind.Procedure
      : DbRoutineKind.Function;

  protected static DbParameterDirection ParseDirection(string? mode) => mode?.ToUpperInvariant() switch {
    "OUT" => DbParameterDirection.Out,
    "INOUT" => DbParameterDirection.InOut,
    _ => DbParameterDirection.In,
  };

  /// <summary>When overridden in a derived class, asynchronously loads the packages, which Oracle alone has. The base
  /// does nothing.</summary>
  protected virtual Task LoadPackagesAsync(CancellationToken cancellation) => Task.CompletedTask;

  /// <summary>When overridden in a derived class, asynchronously loads the synonyms, which SQL Server and Oracle
  /// have. The base does nothing.</summary>
  protected virtual Task LoadSynonymsAsync(CancellationToken cancellation) => Task.CompletedTask;

  /// <summary>When overridden in a derived class, asynchronously loads the comments and descriptions, which every
  /// server keeps somewhere different. The base does nothing.</summary>
  protected virtual Task LoadCommentsAsync(CancellationToken cancellation) => Task.CompletedTask;

  /// <summary>Asynchronously loads the user-defined types. The base reads the domains; enums, composites and object
  /// types are per vendor.</summary>
  protected virtual async Task LoadCustomTypesAsync(CancellationToken cancellation) {
    foreach (var row in (await GetDomainsAsync(cancellation)).Rows) {
      var schemaName = row.GetString("domain_schema");
      var name = row.GetString("domain_name");
      if (schemaName == null || name == null || !Include(schemaName))
        continue;
      Model.AddCustomType(new DbCustomTypeInfo(name, Model.GetOrAddSchema(schemaName), DbCustomTypeKind.Domain) {
        BaseTypeSpec = row.GetString("data_type"),
      });
    }
  }

  /// <summary>Asynchronously loads the triggers, attached to the tables they fire for, merging the one row per event
  /// the catalog reports into one trigger with its events combined.</summary>
  protected virtual async Task LoadTriggersAsync(CancellationToken cancellation) {
    var seen = new Dictionary<string, DbTriggerInfo>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in (await GetTriggersAsync(cancellation)).Rows) {
      var schemaName = row.GetString("table_schema");
      var tableName = row.GetString("table_name");
      var name = row.GetString("trigger_name");
      if (name == null || schemaName == null || !Include(schemaName))
        continue;
      if (FindTable(schemaName, tableName) is not { } table)
        continue;                                   // tables were not asked for

      var events = ParseEvents(row.GetString("event_manipulation"));
      var key = $"{schemaName}.{tableName}.{name}";
      if (seen.TryGetValue(key, out var existing)) {
        existing.Events |= events;
        continue;
      }

      var trigger = new DbTriggerInfo(name, table) {
        Timing = ParseTiming(row.GetString("action_timing")),
        Events = events,
        IsRowLevel = !string.Equals(row.GetString("action_orientation"), "STATEMENT",
          StringComparison.OrdinalIgnoreCase),
        Source = Parts.HasFlag(DbModelParts.SourceText) ? row.GetString("action_statement") : null,
      };
      table.AddTrigger(trigger);
      seen[key] = trigger;
    }
  }

  protected static DbTriggerTiming ParseTiming(string? timing) => timing?.ToUpperInvariant() switch {
    "BEFORE" => DbTriggerTiming.Before,
    "INSTEAD OF" => DbTriggerTiming.InsteadOf,
    _ => DbTriggerTiming.After,
  };

  protected static DbTriggerEvents ParseEvents(string? manipulation) => manipulation?.ToUpperInvariant() switch {
    "INSERT" => DbTriggerEvents.Insert,
    "UPDATE" => DbTriggerEvents.Update,
    "DELETE" => DbTriggerEvents.Delete,
    "TRUNCATE" => DbTriggerEvents.Truncate,
    _ => DbTriggerEvents.None,
  };

  /// <summary>Asynchronously loads the check constraints, attached to their tables, leaving out the ones a server
  /// writes for itself to restate a column's nullability.</summary>
  protected virtual async Task LoadCheckConstraintsAsync(CancellationToken cancellation) {
    foreach (var row in (await GetCheckConstraintsAsync(cancellation)).Rows) {
      var schemaName = row.GetString("table_schema");
      var name = row.GetString("constraint_name");
      var expression = row.GetString("check_clause") ?? "";
      if (name == null || schemaName == null || !Include(schemaName))
        continue;
      if (FindTable(schemaName, row.GetString("table_name")) is not { } table)
        continue;
      if (IsNotNullCheck(name, expression))
        continue;
      table.AddCheckConstraint(new DbCheckConstraintInfo(name, table, expression));
    }
  }

  /// <summary>Decides whether a check constraint is only a server's own restatement of a column's nullability.
  /// </summary>
  protected virtual bool IsNotNullCheck(string name, string expression) =>
    expression.EndsWith("IS NOT NULL", StringComparison.OrdinalIgnoreCase)
    || name.EndsWith("_not_null", StringComparison.OrdinalIgnoreCase);

  /// <summary>Finds a loaded view by schema and name.</summary>
  protected DbViewInfo? FindView(string? schema, string? name) =>
    schema == null || name == null
      ? null
      : Model.Views.FirstOrDefault(v =>
          string.Equals(v.Schema.Name, schema, StringComparison.OrdinalIgnoreCase)
          && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

  // ----- the SQL for them; the base is the information_schema family --------------------------------------------------

  /// <summary>Asynchronously reads the views. Row contract: <c>table_schema</c>, <c>table_name</c>,
  /// <c>view_definition</c>, <c>is_materialized</c>.</summary>
  /// <remarks>The base reports <c>is_materialized</c> as a constant; a provider that has materialized views
  /// overrides this.</remarks>
  protected virtual Task<InfoTable> GetViewsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, view_definition, 0 AS is_materialized
FROM information_schema.views
WHERE {SchemaFilter("table_schema")}", cancellation);

  /// <summary>Asynchronously reads the columns of the views. Row contract: as <see cref="GetColumnsAsync"/>.</summary>
  protected virtual Task<InfoTable> GetViewColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable, c.column_default,
       c.character_maximum_length, c.numeric_precision, c.numeric_scale, 'NO' AS is_identity
FROM information_schema.columns c
     JOIN information_schema.views v
       ON v.table_schema = c.table_schema AND v.table_name = c.table_name
WHERE {SchemaFilter("c.table_schema")}
ORDER BY c.table_schema, c.table_name, c.ordinal_position", cancellation);

  /// <summary>Asynchronously reads the routines. Row contract: <c>routine_schema</c>, <c>routine_name</c>,
  /// <c>specific_name</c>, <c>routine_type</c>, <c>return_type</c>, <c>routine_definition</c>.</summary>
  protected virtual Task<InfoTable> GetRoutinesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT routine_schema, routine_name, specific_name, routine_type,
       data_type AS return_type, routine_definition
FROM information_schema.routines
WHERE {SchemaFilter("routine_schema")}", cancellation);

  /// <summary>Asynchronously reads the routine parameters, joined to their routines by schema and the server's
  /// specific name. Row contract: <c>specific_schema</c>, <c>specific_name</c>, <c>parameter_name</c>,
  /// <c>data_type</c>, <c>parameter_mode</c>, <c>ordinal_position</c>.</summary>
  /// <remarks>A row at position 0 is the value a function returns and not a parameter.</remarks>
  protected virtual Task<InfoTable> GetRoutineParametersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT specific_schema, specific_name, parameter_name, data_type, parameter_mode, ordinal_position
FROM information_schema.parameters
WHERE {SchemaFilter("specific_schema")}
ORDER BY specific_schema, specific_name, ordinal_position", cancellation);

  /// <summary>Asynchronously reads the triggers, one row per event. Row contract: <c>trigger_name</c>,
  /// <c>table_schema</c>, <c>table_name</c>, <c>action_timing</c>, <c>event_manipulation</c>,
  /// <c>action_orientation</c>, <c>action_statement</c>.</summary>
  protected virtual Task<InfoTable> GetTriggersAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(@"
SELECT trigger_name, event_object_schema AS table_schema, event_object_table AS table_name,
       action_timing, event_manipulation, action_orientation, action_statement
FROM information_schema.triggers", cancellation);

  /// <summary>Asynchronously reads the check constraints. Row contract: <c>table_schema</c>, <c>table_name</c>,
  /// <c>constraint_name</c>, <c>check_clause</c>.</summary>
  protected virtual Task<InfoTable> GetCheckConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT tc.table_schema, tc.table_name, tc.constraint_name, cc.check_clause
FROM information_schema.check_constraints cc
     JOIN information_schema.table_constraints tc
       ON tc.constraint_schema = cc.constraint_schema AND tc.constraint_name = cc.constraint_name
WHERE {SchemaFilter("tc.table_schema")}", cancellation);

  /// <summary>Asynchronously reads the domains. Row contract: <c>domain_schema</c>, <c>domain_name</c>,
  /// <c>data_type</c>.</summary>
  protected virtual Task<InfoTable> GetDomainsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT domain_schema, domain_name, data_type
FROM information_schema.domains
WHERE {SchemaFilter("domain_schema")}", cancellation);

  // ----- execution: the loader's own -------------------------------------------------------------------------------

  /// <summary>Asynchronously runs a catalog query on the open schema connection.</summary>
  /// <exception cref="DataAccessException">the query fails. The message includes the SQL.</exception>
  protected virtual async Task<InfoTable> ExecuteSelectAsync(string sql, CancellationToken cancellation) {
    using var dbCommand = Provider.CreateDbCommand();
    dbCommand.CommandText = sql;
    dbCommand.Connection = _connection.DbConnection;
    try {
      await using var reader = await dbCommand.ExecuteReaderAsync(cancellation);
      return await InfoTable.ReadAsync(reader, cancellation);
    } catch (DbException ex) {
      throw Provider.Fail(
        $"Database '{Settings.DatabaseAlias}': reading the schema failed. {ex.Message}{Environment.NewLine}{sql}",
        ex, Settings.DatabaseAlias);
    }
  }

  /// <summary>Asynchronously runs a session setting on the open schema connection, ignoring a failure.</summary>
  /// <remarks>The setting is a preference, which side loses a deadlock, so a server that refuses it is no worse off.
  /// </remarks>
  protected virtual async Task ExecuteSetupAsync(string sql, CancellationToken cancellation) {
    try {
      using var dbCommand = Provider.CreateDbCommand();
      dbCommand.CommandText = sql;
      dbCommand.Connection = _connection.DbConnection;
      await dbCommand.ExecuteNonQueryAsync(cancellation);
    } catch (DbException) {
      // The setting is a preference; nothing is lost by its failure.
    }
  }

  // ----- helpers ----------------------------------------------------------------------------------------------------

  /// <summary>Decides whether a schema is read: never a system schema, and only one of the requested schemas where a
  /// list was given.</summary>
  protected bool Include(string schema) {
    if (Provider.SystemSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
      return false;
    return Schemas.Count == 0 || Schemas.Contains(schema, StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>Builds the SQL condition that restricts a schema column to the schemas being read: never a system
  /// schema, and only the requested ones where a list was given.</summary>
  /// <remarks>Every statement of a loader carries it, so that the rows of other schemas do not travel;
  /// <see cref="Include"/> applies the same rule to the rows that arrive.</remarks>
  protected string SchemaFilter(string column) {
    var excluded = string.Join(", ", Provider.SystemSchemas.Select(s => "'" + s + "'"));
    var filter = $"{column} NOT IN ({excluded})";
    if (Schemas.Count > 0)
      filter += $" AND {column} IN ({string.Join(", ", Schemas.Select(s => "'" + s.Replace("'", "''") + "'"))})";
    return filter;
  }

  protected DbTableInfo? FindTable(string? schema, string? name) =>
    schema == null || name == null ? null : Model.FindTable(schema, name);
}
