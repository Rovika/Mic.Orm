using System.Data.Common;
using System.Text.Json;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.DbInfo;

/// <summary>Defines what MIC records about a database it manages.</summary>
/// <remarks>Declared as an entity, so it takes part in the model and the diff like any other. There is one row per
/// database.</remarks>
[Entity(Name = "DbInfo")]
public interface IDbInfo {
  /// <summary>Gets or sets the key of the row, minted by the ORM on the client.</summary>
  [PrimaryKey, Auto] Guid Id { get; set; }

  /// <summary>Gets or sets the name of the application that last wrote the row.</summary>
  /// <remarks>Informational only; nothing is keyed on it. Several applications may share a database, and what they
  /// share is one record of it.</remarks>
  [Size(100)] string AppName { get; set; }

  /// <summary>Gets or sets the kind of database this is, set by an administrator in the database and never written
  /// by MIC.</summary>
  [NoUpdate] DbInstanceType InstanceType { get; set; }

  /// <summary>Gets or sets the model version last applied.</summary>
  [Size(20)] string Version { get; set; }

  /// <summary>Gets or sets a value that indicates whether the last upgrade failed part-way, leaving the schema
  /// part-changed.</summary>
  /// <remarks>The next run re-diffs and produces what is left to apply.</remarks>
  bool LastUpgradeFailed { get; set; }

  [Unlimited] string? LastUpgradeError { get; set; }

  /// <summary>Gets or sets when MIC last applied DDL to this database, in UTC.</summary>
  /// <value>The time of the last applied upgrade, or <see langword="null"/> on a row written before this column
  /// existed.</value>
  /// <remarks>Stamped by a connect that applied DDL and by the last statement of a script the database tool
  /// wrote, so it dates the schema, not the last attempt.</remarks>
  DateTime? LastUpgradedOn { get; set; }

  /// <summary>Gets or sets everything else MIC remembers about this database, as a JSON document of
  /// <c>DbInfoDetails</c>, including the per-module versions.</summary>
  /// <remarks>Plain text rather than a JSON column type, so that the table exists on types every server has. Read
  /// tolerantly: missing and unknown fields are fine, and absent module versions mean unknown.</remarks>
  [Unlimited] string? Details { get; set; }
}

/// <summary>Represents the module that carries MIC's own bookkeeping, and reads and writes it.</summary>
/// <remarks>Added by <see cref="EntityApp.Init"/> automatically unless
/// <see cref="EntityAppSettings.IncludeDbInfoModule"/> is off. A successful upgrade is recorded as an entity save
/// off <c>DatabaseEvents.Connected</c>, and so is a database found to match the model while its row is missing or
/// behind. Reading the record and recording a failed upgrade go through direct SQL, since the first has to work
/// without an entity model and the second with a part-changed schema.</remarks>
public class DbInfoModule : EntityModule {
  public const string ModuleName = "MicDbInfo";

  public DbInfoModule() : base(ModuleName, new Version(1, 0)) {
    RegisterEntities(typeof(IDbInfo));
  }

  /// <summary>Hooks the recording of a successful upgrade to the application's <c>DatabaseEvents.Connected</c>
  /// event.</summary>
  /// <param name="model">The finished model.</param>
  /// <remarks>The event is raised at the end of <see cref="EntityApp.ConnectToAsync"/>, when the schema has settled
  /// and the entity runtime is up, and never when the upgrade threw; a failure is recorded by
  /// <see cref="SaveUpgradeErrorAsync"/> instead.</remarks>
  public override void OnModelConstructed(EntityAppModel model) {
    if (App != null)
      App.DatabaseEvents.Connected += RecordUpgrade;
  }

  private void RecordUpgrade(Database database) {
    var model = database.DbModel.EntityModel;
    if (model == null || database.UpgradeReport is not { } report)
      return;                                 // introspected: no application, nothing to record about one

    if (report.IsApplied) {
      WriteRecord(database, model, isUpgrade: true);
      return;
    }

    // No DDL ran. A database that matches the model has its row brought up to date: a version changed with no
    // change of schema, a script run with the server's own client, a failed upgrade repaired by hand. A database
    // with differences left in it keeps the row it has.
    if (report.Changes is not { HasItems: false } || report.HasErrors || !IsOutOfDate(database.DbInfo, model))
      return;
    try {
      WriteRecord(database, model, isUpgrade: false);
    } catch (DataAccessException refused) {
      // The schema is as the model wants it; a login that may not write the row is not a reason to stop.
      report.Add(Severity.Warning, $"The {database.Settings.DbInfoTableName} row was not brought up to version "
        + $"{model.Version}: {refused.Message}");
    }
  }

