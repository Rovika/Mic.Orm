namespace Mic.Orm.Db;

/// <summary>Represents one individually executable DDL statement: its kind, its text and what it acts on.</summary>
/// <remarks>Generation produces a list of statements rather than a script, so that the upgrade can interleave
/// migrations between them.</remarks>
public sealed class DdlStatement {
  public DdlStatement(DdlKind kind, string text, string? objectName = null) {
    Kind = kind;
    Text = text;
    ObjectName = objectName;
  }

  public DdlKind Kind { get; }
  public string Text { get; }

  /// <summary>Gets the name of the object the statement acts on, for reporting and for the upgrade's bookkeeping.
  /// </summary>
  public string? ObjectName { get; }

  public override string ToString() => Text;
}
