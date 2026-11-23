using System.Text.RegularExpressions;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Mic.Orm.DbInfo;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Brings a database into line with the application's model: reads the database, compares it with the
/// model, and applies the resulting script or only reports it.</summary>
/// <remarks>A database with no MIC metadata table has never been managed by MIC, and the application's upgrade mode
/// decides. A database whose metadata declares its instance type has that declaration respected over the
/// application's settings.</remarks>
public class DbUpgradeManager {
  public DbUpgradeManager(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    Database = database;
  }

  public Database Database { get; }
  public DbSettings Settings => Database.Settings;

  /// <summary>Gets or sets the operation this upgrade is part of, so its statements and messages reach the log.
  /// </summary>
  public OperationContext? Context { get; set; }

  /// <summary>Gets the report of the current or last run, available from the moment the run starts.</summary>
  /// <remarks>A run that throws leaves its report here.</remarks>
  public DbUpgradeReport? Report { get; private set; }

  /// <summary>Gets the model read back from the server by the last comparison.</summary>
  /// <remarks>The value is replaced by a second read after a lock wait, since what was read before the wait is stale.
  /// </remarks>
  public DbModel? OldDbModel { get; private set; }

  /// <summary>Gets the differences the last comparison found between <see cref="OldDbModel"/> and the application's
  /// model, without those the settings say to leave alone.</summary>
  public DbModelDiffInfo? Changes { get; private set; }

  /// <summary>Asynchronously compares the database with the model and applies the differences the settings and the
  /// database's instance type allow.</summary>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the report of the run.
  /// </returns>
  /// <remarks>Under <see cref="DbUpgradeMode.Never"/> the database is not read: the report says so and holds no
  /// differences. An upgrade lock is taken only where there is something to apply.</remarks>
  public virtual async Task<DbUpgradeReport> UpgradeAsync(CancellationToken cancellation = default) {
    var report = Report = new DbUpgradeReport { Context = Context };

    // The administrator keeps the schema: nothing is read, compared or locked.
    if (Settings.UpgradeMode == DbUpgradeMode.Never) {
      report.Add(Severity.Info, "The upgrade mode is Never: the database was not read or compared with the model.");
      Finish(report);
      return report;
    }

    await DiffAsync(cancellation);
    var changes = Changes!;
    if (!changes.HasItems) {
      report.Add(Severity.Info, "The database matches the model.");
      Finish(report);
      return report;
    }

    var script = BuildScript(changes, report);
    // Every difference is one this server cannot apply: there is nothing to take a lock for.
    if (script.IsEmpty) {
      report.Add(Severity.Info, "Nothing to apply.");
      Finish(report);
      return report;
    }
    if (!IsAllowed(report, out var why)) {
      // Each difference is listed, since the application continues and may fail on one later.
      report.Add(Severity.Warning,
        $"The database differs from the model in {changes.Items.Count} way(s) and {why} "
        + "The application continues; these differences remain.");
      foreach (var change in changes.Items)
        report.Add(Severity.Warning, change.ToString(), change);
      Finish(report);
      return report;
    }

    // A run that only reports needs no lock.
    await using var upgradeLock = await AcquireLockAsync(report, cancellation);

    if (upgradeLock != null) {
      // Whoever held the lock may have changed the database, so everything read before the wait is stale.
      await DiffAsync(cancellation);
      changes = Changes!;
      if (!changes.HasItems) {
        // The script built before the wait is not what this run did.
        report.Script = null;
        report.Add(Severity.Info,
          "Another instance brought the database up to date while this one waited; there is nothing left to do.");
        Finish(report);
        return report;
      }
      script = BuildScript(changes, report);
    }

    await ApplyAsync(script, report, cancellation);
    Finish(report);
    return report;
  }

  /// <summary>Asynchronously compares the database with the model and builds the script that would bring it up to
  /// the model, applying nothing and taking no lock.</summary>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the report, with the
  /// differences and the script where there are any.</returns>
  /// <remarks>This is what a tool writes a production script from. The settings' upgrade mode plays no part: the
  /// script is built whatever the database declares itself, and whoever runs it decides.</remarks>
  public virtual async Task<DbUpgradeReport> PlanAsync(CancellationToken cancellation = default) {
    var report = Report = new DbUpgradeReport { Context = Context };
    await DiffAsync(cancellation);
    var changes = Changes!;
    if (!changes.HasItems)
      report.Add(Severity.Info, "The database matches the model.");
    else {
      var script = BuildScript(changes, report);
      report.Add(Severity.Info, $"The database differs from the model in {changes.Items.Count} way(s); the script "
        + $"has {script.Steps.Count} step(s)" + (script.HasDestructive ? ", some of them destructive." : "."));
    }
    Finish(report);
    return report;
  }

