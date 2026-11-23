using System.Data.Common;
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Readers;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// FIRST CONTACT, and the seams around it: connecting, classifying a failure, and reading rows back.
public abstract partial class DmlTests {
  static class Conn {
    public interface IWidget {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string Name { get; set; }
    }

    public interface IParent {
      [PrimaryKey] Guid Id { get; set; }
      [Unique][Size(30)] string Code { get; set; }
    }

    public interface IChild {
      [PrimaryKey] Guid Id { get; set; }
      IParent Parent { get; set; }
      [Size(20)] string Label { get; set; }
    }
  }

  // ----- connecting ------------------------------------------------------------------------------------------------

  [TestMethod]
  public async Task Connectivity_IsVerifiedUpFront() {
    // A bad connection string is a configuration problem with an obvious owner; the same failure arriving on
    // somebody's first request is an incident.
    //
    // It also RUNS a statement rather than only opening — see `Database.CheckConnectivityAsync` — which is what this
    // exercises per vendor: `SqlDialect.SelectOne` has to be valid SQL on every server in the run, and Oracle's needs
    // a `FROM dual` that nothing else does. A dialect that got it wrong fails right here.
    await SharedDatabase.CheckConnectivityAsync();
  }

  [TestMethod]
  public async Task AConnectionOpensAndCloses() {
    var database = SharedDatabase;
    await using var connection = database.CreateConnection();

    Assert.IsFalse(connection.IsOpen);
    await connection.OpenAsync(RunMode.Async);
    Assert.IsTrue(connection.IsOpen);
    await connection.CloseAsync(RunMode.Async);
    Assert.IsFalse(connection.IsOpen);
  }

  [TestMethod]
  public async Task ATransactionCommitsAndAborts() {
    var database = SharedDatabase;
    await using var connection = database.CreateConnection();
    await connection.OpenAsync(RunMode.Async);

    await connection.BeginTransactionAsync(RunMode.Async, commitOnSave: true);
    Assert.IsNotNull(connection.DbTransaction);
    Assert.IsTrue(connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave));

