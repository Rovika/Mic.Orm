using System.Text;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Builds DDL statements from a db model, spelled through the provider's <see cref="SqlDialect"/>.</summary>
public class DbDdlBuilder {
  public DbDdlBuilder(DbProvider provider) {
    Provider = provider;
    Dialect = provider.SqlDialect;
  }

  protected DbProvider Provider { get; }
  protected SqlDialect Dialect { get; }

  /// <summary>Builds the statements that create a whole model, in an order the database accepts: schemas and
  /// sequences, then tables, then keys, then reference constraints.</summary>
  /// <param name="model">The model to create.</param>
  /// <returns>The statements, in execution order.</returns>
  public virtual IReadOnlyList<DdlStatement> CreateModel(DbModel model) {
    var statements = new List<DdlStatement>();

    if (model.Tables.Any(HasGeoPoint) && GeoPointPrerequisite() is { } prerequisite)
      statements.Add(prerequisite);
    if (model.Tables.Any(HasVector) && VectorPrerequisite() is { } vectorPrerequisite)
      statements.Add(vectorPrerequisite);

    foreach (var schema in model.Schemas) {
      // A schema MIC does not manage is never created; whether it is managed is known only through the entity schema.
      if (schema.Name.Length == 0 || schema.EntitySchema?.IsManaged == false)
        continue;
      statements.Add(CreateSchema(schema));
    }

    foreach (var sequence in model.Sequences)
      statements.Add(CreateSequence(sequence));

    // A view is a table to the rest of the model and is created last, from its select; a function likewise.
    var tables = model.Tables.Where(t => !t.IsView && !t.IsTemporary && !t.IsTableFunction).ToList();
    foreach (var table in tables)
      statements.Add(CreateTable(table));

    // Inlined constraints are already in the CREATE TABLE statements above; an index is its own object everywhere.
    foreach (var table in tables) {
      var inlined = NeedsInlineConstraints(table);
      foreach (var key in GetKeysInCreationOrder(table)) {
        if (key.IsPrimaryKey) {
          if (!inlined)
            statements.Add(AddPrimaryKey(key));
        } else if (key.Flags.HasFlag(KeyFlags.Index) || key.IsUnique) {
          statements.AddRange(CreateIndex(key));
        }
      }
    }

    foreach (var table in tables)
      if (!InlinesRefConstraints())
        foreach (var constraint in table.RefConstraints)
          statements.Add(AddRefConstraint(constraint));

    // Last, over finished tables; a view the model declares as existing is never created, nor a function.
    foreach (var table in model.Tables)
      if (table.View is { IsManaged: true })
        statements.AddRange(CreateView(table));
    foreach (var table in model.Tables)
      if (table.TableFunction is { IsManaged: true } && CreateTableFunction(table) is { } function)
        statements.Add(function);

    return statements;
  }

  /// <summary>Builds the statement that creates a table function MIC manages, or replaces it, with the hash of its
  /// body written into the body as a comment, so a later run can tell whether the definition changed.</summary>
  /// <param name="table">The function's table.</param>
  /// <returns>The statement, or <see langword="null"/> where the server has no table-valued functions.</returns>
  public virtual DdlStatement? CreateTableFunction(DbTableInfo table) {
    ArgumentNullException.ThrowIfNull(table);
    var function = table.TableFunction ?? throw new ArgumentException($"'{table.Name}' is not a function.", nameof(table));
    var body = function.Body
      ?? throw new InvalidOperationException($"Function '{table.Name}' has no body; a function declared as existing is never created.");
    var marked = $"/* {DefinitionHashHelper.Of(body)} */{Dialect.LineBreak}{body}";
    return Dialect.BuildTableFunction(table, marked) is { } text
      ? new DdlStatement(DdlKind.CreateFunction, text, table.Name) : null;
  }

  /// <summary>Builds the statement that drops a function, as read from the database.</summary>
  public virtual DdlStatement DropTableFunction(DbRoutineInfo routine) =>
    new(DdlKind.DropFunction,
      string.Format(Dialect.DropTableFunction, Dialect.QualifiedName(routine.Schema.Name, routine.Name)), routine.Name);