  /// <summary>Asynchronously reads the database and its metadata and compares the result with the model, leaving the
  /// results in <see cref="OldDbModel"/>, <see cref="Changes"/> and the database's <c>DbInfo</c>.</summary>
  /// <exception cref="DataAccessException">the database is at a version ahead of the application's and declares
  /// itself production or staging.</exception>
  protected virtual async Task DiffAsync(CancellationToken cancellation) {
    var report = Report!;
    var model = Database.DbModel;

    var schemas = model.Schemas.Select(s => s.Name).Where(n => n.Length > 0).ToList();
    var loader = Database.Provider.CreateDbModelLoader(Settings);
    // The views, the routines and the check constraints are read too, since the model may declare some; nothing
    // else beyond the tables is compared.
    var loaded = OldDbModel = await loader.LoadModelAsync(schemas,
      DbModelParts.Tables | DbModelParts.Views | DbModelParts.Routines | DbModelParts.CheckConstraints, cancellation);

    var current = TakeDbInfo(loader.DbInfo, report);

    // A database ahead of the application is a deployment mistake on production or staging, where rolling the schema
    // back would destroy what a newer binary added. On a development database the model is the truth and the schema
    // follows it, drops included.
    if (current?.Version != null && current.Version > CurrentVersion) {
      var message = $"Database '{Settings.DatabaseAlias}': the database is at version {current.Version}, "
        + $"ahead of this application's {CurrentVersion}.";
      if (current.InstanceType is DbInstanceType.Production or DbInstanceType.Staging) {
        message += $" MIC never rolls back a {current.InstanceType.ToString().ToUpperInvariant()} database.";
        report.Add(Severity.Error, message);
        Finish(report);
        throw new DataAccessException(message) { DatabaseAlias = Settings.DatabaseAlias };
      }
      report.Add(Severity.Warning, message
        + " This is a development database, so the schema is brought back into line with the model.");
    }

    // What the settings say to leave alone is no difference: it is not reported, and nothing waits on it.
    var changes = Changes = Filter(Database.Provider.CreateDbModelComparer().Compare(loaded, model));
    report.Changes = changes;
    foreach (var error in changes.Errors)
      report.Add(Severity.Error, error);
  }

  private DbUpgradeScript BuildScript(DbModelDiffInfo changes, DbUpgradeReport report) {
    var script = new DbUpgradeScriptBuilder(Database.Provider).Build(changes);
    report.Script = script;
    foreach (var warning in script.Warnings)
      report.Add(Severity.Warning, warning);
    return script;
  }

  /// <summary>Asynchronously takes the upgrade lock, or warns that the server has none or that the user may not
  /// take one.</summary>
  /// <returns>A task that represents the asynchronous operation. The task result contains the lock, or
  /// <see langword="null"/> if the server has no session locks or refused the user the lock.</returns>
  /// <exception cref="DataAccessException">another instance holds the lock and does not release it within
  /// <see cref="DbSettings.UpgradeLockWait"/>.</exception>
  protected virtual async Task<DbUpgradeLock?> AcquireLockAsync(DbUpgradeReport report,
      CancellationToken cancellation) {
    if (!Database.Provider.Supports(DbFeatures.SessionLocks)) {
      report.Add(Severity.Warning,
        $"{Database.Provider.ServerKind} has no session locks, so this upgrade is not coordinated with other "
        + "instances. Two starting at once would both try to apply it.");
      return null;
    }

    DbUpgradeLock? held;
    try {
      held = await DbUpgradeLock.TryAcquireAsync(Database, Settings.UpgradeLockWait, cancellation);
    } catch (DataAccessException refused) when (refused.SubType == DataAccessException.SubTypeAccessDenied) {
      report.Add(Severity.Warning, refused.Message + " This upgrade is not coordinated with other instances; two "
        + "starting at once would both try to apply it.");
      return null;
    }
    if (held != null)
      return held;

    var message = $"Database '{Settings.DatabaseAlias}': another instance is upgrading this database and did not "
      + $"finish within {Settings.UpgradeLockWait.TotalSeconds:F0}s. Stopping rather than working against a schema "
      + "that is still changing.";
    report.Add(Severity.Error, message);
    Finish(report);
    throw new DataAccessException(message) { DatabaseAlias = Settings.DatabaseAlias };
  }

  protected Version CurrentVersion => Database.DbModel.EntityModel?.Version ?? new Version(0, 0);

  // ----- the pieces -------------------------------------------------------------------------------------------------

  /// <summary>Decides whether the upgrade may run under the application's mode and the database's instance type.
  /// </summary>
  /// <param name="report">The report of the run.</param>
  /// <param name="why">When this method returns, contains the reason the upgrade may not run, or an empty string.
  /// </param>
  /// <returns><see langword="true"/> if the upgrade may run; otherwise, <see langword="false"/>.</returns>
  protected virtual bool IsAllowed(DbUpgradeReport report, out string why) {
    switch (Settings.UpgradeMode) {
      case DbUpgradeMode.Always:
        why = "";
        return true;
      case DbUpgradeMode.Never:
        why = "this application is configured never to change a database.";
        return false;
      default:
        var isProduction = report.InstanceType == DbInstanceType.Production;
        why = isProduction
          ? "the database declares itself PRODUCTION, which this application will not change."
          : "";
        return !isProduction;
    }
  }

