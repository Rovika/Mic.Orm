using System.Text;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.DbInfo;
using Mic.Orm.Logging;

namespace Mic.DbTool;

/// <summary>Provides the jobs of the database tool over an initialized application: the upgrade script planned
/// against a database and written out, for a production upgrade done by hand.</summary>
public static class DbToolHelper {
  /// <summary>The header line of a script file that says the tool applied the script.</summary>
  public const string AppliedNote = "The tool applied this script.";

  /// <summary>The heading of the last statement of a planned script, which records the upgrade in the database's
  /// <c>DbInfo</c> row.</summary>
  public const string UpgradeRecordHeading = "The record of this upgrade in the DbInfo row";

  /// <summary>Asynchronously maps the application's model to the database, reads the database and plans the
  /// upgrade: the differences and the script, with nothing applied.</summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The database to plan against.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the report, with the
  /// differences and the script where there are any.</returns>
  /// <exception cref="DataAccessException">The model cannot be mapped to the provider, or the database cannot be
  /// reached or read.</exception>
  public static async Task<DbUpgradeReport> PlanUpgradeAsync(EntityApp app, DbSettings settings,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(settings);
    var database = app.DataAccess.FindDatabase(settings.DatabaseAlias) ?? AppInitHelper.CreateDatabase(app, settings)
      ?? throw new DataAccessException(
        $"Application '{app.Name}': the model cannot be mapped to {settings.Provider.ServerKind}. "
        + app.Model!.BuildReport.Format()) { DatabaseAlias = settings.DatabaseAlias };
    await database.CheckConnectivityAsync(cancellation);
    using var operation = new OperationContext(app) { CorrelationId = $"plan:{database.Alias}" };
    var report = await new DbUpgradeManager(database) { Context = operation }.PlanAsync(cancellation);
    database.UpgradeReport = report;
    return report;
  }

  /// <summary>Asynchronously connects the application to the database, applying the upgrade the database needs, and
  /// writes the script that ran to a file.</summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The database, with an upgrade mode that allows the upgrade.</param>
  /// <param name="scriptPath">The file that receives the script, replacing the planned one.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the report of the run.
  /// </returns>
  /// <remarks>The connect compares the database again under the upgrade lock, so what runs is what the database
  /// needs then, which differs from the plan where another instance changed the database in between. The file is
  /// written also for a run that failed part-way, with the error. A run that failed before it had a script leaves
  /// the file as it was.</remarks>
  /// <exception cref="DataAccessException">The database cannot be reached, or the upgrade failed.</exception>
  public static async Task<DbUpgradeReport> ApplyUpgradeAsync(EntityApp app, DbSettings settings, string scriptPath,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(scriptPath);
    try {
      return await app.ConnectToAsync(settings, cancellation);
    } finally {
      var ran = app.DataAccess.FindDatabase(settings.DatabaseAlias)?.UpgradeReport;
      if (ran != null && (ran.Script != null || !ran.HasErrors))
        File.WriteAllText(scriptPath, FormatScript(ran, settings, app));
    }
  }

  /// <summary>Asynchronously reads a database's tables, check constraints and views, with the views' defining
  /// selects, into a model, for the source writer.</summary>
  /// <param name="settings">The database to read.</param>
  /// <param name="schemas">The schemas to read, or empty for every schema but the system ones.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the model.</returns>
  /// <exception cref="DataAccessException">The database cannot be reached or read.</exception>
  public static Task<DbModel> LoadModelAsync(DbSettings settings, IReadOnlyList<string> schemas,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(schemas);
    var loader = settings.Provider.CreateDbModelLoader(settings);
    var parts = DbModelParts.Tables | DbModelParts.CheckConstraints | DbModelParts.Views | DbModelParts.SourceText;
    return loader.LoadModelAsync(schemas, parts, cancellation);
  }

  /// <summary>Formats an upgrade as the script file: a header saying what it was made from and for and whether the
  /// tool applied it, the warnings and errors, the statements by stage and, in a planned script, the statement that
  /// records the upgrade in the <c>DbInfo</c> row.</summary>
  /// <param name="report">The report <see cref="PlanUpgradeAsync"/> or <see cref="ApplyUpgradeAsync"/> returned.
  /// </param>
  /// <param name="settings">The database the script is for.</param>
  /// <param name="app">The application the script brings the database up to.</param>
  /// <returns>The script text.</returns>
  public static string FormatScript(DbUpgradeReport report, DbSettings settings, EntityApp app) {
    ArgumentNullException.ThrowIfNull(report);
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(app);
    var comment = settings.Provider.SqlDialect.LineComment;
    var text = new StringBuilder();
    text.AppendLine($"{comment} Upgrade script generated by the MIC database tool on {DateTime.Now:yyyy-MM-dd HH:mm}.");
    text.AppendLine($"{comment} Application: {app.Name} {app.Version}; server: {settings.Provider.ServerKind}; "
      + $"database: {settings.DatabaseAlias}.");
    if (report.InstanceType != null)
      text.AppendLine($"{comment} The database declares itself {report.InstanceType}"
        + (report.PreviousVersion == null ? "." : $", at version {report.PreviousVersion}."));
    if (report.IsApplied)
      text.AppendLine($"{comment} {AppliedNote}");
    foreach (var message in report.Messages.Where(m => m.Severity != Severity.Info))
      text.AppendLine($"{comment} {message.Severity}: {message.Message}");
    text.AppendLine();
    if (report.Script is not { IsEmpty: false } script)
      text.AppendLine($"{comment} The database matches the model; there is nothing to run.");
    else {
      if (script.HasDestructive)
        text.AppendLine($"{comment} THIS SCRIPT LOSES DATA: {script.Destructive.Count()} of its steps are destructive.")
          .AppendLine();
      text.Append(script.ToText());

      // A script run with the server's own client records the upgrade itself; an applied one was recorded by the
      // connect that ran it.
      if (!report.IsApplied && !report.HasErrors
          && app.DataAccess.FindDatabase(settings.DatabaseAlias) is { } database
          && DbInfoModule.BuildUpgradeRecordSql(database) is { } record)
        text.AppendLine()
          .AppendLine($"{comment} ----- {UpgradeRecordHeading} -----")
          .AppendLine(record + settings.Provider.SqlDialect.StatementEnd);
    }
    return text.ToString();
  }
}
