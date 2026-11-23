using System.Data.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm;

/// <summary>Base for the provider doubles in this assembly — invented "vendors" that exist to prove behaviour is
/// declared rather than coded.</summary>
/// <remarks>They describe a server and can never reach one, which is the whole point of this suite: it must stay
/// runnable with no database anywhere. Anything that needs a live server lives in <c>Mic.Tests.Orm.Db</c>.</remarks>
public abstract class ModelOnlyProvider(DbServerKind serverKind, DbTypeRegistry typeRegistry, SqlDialect? sqlDialect = null)
    : DbProvider(serverKind, sqlDialect ?? new SqlDialect(), typeRegistry) {
  public override DbConnection CreateConnection(string connectionString) => throw NoDriver();

  public override DbCommand CreateDbCommand() => throw NoDriver();

  NotSupportedException NoDriver() =>
    new($"{GetType().Name} is a model-only test double and has no driver.");
}
