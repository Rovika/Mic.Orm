using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.SqlServer;

namespace Mic.Tests.Orm.Db;

/// <summary>SQL Server, from the container in <c>docker-compose.yml</c> — which is OPT-IN, behind the `heavy` compose
/// profile and out of the default vendor set.</summary>
/// <remarks>Port 1434, so a SQL Server already installed on the machine keeps 1433. Override with
/// <c>MIC_TEST_SQLSERVER</c>.
///
/// A database of one's own is a SCHEMA, as on PostgreSQL — the fourth vendor and only the second answer, which is
/// itself worth noting after SQLite's file and MySQL's database. What differs is the teardown: SQL Server will not
/// drop a schema that still contains anything, so everything in it has to go first, and foreign keys before tables.
/// </remarks>
public sealed class SqlServerFixture : ProviderFixture {
  public const string DefaultConnectionString =
    "Server=localhost,1434;User ID=sa;Password=Mic_test_1;TrustServerCertificate=True;Database=mic_test"
    + $";Max Pool Size={MaxPoolSize}";

  public static string ConnectionString =>
    Environment.GetEnvironmentVariable("MIC_TEST_SQLSERVER") ?? DefaultConnectionString;

  /// <summary>The same server, with no database named — where `CREATE DATABASE` has to be run from.</summary>
  static string MasterConnectionString =>
    string.Join(';', ConnectionString.Split(';').Where(p => !p.TrimStart().StartsWith("Database=",
      StringComparison.OrdinalIgnoreCase))) + ";Database=master";

  static string DatabaseName =>
    ConnectionString.Split(';').Select(p => p.Trim())
      .FirstOrDefault(p => p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase))?["Database=".Length..]
      ?? "mic_test";

  /// <summary>The container starts with system databases only, so the one the tests share has to be made. Once per
  /// process: every test then works inside a schema of its own.</summary>
  static readonly Lazy<Task> EnsureDatabase = new(async () => {
    await using var connection = new SqlServerProvider().CreateConnection(MasterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}]";
    await command.ExecuteNonQueryAsync();
    // A CONSISTENT READ OF A DOCUMENT runs at SNAPSHOT isolation on this server, which the database has to allow —
    // the one setting the pattern needs; a deployment makes the same one. See DbProvider.SnapshotIsolationLevel.
    command.CommandText = $"ALTER DATABASE [{DatabaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON";
    await command.ExecuteNonQueryAsync();
  });

  public override DbServerKind ServerKind => DbServerKind.MsSql;
  public override DbProvider CreateProvider() => new SqlServerProvider();

  public override async Task<TestDatabase> CreateIsolatedAsync(string tag) {
    await EnsureDatabase.Value;
    var schema = Sanitise(tag) + "_" + Guid.NewGuid().ToString("N")[..8];
    var settings = new DbSettings(CreateProvider(), ConnectionString);

    TestDatabase database = null!;
    database = new TestDatabase(settings, schema,
      () => database.ExecuteAsync(DropEverything(schema)),
      () => database.ExecuteAsync(
        $"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA {database.Quote(schema)}')"));
    return database;
  }

  /// <summary>Everything in the schema, then the schema — in one batch, because the statements have to be BUILT from
  /// what is there rather than known in advance.</summary>
  /// <remarks>Foreign keys first: a table cannot be dropped while another one points at it, and the order tables would
  /// have to be dropped in is a topological sort nobody needs to do if the constraints go first.</remarks>
  static string DropEverything(string schema) => $@"
DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                   + N' DROP CONSTRAINT ' + QUOTENAME(f.name) + N';'
FROM sys.foreign_keys f
     JOIN sys.tables t ON t.object_id = f.parent_object_id
     JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = '{schema}';

SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = '{schema}';

SELECT @sql = @sql + N'DROP SEQUENCE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(q.name) + N';'
FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id
WHERE s.name = '{schema}';

IF SCHEMA_ID('{schema}') IS NOT NULL
  SET @sql = @sql + N'DROP SCHEMA ' + QUOTENAME('{schema}') + N';';

EXEC sp_executesql @sql;";

  /// <summary>128 characters is the limit, and a schema name is an ordinary identifier.</summary>
  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    return clean.Length <= 20 ? clean : clean[..20];
  }

  /// <summary>`KILL`, which needs sysadmin — which `sa` is.</summary>
  public override async Task<bool> TryKillSessionAsync(DataConnection connection) {
    await using var command = connection.DbConnection.CreateCommand();
    command.CommandText = "SELECT @@SPID";
    var spid = Convert.ToInt32(await command.ExecuteScalarAsync());

    await using var killer = CreateProvider().CreateConnection(ConnectionString);
    await killer.OpenAsync();
    await using var kill = killer.CreateCommand();
    kill.CommandText = $"KILL {spid}";
    await kill.ExecuteNonQueryAsync();
    return true;
  }

  public override string ToString() => "SqlServer";
}
