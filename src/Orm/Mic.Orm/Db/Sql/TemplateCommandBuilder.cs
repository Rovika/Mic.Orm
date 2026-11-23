using System.Text;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Readers;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Sql;

/// <summary>Binds a translated template into a command that can run, with the reader its result kind calls for.
/// </summary>
public class TemplateCommandBuilder {
  /// <summary>Initializes a new instance of the <see cref="TemplateCommandBuilder"/> class.</summary>
  public TemplateCommandBuilder(DbProvider provider) {
    ArgumentNullException.ThrowIfNull(provider);
    Provider = provider;
  }

  /// <summary>Gets the provider that makes the driver command and adapts the values bound into it.</summary>
  protected DbProvider Provider { get; }

  /// <summary>Builds the commands for a template, bound in the request and named by its description: the one that
  /// runs the statement, after any that stage the rows of a parameter in a temp table and before the one that drops
  /// it.</summary>
  /// <param name="template">The template to bind.</param>
  /// <param name="request">The request the template binds in: the session the reader makes records for, the
  /// arguments its parameters read, and the rows of the cached entities a parameter reads.</param>
  /// <returns>The commands, in the order they run; the statement's is the request's result command.</returns>
  public virtual IReadOnlyList<DataCommand> BuildCommands(SqlTemplate template, DataRequest request) {
    ArgumentNullException.ThrowIfNull(template);
    ArgumentNullException.ThrowIfNull(request);
    if (request.Arguments.Count < template.ArgumentCount)
      throw new ArgumentException(
        $"The template takes {template.ArgumentCount} argument(s) and {request.Arguments.Count} were given.",
        nameof(request));

    // The text is rendered after every placeholder has been asked, since a list placeholder decides how many values there
    // are and a rows placeholder where its rows come from; a template with neither renders alike on every call and
    // keeps its text after the first.
    var dbCommand = Provider.CreateDbCommand();
    var rendered = new string[template.Placeholders.Count];
    var staging = new RowsStaging(request.Session);
    // The text kept is the one with a value in every comparison; a call that brings null to one renders its own.
    var isKeptText = !template.HasListPlaceholder;
    foreach (var placeholder in template.Placeholders) {
      var value = placeholder.GetValue(request);
      isKeptText &= !placeholder.IsComparison || value != null;
      rendered[placeholder.Index] = placeholder.IsRows ? RenderRows(dbCommand, placeholder, value, staging)
        : placeholder.IsList ? RenderList(dbCommand, placeholder, value, staging)
        : placeholder.IsComparison ? RenderComparison(dbCommand, placeholder, value)
        : AddParameter(dbCommand, placeholder.Name, value);
    }
    dbCommand.CommandText = isKeptText && template.FixedText != null ? template.FixedText : template.Render(rendered);
    if (isKeptText)
      template.FixedText ??= dbCommand.CommandText;

    var command = BuildStatementCommand(template, request, dbCommand);
    if (staging.Before.Count == 0)
      return [command];
    request.SetResultCommand(command);
    return [.. staging.Before, command, .. staging.After];
  }

  /// <summary>Builds the statement's own command, with the reader its result kind calls for.</summary>
  private DataCommand BuildStatementCommand(SqlTemplate template, DataRequest request, System.Data.Common.DbCommand dbCommand) {
    var description = request.Description;
    if (template.ResultKind == QueryResultKind.Affected)
      return new DataCommand(dbCommand, DbExecutionType.NonQuery, description);

    // The reader's members come from the template, which keeps the reader and the select list in step.
    var session = request.Session;
    return new DataCommand(dbCommand, DbExecutionType.Reader, description) {
      ResultReader = template.ResultKind switch {
        QueryResultKind.Entities => new EntityRecordReader(session,
          (EntityTypeInfo)template.ResultEntity!, [.. template.ResultMembers.Cast<EntityMemberInfo>()],
          request.Flags.HasFlag(DataRequestFlags.RefreshTracked)),
        QueryResultKind.Rows when template.Result is { Slots: { } slots } =>
          new BundleReader(session, (TransientEntityInfo)template.ResultEntity!, slots, template.JsonTypedColumns),
        QueryResultKind.Rows => new ProjectionReader(Provider, (TransientEntityInfo)template.ResultEntity!, template.JsonTypedColumns),
        QueryResultKind.Scalar => new ScalarReader(Provider, template.ScalarType!),
        QueryResultKind.Values => new ValueListReader(Provider, template.ScalarType!),
        _ => throw new NotSupportedException($"No reader for a {template.ResultKind} result."),
      },
    };
  }

