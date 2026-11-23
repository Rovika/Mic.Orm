using System.Text.RegularExpressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Orm.Db.Model.Construction;

/// <summary>Builds a db model from an entity model for one provider: entities become tables, members columns, keys
/// keys and references foreign-key constraints, every name passing through the naming policy.</summary>
/// <remarks>A feature the provider lacks is dropped with a warning where only performance is lost, such as an
/// included column, a clustering or an index column's direction, and is an error where a rule would be lost, such as
/// a filtered index.</remarks>
internal class DbModelBuilder {
  /// <summary>Matches single-quoted text whole, a doubled quote inside it included, or a braced word outside it,
  /// captured.</summary>
  static readonly Regex BracedNameOrQuotedText = new(@"'(?:[^']|'')*'|\{(\w+)\}", RegexOptions.Compiled);

  /// <summary>Specifies what follows the member's name in the name of the check made for a JSON member whose column
  /// is text.</summary>
  internal const string JsonCheckSuffix = "IsJson";

  /// <summary>Initializes a new instance of the <see cref="DbModelBuilder"/> class for one entity model and one
  /// database, with a build context of its own.</summary>
  public DbModelBuilder(EntityApp app, EntityAppModel model, DbSettings settings) {
    _context = new BuilderContext(app, model);
    _settings = settings;
  }

  readonly BuilderContext _context;
  readonly DbSettings _settings;

  public DbModel Build() {
    var started = System.Diagnostics.Stopwatch.StartNew();
    var context = _context;
    var provider = _settings.Provider;
    var model = context.Model;
    var dbModel = new DbModel(model.Name, provider) { EntityModel = model, Options = _settings.Options };

    BuildSchemas(context, provider, dbModel);
    BuildTables(context, provider, dbModel);
    BuildSequences(context, provider, dbModel);
    BuildKeys(context, provider, dbModel);
    BuildRefConstraints(context, provider, dbModel);
    BuildChecks(context, provider, dbModel);
    CheckNameLengths(context, provider, dbModel);

    // Last, once every table and column exists: the names every statement writes, the filters over them, and the
    // views' SQL, which is written against those names.
    DbModelBuilderHelper.CompleteSetup(dbModel);
    BuildTableFilters(context, dbModel);
    new ViewSourceBuilder(context, provider, dbModel).Build();


    dbModel.BuildReport.Add(context.Diagnostics);
    dbModel.BuildReport.TableCount = dbModel.Tables.Count;
    dbModel.BuildReport.Elapsed = started.Elapsed;
    return dbModel;
  }

  /// <summary>Reports an error for every generated name longer than the server accepts.</summary>
  /// <remarks>PostgreSQL truncates a long name silently, and two names that differ only past the limit become one
  /// object.</remarks>
  private static void CheckNameLengths(BuilderContext context, DbProvider provider, DbModel dbModel) {
    var max = provider.MaxNameLength;
    if (max <= 0)
      return;

    void Check(string name, string what) {
      if (name.Length > max)
        context.AddError(
          $"the generated name is {name.Length} characters and {provider.ServerKind} accepts {max} — it would be "
          + "truncated, and a truncated name can collide with another. Shorten it, or name it outright.", $"{what} '{name}'");
    }

    foreach (var schema in dbModel.Schemas)
      Check(schema.Name, "schema");
    foreach (var sequence in dbModel.Sequences)
      Check(sequence.Name, "sequence");
    foreach (var table in dbModel.Tables) {
      Check(table.Name, "table");
      foreach (var column in table.Columns)
        Check(column.Name, $"column {table.Name}.");
      foreach (var key in table.Keys)
        Check(key.Name, "key");
      foreach (var constraint in table.RefConstraints)
        Check(constraint.Name, "constraint");
      foreach (var check in table.CheckConstraints)
        Check(check.Name, "check constraint");
    }
  }

  private static void BuildSchemas(BuilderContext context, DbProvider provider, DbModel dbModel) {
    var policy = context.App.Settings.NamingPolicy;
    var hasSchemas = provider.Supports(DbFeatures.Schemas);

    if (!hasSchemas && context.Model.Schemas.Count > 1 && !dbModel.Options.HasFlag(DbOptions.AddSchemaToTableNames))
      context.AddWarning(
        $"{provider.ServerKind} has no schemas; all {context.Model.Schemas.Count} schemas collapse into one namespace, "
        + "so two tables of the same name in different schemas would collide. Add "
        + $"{nameof(DbOptions)}.{nameof(DbOptions.AddSchemaToTableNames)} to "
        + $"{nameof(DbSettings)}.{nameof(DbSettings.Options)} to keep them apart.");

    foreach (var schema in context.Model.Schemas) {
      var name = hasSchemas ? policy.GetSchemaName(schema.Name) : string.Empty;
      var dbSchema = dbModel.GetOrAddSchema(name);
      // The link is how IsManaged reaches the database side; a db object does not carry it.
      dbSchema.EntitySchema ??= schema;
    }
  }

