using Mic.Orm.Logging;

namespace Mic.Orm.Model;

/// <summary>Defines the core behavior of a report on what a model build found and what it cost, and provides a base
/// for derived classes.</summary>
/// <remarks>The report is kept on the model it describes and is readable after a failed build.</remarks>
public abstract class ModelBuildReport {
  readonly List<MessageLogEntry> _messages = [];

  /// <summary>Gets the builder's messages, in the order they were issued.</summary>
  public IReadOnlyList<MessageLogEntry> Messages => _messages;

  /// <summary>Gets how long the build took.</summary>
  public TimeSpan Elapsed { get; internal set; }

  public int ErrorCount => _messages.Count(m => m.Severity == Severity.Error);
  public int WarningCount => _messages.Count(m => m.Severity == Severity.Warning);

  public bool HasErrors => _messages.Any(m => m.Severity == Severity.Error);
  public bool HasWarnings => _messages.Any(m => m.Severity == Severity.Warning);

  internal void Add(IReadOnlyList<MessageLogEntry> messages) => _messages.AddRange(messages);

  /// <summary>When overridden in a derived class, gets the counts the report's first line leads with.</summary>
  protected abstract string Stats { get; }

  /// <summary>Formats the whole report for a human: the summary line, then every message.</summary>
  /// <returns>The formatted report.</returns>
  public string Format() {
    var lines = new List<string> { ToString() };
    lines.AddRange(_messages.Select(m => m.ToString().TrimEnd()));
    return string.Join(Environment.NewLine, lines);
  }

  public override string ToString() =>
    $"{Stats}, {Elapsed.TotalMilliseconds:F0} ms"
    + (ErrorCount > 0 ? $", {ErrorCount} error(s)" : "")
    + (WarningCount > 0 ? $", {WarningCount} warning(s)" : "");
}

/// <summary>Represents what building the entity model found, one entry per fault in the declarations.</summary>
public sealed class EntityModelBuildReport : ModelBuildReport {
  public int EntityCount { get; internal set; }

  protected override string Stats => $"entity model: {EntityCount} entities";
}

/// <summary>Represents what mapping the entity model to a database found.</summary>
public sealed class DbModelBuildReport : ModelBuildReport {
  public int TableCount { get; internal set; }

  protected override string Stats => $"db model: {TableCount} tables";
}
