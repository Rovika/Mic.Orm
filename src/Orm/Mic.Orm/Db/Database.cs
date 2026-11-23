using System.Data.Common;
using Mic.Linq.Model;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.DbInfo;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db;

/// <summary>Represents one database instance: the model it holds, the settings that reach it, and the connections to
/// it.</summary>
/// <remarks>Everything derived from the model for one database, such as the query cache and the entity cache, hangs
/// off its <see cref="Database"/>.</remarks>
public class Database {
  public Database(DbModel dbModel, DbSettings settings) {
    ArgumentNullException.ThrowIfNull(dbModel);
    ArgumentNullException.ThrowIfNull(settings);
    DbModel = dbModel;
    Settings = settings;
    Alias = settings.DatabaseAlias;
    GuidScheme = settings.Provider.GuidScheme;
    RequestExecutor = new Runtime.DataRequestExecutor(this);
  }

  public DbModel DbModel { get; }

  /// <summary>Gets the layout of the Guids minted for this database: the application's
  /// <see cref="EntityAppSettings.GuidScheme"/>, or the provider's under <see cref="GuidScheme.Auto"/>.</summary>
  public GuidScheme GuidScheme { get; init; }

  /// <summary>Gets the executor that runs a session's requests against this database.</summary>
  /// <remarks>One executor serves every session and holds nothing of any; a request carries its session and runs over
  /// that session's connection.</remarks>
  public Runtime.DataRequestExecutor RequestExecutor { get; }

  /// <summary>Gets the translated queries this database has seen, by their shape.</summary>
  /// <remarks>A translation is per database: the same tree becomes different SQL for each vendor.</remarks>
  public QueryCache QueryCache { get; } = new();

  /// <summary>Gets the entities held whole in memory for this database, and their rows.</summary>
  public Runtime.EntityCache EntityCache { get; } = new();
  public DbSettings Settings { get; }
  public DbProvider Provider => Settings.Provider;

  /// <summary>Gets the name the application knows this database by, which is its identity in the registry and what
  /// an operation names to reach it.</summary>
  /// <remarks>The alias is not the database's name on the server and appears in no connection string. An application
  /// with one database uses <c>(default)</c>; a multi-tenant one names a tenant.</remarks>
  public string Alias { get; }

  /// <summary>Gets or sets the report of the last upgrade run against this database, or <see langword="null"/> if
  /// nothing has connected yet.</summary>
  /// <remarks><see cref="EntityApp.IsConnected"/> relies on <see langword="null"/> meaning never connected.</remarks>
  public DbUpgradeReport? UpgradeReport { get; set; }

  /// <summary>Creates a new, unopened connection to this database.</summary>
  /// <param name="lifetime">One of the enumeration values that specifies how long the connection stays open.</param>
  /// <param name="forSchema"><see langword="true"/> to use the schema-management connection string; otherwise,
  /// <see langword="false"/>.</param>
  /// <returns>The new connection.</returns>
  public DataConnection CreateConnection(DbConnectionLifetime lifetime = DbConnectionLifetime.Operation,
      bool forSchema = false) {
    var connectionString = forSchema ? Settings.SchemaConnectionString : Settings.ConnectionString;
    var dbConnection = Provider.CreateConnection(connectionString);
    return new DataConnection(this, dbConnection, lifetime);
  }

  /// <summary>Gets the record MIC keeps about this database, as last read: the version last applied, whether the
  /// last upgrade failed, and what kind of instance it is. The value is <see langword="null"/> until something has read
  /// it.</summary>
  public DbInfoRecord? DbInfo { get; internal set; }

  /// <summary>Finds a table in this database's model by name, in a given schema or in whichever schema has it.
  /// </summary>
  /// <param name="name">The name of the table, compared ignoring case.</param>
  /// <param name="schema">The name of the schema, or <see langword="null"/> to search every schema.</param>
  /// <returns>The table, or <see langword="null"/> if the model has no such table.</returns>
  public DbTableInfo? FindTable(string name, string? schema = null) =>
    DbModel.Tables.FirstOrDefault(t =>
      string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
      && (schema == null || string.Equals(t.Schema.Name, schema, StringComparison.OrdinalIgnoreCase)));