  private static void BuildTables(BuilderContext context, DbProvider provider, DbModel dbModel) {
    var policy = context.App.Settings.NamingPolicy;

    foreach (var entity in GetEntitiesInTableOrder(context.Model.Entities)) {
      var schemaName = provider.Supports(DbFeatures.Schemas)
        ? entity.Schema != null ? policy.GetSchemaName(entity.Schema.Name) : provider.DefaultSchema
        : string.Empty;
      var dbSchema = dbModel.GetOrAddSchema(schemaName);

      var tableName = GetTableName(policy, entity, dbModel.Options);
      // Where the server has no schemas, the schema can be kept as part of the table name.
      if (!provider.Supports(DbFeatures.Schemas) && dbModel.Options.HasFlag(DbOptions.AddSchemaToTableNames)
          && entity.Schema != null)
        tableName = policy.GetSchemaPrefixedTableName(entity.Schema.Name, tableName);

      // A temp table is named as the server names one, "#Name" or "ORA$PTT_Name", and never qualified.
      if (entity.IsTempTable)
        tableName = provider.SqlDialect.GetTempTableName(tableName);

      // A function's table is named as the function is, which the module declared outright.
      if (entity.TableFunction is { } declaredFunction)
        tableName = declaredFunction.Name;

      var table = new DbTableInfo(tableName, dbSchema) { Entity = entity, IsTemporary = entity.IsTempTable };
      if (entity.View is { } view)
        table.View = BuildView(context, provider, view, tableName, dbSchema);
      if (entity.TableFunction is { } function)
        table.TableFunction = BuildTableFunction(context, provider, function);
      foreach (var member in entity.Members) {
        if (member.Kind is MemberKind.List or MemberKind.Transient)
          continue;                                       // no column: a list is a query, a transient never stored
        if (member.Kind == MemberKind.Reference)
          continue;                                       // the reference's own FK columns are separate members
        var column = BuildColumn(context, provider, member);
        if (column != null)
          table.AddColumn(column);
      }
      BuildComputedColumns(context, provider, table);
      dbModel.AddTable(table);
    }
  }

  /// <summary>Orders the entities for their tables: model order, except that a view comes after the views it
  /// depends on, so that the model's tables create in one pass.</summary>
  private static List<EntityTypeInfo> GetEntitiesInTableOrder(IReadOnlyList<EntityTypeInfo> entities) {
    var ordered = new List<EntityTypeInfo>(entities.Count);
    var placed = new HashSet<EntityTypeInfo>();
    foreach (var entity in entities)
      Place(entity);
    return ordered;

    // ----- local functions
    void Place(EntityTypeInfo entity) {
      if (!placed.Add(entity))
        return;
      if (entity.View is { } view)
        foreach (var dependency in view.DependsOn)
          if (dependency.IsView)
            Place(dependency);
      ordered.Add(entity);
    }
  }

  /// <summary>Gets the name of an entity's table: the policy's name, pluralized where the database asks for it and
  /// the entity is a plain table whose name is not given outright.</summary>
  private static string GetTableName(NamingPolicy policy, EntityTypeInfo entity, DbOptions options) {
    var name = policy.GetTableName(entity);
    if (!options.HasFlag(DbOptions.PluralizeTableNames) || entity.TableName != null || entity.IsReadOnly)
      return name;
    return entity.PluralName ?? policy.GetPluralName(name);
  }

