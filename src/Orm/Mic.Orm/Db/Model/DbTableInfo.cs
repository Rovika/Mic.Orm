using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm.Model;
namespace Mic.Orm.Db.Model;

/// <summary>Represents a column, with its type carried verbatim as the database declares it.</summary>
public class DbColumnInfo : DbModelObject, ILinqColumn {
  public DbTypeRef TypeRef { get; }
  public bool IsNullable { get; set; }

  /// <summary>Gets the table this column belongs to, set when the column is added to one.</summary>
  public DbTableInfo? Table { get; internal set; }

  public DbColumnFlags DbFlags { get; set; }

  /// <summary>Gets or sets the sequence the column's value is drawn from on insert, or <see langword="null"/> for
  /// any other column.</summary>
  public DbSequenceInfo? Sequence { get; set; }

  /// <summary>Gets the type as the database declares it.</summary>
  public string TypeSpec => ((DbTypeRef)TypeRef).TypeSpec;

  public int Size { get; set; }
  public byte Precision { get; set; }
  public byte Scale { get; set; }

  /// <summary>Gets or sets the default expression, as SQL text.</summary>
  public string? DefaultExpression { get; set; }

  /// <summary>Gets or sets the comment the database records for this column, where the server keeps one.</summary>
  public string? Comment { get; set; }

  /// <summary>Gets or sets the name of the default, on a server where a default is a named constraint rather than a
  /// column property.</summary>
  /// <remarks>The value is set only by introspection and is never compared.</remarks>
  public string? DefaultConstraintName { get; set; }

  /// <summary>Gets or sets the entity member this column came from, or <see langword="null"/> for an introspected
  /// model.</summary>
  public EntityMemberInfo? Member { get; set; }

  public bool IsIdentity => DbFlags.HasFlag(DbColumnFlags.Identity);

  /// <summary>Gets a value that indicates whether the server computes this column from an expression over the row.
  /// </summary>
  public bool IsComputed => DbFlags.HasFlag(DbColumnFlags.Computed);

  /// <summary>Gets a value that indicates whether the server stores a computed column's value rather than computes
  /// it on each read.</summary>
  public bool IsComputedStored => DbFlags.HasFlag(DbColumnFlags.Stored);

  /// <summary>Gets or sets the expression a computed column is computed from, as this server's SQL over the row's
  /// other columns; or <see langword="null"/> for any other column, and for a computed column read from a server.
  /// </summary>
  public string? ComputedExpression { get; set; }

  /// <summary>Gets or sets the hash of a computed column's expression that MIC wrote beside the column, as read from
  /// a server; or <see langword="null"/> where there is none.</summary>
  /// <remarks>A server rewrites the expression it reports, so an upgrade compares this hash with the hash of the
  /// expression the model declares.</remarks>
  public string? StoredDefinitionHash { get; set; }

  /// <summary>Gets a value that indicates whether this column holds a geographic point, read and written as
  /// well-known binary through the dialect's spelling.</summary>
  public bool IsGeoPoint => Member?.DataType == typeof(GeoPoint) || TypeRef.ClrType == typeof(GeoPoint);

  /// <summary>Gets a value that indicates whether this column holds a vector, read and written through the
  /// dialect's spelling.</summary>
  public bool IsVector => Member?.IsVector == true;

  /// <summary>Gets this column's name as a statement writes it, quoted.</summary>
  /// <remarks>The value is filled by <see cref="Construction.DbModelBuilderHelper.CompleteSetup"/>.</remarks>
  public string QuotedName { get; internal set; } = "";

  // The query engine's view of a column.
  Type? ILinqColumn.DataType => Member?.DataType ?? TypeRef.ClrType;
  ILinqTable ILinqColumn.Table => Table ?? throw new InvalidOperationException($"Column '{Name}' belongs to no table.");
  ILinqEntityMember? ILinqColumn.Member => Member;

  public DbColumnInfo(string name, DbTypeRef typeRef, bool isNullable = false) : base(name) {
    TypeRef = typeRef;
    IsNullable = isNullable;
  }
}

/// <summary>Represents a table, or a view read as one.</summary>
public class DbTableInfo : DbModelObject, ILinqTable {
  public DbSchemaInfo Schema { get; }

  /// <summary>Gets or sets the view this table is, for an entity that reads through a view: how it is defined and
  /// whether MIC manages it; or <see langword="null"/> for a table.</summary>
  /// <remarks>A view has columns like a table and no keys or constraints the database enforces.</remarks>
  public DbViewInfo? View { get; set; }

  /// <summary>Gets a value that indicates whether this table is a view.</summary>
  public bool IsView => View != null;

  /// <summary>Gets or sets the function this table is, for an entity whose rows a function of the server returns:
  /// its parameters, and the body where MIC defines it; or <see langword="null"/> for a table.</summary>
  /// <remarks>The table's name is the function's; it has columns like a table and no keys or constraints.</remarks>
  public DbTableFunctionInfo? TableFunction { get; set; }

  /// <summary>Gets a value that indicates whether this table is a function's rows.</summary>
  public bool IsTableFunction => TableFunction != null;

  /// <summary>Gets or sets a value that indicates whether this is a temp table: created by a session on its
  /// connection, named as the server names one, referred to unqualified, and never part of an upgrade.</summary>
  public bool IsTemporary { get; set; }