  /// <summary>Builds the statements that create a view MIC manages: the view over its select, with the hash of the
  /// select carried as the server allows, so a later run can tell whether the definition changed.</summary>
  /// <param name="table">The view's table.</param>
  /// <returns>The statements: the CREATE, and the comment that carries the hash where the server keeps it apart.
  /// </returns>
  public virtual IEnumerable<DdlStatement> CreateView(DbTableInfo table) {
    ArgumentNullException.ThrowIfNull(table);
    var view = table.View ?? throw new ArgumentException($"'{table.Name}' is not a view.", nameof(table));
    var sql = view.Source
      ?? throw new InvalidOperationException($"View '{table.Name}' has no select; a view declared as existing is never created.");
    var hash = DefinitionHashHelper.Of(sql);
    var body = Dialect.ViewHashCarrier switch {
      ViewHashCarrier.TextComment => $"/* {hash} */{Dialect.LineBreak}{sql}",
      ViewHashCarrier.Literal => string.Format(Dialect.ViewHashLiteral, sql, Dialect.GetLiteral(hash)),
      _ => sql,
    };
    var columns = view.ColumnNames == null ? "" : " (" + string.Join(", ", view.ColumnNames.Select(Dialect.Quote)) + ")";
    yield return new DdlStatement(DdlKind.CreateView,
      string.Format(view.IsMaterialized ? Dialect.CreateMaterializedView : Dialect.CreateView, table.RefName, columns, body),
      table.Name);
    if (Dialect.ViewHashCarrier == ViewHashCarrier.Comment)
      yield return new DdlStatement(DdlKind.Comment,
        string.Format(view.IsMaterialized ? Dialect.CommentOnMaterializedView : Dialect.CommentOnView, table.RefName,
          Dialect.GetLiteral(hash)),
        table.Name);
  }

  /// <summary>Builds the statement that drops a view, as read from the database.</summary>
  public virtual DdlStatement DropView(DbViewInfo view) =>
    new(DdlKind.DropView,
      string.Format(view.IsMaterialized ? Dialect.DropMaterializedView : Dialect.DropView,
        Dialect.QualifiedName(view.Schema.Name, view.Name)),
      view.Name);

  /// <summary>Builds the statements that create one table with its primary key, indexes and foreign keys, for showing
  /// the table's DDL in one place.</summary>
  /// <param name="table">The table.</param>
  /// <returns>The statements, grouped by the table rather than ordered for a whole model.</returns>
  /// <remarks>Unlike <see cref="CreateModel"/>, this orders by object, so a foreign key may name a table that does
  /// not exist yet. Use it for display, not for creating a model.</remarks>
  public virtual IReadOnlyList<DdlStatement> CreateTableScript(DbTableInfo table) {
    ArgumentNullException.ThrowIfNull(table);
    if (table.View is { } view)
      return view.IsManaged ? [.. CreateView(table)] : [];
    if (table.TableFunction is { } function)
      return function.IsManaged && CreateTableFunction(table) is { } create ? [create] : [];
    if (table.IsTemporary)
      return [CreateTempTable(table)];
    var statements = new List<DdlStatement> { CreateTable(table) };
    foreach (var column in table.Columns)
      if (CommentComputedColumn(column) is { } comment)
        statements.Add(comment);
    var inlined = NeedsInlineConstraints(table);

    foreach (var key in GetKeysInCreationOrder(table)) {
      if (key.IsPrimaryKey) {
        if (!inlined)
          statements.Add(AddPrimaryKey(key));
      } else if (key.Flags.HasFlag(KeyFlags.Index) || key.IsUnique) {
        statements.AddRange(CreateIndex(key));
      }
    }

    if (!InlinesRefConstraints())
      foreach (var constraint in table.RefConstraints)
        statements.Add(AddRefConstraint(constraint));

    return statements;
  }

  /// <summary>Gets a table's keys with the primary key first, whatever order the model declared them in.</summary>
  /// <param name="table">The table.</param>
  /// <returns>The keys, primary key first.</returns>
  /// <remarks>An index may depend on the primary key, as SQL Server's full-text index does.</remarks>
  public static IEnumerable<DbKeyInfo> GetKeysInCreationOrder(DbTableInfo table) =>
    table.Keys.OrderBy(k => k.IsPrimaryKey ? 0 : 1);

  /// <summary>Renders statements as a runnable script, one per line, each ended with the dialect's terminator.
  /// </summary>
  /// <param name="statements">The statements.</param>
  /// <returns>The script as text.</returns>
  public string ToScript(IEnumerable<DdlStatement> statements) {
    var text = new StringBuilder();
    foreach (var statement in statements)
      text.AppendLine(statement.Text + Dialect.StatementEnd);
    return text.ToString();
  }

