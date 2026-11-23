using Mic.Orm.Db;

namespace Mic.Orm.Model;

/// <summary>Represents how the database computes a member declared with <c>[DbComputed]</c>: the SQL expression
/// written for each server, over the row's other columns. This class cannot be inherited.</summary>
/// <param name="SqlByServer">The expression by server kind; a member of the entity written as <c>{Name}</c> stands
/// for its column.</param>
/// <param name="Stored"><see langword="true"/> if the server is to store the value rather than compute it on each
/// read; otherwise, <see langword="false"/>.</param>
public sealed record DbComputedInfo(IReadOnlyDictionary<DbServerKind, string> SqlByServer, bool Stored) {
  /// <summary>Finds the expression written for a server kind.</summary>
  /// <param name="server">The server kind.</param>
  /// <returns>The expression, or <see langword="null"/> if none was written for the server.</returns>
  public string? FindSql(DbServerKind server) => SqlByServer.TryGetValue(server, out var sql) ? sql : null;
}
