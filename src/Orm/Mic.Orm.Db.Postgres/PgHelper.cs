using System.Data.Common;
using Mic.Orm.Db.Model;

namespace Mic.Orm.Db.Postgres;

/// <summary>Provides PostgreSQL maintenance operations that a developer or an administrator runs on demand.</summary>
/// <remarks>Each operation takes the <see cref="Database"/> to work on as a parameter; none of them runs as part of
/// the schema upgrade.</remarks>
public static class PgHelper {
  /// <summary>Asynchronously orders each table's rows by the index the model marks as clustered and reports what was
  /// done.</summary>
  /// <remarks>For each marked table this runs <c>ALTER TABLE … CLUSTER ON …</c> and then <c>CLUSTER</c>, which
  /// takes an ACCESS EXCLUSIVE lock and rewrites the whole table, so it belongs in a maintenance window. PostgreSQL
  /// does not maintain the order afterwards: rows inserted later land wherever there is room, and a later call
  /// repeats the reorder. When the model marks no index as clustered, nothing is done.</remarks>
  /// <param name="database">The database whose tables are reordered.</param>
  /// <param name="cancellation">A token to observe while waiting for the statements to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains one line per table that
  /// was reordered, naming the table and the index.</returns>
  public static async Task<IReadOnlyList<string>> ApplyClusteringAsync(Database database,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(database);
    var dialect = database.Provider.SqlDialect;
    var done = new List<string>();

    var clustered = database.DbModel.Tables
      .Select(t => (Table: t, Key: t.Keys.FirstOrDefault(k => k.Flags.HasFlag(KeyFlags.Clustered))))
      .Where(pair => pair.Key != null)
      .ToList();
    if (clustered.Count == 0)
      return done;

    await using var connection = database.CreateConnection(forSchema: true);
    await connection.OpenAsync(RunMode.Async, cancellation);

    foreach (var (table, key) in clustered) {
      if (dialect.ClusterTable(table, key!) is not { } mark)
        continue;                                 // a dialect with no cluster statement
      await ExecuteAsync(mark, cancellation);
      await ExecuteAsync($"CLUSTER {dialect.GetTableRef(table)}", cancellation);
      done.Add($"{table.Schema.Name}.{table.Name} clustered on {key!.Name}");
    }
    return done;

    async Task ExecuteAsync(string sql, CancellationToken token) {
      using var command = database.Provider.CreateDbCommand();
      command.Connection = connection.DbConnection;
      command.CommandText = sql;
      try {
        await command.ExecuteNonQueryAsync(token);
      } catch (DbException ex) {
        throw database.Provider.Fail(
          $"Database '{database.Alias}': clustering failed. {ex.Message}{Environment.NewLine}{sql}", ex,
          database.Alias);
      }
    }
  }
}
