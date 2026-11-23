using Mic.Orm.Logging;


namespace Mic.Orm.Model.Construction;

/// <summary>Holds the state every pass of the model builder shares: the application being read, the model being
/// produced, the current attribute phase and the diagnostics.</summary>
/// <remarks>Diagnostics are collected, never thrown, so one build reports every fault. Two builds can run at once
/// without sharing anything.</remarks>
public class BuilderContext {
  readonly List<MessageLogEntry> _diagnostics = new();

  public BuilderContext(EntityApp app, EntityAppModel model) {
    App = app;
    Model = model;
  }

  public EntityApp App { get; }
  public EntityAppModel Model { get; }

  /// <summary>Gets the entities by their declaring interface, which is how a member's type is recognised as a
  /// reference and how a type is known to be an entity.</summary>
  public Dictionary<Type, EntityTypeInfo> EntitiesByType { get; } = new();

  /// <summary>Gets the db model, once mapped, or <see langword="null"/> when no provider was given or the entity model
  /// had errors.</summary>
  public Db.Model.DbModel? DbModel { get; internal set; }

  /// <summary>Gets which of the runs over the attributes is in progress; an attribute reads it to decide whether it
  /// is its turn.</summary>
  public AttributePhase Phase { get; internal set; }

  public IReadOnlyList<MessageLogEntry> Diagnostics => _diagnostics;
  public bool HasErrors => _diagnostics.Any(d => d.Severity == Severity.Error);

  public void AddError(string message, string? location = null) => Add(Severity.Error, message, location);

  public void AddWarning(string message, string? location = null) => Add(Severity.Warning, message, location);

  // The source is empty: a model build belongs to no session, request or correlation.
  private void Add(Severity severity, string message, string? location) =>
    _diagnostics.Add(new MessageLogEntry(default, App.TimeService.UtcNow, message, severity, location));

  /// <summary>Takes the errors and warnings of a member-list parse into the diagnostics.</summary>
  /// <param name="result">The parse result.</param>
  /// <param name="location">The place the diagnostics are reported at, or <see langword="null"/> for none.</param>
  public void Absorb(MemberListResult result, string? location = null) {
    foreach (var error in result.Errors)
      AddError(error, location);
    foreach (var warning in result.Warnings)
      AddWarning(warning, location);
  }
}
