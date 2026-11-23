using System.Text;

namespace Mic.Orm.Logging;

/// <summary>Represents where a log entry came from: the ids a combined log groups and labels its entries by.</summary>
/// <remarks>A session computes its own once when it opens, and every entry it posts carries that copy; an entry
/// posted straight to the context carries the same with a <see langword="null"/> <see cref="SessionId"/>.</remarks>
public readonly record struct LogSource(Guid? SessionId, string? CorrelationId, string? WebRequestId);

/// <summary>Defines the core behavior of a log entry, one thing worth recording and when it happened, and provides a
/// base for derived classes.</summary>
/// <remarks>The entry's type is its category. An entry holds what it was given and renders on demand, through
/// <see cref="WriteText"/>, on the background thread that writes the batch. A derived class overrides
/// <see cref="WriteText"/>.</remarks>
public abstract class LogEntry {
  protected LogEntry(LogSource source, DateTime time, Severity severity = Severity.Info, string? location = null) {
    Source = source;
    Time = time;
    Severity = severity;
    Location = location;
  }

  public LogSource Source { get; }

  /// <summary>Gets how much the entry matters.</summary>
  public Severity Severity { get; }

  /// <summary>Gets what the entry is about, when that is knowable: an entity and member for a model diagnostic, a
  /// qualified object path for an upgrade message.</summary>
  /// <value>The location, or <see langword="null"/> for an entry about nothing but itself, such as a statement.</value>
  public string? Location { get; }

  /// <summary>Gets when the entry happened, from the application's clock.</summary>
  /// <remarks>Per entry, not the session's per-save instant, so a log can tell two statements apart.</remarks>
  public DateTime Time { get; }

  /// <summary>Gets a value that indicates whether the entry is an error, which an error-only log waits for.</summary>
  public bool IsError => Severity == Severity.Error;

  /// <summary>When overridden in a derived class, renders the entry into the batch's buffer.</summary>
  /// <param name="text">The buffer the batch is written into.</param>
  /// <remarks>Called on a background thread, at flush.</remarks>
  public abstract void WriteText(StringBuilder text);


  public override string ToString() {
    var text = new StringBuilder();
    WriteText(text);
    return text.ToString();
  }
}

/// <summary>Represents a message an application or a MIC layer wanted said, at any severity.</summary>
/// <remarks>An error that carries an exception is an <see cref="ErrorLogEntry"/>.</remarks>
public class MessageLogEntry(LogSource source, DateTime time, string message,
      Severity severity = Severity.Info, string? location = null)
    : LogEntry(source, time, severity, location) {
  public string Message { get; } = message;

  public override void WriteText(StringBuilder text) {
    text.Append(Time.ToString("HH:mm:ss.fff")).Append("  ");
    if (Severity != Severity.Info)
      text.Append(Severity.ToString().ToUpperInvariant()).Append(": ");
    if (Location != null)
      text.Append(Location).Append(": ");
    text.AppendLine(Message);
  }
}

/// <summary>Represents an error that brought an exception with it, rendered whole with its stack.</summary>
/// <remarks>An error with nothing but a message is a <see cref="MessageLogEntry"/> at <see cref="Severity.Error"/>.
/// </remarks>
public class ErrorLogEntry(LogSource source, DateTime time, string message, Exception? error,
      string? location = null)
    : LogEntry(source, time, Severity.Error, location) {
  public string Message { get; } = message;
  public Exception? Error { get; } = error;

  public override void WriteText(StringBuilder text) {
    text.Append(Time.ToString("HH:mm:ss.fff")).Append("  ERROR: ");
    if (Location != null)
      text.Append(Location).Append(": ");
    text.AppendLine(Message);
    if (Error != null)
      text.AppendLine(Error.ToString());
  }
}