  // ----- objects --------------------------------------------------------------------------------------------------

  public virtual DdlStatement CreateSchema(DbSchemaInfo schema) =>
    new(DdlKind.CreateSchema, string.Format(Dialect.CreateSchema, Dialect.Quote(schema.Name)), schema.Name);

  public virtual DdlStatement CreateSequence(DbSequenceInfo sequence) =>
    new(DdlKind.CreateSequence,
      string.Format(Dialect.CreateSequence, Dialect.QualifiedName(sequence.Schema.Name, sequence.Name),
        sequence.StartValue ?? 1, sequence.Increment),
      sequence.Name);

  public virtual DdlStatement CreateTable(DbTableInfo table) {
    var parts = table.Columns.Select(c => Dialect.Indent + ColumnSpec(c)).ToList();

    // Constraints a server cannot add later go inside the CREATE TABLE; elsewhere they are separate statements so an
    // upgrade can change one.
    if (NeedsInlineConstraints(table))
      parts.AddRange(InlineConstraints(table).Select(c => Dialect.Indent + c));

    // A check is written here on every server: each takes it in the CREATE TABLE, and one cannot add it later.
    parts.AddRange(table.CheckConstraints.Select(c =>
      Dialect.Indent + string.Format(Dialect.InlineCheckConstraint, Dialect.Quote(c.Name), c.Expression)));

    return new DdlStatement(DdlKind.CreateTable,
      string.Format(Dialect.CreateTable, table.RefName, string.Join("," + Dialect.LineBreak, parts)), table.Name);
  }

  /// <summary>Builds the statement that makes the server able to hold a geographic point, such as PostGIS's
  /// extension.</summary>
  /// <returns>The statement, or <see langword="null"/> where the server needs nothing.</returns>
  public DdlStatement? GeoPointPrerequisite() =>
    Dialect.GeoPointPrerequisite is { } text ? new DdlStatement(DdlKind.Prerequisite, text, "geography") : null;

  /// <summary>Determines whether a table has a geographic point column.</summary>
  public static bool HasGeoPoint(DbTableInfo table) => table.Columns.Any(c => c.IsGeoPoint);

  /// <summary>Builds the statement that makes the server able to hold a vector, such as pgvector's extension.
  /// </summary>
  /// <returns>The statement, or <see langword="null"/> where the server needs nothing.</returns>
  public DdlStatement? VectorPrerequisite() =>
    Dialect.VectorPrerequisite is { } text ? new DdlStatement(DdlKind.Prerequisite, text, "vector") : null;

  /// <summary>Determines whether a table has a vector column.</summary>
  public static bool HasVector(DbTableInfo table) => table.Columns.Any(c => c.IsVector);

  /// <summary>Builds the statement that creates a temp table on a session's connection: its columns, and no key or
  /// constraint, since nothing enforces them on rows that live as long as the session.</summary>
  public virtual DdlStatement CreateTempTable(DbTableInfo table) {
    ArgumentNullException.ThrowIfNull(table);
    var allNullable = Provider.Supports(DbFeatures.NoTempTableNotNullOracle);
    var columns = string.Join("," + Dialect.LineBreak,
      table.Columns.Select(c => Dialect.Indent + ColumnSpec(c, asNullable: allNullable)));
    return new DdlStatement(DdlKind.CreateTable, string.Format(Dialect.CreateTempTable, table.RefName, columns), table.Name);
  }

  /// <summary>Builds the statement that drops a session's temp table.</summary>
  public virtual DdlStatement DropTempTable(DbTableInfo table) =>
    new(DdlKind.DropTable, string.Format(Dialect.DropTempTable, table.RefName), table.Name);

  /// <summary>Decides whether a table's primary key has to be part of its CREATE TABLE statement.</summary>
  /// <param name="table">The table.</param>
  /// <returns><see langword="true"/> if the server takes constraints no other way, or refuses an identity column that
  /// is not keyed at creation and the table has one; otherwise, <see langword="false"/>.</returns>
  public virtual bool NeedsInlineConstraints(DbTableInfo table) =>
    InlinesRefConstraints()
    || (Provider.Supports(DbFeatures.IdentityRequiresInlineKey) && table.Columns.Any(c => c.IsIdentity));