  /// <summary>Removes the differences the settings say to leave alone: the drop and the change of an object that
  /// <see cref="DbSettings.IgnoreDbObjects"/> names, and the drop of a table the model does not know unless
  /// <see cref="DbSettings.DropUnknownTables"/> is set.</summary>
  /// <param name="changes">The differences the comparison found.</param>
  /// <returns>The differences that remain.</returns>
  /// <remarks>Nothing is reported about a difference removed: to the upgrade such an object does not exist.</remarks>
  protected virtual DbModelDiffInfo Filter(DbModelDiffInfo changes) {
    var ignored = Settings.IgnoreDbObjects.Where(p => !string.IsNullOrWhiteSpace(p))
      .Select(p => BuildPathRegex(p.Trim())).ToList();
    if (Settings.DropUnknownTables && ignored.Count == 0)
      return changes;

    var filtered = new DbModelDiffInfo();
    foreach (var change in changes.Items) {
      if (change.DiffKind is DbDiffKind.Drop or DbDiffKind.Modify) {
        var path = DbModelHelper.GetPath(change.Target ?? change.Source);
        if (ignored.Any(pattern => pattern.IsMatch(path)))
          continue;
      }
      if (!Settings.DropUnknownTables && change.ObjectKind == DbObjectKind.Table
          && change.DiffKind == DbDiffKind.Drop)
        continue;
      filtered.Add(change);
    }
    foreach (var error in changes.Errors)
      filtered.AddError(error);
    return filtered;
  }

  /// <summary>Builds the expression a pattern of <see cref="DbSettings.IgnoreDbObjects"/> matches a path by.</summary>
  private static Regex BuildPathRegex(string pattern) =>
    new("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

  protected virtual async Task ApplyAsync(DbUpgradeScript script, DbUpgradeReport report,
      CancellationToken cancellation) {
    if (script.IsEmpty) {
      report.Add(Severity.Info, "Nothing to apply.");
      return;
    }
    try {
      var result = await new DdlExecutor(Database).ExecuteAsync(script, Context, cancellation);
      report.IsApplied = true;
      report.Add(Severity.Info,
        $"Applied {result.StatementCount} statement(s) in {result.TotalMs:F0} ms.");

      // Success is recorded by DbInfoModule on DatabaseEvents.Connected, once the runtime is up.
    } catch (DataAccessException ex) {
      // The schema is now part-changed. The failure is recorded here because Connected never fires for a run that
      // threw, and the entity runtime may be what the part-changed schema broke.
      report.Add(Severity.Error, ex.Message);
      await DbInfoModule.SaveUpgradeErrorAsync(Database, ex.Message, cancellation);
      throw;
    }
  }

  private void Finish(DbUpgradeReport report) {
    if (Settings.UpgradeReportPath != null)
      report.SaveTo(Settings.UpgradeReportPath);
  }

  // ----- the metadata -------------------------------------------------------------------------------------------------
  /// <summary>Stores the metadata record the loader read on the database being upgraded, tells the report what it
  /// holds, and warns when the last run failed part-way or minted its keys under another Guid scheme.</summary>
  /// <param name="loaded">The record the loader read, or <see langword="null"/> where the database has none.</param>
  /// <param name="report">The report of the run.</param>
  /// <returns>The record, or <see langword="null"/> where the database has none.</returns>
  protected virtual DbInfoRecord? TakeDbInfo(DbInfoRecord? loaded, DbUpgradeReport report) {
    Database.DbInfo = loaded;
    if (loaded is not { } record)
      return null;

    report.InstanceType = record.InstanceType;
    report.PreviousVersion = record.Version;
    report.PreviousModuleVersions = record.Details.ModuleVersions;

    if (record.LastUpgradeFailed)
      report.Add(Severity.Warning,
        "The last upgrade of this database FAILED part-way. This run re-diffs against what is actually there, so it "
        + "produces exactly the work that remains."
        + (record.LastUpgradeError == null ? "" : Environment.NewLine + record.LastUpgradeError));

    // Keys keep the layout they were minted in, so a change of scheme leaves a table holding both.
    if (record.Details.GuidScheme is { } recorded && recorded != Database.GuidScheme.ToString())
      report.Add(Severity.Warning,
        $"The database was last upgraded under Guid scheme {recorded} and this application mints "
        + $"{Database.GuidScheme}; keys already stored keep their layout, so a table will hold both.");
    return record;
  }
}