  /// <summary>Writes the row from the application's model and puts what was written on the database.</summary>
  /// <param name="database">The database.</param>
  /// <param name="model">The application's model.</param>
  /// <param name="isUpgrade"><see langword="true"/> for a run that applied DDL, which stamps the time and records
  /// the Guid scheme; <see langword="false"/> to keep both as the row has them.</param>
  private void WriteRecord(Database database, EntityAppModel model, bool isUpgrade) {
    var session = new EntitySession(new OperationContext(App!) { DatabaseAlias = database.Alias });
    var current = database.DbInfo;

    // InstanceType is never assigned: [NoUpdate] keeps an administrator's value, and a new row takes the enum's
    // default, Development.
    var info = current != null ? session.GetEntity<IDbInfo>(current.Id) : session.NewEntity<IDbInfo>();

    var scheme = database.GuidScheme.ToString();
    var details = GetDetails(model, isUpgrade ? scheme : current?.Details.GuidScheme ?? scheme);
    var upgradedOn = isUpgrade ? App!.TimeService.UtcNow : current?.LastUpgradedOn;

    info.AppName = model.Name;
    info.Version = model.Version.ToString();
    info.LastUpgradeFailed = false;
    info.LastUpgradeError = null;
    info.LastUpgradedOn = upgradedOn;
    info.Details = JsonSerializer.Serialize(details, JsonOptions);
    session.SaveChanges();

    database.DbInfo = new DbInfoRecord {
      Id = info.Id,
      AppName = info.AppName,
      InstanceType = info.InstanceType,
      Version = model.Version,
      LastUpgradedOn = upgradedOn,
      Details = details,
    };
  }

  /// <summary>Determines whether the row is missing or says something else than the model about the versions or
  /// the outcome of the last upgrade.</summary>
  private static bool IsOutOfDate(DbInfoRecord? current, EntityAppModel model) {
    if (current == null || current.LastUpgradeFailed || current.Version != model.Version)
      return true;
    var stored = current.Details.ModuleVersions;
    return stored.Count != model.Modules.Count
      || model.Modules.Any(m => !stored.TryGetValue(m.Name, out var version) || version != m.Version.ToString());
  }

  private static DbInfoDetails GetDetails(EntityAppModel model, string guidScheme) {
    var details = new DbInfoDetails { GuidScheme = guidScheme };
    foreach (var module in model.Modules)
      details.ModuleVersions[module.Name] = module.Version.ToString();
    return details;
  }

  private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

  /// <summary>Asynchronously reads what MIC records about a database.</summary>
  /// <param name="database">The database, whose own model names the table.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the record, or
  /// <see langword="null"/> if the table holds no row.</returns>
  /// <remarks>The first row is taken; there is one record per database. Whether the table exists on the server is
  /// not probed here; a caller mid-upgrade has just introspected and asks that first.</remarks>
  public static async Task<DbInfoRecord?> LoadDbInfoAsync(Database database,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(database);
    var rows = await database.LoadTableAsync(database.Settings.DbInfoTableName, cancellation: cancellation);
    var row = rows?.Rows.FirstOrDefault();
    if (row == null)
      return null;

    return new DbInfoRecord {
      // A server with no Guid type stores text, as SQLite does, so the value is not taken as a Guid outright.
      Id = ReadGuid(row["Id"]),
      AppName = row.GetString(nameof(IDbInfo.AppName)) ?? "",
      InstanceType = (DbInstanceType)row.GetInt(nameof(IDbInfo.InstanceType)),
      Version = Version.TryParse(row.GetString(nameof(IDbInfo.Version)), out var version) ? version : null,
      LastUpgradeFailed = row.GetBool(nameof(IDbInfo.LastUpgradeFailed)),
      LastUpgradeError = row.GetString(nameof(IDbInfo.LastUpgradeError)),
      // Written as UTC, and often read back as Unspecified, which ToLocalTime() would shift.
      LastUpgradedOn = row.GetDateTime(nameof(IDbInfo.LastUpgradedOn)) is { } upgradedOn
        ? DateTime.SpecifyKind(upgradedOn, DateTimeKind.Utc)
        : null,
      Details = ReadDetails(row.GetString(nameof(IDbInfo.Details))),
    };
  }

