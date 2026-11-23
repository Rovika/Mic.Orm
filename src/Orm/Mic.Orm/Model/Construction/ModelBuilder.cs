using System.Reflection;
using Mic.Common;
using Mic.Linq.Translation;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Orm.Model.Construction;

/// <summary>Builds an <see cref="EntityAppModel"/> from an <see cref="EntityApp"/>: its modules, their registered
/// interfaces and the attributes on them.</summary>
/// <remarks>Never throws for a model problem; every fault is a diagnostic in <see cref="EntityAppModel.BuildReport"/>,
/// so one run reports everything wrong. The passes run in order, each depending on what the previous one established:
/// members before keys, keys before references, references before many-to-many.</remarks>
public class ModelBuilder {
  /// <summary>Builds the model for an application.</summary>
  /// <param name="app">The application whose declarations to read.</param>
  /// <returns>The model, built or not; <see cref="EntityAppModel.BuildReport"/> says which.</returns>
  /// <remarks>The extensions are not called here; <see cref="ApplyExtensions"/> is a second step, once the model is on
  /// its application.</remarks>
  public EntityAppModel Build(EntityApp app) {
    ArgumentNullException.ThrowIfNull(app);
    var started = System.Diagnostics.Stopwatch.StartNew();
    var model = new EntityAppModel(app.Name, app.Version);
    var context = new BuilderContext(app, model);

    BuildModules(context);
    BuildEntities(context);
    BuildSequences(context);
    ApplyReplacements(context);
    new MemberBuilder(context).BuildMembers();
    // Before the attributes: [Json(Discriminator = …)] reads its types off the model.
    ApplyJsonTypeDeclarations(context);
    ApplyAttributes(context);
    ResolveSequenceMembers(context);
    // After the attributes, since a declared view name overrides the attribute's table name.
    BuildViews(context);
    BuildTableFunctions(context);
    MarkTempTables(context);
    new ReferenceBuilder(context).BuildReferences();
    new MemberBuilder(context).ApplyDefaults();
    new ModelValidator(context).Validate();

    // Last, and only for a sound model: accessors over members the earlier passes never finished would be a worse
    // failure than a missing accessor.
    if (!context.HasErrors)
      foreach (var entity in context.Model.Entities) {
        MemberValueAccessorHelper.AssignAccessors(entity);
        entity.CompleteSetup();
      }

    if (!context.HasErrors)
      ApplyCacheDeclarations(context);

    if (!context.HasErrors) {
      AssignTopologicalIndexes(context.Model);
      MarkUniqueKeyMembers(context.Model);
    }

    model.BuildReport.Add(context.Diagnostics);
    model.BuildReport.EntityCount = model.Entities.Count;
    model.BuildReport.Elapsed = started.Elapsed;

    return model;
  }

  /// <summary>Gives every entity of an application's built model to the extensions, in the order they were added,
  /// and checks the filters they attached.</summary>
  /// <param name="app">The application, with its model built and set as <see cref="EntityApp.Model"/>.</param>
  /// <returns><see langword="true"/> if every extension ran without error; otherwise, <see langword="false"/>, with
  /// the errors in the model's <see cref="EntityAppModel.BuildReport"/>.</returns>
  /// <remarks>An exception from an extension is a diagnostic under the extension's name. The extensions see finished
  /// entities, so this runs once the model is built and on its application.</remarks>
  public bool ApplyExtensions(EntityApp app) {
    ArgumentNullException.ThrowIfNull(app);
    var model = app.Model ?? throw new InvalidOperationException("The application has no model to apply the extensions to.");
    var context = new BuilderContext(app, model);

    foreach (var extension in app.Extensions)
      extension.BuildContext = context;
    foreach (var entity in model.Entities)
      foreach (var extension in app.Extensions)
        try {
          extension.OnEntityBuilt(entity);
        } catch (Exception ex) {
          context.AddError(ex.Message, $"extension '{extension.GetType().Name}', entity '{entity.Name}'");
        }
    foreach (var extension in app.Extensions)
      extension.BuildContext = null;

    foreach (var entity in model.Entities)
      RequireCacheDiscriminator(context, entity);

    model.BuildReport.Add(context.Diagnostics);
    return !context.HasErrors;
  }