  /// <summary>Builds the function part of a function's table: the parameters typed for this server, and the body
  /// written for it where MIC defines the function.</summary>
  private static DbTableFunctionInfo BuildTableFunction(BuilderContext context, DbProvider provider,
      TableFunctionInfo function) {
    var location = $"table function '{function.Name}'";
    var dbFunction = new DbTableFunctionInfo { IsManaged = function.IsManaged };
    // A server with no table functions gets no body and no error for lacking one: the warning says it all.
    var supported = provider.SqlDialect.TableFunctionSource != null;
    if (!supported)
      context.AddWarning($"{provider.ServerKind} has no table-valued functions; '{function.Entity.Name}' cannot be "
        + "read on this database.", location);
    var position = 0;
    foreach (var parameter in function.Parameters) {
      var typeInfo = provider.TypeRegistry.MapType(parameter.Type);
      if (typeInfo == null) {
        context.AddError($"parameter '{parameter.Name}' is of type '{parameter.Type.Name}', which no database type is "
          + "registered for.", location);
        continue;
      }
      dbFunction.AddParameter(new DbParameterInfo(parameter.Name, typeInfo.TypeSpec) { Position = ++position });
    }
    if (function.IsManaged && supported) {
      dbFunction.Body = function.FindBody(provider.ServerKind);
      if (dbFunction.Body == null)
        context.AddError($"has no body written for {provider.ServerKind}.", location);
    }
    return dbFunction;
  }

  /// <summary>Gives each computed column its expression, from a JSON document's path or from the SQL written for
  /// this server, and leaves it nullable, since the server decides.</summary>
  private static void BuildComputedColumns(BuilderContext context, DbProvider provider, DbTableInfo table) {
    foreach (var column in table.Columns) {
      if (column.Member is not { } member || (member.FromJson == null && member.DbComputed == null))
        continue;
      var expression = member.FromJson is { } fromJson
        ? BuildJsonExpression(context, provider, table, member, fromJson)
        : BuildSqlExpression(context, provider, table, member, member.DbComputed!);
      if (expression == null)
        continue;
      column.ComputedExpression = expression;
      column.DbFlags |= DbColumnFlags.Computed;
      if (member.FromJson?.Stored ?? member.DbComputed!.Stored)
        column.DbFlags |= DbColumnFlags.Stored;
      column.IsNullable = true;
    }
  }

  /// <summary>Builds the dialect's read of a path out of a JSON document's column, cast to the member's type.
  /// </summary>
  private static string? BuildJsonExpression(BuilderContext context, DbProvider provider, DbTableInfo table,
      EntityMemberInfo member, FromJsonInfo fromJson) {
    var document = FindColumn(table, fromJson.JsonMember);
    if (document == null) {
      context.AddError($"reads '{fromJson.JsonMember.Name}', which produced no column.", $"{member.Entity?.Name}.{member.Name}");
      return null;
    }
    var dialect = provider.SqlDialect;
    var typeInfo = provider.TypeRegistry.MapMember(member, out _);
    return dialect.BuildJsonValue(dialect.Quote(document.Name), dialect.GetLiteral(fromJson.Path), typeInfo);
  }

  /// <summary>Builds the expression written for this server, with each member named as <c>{Name}</c> replaced by its
  /// column, quoted as the server quotes.</summary>
  private static string? BuildSqlExpression(BuilderContext context, DbProvider provider, DbTableInfo table,
      EntityMemberInfo member, DbComputedInfo computed) {
    var location = $"{member.Entity?.Name}.{member.Name}";
    var sql = computed.FindSql(provider.ServerKind);
    if (sql == null) {
      context.AddError($"is computed, and no [SqlExpression] is written for {provider.ServerKind}.", location);
      return null;
    }
    return ResolveMemberNames(context, provider, table, sql, location);
  }

  /// <summary>Replaces each member of the table's entity named as <c>{Name}</c> in SQL text with its column, quoted
  /// as the server quotes, or returns <see langword="null"/> where a name is no column member, which is reported.
  /// </summary>
  /// <remarks>A braced word inside single-quoted text is text and is left as written.</remarks>
  private static string? ResolveMemberNames(BuilderContext context, DbProvider provider, DbTableInfo table, string sql,
      string location) {
    var unresolved = false;
    var expression = BracedNameOrQuotedText.Replace(sql, match => {
      if (!match.Groups[1].Success)
        return match.Value;
      var named = table.Entity?.FindMember(match.Groups[1].Value);
      var column = named == null ? null : FindColumn(table, named);
      if (column != null)
        return provider.SqlDialect.Quote(column.Name);
      context.AddError($"names '{match.Value}' in its expression, which is no column member of the entity.", location);
      unresolved = true;
      return match.Value;
    });
    return unresolved ? null : expression;
  }

