using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// SERVER VALUES: the session answers the few questions about the server that have one meaning on every vendor, each
// through the dialect's own statement — the time in UTC, the connection's user, the database it is in.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task ServerValues_AreReadThroughTheSession() {
    await using var session = NewSession();

    // ----- the time is the server's clock, UTC, and close to ours -------------------------------------------------
    var before = DateTime.UtcNow;
    var serverTime = await session.GetServerTimeUtcAsync();
    Assert.AreEqual(DateTimeKind.Utc, serverTime.Kind);
    var drift = (serverTime - before).Duration();
    Assert.IsTrue(drift < TimeSpan.FromMinutes(5), $"the server's clock is {drift} from ours");

    // ----- the user, where the server has users; refused by name where it has none --------------------------------
    if (Db.Provider.ServerKind == DbServerKind.SQLite) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.GetServerUserAsync());
      StringAssert.Contains(refused.Message, "user");
    } else {
      var user = await session.GetServerUserAsync();
      Assert.IsFalse(string.IsNullOrWhiteSpace(user), "the connection runs as somebody");
    }

    // ----- the database the connection is in; the MySQL harness connects with no default schema, so none -----------
    var database = await session.GetServerDatabaseAsync();
    if (Db.Provider.ServerKind == DbServerKind.MySql)
      Assert.IsNull(database, "the harness names no database");
    else
      Assert.IsFalse(string.IsNullOrWhiteSpace(database), "the connection is in a database");
  }
}
