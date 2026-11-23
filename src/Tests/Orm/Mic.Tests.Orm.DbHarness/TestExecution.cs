using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Tests.Orm.Db;

/// <summary>Run schema statements against a database — the one shape a test needs to create, alter or provoke.</summary>
/// <remarks>A HELPER RATHER THAN `new DdlExecutor(database).ExecuteAsync(…)` AT EACH SITE, because several of them are
/// lambdas handed to `Assert.ThrowsExactlyAsync`, and a test should read as what it asks. The DDL executor rents its
/// schema connection for the run and returns it, so nothing here needs disposing.</remarks>
public static class TestExecution {
  public static Task<DdlResult> RunDdlAsync(this Database database, IReadOnlyList<string> statements,
      string description = "DDL") {
    ArgumentNullException.ThrowIfNull(database);
    return new DdlExecutor(database).ExecuteAsync(statements, null, description);
  }

  public static Task<DdlResult> RunDdlAsync(this Database database, DbUpgradeScript script) {
    ArgumentNullException.ThrowIfNull(database);
    return new DdlExecutor(database).ExecuteAsync(script);
  }

  public static Task<DdlResult> RunDdlAsync(this Database database, IReadOnlyList<DdlStatement> statements,
      string description = "DDL") {
    ArgumentNullException.ThrowIfNull(database);
    return new DdlExecutor(database).ExecuteAsync(statements, null, description);
  }
}
