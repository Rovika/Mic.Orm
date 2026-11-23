using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm.Db;
using Mic.Orm.Model;

namespace Mic.Orm;

/// <summary>Represents one unit of functionality: a set of related entities and the code that goes with them, with
/// its own version and migrations.</summary>
/// <remarks>The declarations are recorded here and validated by the model builder; whether a registered type is a
/// usable entity is reported in the build report.</remarks>
public class EntityModule {
  readonly List<Type> _entityTypes = new();
  readonly List<Type> _dependencies = new();
  readonly List<JsonTypeDeclaration> _jsonTypeDeclarations = new();
  readonly List<SequenceDefinition> _sequenceDeclarations = new();
  readonly List<ViewDeclaration> _viewDeclarations = new();
  readonly List<Type> _tempTableTypes = new();
  readonly List<TableFunctionDeclaration> _tableFunctionDeclarations = new();

  public EntityModule(string name, Version version) {
    Name = name;
    Version = version;
  }

  /// <summary>Gets the JSON type declarations recorded by <c>MapJsonTypes</c>, for the model builder to validate and
  /// put on the model.</summary>
  internal IReadOnlyList<JsonTypeDeclaration> JsonTypeDeclarations => _jsonTypeDeclarations;

  /// <summary>Gets the sequences recorded by <see cref="RegisterSequence"/>, for the model builder to put on the
  /// model.</summary>
  internal IReadOnlyList<SequenceDefinition> SequenceDeclarations => _sequenceDeclarations;

  /// <summary>Gets the views recorded by the <c>RegisterView</c> methods, for the model builder to put on their
  /// entities.</summary>
  internal IReadOnlyList<ViewDeclaration> ViewDeclarations => _viewDeclarations;

  /// <summary>Gets the entity types recorded by <see cref="RegisterTempTable{TTemp}"/>, for the model builder to
  /// mark.</summary>
  internal IReadOnlyList<Type> TempTableTypes => _tempTableTypes;

  /// <summary>Gets the table functions this module declared, held until the model exists to put them on their
  /// entities.</summary>
  internal IReadOnlyList<TableFunctionDeclaration> TableFunctionDeclarations => _tableFunctionDeclarations;

  public string Name { get; }

  /// <summary>Gets the version of the module, which drives its migrations.</summary>
  public Version Version { get; }

  /// <summary>Gets or sets the schema the module's entities go in.</summary>
  /// <remarks>Assigned by <see cref="EntityApp.AddModule"/> or reassigned before <see cref="EntityApp.Init"/>;
  /// <see langword="null"/> falls back to <see cref="EntityAppSettings.DefaultSchema"/>.</remarks>
  public string? Schema { get; set; }

  /// <summary>Gets the application this module belongs to, set by <see cref="EntityApp.AddModule"/>.</summary>
  public EntityApp? App { get; internal set; }

  public IReadOnlyList<Type> EntityTypes => _entityTypes;
  public IReadOnlyList<Type> Dependencies => _dependencies;

  /// <summary>Registers the entity interfaces this module owns.</summary>
  /// <param name="entityTypes">An array that contains the entity interface types.</param>
  /// <remarks>The types are not examined here; whether each is a usable entity is reported by the model builder.
  /// </remarks>
  public void RegisterEntities(params Type[] entityTypes) {
    ArgumentNullException.ThrowIfNull(entityTypes);
    foreach (var type in entityTypes) {
      ArgumentNullException.ThrowIfNull(type);
      if (_entityTypes.Contains(type))
        throw new InvalidOperationException($"Module '{Name}': entity '{type.Name}' is already registered.");
      _entityTypes.Add(type);
    }
  }

  /// <summary>Declares that this module needs another module to be present in the application.</summary>
  /// <typeparam name="TModule">The type of the required module.</typeparam>
  /// <remarks>Verified at model validation; a missing module is a diagnostic in the build report.</remarks>
  public void Requires<TModule>() where TModule : EntityModule {
    var type = typeof(TModule);
    if (!_dependencies.Contains(type))
      _dependencies.Add(type);
  }

  /// <summary>Declares the types a JSON member of <paramref name="baseType"/> may hold, each under the discriminator
  /// its row carries in the column <c>[Json(Discriminator = "…")]</c> names.</summary>
  /// <param name="baseType">The declared type of the JSON member.</param>
  /// <param name="types">An array that contains each derived type, the base type itself included where it may occur,
  /// with its discriminator: a string, a number or an enum member, converted by the model builder to the
  /// discriminator column's own type.</param>
  /// <remarks>The serializer's own in-document polymorphism (<c>[JsonDerivedType]</c>) needs no declaration. A type
  /// that is not derived from <paramref name="baseType"/>, a discriminator given twice, a discriminator that does not
  /// convert, or a base type mapped twice in any module is a diagnostic in the build report.</remarks>
  public void MapJsonTypes(Type baseType, params (object Discriminator, Type Type)[] types) {
    ArgumentNullException.ThrowIfNull(baseType);
    ArgumentNullException.ThrowIfNull(types);
    _jsonTypeDeclarations.Add(new JsonTypeDeclaration(baseType, types));
  }