  /// <summary>Renders the second half of a comparison with a parameter: the operator and the parameter, bound, or
  /// the null test where the value is null, with nothing bound.</summary>
  private string RenderComparison(System.Data.Common.DbCommand dbCommand, SqlPlaceholder placeholder, object? value) {
    if (value == null)
      return placeholder.NullComparison!;
    Provider.AddParameter(dbCommand, placeholder.Name, value);
    return placeholder.ValueComparison!;
  }

  private string AddParameter(System.Data.Common.DbCommand dbCommand, string name, object? value) {
    Provider.AddParameter(dbCommand, name, value ?? DBNull.Value);
    return Provider.ParameterPrefix + name;
  }

  // ----- the rows of a parameter ---------------------------------------------------------------------------------

  /// <summary>Holds the commands that stage the rows of a request's parameters in temp tables before its statement
  /// and drop them after it, and where each parameter's rows went: a bound value or a table.</summary>
  private sealed class RowsStaging(EntitySession session) {
    public EntitySession Session => session;
    public List<DataCommand> Before { get; } = [];
    public List<DataCommand> After { get; } = [];
    public Dictionary<SqlPlaceholder, (string Reference, bool IsTable)> Sources { get; } = new(ReferenceEqualityComparer.Instance);
  }

  /// <summary>Renders the rows of a parameter as the source the statement reads them from: the dialect's spelling
  /// over the rows bound as one JSON value, or, past the database's threshold, a temp table created and filled
  /// before the statement and dropped after it. A second reading of the same rows reuses where they went.</summary>
  private string RenderRows(System.Data.Common.DbCommand dbCommand, SqlPlaceholder placeholder, object? value,
      RowsStaging staging) {
    var columns = placeholder.RowsColumns!;
    var alias = placeholder.RowsAlias!;
    var dialect = Provider.SqlDialect;
    var owner = placeholder.RowsOf ?? placeholder;
    if (!staging.Sources.TryGetValue(owner, out var source)) {
      source = StageRows(dbCommand, owner, columns, JsonRowsHelper.GetRows(value).ToList(), staging);
      staging.Sources[owner] = source;
    }
    return source.IsTable
      ? dialect.JsonRowsTempSource(source.Reference, alias)
      : dialect.JsonRowsSource(source.Reference, columns, alias);
  }

  /// <summary>Binds the rows as one value, JSON unless the provider serializes them otherwise, or stages them in a
  /// temp table past the threshold, and returns how the statement refers to them.</summary>
  private (string Reference, bool IsTable) StageRows(System.Data.Common.DbCommand dbCommand, SqlPlaceholder owner,
      IReadOnlyList<JsonRowsColumn> columns, IReadOnlyList<object?[]> rows, RowsStaging staging) {
    var text = Provider.SerializeRows(columns, rows);
    if (text.Length <= staging.Session.Database.Settings.RowsTempTableThreshold)
      return (AddParameter(dbCommand, owner.Name, text), false);

    var dialect = Provider.SqlDialect;
    // The table is named into the model's first schema, for the server that wants a temp table in a database.
    var schema = staging.Session.Database.DbModel.Schemas.FirstOrDefault()?.Name ?? string.Empty;
    var tableRef = dialect.GetTempTableRef(schema,
      dialect.GetTempTableName("mic_rows_" + staging.Session.NextRowsTableNumber()));
    var tempColumns = dialect.GetJsonRowsTempColumns(columns);
    var declared = string.Join("," + dialect.LineBreak,
      tempColumns.Select(c => $"{dialect.Indent}{dialect.Quote(c.Name)} {c.TypeSpec}"));
    staging.Before.Add(MakeCommand(string.Format(dialect.CreateTempTable, tableRef, declared), $"create {tableRef}"));
    if (Provider.Supports(DbFeatures.BulkInsert))
      staging.Before.Add(BuildRowsBulkInsert(tableRef, columns, tempColumns, rows));
    else
      staging.Before.AddRange(BuildRowsInserts(tableRef, columns, tempColumns, rows));
    staging.After.Add(MakeCommand(string.Format(dialect.DropTempTable, tableRef), $"drop {tableRef}"));
    return (tableRef, true);
  }

