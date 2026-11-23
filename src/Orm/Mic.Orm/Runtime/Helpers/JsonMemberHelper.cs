using System.Text.Json;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Converts between a JSON member's object and the text its column holds: writes the text for a statement,
/// reads an object back out of a row, and finds the paths of a document that changed. This class cannot be
/// inherited.</summary>
/// <remarks>A document is written and read as the member's declared type, with the serializer's own polymorphism,
/// the derived types the base declares with <c>[JsonDerivedType]</c>, putting the discriminator inside the document.
/// With a discriminator in another column (<see cref="EntityMemberInfo.JsonDiscriminatorMember"/>) the column leads:
/// the document is written and read as the type the column names, with no discriminator in it, and a document of
/// another type is a validation fault (<see cref="GetTypeFault"/>); the ORM corrects neither side. A record holds
/// a loaded document as its text, a <see cref="JsonText"/> in the member's slot, until the member is first read. A
/// change inside the object is invisible to the record: a save writes a document whose member was assigned or
/// marked with <see cref="EntityHelper.MarkModified(object, string)"/>, and reads its changed paths against the
/// text the row holds, kept beside the record.</remarks>
public static class JsonMemberHelper {
  /// <summary>Gets or sets the options a JSON member is written and read with, one setting for the application, to be
  /// changed before any row is read.</summary>
  /// <remarks>Out-of-order metadata is allowed because a server that stores JSON in its own form, such as
  /// PostgreSQL's <c>jsonb</c>, hands the properties back in its own order, and the serializer would otherwise
  /// insist on <c>$type</c> being the first.</remarks>
  public static JsonSerializerOptions Options { get; set; } = new(JsonSerializerDefaults.General) {
    AllowOutOfOrderMetadataProperties = true,
  };

  /// <summary>Writes a member's value as JSON text for a statement.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <returns>The text, or <see langword="null"/> for a null value. A string member's value is its own text, and a
  /// document not read since it was loaded or set as text is that text.</returns>
  /// <remarks>With a discriminator in another column, the document is written as the type the column names and
  /// carries no discriminator; a document the column does not name, a fault when validated, is written as its own
  /// type.</remarks>
  public static JsonText? Serialize(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (record.GetRawValue(member) is not { } value)
      return null;
    if (value is JsonText text)
      return text;
    var asType = member.DataType;
    if (member.JsonDiscriminatorMember != null)
      asType = FindNamedType(record, member) is { } named && named.IsInstanceOfType(value) ? named : value.GetType();
    return new JsonText(Serialize(value, asType));
  }

  /// <summary>Tells why a member's document does not match its discriminator column.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <returns>The reason, or <see langword="null"/> where the document matches: the column names no declared type,
  /// or the document is not of the type it names. A null document matches anything.</returns>
  /// <remarks>A document still held as text is of no type yet and is read as the type the column names, so it
  /// matches wherever the column names one.</remarks>
  public static string? GetTypeFault(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (member.JsonDiscriminatorMember is not { } discriminatorMember || record.GetRawValue(member) is not { } value)
      return null;
    var named = FindNamedType(record, member);
    var held = value is JsonText ? "a document as text" : $"a '{value.GetType().Name}'";
    if (named == null)
      return $"'{discriminatorMember.Name}' holds '{record.GetRawValue(discriminatorMember) ?? "null"}', which names "
        + $"none of the types mapped for '{member.DataType.Name}'; '{member.Name}' holds {held}.";
    return value is JsonText || named.IsInstanceOfType(value)
      ? null
      : $"'{member.Name}' holds {held} where '{discriminatorMember.Name}' says '{named.Name}'.";
  }

