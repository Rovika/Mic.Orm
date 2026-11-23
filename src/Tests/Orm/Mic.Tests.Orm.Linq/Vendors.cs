using Mic.Tests.Orm.Db;

namespace Mic.Tests.Orm.Linq;

// ONE CLASS PER VENDOR, AND THIS IS THE WHOLE OF IT. Every test in LinqTests — however many files it grows to — runs
// against each of these. Adding a vendor to the suite is three lines.

[TestClass]
public sealed class Postgres_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new PostgresFixture();
}

[TestClass]
public sealed class Sqlite_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new SqliteFixture();
}

[TestClass]
public sealed class MySql_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new MySqlFixture();
}

[TestClass]
public sealed class SqlServer_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new SqlServerFixture();
}

// Oracle runs its container at full CPU for the length of a suite and starves the servers it runs beside into
// connection-open timeouts, so its class runs after the others, by itself.
[TestClass, DoNotParallelize]
public sealed class Oracle_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new OracleFixture();
}

/// <summary>Drops every shared schema when the run ends.</summary>
/// <remarks>ITS OWN `[TestClass]` BECAUSE MSTEST REQUIRES ONE — `[AssemblyCleanup]` is only discovered inside a class
/// marked as such, and the per-vendor classes above are the test classes while `LinqTests` is abstract. The DML suite
/// learned this the expensive way: its hook sat on the abstract base, was never called, and every run left its shared
/// schemas behind on every server.</remarks>
[TestClass]
public static class SuiteCleanup {
  [AssemblyCleanup]
  public static Task DropSharedDatabasesAsync() => LinqTests.DropSharedDatabasesAsync();
}

// Db2 runs after the others too: its container is the largest of the set.
[TestClass, DoNotParallelize]
public sealed class Db2_LinqTests : LinqTests {
  protected override ProviderFixture Fixture { get; } = new Db2Fixture();
}
