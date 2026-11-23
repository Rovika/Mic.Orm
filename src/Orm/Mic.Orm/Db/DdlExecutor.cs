using System.Data.Common;
using System.Diagnostics;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Logging;

namespace Mic.Orm.Db;

/// <summary>Runs schema statements on the schema-management connection, one after another and outside any
/// transaction.</summary>
/// <remarks>A failure leaves the statements before it applied; the next run carries on from there.</remarks>
public class DdlExecutor {
  public Database Database { get; }

  public DdlExecutor(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    Database = database;
  }

  /// <summary>Asynchronously runs an upgrade script's steps, DDL statements and migrations alike, in their order.
  /// </summary>
  public Task<DdlResult> ExecuteAsync(DbUpgradeScript script, OperationContext? context = null,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(script);
    return ExecuteAsync([.. script.Steps.Select(s => s.Text)], context, $"upgrade ({script.Steps.Count} steps)", cancellation);
  }

  /// <summary>Asynchronously runs a model creation's statements in their order.</summary>
  public Task<DdlResult> ExecuteAsync(IReadOnlyList<DdlStatement> statements, OperationContext? context = null,
      string description = "DDL", CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(statements);
    return ExecuteAsync([.. statements.Select(s => s.Text)], context, description, cancellation);
  }

  /// <summary>Asynchronously runs statements in their order.</summary>
  /// <param name="statements">The statements to run.</param>
  /// <param name="context">The operation the run is part of, for the log, or <see langword="null"/> to log nothing.
  /// </param>
  /// <param name="description">The name of the run, used in the log and in a failure's message.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the statement count and
  /// the elapsed time.</returns>
  /// <exception cref="DataAccessException">a statement fails. The message names its position and text; the
  /// statements before it stay applied.</exception>
  public async Task<DdlResult> ExecuteAsync(IReadOnlyList<string> statements, OperationContext? context = null,
      string description = "DDL", CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(statements);
    var started = Stopwatch.GetTimestamp();
    var logging = context is { LogMode: not LogMode.None };
    var index = -1;
    DbCommand? dbCommand = null;
    await using var connection = Database.CreateConnection(forSchema: true);
    try {
      await connection.OpenAsync(RunMode.Async, cancellation);
      for (index = 0; index < statements.Count; index++) {
        dbCommand = Database.Provider.CreateDbCommand();
        dbCommand.Connection = connection.DbConnection;
        dbCommand.CommandText = statements[index];
        var startedAt = Stopwatch.GetTimestamp();
        var rowCount = await dbCommand.ExecuteNonQueryAsync(cancellation);
        if (logging)
          context!.Log(new DbCommandLogEntry(context.LogSource, context.Now, dbCommand, DbExecutionType.NonQuery,
            rowCount, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds));
        dbCommand.Dispose();
        dbCommand = null;
      }
    } catch (DbException ex) {
      var where = dbCommand == null ? "while opening the connection" : $"at command {index + 1} of {statements.Count}";
      var summary = dbCommand == null ? null : DbCommandHelper.Describe(dbCommand);
      var failure = Database.Provider.Fail(
        $"Database '{Database.Settings.DatabaseAlias}': {description} failed {where}. {ex.Message}"
        + (summary == null ? "" : Environment.NewLine + summary),
        ex, Database.Settings.DatabaseAlias);
      failure.CommandSummary = summary;
      context?.LogError($"{description} failed: {failure.Message}", failure);
      throw failure;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // A database that cannot be reached fails inside OpenAsync, already as a DataAccessException.
      context?.LogError($"{description} failed: {ex.Message}", ex);
      throw;
    } finally {
      dbCommand?.Dispose();
    }
    return new DdlResult(statements.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
  }
}

/// <summary>Represents the outcome of a DDL run: the number of statements and the elapsed time, in milliseconds.
/// </summary>
public sealed record DdlResult(int StatementCount, double TotalMs) {
  public override string ToString() => $"{StatementCount} statement(s), {TotalMs:F1} ms";
}