  /// <summary>Declares the types a JSON member of <typeparamref name="TBase"/> may hold, each under its
  /// discriminator.</summary>
  /// <typeparam name="TBase">The declared type of the JSON member.</typeparam>
  /// <param name="types">An array that contains each derived type with its discriminator.</param>
  public void MapJsonTypes<TBase>(params (object Discriminator, Type Type)[] types) => MapJsonTypes(typeof(TBase), types);

  /// <summary>Represents the types a JSON member of a base type may hold, each under its discriminator, held until
  /// the model exists to validate them.</summary>
  public sealed record JsonTypeDeclaration(Type BaseType, IReadOnlyList<(object Discriminator, Type Type)> Types);

  /// <summary>Declares a database sequence this module owns, which a member refers to by name in
  /// <c>[Sequence]</c>.</summary>
  /// <param name="name">The name of the sequence; with a schema in dotted notation, as in <c>"billing.numbers"</c>,
  /// for a sequence outside the module's schema.</param>
  /// <param name="start">The first value the sequence hands out.</param>
  /// <param name="increment">The step between values.</param>
  /// <param name="dataType">The type of the values; <see langword="null"/> for a <c>long</c>.</param>
  /// <remarks>A member referring to a sequence no module declares, or a sequence declared twice in one schema, is a
  /// diagnostic in the build report.</remarks>
  public SequenceDefinition RegisterSequence(string name, long start = 1, long increment = 1, Type? dataType = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (increment == 0)
      throw new ArgumentOutOfRangeException(nameof(increment), "A sequence's increment cannot be zero.");
    var sequence = new SequenceDefinition(name, start, increment, dataType ?? typeof(long));
    _sequenceDeclarations.Add(sequence);
    return sequence;
  }

  /// <summary>Represents a sequence a module declared, held until the model exists to place it in a schema.
  /// </summary>
  /// <summary>Declares a database view this module owns, defined by a typed query: the entity gives the view its
  /// shape, the query its rows.</summary>
  /// <typeparam name="TView">The entity interface that reads through the view; registered as an entity of this
  /// module where it is not yet.</typeparam>
  /// <param name="query">The query, written over the sets <see cref="ViewHelper.EntitySet{T}"/> gives, whose
  /// projection names a value for every member of the view.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the view is beyond
  /// its definition.</param>
  /// <param name="viewName">The name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</param>
  /// <param name="dependsOn">The entity interfaces of the views this view's definition reads, which are created
  /// before it and recreated with it, and, for a view defined by SQL, of the tables it reads, whose column changes
  /// recreate it; or <see langword="null"/> for none.</param>
  /// <remarks>The query is translated for each database when its db model is built; a query that takes a parameter
  /// or selects a value the entity has no member for is a diagnostic in the build report. A view's rows are
  /// read-only.</remarks>
  public void RegisterView<TView>(IQueryable query, ViewOptions options = ViewOptions.None, string? viewName = null,
      Type[]? dependsOn = null) where TView : class {
    ArgumentNullException.ThrowIfNull(query);
    AddView(new ViewDeclaration(typeof(TView), ViewDefinitionKind.Query, options, viewName) {
      TypedQuery = query, DependsOn = dependsOn });
  }

  /// <summary>Declares a database view this module owns, defined by a query tree built by name with
  /// <see cref="Mic.Linq.QueryMaker"/> over the model it is given.</summary>
  /// <typeparam name="TView">The entity interface that reads through the view.</typeparam>
  /// <param name="buildQuery">A function that builds the query tree over a database's model, with a projection that
  /// names a value for every member of the view; called once per database when its db model is built.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the view is beyond
  /// its definition.</param>
  /// <param name="viewName">The name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</param>
  /// <param name="dependsOn">The entity interfaces of the views this view's definition reads, which are created
  /// before it and recreated with it, and, for a view defined by SQL, of the tables it reads, whose column changes
  /// recreate it; or <see langword="null"/> for none.</param>
  public void RegisterView<TView>(Func<ILinqDataModel, QueryNode> buildQuery, ViewOptions options = ViewOptions.None,
      string? viewName = null, Type[]? dependsOn = null) where TView : class {
    ArgumentNullException.ThrowIfNull(buildQuery);
    AddView(new ViewDeclaration(typeof(TView), ViewDefinitionKind.Query, options, viewName) {
      QueryBuilder = buildQuery, DependsOn = dependsOn });
  }