  /// <summary>Puts the document a row holds on a record as loaded, kept as text until the member is read.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <param name="stored">The value the row holds for the column, or <see langword="null"/> for a null column.
  /// </param>
  public static void SetLoadedJson(EntityRecord record, EntityMemberInfo member, object? stored) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    var text = stored is null or DBNull ? null : GetText(stored);
    record.SetLoadedValue(member, GetTextValue(member, text));
    record.SetLoadedJsonText(member, text);
  }

  /// <summary>Puts a document read as a value of a projection on a record as loaded: the object, or the text of a
  /// member held as text, with the text a save reads the document's changes against.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <param name="value">The value the projection answered, or <see langword="null"/> for a null document.</param>
  internal static void SetLoadedDocument(EntityRecord record, EntityMemberInfo member, object? value) {
    if (value is null or string) {
      SetLoadedJson(record, member, value);
      return;
    }
    record.SetLoadedValue(member, value);
    record.SetLoadedJsonText(member, Serialize(record, member)?.Text);
  }

  /// <summary>Gets a member's object, reading it out of the document's text on the first call.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <returns>The object, or <see langword="null"/> for a null document.</returns>
  /// <remarks>The object replaces the text in the member's slot. Whether the member counts as modified does not
  /// change.</remarks>
  public static object? GetObject(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (record.GetRawValue(member) is not JsonText text)
      return record.GetRawValue(member);
    var value = Deserialize(record, member, text.Text);
    record.SetLoadedValue(member, value);
    return value;
  }

  /// <summary>Gets a member's document as JSON text, without making its object.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <returns>The text, or <see langword="null"/> for a null document: the text the row holds where the member was
  /// not read since, and the object written as text otherwise.</returns>
  public static string? GetJson(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (!record.Loaded.IsSet(member.DataIndex) && record.Session is { } session)
      session.LoadValue(record, member);
    return Serialize(record, member)?.Text;
  }

  /// <summary>Replaces a member's document with JSON text and counts the member as changed.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <param name="json">The text, or <see langword="null"/> for a null document.</param>
  /// <remarks>The text is not checked; the server refuses what is not JSON where the column is of its JSON type.
  /// The next read of the member makes the object from the text.</remarks>
  public static void SetJson(EntityRecord record, EntityMemberInfo member, string? json) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    record.SetValue(member, GetTextValue(member, json));
  }

  // What a member's slot holds for a document as text: the text itself for a string member, marked text otherwise.
  private static object? GetTextValue(EntityMemberInfo member, string? text) =>
    text == null ? null : member.DataType == typeof(string) ? text : new JsonText(text);

  // The type the discriminator column names for this member, or null where it names none.
  private static Type? FindNamedType(EntityRecord record, EntityMemberInfo member) =>
    member.JsonTypes!.FindType(record.GetRawValue(member.JsonDiscriminatorMember!));

  /// <summary>Gets the columns of a statement's root select list that hold a JSON document typed by a discriminator
  /// column, each paired with that column's place on the same list.</summary>
  /// <param name="sqlRoot">The root of the statement's tree.</param>
  /// <returns>The typed columns; empty for a root that is no select, or a select reading whole rows.</returns>
  /// <remarks>A document projected without its discriminator beside it is refused, since nothing could say which
  /// type to read; the caller selects the column too, or the entity.</remarks>
  public static IReadOnlyList<JsonTypedColumn> GetTypedColumns(QueryNode? sqlRoot) {
    if (sqlRoot is not SelectNode { SelectList.Children: var selected } || selected.Count == 0)
      return [];
    List<JsonTypedColumn>? typed = null;
    for (var i = 0; i < selected.Count; i++) {
      if (GetProjectedMember(selected[i]) is not { JsonDiscriminatorMember: { } discriminator } member)
        continue;
      var row = ((ColumnNode)((SelectedValueNode)selected[i]).Source).Row;
      var at = -1;
      for (var j = 0; j < selected.Count && at < 0; j++)
        if (selected[j] is SelectedValueNode { Source: ColumnNode { Column.Member: var other } column }
            && ReferenceEquals(other, discriminator) && ReferenceEquals(column.Row.RowSet, row.RowSet))
          at = j;
      if (at < 0)
        throw new NotSupportedException(
          $"'{member.Entity?.Name}.{member.Name}' is read as the type its '{discriminator.Name}' column names — select "
          + $"'{discriminator.Name}' beside it, or the entity.");
      (typed ??= []).Add(new JsonTypedColumn(i, at, member));
    }
    return typed ?? [];
  }

  // The entity member a select-list entry projects as a plain column, or null for anything else.
  private static EntityMemberInfo? GetProjectedMember(QueryNode selected) =>
    selected is SelectedValueNode { Source: ColumnNode { Column.Member: EntityMemberInfo member } } ? member : null;

  /// <summary>Lays the typed columns out by column ordinal, for a reader walking a row.</summary>
  /// <param name="typedColumns">The typed columns of the statement.</param>
  /// <param name="columnCount">The number of columns in the row.</param>
  /// <returns>An array with each typed column at its ordinal and <see langword="null"/> at every other.</returns>
  public static JsonTypedColumn?[] GetTypedByColumn(IReadOnlyList<JsonTypedColumn> typedColumns, int columnCount) {
    ArgumentNullException.ThrowIfNull(typedColumns);
    var byColumn = new JsonTypedColumn?[columnCount];
    foreach (var typed in typedColumns)
      if (typed.Column < columnCount)
        byColumn[typed.Column] = typed;
    return byColumn;
  }

  /// <summary>Reads a typed column's document out of the current row, as the type its discriminator column
  /// names.</summary>
  /// <param name="provider">The provider that converts the discriminator to its member's type.</param>
  /// <param name="reader">The reader positioned on the row.</param>
  /// <param name="typed">The typed column to read.</param>
  /// <returns>The document, or <see langword="null"/> for a null column.</returns>
  /// <remarks>The discriminator is read off its own column; a value that names no mapped type is refused by
  /// name.</remarks>
  public static object? ReadTypedColumn(DbProvider provider, System.Data.Common.DbDataReader reader, JsonTypedColumn typed) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(typed);
    if (reader.IsDBNull(typed.Column))
      return null;
    var member = typed.Member;
    var discriminatorMember = member.JsonDiscriminatorMember!;
    var discriminator = reader.IsDBNull(typed.DiscriminatorColumn) ? null
      : provider.ConvertReadValue(reader.GetValue(typed.DiscriminatorColumn), discriminatorMember.DataType);
    var type = member.JsonTypes!.FindType(discriminator)
      ?? throw new InvalidOperationException(
        $"'{member.Entity?.Name}.{discriminatorMember.Name}' holds '{discriminator ?? "null"}', which names none of "
        + $"the types mapped for '{member.DataType.Name}'.");
    return Deserialize(GetText(reader.GetValue(typed.Column)), type);
  }

  /// <summary>Writes a value as JSON text of a given type.</summary>
  /// <param name="value">The value to write.</param>
  /// <param name="asType">The type to write the value as.</param>
  /// <returns>The text; a string is its own text.</returns>
  public static string Serialize(object value, Type asType) =>
    value as string ?? JsonSerializer.Serialize(value, asType, Options);

  /// <summary>Reads a member's object out of what the row holds.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <param name="stored">The value the row holds for the column.</param>
  /// <returns>The object, or <see langword="null"/> for a null value; the text itself for a string member.</returns>
  /// <remarks>With a discriminator in another column, the document is read as the type that column names; a value
  /// that names no mapped type is refused by name.</remarks>
  public static object? Deserialize(EntityRecord record, EntityMemberInfo member, object? stored) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    if (stored is null or DBNull)
      return null;
    var type = member.DataType;
    if (member.JsonDiscriminatorMember is { } discriminatorMember)
      type = FindNamedType(record, member)
        ?? throw new InvalidOperationException(
          $"'{member.Entity?.Name}.{discriminatorMember.Name}' holds '{record.GetRawValue(discriminatorMember)}', "
          + $"which names none of the types mapped for '{member.DataType.Name}'.");
    return Deserialize(GetText(stored), type);
  }

  /// <summary>Reads an object of a declared type out of JSON text.</summary>
  /// <param name="text">The JSON text.</param>
  /// <param name="declaredType">The type to read the text as.</param>
  /// <returns>The object; the text itself where a string is wanted.</returns>
  public static object? Deserialize(string text, Type declaredType) =>
    declaredType == typeof(string) ? text : JsonSerializer.Deserialize(text, declaredType, Options);

  /// <summary>Gets the text a driver handed back for a JSON column.</summary>
  /// <param name="stored">The driver's value: a string on every server, or the driver's own JSON value.</param>
  /// <returns>The text.</returns>
  public static string GetText(object stored) =>
    stored as string ?? Convert.ToString(stored, System.Globalization.CultureInfo.InvariantCulture) ?? "";

  /// <summary>Specifies how many paths an update writes into a document before the whole document is written
  /// instead.</summary>
  public const int MaxPatches = 8;

  /// <summary>Gets the paths of a JSON member's document that changed in place since it was loaded or last saved,
  /// each with its new value or marked removed, for an update that writes them into the column rather than the whole
  /// document.</summary>
  /// <param name="record">The record the member belongs to.</param>
  /// <param name="member">The JSON member.</param>
  /// <returns>The patches, or <see langword="null"/> where the whole document is what to write: no text kept, either
  /// side no object, more than <see cref="MaxPatches"/> paths, or the values together no shorter than the
  /// document.</returns>
  /// <remarks>A changed array is one patch, the array whole. A property added is a patch with its value; a property
  /// removed is a patch with none, which every server spells as a removal at the path.</remarks>
  public static IReadOnlyList<JsonPatch>? GetPatches(EntityRecord record, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(member);
    var loaded = record.GetLoadedJsonText(member);
    var current = Serialize(record, member)?.Text;
    if (loaded == null || current == null)
      return null;
    System.Text.Json.Nodes.JsonNode? before, after;
    try {
      before = System.Text.Json.Nodes.JsonNode.Parse(loaded);
      after = System.Text.Json.Nodes.JsonNode.Parse(current);
    } catch (JsonException) {
      return null;
    }
    if (before is not System.Text.Json.Nodes.JsonObject old || after is not System.Text.Json.Nodes.JsonObject now)
      return null;
    var patches = new List<JsonPatch>();
    CollectPatches(old, now, [], patches);
    if (patches.Count == 0 || patches.Count > MaxPatches)
      return null;
    return patches.Sum(p => p.Json.Length) < current.Length ? patches : null;
  }

  // The patches that turn `old` into `now`, under the steps so far: a value at each path that changed or appeared,
  // and a removal at each that is gone.
  private static void CollectPatches(System.Text.Json.Nodes.JsonObject old, System.Text.Json.Nodes.JsonObject now,
      List<string> steps, List<JsonPatch> patches) {
    foreach (var (key, _) in old)
      if (!now.ContainsKey(key)) {
        steps.Add(key);
        patches.Add(MakePatch(steps, null, remove: true));
        steps.RemoveAt(steps.Count - 1);
      }
    foreach (var (key, value) in now) {
      var had = old.TryGetPropertyValue(key, out var was);
      if (had && System.Text.Json.Nodes.JsonNode.DeepEquals(was, value))
        continue;
      steps.Add(key);
      if (was is System.Text.Json.Nodes.JsonObject wasObject && value is System.Text.Json.Nodes.JsonObject nowObject)
        CollectPatches(wasObject, nowObject, steps, patches);
      else
        patches.Add(MakePatch(steps, value, remove: false));
      steps.RemoveAt(steps.Count - 1);
    }
  }

  private static JsonPatch MakePatch(List<string> steps, System.Text.Json.Nodes.JsonNode? value, bool remove) {
    var path = "$" + string.Concat(steps.Select(s => $".\"{s}\""));
    var (scalar, isScalar) = value switch {
      null => (null, true),
      System.Text.Json.Nodes.JsonValue held => (GetScalar(held), true),
      _ => ((object?)null, false),
    };
    return new JsonPatch(path, steps.ToArray(), value?.ToJsonString() ?? "null", scalar, isScalar, remove);
  }

  // A JSON value as the plain scalar it holds; a number as a whole number, an exact decimal, or a double.
  private static object? GetScalar(System.Text.Json.Nodes.JsonValue value) => value.GetValueKind() switch {
    JsonValueKind.String => value.GetValue<string>(),
    JsonValueKind.Number => value.TryGetValue<long>(out var whole) ? whole
      : value.TryGetValue<decimal>(out var exact) ? exact : value.GetValue<double>(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    _ => null,
  };

  /// <summary>Remembers the current text of every JSON member a save wrote as the text the row holds.</summary>
  /// <param name="record">The record whose texts to accept.</param>
  /// <remarks>Call this before the record's modified mask is cleared: a member not counted as modified was not
  /// written, and keeps the text it has.</remarks>
  public static void AcceptTexts(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    foreach (var member in record.Entity.JsonMembers)
      if (record.Modified.IsSet(member.DataIndex))
        record.SetLoadedJsonText(member, Serialize(record, member)?.Text);
  }
}

/// <summary>Represents a column of a projection holding a JSON document whose type the row's discriminator column
/// names, and where that column lies in the same row of the result.</summary>
/// <param name="Column">The zero-based index of the document's column in the result.</param>
/// <param name="DiscriminatorColumn">The zero-based index of the discriminator's column in the result.</param>
/// <param name="Member">The JSON member, which carries the discriminator member and the typed map.</param>
public sealed record JsonTypedColumn(int Column, int DiscriminatorColumn, EntityMemberInfo Member);