  /// <summary>Determines whether SQL text holds a braced word outside single-quoted text, which a declaration reads
  /// as a member's name.</summary>
  /// <param name="sql">The text of a condition or an expression.</param>
  /// <returns><see langword="true"/> if the text holds such a word; otherwise, <see langword="false"/>.</returns>
  internal static bool HasBracedNames(string sql) {
    foreach (Match match in BracedNameOrQuotedText.Matches(sql))
      if (match.Groups[1].Success)
        return true;
    return false;
  }

  /// <summary>Builds the check constraints of every table: the check that a JSON member held as text is JSON, and
  /// the entity's declarations, for each name the one written for this server, or else the one written for every
  /// server.</summary>
  /// <remarks>A check is named by <see cref="DbCheckConstraintInfo.GetManagedName"/> from its condition with the
  /// columns written in, so a renamed column gives it a new name.</remarks>
  private static void BuildChecks(BuilderContext context, DbProvider provider, DbModel dbModel) {
    foreach (var table in dbModel.Tables) {
      if (table.Entity is not { } entity)
        continue;
      if (table.IsView || table.IsTemporary || table.IsTableFunction) {
        if (entity.Checks.Count > 0)
          context.AddError("declares a check constraint, which only a table takes.", entity.Name);
        continue;
      }
      AddJsonTextChecks(provider, table);
      foreach (var declared in entity.Checks.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) {
        var check = declared.FirstOrDefault(c => c.ServerKind == provider.ServerKind)
          ?? declared.FirstOrDefault(c => c.ServerKind == null);
        // Written for other servers only.
        if (check == null)
          continue;
        var expression = ResolveMemberNames(context, provider, table, check.Expression,
          $"[Check] '{check.Name}' on '{entity.Name}'");
        if (expression == null)
          continue;
        var name = DbCheckConstraintInfo.GetManagedName(table.Name, check.Name, expression);
        // A declared check with the name and the condition of a JSON check is that check.
        if (!table.CheckConstraints.Any(c => c.Name == name))
          table.AddCheckConstraint(new DbCheckConstraintInfo(name, table, expression));
      }
    }
  }

  /// <summary>Adds, for each JSON member of a table whose column is text on this server, the check that the text is
  /// JSON.</summary>
  /// <remarks>A server with a JSON type refuses other text itself, and a member that names its column type is left
  /// as declared. The check is named as a declared one is, by the member's name followed by <c>IsJson</c>, so an
  /// upgrade adds and drops it by name.</remarks>
  private static void AddJsonTextChecks(DbProvider provider, DbTableInfo table) {
    var dialect = provider.SqlDialect;
    if (dialect.JsonTextCheck == null || provider.TypeRegistry.FindSpecial(DbSpecialType.Json) != null)
      return;
    foreach (var column in table.Columns) {
      if (column.Member is not { IsJson: true, DbTypeSpec: null } member)
        continue;
      var expression = string.Format(dialect.JsonTextCheck, dialect.Quote(column.Name));
      table.AddCheckConstraint(new DbCheckConstraintInfo(
        DbCheckConstraintInfo.GetManagedName(table.Name, member.Name + JsonCheckSuffix, expression), table,
        expression));
    }
  }

  /// <summary>Builds the view part of a view entity's table: managed unless the view exists already, materialized
  /// where asked and the server can.</summary>
  /// <remarks>Where the server has no materialized views the view is an ordinary one, with a warning, since only
  /// performance is lost; a refresh of it does nothing.</remarks>
  private static DbViewInfo BuildView(BuilderContext context, DbProvider provider, ViewInfo view, string name,
      DbSchemaInfo schema) {
    var materialized = view.IsMaterialized;
    if (materialized && !provider.Supports(DbFeatures.MaterializedViews)) {
      context.AddWarning(
        $"is a materialized view, which {provider.ServerKind} does not have; it is an ordinary view here.",
        view.Entity.Name);
      materialized = false;
    }
    return new DbViewInfo(name, schema) {
      IsManaged = view.Kind != ViewDefinitionKind.Existing,
      IsMaterialized = materialized,
    };
  }