  /// <summary>Asynchronously reads a whole table found by name.</summary>
  /// <param name="name">The name of the table, compared ignoring case.</param>
  /// <param name="schema">The name of the schema, or <see langword="null"/> to search every schema.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the rows, or
  /// <see langword="null"/> if the model has no such table.</returns>
  public async Task<InfoTable?> LoadTableAsync(string name, string? schema = null,
      CancellationToken cancellation = default) =>
    FindTable(name, schema) is { } table ? await LoadTableAsync(table, cancellation) : null;

  /// <summary>Asynchronously reads a whole table on its own schema connection, for MIC's own bookkeeping and for
  /// rows read before the entity runtime exists.</summary>
  /// <param name="table">The table to read.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains every row of the table.
  /// </returns>
  /// <remarks>This is not a query facility: it selects everything, which suits a metadata table of a few rows and
  /// nothing larger.</remarks>
  public async Task<InfoTable> LoadTableAsync(DbTableInfo table, CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(table);
    await using var connection = CreateConnection(forSchema: true);
    await connection.OpenAsync(RunMode.Async, cancellation);

    using var dbCommand = Provider.CreateDbCommand();
    dbCommand.Connection = connection.DbConnection;
    dbCommand.CommandText = $"SELECT * FROM {Provider.SqlDialect.GetTableRef(table)}";
    try {
      await using var reader = await dbCommand.ExecuteReaderAsync(cancellation);
      return await InfoTable.ReadAsync(reader, cancellation);
    } catch (DbException ex) {
      throw Provider.Fail(
        $"Database '{Settings.DatabaseAlias}': reading '{table.Name}' failed. {ex.Message}", ex,
        Settings.DatabaseAlias);
    }
  }

  /// <summary>Asynchronously reads the record MIC keeps about this database from the server and stores it in
  /// <see cref="DbInfo"/>.</summary>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the record, or
  /// <see langword="null"/> if the database has none.</returns>
  /// <remarks>The table is read through this database's own model, which must contain it. A load of the model fills
  /// <see cref="DbInfo"/> already; calling this asks the server again.</remarks>
  public async Task<DbInfoRecord?> LoadDbInfoAsync(CancellationToken cancellation = default) =>
    DbInfo = await DbInfoModule.LoadDbInfoAsync(this, cancellation);

  /// <summary>Asynchronously verifies that the database can be reached on each of its connection strings by opening
  /// a connection, running a trivial statement and closing it again.</summary>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <exception cref="DataAccessException">the database cannot be reached on one of its connection strings. The
  /// exception's <see cref="DataAccessException.SubType"/> is
  /// <see cref="DataAccessException.SubTypeConnectionFailed"/>.</exception>
  /// <remarks>Both connection strings are checked when they differ. Only reachability is verified; whether the schema
  /// matches the model is the upgrade's question.</remarks>
  public async Task CheckConnectivityAsync(CancellationToken cancellation = default) {
    await CheckAsync(Settings.ConnectionString, "connection string", cancellation);
    if (Settings.HasSeparateSchemaConnection)
      await CheckAsync(Settings.SchemaConnectionString, "schema-management connection string", cancellation);
  }

  private async Task CheckAsync(string connectionString, string what, CancellationToken cancellation) {
    DbConnection? connection = null;
    try {
      connection = Provider.CreateConnection(connectionString);
      await connection.OpenAsync(cancellation);

      await using var command = connection.CreateCommand();
      command.CommandText = Provider.SqlDialect.SelectOne;
      await command.ExecuteScalarAsync(cancellation);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      var failure = Provider.Fail(
        $"Database '{Settings.DatabaseAlias}': cannot reach the database using the {what}. {ex.Message}", ex,
        Settings.DatabaseAlias);
      failure.SubType = DataAccessException.SubTypeConnectionFailed;
      throw failure;
    } finally {
      if (connection != null)
        await connection.DisposeAsync();
    }
  }

  public override string ToString() => $"{Settings.DatabaseAlias} ({Provider.ServerKind}, {DbModel.Tables.Count} tables)";
}