  /// <summary>Declares a database view this module owns, defined by SQL text written per server kind.</summary>
  /// <typeparam name="TView">The entity interface that reads through the view.</typeparam>
  /// <param name="sqlByServer">The defining select, by server kind; a database whose server kind has none is a
  /// diagnostic in its db model's build report.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the view is beyond
  /// its definition.</param>
  /// <param name="viewName">The name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</param>
  /// <param name="dependsOn">The entity interfaces of the views this view's definition reads, which are created
  /// before it and recreated with it, and, for a view defined by SQL, of the tables it reads, whose column changes
  /// recreate it; or <see langword="null"/> for none.</param>
  /// <remarks>The select's columns are the view's, in its order; the entity's members are matched to them by name
  /// when the view is read.</remarks>
  public void RegisterView<TView>(IReadOnlyDictionary<DbServerKind, string> sqlByServer,
      ViewOptions options = ViewOptions.None, string? viewName = null, Type[]? dependsOn = null) where TView : class {
    ArgumentNullException.ThrowIfNull(sqlByServer);
    AddView(new ViewDeclaration(typeof(TView), ViewDefinitionKind.Sql, options, viewName) {
      SqlByServer = sqlByServer, DependsOn = dependsOn });
  }

  /// <summary>Declares a database view this module owns, defined by SQL text for one server kind.</summary>
  /// <typeparam name="TView">The entity interface that reads through the view.</typeparam>
  /// <param name="serverKind">The server kind the select is written for; a database of any other kind is a
  /// diagnostic in its db model's build report.</param>
  /// <param name="sql">The defining select.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the view is beyond
  /// its definition.</param>
  /// <param name="viewName">The name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</param>
  /// <param name="dependsOn">The entity interfaces of the views this view's definition reads, which are created
  /// before it and recreated with it, and, for a view defined by SQL, of the tables it reads, whose column changes
  /// recreate it; or <see langword="null"/> for none.</param>
  public void RegisterView<TView>(DbServerKind serverKind, string sql, ViewOptions options = ViewOptions.None,
      string? viewName = null, Type[]? dependsOn = null) where TView : class {
    ArgumentNullException.ThrowIfNull(sql);
    RegisterView<TView>(new Dictionary<DbServerKind, string> { [serverKind] = sql }, options, viewName, dependsOn);
  }

  /// <summary>Declares that an entity of this module reads through a view that exists in the database already,
  /// which MIC never creates, changes or drops.</summary>
  /// <typeparam name="TView">The entity interface that reads through the view.</typeparam>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the view is;
  /// <see cref="ViewOptions.Materialized"/> says it is one, so that it can be refreshed.</param>
  /// <param name="viewName">The name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</param>
  /// <remarks>An upgrade checks that the view exists with a column for every member, and reports it where it does
  /// not.</remarks>
  public void RegisterExistingView<TView>(ViewOptions options = ViewOptions.None, string? viewName = null)
      where TView : class =>
    AddView(new ViewDeclaration(typeof(TView), ViewDefinitionKind.Existing, options, viewName));

  /// <summary>Declares an entity whose rows live in a temp table: a table a session creates on its connection and
  /// fills from objects with <c>CreateTempTable</c> or from queries with <c>ExecuteInsert</c>, reads like any entity
  /// set, and drops when it ends.</summary>
  /// <typeparam name="TTemp">The entity interface, registered as an entity of this module where it is not yet.
  /// </typeparam>
  /// <remarks>The table is never part of an upgrade. The entity has plain column members, and nothing else: no
  /// reference, list, auto value or index; nothing can reference it. Its primary key is optional: without one, the
  /// rows are not tracked. Its rows are read-only in the session, the fill being their one write; a set-based
  /// mutation over them still runs.</remarks>
  public void RegisterTempTable<TTemp>() where TTemp : class {
    var type = typeof(TTemp);
    if (_tempTableTypes.Contains(type))
      throw new InvalidOperationException($"Module '{Name}': '{type.Name}' is already declared as a temp table.");
    if (!_entityTypes.Contains(type))
      _entityTypes.Add(type);
    _tempTableTypes.Add(type);
  }