  private static DbColumnInfo? BuildColumn(BuilderContext context, DbProvider provider, EntityMemberInfo member) {
    var location = $"{member.Entity?.Name}.{member.Name}";

    var typeInfo = provider.TypeRegistry.MapMember(member, out var error);
    if (typeInfo == null) {
      context.AddError(error ?? "cannot be mapped to a database type.", location);
      return null;
    }

    // A legacy type is reached only by name, so this fires only when someone wrote it.
    if (typeInfo.TypeDef.IsLegacy)
      context.AddWarning(
        $"uses '{typeInfo.TypeDef.Name}', which {provider.ServerKind} has superseded.", location);

    var column = new DbColumnInfo(context.App.Settings.NamingPolicy.GetColumnName(member), typeInfo.ToTypeRef(),
        member.IsNullable) {
      Member = member,
      Size = typeInfo.Size,
      Precision = typeInfo.Precision,
      Scale = typeInfo.Scale,
      DefaultExpression = member.DefaultExpression,
    };

    // The row version is an ordinary integer column the ORM counts; the flag marks it for readers of the db model and
    // puts nothing in the DDL.
    if (member.AutoValue == AutoType.RowVersion)
      column.DbFlags |= DbColumnFlags.RowVersion;
    // A computed member is db-generated too, and gets its expression once every column of the table exists.
    if (member.AutoValue == AutoType.Identity) {
      if (provider.Supports(DbFeatures.IdentityColumns))
        column.DbFlags |= DbColumnFlags.Identity;
      else
        context.AddWarning(
          $"is an identity column, which {provider.ServerKind} cannot declare; the designation is dropped and the "
          + "application must supply the value.", location);
    }
    return column;
  }

  /// <summary>Adds a sequence for each one the modules declared, with its start, increment and type, and links every
  /// <c>[Sequence]</c> column to the one its member resolved to.</summary>
  /// <remarks>Where the server has no sequences each is dropped with a warning and its columns are ordinary ones the
  /// application fills.</remarks>
  private static void BuildSequences(BuilderContext context, DbProvider provider, DbModel dbModel) {
    var policy = context.App.Settings.NamingPolicy;
    var hasSchemas = provider.Supports(DbFeatures.Schemas);
    var built = new Dictionary<SequenceInfo, DbSequenceInfo>(ReferenceEqualityComparer.Instance);
    foreach (var sequence in context.Model.Sequences) {
      if (!provider.Supports(DbFeatures.Sequences)) {
        context.AddWarning(
          $"is a sequence, which {provider.ServerKind} cannot declare; it is dropped, and the members drawing from it "
          + "are ordinary columns the application must fill.", $"sequence '{sequence}'");
        continue;
      }
      var schemaName = hasSchemas
        ? sequence.Schema != null ? policy.GetSchemaName(sequence.Schema.Name) : provider.DefaultSchema
        : string.Empty;
      var dbSequence = new DbSequenceInfo(sequence.Name, dbModel.GetOrAddSchema(schemaName)) {
        StartValue = sequence.Start,
        Increment = sequence.Increment,
        DataType = sequence.DataType,
      };
      dbModel.AddSequence(dbSequence);
      built[sequence] = dbSequence;
    }
    foreach (var table in dbModel.Tables)
      foreach (var column in table.Columns)
        if (column.Member is { Sequence: { } sequence } && built.TryGetValue(sequence, out var dbSequence))
          column.Sequence = dbSequence;
  }

  /// <summary>Builds every table's keys. A view's or a temp table's keys stay off the database: the primary key
  /// identifies rows in memory and nothing else.</summary>
  private static void BuildKeys(BuilderContext context, DbProvider provider, DbModel dbModel) {
    foreach (var table in dbModel.Tables) {
      var entity = table.Entity;
      if (entity == null || table.IsView || table.IsTemporary || table.IsTableFunction)
        continue;

      foreach (var key in entity.Keys) {
        var dbKey = BuildKey(context, provider, table, key, dbModel.Options);
        if (dbKey != null)
          table.AddKey(dbKey);
      }
    }
  }

