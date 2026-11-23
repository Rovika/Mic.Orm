
namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Represents one difference between two db models: the kind of object, the kind of change, the two objects
/// compared and a readable description.</summary>
/// <remarks>The source and target objects are carried by reference; a caller that needs to know what differs has
/// both in hand.</remarks>
public sealed class DbObjectDiffInfo {
  public DbObjectDiffInfo(DbObjectKind objectKind, DbDiffKind diffKind, DbModelObject? source, DbModelObject? target,
      string? description = null) {
    ObjectKind = objectKind;
    DiffKind = diffKind;
    Source = source;
    Target = target;
    Description = description;
  }

  public DbObjectKind ObjectKind { get; }
  public DbDiffKind DiffKind { get; }

  /// <summary>Gets the object the comparison runs from, which in an upgrade is the database as it is now. The value
  /// is <see langword="null"/> for an addition.</summary>
  /// <remarks>A diff describes turning the source into the target; swapping the two turns every addition into a
  /// drop.</remarks>
  public DbModelObject? Source { get; }

  /// <summary>Gets the object the comparison runs to, which in an upgrade is what the model wants. The value is
  /// <see langword="null"/> for a drop.</summary>
  public DbModelObject? Target { get; }

  /// <summary>Gets the readable description a report shows, such as "size 50 → 80" or "now nullable".</summary>
  public string? Description { get; }

  public string Name => (Target ?? Source)?.Name ?? "?";

  /// <summary>Gets a value that indicates whether applying this difference loses data.</summary>
  /// <remarks>Only a dropped table, column or schema is destructive. A dropped index or constraint is not, since the
  /// model can put it back from its own description.</remarks>
  public bool IsDestructive =>
    DiffKind == DbDiffKind.Drop
    && ObjectKind is DbObjectKind.Table or DbObjectKind.Column or DbObjectKind.Schema;

  public override string ToString() =>
    $"{DiffKind} {ObjectKind} '{Name}'" + (Description == null ? "" : $": {Description}");
}

/// <summary>Represents the result of comparing two db models.</summary>
public sealed class DbModelDiffInfo {
  readonly List<DbObjectDiffInfo> _items = new();
  readonly List<string> _errors = new();

  public IReadOnlyList<DbObjectDiffInfo> Items => _items;

  /// <summary>Gets the conflicts the comparison could not resolve, such as two objects claiming one old name.</summary>
  public IReadOnlyList<string> Errors => _errors;

  public bool HasItems => _items.Count > 0;
  public bool HasErrors => _errors.Count > 0;

  /// <summary>Gets the differences that lose data when applied.</summary>
  public IEnumerable<DbObjectDiffInfo> Destructive => _items.Where(d => d.IsDestructive);

  public void Add(DbObjectDiffInfo item) => _items.Add(item);
  public void AddError(string message) => _errors.Add(message);

  public IEnumerable<DbObjectDiffInfo> OfKind(DbObjectKind kind) => _items.Where(d => d.ObjectKind == kind);

  public string Report() => string.Join(Environment.NewLine, _items.Select(d => d.ToString()));
}
