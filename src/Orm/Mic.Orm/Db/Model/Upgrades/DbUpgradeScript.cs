using System.Text;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Represents work that runs between schema changes, such as filling a new column or reshaping data a type
/// change would otherwise reject.</summary>
/// <remarks>A migration attaches to a stage and runs after that stage completes.</remarks>
public sealed class DbMigration {
  public DbMigration(string name, DbUpgradeStage after, string sql, string? description = null) {
    Name = name;
    After = after;
    Sql = sql;
    Description = description;
  }

  public string Name { get; }

  /// <summary>Gets the stage this migration runs after.</summary>
  /// <remarks>A migration that fills a new required column attaches to <see cref="DbUpgradeStage.AddColumns"/>.
  /// </remarks>
  public DbUpgradeStage After { get; }

  public string Sql { get; }
  public string? Description { get; }

  public override string ToString() => Name;
}

/// <summary>Represents one step of an upgrade: a DDL statement or a migration, never both.</summary>
public sealed class DbUpgradeStep {
  DbUpgradeStep(DbUpgradeStage stage, DdlStatement? ddl, DbMigration? migration, DbObjectDiffInfo? change) {
    Stage = stage;
    Ddl = ddl;
    Migration = migration;
    Change = change;
  }

  public static DbUpgradeStep ForDdl(DbUpgradeStage stage, DdlStatement ddl, DbObjectDiffInfo? change) =>
    new(stage, ddl, null, change);

  public static DbUpgradeStep ForMigration(DbMigration migration) =>
    new(migration.After, null, migration, null);

  public DbUpgradeStage Stage { get; }
  public DdlStatement? Ddl { get; }
  public DbMigration? Migration { get; }

  /// <summary>Gets the change this step came from, or <see langword="null"/> for a migration and for the pieces of a
  /// new table that no change of their own describes.</summary>
  public DbObjectDiffInfo? Change { get; }

  /// <summary>Gets a value that indicates whether this step loses data.</summary>
  /// <remarks>The answer comes from the change, not the statement: a key dropped in order to be rebuilt is not
  /// destructive.</remarks>
  public bool IsDestructive => Change?.IsDestructive == true;

  public string Text => Ddl?.Text ?? Migration?.Sql ?? "";

  public override string ToString() => Text;
}

/// <summary>Represents everything an upgrade would do, in order, with the migrations in place.</summary>
/// <remarks>A script runs outside any transaction. A failure part-way leaves the steps before it applied, and the
/// next run compares the model against the database again and produces the remaining work.</remarks>
public sealed class DbUpgradeScript {
  readonly List<DbUpgradeStep> _steps = new();

  public DbUpgradeScript(SqlDialect dialect) {
    Dialect = dialect;
  }

  public SqlDialect Dialect { get; }

  public IReadOnlyList<DbUpgradeStep> Steps => _steps;
  public bool IsEmpty => _steps.Count == 0;

  /// <summary>Gets the warnings about what the script cannot do for itself, such as a required column whose type has
  /// no placeholder to fill existing rows with.</summary>
  public List<string> Warnings { get; } = new();

  public IEnumerable<DdlStatement> Statements => _steps.Where(s => s.Ddl != null).Select(s => s.Ddl!);
  public IEnumerable<DbMigration> Migrations => _steps.Where(s => s.Migration != null).Select(s => s.Migration!);

  /// <summary>Gets the steps that lose data.</summary>
  public IEnumerable<DbUpgradeStep> Destructive => _steps.Where(s => s.IsDestructive);

  public bool HasDestructive => _steps.Any(s => s.IsDestructive);

  public void Add(DbUpgradeStep step) => _steps.Add(step);

  /// <summary>Renders the whole script as one text, with each stage and each migration marked by a comment.</summary>
  /// <returns>The script as text.</returns>
  public string ToText() {
    var text = new StringBuilder();
    DbUpgradeStage? current = null;

    foreach (var step in _steps) {
      if (current != step.Stage) {
        current = step.Stage;
        if (text.Length > 0)
          text.AppendLine();
        text.AppendLine($"{Dialect.LineComment} ----- {step.Stage} -----");
      }
      if (step.Migration != null)
        text.AppendLine($"{Dialect.LineComment} migration: {step.Migration.Name}"
          + (step.Migration.Description == null ? "" : $" — {step.Migration.Description}"));
      else if (step.Change?.Description != null)
        text.AppendLine($"{Dialect.LineComment} {step.Change.Name}: {step.Change.Description}");
      text.AppendLine(step.Text + Dialect.StatementEnd);
    }
    return text.ToString();
  }

  public override string ToString() => $"{_steps.Count} steps" + (HasDestructive ? " (destructive)" : "");
}