  /// <summary>Applies the application's caching declarations to the entities they name.</summary>
  private static void ApplyCacheDeclarations(BuilderContext context) {
    foreach (var declared in context.App.CachedEntities)
      if (FindDeclaredEntity(context, declared.EntityType, "cached") is { } entity) {
        // The cache holds an entity's rows by key; a view or a function's rows may have none.
        if (declared.CacheType.HasFlag(CacheType.Server) && entity.PrimaryKey == null) {
          context.AddError("is cached and has no primary key; the cache holds an entity's rows by key.", entity.Name);
          continue;
        }
        // The cache answers queries from its rows in memory, so it holds each row whole.
        if (declared.CacheType.HasFlag(CacheType.Server)
            && entity.Members.FirstOrDefault(m => m.IsOnDemand) is { } onDemand) {
          context.AddError($"is cached and has an [OnDemand] member, '{onDemand.Name}'; a cached entity's rows are "
            + "held whole.", entity.Name);
          continue;
        }
        entity.CacheType = declared.CacheType;
        // An infinite span means never, kept as null.
        var expiration = declared.Expiration ?? context.App.DefaultCacheExpiration;
        entity.CacheExpiration = expiration == Timeout.InfiniteTimeSpan ? null : expiration;
      }
  }

  /// <summary>Puts the modules' JSON type maps on the model, reporting a map that does not build or a base type
  /// mapped twice.</summary>
  private static void ApplyJsonTypeDeclarations(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var declared in module.JsonTypeDeclarations) {
        var location = $"module '{module.Name}', JSON types of '{declared.BaseType.Name}'";
        if (context.Model.FindJsonTypes(declared.BaseType) != null) {
          context.AddError("the JSON types are mapped twice.", location);
          continue;
        }
        try {
          context.Model.AddJsonTypes(new JsonTypeMap(declared.BaseType, declared.Types));
        } catch (ArgumentException ex) {
          context.AddError(ex.Message, location);
        }
      }
  }

  /// <summary>Reports a cached entity's varying filter that does not say how its cache blocks differ.</summary>
  private static void RequireCacheDiscriminator(BuilderContext context, EntityTypeInfo entity) {
    if (entity.CacheType == CacheType.NoCache)
      return;
    foreach (var filter in entity.Filters)
      if (filter.Varies && filter.Discriminator == null)
        context.AddError(
          $"entity '{entity.Name}' is cached and has a filter that reads the operation context, and that filter does "
          + "not say how one cached block differs from another — a block filled under one context would be served "
          + "under another. Supply a cache discriminator with the filter, or do not cache the entity.",
          $"application '{context.App.Name}'");
  }

  /// <summary>Finds the entity a declaration names, reporting one that is not an entity of the application.</summary>
  private static EntityTypeInfo? FindDeclaredEntity(BuilderContext context, Type type, string what) {
    var entity = context.Model.FindEntity(type);
    if (entity == null)
      context.AddError($"'{type.Name}' is declared {what} and is not an entity of this application.",
        $"application '{context.App.Name}'");
    return entity;
  }

  /// <summary>Flags the columns of every unique key other than a primary key with
  /// <see cref="MemberFlags.InUniqueKey"/>.</summary>
  private static void MarkUniqueKeyMembers(EntityAppModel model) {
    foreach (var entity in model.Entities)
      foreach (var key in entity.Keys)
        if (key.IsUnique && !key.IsPrimaryKey)
          foreach (var keyMember in key.MembersExpanded)
            keyMember.Member.MemberFlags |= MemberFlags.InUniqueKey;
  }

  /// <summary>Assigns every entity type its position in the dependency order and its reference-graph flags, in one
  /// pass over the type graph.</summary>
  /// <remarks>Types in one strongly connected component share an index. An edge runs from the type that depends on
  /// another to the one it depends on, and sorted components come back dependencies first, so a lower index is
  /// written first.</remarks>
  private static void AssignTopologicalIndexes(EntityAppModel model) {
    var graph = new Graph<EntityTypeInfo>();
    foreach (var entity in model.Entities)
      graph.AddNode(entity);
    foreach (var entity in model.Entities)
      foreach (var member in entity.Members)
        if (member is { Kind: MemberKind.Reference, ReferenceInfo: { } reference }) {
          graph.AddEdge(entity, reference.TargetEntity);
          if (reference.TargetEntity.IdentityMember != null)
            entity.Flags |= EntityTypeFlags.ReferencesIdentity;

          // A self-reference does not make a type referenced: nothing else waits on its rows.
          if (reference.TargetEntity != entity)
            reference.TargetEntity.Flags |= EntityTypeFlags.Referenced;
          else
            entity.Flags |= EntityTypeFlags.SelfReferencing;
        }

    var index = 0;
    foreach (var component in graph.GetSortedComponents()) {
      var nonTrivial = component.IsCycle || component.Nodes.Count > 1;
      foreach (var entity in component.Nodes) {
        entity.TopologicalIndex = index;
        if (nonTrivial)
          entity.Flags |= EntityTypeFlags.InTopologyGroup;
      }
      index++;
    }
  }

  private static void BuildModules(BuilderContext context) {
    foreach (var module in context.App.Modules)
      context.Model.AddModule(module);
  }

  /// <summary>Puts the modules' sequence declarations on the model, each in the schema its dotted name gives or the
  /// module's, reporting one declared twice in a schema.</summary>
  private static void BuildSequences(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var declared in module.SequenceDeclarations) {
        var location = $"module '{module.Name}', sequence '{declared.Name}'";
        var (schema, name) = SplitSequenceName(context, declared.Name, GetSchema(context, module));
        if (context.Model.FindSequence(name, schema?.Name ?? "") != null) {
          context.AddError("the sequence is declared twice.", location);
          continue;
        }
        context.Model.AddSequence(new SequenceInfo(name, schema, module) {
          Start = declared.Start,
          Increment = declared.Increment,
          DataType = declared.DataType,
        });
      }
  }

  /// <summary>Puts the modules' view declarations on their entities, reporting one whose type did not become an
  /// entity.</summary>
  private static void BuildViews(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var declared in module.ViewDeclarations) {
        var location = $"module '{module.Name}', view '{declared.EntityType.Name}'";
        if (!context.EntitiesByType.TryGetValue(declared.EntityType, out var entity)) {
          context.AddError("the view's type is not an entity of this application.", location);
          continue;
        }
        // A [View] attribute may have marked the entity already; its option holds, and its name where the
        // registration gives none.
        entity.View = new ViewInfo(entity, declared.Kind) {
          IsMaterialized = declared.Options.HasFlag(ViewOptions.Materialized) || entity.View?.IsMaterialized == true,
          TypedQuery = declared.TypedQuery,
          QueryBuilder = declared.QueryBuilder,
          SqlByServer = declared.SqlByServer,
        };
        if (declared.Name != null)
          entity.TableName = declared.Name;
      }
    ResolveViewDependencies(context);
  }

  /// <summary>Resolves each view's declared dependencies to the entities of the tables and views it reads, reporting a
  /// type that is neither and a view that depends on itself through others.</summary>
  private static void ResolveViewDependencies(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var declared in module.ViewDeclarations) {
        if (declared.DependsOn is not { Count: > 0 } types
            || !context.EntitiesByType.TryGetValue(declared.EntityType, out var entity) || entity.View == null)
          continue;
        var location = $"module '{module.Name}', view '{declared.EntityType.Name}'";
        var dependencies = new List<EntityTypeInfo>();
        foreach (var type in types) {
          if (context.EntitiesByType.TryGetValue(type, out var dependency)
              && (dependency.IsView || !dependency.IsReadOnly))
            dependencies.Add(dependency);
          else
            context.AddError($"the view depends on '{type.Name}', which is not a table or a view of this application.",
              location);
        }
        entity.View.DependsOn = dependencies;
      }
    foreach (var entity in context.Model.Entities)
      if (entity.View is { DependsOn.Count: > 0 } view && DependsOnItself(entity, view.DependsOn, []))
        context.AddError("the view depends on itself through the views it reads.", $"view '{entity.Name}'");
  }

  private static bool DependsOnItself(EntityTypeInfo entity, IReadOnlyList<EntityTypeInfo> dependencies,
      HashSet<EntityTypeInfo> visited) {
    foreach (var dependency in dependencies) {
      if (dependency == entity)
        return true;
      if (visited.Add(dependency) && dependency.View is { } view && DependsOnItself(entity, view.DependsOn, visited))
        return true;
    }
    return false;
  }

  /// <summary>Puts each module's table function declarations on their entities, reporting one that is a view as
  /// well.</summary>
  private static void BuildTableFunctions(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var declared in module.TableFunctionDeclarations) {
        var location = $"module '{module.Name}', table function '{declared.Name}'";
        if (!context.EntitiesByType.TryGetValue(declared.EntityType, out var entity)) {
          context.AddError("the rows' type is not an entity of this application.", location);
          continue;
        }
        if (entity.IsView)
          context.AddError("is declared both as a view and as a table function's rows.", entity.Name);
        entity.TableFunction = new TableFunctionInfo(entity, declared.Name, declared.Parameters, declared.BodyByServer);
      }
  }

  /// <summary>Marks the entities the modules declared as temp tables, reporting one that is a view or a table
  /// function as well.</summary>
  private static void MarkTempTables(BuilderContext context) {
    foreach (var module in context.App.Modules)
      foreach (var type in module.TempTableTypes) {
        if (!context.EntitiesByType.TryGetValue(type, out var entity))
          continue;                                             // reported where the entity was not built
        if (entity.IsView || entity.IsTableFunction)
          context.AddError($"is declared both as {entity.ReadOnlyKindName} and as a temp table.", entity.Name);
        entity.IsTempTable = true;
      }
  }

  /// <summary>Resolves every <c>[Sequence]</c> member's name to a declared sequence, reporting one no module
  /// declares.</summary>
  private static void ResolveSequenceMembers(BuilderContext context) {
    foreach (var entity in context.Model.Entities)
      foreach (var member in entity.Members) {
        if (member.AutoValue != AutoType.Sequence || member.SequenceName is not { } sequenceName)
          continue;
        var (schema, name) = SplitSequenceName(context, sequenceName, entity.Schema);
        member.Sequence = context.Model.FindSequence(name, schema?.Name ?? "");
        if (member.Sequence == null)
          context.AddError(
            $"draws its value from sequence '{sequenceName}', which no module declares; declare it with "
            + $"{nameof(EntityModule)}.{nameof(EntityModule.RegisterSequence)}.", $"{entity.Name}.{member.Name}");
      }
  }

  /// <summary>Splits a sequence name written with an optional dotted schema into the schema, the default where none
  /// is written, and the bare name.</summary>
  private static (EntitySchemaInfo? Schema, string Name) SplitSequenceName(BuilderContext context, string written,
      EntitySchemaInfo? defaultSchema) {
    var dot = written.LastIndexOf('.');
    return dot < 0
      ? (defaultSchema, written)
      : (context.Model.GetOrAddSchema(written[..dot]), written[(dot + 1)..]);
  }

  /// <summary>Gets the schema a module's entities land in, as a model object shared by every module naming it, or
  /// <see langword="null"/> when neither the module nor the application names one.</summary>
  private static EntitySchemaInfo? GetSchema(BuilderContext context, EntityModule module) {
    var schemaName = module.Schema ?? context.App.Settings.DefaultSchema;
    return schemaName == null ? null : context.Model.GetOrAddSchema(schemaName);
  }

  /// <summary>Creates one entity per registered interface, settling that it exists and what it is called.</summary>
  private static void BuildEntities(BuilderContext context) {
    var policy = context.App.Settings.NamingPolicy;

    foreach (var module in context.App.Modules) {
      var schema = GetSchema(context, module);

      foreach (var type in module.EntityTypes) {
        var location = $"module '{module.Name}', type '{type.Name}'";

        if (!type.IsInterface) {
          context.AddError($"an entity must be an interface; '{type.Name}' is not.", location);
          continue;
        }

        var attr = type.GetCustomAttribute<EntityAttribute>();
        var name = attr?.Name ?? policy.GetEntityName(type);

        // Unique by schema and name, as the database requires.
        var schemaName = schema?.Name ?? "";
        if (context.Model.FindEntity(name, schemaName) != null) {
          context.AddError($"two entities are named '{name}' in schema '{schemaName}'.", location);
          continue;
        }

        var entity = new EntityTypeInfo(name, module, type) { Schema = schema };
        context.Model.AddEntity(entity);
        context.EntitiesByType[type] = entity;
      }
    }
  }

  /// <summary>Substitutes the application's real types for the placeholders its modules declared, in both the
  /// model's lookup and the context's.</summary>
  /// <remarks>Runs between the entities and the members, so a member typed by a placeholder resolves through the
  /// rewritten lookups and nothing built later learns of the substitution. Every check on the declarations is here.
  /// </remarks>
  private static void ApplyReplacements(BuilderContext context) {
    var app = context.App;
    if (app.EntityReplacements.Count == 0)
      return;
    var chain = BuildReplacementChain(context);

    foreach (var placeholderType in chain.Keys) {
      var location = $"application '{app.Name}', replacing '{placeholderType.Name}'";

      if (!context.EntitiesByType.TryGetValue(placeholderType, out var placeholder)) {
        context.AddError(
          $"'{placeholderType.Name}' is not a registered entity — the module that declares it must register it, "
          + "even though it is about to be replaced.", location);
        continue;
      }

      if (!TryResolveFinal(context, chain, placeholderType, location, out var finalType))
        continue;

      if (!context.EntitiesByType.TryGetValue(finalType, out var replacement)) {
        context.AddError($"'{finalType.Name}' is not a registered entity.", location);
        continue;
      }

      // The module's reference member is typed by the placeholder, so the replacement must derive from it.
      if (!placeholderType.IsAssignableFrom(finalType)) {
        context.AddError(
          $"'{finalType.Name}' must derive from '{placeholderType.Name}' to replace it — otherwise a member typed "
          + $"'{placeholderType.Name}' cannot be assigned one.", location);
        continue;
      }

      context.Model.ReplaceEntity(placeholder, replacement);
      context.EntitiesByType[placeholderType] = replacement;
    }
  }

  /// <summary>Builds the placeholder-to-replacement map from the declarations, reporting the faults that need no
  /// model: a type that is not an interface, one that replaces itself, one replaced twice.</summary>
  /// <remarks>A second replacement of one placeholder is reported and dropped, so the build proceeds on the first.
  /// </remarks>
  private static Dictionary<Type, Type> BuildReplacementChain(BuilderContext context) {
    var chain = new Dictionary<Type, Type>();
    foreach (var declared in context.App.EntityReplacements) {
      var (placeholder, replacement) = (declared.Placeholder, declared.Replacement);
      var location = $"application '{context.App.Name}', replacing '{placeholder.Name}'";

      if (placeholder == replacement) {
        context.AddError($"'{placeholder.Name}' cannot replace itself.", location);
        continue;
      }
      if (!placeholder.IsInterface || !replacement.IsInterface) {
        context.AddError(
          $"an entity is an interface — '{(placeholder.IsInterface ? replacement : placeholder).Name}' is not.",
          location);
        continue;
      }
      if (chain.TryGetValue(placeholder, out var already)) {
        if (already != replacement)
          context.AddError($"'{placeholder.Name}' is already replaced by '{already.Name}'.", location);
        continue;
      }
      chain[placeholder] = replacement;
    }
    return chain;
  }

  /// <summary>Walks a replacement chain to the type at the end of it, reporting a cycle.</summary>
  /// <remarks>A placeholder replaced by a type that is itself replaced resolves to the last one.</remarks>
  private static bool TryResolveFinal(BuilderContext context, IReadOnlyDictionary<Type, Type> chain, Type from,
      string location, out Type final) {
    final = from;
    var seen = new HashSet<Type> { from };
    while (chain.TryGetValue(final, out var next)) {
      if (!seen.Add(next)) {
        context.AddError($"the replacements form a cycle at '{next.Name}'.", location);
        return false;
      }
      final = next;
    }
    return true;
  }

  /// <summary>Runs every entity-level and member-level attribute once per <see cref="AttributePhase"/>; each
  /// attribute acts in the phase it belongs to.</summary>
  /// <remarks>Runs after the members exist, since an entity-level key attribute names them.</remarks>
  internal static void ApplyAttributes(BuilderContext context) {
    foreach (var phase in Enum.GetValues<AttributePhase>()) {
      context.Phase = phase;
      foreach (var entity in context.Model.Entities) {
        if (entity.InterfaceType == null)
          continue;
        foreach (var attr in entity.InterfaceType.GetCustomAttributes<EntityModelAttributeBase>())
          attr.ApplyToEntity(context, entity);
        new MemberBuilder(context).ApplyMemberAttributes(entity);
      }
    }
  }
}