  /// <summary>Builds the inserts that fill a rows table, as many rows a statement as the server takes and as many
  /// values a command as its parameters allow, each value a literal where the dialect has one.</summary>
  private IEnumerable<DataCommand> BuildRowsInserts(string tableRef, IReadOnlyList<JsonRowsColumn> columns,
      IReadOnlyList<JsonRowsTempColumn> tempColumns, IReadOnlyList<object?[]> rows) {
    var dialect = Provider.SqlDialect;
    var columnList = string.Join(", ", tempColumns.Select(c => dialect.Quote(c.Name)));
    var budget = Math.Max(1, Provider.MaxParameters - 100);
    var at = 0;
    while (at < rows.Count) {
      var dbCommand = Provider.CreateDbCommand();
      var written = new List<string>();
      while (at < rows.Count && written.Count < Provider.MaxRowsPerInsert
          && dbCommand.Parameters.Count + tempColumns.Count <= budget) {
        var values = tempColumns.Select((c, i) => RenderRowsValue(dbCommand, c, columns, rows[at], i, written.Count));
        written.Add(string.Format(dialect.InsertManyRow, tableRef, columnList, string.Join(", ", values)));
        at++;
      }
      dbCommand.CommandText = string.Format(dialect.InsertMany, tableRef, columnList,
        string.Join(dialect.InsertManyRowSeparator, written));
      yield return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"fill {tableRef}, {written.Count} rows");
    }
  }

  /// <summary>Builds the command that fills a rows table over the server's bulk channel, every row in one write.
  /// </summary>
  private DataCommand BuildRowsBulkInsert(string tableRef, IReadOnlyList<JsonRowsColumn> columns,
      IReadOnlyList<JsonRowsTempColumn> tempColumns, IReadOnlyList<object?[]> rows) {
    var values = new object?[rows.Count][];
    for (var r = 0; r < rows.Count; r++) {
      values[r] = new object?[tempColumns.Count];
      for (var c = 0; c < tempColumns.Count; c++)
        values[r][c] = GetRowsValue(tempColumns[c], columns, rows[r], c);
    }
    var bulk = new BulkInsertInfo(tableRef, [.. tempColumns.Select(c => c.Name)], values);
    var dbCommand = Provider.CreateDbCommand();
    dbCommand.CommandText = bulk.Describe();
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, $"fill {tableRef}, {rows.Count} rows") {
      BulkInsert = bulk,
    };
  }

  /// <summary>Renders one value of a rows table: a literal where the dialect has one, a parameter otherwise.
  /// </summary>
  private string RenderRowsValue(System.Data.Common.DbCommand dbCommand, JsonRowsTempColumn column,
      IReadOnlyList<JsonRowsColumn> columns, object?[] row, int columnIndex, int rowIndex) {
    var adapted = GetRowsValue(column, columns, row, columnIndex);
    return Provider.SqlDialect.GetLiteral(adapted) ?? AddParameter(dbCommand, $"r{rowIndex}_{columnIndex}", adapted);
  }

  /// <summary>Gets one value of a rows table in the form its column takes, the value, a Guid's hex text or the whole
  /// row as JSON, adapted as a parameter value is.</summary>
  private object? GetRowsValue(JsonRowsTempColumn column, IReadOnlyList<JsonRowsColumn> columns, object?[] row,
      int columnIndex) {
    // The row JSON form has one column and stands for the whole row; the others take the value at their position.
    var value = column.Form switch {
      RowsTempValueForm.RowJson => JsonRowsHelper.SerializeRow(Provider, columns, row),
      RowsTempValueForm.GuidText => row[columnIndex] is Guid guid ? JsonRowsHelper.GetGuidText(Provider, guid) : row[columnIndex],
      _ => row[columnIndex],
    };
    return value == null ? null : Provider.AdaptParameterValue(value);
  }

  private DataCommand MakeCommand(string text, string description) {
    var dbCommand = Provider.CreateDbCommand();
    dbCommand.CommandText = text;
    return new DataCommand(dbCommand, DbExecutionType.NonQuery, description);
  }

  /// <summary>Renders a collection parameter: one bound array parameter where the server binds arrays; past the
  /// dialect's <see cref="SqlDialect.MaxLiteralListItems"/>, a select over the values as the rows of one JSON
  /// parameter, staged in a temp table past the threshold as rows are; its values written into the text otherwise.
  /// </summary>
  /// <remarks>An empty collection renders as <c>NULL</c>, which matches nothing, since <c>IN ()</c> parses on no
  /// server. A <see langword="null"/> element is refused.</remarks>
  private string RenderList(System.Data.Common.DbCommand dbCommand, SqlPlaceholder placeholder, object? value,
      RowsStaging staging) {
    if (value is not System.Collections.IEnumerable source || value is string)
      throw new ArgumentException(
        $"Parameter '{placeholder.Name}' is compared with IN and needs a collection, and a "
        + $"{value?.GetType().Name ?? "null"} was given.");

    var items = new List<object>();
    foreach (var item in source)
      items.Add(item ?? throw new ArgumentException(
        $"Parameter '{placeholder.Name}' has a null in its list, which matches nothing."));

    if (Provider.Supports(DbFeatures.ArrayParameters))
      return AddParameter(dbCommand, placeholder.Name,
        ToTypedArray(placeholder, items.Select(item => (object?)Provider.AdaptParameterValue(item)).ToList()));

    if (items.Count == 0)
      return "NULL";

    var dialect = Provider.SqlDialect;
    if (items.Count > dialect.MaxLiteralListItems && Provider.Supports(DbFeatures.JsonRows))
      return RenderListAsRows(dbCommand, placeholder, items, staging);

    var text = new StringBuilder();
    for (var i = 0; i < items.Count; i++) {
      if (i > 0)
        text.Append(", ");
      // The element is adapted first, so a literal and a bound value of the same element agree.
      var adapted = Provider.AdaptParameterValue(items[i]);
      text.Append(dialect.GetLiteral(adapted) ?? AddParameter(dbCommand, $"{placeholder.Name}_{i}", adapted));
    }
    return text.ToString();
  }

  /// <summary>Renders a long list as the select of one column over its values as the rows of one JSON parameter,
  /// each value a row of one column typed as the element is.</summary>
  private string RenderListAsRows(System.Data.Common.DbCommand dbCommand, SqlPlaceholder placeholder,
      List<object> items, RowsStaging staging) {
    var elementType = items[0].GetType();
    var type = Provider.TypeRegistry.MapType(elementType)
      ?? throw new NotSupportedException(
        $"Parameter '{placeholder.Name}' lists {elementType.Name} values, which have no database type.");
    var columns = new[] { new JsonRowsColumn("v", type) };
    var alias = "in_" + placeholder.Name;
    var rows = items.Select(item => new object?[] { item }).ToList();
    var source = StageRows(dbCommand, placeholder, columns, rows, staging);
    var dialect = Provider.SqlDialect;
    var from = source.IsTable
      ? dialect.JsonRowsTempSource(source.Reference, alias)
      : dialect.JsonRowsSource(source.Reference, columns, alias);
    return $"SELECT {dialect.JsonRowsColumn(alias, columns[0])} FROM {from}";
  }

  /// <summary>Returns the adapted values as an array of their own element type, which an array parameter has to be.
  /// </summary>
  private static Array ToTypedArray(SqlPlaceholder placeholder, List<object?> items) {
    var elementType = items.Count > 0 ? items[0]!.GetType()
      : placeholder.DataType is { } declared ? Nullable.GetUnderlyingType(declared) ?? declared
      : typeof(object);
    var array = Array.CreateInstance(elementType, items.Count);
    for (var i = 0; i < items.Count; i++)
      array.SetValue(items[i], i);
    return array;
  }
}