  /// <summary>Decides whether foreign keys are part of the CREATE TABLE statement: only where the server takes
  /// constraints no other way.</summary>
  /// <remarks>A table keyed at creation for its identity column still gets its foreign keys afterwards, with every
  /// table's; inlined, one could name a table whose key is not there yet.</remarks>
  public virtual bool InlinesRefConstraints() => !Provider.Supports(DbFeatures.AddConstraintsAfterCreate);

  /// <summary>Builds a table's constraints as clauses of its own CREATE TABLE statement: the primary key, and the
  /// foreign keys where the server takes them no other way.</summary>
  protected virtual IEnumerable<string> InlineConstraints(DbTableInfo table) {
    if (table.PrimaryKey != null)
      yield return string.Format(
        YieldsTheClusteredSlot(table) ? Dialect.InlinePrimaryKeyNonClustered : Dialect.InlinePrimaryKey,
        Dialect.Quote(table.PrimaryKey.Name), ColumnList(table.PrimaryKey));
    if (!Provider.Supports(DbFeatures.ReferentialConstraints) || !InlinesRefConstraints())
      yield break;
    foreach (var constraint in table.RefConstraints)
      yield return string.Format(Dialect.InlineRefConstraint,
        Dialect.Quote(constraint.Name), ColumnList(constraint.FromKey),
        constraint.ToTable.RefName, ColumnList(constraint.ToKey))
        + (constraint.CascadeDelete ? Dialect.CascadeDelete : "");
  }

  /// <summary>Builds a column's definition as it appears inside a CREATE TABLE or an ADD COLUMN.</summary>
  /// <param name="column">The column.</param>
  /// <param name="asNullable"><see langword="true"/> to write a required column as nullable, so existing rows can
  /// hold it until a migration fills it and it is tightened; otherwise, <see langword="false"/>.</param>
  /// <param name="omitDefault"><see langword="true"/> to leave out the default, for a restated definition on a server
  /// where a default is a named constraint; otherwise, <see langword="false"/>.</param>
  /// <returns>The column definition.</returns>
  protected virtual string ColumnSpec(DbColumnInfo column, bool asNullable = false, bool omitDefault = false) {
    if (column.IsComputed)
      return ComputedColumnSpec(column);
    var notNull = !column.IsNullable && !asNullable ? Dialect.NotNull : "";
    var defaultValue = column.DefaultExpression != null && !omitDefault
      ? string.Format(Dialect.DefaultValue, column.DefaultExpression) : "";
    var identity = column.IsIdentity ? Dialect.Identity : "";
    var modifiers = string.Format(Dialect.ColumnModifiers, notNull, defaultValue, identity);
    return string.Format(Dialect.ColumnSpec, column.QuotedName, column.TypeSpec, modifiers);
  }

  /// <summary>Builds a computed column's definition: the column over its expression, stored or virtual as the member
  /// declares where the server has both, and as the server has it elsewhere. No nullability, default or identity:
  /// the expression decides them.</summary>
  protected virtual string ComputedColumnSpec(DbColumnInfo column) {
    var expression = column.ComputedExpression
      ?? throw new InvalidOperationException($"Computed column '{column.Name}' has no expression to create it from.");
    var stored = Dialect.ComputedStorage switch {
      ComputedStorage.Stored => true,
      ComputedStorage.Virtual => false,
      _ => column.IsComputedStored,
    };
    return string.Format(Dialect.ComputedColumnSpec, column.QuotedName, column.TypeSpec, expression,
      stored ? Dialect.ComputedStored : Dialect.ComputedVirtual, DefinitionHashHelper.Of(expression));
  }

  /// <summary>Builds the statement that writes the hash of a computed column's expression beside the column.
  /// </summary>
  /// <param name="column">The column.</param>
  /// <returns>The statement; <see langword="null"/> for a column that is not computed, and where the dialect
  /// writes the hash inside the column's definition.</returns>
  /// <remarks>An upgrade reads the hash back to tell a changed expression, which the server's own text of it does
  /// not: the server rewrites what it was given.</remarks>
  public virtual DdlStatement? CommentComputedColumn(DbColumnInfo column) {
    ArgumentNullException.ThrowIfNull(column);
    if (!column.IsComputed || column.ComputedExpression == null || Dialect.CommentOnColumn == null)
      return null;
    var table = column.Table!;
    var text = string.Format(Dialect.CommentOnColumn, table.RefName + "." + column.QuotedName,
      Dialect.GetLiteral(DefinitionHashHelper.Of(column.ComputedExpression)), Dialect.GetLiteral(table.Schema.Name),
      Dialect.GetLiteral(table.Name), Dialect.GetLiteral(column.Name));
    return new DdlStatement(DdlKind.Comment, text, column.Name);
  }

