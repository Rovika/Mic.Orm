using System.Text;
using System.Text.RegularExpressions;
using Mic.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.DbTool;

/// <summary>Writes the C# declarations of a database read into a <see cref="DbModel"/>: an entity interface per
/// table and view, a module per schema, and the application class.</summary>
/// <remarks>With <see cref="EntitySourceOptions.AddDbObjectsNames"/> every table, column, key and foreign key is
/// named in an attribute, so the identifiers can be renamed and the mapping stands; without it a name is written
/// only where it differs from what the naming policy would produce, so a database MIC made comes back as the
/// declarations it was made from. A table referenced from the selection but not in it leaves its foreign-key column
/// a plain column; the file's header and <see cref="Warnings"/> say so. A check constraint is written as a
/// comment above its interface and not declared, since a declared check is created under the ORM's own name. The
/// check the ORM makes for a JSON member whose column is text is not written: <c>[Json]</c> on the member declares
/// it.</remarks>
public sealed class EntitySourceWriter {
  /// <summary>Gets what the writer could not express, one line each, after <see cref="Write"/> ran.</summary>
  public IReadOnlyList<string> Warnings => _warnings;

  readonly DbModel _model;
  readonly DbSettings _settings;
  readonly EntitySourceOptions _options;
  readonly NamingPolicy _policy = new();
  readonly DbTypeRegistry _registry;
  readonly List<string> _warnings = [];
  readonly List<EntitySpec> _entities = [];
  readonly Dictionary<DbColumnInfo, string> _memberNames = [];
  readonly Dictionary<DbColumnInfo, ReferenceSpec> _referenceByColumn = [];
  readonly HashSet<DbCheckConstraintInfo> _jsonChecks = [];
  bool _usesGeoPoint;
  int _outsideReferences;

