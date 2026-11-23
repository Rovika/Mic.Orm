using System.Text;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Sql;

/// <summary>Represents a translated statement as a template: text parts with marked positions between them, each
/// filled by a parameter when the statement runs, together with what the statement returns.</summary>
/// <remarks>The statement is kept in the query cache and is tied to no vendor's command object. A slot is an index
/// into <see cref="Placeholders"/>, since one placeholder may fill several slots; it is bound once and referred to
/// twice.</remarks>
public sealed class SqlTemplate {
  public SqlTemplate(IReadOnlyList<string> textParts, IReadOnlyList<int> slots,
      IReadOnlyList<SqlPlaceholder> placeholders, int argumentCount, QueryResultKind resultKind,
      ILinqEntity? resultEntity, IReadOnlyList<ILinqEntityMember> resultMembers, Type? scalarType = null) {
    ResultKind = resultKind;
    if (textParts.Count != slots.Count + 1)
      throw new ArgumentException("A template has one more text part than it has slots.", nameof(textParts));
    TextParts = textParts;
    Slots = slots;
    Placeholders = placeholders;
    HasListPlaceholder = placeholders.Any(p => p.IsRenderedPerCall);
    ArgumentCount = argumentCount;
    ResultEntity = resultEntity;
    ResultMembers = resultMembers;
    ScalarType = scalarType;
  }

  /// <summary>Gets the fixed text parts, one more than there are slots.</summary>
  public IReadOnlyList<string> TextParts { get; }

  /// <summary>Gets the slots between the text parts, each an index into <see cref="Placeholders"/>.</summary>
  public IReadOnlyList<int> Slots { get; }

  /// <summary>Gets the placeholders, each rendered once however many slots refer to it.</summary>
  public IReadOnlyList<SqlPlaceholder> Placeholders { get; }

  /// <summary>Gets the number of values the caller has to supply.</summary>
  /// <remarks>This is neither the number of placeholders, since a fixed one needs nothing, nor the number of slots,
  /// since one placeholder may fill several.</remarks>
  public int ArgumentCount { get; }

  /// <summary>Gets or initializes the statement this is: a select, or one of the mutations.</summary>
  public StatementKind StatementKind { get; init; }

  /// <summary>Gets the kind of result the statement returns, which decides the reader that shapes it.</summary>
  public QueryResultKind ResultKind { get; }

  /// <summary>Gets the row shape: the entity for <see cref="QueryResultKind.Entities"/>, the structure the engine
  /// synthesized for <see cref="QueryResultKind.Rows"/>, or <see langword="null"/> for a result of values.</summary>
  public ILinqEntity? ResultEntity { get; }

  /// <summary>Gets the members the columns come back as, in column order.</summary>
  /// <remarks>A reader takes the first column as the first member, so the list must match the statement exactly.
  /// </remarks>
  public IReadOnlyList<ILinqEntityMember> ResultMembers { get; }

  /// <summary>Gets the type the caller asked for of the one value an aggregate returns, or of the column a collapsed
  /// projection returns.</summary>
  /// <remarks>The column may come back as another type; averaging integers gives an integer on some servers and a
  /// decimal on others.</remarks>
  public Type? ScalarType { get; }

  /// <summary>Gets or initializes what the statement returns, as the translator describes it, or
  /// <see langword="null"/> for a statement built without one.</summary>
  public QueryResultInfo? Result { get; init; }

  /// <summary>Gets or initializes the evaluator that runs over what the statement returned, such as a grouping
  /// returned as groups, or <see langword="null"/> where the rows are the answer.</summary>
  /// <remarks>The evaluator's result replaces the reader's.</remarks>
  public NodeEvaluator? PostSqlEvaluator { get; init; }

  /// <summary>Gets or initializes the cached entities a parameter of this statement reads in memory while it is
  /// bound, which whoever runs the statement loads first.</summary>
  public IReadOnlyList<ILinqEntity> CachedEntities { get; init; } = [];

  /// <summary>Gets or initializes the columns of a projection that hold a JSON document whose type another column of
  /// the row names, for the reader to pick the type by. Empty where the rows hold none.</summary>
  public IReadOnlyList<JsonTypedColumn> JsonTypedColumns { get; init; } = [];

  /// <summary>Gets a value that indicates whether a placeholder is a list or the rows of a parameter, whose rendering
  /// varies with the values a call brings, so the text is rendered on every call.</summary>
  public bool HasListPlaceholder { get; }

  /// <summary>Gets or sets the text of a template with no list placeholder, as rendered on its first call with a
  /// value in every comparison and kept, since every such call renders it alike; <see langword="null"/> until
  /// then, and always where a list varies the text.</summary>
  /// <remarks>A call that brings null to a comparison renders its own text and does not use this one.</remarks>
  public string? FixedText { get; internal set; }

  /// <summary>Renders the statement with each placeholder written as the caller says.</summary>
  /// <param name="placeholderText">One entry per entry of <see cref="Placeholders"/>: a bound placeholder's prefixed name,
  /// or its values written out.</param>
  /// <returns>The statement text.</returns>
  public string Render(IReadOnlyList<string> placeholderText) {
    ArgumentNullException.ThrowIfNull(placeholderText);
    if (placeholderText.Count != Placeholders.Count)
      throw new ArgumentException(
        $"The statement has {Placeholders.Count} placeholder(s) and {placeholderText.Count} rendering(s) were given.",
        nameof(placeholderText));
    if (Slots.Count == 0)
      return TextParts[0];
    var text = new StringBuilder();
    for (var i = 0; i < Slots.Count; i++)
      text.Append(TextParts[i]).Append(placeholderText[Slots[i]]);
    return text.Append(TextParts[^1]).ToString();
  }

  /// <summary>Renders the template for reading, with each placeholder shown as <c>{name}</c>.</summary>
  /// <returns>The template text, which is not executable SQL.</returns>
  public override string ToString() => Render([.. Placeholders.Select(p =>
    p.IsComparison ? p.ValueComparison!.Split(' ')[0] + " {" + p.Name + "}" : "{" + p.Name + "}")]);
}
