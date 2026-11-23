using System.Text;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Sql;

/// <summary>Represents one placeholder in a translated statement, a position in the text whose rendering is decided
/// when the statement runs.</summary>
/// <remarks>Everything that varies with data is a placeholder, so the text varies with the query's shape alone. A
/// constant is written as a literal and becomes a placeholder only where the server has no literal form for it. A
/// list is always a placeholder, since a server with array parameters binds it whole and one without needs the
/// values written into the text.</remarks>
public sealed class SqlPlaceholder {
  private SqlPlaceholder(string name, Type? dataType, int argumentIndex, object? fixedValue, bool isList,
      QueryValueGetter? valueGetter = null) {
    Name = name;
    DataType = dataType;
    ArgumentIndex = argumentIndex;
    FixedValue = fixedValue;
    IsList = isList;
    ValueGetter = valueGetter;
  }

  /// <summary>Creates a parameter whose value is read from the operation context when the statement runs, such as an
  /// extension's filter.</summary>
  public static SqlPlaceholder MakeFromContext(string name, Type? dataType, QueryValueGetter valueGetter,
      bool isList = false) =>
    new(name, dataType, -1, null, isList, valueGetter);

  /// <summary>Creates a parameter whose value was known when the query was translated.</summary>
  public static SqlPlaceholder MakeFixed(string name, Type? dataType, object? value, bool isList = false) =>
    new(name, dataType, -1, value, isList);

  /// <summary>Creates a parameter whose value the caller supplies when the statement runs, at a position in its
  /// arguments.</summary>
  /// <param name="name">The parameter's name in the SQL, without the prefix.</param>
  /// <param name="dataType">The value's type, or a list's element type, or <see langword="null"/>.</param>
  /// <param name="argumentIndex">The zero-based index of the value in the caller's arguments.</param>
  /// <param name="isList"><see langword="true"/> if the value is a collection; otherwise, <see langword="false"/>.
  /// </param>
  /// <param name="valueGetter">A function that reads the value out of the argument where the argument is not the
  /// value itself, such as a field of a captured display class, or <see langword="null"/>.</param>
  public static SqlPlaceholder MakeArgument(string name, Type? dataType, int argumentIndex, bool isList = false,
      QueryValueGetter? valueGetter = null) =>
    new(name, dataType, argumentIndex, null, isList, valueGetter);

  /// <summary>Gets the parameter's name in the SQL, such as <c>p0</c>, without the vendor's prefix and numbered by
  /// position.</summary>
  /// <remarks>Whoever renders the statement writes the prefixed name; whoever binds the value names it to the driver,
  /// which wants the bare name on some vendors.</remarks>
  public string Name { get; }

  /// <summary>Gets the value's type, or a list's element type.</summary>
  public Type? DataType { get; }

  /// <summary>Gets the zero-based index of the value in the caller's arguments, or -1 when the value is fixed.
  /// </summary>
  public int ArgumentIndex { get; }

  /// <summary>Gets the value, when <see cref="IsFixed"/>.</summary>
  public object? FixedValue { get; }

  /// <summary>Gets the function that reads the value out of the run, or <see langword="null"/> when the argument at
  /// <see cref="ArgumentIndex"/>, or <see cref="FixedValue"/>, is the value itself.</summary>
  public QueryValueGetter? ValueGetter { get; private set; }

  /// <summary>Gets the value for one run of the statement.</summary>
  /// <param name="callCtx">The call context of the run.</param>
  /// <returns>The value.</returns>
  public object? GetValue(ILinqCallContext callCtx) =>
    ValueGetter is { } getter ? getter(callCtx) : IsFixed ? FixedValue : callCtx.GetArgument(ArgumentIndex);

  /// <summary>Puts a step in front of whatever reads this parameter's value, such as building a <c>LIKE</c> pattern
  /// around the caller's search term.</summary>
  /// <param name="wrap">A function that transforms the value <see cref="GetValue"/> would have returned.</param>
  public void WrapGetter(Func<object?, object?> wrap) {
    var inner = ValueGetter;
    var index = ArgumentIndex;
    var fixedValue = FixedValue;
    var isFixed = IsFixed;
    ValueGetter = callCtx => wrap(inner != null ? inner(callCtx) : isFixed ? fixedValue : callCtx.GetArgument(index));
  }

  /// <summary>Gets a value that indicates whether the value comes from somewhere other than the caller's arguments.
  /// </summary>
  public bool IsFixed => ArgumentIndex < 0;

  /// <summary>Gets a value that indicates whether the value is a collection, compared against with <c>IN</c>.</summary>
  public bool IsList { get; }

  /// <summary>Gets a value that indicates whether the placeholder is the rows of a parameter: rendered per call as the
  /// source the statement reads them from, a JSON value bound and read by the dialect's spelling, or a temp table
  /// filled first where the rows are many.</summary>
  public bool IsRows => RowsColumns != null;

  /// <summary>Gets or sets the columns of a rows placeholder, as the statement reads them; <see langword="null"/> for
  /// any other placeholder.</summary>
  public IReadOnlyList<JsonRowsColumn>? RowsColumns { get; set; }

  /// <summary>Gets or sets the alias a rows placeholder's source is read under.</summary>
  public string? RowsAlias { get; set; }

  /// <summary>Gets the placeholder whose rows this one reads under another alias, where a statement reads one
  /// parameter's rows in two places; <see langword="null"/> for the placeholder that binds the rows itself.</summary>
  public SqlPlaceholder? RowsOf { get; private set; }

  /// <summary>Creates a second reading of a rows placeholder, under its own alias, over the rows the first binds.
  /// </summary>
  public static SqlPlaceholder MakeRowsOccurrence(string name, SqlPlaceholder rowsOf, IReadOnlyList<JsonRowsColumn> columns,
      string alias) =>
    new(name, null, -1, null, false, rowsOf.GetValue) { RowsColumns = columns, RowsAlias = alias, RowsOf = rowsOf };

  /// <summary>Gets a value that indicates whether the placeholder renders differently from call to call, so that the
  /// statement's text cannot be kept: a list, or the rows of a parameter.</summary>
  public bool IsRenderedPerCall => IsList || IsRows;

  /// <summary>Gets a value that indicates whether the placeholder is the second half of a comparison with a
  /// parameter, rendered by the parameter's value when the statement is bound.</summary>
  public bool IsComparison => ValueComparison != null;

  /// <summary>Gets what a comparison placeholder is written as where the parameter holds a value: the operator and
  /// the parameter, as in <c>= @p0</c>; <see langword="null"/> for any other placeholder.</summary>
  public string? ValueComparison { get; private set; }

  /// <summary>Gets what a comparison placeholder is written as where the parameter holds null, which no operator
  /// matches: <c>IS NULL</c> or <c>IS NOT NULL</c>; <see langword="null"/> for any other placeholder.</summary>
  public string? NullComparison { get; private set; }

  /// <summary>Makes the placeholder the second half of a comparison.</summary>
  /// <param name="valueComparison">The operator and the parameter, as the statement writes them.</param>
  /// <param name="nullComparison">The null test that stands in for them where the parameter holds null.</param>
  public void SetComparison(string valueComparison, string nullComparison) {
    ValueComparison = valueComparison;
    NullComparison = nullComparison;
  }

  /// <summary>Gets or sets the zero-based index of this parameter in its statement's parameter list, which a slot
  /// refers to.</summary>
  public int Index { get; set; } = -1;

  public override string ToString() =>
    (IsFixed ? $"{Name} = {FixedValue ?? "null"}" : $"{Name} = arg[{ArgumentIndex}]") + (IsList ? " (list)" : "");
}