  private static DbKeyInfo? BuildKey(BuilderContext context, DbProvider provider, DbTableInfo table,
      EntityKeyInfo key, DbOptions options) {
    var location = $"{table.Entity!.Name}.{key.Name}";

    var flags = key.Flags;
    var ordered = provider.Supports(DbFeatures.OrderedColumnsInIndexes);

    // Clustering is meaningless only where the server has neither clustered storage nor a clustering marker.
    if (flags.HasFlag(KeyFlags.Clustered)
        && !provider.Supports(DbFeatures.ClusteredStorage) && !provider.Supports(DbFeatures.ClusteringMarker)) {
      context.AddWarning($"is clustered, which {provider.ServerKind} does not support; the designation is dropped.",
        location);
      flags &= ~KeyFlags.Clustered;
    }

    // Where the clustered index is the storage, the server rejects included columns and a filter on it.
    if (flags.HasFlag(KeyFlags.Clustered) && provider.Supports(DbFeatures.ClusteredStorage)
        && (key.Index?.IncludedMembers.Count > 0 || key.Index?.Filter != null)) {
      context.AddWarning(
        $"is clustered AND {(key.Index?.Filter != null ? "filtered" : "carries included columns")}, which "
        + $"{provider.ServerKind} does not allow on the index that IS the table; the clustering is dropped and the "
        + "index is created as an ordinary one.", location);
      flags &= ~KeyFlags.Clustered;
    }

    // A server that names the primary key itself, as MySQL does, has its name taken here so the comparer matches it.
    var keyName = flags.HasFlag(KeyFlags.PrimaryKey) && provider.PrimaryKeyName != null
      ? provider.PrimaryKeyName
      : key.Name;

    // An index on every foreign key, where the application asked for one and the server does not do it itself.
    if (flags.HasFlag(KeyFlags.ForeignKey) && options.HasFlag(DbOptions.AutoIndexForeignKeys)
        && !provider.Supports(DbFeatures.ForeignKeysAutoIndexed))
      flags |= KeyFlags.Index;

    // The access method is always resolved to a concrete type here, since introspection reports one back.
    var indexType = key.Index?.IndexType ?? DbIndexType.Default;

    // An index over a geographic point is the server's spatial index, whatever it is called there.
    if (flags.HasFlag(KeyFlags.Index) && indexType is DbIndexType.Default or DbIndexType.Spatial
        && key.MembersExpanded is [{ Member: { DataType: var pointType, IsNullable: var nullable } }]
        && pointType == typeof(GeoPoint)) {
      if (nullable && provider.Supports(DbFeatures.SpatialIndexNeedsNotNullMySql)) {
        context.AddWarning($"is a spatial index over a nullable point, which {provider.ServerKind} does not allow; "
          + "no index is created.", location);
        return null;
      }
      indexType = provider.SpatialIndexType;
    }

    // An index over a vector is the server's vector index, of the member's metric.
    var vectorMember = flags.HasFlag(KeyFlags.Index) && indexType == DbIndexType.Default
        && key.MembersExpanded is [{ Member: { IsVector: true } indexed }]
      ? indexed
      : null;
    if (vectorMember != null) {
      if (provider.VectorIndexType == DbIndexType.Default) {
        context.AddWarning($"is an index over a vector, and the ORM creates no vector index on {provider.ServerKind}; "
          + "no index is created.", location);
        return null;
      }
      indexType = provider.VectorIndexType;
    }

    if (indexType != DbIndexType.Default && !provider.SupportsIndexType(indexType)) {
      context.AddWarning(
        $"asks for a {indexType} index, which {provider.ServerKind} does not have; an ordinary "
        + $"{provider.DefaultIndexType} index is created instead. An access method costs performance and not "
        + "correctness, so this is a warning rather than an error.", location);
      indexType = DbIndexType.Default;
    }

    var dbKey = new DbKeyInfo(keyName, table, flags) {
      EntityKey = key,
      IndexType = indexType == DbIndexType.Default ? provider.DefaultIndexType : indexType,
      VectorMetric = vectorMember?.VectorMetric,
    };

    foreach (var km in key.MembersExpanded) {
      var column = FindColumn(table, km.Member);
      if (column == null) {
        context.AddError($"names member '{km.Member.Name}', which produced no column.", location);
        return null;
      }
      if (km.Descending && !ordered)
        context.AddWarning(
          $"orders '{km.Member.Name}' descending, which {provider.ServerKind} does not support in an index; "
          + "the direction is dropped.", location);
      dbKey.AddColumn(column, km.Descending && ordered);
    }

    if (key.Index != null)
      BuildIndexOptions(context, provider, table, dbKey, key.Index, location);
    return dbKey;
  }

