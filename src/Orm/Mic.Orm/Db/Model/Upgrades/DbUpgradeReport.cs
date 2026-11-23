using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Logging;


namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Represents the outcome of an upgrade run against a database: its messages, the differences found, the
/// script produced and whether it was applied.</summary>
public sealed class DbUpgradeReport {
  readonly List<MessageLogEntry> _messages = new();

  /// <summary>Gets the upgrade's messages, as log entries.</summary>
  /// <remarks>Each message is kept here and also posted to the operation's log.</remarks>
  public IReadOnlyList<MessageLogEntry> Messages => _messages;

  /// <summary>Gets or sets the differences found, whether or not they were applied.</summary>
  public DbModelDiffInfo? Changes { get; set; }

  /// <summary>Gets or sets the script that was produced, present even when nothing ran.</summary>
  public DbUpgradeScript? Script { get; set; }

  /// <summary>Gets or sets a value that indicates whether the script was executed.</summary>
  public bool IsApplied { get; set; }

  /// <summary>Gets or sets the kind of instance the database declares itself to be, or <see langword="null"/> where
  /// it declares none.</summary>
  public DbInstanceType? InstanceType { get; set; }

  /// <summary>Gets or sets the version recorded in the database before this run, or <see langword="null"/> when MIC
  /// had never managed it.</summary>
  public Version? PreviousVersion { get; set; }

  /// <summary>Gets or sets the versions recorded in the database before this run, by module name.</summary>
  public IReadOnlyDictionary<string, string> PreviousModuleVersions { get; set; } =
    new Dictionary<string, string>();

  public bool HasErrors => _messages.Any(m => m.Severity == Severity.Error);
  public bool HasWarnings => _messages.Any(m => m.Severity == Severity.Warning);

  /// <summary>Gets or sets the operation this upgrade belongs to, which supplies the clock and receives the
  /// messages.</summary>
  /// <remarks>The value is <see langword="null"/> when a report is built outside an application; messages are then
  /// stamped with the machine's clock and posted nowhere.</remarks>
  internal OperationContext? Context { get; set; }

  /// <summary>Adds a message, optionally about a particular change.</summary>
  /// <param name="severity">One of the enumeration values that specifies the severity.</param>
  /// <param name="text">The message.</param>
  /// <param name="change">The change the message is about, or <see langword="null"/>.</param>
  /// <remarks>The message is posted to the operation's log as well. A change is recorded as its qualified path, such
  /// as <c>sales.Person.Score</c>.</remarks>
  public void Add(Severity severity, string text, DbObjectDiffInfo? change = null) {
    var entry = new MessageLogEntry(default, Context?.Now ?? DateTime.UtcNow, text, severity,
      change == null ? null : DbModelHelper.GetPath(change.Target ?? change.Source));
    _messages.Add(entry);
    Context?.Log(entry);
  }

  public string Report() => string.Join(Environment.NewLine, _messages.Select(m => m.ToString()));

  /// <summary>Writes the messages, and the script when there is one, to a file.</summary>
  /// <param name="path">The path of the file to write.</param>
  public void SaveTo(string path) {
    var text = new System.Text.StringBuilder();
    text.AppendLine(Report());
    if (Script is { IsEmpty: false }) {
      text.AppendLine();
      text.AppendLine(Script.ToText());
    }
    File.WriteAllText(path, text.ToString());
  }

  public override string ToString() =>
    $"{_messages.Count} messages, {Changes?.Items.Count ?? 0} changes" + (IsApplied ? ", applied" : ", not applied");
}