  /// <summary>Gets the conditions the entity's extensions attach to every read of the table, one per extension, each
  /// a template prepared once over a placeholder row when the db model is built; empty if the entity has no filter.
  /// </summary>
  public IReadOnlyList<LinqTableFilter> Filters { get; internal set; } = [];
  public IReadOnlyList<DbColumnInfo> Columns => _columns;
  public IReadOnlyList<DbKeyInfo> Keys => _keys;
  public IReadOnlyList<DbRefConstraintInfo> RefConstraints => _refConstraints;
  public DbKeyInfo? PrimaryKey => _keys.FirstOrDefault(k => k.IsPrimaryKey);

  /// <summary>Gets the triggers on this table, empty unless <see cref="DbModel.LoadedParts"/> includes them.</summary>
  public IReadOnlyList<DbTriggerInfo> Triggers => _triggers;

  /// <summary>Gets the check constraints over this table's values, empty unless <see cref="DbModel.LoadedParts"/>
  /// includes them.</summary>
  public IReadOnlyList<DbCheckConstraintInfo> CheckConstraints => _checkConstraints;

  /// <summary>Gets or sets the comment the database records for this table, where the server keeps one.</summary>
  public string? Comment { get; set; }

  /// <summary>Gets or sets the entity this table came from, or <see langword="null"/> for an introspected model.
  /// </summary>
  public EntityTypeInfo? Entity { get; set; }

  /// <summary>Gets this table's name, quoted and not qualified.</summary>
  public string QuotedName { get; internal set; } = "";

  /// <summary>Gets the name a statement refers to this table by, schema-qualified where the server has schemas.
  /// </summary>
  /// <remarks>The value is filled by <see cref="Construction.DbModelBuilderHelper.CompleteSetup"/>, as is
  /// <see cref="QuotedName"/>.</remarks>
  public string RefName { get; internal set; } = "";

  /// <summary>Gets the columns an insert of one row writes, in the entity's member order, with the db-generated
  /// columns left out.</summary>
  public IReadOnlyList<DbColumnInfo> InsertColumns { get; internal set; } = [];

  /// <summary>Gets the quoted names of <see cref="InsertColumns"/> joined as the column list of an insert.</summary>
  public string InsertColumnList { get; internal set; } = "";

  // The query engine's view of a table.
  string? ILinqTable.Schema => Schema.Name;
  IReadOnlyList<ILinqColumn> ILinqTable.Columns => _columns;
  ILinqEntity? ILinqTable.Entity => Entity;

  readonly List<DbColumnInfo> _columns = new();
  readonly List<DbKeyInfo> _keys = new();
  readonly List<DbRefConstraintInfo> _refConstraints = new();
  readonly List<DbTriggerInfo> _triggers = new();
  readonly List<DbCheckConstraintInfo> _checkConstraints = new();
  DbColumnInfo?[]? _columnsByMemberIndex;

  public DbTableInfo(string name, DbSchemaInfo schema) : base(name) {
    Schema = schema;
  }

  /// <summary>Gets the column a member of the entity this table maps is stored in.</summary>
  /// <param name="member">The entity member.</param>
  /// <returns>The column, or <see langword="null"/> if the member has none.</returns>
  public DbColumnInfo? GetColumn(EntityMemberInfo member) {
    var byIndex = _columnsByMemberIndex ??= BuildColumnIndex();
    var index = member.Index;
    return index >= 0 && index < byIndex.Length ? byIndex[index] : null;
  }

  public DbColumnInfo? FindColumn(string name) =>
    _columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

  public DbColumnInfo GetColumn(string name) =>
    FindColumn(name) ?? throw new KeyNotFoundException($"Column '{name}' not found on table '{Name}'.");

  public void AddColumn(DbColumnInfo column) {
    ArgumentNullException.ThrowIfNull(column);
    if (column.Table != null)
      throw new InvalidOperationException($"Column '{column.Name}' already belongs to table '{column.Table.Name}'.");
    _columns.Add(column);
    column.Table = this;
  }

  public void AddKey(DbKeyInfo key) {
    if (!ReferenceEquals(key.Table, this))
      throw new InvalidOperationException($"Key '{key.Name}' was created for table '{key.Table.Name}', not '{Name}'.");
    _keys.Add(key);
  }

  public void AddRefConstraint(DbRefConstraintInfo constraint) {
    if (!ReferenceEquals(constraint.FromKey.Table, this))
      throw new InvalidOperationException(
        $"Constraint '{constraint.Name}' does not start from table '{Name}'.");
    _refConstraints.Add(constraint);
  }

  public void AddTrigger(DbTriggerInfo trigger) =>
    _triggers.Add(trigger ?? throw new ArgumentNullException(nameof(trigger)));

  public void AddCheckConstraint(DbCheckConstraintInfo constraint) =>
    _checkConstraints.Add(constraint ?? throw new ArgumentNullException(nameof(constraint)));

  private DbColumnInfo?[] BuildColumnIndex() {
    var size = Entity?.Members.Count ?? 0;
    var byIndex = new DbColumnInfo?[size];
    foreach (var column in _columns)
      if (column.Member is { } member && member.Index >= 0 && member.Index < size)
        byIndex[member.Index] = column;
    return byIndex;
  }
}