  private static void BuildIndexOptions(BuilderContext context, DbProvider provider, DbTableInfo table,
      DbKeyInfo dbKey, IndexOptions options, string location) {
    // A filtered index is an error rather than a warning: a unique filtered index is a constraint.
    if (options.Filter != null) {
      if (!provider.Supports(DbFeatures.FilterInIndexes))
        context.AddError(
          $"is a filtered index, which {provider.ServerKind} does not support — the filter cannot be dropped, because "
          + "a unique filtered index is a constraint and losing it changes what the database enforces.", location);
      else
        dbKey.Filter = options.Filter;
    }

    if (options.IncludedMembersExpanded.Count == 0)
      return;
    if (!provider.Supports(DbFeatures.IncludeColumnsInIndexes)) {
      context.AddWarning(
        $"has included columns, which {provider.ServerKind} does not support; they are dropped and the index covers "
        + "less, which costs performance and nothing else.", location);
      return;
    }
    foreach (var member in options.IncludedMembersExpanded) {
      var column = FindColumn(table, member);
      if (column != null)
        dbKey.AddIncludedColumn(column);
    }
  }

  /// <summary>Builds the foreign-key constraints of every table from the model's references.</summary>
  /// <remarks>None where <see cref="DbOptions.UseRefIntegrity"/> is off, and none, with a warning, where the server
  /// cannot enforce them.</remarks>
  private static void BuildRefConstraints(BuilderContext context, DbProvider provider, DbModel dbModel) {
    if (!dbModel.Options.HasFlag(DbOptions.UseRefIntegrity))
      return;
    var enforced = provider.Supports(DbFeatures.ReferentialConstraints);
    var warnedOnce = false;

    foreach (var table in dbModel.Tables) {
      var entity = table.Entity;
      if (entity == null || table.IsView || table.IsTemporary || table.IsTableFunction)
        continue;

      foreach (var member in entity.Members) {
        var reference = member.ReferenceInfo;
        if (reference == null)
          continue;
        var location = $"{entity.Name}.{member.Name}";

        if (!reference.HasDbConstraint)
          continue;

        if (!enforced) {
          if (!warnedOnce) {
            warnedOnce = true;
            context.AddWarning(
              $"{provider.ServerKind} does not enforce referential constraints; the model's foreign keys are not "
              + "emitted, and referential integrity rests on the application alone.");
          }
          continue;
        }

        var fromKey = table.Keys.FirstOrDefault(k => ReferenceEquals(k.EntityKey, reference.FromKey));
        var toTable = dbModel.Tables.FirstOrDefault(t => ReferenceEquals(t.Entity, reference.TargetEntity));
        var toKey = toTable?.Keys.FirstOrDefault(k => ReferenceEquals(k.EntityKey, reference.ToKey));
        if (fromKey == null || toKey == null) {
          context.AddError("could not be mapped to a foreign-key constraint.", location);
          continue;
        }

        if (!toKey.IsPrimaryKey && provider.Supports(DbFeatures.ForeignKeyMustTargetPrimaryKey)) {
          context.AddError(
            $"targets a unique key rather than a primary key, which {provider.ServerKind} does not allow.", location);
          continue;
        }

        table.AddRefConstraint(new DbRefConstraintInfo(fromKey.Name, fromKey, toKey) {
          Reference = reference,
          CascadeDelete = reference.CascadeDelete,
        });
      }
    }
  }

  /// <summary>Builds, for every table whose entity has filters, the templates of the conditions the query engine
  /// attaches to each read of the table, one per filter: each built over a placeholder row, bound to this db model and
  /// transformed once, so that each query instantiates the template instead of building the condition.</summary>
  /// <remarks>A filter that does not build against this database, such as one naming a member the database does not
  /// store, is an error on the entity and leaves the table unfiltered.</remarks>
  private static void BuildTableFilters(BuilderContext context, DbModel dbModel) {
    foreach (var table in dbModel.Tables) {
      if (table.Entity is not { Filters.Count: > 0 } entity)
        continue;
      var placeholder = new TableNode(table);
      try {
        var filters = new List<LinqTableFilter>();
        foreach (var (owner, condition) in entity.BuildFilters(placeholder.Row)) {
          var translated = QueryTranslator.TranslateCondition(condition, dbModel);
          filters.Add(new LinqTableFilter(owner, new QueryTreeTemplate(translated,
            new Dictionary<string, QueryNode> { [QueryTreeTemplate.RowPlaceholder] = placeholder.Row })));
        }
        table.Filters = filters;
      } catch (Exception ex) {
        context.AddError($"the filter could not be prepared for '{dbModel.Name}': {ex.Message}", entity.Name);
      }
    }
  }

  private static DbColumnInfo? FindColumn(DbTableInfo table, EntityMemberInfo member) =>
    table.Columns.FirstOrDefault(c => ReferenceEquals(c.Member, member));
}