  public virtual DdlStatement AddPrimaryKey(DbKeyInfo key) =>
    new(DdlKind.AddPrimaryKey,
      string.Format(YieldsTheClusteredSlot(key.Table) ? Dialect.AddPrimaryKeyNonClustered : Dialect.AddPrimaryKey,
        key.Table.RefName, Dialect.Quote(key.Name), ColumnList(key)),
      key.Name);

  /// <summary>Decides whether a table's primary key is created non-clustered because another key of the table is the
  /// clustered one.</summary>
  /// <returns><see langword="true"/> if the server has clustered storage and a key other than the primary key is
  /// clustered; otherwise, <see langword="false"/>.</returns>
  protected bool YieldsTheClusteredSlot(DbTableInfo table) =>
    Provider.Supports(DbFeatures.ClusteredStorage)
    && table.Keys.Any(k => k.Flags.HasFlag(KeyFlags.Clustered) && !k.IsPrimaryKey);

  /// <summary>Builds the statements that create an index, with the hash of its filter attached separately on a server
  /// that rewrites definitions.</summary>
  /// <param name="key">The index.</param>
  /// <returns>The statements.</returns>
  public virtual IEnumerable<DdlStatement> CreateIndex(DbKeyInfo key) {
    // A full-text index that is more than a CREATE INDEX is the dialect's own statements, each under the index's name.
    if (key.IndexType == DbIndexType.FullText && Dialect.BuildFullTextIndex(key) is { } statements) {
      foreach (var statement in statements)
        yield return new DdlStatement(DdlKind.CreateIndex, statement, key.Name);
      yield break;
    }
    // A spatial index may want the column registered first.
    if (key.Columns is [{ Column.IsGeoPoint: true }] && Dialect.BuildSpatialIndexPrerequisites(key) is { } prerequisites)
      foreach (var prerequisite in prerequisites)
        yield return new DdlStatement(DdlKind.Prerequisite, prerequisite, key.Name);
    // A server that only marks clustering creates an ordinary index here and is told about the order afterwards.
    var clustered = key.Flags.HasFlag(KeyFlags.Clustered) && Provider.Supports(DbFeatures.ClusteredStorage);
    var template = (key.IsUnique, clustered) switch {
      (true, true) => Dialect.CreateUniqueClusteredIndex,
      (true, false) => Dialect.CreateUniqueIndex,
      (false, true) => Dialect.CreateClusteredIndex,
      _ => Dialect.CreateIndex,
    };
    // The default type contributes no prefix and no clause.
    var (typePrefix, typeClause) = key.IndexType == Provider.DefaultIndexType || key.IndexType == DbIndexType.Default
      ? ("", "")
      : Dialect.IndexTypeSyntax(key.IndexType);

    // A full-text index is over the columns, or over a text-search expression where the server has no such index kind.
    // A vector index may state its metric with the column.
    var columns = key.IndexType == DbIndexType.FullText ? Dialect.BuildFullTextIndexColumns(key)
      : key.VectorMetric != null ? Dialect.BuildVectorIndexColumns(key) ?? ColumnList(key)
      : ColumnList(key);
    var text = new StringBuilder(
      string.Format(template, Dialect.Quote(key.Name), key.Table.RefName, columns,
        Dialect.QualifiedName(key.Table.Schema.Name, key.Name), typePrefix, typeClause));

    if (key.IncludedColumns.Count > 0)
      text.Append(string.Format(Dialect.IndexInclude,
        string.Join(", ", key.IncludedColumns.Select(c => c.QuotedName))));
    if (key.Filter != null)
      text.Append(string.Format(Dialect.IndexFilter, key.Filter));

    yield return new DdlStatement(DdlKind.CreateIndex, text.ToString(), key.Name);

    foreach (var comment in DefinitionComments(key))
      yield return comment;
  }

