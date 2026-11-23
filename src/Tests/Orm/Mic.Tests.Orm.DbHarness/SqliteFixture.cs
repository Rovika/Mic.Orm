using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;

namespace Mic.Tests.Orm.Db;

/// <summary>SQLite, which needs nothing installed and nothing running.</summary>
/// <remarks>A database of one's own is a FILE here, not a schema — which is better isolation than PostgreSQL's and
/// costs less. It is also the reason the harness asks the fixture for "somewhere of my own" instead of asking for a
/// schema: the two vendors answer differently and the suite never learns which.
///
/// A file rather than <c>:memory:</c> because MIC opens several connections — the executor's, the loader's, the
/// upgrade's — and an in-memory database is private to one connection unless shared through a URI, which would be
/// solving a problem the file does not have.</remarks>
public sealed class SqliteFixture : ProviderFixture {
  public override DbServerKind ServerKind => DbServerKind.SQLite;
  public override DbProvider CreateProvider() => new SqliteProvider();

  public override Task<TestDatabase> CreateIsolatedAsync(string tag) {
    var path = Path.Combine(Path.GetTempPath(), "mic_tests",
      $"{Sanitise(tag)}_{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    // No "Foreign Keys=True": the provider turns them on, and the suite's foreign-key violation test proves it does.
    var settings = new DbSettings(CreateProvider(), $"Data Source={path}");

    // No schema: the one namespace has no name, and Schema being EMPTY is what every other part of MIC already
    // handles rather than a special case invented here.
    return Task.FromResult(new TestDatabase(settings, string.Empty, () => Delete(path)));
  }

  static Task Delete(string path) {
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();     // the file stays locked until the pool lets go
    foreach (var file in new[] { path, path + "-wal", path + "-shm" })
      if (File.Exists(file))
        File.Delete(file);
    return Task.CompletedTask;
  }

  static string Sanitise(string tag) {
    var clean = new string(tag.Where(char.IsLetterOrDigit).ToArray());
    return clean.Length <= 24 ? clean : clean[..24];
  }

  public override string ToString() => "SQLite";
}
