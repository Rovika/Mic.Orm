using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.MySql;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

/// <summary>MySQL, from the container in <c>docker-compose.yml</c>.</summary>
/// <remarks>Port 3307, so a MySQL already installed on the machine keeps 3306. Override with <c>MIC_TEST_MYSQL</c>.
///
/// A database of one's own is a DATABASE here, because that is what a schema IS on this server — the third different
/// answer the fixture has given to the same question, after PostgreSQL's schema and SQLite's file. The connection
/// names no database at all: every test works inside the one it just made, and MIC qualifies everything by schema
/// anyway.</remarks>
public sealed class MySqlFixture : ProviderFixture {
  public const string DefaultConnectionString =
    $"Server=localhost;Port=3307;User ID=root;Password=mic;AllowUserVariables=True;MaximumPoolSize={MaxPoolSize}";

  public static string ConnectionString =>
    Environment.GetEnvironmentVariable("MIC_TEST_MYSQL") ?? DefaultConnectionString;

  public override DbServerKind ServerKind => DbServerKind.MySql;
  public override DbProvider CreateProvider() => new MySqlProvider();

  public override async Task<TestDatabase> CreateIsolatedAsync(string tag) {
    var schema = Sanitise(tag) + "_" + Guid.NewGuid().ToString("N")[..8];
    var settings = new DbSettings(CreateProvider(), ConnectionString);

    TestDatabase database = null!;
    database = new TestDatabase(settings, schema,
      () => database.ExecuteAsync($"DROP DATABASE IF EXISTS {database.Quote(schema)}"),
      () => database.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {database.Quote(schema)}"));
    await database.ExecuteAsync($"DROP DATABASE IF EXISTS {database.Quote(schema)}");
    return database;
  }

  /// <summary>`pg_terminate_backend`'s counterpart.</summary>
  public override async Task<bool> TryKillSessionAsync(DataConnection connection) {
    await using var command = connection.DbConnection.CreateCommand();
    command.CommandText = "SELECT CONNECTION_ID()";
    var id = Convert.ToInt64(await command.ExecuteScalarAsync());

    await using var killer = CreateProvider().CreateConnection(ConnectionString);
    await killer.OpenAsync();
    await using var kill = killer.CreateCommand();
    kill.CommandText = $"KILL CONNECTION {id}";
    await kill.ExecuteNonQueryAsync();
    return true;
  }

  /// <summary>Lower case and short: 64 characters is the limit, and a database name is a directory name on disk.
  /// </summary>
  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    return clean.Length <= 20 ? clean : clean[..20];
  }

  public override string ToString() => "MySql";
}