  /// <summary>Builds the statement that attaches the hash of an index's filter as a comment, on a server that does not
  /// preserve comments inside the definition.</summary>
  /// <returns>The comment statement, or nothing where the index has no filter or the server preserves comments.
  /// </returns>
  protected virtual IEnumerable<DdlStatement> DefinitionComments(DbKeyInfo key) {
    if (key.Filter == null || Provider.Supports(DbFeatures.ServerPreservesComments))
      yield break;
    var hash = DefinitionHashHelper.Of(key.Filter);
    yield return new DdlStatement(DdlKind.Comment,
      string.Format(Dialect.CommentOnIndex, Dialect.QualifiedName(key.Table.Schema.Name, key.Name), Dialect.GetLiteral(hash),
        Dialect.GetLiteral(key.Table.Schema.Name), Dialect.GetLiteral(key.Table.Name), Dialect.GetLiteral(key.Name)),
      key.Name);
  }

  public virtual DdlStatement AddRefConstraint(DbRefConstraintInfo constraint) {
    var text = string.Format(Dialect.AddRefConstraint,
      constraint.FromTable.RefName, Dialect.Quote(constraint.Name), ColumnList(constraint.FromKey),
      constraint.ToTable.RefName, ColumnList(constraint.ToKey));
    if (constraint.CascadeDelete)
      text += Dialect.CascadeDelete;
    return new DdlStatement(DdlKind.AddRefConstraint, text, constraint.Name);
  }

  // ----- altering: what an upgrade needs and a fresh CREATE never does ----------------------------------------------

  public virtual DdlStatement AddColumn(DbColumnInfo column, bool asNullable = false) =>
    new(DdlKind.AddColumn,
      string.Format(Dialect.AddColumn, column.Table!.RefName, ColumnSpec(column, asNullable)), column.Name);

  /// <summary>Builds the statements that add a column to a table that exists: the ADD COLUMN, between the
  /// dialect's two statements where the column is computed and the server wants the table prepared for one.
  /// </summary>
  /// <param name="column">The column.</param>
  /// <param name="asNullable"><see langword="true"/> to add a required column as nullable, to be tightened later;
  /// otherwise, <see langword="false"/>.</param>
  /// <returns>The statements, in the order they run.</returns>
  public virtual IEnumerable<DdlStatement> AddColumnToTable(DbColumnInfo column, bool asNullable = false) {
    var table = column.Table!.RefName;
    if (column.IsComputed && Dialect.BeforeAddComputedColumn is { } before)
      yield return new DdlStatement(DdlKind.AddColumn, string.Format(before, table), column.Name);
    yield return AddColumn(column, asNullable);
    if (column.IsComputed && Dialect.AfterAddComputedColumn is { } after)
      yield return new DdlStatement(DdlKind.AddColumn, string.Format(after, table), column.Name);
  }

  /// <summary>Builds the statements that change an existing column's type, default and, where it loosens, its
  /// nullability.</summary>
  /// <param name="old">The column as it is.</param>
  /// <param name="new">The column as the model wants it.</param>
  /// <returns>The statements, one per changed part, or one restated definition on a server that alters a column that
  /// way.</returns>
  /// <remarks>Tightening to not null is not included; the upgrade asks for it separately with
  /// <see cref="SetColumnNotNull"/>, after the migration slot.</remarks>
  public virtual IEnumerable<DdlStatement> AlterColumn(DbColumnInfo old, DbColumnInfo @new) {
    var table = @new.Table!.RefName;
    var name = Dialect.Quote(@new.Name);

    // Where an alter restates the whole definition, one statement says everything; MySQL's MODIFY replaces the column,
    // so a type change that did not repeat NOT NULL would drop it.
    var restates = Provider.Supports(DbFeatures.AlterColumnRestatesDefinition);

    // SQL Server restates type and nullability together but takes no DEFAULT clause, so the default stays separate.
    var separateDefault = Provider.Supports(DbFeatures.NamedDefaultConstraints);

    if (restates)
      yield return new DdlStatement(DdlKind.AlterColumn,
        string.Format(Dialect.ModifyColumn, table, ColumnSpec(@new, omitDefault: separateDefault)), @new.Name);
    else if (!string.Equals(old.TypeSpec, @new.TypeSpec, StringComparison.OrdinalIgnoreCase))
      yield return new DdlStatement(DdlKind.AlterColumn,
        string.Format(Dialect.AlterColumnType, table, name, @new.TypeSpec), @new.Name);

    if ((!restates || separateDefault) && old.DefaultExpression != @new.DefaultExpression) {
      // A named default is dropped before it is added again; where a default is a column property, SET overwrites it.
      if (old.DefaultExpression != null && (separateDefault || @new.DefaultExpression == null))
        yield return new DdlStatement(DdlKind.AlterColumn,
          string.Format(Dialect.DropDefault, table, name, "", Dialect.Quote(old.DefaultConstraintName ?? "")), @new.Name);
      if (@new.DefaultExpression != null)
        yield return new DdlStatement(DdlKind.AlterColumn,
          string.Format(Dialect.SetDefault, table, name, @new.DefaultExpression, ""), @new.Name);
    }

    // A column becoming nullable has nothing to wait for; a restated definition has already said so.
    if (!restates && !old.IsNullable && @new.IsNullable)
      yield return new DdlStatement(DdlKind.AlterColumn,
        string.Format(Dialect.DropNotNull, table, name), @new.Name);
  }

