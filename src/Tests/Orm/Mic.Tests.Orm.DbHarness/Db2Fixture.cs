using System.Data;
using System.Data.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Db2;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

/// <summary>Db2 Community Edition, from the container in <c>docker-compose.yml</c>, behind the profile
/// <c>db2</c>.</summary>
/// <remarks>Port 50000. Override with <c>MIC_TEST_DB2</c>. A database of one's own is a schema of the database
/// <c>MICTEST</c>. A schema with objects in it is dropped by the procedure <c>SYSPROC.ADMIN_DROP_SCHEMA</c>, since
/// <c>DROP SCHEMA</c> takes an empty one only.</remarks>
public sealed class Db2Fixture : ProviderFixture {
  public const string DefaultConnectionString =
    $"Server=localhost:50000;Database=MICTEST;UID=db2inst1;PWD=mic;Max Pool Size={MaxPoolSize}";

  public static string ConnectionString =>
    Environment.GetEnvironmentVariable("MIC_TEST_DB2") ?? DefaultConnectionString;

  public override DbServerKind ServerKind => DbServerKind.Db2;
  public override DbProvider CreateProvider() => new Db2Provider();

  public override async Task<TestDatabase> CreateIsolatedAsync(string tag) {
    var schema = Sanitise(tag) + "_" + Guid.NewGuid().ToString("N")[..8];
    var settings = new DbSettings(CreateProvider(), ConnectionString);
    var database = new TestDatabase(settings, schema,
      () => DropSchemaAsync(settings, schema),
      () => EnsureSchemaAsync(settings, schema));
    await DropSchemaAsync(settings, schema);
    await EnsureTempSpaceAsync(settings);
    return database;
  }

  /// <summary>Creates a user temporary table space unless the database has one: a declared temporary table needs
  /// it, and a new database has none.</summary>
  static async Task EnsureTempSpaceAsync(DbSettings settings) {
    await using var connection = settings.Provider.CreateConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await using var find = connection.CreateCommand();
    find.CommandText = "SELECT COUNT(*) FROM SYSCAT.TABLESPACES WHERE DATATYPE = 'U'";
    if (Convert.ToInt32(await find.ExecuteScalarAsync()) > 0)
      return;
    await using var create = connection.CreateCommand();
    create.CommandText = "CREATE USER TEMPORARY TABLESPACE MICTEMP";
    try {
      await create.ExecuteNonQueryAsync();
    } catch (DbException ex) when (ex.Message.Contains("42710", StringComparison.Ordinal)) {
    }
  }

  /// <summary>Creates the schema unless it is there; SQLSTATE 42710 says a name is taken.</summary>
  static async Task EnsureSchemaAsync(DbSettings settings, string schema) {
    await using var connection = settings.Provider.CreateConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"CREATE SCHEMA \"{schema}\"";
    try {
      await command.ExecuteNonQueryAsync();
    } catch (DbException ex) when (ex.Message.Contains("42710", StringComparison.Ordinal)) {
    }
  }

  /// <summary>Drops the schema with everything in it, where it exists.</summary>
  /// <remarks>The procedure reports what it could not drop into a table it names through its last two arguments,
  /// which are input and output both.</remarks>
  static async Task DropSchemaAsync(DbSettings settings, string schema) {
    await using var connection = settings.Provider.CreateConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await using var find = connection.CreateCommand();
    find.CommandText = $"SELECT COUNT(*) FROM SYSCAT.SCHEMATA WHERE SCHEMANAME = '{schema}'";
    if (Convert.ToInt32(await find.ExecuteScalarAsync()) == 0)
      return;

    await using var drop = connection.CreateCommand();
    drop.CommandText = "CALL SYSPROC.ADMIN_DROP_SCHEMA(@schema, NULL, @errorSchema, @errorTable)";
    AddTextParameter(drop, "@schema", schema, ParameterDirection.Input);
    AddTextParameter(drop, "@errorSchema", "MICDROPERR", ParameterDirection.InputOutput);
    AddTextParameter(drop, "@errorTable", "E" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
      ParameterDirection.InputOutput);
    await drop.ExecuteNonQueryAsync();
  }

  static void AddTextParameter(DbCommand command, string name, string value, ParameterDirection direction) {
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.DbType = DbType.String;
    parameter.Size = 128;
    parameter.Direction = direction;
    parameter.Value = value;
    command.Parameters.Add(parameter);
  }

  /// <summary>Lower case, and short: the name is quoted everywhere MIC writes it, so it keeps this case.</summary>
  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    return clean.Length <= 20 ? clean : clean[..20];
  }

  /// <summary>Forces the connection's application off the server, by its handle.</summary>
  public override async Task<bool> TryKillSessionAsync(DataConnection connection) {
    await using var command = connection.DbConnection.CreateCommand();
    command.CommandText = "SELECT MON_GET_APPLICATION_HANDLE() FROM SYSIBM.SYSDUMMY1";
    var handle = Convert.ToInt64(await command.ExecuteScalarAsync());

    await using var killer = CreateProvider().CreateConnection(ConnectionString);
    await killer.OpenAsync();
    await using var kill = killer.CreateCommand();
    kill.CommandText = $"CALL SYSPROC.ADMIN_CMD('FORCE APPLICATION ({handle})')";
    await kill.ExecuteNonQueryAsync();
    return true;
  }

  public override string ToString() => "Db2";
}
