using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Compares two db models and produces the differences between them.</summary>
/// <remarks>Objects are matched by name, ignoring case, in two passes: current names first, then old names against
/// what is still unmatched. An object that matches by its own name is never a rename, whatever its old names say.
/// </remarks>
public class DbModelComparer {
  public DbModelComparer(DbProvider provider) {
    Provider = provider;
  }

  protected DbProvider Provider { get; }

  /// <summary>Compares a source model with a target model.</summary>
  /// <param name="source">The model the comparison runs from, such as the database as it is.</param>
  /// <param name="target">The model the comparison runs to, such as what the application wants.</param>
  /// <returns>The differences that turn the source into the target.</returns>
  public virtual DbModelDiffInfo Compare(DbModel source, DbModel target) {
    var diff = new DbModelDiffInfo();
    CompareSchemas(diff, source, target);
    CompareSequences(diff, source, target);
    CompareTables(diff, source, target);
    CompareViews(diff, source, target);
    RecreateViewsOverChangedColumns(diff, source, target);
    RecreateDependentViews(diff, source, target.Tables.Where(t => t.IsView).ToList());
    CompareTableFunctions(diff, source, target);
    return diff;
  }

  // ----- table functions --------------------------------------------------------------------------------------------

  /// <summary>Compares the table functions the model declares with the routines read from the database: a managed
  /// function is added, or recreated where its stored hash differs from its body's; a function declared as existing
  /// is checked for and never changed; a function MIC made and the model no longer declares is dropped, and any
  /// other routine is left alone.</summary>
  protected virtual void CompareTableFunctions(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    // A server with no table-valued functions has nothing to compare; the db model's build has said so.
    if (Provider.SqlDialect.TableFunctionSource == null)
      return;
    var targets = targetModel.Tables.Where(t => t.IsTableFunction).ToList();
    foreach (var target in targets) {
      var existing = sourceModel.Routines.FirstOrDefault(r => SameObject(r.Schema.Name, r.Name, target));
      if (!target.TableFunction!.IsManaged) {
        if (existing == null)
          diff.AddError($"Function '{target.Name}' is declared as existing in the database, and the database has no such function.");
      } else if (target.TableFunction.Body == null) {
        continue;                                             // reported where the db model was built
      } else if (existing == null)
        diff.Add(new DbObjectDiffInfo(DbObjectKind.TableFunction, DbDiffKind.Add, null, target));
      else if (existing.StoredDefinitionHash != DefinitionHashHelper.Of(target.TableFunction.Body))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.TableFunction, DbDiffKind.Modify, existing, target,
          existing.StoredDefinitionHash == null
            ? "not made by MIC, or made before its definition was hashed — the function is recreated"
            : "definition changed — the function is recreated"));
    }

    foreach (var source in sourceModel.Routines)
      if (source.StoredDefinitionHash != null && !targets.Any(t => SameObject(source.Schema.Name, source.Name, t)))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.TableFunction, DbDiffKind.Drop, source, null));
  }

  // ----- views ----------------------------------------------------------------------------------------------------

  /// <summary>Compares the views the model declares with the views read from the database: a managed view is added,
  /// or recreated where its stored hash differs from its select's; a view declared as existing is checked for its
  /// columns and never changed; a view MIC made and the model no longer declares is dropped, and any other view is
  /// left alone.</summary>
  protected virtual void CompareViews(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    var targets = targetModel.Tables.Where(t => t.IsView).ToList();
    foreach (var target in targets) {
      if (sourceModel.Tables.Any(t => SameObject(t.Schema.Name, t.Name, target))) {
        diff.AddError($"'{target.Name}' is a view in the model and a table in the database.");
        continue;
      }
      var existing = sourceModel.Views.FirstOrDefault(v => SameObject(v.Schema.Name, v.Name, target));
      if (!target.View!.IsManaged)
        CheckExistingView(diff, existing, target);
      else if (existing == null)
        diff.Add(new DbObjectDiffInfo(DbObjectKind.View, DbDiffKind.Add, null, target));
      else if (!ViewsMatch(existing, target, out var description))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.View, DbDiffKind.Modify, existing, target, description));
    }

    foreach (var source in sourceModel.Views)
      if (source.StoredDefinitionHash != null && !targets.Any(t => SameObject(source.Schema.Name, source.Name, t)))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.View, DbDiffKind.Drop, source, null));
  }

  /// <summary>Adds a recreation of every managed view that reads a table whose column the differences drop or
  /// change in type, since a server may refuse such a change under a view.</summary>
  private void RecreateViewsOverChangedColumns(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    var changed = new HashSet<DbTableInfo>();
    foreach (var change in diff.Items.Where(c => c.ObjectKind == DbObjectKind.Column).ToList()) {
      if (change.DiffKind == DbDiffKind.Drop && change.Source is DbColumnInfo { Table: { } dropped }
          && targetModel.Tables.FirstOrDefault(t => SameObject(dropped.Schema.Name, dropped.Name, t)) is { } table)
        changed.Add(table);
      if (change.DiffKind == DbDiffKind.Modify && change is { Source: DbColumnInfo before, Target: DbColumnInfo { Table: { } now } after }
          && !string.Equals(before.TypeSpec, after.TypeSpec, StringComparison.OrdinalIgnoreCase))
        changed.Add(now);
    }
    if (changed.Count == 0)
      return;
    var recreated = diff.Items.Where(c => c.ObjectKind == DbObjectKind.View).Select(c => c.Target).ToHashSet();
    foreach (var target in targetModel.Tables) {
      if (target.View is not { IsManaged: true } view || recreated.Contains(target))
        continue;
      var cause = view.ReadsTables.FirstOrDefault(changed.Contains);
      var existing = cause == null ? null
        : sourceModel.Views.FirstOrDefault(v => SameObject(v.Schema.Name, v.Name, target));
      if (existing != null)
        diff.Add(new DbObjectDiffInfo(DbObjectKind.View, DbDiffKind.Modify, existing, target,
          $"recreated over table '{cause!.Name}', whose column changes"));
    }
  }

  /// <summary>Adds a recreation of every managed view that depends, directly or through others, on a view the
  /// differences recreate, since a server may refuse to drop a view another view reads.</summary>
  private static void RecreateDependentViews(DbModelDiffInfo diff, DbModel sourceModel,
      IReadOnlyList<DbTableInfo> targets) {
    var recreated = new HashSet<EntityTypeInfo>();
    foreach (var change in diff.Items)
      if (change.ObjectKind == DbObjectKind.View && change.DiffKind == DbDiffKind.Modify
          && change.Target is DbTableInfo { Entity: { } entity })
        recreated.Add(entity);
    if (recreated.Count == 0)
      return;
    foreach (var target in targets) {
      if (target.View is not { IsManaged: true } || target.Entity is not { View: { } view }
          || recreated.Contains(target.Entity))
        continue;
      var cause = FindRecreatedDependency(view, recreated, []);
      var existing = cause == null ? null
        : sourceModel.Views.FirstOrDefault(v => SameObject(v.Schema.Name, v.Name, target));
      if (existing != null)
        diff.Add(new DbObjectDiffInfo(DbObjectKind.View, DbDiffKind.Modify, existing, target,
          $"recreated with the view it reads, '{cause!.Name}'"));
    }
  }

  private static EntityTypeInfo? FindRecreatedDependency(ViewInfo view, HashSet<EntityTypeInfo> recreated,
      HashSet<EntityTypeInfo> visited) {
    foreach (var dependency in view.DependsOn) {
      if (recreated.Contains(dependency))
        return dependency;
      if (visited.Add(dependency) && dependency.View is { } inner
          && FindRecreatedDependency(inner, recreated, visited) is { } found)
        return found;
    }
    return null;
  }

  /// <summary>Compares a view as it is with the view the model wants, by whether it is materialized and by the hash
  /// of its definition.</summary>
  protected virtual bool ViewsMatch(DbViewInfo source, DbTableInfo target, out string? description) {
    description = null;
    var view = target.View!;
    if (source.IsMaterialized != view.IsMaterialized)
      description = view.IsMaterialized ? "now materialized" : "no longer materialized";
    if (source.StoredDefinitionHash != DefinitionHashHelper.Of(view.Source ?? ""))
      description = Join(description, source.StoredDefinitionHash == null
        ? "not made by MIC, or made before its definition was hashed — the view is recreated"
        : "definition changed — the view is recreated");
    return description == null;
  }

  /// <summary>Reports a view the model declares as existing that the database lacks, or that lacks a column the
  /// entity reads.</summary>
  private static void CheckExistingView(DbModelDiffInfo diff, DbViewInfo? existing, DbTableInfo target) {
    if (existing == null) {
      diff.AddError($"View '{target.Name}' is declared as existing in the database, and the database has no such view.");
      return;
    }
    foreach (var column in target.Columns)
      if (!existing.Columns.Any(c => SameName(c.Name, column.Name)))
        diff.AddError(
          $"View '{target.Name}' has no column '{column.Name}', which '{target.Entity?.Name}.{column.Member?.Name}' reads.");
  }

  private static bool SameObject(string schema, string name, DbTableInfo table) =>
    SameName(schema, table.Schema.Name) && SameName(name, table.Name);

  // ----- schemas and sequences ------------------------------------------------------------------------------------

  protected virtual void CompareSchemas(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    var match = Match(diff, sourceModel.Schemas, targetModel.Schemas, _ => []);
    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Schema, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Schema, DbDiffKind.Drop, dropped, null));
  }

  protected virtual void CompareSequences(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    var match = Match(diff, sourceModel.Sequences, targetModel.Sequences, _ => []);
    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Sequence, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Sequence, DbDiffKind.Drop, dropped, null));
    foreach (var (source, target) in match.Pairs)
      if (!SequencesMatch(source, target, out var description))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.Sequence, DbDiffKind.Modify, source, target, description));
  }

  protected virtual bool SequencesMatch(DbSequenceInfo source, DbSequenceInfo target, out string? description) {
    description = null;
    // A start the server did not report is not a difference: nothing can be read back to compare it with.
    if (source.StartValue is { } current && target.StartValue is { } wanted && current != wanted)
      description = $"start {current} → {wanted}";
    if (source.Increment != target.Increment)
      description = Join(description, $"increment {source.Increment} → {target.Increment}");
    return description == null;
  }

  // ----- tables ---------------------------------------------------------------------------------------------------

  protected virtual void CompareTables(DbModelDiffInfo diff, DbModel sourceModel, DbModel targetModel) {
    // The model's views and functions are compared with the database's views and routines, not with its tables.
    var match = Match(diff, sourceModel.Tables, [.. targetModel.Tables.Where(t => !t.IsView && !t.IsTemporary && !t.IsTableFunction)],
      t => t.Entity?.OldNames ?? []);

    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Table, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Table, DbDiffKind.Drop, dropped, null));

    foreach (var (source, target) in match.Pairs) {
      if (!SameName(source.Name, target.Name))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.Table, DbDiffKind.Rename, source, target,
          $"'{source.Name}' → '{target.Name}'"));
      CompareColumns(diff, source, target);
      CompareKeys(diff, source, target);
      if (targetModel.Options.HasFlag(DbOptions.UseRefIntegrity))
        CompareRefConstraints(diff, source, target);
      CompareChecks(diff, source, target);
    }
  }

  /// <summary>Compares the check constraints of a table by name: a declared one the table lacks is added, and one
  /// the table has under a name of the ORM's making that the model no longer declares is dropped.</summary>
  /// <param name="diff">The diff the changes are added to.</param>
  /// <param name="sourceTable">The table as it is.</param>
  /// <param name="targetTable">The table as the model wants it.</param>
  /// <remarks>The name carries a hash of the condition, so a changed condition is a drop and an add. A constraint
  /// under any other name is somebody else's and is left alone.</remarks>
  protected virtual void CompareChecks(DbModelDiffInfo diff, DbTableInfo sourceTable, DbTableInfo targetTable) {
    foreach (var target in targetTable.CheckConstraints)
      if (!sourceTable.CheckConstraints.Any(source => SameName(source.Name, target.Name)))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.CheckConstraint, DbDiffKind.Add, null, target));

    // The table may have been renamed since the constraint was named after it.
    foreach (var source in sourceTable.CheckConstraints)
      if (!targetTable.CheckConstraints.Any(target => SameName(target.Name, source.Name))
          && (DbCheckConstraintInfo.IsManagedName(source.Name, sourceTable.Name)
            || DbCheckConstraintInfo.IsManagedName(source.Name, targetTable.Name)))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.CheckConstraint, DbDiffKind.Drop, source, null));
  }

  protected virtual void CompareColumns(DbModelDiffInfo diff, DbTableInfo sourceTable, DbTableInfo targetTable) {
    var match = Match(diff, sourceTable.Columns, targetTable.Columns, c => c.Member?.OldNames ?? []);

    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Column, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Column, DbDiffKind.Drop, dropped, null));

    foreach (var (source, target) in match.Pairs) {
      if (!SameName(source.Name, target.Name))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.Column, DbDiffKind.Rename, source, target,
          $"'{source.Name}' → '{target.Name}'"));
      if (!ColumnsMatch(source, target, out var description))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.Column, DbDiffKind.Modify, source, target, description));
    }
  }

  /// <summary>Compares two columns by type spec, nullability, identity and default.</summary>
  /// <param name="source">The column as it is.</param>
  /// <param name="target">The column as the model wants it.</param>
  /// <param name="description">When this method returns, contains the description of the differences, or
  /// <see langword="null"/> if there are none.</param>
  /// <returns><see langword="true"/> if the columns match, or the target member is marked as-is; otherwise,
  /// <see langword="false"/>.</returns>
  protected virtual bool ColumnsMatch(DbColumnInfo source, DbColumnInfo target, out string? description) {
    description = null;

    // A column marked AsIs is left alone, whatever it looks like.
    if (target.Member?.MemberFlags.HasFlag(MemberFlags.AsIs) == true)
      return true;

    // A computed column's type and nullability are the expression's, which the server reports as it likes; what is
    // compared is that both sides compute it, and from the same expression.
    if (source.IsComputed != target.IsComputed) {
      description = target.IsComputed ? "now computed — the column is recreated" : "no longer computed — the column is recreated";
      return false;
    }
    if (target.IsComputed) {
      // The expression is compared through its hash, since the server rewrites the text.
      var stored = GetExpressionHash(source);
      if (stored == GetExpressionHash(target))
        return true;
      description = stored == null
        ? "no hash of the expression beside the column — the column is recreated"
        : "expression changed — the column is recreated";
      return false;
    }

    if (!string.Equals(source.TypeSpec, target.TypeSpec, StringComparison.OrdinalIgnoreCase))
      description = $"type {source.TypeSpec} → {target.TypeSpec}";
    if (source.IsNullable != target.IsNullable)
      description = Join(description, target.IsNullable ? "now nullable" : "now NOT NULL");
    // Where an identity is a shape and not a marker, the database reads one off any key of that shape; the model not
    // declaring it is the same column, which no statement could change.
    var isImpliedIdentity = source.IsIdentity && Provider.Supports(DbFeatures.IdentityIsImpliedSqlite);
    if (source.IsIdentity != target.IsIdentity && !isImpliedIdentity)
      description = Join(description, target.IsIdentity ? "now identity" : "no longer identity");

    // Defaults are dialect text; a server may rewrite what it was given, which the loader normalizes.
    if (!string.Equals(source.DefaultExpression, target.DefaultExpression, StringComparison.OrdinalIgnoreCase))
      description = Join(description, $"default {Show(source.DefaultExpression)} → {Show(target.DefaultExpression)}");

    return description == null;
  }

  /// <summary>Gets the hash a computed column's expression is compared by: the hash MIC wrote beside the column
  /// where one was read, otherwise the hash of the expression the column carries.</summary>
  /// <remarks>A computed column MIC did not create, or created before it wrote the hash, has neither and compares
  /// as changed.</remarks>
  protected virtual string? GetExpressionHash(DbColumnInfo column) =>
    column.StoredDefinitionHash
      ?? (column.ComputedExpression == null ? null : DefinitionHashHelper.Of(column.ComputedExpression));

  // ----- keys and constraints -------------------------------------------------------------------------------------

  protected virtual void CompareKeys(DbModelDiffInfo diff, DbTableInfo sourceTable, DbTableInfo targetTable) {
    // A foreign key's column set carries the constraint's name, so where the server does not keep that name the key
    // is matched by its columns instead.
    var named = Provider.Supports(DbFeatures.NamedRefConstraints);
    var match = named
      ? Match(diff, sourceTable.Keys, targetTable.Keys, _ => [])
      : Merge(
          Match(diff, [.. sourceTable.Keys.Where(k => !k.IsForeignKey)],
            [.. targetTable.Keys.Where(k => !k.IsForeignKey)], _ => []),
          MatchKeysBySignature([.. sourceTable.Keys.Where(k => k.IsForeignKey)],
            [.. targetTable.Keys.Where(k => k.IsForeignKey)]));

    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Key, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.Key, DbDiffKind.Drop, dropped, null));

    foreach (var (source, target) in match.Pairs)
      if (!KeysMatch(source, target, out var description))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.Key, DbDiffKind.Modify, source, target, description));
  }

  protected virtual bool KeysMatch(DbKeyInfo source, DbKeyInfo target, out string? description) {
    description = null;

    // Clustering is not compared where it is only a marker: the server does not maintain the order, so the difference
    // would come back on its own and an index rebuild could not fix it.
    var flagMask = Provider.Supports(DbFeatures.ClusteringMarker) ? ~KeyFlags.Clustered : ~KeyFlags.None;
    if ((source.Flags & flagMask) != (target.Flags & flagMask))
      description = $"{source.Flags} → {target.Flags}";
    if (!ColumnListsMatch(source.Columns, target.Columns))
      description = Join(description, "columns changed");
    if (!ColumnNamesMatch(source.IncludedColumns, target.IncludedColumns))
      description = Join(description, "included columns changed");

    // The access method is compared only where the server has more than one; a difference means a rebuild.
    if (Provider.IndexTypes.Count > 0 && source.IndexType != target.IndexType)
      description = Join(description, $"index type {source.IndexType} → {target.IndexType} — the index is rebuilt");

    // A vector index serves one metric, so another metric is another index.
    if (source.VectorMetric != target.VectorMetric)
      description = Join(description,
        $"metric {DescribeMetric(source)} → {DescribeMetric(target)} — the index is rebuilt");

    // A filter is compared through its hash, since the server may rewrite the text.
    if (FilterHash(source) != FilterHash(target))
      description = Join(description, "filter changed — the index is rebuilt");
    return description == null;
  }

  private static string DescribeMetric(DbKeyInfo key) => key.VectorMetric?.ToString() ?? "none";

  /// <summary>Gets the hash a key's filter is compared by: the hash MIC stored beside the index where there is one,
  /// otherwise the hash of the filter text.</summary>
  /// <remarks>A filtered index MIC did not create has no stored hash and compares as changed.</remarks>
  protected virtual string FilterHash(DbKeyInfo key) =>
    key.StoredDefinitionHash ?? DefinitionHashHelper.Of(key.Filter ?? "");

  protected virtual void CompareRefConstraints(DbModelDiffInfo diff, DbTableInfo sourceTable, DbTableInfo targetTable) {
    // Where a server does not keep constraint names, a foreign key is matched by its columns and target instead.
    var match = Provider.Supports(DbFeatures.NamedRefConstraints)
      ? Match(diff, sourceTable.RefConstraints, targetTable.RefConstraints, _ => [])
      : MatchBySignature(sourceTable.RefConstraints, targetTable.RefConstraints);

    foreach (var added in match.Added)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.RefConstraint, DbDiffKind.Add, null, added));
    foreach (var dropped in match.Dropped)
      diff.Add(new DbObjectDiffInfo(DbObjectKind.RefConstraint, DbDiffKind.Drop, dropped, null));

    foreach (var (source, target) in match.Pairs)
      if (!RefConstraintsMatch(source, target, out var description))
        diff.Add(new DbObjectDiffInfo(DbObjectKind.RefConstraint, DbDiffKind.Modify, source, target, description));
  }

  protected virtual bool RefConstraintsMatch(DbRefConstraintInfo source, DbRefConstraintInfo target,
      out string? description) {
    description = null;
    if (!ColumnListsMatch(source.FromKey.Columns, target.FromKey.Columns))
      description = "columns changed";
    if (!SameName(source.ToTable.Name, target.ToTable.Name))
      description = Join(description, $"targets '{source.ToTable.Name}' → '{target.ToTable.Name}'");
    if (source.CascadeDelete != target.CascadeDelete)
      description = Join(description, target.CascadeDelete ? "now ON DELETE CASCADE" : "no longer ON DELETE CASCADE");
    return description == null;
  }

  /// <summary>Pairs reference constraints by their signature, the target table and the referencing columns in order,
  /// rather than by name.</summary>
  protected MatchResult<DbRefConstraintInfo> MatchBySignature(
      IReadOnlyList<DbRefConstraintInfo> sourceConstraints, IReadOnlyList<DbRefConstraintInfo> targetConstraints) {
    var result = new MatchResult<DbRefConstraintInfo>();
    var unmatched = sourceConstraints.ToList();

    foreach (var target in targetConstraints) {
      var match = unmatched.FirstOrDefault(o => SameName(BuildSignature(o), BuildSignature(target)));
      if (match == null) {
        result.Added.Add(target);
      } else {
        result.Pairs.Add((match, target));
        unmatched.Remove(match);
      }
    }
    result.Dropped.AddRange(unmatched);
    return result;
  }

  private static string BuildSignature(DbRefConstraintInfo constraint) =>
    constraint.ToTable.Name + "(" + string.Join(",", constraint.FromKey.Columns.Select(c => c.Column.Name)) + ")";

  /// <summary>Pairs foreign keys by their column lists rather than by name.</summary>
  protected static MatchResult<DbKeyInfo> MatchKeysBySignature(
      IReadOnlyList<DbKeyInfo> sourceKeys, IReadOnlyList<DbKeyInfo> targetKeys) {
    var result = new MatchResult<DbKeyInfo>();
    var unmatched = sourceKeys.ToList();

    foreach (var target in targetKeys) {
      var match = unmatched.FirstOrDefault(o => SameName(Columns(o), Columns(target)));
      if (match == null) {
        result.Added.Add(target);
      } else {
        result.Pairs.Add((match, target));
        unmatched.Remove(match);
      }
    }
    result.Dropped.AddRange(unmatched);
    return result;
  }

  private static string Columns(DbKeyInfo key) => string.Join(",", key.Columns.Select(c => c.Column.Name));

  static MatchResult<T> Merge<T>(MatchResult<T> first, MatchResult<T> second) where T : DbModelObject {
    first.Pairs.AddRange(second.Pairs);
    first.Added.AddRange(second.Added);
    first.Dropped.AddRange(second.Dropped);
    return first;
  }

  // ----- matching -------------------------------------------------------------------------------------------------

  protected sealed class MatchResult<T> {
    public List<(T Source, T Target)> Pairs { get; } = new();
    public List<T> Added { get; } = new();
    public List<T> Dropped { get; } = new();
  }

  /// <summary>Pairs source and target objects by name, then by old names against what is still unmatched.</summary>
  /// <param name="diff">The diff an unresolvable conflict is reported to.</param>
  /// <param name="sourceObjects">The objects of the source model.</param>
  /// <param name="targetObjects">The objects of the target model.</param>
  /// <param name="oldNamesOf">A function that returns the old names of a target object.</param>
  /// <returns>The pairs, the objects added and the objects dropped.</returns>
  /// <remarks>Two target objects claiming one source object by old name is an error in the diff, and both are treated
  /// as added.</remarks>
  protected MatchResult<T> Match<T>(DbModelDiffInfo diff, IReadOnlyList<T> sourceObjects,
      IReadOnlyList<T> targetObjects, Func<T, IReadOnlyList<string>> oldNamesOf) where T : DbModelObject {
    var result = new MatchResult<T>();
    var unmatchedSource = sourceObjects.ToList();
    var unmatchedTarget = new List<T>();

    foreach (var target in targetObjects) {
      var byName = unmatchedSource.FirstOrDefault(o => SameName(o.Name, target.Name));
      if (byName != null) {
        result.Pairs.Add((byName, target));
        unmatchedSource.Remove(byName);
      } else {
        unmatchedTarget.Add(target);
      }
    }

    var claimed = new Dictionary<T, T>();
    foreach (var target in unmatchedTarget) {
      T? found = null;
      foreach (var oldName in oldNamesOf(target)) {
        found = unmatchedSource.FirstOrDefault(o => SameName(o.Name, oldName));
        if (found != null)
          break;
      }
      if (found == null) {
        result.Added.Add(target);
        continue;
      }
      if (claimed.TryGetValue(found, out var other)) {
        diff.AddError(
          $"'{target.Name}' and '{other.Name}' both claim to be the former '{found.Name}' — one object cannot become two.");
        result.Added.Add(target);
        continue;
      }
      claimed[found] = target;
      result.Pairs.Add((found, target));
    }

    foreach (var source in unmatchedSource)
      if (!claimed.ContainsKey(source))
        result.Dropped.Add(source);
    return result;
  }

  // ----- helpers --------------------------------------------------------------------------------------------------

  /// <summary>Compares two names ignoring case, since each server folds identifiers differently.</summary>
  protected static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

  private static bool ColumnListsMatch(IReadOnlyList<DbKeyColumn> a, IReadOnlyList<DbKeyColumn> b) =>
    a.Count == b.Count
    && a.Zip(b).All(p => SameName(p.First.Column.Name, p.Second.Column.Name)
      && p.First.Descending == p.Second.Descending);

  private static bool ColumnNamesMatch(IReadOnlyList<DbColumnInfo> a, IReadOnlyList<DbColumnInfo> b) =>
    a.Count == b.Count && a.Zip(b).All(p => SameName(p.First.Name, p.Second.Name));

  private static string Join(string? existing, string next) => existing == null ? next : existing + "; " + next;

  private static string Show(string? value) => value ?? "(none)";
}