  /// <summary>Builds the statement that adds a required column in one step, not null with its placeholder as an
  /// inline default, for a server that cannot alter a column afterwards.</summary>
  /// <param name="column">The column.</param>
  /// <returns>The statement, or <see langword="null"/> if the column's type has no placeholder.</returns>
  public virtual DdlStatement? AddRequiredColumn(DbColumnInfo column) {
    var init = column.DefaultExpression ?? GetColumnInit(column);
    if (init == null)
      return null;
    var spec = string.Format(Dialect.ColumnSpec, column.QuotedName, column.TypeSpec,
      Dialect.NotNull + string.Format(Dialect.DefaultValue, init));
    return new DdlStatement(DdlKind.AddColumn,
      string.Format(Dialect.AddColumn, column.Table!.RefName, spec), column.Name);
  }

  /// <summary>Builds the statement that fills a column's null rows with the type's placeholder, so the column can be
  /// made not null.</summary>
  /// <param name="column">The column.</param>
  /// <returns>The statement, or <see langword="null"/> if the column's type has no placeholder.</returns>
  /// <remarks>Only rows still null are filled, so a migration that ran first keeps its values.</remarks>
  public virtual DdlStatement? InitializeColumn(DbColumnInfo column) {
    var init = GetColumnInit(column);
    if (init == null)
      return null;
    return new DdlStatement(DdlKind.ColumnInit,
      string.Format(Dialect.InitializeColumn, column.Table!.RefName, column.QuotedName, init), column.Name);
  }

  /// <summary>Gets the placeholder literal for a column's type.</summary>
  /// <param name="column">The column.</param>
  /// <returns>The placeholder, or <see langword="null"/> if the type has none.</returns>
  public virtual string? GetColumnInit(DbColumnInfo column) =>
    Provider.TypeRegistry.ParseSpec(column.TypeSpec, out _)?.TypeDef.ColumnInit;

  /// <summary>Builds the statement that makes a column not null, issued after whatever fills the column.</summary>
  public virtual DdlStatement SetColumnNotNull(DbColumnInfo column) =>
    Provider.Supports(DbFeatures.AlterColumnRestatesDefinition)
      ? new DdlStatement(DdlKind.AlterColumn,
          string.Format(Dialect.ModifyColumn, column.Table!.RefName, ColumnSpec(column)), column.Name)
      : new DdlStatement(DdlKind.AlterColumn,
          string.Format(Dialect.SetNotNull, column.Table!.RefName, column.QuotedName), column.Name);

  /// <summary>Builds the statement that renames a table.</summary>
  /// <remarks>The new name is written unqualified; a rename does not move a table between schemas, and a server
  /// handed a qualified target rejects it.</remarks>
  public virtual DdlStatement RenameTable(DbTableInfo old, DbTableInfo @new) =>
    new(DdlKind.RenameTable,
      string.Format(Dialect.RenameTable, Dialect.GetTableRef(old), Dialect.Quote(@new.Name)), @new.Name);

  public virtual DdlStatement RenameColumn(DbColumnInfo old, DbColumnInfo @new) =>
    new(DdlKind.RenameColumn,
      string.Format(Dialect.RenameColumn, @new.Table!.RefName, old.QuotedName, Dialect.Quote(@new.Name)), @new.Name);

  public virtual DdlStatement DropColumn(DbColumnInfo column) =>
    new(DdlKind.DropColumn,
      string.Format(Dialect.DropColumn, column.Table!.RefName, column.QuotedName), column.Name);

  /// <summary>Builds the statement that runs after one of a table's columns was dropped or altered, from
  /// <see cref="SqlDialect.AfterTableModified"/>.</summary>
  /// <param name="table">The table.</param>
  /// <returns>The statement, or <see langword="null"/> where the server needs none.</returns>
  public virtual DdlStatement? AfterTableModified(DbTableInfo table) =>
    Dialect.AfterTableModified is { } text
      ? new DdlStatement(DdlKind.AlterColumn, string.Format(text, table.RefName), table.Name)
      : null;

