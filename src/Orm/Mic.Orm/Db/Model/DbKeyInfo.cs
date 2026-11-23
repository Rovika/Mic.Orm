using Mic.Orm.Model;

namespace Mic.Orm.Db.Model;

/// <summary>Represents one column of a key, with its sort direction.</summary>
public readonly struct DbKeyColumn {
  public DbKeyColumn(DbColumnInfo column, bool descending = false) {
    Column = column;
    Descending = descending;
  }

  public DbColumnInfo Column { get; }
  public bool Descending { get; }

  public override string ToString() => Descending ? "-" + Column.Name : Column.Name;
}

/// <summary>Represents a key or index on a table: a primary key, a unique constraint, an index, or the column set
/// behind a foreign key.</summary>
/// <remarks>Whether a unique key is rendered as a constraint or as a unique index is the vendor's decision.</remarks>
public class DbKeyInfo : DbModelObject {
  readonly List<DbKeyColumn> _columns = new();
  readonly List<DbColumnInfo> _includedColumns = new();

  public DbKeyInfo(string name, DbTableInfo table, KeyFlags flags) : base(name) {
    Table = table;
    Flags = flags;
  }

  public DbTableInfo Table { get; }
  public KeyFlags Flags { get; set; }

  /// <summary>Gets the columns of the key, in order.</summary>
  public IReadOnlyList<DbKeyColumn> Columns => _columns;

  /// <summary>Gets the columns carried in the index leaf that are not part of the key.</summary>
  public IReadOnlyList<DbColumnInfo> IncludedColumns => _includedColumns;

  /// <summary>Gets or sets the filter of a filtered or partial index, as SQL text.</summary>
  /// <remarks>An upgrade compares the filter through a stored hash rather than a text match, since a server rewrites
  /// the text it was given.</remarks>
  public string? Filter { get; set; }

  /// <summary>Gets or sets the access method of the index.</summary>
  /// <remarks>The value is concrete on both sides of a comparison: a model built from the entity model resolves an
  /// unstated method to the provider's default, and introspection reads back what the server reports.</remarks>
  public DbIndexType IndexType { get; set; }

  /// <summary>Gets or sets the metric a vector index finds the nearest vectors by, or <see langword="null"/> for any
  /// other index.</summary>
  /// <remarks>An ordering by another metric is not served by the index, so a change of the metric is a rebuild.
  /// </remarks>
  public VectorMetric? VectorMetric { get; set; }

  /// <summary>Gets or sets the hash MIC stored alongside this index when it created it, as read back by introspection.
  /// </summary>
  /// <value>The stored hash; <see langword="null"/> on a model built from the entity model, where the hash is computed
  /// from <see cref="Filter"/>, and on an index MIC did not create.</value>
  public string? StoredDefinitionHash { get; set; }

  /// <summary>Gets or sets the entity-model key this key came from, or <see langword="null"/> for an introspected
  /// model.</summary>
  public EntityKeyInfo? EntityKey { get; set; }

  public bool IsPrimaryKey => Flags.HasFlag(KeyFlags.PrimaryKey);
  public bool IsForeignKey => Flags.HasFlag(KeyFlags.ForeignKey);
  public bool IsUnique => Flags.HasFlag(KeyFlags.Unique) || IsPrimaryKey;
  public bool IsComposite => _columns.Count > 1;

  public void AddColumn(DbColumnInfo column, bool descending = false) {
    ArgumentNullException.ThrowIfNull(column);
    if (!ReferenceEquals(column.Table, Table))
      throw new InvalidOperationException(
        $"Key '{Name}' on '{Table.Name}': column '{column.Name}' belongs to another table.");
    _columns.Add(new DbKeyColumn(column, descending));
  }

  public void AddIncludedColumn(DbColumnInfo column) => _includedColumns.Add(column);
}

/// <summary>Represents a foreign key constraint: a named pairing of one table's columns with a key on another table.
/// </summary>
/// <remarks>The column set on the referencing side is an ordinary <see cref="DbKeyInfo"/> flagged as a foreign key.
/// </remarks>
public class DbRefConstraintInfo : DbModelObject {
  public DbRefConstraintInfo(string name, DbKeyInfo fromKey, DbKeyInfo toKey) : base(name) {
    FromKey = fromKey;
    ToKey = toKey;
  }

  /// <summary>Gets the key over the referencing table's columns.</summary>
  public DbKeyInfo FromKey { get; }

  /// <summary>Gets the key being referenced, a primary or unique key on the other table.</summary>
  public DbKeyInfo ToKey { get; }

  public DbTableInfo FromTable => FromKey.Table;
  public DbTableInfo ToTable => ToKey.Table;

  /// <summary>Gets or sets a value that indicates whether the constraint deletes referencing rows when the
  /// referenced row is deleted.</summary>
  public bool CascadeDelete { get; set; }

  /// <summary>Gets or sets the entity-model relationship this constraint came from, or <see langword="null"/> for an
  /// introspected model.</summary>
  public ReferenceInfo? Reference { get; set; }
}