    // A connection carrying a transaction cannot be closed underneath it; the lifetime is not touched.
    Assert.AreEqual(DbConnectionLifetime.Operation, connection.Lifetime);
    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connection.CloseAsync(RunMode.Async));

    await connection.CommitAsync(RunMode.Async);
    Assert.IsNull(connection.DbTransaction);
    Assert.IsFalse(connection.Flags.HasFlag(DbConnectionFlags.CommitOnSave));

    await connection.BeginTransactionAsync(RunMode.Async, commitOnSave: false);
    await connection.AbortAsync(RunMode.Async);
    Assert.IsNull(connection.DbTransaction);
  }

  // ----- what KIND of failure it was -------------------------------------------------------------------------------

  /// <summary>Run a statement the application would, through a session of its own, and hand back the failure it
  /// was classified into.</summary>
  async Task<DataAccessException> ProvokeAsync(string sql, params (string Name, object? Value)[] parameters) {
    await using var session = NewSession();
    return await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.RunSqlNonQueryAsync(sql, parameters));
  }

  /// <summary>Four ways to fail, four SubTypes — against every vendor, which is the whole point.</summary>
  /// <remarks>The model-only suite proves the SQLSTATE table; only a real server proves that each of these actually
  /// produces the state the table maps. Four methods, each provoking one failure, and provoking is what costs.</remarks>
  [TestMethod]
  public async Task EachFailureKind_HasItsOwnSubType() {
    // ----- a UNIQUE violation ---------------------------------------------------------------------------------------
    //
    // With two unique keys on a table, only the name tells an application which one the user has to fix. A CODE OF ITS
    // OWN, because the database is shared: a fixed literal would already be there from the last run of this test and
    // the FIRST insert would be the one that failed.
    var code = "dup-" + Guid.NewGuid().ToString("N")[..8];
    var insert = $"INSERT INTO {Db.GetTableRef("Parent")} ({Db.Quote("Id")}, {Db.Quote("Code")}) VALUES (@id, @code)";
    await using var writer = NewSession();
    await writer.RunSqlNonQueryAsync(insert, ("id", Guid.NewGuid()), ("code", code));
    var duplicate = await ProvokeAsync(insert, ("id", Guid.NewGuid()), ("code", code));

    Assert.AreEqual(DataAccessException.SubTypeUniqueViolation, duplicate.SubType);

    // ----- a missing required value is a NOT NULL violation ------------------------------------------------------------
    var missing = await ProvokeAsync(
      $"INSERT INTO {Db.GetTableRef("Parent")} ({Db.Quote("Id")}) VALUES (@id)", ("id", Guid.NewGuid()));
    Assert.AreEqual(DataAccessException.SubTypeNotNullViolation, missing.SubType);

    // ----- while BAD SQL is not mistaken for a data problem at all --------------------------------------------------------
    //
    // The distinction that matters for an upgrade: a fault in MIC or in a hand-written migration, rather than something
    // about the rows.
    var broken = await ProvokeAsync($"SELECT * FROM {Db.GetTableRef("NoSuchTable")}");
    Assert.AreEqual(DataAccessException.SubTypeSyntaxOrMissingObject, broken.SubType);

    // ----- and a FOREIGN KEY violation is an integrity violation, wherever there are foreign keys ---------------------------
    //
    // Last, and gated on its own: a server without referential constraints takes this insert happily, and the three
    // above have nothing to do with that.
    if (!Db.Provider.Supports(DbFeatures.ReferentialConstraints))
      return;

    var orphan = await ProvokeAsync(
      $"INSERT INTO {Db.GetTableRef("Child")} ({Db.Quote("Id")}, {Db.Quote("Parent_Id")}, {Db.Quote("Label")}) "
      + "VALUES (@id, @parent, 'x')", ("id", Guid.NewGuid()), ("parent", Guid.NewGuid()));

    Assert.AreEqual(DataAccessException.SubTypeIntegrityViolation, orphan.SubType);
  }

  [TestMethod]
  public async Task UnreachableServer_IsAConnectionFailure() {
    // Whatever the server said, failing to get IN is a connection failure — the caller only needs to know it never
    // arrived.
    var settings = new DbSettings(Db.Provider, Db.Provider.ServerKind switch {
      DbServerKind.Postgres => "Host=localhost;Port=1;Database=nope;Username=nobody;Password=nothing;Timeout=2",
      _ => "Data Source=/nowhere-at-all/nope.db;Mode=ReadWrite",
    }, databaseAlias: "unreachable");

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => new Database(SharedDatabase.DbModel, settings).CheckConnectivityAsync());
    Assert.AreEqual(DataAccessException.SubTypeConnectionFailed, ex.SubType);
  }

  [TestMethod]
  public async Task AMissingDatabase_IsConnectedByTheHandlerOnFirstUse() {
    // A tenant's database the application learns of when a request names it: the session opens on an alias nothing
    // is registered under, the handler looks the alias up and connects, and the request runs on the new database.
    // The "new" database here is this vendor's own under a second alias, so the upgrade finds nothing to apply.
    var alias = "tenant-" + Guid.NewGuid().ToString("N")[..8];
    var connected = new List<string>();
    void Connect(EntityApp app, string name) {
      connected.Add(name);
      app.ConnectTo(new DbSettings(Db.Settings.Provider, Db.Settings.ConnectionString, databaseAlias: name) {
        Options = Db.Settings.Options,
      });
    }
    App.DatabaseEvents.DatabaseMissing += Connect;
    try {
      var context = NewContext(App);
      context.DatabaseAlias = alias;
      var session = context.OpenSession();
      var tenantDb = ((EntitySession)session).Database;
      Assert.AreEqual(alias, tenantDb.Alias);
      Assert.AreSame(SharedDatabase.DbModel, tenantDb.DbModel, "the same provider and options: the model is shared");
      Assert.IsFalse(tenantDb.UpgradeReport!.Changes!.HasItems,
        "the same schema behind the alias, nothing to apply; found: " + string.Join("; ", tenantDb.UpgradeReport.Changes.Items));

      // The request runs on the database the handler connected.
      var memo = session.NewEntity<Dml.IMemo>();
      memo.Id = Guid.NewGuid();
      memo.Text = alias;
      await session.SaveChangesAsync();
      Assert.AreEqual(1, await NewContext(App).OpenSession().EntitySet<Dml.IMemo>().Where(m => m.Text == alias).CountAsync(),
        "one physical database behind the two aliases");

      CollectionAssert.AreEqual(new[] { alias }, connected, "the handler ran once");
    } finally {
      App.DatabaseEvents.DatabaseMissing -= Connect;
      App.DataAccess.RemoveDatabase(alias);
    }
  }

  // ----- readers ----------------------------------------------------------------------------------------------------

  /// <summary>A statement as the application would send one, run over a session of its own.</summary>
  async Task<DataRequest> QueryAsync(IResultReader? resultReader, string sql,
      DbConnectionReuseMode mode = DbConnectionReuseMode.KeepOpen) {
    var session = NewSession();
    session.Context.ConnectionReuseMode = mode;
    var request = new DataRequest(session, sql, [], DbExecutionType.Reader, resultReader);
    await session.Database.RequestExecutor.ExecuteAsync(request);
    return request;
  }

  /// <summary>Three rows of one integer column, however this server spells that.</summary>
  string ThreeRows => Db.Provider.ServerKind switch {
    DbServerKind.Postgres => "SELECT n FROM generate_series(1, 3) AS n",
    DbServerKind.Db2 => "SELECT n FROM (VALUES 1, 2, 3) AS t (n)",
    _ => "SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3",
  };

  /// <summary>One row of one integer column: a select reads a table always on Db2.</summary>
  string OneRow => Db.Provider.ServerKind == DbServerKind.Db2
    ? "SELECT 42 AS n FROM SYSIBM.SYSDUMMY1"
    : "SELECT 42 AS n";

  /// <summary>What comes back from a reader, and the two regressions that made it come back DEAD.</summary>
  /// <remarks>Both were the same mistake: the executor handed the caller a LIVE reader, and then something closed it —
  /// the connection on the way out, or the no-MARS guard when the next command started. Either way the caller received
  /// something already disposed, with no way to tell.</remarks>
  [TestMethod]
  public async Task Rows_AreBufferedAndSurviveAClosedReader() {
    var database = SharedDatabase;

    // ----- the ordinary case ----------------------------------------------------------------------------------------
    var rows = (await QueryAsync(null, ThreeRows)).DataCommands.Single().Result as InfoTable;
    Assert.IsNotNull(rows, "a reader command produced no rows");
    Assert.AreEqual(3, rows.Rows.Count);

    // ----- a second reader on the same connection no longer throws away the ONE BEFORE IT ---------------------------
    var session = NewSession();
    var executor = session.Database.RequestExecutor;
    var first = (InfoTable)(await executor.ExecuteAsync(new DataRequest(session, ThreeRows, [], DbExecutionType.Reader)))!;
    var second = (InfoTable)(await executor.ExecuteAsync(new DataRequest(session, OneRow, [], DbExecutionType.Reader)))!;
    Assert.AreEqual(3, first.Rows.Count);
    Assert.AreEqual(1, second.Rows.Count);

    // ----- and NoReuse no longer destroys the result either ------------------------------------------------------------
    // The mode is the context's, so this session alone runs under it.
    var unreused = (await QueryAsync(null, ThreeRows, DbConnectionReuseMode.NoReuse)).DataCommands.Single().Result as InfoTable;
    Assert.IsNotNull(unreused);
    Assert.AreEqual(3, unreused.Rows.Count);
  }

  sealed class SumReader : IResultReader {
    public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader,
        CancellationToken cancellation) {
      var total = 0L;
      while (await reader.ReadAsync(cancellation))
        total += Convert.ToInt64(reader.GetValue(0));
      return total;
    }
  }

  [TestMethod]
  public async Task ResultReader_SeesTheOpenReader() {
    // Where result shaping lives — on the command, driven by the executor. Entity materialisation is a reader of
    // this shape, which is the whole reason the seam exists rather than buffering everything.
    var result = await QueryAsync(new SumReader(), ThreeRows);
    Assert.AreEqual(6L, result.DataCommands.Single().Result);

    // ----- and the connection is USABLE again afterwards --------------------------------------------------------------
    var session = NewSession();
    var executor = session.Database.RequestExecutor;

    await executor.ExecuteAsync(new DataRequest(session, ThreeRows, [], DbExecutionType.Reader));
    Assert.IsNotNull(session.Connection);
    Assert.IsNull(session.Connection.ActiveReader, "a reader was left open");

    var second = await executor.ExecuteAsync(new DataRequest(session, OneRow, [], DbExecutionType.Reader));
    Assert.AreEqual(1, ((InfoTable)second!).Rows.Count);
  }

  [TestMethod]
  public async Task AUserWithoutTheLockPrivilege_IsWarnedAndUpgradesUncoordinated() {
    // Oracle's session lock is DBMS_LOCK, a grant an ordinary user does not have; the upgrade says so and goes on.
    if (Db.Provider.ServerKind != DbServerKind.Oracle)
      Assert.Inconclusive("The session lock needs a grant on Oracle alone.");
    const string user = "MIC_NOGRANT";
    await using var admin = NewSession();
    // A user an earlier run left behind is dropped first; the block swallows "does not exist".
    await admin.RunSqlNonQueryAsync(
      $"BEGIN EXECUTE IMMEDIATE 'DROP USER {user} CASCADE'; EXCEPTION WHEN OTHERS THEN NULL; END;");
    await admin.RunSqlNonQueryAsync($"CREATE USER {user} IDENTIFIED BY mic QUOTA UNLIMITED ON USERS");
    await admin.RunSqlNonQueryAsync($"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE TO {user}");
    try {
      var app = BuildApp("NoGrant", new Version(1, 0), a => a.Settings.DefaultSchema = user,
        typeof(Dml.IParent), typeof(Dml.IChild));
      var connectionString = Db.Settings.ConnectionString
        .Replace("User Id=system;Password=mic", $"User Id={user};Password=mic", StringComparison.OrdinalIgnoreCase)
        + ";Pooling=false";                         // its sessions end with the application, so it can be dropped
      var settings = new DbSettings(Db.Provider, connectionString) { Options = Db.Settings.Options };
      var report = await app.ConnectToAsync(settings);
      Assert.IsFalse(report.HasErrors, report.Report());
      var warned = report.Messages.Any(m => m.Severity == Severity.Warning && m.Message.Contains("DBMS_LOCK"));
      Assert.IsTrue(warned, report.Report());
      Assert.IsTrue(app.IsConnected);
      await app.ShutdownAsync();
    } finally {
      await admin.RunSqlNonQueryAsync($"DROP USER {user} CASCADE");
    }
  }

  [TestMethod]
  public async Task GuidScheme_IsRecordedAndAConnectUnderAnotherWarns() {
    // The suite's own connect applied the DDL, so the scheme its keys were minted under is in the DbInfo row.
    var inForce = SharedDatabase.GuidScheme;
    Assert.AreNotEqual(GuidScheme.Auto, inForce);
    Assert.AreEqual(inForce.ToString(), SharedDatabase.DbInfo?.Details.GuidScheme);

    // The same model under a scheme set by hand: nothing to apply, so nothing is rewritten, and the report says the
    // table would hold both layouts.
    var app = new DmlApp(Db.Schema);
    app.Settings.GuidScheme = GuidScheme.V4;
    app.Init();
    var report = await app.ConnectToAsync(Db.Settings);
    Assert.IsFalse(report.HasErrors, report.Report());
    Assert.IsTrue(report.Messages.Any(m => m.Severity == Severity.Warning && m.Message.Contains($"{inForce}")
      && m.Message.Contains("V4")), report.Report());
    Assert.AreEqual(GuidScheme.V4, app.DataAccess.FindDatabase()!.GuidScheme);
    var session = (EntitySession)app.OpenSession();
    Assert.AreEqual(4, session.SessionId.Version, "the session mints under the scheme set by hand");
    await app.ShutdownAsync();
  }
}