  /// <summary>Asynchronously notes in the database that the last upgrade failed, and what it said.</summary>
  /// <param name="database">The database.</param>
  /// <param name="upgradeError">The error message to record.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>A note only; nothing in MIC reads it to decide anything. Updates every row of the table, inserts none,
  /// and never throws: it runs from inside a catch with a real failure in flight.</remarks>
  public static async Task SaveUpgradeErrorAsync(Database database, string upgradeError,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(database);
    var table = database.FindTable(database.Settings.DbInfoTableName);
    if (table == null)
      return;

    var provider = database.Provider;
    var dialect = provider.SqlDialect;

    // The bind names avoid reserved words: `:values` and `:rowId` are ORA-01745 on Oracle, so a bind name is spelled
    // after what it is to MIC rather than after the column.
    var prefix = provider.ParameterPrefix;
    try {
      await using var connection = database.CreateConnection(forSchema: true);
      await connection.OpenAsync(RunMode.Async, cancellation);
      using var dbCommand = provider.CreateDbCommand();
      dbCommand.Connection = connection.DbConnection;
      dbCommand.CommandText =
        $"UPDATE {dialect.GetTableRef(table)} SET {dialect.Quote("LastUpgradeFailed")} = {prefix}upgradeFailed, "
        + $"{dialect.Quote("LastUpgradeError")} = {prefix}upgradeError";
      provider.AddParameter(dbCommand, "upgradeFailed", true);
      provider.AddParameter(dbCommand, "upgradeError", upgradeError);
      await dbCommand.ExecuteNonQueryAsync(cancellation);
    } catch (DbException) {
      // The note is never worth losing the real failure over.
    }
  }

  /// <summary>Builds the statement that records an upgrade in the database's row, for a script run with the
  /// server's own client.</summary>
  /// <param name="database">The database, mapped from the application's model.</param>
  /// <returns>An <c>UPDATE</c> that sets the application's name and version, the module versions and the Guid
  /// scheme, clears the note of a failed upgrade and stamps the server's time; or <see langword="null"/> if the
  /// model has no such table.</returns>
  /// <remarks>The statement updates the row and inserts none. A database that has no row yet gets one from the
  /// application's next connect.</remarks>
  public static string? BuildUpgradeRecordSql(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    var table = database.FindTable(database.Settings.DbInfoTableName);
    if (table == null || database.DbModel.EntityModel is not { } model)
      return null;

    var dialect = database.Provider.SqlDialect;
    var details = JsonSerializer.Serialize(GetDetails(model, database.GuidScheme.ToString()), JsonOptions);
    List<(string Column, string Value)> values = [
      (nameof(IDbInfo.AppName), dialect.GetLiteral(model.Name)),
      (nameof(IDbInfo.Version), dialect.GetLiteral(model.Version.ToString())),
      (nameof(IDbInfo.LastUpgradeFailed), dialect.BooleanLiteral(false)),
      (nameof(IDbInfo.LastUpgradeError), "NULL"),
      (nameof(IDbInfo.Details), dialect.GetLiteral(details)),
    ];
    if (dialect.SelectServerTimeUtc.Length > 0)
      values.Add((nameof(IDbInfo.LastUpgradedOn), $"({dialect.SelectServerTimeUtc})"));

    var indent = Environment.NewLine + dialect.Indent;
    return $"UPDATE {dialect.GetTableRef(table)} SET"
      + indent + string.Join("," + indent, values.Select(v => $"{dialect.Quote(v.Column)} = {v.Value}"));
  }

  /// <summary>Reads the details document tolerantly; unreadable JSON means unknown rather than a failure.</summary>
  private static DbInfoDetails ReadDetails(string? json) {
    if (string.IsNullOrWhiteSpace(json))
      return new DbInfoDetails();
    try {
      return JsonSerializer.Deserialize<DbInfoDetails>(json, JsonOptions) ?? new DbInfoDetails();
    } catch (JsonException) {
      return new DbInfoDetails();
    }
  }

  /// <summary>Reads a Guid however the server keeps one: a Guid, the text a server without the type stores, or the
  /// sixteen bytes a binary column holds.</summary>
  /// <remarks>Bytes are read in RFC order, the order a Guid is written to a binary column in.</remarks>
  private static Guid ReadGuid(object? value) => value switch {
    Guid guid => guid,
    string text when Guid.TryParse(text, out var parsed) => parsed,
    byte[] bytes when bytes.Length == 16 => new Guid(bytes, bigEndian: true),
    _ => Guid.Empty,
  };
}