  /// <summary>Initializes a new instance of the <see cref="EntitySourceWriter"/> class.</summary>
  /// <param name="model">The model read from the database.</param>
  /// <param name="settings">The settings the model was read with.</param>
  /// <param name="options">What to write.</param>
  public EntitySourceWriter(DbModel model, DbSettings settings, EntitySourceOptions options) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(options);
    _model = model;
    _settings = settings;
    _options = options;
    _registry = settings.Provider.TypeRegistry;
  }

  /// <summary>Writes the source.</summary>
  /// <returns>The C# source of one file.</returns>
  /// <exception cref="InvalidOperationException">The selection holds no table.</exception>
  public string Write() {
    SelectEntities();
    if (_entities.Count == 0)
      throw new InvalidOperationException("The selection holds no table; nothing to write.");
    foreach (var entity in _entities)
      NameColumns(entity);
    foreach (var entity in _entities.Where(e => !e.IsView))
      BuildReferences(entity);
    foreach (var entity in _entities)
      BuildMembers(entity);
    // A view has no keys: it is written as its columns alone, and its rows are read untracked.
    foreach (var entity in _entities.Where(e => !e.IsView)) {
      BuildKeys(entity);
      MoveKeyFirst(entity);
      BuildChecks(entity);
    }
    if (_options.Lists)
      BuildLists();
    return WriteFile();
  }

  // ----- the selection ------------------------------------------------------------------------------------------------

  private void SelectEntities() {
    var usedNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var table in _model.Tables) {
      if (table.IsTemporary || IsDbInfoTable(table) || !IsSelected(table.Schema.Name, table.Name))
        continue;
      // An entity has a primary key. A table that declares none is written with a unique key standing in for it,
      // or is left out.
      var standInKey = table.PrimaryKey == null ? FindStandInKey(table) : null;
      if (table.PrimaryKey == null && standInKey == null) {
        _warnings.Add($"{table.Name}: the table has no primary key and no unique key over required columns; "
          + "not written.");
        continue;
      }
      if (standInKey != null)
        _warnings.Add($"{table.Name}: the table has no primary key; its unique key {standInKey.Name} is written "
          + "as the primary key.");
      _entities.Add(new EntitySpec(table.Name, table.Schema, table.Columns, usedNames, _entities.Count) {
        Table = table, Keys = table.Keys, RefConstraints = table.RefConstraints, StandInKey = standInKey,
      });
    }
    foreach (var view in _model.Views)
      if (IsSelected(view.Schema.Name, view.Name))
        _entities.Add(new EntitySpec(view.Name, view.Schema, view.Columns, usedNames, _entities.Count) {
          View = view, ViewSelect = ExtractSelect(view.Source),
        });

    // The catalog's order is nobody's; the file is ordered by name, the views after the tables, a view after the
    // views it reads so that they are created in that order.
    _entities.Sort((x, y) => x.IsView != y.IsView ? x.IsView.CompareTo(y.IsView) : string.CompareOrdinal(x.Name, y.Name));
    OrderViewsByDependency();
  }

  /// <summary>Finds the unique key that stands in for the primary key a table does not declare: one whose columns
  /// are all required, the one of fewest columns, the first by name among those.</summary>
  /// <returns>The key, or <see langword="null"/> if the table has no such key.</returns>
  private static DbKeyInfo? FindStandInKey(DbTableInfo table) =>
    table.Keys
      .Where(k => k.IsUnique && !k.IsForeignKey && k.Filter == null && k.Columns.Count > 0
        && k.Columns.All(c => !c.Column.IsNullable))
      .OrderBy(k => k.Columns.Count).ThenBy(k => k.Name, StringComparer.Ordinal)
      .FirstOrDefault();

  /// <summary>Moves a view after every view its select names, keeping the name order otherwise; a cycle, which a
  /// server would not have allowed, leaves the order as it is.</summary>
  private void OrderViewsByDependency() {
    var views = _entities.Where(e => e.IsView).ToList();
    var ordered = new List<EntitySpec>();
    while (views.Count > 0) {
      var independent = views.FirstOrDefault(v => !views.Any(other => other != v && Reads(v, other))) ?? views[0];
      views.Remove(independent);
      ordered.Add(independent);
    }
    _entities.RemoveAll(e => e.IsView);
    _entities.AddRange(ordered);
  }

  private static bool Reads(EntitySpec view, EntitySpec other) =>
    view.ViewSelect != null
    && Regex.IsMatch(view.ViewSelect, $@"\b{Regex.Escape(other.TableName)}\b", RegexOptions.IgnoreCase);

  /// <summary>Cuts a view's definition down to its select where a server reports the whole CREATE statement, as SQL
  /// Server and SQLite do; trims a trailing semicolon.</summary>
  private static string? ExtractSelect(string? definition) {
    if (string.IsNullOrWhiteSpace(definition))
      return null;
    var text = definition.Trim();
    var create = Regex.Match(text, @"^CREATE\s+(OR\s+(REPLACE|ALTER)\s+)?(MATERIALIZED\s+)?VIEW\b.*?\bAS\b\s*",
      RegexOptions.IgnoreCase | RegexOptions.Singleline);
    if (create.Success)
      text = text[create.Length..];
    return text.TrimEnd().TrimEnd(';').TrimEnd();
  }

  private bool IsDbInfoTable(DbTableInfo table) =>
    string.Equals(table.Name, _settings.DbInfoTableName, StringComparison.OrdinalIgnoreCase);

  private bool IsSelected(string schema, string name) =>
    (_options.Tables.Count == 0 || _options.Tables.Any(p => Matches(p, schema, name)))
    && !_options.IgnoreTables.Any(p => Matches(p, schema, name));

  private static bool Matches(string pattern, string schema, string name) {
    var dot = pattern.IndexOf('.');
    var namePattern = dot < 0 ? pattern : pattern[(dot + 1)..];
    if (dot >= 0 && !string.Equals(pattern[..dot], schema, StringComparison.OrdinalIgnoreCase))
      return false;
    var regex = "^" + Regex.Escape(namePattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
    return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
  }

  // ----- names --------------------------------------------------------------------------------------------------------

  private void NameColumns(EntitySpec entity) {
    foreach (var column in entity.Columns)
      _memberNames[column] = entity.TakeName(ToPascalCase(column.Name));
  }

  /// <summary>Turns a database name into a C# identifier: underscores and spaces removed with the next letter
  /// capitalized, the first letter capitalized, anything else that is not a letter or digit replaced.</summary>
  private static string ToPascalCase(string name) {
    var parts = name.Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
    var text = parts.Length <= 1
      ? Capitalize(name.Trim())
      : string.Concat(parts.Select(Capitalize));
    var chars = text.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
    var identifier = new string(chars);
    return identifier.Length == 0 || char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
  }

  private static string Capitalize(string part) =>
    part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];

  // ----- references ---------------------------------------------------------------------------------------------------

  private void BuildReferences(EntitySpec entity) {
    foreach (var constraint in entity.RefConstraints) {
      // It constrains nothing, and written as a reference it would key the entity on itself.
      if (IsSelfKeyReference(constraint)) {
        _warnings.Add($"{entity.Name}: the foreign key {constraint.Name} points from a key to the same key; "
          + "not written.");
        continue;
      }
      var target = _entities.FirstOrDefault(e => ReferenceEquals(e.Table, constraint.ToTable));
      if (target == null) {
        _outsideReferences++;
        _warnings.Add($"{entity.Name}: the foreign key to {constraint.ToTable.Schema.Name}.{constraint.ToTable.Name}"
          + " points outside the selection; its columns are written as plain columns.");
        foreach (var keyColumn in constraint.FromKey.Columns)
          entity.OutsideReferences[keyColumn.Column] = constraint.ToTable.Name;
        continue;
      }
      var reference = new ReferenceSpec(constraint, entity, target, DeriveReferenceName(entity, constraint, target));
      entity.References.Add(reference);
      foreach (var keyColumn in constraint.FromKey.Columns)
        _referenceByColumn[keyColumn.Column] = reference;
    }
  }

  /// <summary>Decides whether a foreign key points from a key to that same key, column for column.</summary>
  private static bool IsSelfKeyReference(DbRefConstraintInfo constraint) {
    if (!ReferenceEquals(constraint.FromKey.Table, constraint.ToTable))
      return false;
    var fromColumns = constraint.FromKey.Columns.Select(c => c.Column);
    var toColumns = constraint.ToKey.Columns.Select(c => c.Column);
    return fromColumns.SequenceEqual(toColumns);
  }

  /// <summary>Derives a reference member's name from its column: the target key's member name cut off the end, as
  /// the naming policy composes it; the target entity's name where that leaves nothing or the key is composite.
  /// </summary>
  private string DeriveReferenceName(EntitySpec entity, DbRefConstraintInfo constraint, EntitySpec target) {
    string baseName;
    if (constraint.FromKey.Columns.Count == 1) {
      var columnName = constraint.FromKey.Columns[0].Column.Name;
      var targetMember = _memberNames[constraint.ToKey.Columns[0].Column];
      baseName = columnName.EndsWith(targetMember, StringComparison.OrdinalIgnoreCase)
          && columnName.Length > targetMember.Length
        ? ToPascalCase(columnName[..^targetMember.Length].TrimEnd('_', ' '))
        : target.BaseName;
      if (baseName.Length == 0)
        baseName = target.BaseName;
    } else
      baseName = target.BaseName;
    return entity.TakeName(baseName, "Ref");
  }

  // ----- members ------------------------------------------------------------------------------------------------------

  private void BuildMembers(EntitySpec entity) {
    var written = new HashSet<ReferenceSpec>();
    foreach (var column in entity.Columns) {
      if (_referenceByColumn.TryGetValue(column, out var reference) && reference.Entity == entity) {
        if (written.Add(reference))
          entity.Members.Add(BuildReferenceMember(reference));
        continue;
      }
      var member = BuildColumnMember(entity, column);
      if (member != null)
        entity.Members.Add(member);
    }
  }

  private MemberSpec BuildReferenceMember(ReferenceSpec reference) {
    var constraint = reference.Constraint;
    var member = new MemberSpec(reference.Name,
      reference.Target.Name + (constraint.FromKey.Columns.Any(c => c.Column.IsNullable) ? "?" : ""));
    if (_options.AddDbObjectsNames || !HasPolicyColumnNames(reference)) {
      var columnNames = string.Join(",", constraint.FromKey.Columns.Select(c => c.Column.Name));
      // A server that keeps no constraint names reports invented ones, which would pin nothing.
      var named = _options.AddDbObjectsNames && _settings.Provider.Supports(DbFeatures.NamedRefConstraints);
      var foreignKeyName = named ? $", ForeignKeyName = {Quote(constraint.Name)}" : "";
      member.Attributes.Add($"EntityRef(KeyColumns = {Quote(columnNames)}{foreignKeyName})");
    }
    if (constraint.CascadeDelete)
      member.Attributes.Add("CascadeDelete");
    return member;
  }

  /// <summary>Decides whether the foreign key's columns are named as the policy would name them from the reference
  /// member and the target key's members, in which case no attribute states them.</summary>
  private bool HasPolicyColumnNames(ReferenceSpec reference) {
    var fromColumns = reference.Constraint.FromKey.Columns;
    var toColumns = reference.Constraint.ToKey.Columns;
    if (fromColumns.Count != toColumns.Count)
      return false;
    for (var i = 0; i < fromColumns.Count; i++)
      if (fromColumns[i].Column.Name != _policy.GetForeignKeyMemberName(reference.Name, _memberNames[toColumns[i].Column]))
        return false;
    return true;
  }

  private MemberSpec? BuildColumnMember(EntitySpec entity, DbColumnInfo column) {
    // The whole spec first, since a server may register one as a name of its own, such as nvarchar(max).
    var typeDef = _registry.FindByName(column.TypeSpec) ?? _registry.ParseSpec(column.TypeSpec, out _)?.TypeDef;
    var clrType = column.TypeRef.ClrType ?? typeDef?.ClrType;
    if (clrType == typeof(byte[]) && IsGuidType(column.TypeSpec))
      clrType = typeof(Guid);
    // The other way round: a server that holds a Guid in a binary type reads every column of that type as one, and
    // only the Guid's own size is a Guid. Any other size is the binary it says.
    if (clrType == typeof(Guid) && typeDef is { TakesSize: true } && !IsGuidType(column.TypeSpec)) {
      clrType = typeof(byte[]);
      typeDef = _registry.MapType(clrType, column.Size)?.TypeDef ?? typeDef;
    }
    // A vector with no dimensions stated is no member: [Vector] wants the number.
    var isVector = typeDef?.SpecialType == DbSpecialType.Vector;
    if (clrType == null || column.IsComputed || isVector && column.Size <= 0) {
      var reason = clrType == null ? $"type '{column.TypeSpec}' maps to no CLR type"
        : column.IsComputed ? "it is computed by the server"
        : "it is a vector with no dimensions";
      entity.SkippedColumns.Add($"{column.Name}: {reason}; not written.");
      _warnings.Add($"{entity.Name}.{column.Name}: {reason}; not written.");
      return null;
    }
    var member = new MemberSpec(_memberNames[column], GetTypeName(clrType, column.IsNullable)) { ClrType = clrType };
    // A JSON column is one of the server's JSON type, or a text column under the check the ORM makes for one.
    var jsonCheck = FindJsonTextCheck(entity, column, typeDef);
    if (jsonCheck != null)
      _jsonChecks.Add(jsonCheck);
    var isJson = jsonCheck != null || typeDef?.SpecialType == DbSpecialType.Json;
    AddTypeAttributes(member, column, typeDef, clrType, isJson, isVector);
    if (column.IsIdentity)
      member.Attributes.Add("Identity");
    else if (column.DbFlags.HasFlag(DbColumnFlags.RowVersion))
      member.Attributes.Add("RowVersion");
    if (entity.OutsideReferences.TryGetValue(column, out var outsideTable))
      member.Comment = $"references {outsideTable}, which is outside the selection";
    return member;
  }

  /// <summary>Adds the attributes that pin the column's type: its size or unlimited length, precision and scale, the
  /// column name where it is not the member's, the type spec where the registry would choose another, the default.
  /// </summary>
  /// <remarks>A JSON column gets <c>[Json]</c> in place of a length and a type spec, since the attribute alone
  /// decides its type; a vector column gets <c>[Vector]</c> so.</remarks>
  private void AddTypeAttributes(MemberSpec member, DbColumnInfo column, DbTypeDef? typeDef, Type clrType,
      bool isJson, bool isVector) {
    // A length belongs to text and binary; a Guid in raw(16) or binary(16) has none to state.
    var lengthy = clrType == typeof(string) || clrType == typeof(byte[]);
    var sized = lengthy && typeDef is { TakesSize: true } && column.Size > 0;
    var unlimited = lengthy && typeDef is { IsUnlimited: true };
    if (isJson)
      member.Attributes.Add("Json");
    else if (isVector)
      member.Attributes.Add(GetVectorAttribute(column));
    else if (sized)
      member.Attributes.Add($"Size({column.Size})");
    else if (unlimited)
      member.Attributes.Add("Unlimited");

    var arguments = new List<string>();
    if (_options.AddDbObjectsNames || column.Name != member.Name)
      arguments.Add(Quote(column.Name));
    if (typeDef is { TakesPrecisionScale: true }) {
      if (column.Precision > 0)
        arguments.Add($"Precision = {column.Precision}");
      if (column.Scale > 0)
        arguments.Add($"Scale = {column.Scale}");
    }
    var expected = _registry.MapType(clrType, sized ? column.Size : 0, column.Precision, column.Scale, unlimited);
    if (!isJson && !isVector && !string.Equals(expected?.TypeSpec, column.TypeSpec, StringComparison.OrdinalIgnoreCase))
      arguments.Add($"DbTypeSpec = {Quote(column.TypeSpec)}");
    if (column.DefaultExpression != null)
      arguments.Add($"Default = {Quote(column.DefaultExpression)}");
    if (arguments.Count > 0)
      member.Attributes.Add($"Column({string.Join(", ", arguments)})");
  }

  /// <summary>Gets the <c>[Vector]</c> attribute of a vector column: its dimensions, and the metric of the vector
  /// index over it where there is one and the metric is not the default.</summary>
  private static string GetVectorAttribute(DbColumnInfo column) {
    var metric = column.Table?.Keys
      .FirstOrDefault(k => k.VectorMetric != null && k.Columns is [{ Column: var indexed }] && indexed == column)
      ?.VectorMetric;
    return metric is null or VectorMetric.Cosine
      ? $"Vector({column.Size})"
      : $"Vector({column.Size}, Metric = VectorMetric.{metric})";
  }

  /// <summary>Finds the check the ORM made for a JSON member whose column is text on this server.</summary>
  /// <returns>The check, or <see langword="null"/> if the column is not unlimited text or carries no such check.
  /// </returns>
  /// <remarks>The check's name ends with a hash of its condition as the ORM wrote it for the column, so the name is
  /// built again and compared; the server's rewrite of the condition is not read.</remarks>
  private DbCheckConstraintInfo? FindJsonTextCheck(EntitySpec entity, DbColumnInfo column, DbTypeDef? typeDef) {
    var dialect = _settings.Provider.SqlDialect;
    if (entity.Table is not { } table || dialect.JsonTextCheck == null || typeDef == null
        || typeDef != _registry.FindSpecial(DbSpecialType.StringUnlimited))
      return null;
    var condition = string.Format(dialect.JsonTextCheck, dialect.Quote(column.Name));
    foreach (var check in table.CheckConstraints) {
      var declaredName = DbCheckConstraintInfo.GetCheckName(check.Name, table.Name);
      if (declaredName == null || !declaredName.EndsWith(DbModelBuilder.JsonCheckSuffix, StringComparison.Ordinal))
        continue;
      var expectedName = DbCheckConstraintInfo.GetManagedName(table.Name, declaredName, condition);
      if (string.Equals(check.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        return check;
    }
    return null;
  }

  /// <summary>Decides whether a binary column is of the type this server stores a Guid in, such as Oracle's
  /// <c>raw(16)</c>, in which case it is read as one.</summary>
  private bool IsGuidType(string typeSpec) =>
    string.Equals(_registry.MapType(typeof(Guid))?.TypeSpec, typeSpec, StringComparison.OrdinalIgnoreCase);

  private string GetTypeName(Type clrType, bool nullable) {
    if (clrType == typeof(GeoPoint))
      _usesGeoPoint = true;
    var name = clrType == typeof(byte[]) ? "byte[]"
      : clrType.IsArray ? GetTypeName(clrType.GetElementType()!, nullable: false) + "[]"
      : _keywords.TryGetValue(clrType, out var keyword) ? keyword
      : clrType.FullName!;
    return nullable ? name + "?" : name;
  }

  static readonly Dictionary<Type, string> _keywords = new() {
    [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short",
    [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
    [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
    [typeof(string)] = "string", [typeof(char)] = "char", [typeof(Guid)] = "Guid", [typeof(DateTime)] = "DateTime",
    [typeof(DateTimeOffset)] = "DateTimeOffset", [typeof(DateOnly)] = "DateOnly", [typeof(TimeOnly)] = "TimeOnly",
    [typeof(TimeSpan)] = "TimeSpan", [typeof(GeoPoint)] = "GeoPoint",
  };

  // ----- keys ---------------------------------------------------------------------------------------------------------

  private void BuildKeys(EntitySpec entity) {
    foreach (var key in entity.Keys.Where(k => !k.IsForeignKey)) {
      var isPrimary = key.IsPrimaryKey || ReferenceEquals(key, entity.StandInKey);
      var members = key.Columns.Select(c => GetKeyMemberName(entity, c.Column)).ToList();
      var owner = key.Columns.Count == 1 && !key.Columns[0].Descending
        ? entity.Members.FirstOrDefault(m => m.Name == members[0]) : null;
      var arguments = new List<string>();
      if (owner == null)
        arguments.Add(Quote(string.Join(",", key.Columns.Select((c, i) => c.Descending ? "-" + members[i] : members[i]))));
      if (_options.AddDbObjectsNames)
        arguments.Add($"Name = {Quote(key.Name)}");
      if (key.Filter != null)
        arguments.Add($"Filter = {Quote(key.Filter)}");
      if (key.IncludedColumns.Count > 0) {
        var included = string.Join(",", key.IncludedColumns.Select(c => GetKeyMemberName(entity, c)));
        arguments.Add($"IncludeMembers = {Quote(included)}");
      }
      // A vector index states no access method: an index over a vector member is the server's vector index.
      if (!isPrimary && key.IndexType != _settings.Provider.DefaultIndexType && key.IndexType != DbIndexType.Default
          && key.VectorMetric == null)
        arguments.Add($"IndexType = DbIndexType.{key.IndexType}");
      var attributeName = isPrimary ? "PrimaryKey" : GetIndexAttributeName(key);
      var attribute = attributeName + (arguments.Count == 0 ? "" : $"({string.Join(", ", arguments)})");
      if (isPrimary)
        entity.KeyMemberNames.AddRange(members);
      if (owner == null)
        entity.EntityAttributes.Add(attribute);
      else if (!isPrimary)
        owner.Attributes.Add(attribute);
      else {
        owner.Attributes.Insert(0, attribute);
        if (_options.AutoGuidKeys && !key.Columns[0].Column.IsIdentity && owner.ClrType == typeof(Guid))
          owner.Attributes.Insert(1, "Auto");
      }
    }
  }

  /// <summary>Moves the primary key's members to the front of the entity, in their own order; the column order of
  /// the database decides the rest.</summary>
  private static void MoveKeyFirst(EntitySpec entity) {
    var keyMembers = entity.Members.Where(m => entity.KeyMemberNames.Contains(m.Name)).ToList();
    foreach (var member in keyMembers)
      entity.Members.Remove(member);
    entity.Members.InsertRange(0, keyMembers);
  }

  /// <summary>Gets the name a key's member list refers to a column by: the reference member for the column of a
  /// single-column foreign key, the foreign-key member the policy names for a column of a composite one, the column's
  /// own member otherwise.</summary>
  private string GetKeyMemberName(EntitySpec entity, DbColumnInfo column) {
    if (!_referenceByColumn.TryGetValue(column, out var reference) || reference.Entity != entity)
      return _memberNames[column];
    var fromColumns = reference.Constraint.FromKey.Columns;
    if (fromColumns.Count == 1)
      return reference.Name;
    if (!HasPolicyColumnNames(reference))
      return column.Name;
    var position = fromColumns.ToList().FindIndex(c => ReferenceEquals(c.Column, column));
    return _policy.GetForeignKeyMemberName(reference.Name, _memberNames[reference.Constraint.ToKey.Columns[position].Column]);
  }

  /// <summary>Gets the attribute for the flags of a key that is not the primary key; clustering is written for an
  /// index and not for a primary key, whose placement is the server's default.</summary>
  private static string GetIndexAttributeName(DbKeyInfo key) {
    var clustered = key.Flags.HasFlag(KeyFlags.Clustered);
    return key.IsUnique
      ? clustered ? "UniqueClusteredIndex" : "Unique"
      : clustered ? "ClusteredIndex" : "Index";
  }

  // ----- lists --------------------------------------------------------------------------------------------------------

  private void BuildLists() {
    foreach (var entity in _entities)
      foreach (var reference in entity.References) {
        var target = reference.Target;
        var listName = target.TakeName(_policy.GetPluralName(entity.BaseName));
        var list = new MemberSpec(listName, $"IList<{entity.Name}>") { GetterOnly = true, IsList = true };
        if (entity.References.Count(r => r.Target == target) > 1)
          list.Attributes.Add($"OneToMany({Quote(reference.Name)})");
        target.Members.Add(list);
      }
  }

  // ----- check constraints -------------------------------------------------------------------------------------------

  /// <summary>Builds the comment lines of a table's check constraints: each one's name and condition, and the
  /// declaration that would make it the application's, commented out.</summary>
  /// <remarks>A constraint under the database's own name is kept by the database and left alone by an upgrade. One
  /// under the name the ORM gives a declared check is dropped by an upgrade over a model that does not declare it.
  /// A condition with a braced word outside quoted text gets no declaration, since <c>[Check]</c> reads such a word
  /// as a member. The check of a JSON member gets no lines: <c>[Json]</c> on the member declares it.
  /// </remarks>
  private void BuildChecks(EntitySpec entity) {
    var table = entity.Table!;
    var lines = entity.CheckComments;
    foreach (var check in table.CheckConstraints.OrderBy(c => c.Name, StringComparer.Ordinal)) {
      if (_jsonChecks.Contains(check))
        continue;
      var condition = Regex.Replace(check.Expression, @"\s*[\r\n]+\s*", " ").Trim();
      var declaredName = DbCheckConstraintInfo.GetCheckName(check.Name, table.Name);
      var isManaged = declaredName != null;
      lines.Add($"Check constraint {check.Name}"
        + (condition.Length == 0 ? "; the server reports no condition." : $": {condition}"));
      if (isManaged) {
        lines.Add("It carries the name MIC gives a check it declares, so an upgrade over this model drops it unless "
          + "it is declared.");
        _warnings.Add($"{entity.Name}: check constraint {check.Name} carries MIC's name and is not declared; an "
          + "upgrade over this model drops it.");
      } else {
        lines.Add("The database keeps it, and an upgrade leaves it alone.");
        _warnings.Add($"{entity.Name}: check constraint {check.Name} is written as a comment, not declared.");
      }
      if (condition.Length == 0)
        continue;
      if (DbModelBuilder.HasBracedNames(condition)) {
        lines.Add("Its condition holds a braced word outside quoted text, which [Check] reads as a member's name, "
          + "so it cannot be declared as it is.");
        continue;
      }
      lines.Add(isManaged
        ? "To declare it, uncomment; the first upgrade replaces it under the name of this text:"
        : $"To have MIC own it, uncomment, and drop {check.Name}:");
      var name = declaredName ?? ToPascalCase(check.Name);
      lines.Add($"[Check(DbServerKind.{_settings.Provider.ServerKind}, {Quote(name)}, {Quote(condition)})]");
    }
  }

  // ----- the text -----------------------------------------------------------------------------------------------------

  private string WriteFile() {
    var text = new StringBuilder();
    var schemas = _entities.Select(e => e.Schema.Name).Distinct().ToList();
    text.AppendLine("// <auto-generated>");
    text.AppendLine($"//   Generated by the MIC database tool on {DateTime.Now:yyyy-MM-dd HH:mm} from the "
      + $"{_settings.Provider.ServerKind} database '{_settings.DatabaseAlias}'"
      + (schemas.Any(s => s.Length > 0) ? $", schema(s) {string.Join(", ", schemas.Where(s => s.Length > 0))}." : "."));
    text.AppendLine($"//   {_entities.Count(e => !e.IsView)} table(s), {_entities.Count(e => e.IsView)} view(s).");
    if (_outsideReferences > 0)
      text.AppendLine($"//   {_outsideReferences} foreign key(s) point outside the selection and are written as plain "
        + "columns; an upgrade run over this model would drop them.");
    if (_options.AddDbObjectsNames)
      text.AppendLine("//   Every table, column, key and foreign key is named in an attribute: rename any identifier "
        + "freely, the mapping stands.");
    text.AppendLine("// </auto-generated>");
    text.AppendLine("#nullable enable");
    text.AppendLine("using System;");
    text.AppendLine("using System.Collections.Generic;");
    if (_usesGeoPoint)
      text.AppendLine("using Mic.Common;");
    text.AppendLine("using Mic.Orm;");
    text.AppendLine("using Mic.Orm.Db;");
    text.AppendLine();
    text.AppendLine($"namespace {_options.Namespace};");

    foreach (var schema in schemas) {
      var title = $"// ----- {(schema.Length == 0 ? "the database" : "schema " + schema)} ";
      text.AppendLine().AppendLine(title + new string('-', Math.Max(0, 110 - title.Length)));
      foreach (var entity in _entities.Where(e => e.Schema.Name == schema))
        WriteEntity(text, entity);
      WriteModule(text, schema, schemas.Count > 1);
    }
    WriteApp(text, schemas);
    WriteViewDefinitions(text);
    return text.ToString();
  }

  private void WriteEntity(StringBuilder text, EntitySpec entity) {
    text.AppendLine();
    foreach (var skipped in entity.SkippedColumns)
      text.AppendLine($"// {skipped}");
    foreach (var line in entity.CheckComments)
      text.AppendLine($"// {line}");
    var named = _options.AddDbObjectsNames || entity.TableName != entity.BaseName;
    var head = entity.IsView
      ? "Entity, " + GetViewAttribute(entity, named)
      : named ? $"Entity(TableName = {Quote(entity.TableName)})" : "Entity";
    text.AppendLine($"[{string.Join(", ", new[] { head }.Concat(entity.EntityAttributes))}]");
    text.AppendLine($"public interface {entity.Name} {{");
    var written = 0;
    foreach (var member in entity.Members.Where(m => !m.IsList)) {
      if (written++ > 0)
        text.AppendLine();
      if (member.Attributes.Count > 0)
        text.AppendLine($"  [{string.Join(", ", member.Attributes)}]");
      var comment = member.Comment == null ? "" : $"   // {member.Comment}";
      text.AppendLine($"  {member.TypeName} {member.Name} {{ get; set; }}{comment}");
    }

    // The lists are offered rather than declared: a tenant entity would get one per table, and a large, ever-growing
    // set of dependents wants IQueryable<T>, which loads nothing until asked.
    var lists = entity.Members.Where(m => m.IsList).ToList();
    if (lists.Count > 0) {
      if (written > 0)
        text.AppendLine();
      text.AppendLine("  // Lists: uncomment, delete, or replace with IQueryable<T> for a large, ever-growing set of "
        + "dependent entities.");
      foreach (var list in lists) {
        var attributes = list.Attributes.Count == 0 ? "" : $"[{string.Join(", ", list.Attributes)}] ";
        text.AppendLine($"  // {attributes}{list.TypeName} {list.Name} {{ get; }}");
      }
    }
    text.AppendLine("}");
  }

  /// <summary>Gets the <c>View</c> attribute of a view entity: its name where it is pinned or differs from the
  /// entity's, and whether it is materialized.</summary>
  private static string GetViewAttribute(EntitySpec entity, bool named) {
    var arguments = new List<string>();
    if (named)
      arguments.Add(Quote(entity.TableName));
    if (entity.View!.IsMaterialized)
      arguments.Add("Materialized = true");
    return arguments.Count == 0 ? "View" : $"View({string.Join(", ", arguments)})";
  }

  /// <summary>Writes a schema's module: the entities registered, and each view whose select the server gave
  /// registered with it, a constant of the class at the end of the file, so that the application owns the view; a
  /// view without one is left as it is declared, existing.</summary>
  private void WriteModule(StringBuilder text, string schema, bool severalSchemas) {
    var moduleName = GetModuleName(schema, severalSchemas);
    var entities = _entities.Where(e => e.Schema.Name == schema).ToList();
    var views = entities.Where(e => e.IsView).ToList();
    var serverKind = $"DbServerKind.{_settings.Provider.ServerKind}";
    text.AppendLine();
    text.AppendLine($"public class {moduleName} : EntityModule {{");
    text.AppendLine($"  public {moduleName}() : base({Quote(moduleName)}, new Version(1, 0)) {{");
    var tables = entities.Where(e => !e.IsView).Select(e => $"typeof({e.Name})").ToList();
    if (tables.Count > 0)
      text.AppendLine($"    RegisterEntities({WrapList(tables, "      ", firstLineUsed: 21)});");
    foreach (var view in views)
      text.AppendLine(view.ViewSelect == null
        ? $"    RegisterEntities(typeof({view.Name}));   // the server gave no definition; the view is read as existing"
        : $"    RegisterView<{view.Name}>({serverKind}, {ViewDefinitionsClass}.{GetViewConstantName(view)});");
    text.AppendLine("  }");
    text.AppendLine("}");
  }

  const string ViewDefinitionsClass = "ViewDefinitions";

  private static string GetViewConstantName(EntitySpec view) => view.Name[1..] + "Sql";

  /// <summary>Writes the class holding the views' defining selects, at the end of the file, where their length
  /// is out of the way of the declarations.</summary>
  private void WriteViewDefinitions(StringBuilder text) {
    var views = _entities.Where(e => e.ViewSelect != null).ToList();
    if (views.Count == 0)
      return;
    text.AppendLine();
    text.AppendLine($"/// <summary>The views' defining selects, as the {_settings.Provider.ServerKind} server reported them.</summary>");
    text.AppendLine($"static class {ViewDefinitionsClass} {{");
    for (var i = 0; i < views.Count; i++) {
      if (i > 0)
        text.AppendLine();
      WriteRawString(text, $"  public const string {GetViewConstantName(views[i])} = ", views[i].ViewSelect!);
    }
    text.AppendLine("}");
  }

  /// <summary>Writes a value as a multi-line raw string literal, the SQL's quotes left as they are: a delimiter one
  /// quote longer than the longest run of quotes in the value, the lines indented past the closing delimiter.
  /// </summary>
  private static void WriteRawString(StringBuilder text, string lead, string value) {
    var longestRun = Regex.Matches(value, "\"+").Select(m => m.Length).DefaultIfEmpty(0).Max();
    var delimiter = new string('"', Math.Max(3, longestRun + 1));
    text.AppendLine(lead + delimiter);
    foreach (var line in value.Split('\n'))
      text.AppendLine("    " + line.TrimEnd('\r'));
    text.AppendLine("    " + delimiter + ";");
  }

  /// <summary>Joins the items with commas, breaking the line where the next item would pass the width.</summary>
  private static string WrapList(IReadOnlyList<string> items, string indent, int firstLineUsed, int width = 110) {
    var text = new StringBuilder();
    var lineLength = firstLineUsed;
    for (var i = 0; i < items.Count; i++) {
      var item = items[i];
      if (i > 0) {
        text.Append(',');
        lineLength++;
        if (lineLength + 1 + item.Length + 2 > width) {
          text.AppendLine().Append(indent);
          lineLength = indent.Length;
        } else {
          text.Append(' ');
          lineLength++;
        }
      }
      text.Append(item);
      lineLength += item.Length;
    }
    return text.ToString();
  }

  private void WriteApp(StringBuilder text, List<string> schemas) {
    var defaultSchema = schemas.FirstOrDefault(s => s.Length > 0);
    var settings = defaultSchema == null ? "" : $", new EntityAppSettings {{ DefaultSchema = {Quote(defaultSchema)} }}";
    text.AppendLine();
    text.AppendLine($"public class {_options.AppClassName} : EntityApp {{");
    var appName = _options.AppClassName;
    text.AppendLine($"  public {appName}() : base({Quote(appName)}, new Version(1, 0){settings}) {{");
    foreach (var schema in schemas) {
      var schemaArgument = schema.Length == 0 ? "" : ", " + Quote(schema);
      text.AppendLine($"    AddModule(new {GetModuleName(schema, schemas.Count > 1)}(){schemaArgument});");
    }
    text.AppendLine("  }");
    text.AppendLine("}");
  }

  private string GetModuleName(string schema, bool severalSchemas) {
    var schemaPart = schema.Length == 0 ? "Main" : ToPascalCase(schema);
    return severalSchemas ? schemaPart + (_options.ModuleName ?? "Module") : _options.ModuleName ?? schemaPart + "Module";
  }

  private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

  // ----- the pieces ---------------------------------------------------------------------------------------------------

  /// <summary>An entity to write: a table or a view, its members as they build up, and the names taken in it.</summary>
  sealed class EntitySpec {
    public string Name { get; }
    public string BaseName { get; }
    public string TableName { get; }
    public DbSchemaInfo Schema { get; }
    public IReadOnlyList<DbColumnInfo> Columns { get; }
    public DbTableInfo? Table { get; init; }
    public IReadOnlyList<DbKeyInfo> Keys { get; init; } = [];
    public IReadOnlyList<DbRefConstraintInfo> RefConstraints { get; init; } = [];
    public DbViewInfo? View { get; init; }
    public bool IsView => View != null;

    /// <summary>The unique key written as the primary key of a table that declares none; <see langword="null"/>
    /// where the table has its own.</summary>
    public DbKeyInfo? StandInKey { get; init; }

    /// <summary>The view's defining select as the server reported it, cut to the select; <see langword="null"/>
    /// for a table or where the server gave none.</summary>
    public string? ViewSelect { get; init; }
    public List<MemberSpec> Members { get; } = [];
    public List<ReferenceSpec> References { get; } = [];
    public List<string> EntityAttributes { get; } = [];
    public List<string> SkippedColumns { get; } = [];

    /// <summary>The lines written above the interface for the table's check constraints.</summary>
    public List<string> CheckComments { get; } = [];

    /// <summary>The names of the primary key's members, which are written first.</summary>
    public List<string> KeyMemberNames { get; } = [];
    public Dictionary<DbColumnInfo, string> OutsideReferences { get; } = [];

    readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);

    public EntitySpec(string tableName, DbSchemaInfo schema, IReadOnlyList<DbColumnInfo> columns,
        HashSet<string> usedEntityNames, int ordinal) {
      TableName = tableName;
      Schema = schema;
      Columns = columns;
      BaseName = ToPascalCase(tableName);
      var name = "I" + BaseName;
      if (!usedEntityNames.Add(name)) {
        name = $"I{BaseName}_{ToPascalCase(schema.Name)}";
        if (!usedEntityNames.Add(name))
          name = $"I{BaseName}{ordinal}";
      }
      Name = name;
    }

    /// <summary>Takes a member name not yet used in this entity: the name itself, else with the suffix, else numbered.
    /// </summary>
    public string TakeName(string baseName, string? suffix = null) {
      if (_usedNames.Add(baseName))
        return baseName;
      if (suffix != null && _usedNames.Add(baseName + suffix))
        return baseName + suffix;
      for (var i = 1; ; i++)
        if (_usedNames.Add(baseName + i))
          return baseName + i;
    }
  }

  sealed class MemberSpec(string name, string typeName) {
    public string Name { get; } = name;
    public string TypeName { get; set; } = typeName;
    public List<string> Attributes { get; } = [];
    public bool GetterOnly { get; init; }
    public bool IsList { get; init; }

    /// <summary>The CLR type of a column member, as written; <see langword="null"/> for a reference or a list.</summary>
    public Type? ClrType { get; init; }
    public string? Comment { get; set; }
  }

  sealed record ReferenceSpec(DbRefConstraintInfo Constraint, EntitySpec Entity, EntitySpec Target, string Name);
}