  /// <summary>Declares that an entity of this module reads the rows a function of the server returns, a function
  /// that exists in the database already, which MIC never creates, changes or drops.</summary>
  /// <typeparam name="TRow">The entity interface that gives the rows their shape; registered as an entity of this
  /// module where it is not yet. Its primary key is optional: without one, the rows are not tracked.</typeparam>
  /// <param name="functionName">The name of the function in the database, in the module's schema.</param>
  /// <param name="parameters">The function's parameters, by name and CLR type, in order.</param>
  /// <remarks>The rows are read through <c>session.TableFunction&lt;TRow&gt;(arguments)</c>, on a server that has
  /// table-valued functions; MySQL and SQLite have none. An upgrade checks that the function exists and reports it
  /// where it does not.</remarks>
  public void RegisterExistingTableFunction<TRow>(string functionName, params (string Name, Type Type)[] parameters)
      where TRow : class =>
    AddTableFunction(new TableFunctionDeclaration(typeof(TRow), functionName, ToParameters(parameters), null));

  /// <summary>Declares a function of the server this module owns, which returns the rows of an entity: defined by a
  /// body written per server kind, created and recreated by the hash of its text.</summary>
  /// <typeparam name="TRow">The entity interface that gives the rows their shape; registered as an entity of this
  /// module where it is not yet. Its primary key is optional: without one, the rows are not tracked.</typeparam>
  /// <param name="functionName">The name of the function in the database, in the module's schema.</param>
  /// <param name="bodyByServer">The defining select, by server kind, written over the parameters by the names given
  /// here, spelled as the server spells a parameter; a database whose server kind has none is a diagnostic in its db
  /// model's build report.</param>
  /// <param name="parameters">The function's parameters, by name and CLR type, in order.</param>
  /// <remarks>The select's columns are the rows' columns, matched to the entity's members by name. The rows are read
  /// through <c>session.TableFunction&lt;TRow&gt;(arguments)</c>.</remarks>
  public void RegisterTableFunction<TRow>(string functionName, IReadOnlyDictionary<DbServerKind, string> bodyByServer,
      params (string Name, Type Type)[] parameters) where TRow : class {
    ArgumentNullException.ThrowIfNull(bodyByServer);
    AddTableFunction(new TableFunctionDeclaration(typeof(TRow), functionName, ToParameters(parameters), bodyByServer));
  }

  private static IReadOnlyList<TableFunctionParameter> ToParameters((string Name, Type Type)[] parameters) {
    ArgumentNullException.ThrowIfNull(parameters);
    return [.. parameters.Select(p => new TableFunctionParameter(p.Name, p.Type))];
  }

  private void AddTableFunction(TableFunctionDeclaration declaration) {
    ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Name);
    if (_tableFunctionDeclarations.Any(f => f.EntityType == declaration.EntityType))
      throw new InvalidOperationException(
        $"Module '{Name}': '{declaration.EntityType.Name}' is already declared as a table function's rows.");
    if (!_entityTypes.Contains(declaration.EntityType))
      _entityTypes.Add(declaration.EntityType);
    _tableFunctionDeclarations.Add(declaration);
  }

  /// <summary>Represents a table function a module declared, held until the model exists to put it on its entity.
  /// </summary>
  public sealed record TableFunctionDeclaration(Type EntityType, string Name,
    IReadOnlyList<TableFunctionParameter> Parameters, IReadOnlyDictionary<DbServerKind, string>? BodyByServer);

  private void AddView(ViewDeclaration declaration) {
    if (_viewDeclarations.Any(v => v.EntityType == declaration.EntityType))
      throw new InvalidOperationException(
        $"Module '{Name}': '{declaration.EntityType.Name}' is already declared as a view.");
    if (!_entityTypes.Contains(declaration.EntityType))
      _entityTypes.Add(declaration.EntityType);
    _viewDeclarations.Add(declaration);
  }

  /// <summary>Represents a view a module declared, held until the model exists to put it on its entity.</summary>
  public sealed record ViewDeclaration(Type EntityType, ViewDefinitionKind Kind, ViewOptions Options, string? Name) {
    public IQueryable? TypedQuery { get; init; }
    public Func<ILinqDataModel, QueryNode>? QueryBuilder { get; init; }
    public IReadOnlyDictionary<DbServerKind, string>? SqlByServer { get; init; }
    public IReadOnlyList<Type>? DependsOn { get; init; }
  }

  /// <summary>When overridden in a derived class, hooks the events this module listens to, such as those on
  /// <see cref="EntityTypeInfo.Events"/>.</summary>
  /// <param name="model">The finished model, which built without errors.</param>
  /// <remarks>Every derived fact of the model is present when this runs. The model is read and hooked here, not
  /// changed: a member added now would have no accessor and appear in no list. Not called on a model that failed to
  /// build. The order across modules is undefined.</remarks>
  public virtual void OnModelConstructed(EntityAppModel model) { }
}
