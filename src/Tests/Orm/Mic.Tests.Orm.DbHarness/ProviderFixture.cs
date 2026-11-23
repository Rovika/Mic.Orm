using Mic.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

/// <summary>What a vendor supplies to the suite: how to reach it, and how to get a database of one's own.</summary>
/// <remarks>ISOLATION IS THE INTERESTING PART, and it is the first place the harness stops pretending the vendors are
/// alike. Every test wants a database nobody else is touching. PostgreSQL gives that as a SCHEMA; SQLite has no
/// schemas at all and gives it as a FILE. Writing the suite against either one directly is what makes a second vendor
/// a rewrite, so tests ask for "somewhere of my own" and the fixture decides what that means.</remarks>
public abstract class ProviderFixture {
  /// <summary>The most connections a pool of the suite may hold, as the value a connection string takes.</summary>
  /// <remarks>Every driver's default is 100, which on PostgreSQL is all the server accepts, so a test process that
  /// leaks connections locks every other client out and fails with the server's "too many clients". Under the cap
  /// it fails with the driver's pool timeout, in the process that leaks, and the server stays reachable. A suite
  /// holds a handful: a vendor's tests run one after another.</remarks>
  public const string MaxPoolSize = "20";

  public abstract DbServerKind ServerKind { get; }
  public abstract DbProvider CreateProvider();

  /// <summary>Somewhere nothing else is using. Disposing it takes it away again.</summary>
  public abstract Task<TestDatabase> CreateIsolatedAsync(string tag);

  /// <summary>Whether this vendor can be reached at all — checked once per class so a missing server is reported as
  /// what it is rather than as every test failing separately.</summary>
  public virtual async Task<string?> ReachableAsync() {
    try {
      await using var database = await CreateIsolatedAsync("probe");
      return null;
    } catch (Exception ex) {
      return ex.Message;
    }
  }

  /// <summary>Kill a session from OUTSIDE, as a crashed process looks to a server. False where the harness has no way
  /// to — which is a fact about the SERVER, not about MIC, so it lives here rather than in <c>DbFeatures</c>.</summary>
  public virtual Task<bool> TryKillSessionAsync(DataConnection connection) => Task.FromResult(false);

  public override string ToString() => ServerKind.ToString();
}

/// <summary>A database of one's own, and the few things a test needs to do to it that MIC itself does not.</summary>
public sealed class TestDatabase : IAsyncDisposable {
  readonly Func<Task> _drop;
  readonly Func<Task> _ensure;

  public TestDatabase(DbSettings settings, string schema, Func<Task> drop, Func<Task>? ensure = null) {
    Settings = settings;
    Schema = schema;
    _drop = drop;
    _ensure = ensure ?? (() => Task.CompletedTask);
  }

  /// <summary>Make the namespace exist without MIC's help — for a test that has to put something there BEFORE MIC
  /// runs. A no-op where the server has no namespaces to make.</summary>
  /// <remarks>MIC does not then trip over it: the upgrade creates a schema only when introspection says it is
  /// missing, so an existing one is simply found.</remarks>
  public Task EnsureSchemaAsync() => _ensure();

  public DbSettings Settings { get; }
  public DbProvider Provider => Settings.Provider;

  /// <summary>The namespace this database's tables live in — EMPTY where the server has none, which is not a special
  /// case anywhere that treats it as a name.</summary>
  public string Schema { get; }

  /// <summary>A table reference, quoted and qualified the way this server wants it. What replaces the hand-written
  /// <c>"schema"."Table"</c> that made every test PostgreSQL-only.</summary>
  public string GetTableRef(string table) => Provider.SqlDialect.QualifiedName(Schema, table);

  public string Quote(string name) => Provider.SqlDialect.Quote(name);

  /// <summary>A Guid as this server takes it in a literal. Not <c>gen_random_uuid()</c> — generating it HERE is both
  /// portable and lets a test know what it wrote.</summary>
  /// <remarks>This is the designated place for that kind of knowledge — the rule is that a test body never asks what
  /// server it is on, not that the harness cannot.
  ///
  /// THREE OF THE FIVE STORE 16 BYTES, so a literal is hex rather than quoted text, and Oracle needs a function where
  /// the other two have a syntax for it. The bytes are in RFC order, which is the order the ORM writes — writing them
  /// in .NET's order would produce a literal that matches nothing, silently, since a `SELECT` that finds no row and a
  /// column that is null look identical.
  ///
  /// It used to say something else here: SQLite held a Guid as TEXT and `Microsoft.Data.Sqlite` wrote it upper-cased
  /// where every other driver wrote it lower, which mattered because comparison on TEXT is case-sensitive. Binary
  /// storage has no case, and that whole hazard is gone.</remarks>
  public string GuidLiteral(Guid value) {
    if (!Provider.Supports(DbFeatures.BinaryGuids))
      return "'" + value + "'";
    var hex = Convert.ToHexString(ConvertHelper.GuidToBytes(value));
    return Provider.ServerKind switch {
      DbServerKind.Oracle => $"HEXTORAW('{hex}')",
      DbServerKind.Db2 => $"BX'{hex}'",
      _ => $"x'{hex}'",
    };
  }

  // ----- raw access, for the things MIC cannot do yet ---------------------------------------------------------------

  public async Task<object?> ScalarAsync(string sql) {
    await using var connection = Settings.Provider.CreateConnection(Settings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    var value = await command.ExecuteScalarAsync();
    return value == DBNull.Value ? null : value;
  }

  /// <summary>A truth value however this driver hands one back — a bool, a number, or a one-byte integer, which is
  /// what a server with no boolean type returns.</summary>
  public async Task<bool> ScalarBoolAsync(string sql) => (await ScalarAsync(sql)) switch {
    null => false,
    bool value => value,
    string text => text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase),
    var other => Convert.ToInt64(other, System.Globalization.CultureInfo.InvariantCulture) != 0,
  };

  /// <summary>One column of every row, in the order the server returned them — for an assertion about ORDER, where a
  /// scalar cannot say anything.</summary>
  public async Task<List<string?>> ColumnAsync(string sql) {
    await using var connection = Settings.Provider.CreateConnection(Settings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    var values = new List<string?>();
    while (await reader.ReadAsync())
      values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString());
    return values;
  }

  public async Task<int> ExecuteAsync(string sql) {
    await using var connection = Settings.Provider.CreateConnection(Settings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return await command.ExecuteNonQueryAsync();
  }

  /// <summary>What the server says is there — MIC's own loader, which is the only portable answer and the one that
  /// makes an assertion mean the same thing on every vendor.</summary>
  public Task<DbModel> LoadAsync(DbModelParts parts = DbModelParts.Default) =>
    Provider.CreateDbModelLoader(Settings).LoadModelAsync(Schema.Length == 0 ? null : [Schema], parts);

  public async Task<bool> TableExistsAsync(string table) =>
    (await LoadAsync()).FindTable(Schema, table) != null;

  public async ValueTask DisposeAsync() {
    try {
      await _drop();
    } catch (Exception) {
      // A test that already failed should report ITS failure, not a cleanup that failed after it.
    }
  }
}
