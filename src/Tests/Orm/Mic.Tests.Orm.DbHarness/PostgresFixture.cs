using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

/// <summary>PostgreSQL, from the container in <c>docker-compose.yml</c>.</summary>
/// <remarks>Port 5433, so a PostgreSQL already installed on the machine keeps 5432. Override with
/// <c>MIC_TEST_POSTGRES</c> to run against something else.
///
/// A database of one's own is a SCHEMA here — cheap to make, cheap to drop, and several can live in the one database
/// the container starts with.</remarks>
public sealed class PostgresFixture : ProviderFixture {
  public const string DefaultConnectionString =
    $"Host=localhost;Port=5433;Database=mic_test;Username=mic;Password=mic;Maximum Pool Size={MaxPoolSize}";

  public static string ConnectionString =>
    Environment.GetEnvironmentVariable("MIC_TEST_POSTGRES") ?? DefaultConnectionString;

  public override DbServerKind ServerKind => DbServerKind.Postgres;
  public override DbProvider CreateProvider() => new PostgresProvider();

  public override async Task<TestDatabase> CreateIsolatedAsync(string tag) {
    var schema = Sanitise(tag) + "_" + Guid.NewGuid().ToString("N")[..8];
    var settings = new DbSettings(CreateProvider(), ConnectionString);
    TestDatabase database = null!;
    database = new TestDatabase(settings, schema,
      () => DropSchemaAsync(settings, schema),
      () => database.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {database.Quote(schema)}"));
    await database.ExecuteAsync($"DROP SCHEMA IF EXISTS {database.Quote(schema)} CASCADE");
    return database;
  }

  static async Task DropSchemaAsync(DbSettings settings, string schema) {
    await using var connection = settings.Provider.CreateConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>A schema name from a test's name — lower case and short, because PostgreSQL folds unquoted identifiers
  /// and truncates at 63.</summary>
  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    return clean.Length <= 20 ? clean : clean[..20];
  }

  /// <summary>`pg_terminate_backend`, from another connection. Closing the connection would NOT do: Npgsql pools it
  /// and resets on the way out rather than in, so the backend — and any session lock it holds — outlives the close.
  /// </summary>
  public override async Task<bool> TryKillSessionAsync(DataConnection connection) {
    await using var command = connection.DbConnection.CreateCommand();
    command.CommandText = "SELECT pg_backend_pid()";
    var pid = Convert.ToInt32(await command.ExecuteScalarAsync());

    await using var killer = CreateProvider().CreateConnection(ConnectionString);
    await killer.OpenAsync();
    await using var kill = killer.CreateCommand();
    kill.CommandText = $"SELECT pg_terminate_backend({pid})";
    await kill.ExecuteScalarAsync();
    return true;
  }

  public override string ToString() => "Postgres";
}