  public virtual DdlStatement DropTable(DbTableInfo table) =>
    new(DdlKind.DropTable, string.Format(Dialect.DropTable, table.RefName), table.Name);

  /// <summary>Builds the statements that drop a key: a constraint drop for a primary key, an index drop otherwise, and
  /// the dialect's own statements for a full-text index that came with more than itself.</summary>
  public virtual IEnumerable<DdlStatement> DropKey(DbKeyInfo key) {
    if (key.IsPrimaryKey)
      yield return new DdlStatement(DdlKind.DropConstraint,
        string.Format(Dialect.DropPrimaryKey, key.Table.RefName, Dialect.Quote(key.Name)), key.Name);
    else if (key.IndexType == DbIndexType.FullText && Dialect.BuildDropFullTextIndex(key) is { } statements)
      foreach (var text in statements)
        yield return new DdlStatement(DdlKind.DropIndex, text, key.Name);
    else {
      yield return new DdlStatement(DdlKind.DropIndex,
        string.Format(Dialect.DropIndex, Dialect.QualifiedName(key.Table.Schema.Name, key.Name), key.Table.RefName,
          Dialect.Quote(key.Name)), key.Name);
      foreach (var statement in CleanUpSpatialIndex(key, DdlKind.DropIndex))
        yield return statement;
    }
  }

  /// <summary>Builds the statements that remove what a spatial index left on the server beside itself, to run after
  /// the index or its table is dropped.</summary>
  /// <param name="key">An index; one that is not over a single geographic point column yields nothing.</param>
  /// <param name="kind">The kind of the drop the statements belong to.</param>
  /// <returns>The statements; empty where the server keeps nothing beside the index.</returns>
  public virtual IEnumerable<DdlStatement> CleanUpSpatialIndex(DbKeyInfo key, DdlKind kind) {
    if (key.Columns is [{ Column.IsGeoPoint: true }] && Dialect.BuildSpatialIndexCleanup(key) is { } statements)
      foreach (var text in statements)
        yield return new DdlStatement(kind, text, key.Name);
  }

  public virtual DdlStatement DropRefConstraint(DbRefConstraintInfo constraint) =>
    new(DdlKind.DropConstraint,
      string.Format(Dialect.DropConstraint, constraint.FromTable.RefName, Dialect.Quote(constraint.Name)), constraint.Name);

  public virtual DdlStatement AddCheckConstraint(DbCheckConstraintInfo check) {
    ArgumentNullException.ThrowIfNull(check);
    return new DdlStatement(DdlKind.AddCheckConstraint,
      string.Format(Dialect.AddCheckConstraint, check.Table.RefName, Dialect.Quote(check.Name), check.Expression),
      check.Name);
  }

  public virtual DdlStatement DropCheckConstraint(DbCheckConstraintInfo check) {
    ArgumentNullException.ThrowIfNull(check);
    return new DdlStatement(DdlKind.DropConstraint,
      string.Format(Dialect.DropCheckConstraint, check.Table.RefName, Dialect.Quote(check.Name)), check.Name);
  }

  public virtual DdlStatement AlterSequence(DbSequenceInfo sequence) =>
    new(DdlKind.AlterSequence,
      string.Format(Dialect.AlterSequence, Dialect.QualifiedName(sequence.Schema.Name, sequence.Name), sequence.Increment),
      sequence.Name);

  public virtual DdlStatement DropSequence(DbSequenceInfo sequence) =>
    new(DdlKind.DropSequence,
      string.Format(Dialect.DropSequence, Dialect.QualifiedName(sequence.Schema.Name, sequence.Name)), sequence.Name);

  public virtual DdlStatement DropSchema(DbSchemaInfo schema) =>
    new(DdlKind.DropSchema, string.Format(Dialect.DropSchema, Dialect.Quote(schema.Name)), schema.Name);

  // ----- pieces ---------------------------------------------------------------------------------------------------

  /// <summary>Builds a key's column list, each column with its direction where it descends.</summary>
  protected virtual string ColumnList(DbKeyInfo key) =>
    string.Join(", ", key.Columns.Select(c => c.Column.QuotedName + (c.Descending ? Dialect.Descending : "")));
}
