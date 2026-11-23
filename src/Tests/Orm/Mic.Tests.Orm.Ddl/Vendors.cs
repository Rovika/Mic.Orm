
namespace Mic.Tests.Orm.Db;

// ONE CLASS PER VENDOR, AND THIS IS THE WHOLE OF IT. Every test in DdlTests — however many files it grows to — runs
// against each of these. Adding a vendor to the suite is three lines.

[TestClass]
public sealed class Postgres_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new PostgresFixture();
}

[TestClass]
public sealed class Sqlite_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new SqliteFixture();
}

[TestClass]
public sealed class MySql_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new MySqlFixture();
}

[TestClass]
public sealed class SqlServer_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new SqlServerFixture();
}

// Oracle runs its container at full CPU for the length of a suite and starves the servers it runs beside into
// connection-open timeouts, so its class runs after the others, by itself.
[TestClass, DoNotParallelize]
public sealed class Oracle_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new OracleFixture();
}

// Db2 runs after the others too: its container is the largest of the set.
[TestClass, DoNotParallelize]
public sealed class Db2_DdlTests : DdlTests {
  protected override ProviderFixture Fixture { get; } = new Db2Fixture();
}
