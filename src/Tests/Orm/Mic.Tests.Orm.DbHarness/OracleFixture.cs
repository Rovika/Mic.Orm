using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Oracle;
using Mic.Orm.Db.Provider;
using Oracle.ManagedDataAccess.Client;

namespace Mic.Tests.Orm.Db;

/// <summary>Oracle Free, from the container in <c>docker-compose.yml</c> — opt-in, behind the same `heavy` profile as
/// SQL Server.</summary>
/// <remarks>Port 1522, so an Oracle already installed on the machine keeps 1521. Override with
/// <c>MIC_TEST_ORACLE</c>.
///
/// A database of one's own is a USER, which is the fourth different answer the fixture has given to that question —
/// after PostgreSQL's schema, SQLite's file and MySQL's database. The connection is `system` throughout: nobody ever
/// connects AS the schema owner, because the owner has no password and exists only to own things.
///
/// The teardown is `DROP USER … CASCADE`, and the recycle bin is why `PURGE` has to follow: a dropped table is
/// renamed rather than removed, and it goes on answering every catalog query until somebody says otherwise.</remarks>
public sealed class OracleFixture : ProviderFixture {
  /// <remarks>THE STATEMENT CACHE IS OFF, and it is a fact about this HARNESS rather than about Oracle. ODP.NET's
  /// `Self Tuning` is on by default and grows a per-connection cache of prepared statements, each of which holds a
  /// server-side CURSOR — and `open_cursors` is 300 on a stock container. That is a good default for an application,
  /// whose SQL is a bounded set: the schema name is stable, so the same few hundred statements recur forever.
  ///
  /// It is the wrong default HERE, because every test class creates its own schema and the name is IN the SQL, so the
  /// set of distinct statements grows with the size of the suite. Once connections stopped leaking — see ORM_24 step
  /// 2 — a single session began living long enough to cache its way past the limit, and the symptom is `ORA-01000`
  /// raised by whatever query happened to be next, which is never the one at fault.</remarks>
  public const string DefaultConnectionString =
    "User Id=system;Password=mic;Data Source=localhost:1522/FREEPDB1;Self Tuning=false;Statement Cache Size=0"
    + $";Max Pool Size={MaxPoolSize}";

  public static string ConnectionString =>
    Environment.GetEnvironmentVariable("MIC_TEST_ORACLE") ?? DefaultConnectionString;

  public override DbServerKind ServerKind => DbServerKind.Oracle;
  public override DbProvider CreateProvider() => new OracleProvider();

  public override async Task<TestDatabase> CreateIsolatedAsync(string tag) {
    var schema = Sanitise(tag) + "_" + Guid.NewGuid().ToString("N")[..8];
    var settings = new DbSettings(CreateProvider(), ConnectionString);

    TestDatabase database = null!;
    database = new TestDatabase(settings, schema,
      () => DropUserAsync(database, schema),
      () => EnsureUserAsync(database, schema));
    await DropUserAsync(database, schema);
    return database;
  }

  /// <summary>`NO AUTHENTICATION` is what makes this a namespace rather than a login.</summary>
  /// <remarks>THERE IS NO `IF EXISTS` IN ORACLE, in any statement, so "make sure this is there" is a block that
  /// swallows exactly one error number and re-raises everything else. `IF EXISTS` written out longhand — and better
  /// than a `catch` around it, which would swallow the ones worth seeing.</remarks>
  /// <remarks>THE GRANTS ARE FOR MATERIALIZED VIEWS, SPATIAL INDEXES AND TABLE FUNCTIONS: one keeps its rows in a
  /// table of the schema's own, one builds its R-tree as sequences and tables there, which Oracle creates as the
  /// schema's user however privileged the creator is, so the user must be allowed them; and a function of the
  /// schema's runs for the connected user only where the schema's user may inherit that user's privileges, which
  /// SYSTEM grants nobody by default.</remarks>
  static Task EnsureUserAsync(TestDatabase database, string schema) => database.ExecuteAsync($@"
BEGIN
  BEGIN
    EXECUTE IMMEDIATE 'CREATE USER {database.Quote(schema)} NO AUTHENTICATION QUOTA UNLIMITED ON USERS';
  EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1920 THEN RAISE; END IF;   -- ORA-01920: the name is taken
  END;
  EXECUTE IMMEDIATE 'GRANT CREATE TABLE, CREATE MATERIALIZED VIEW, CREATE SEQUENCE TO {database.Quote(schema)}';
  EXECUTE IMMEDIATE 'GRANT INHERIT PRIVILEGES ON USER ' || USER || ' TO {database.Quote(schema)}';
END;");

  /// <summary>`CASCADE` takes the schema's objects with it, and the recycle bin gets what it leaves renamed rather
  /// than removed.</summary>
  static Task DropUserAsync(TestDatabase database, string schema) => database.ExecuteAsync($@"
BEGIN
  EXECUTE IMMEDIATE 'DROP USER {database.Quote(schema)} CASCADE';
EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF;   -- ORA-01918: no such user
END;");

  /// <summary>Lower case, and short: the name is quoted everywhere MIC writes it, so it keeps this case rather than
  /// folding to Oracle's upper.</summary>
  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    return clean.Length <= 20 ? clean : clean[..20];
  }

  /// <summary>`ALTER SYSTEM KILL SESSION`, which needs the SID and serial number rather than one id.</summary>
  /// <remarks>ORA-00031 IS A SUCCESS, and the only one of Oracle's errors treated that way here. `IMMEDIATE` asks for
  /// the session to go down at once; when it is in a state that cannot be interrupted, Oracle instead FLAGS it to die
  /// shortly and reports that as an error — "session marked for kill". The kill was accepted either way, and the
  /// caller is staging a dead instance rather than timing one, so a death that arrives a moment later is the same
  /// event. Under a five-vendor run the uninterruptible case is common enough to fail the test outright.</remarks>
  public override async Task<bool> TryKillSessionAsync(DataConnection connection) {
    await using var command = connection.DbConnection.CreateCommand();
    command.CommandText =
      "SELECT sid || ',' || serial# FROM v$session WHERE sid = SYS_CONTEXT('USERENV', 'SID')";
    var session = (await command.ExecuteScalarAsync())?.ToString();
    if (session == null)
      return false;

    await using var killer = CreateProvider().CreateConnection(ConnectionString);
    await killer.OpenAsync();
    await using var kill = killer.CreateCommand();
    kill.CommandText = $"ALTER SYSTEM KILL SESSION '{session}' IMMEDIATE";
    try {
      await kill.ExecuteNonQueryAsync();
    } catch (OracleException ex) when (ex.Number == 31) {   // ORA-00031: session marked for kill
    }
    return true;
  }

  public override string ToString() => "Oracle";
}
