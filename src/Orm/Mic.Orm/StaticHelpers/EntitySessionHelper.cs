using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides what a session can answer that <see cref="IEntitySession"/> does not ask: queries built by name
/// as node trees, the full-text index check, and primary keys built from values.</summary>
/// <remarks>A query built as a node tree is the framework's front end onto the query engine; an application writes
/// <c>EntitySet&lt;T&gt;()</c> and C#.</remarks>
public static class EntitySessionHelper {
  // ----- queries built by name ----------------------------------------------------------------------------------------

  /// <summary>Gets the model the query engine runs over for the session's database, which a query names its sets
  /// against.</summary>
  /// <param name="session">The session.</param>
  /// <returns>The query model.</returns>
  public static ILinqDataModel GetLinqModel(this IEntitySession session) => GetEntitySession(session).Database.DbModel;

  /// <summary>Starts a query over all the records of an entity named by name.</summary>
  /// <param name="session">The session.</param>
  /// <param name="entityName">The entity name.</param>
  /// <returns>The query node.</returns>
  public static QueryNode From(this IEntitySession session, string entityName) =>
    QueryMaker.From(session.GetLinqModel(), entityName);

  /// <summary>Gets the rows a function of the server returns for the arguments given, as a queryable to compose over
  /// and run with the async extensions.</summary>
  /// <typeparam name="TRow">The entity interface the module declared as the function's rows.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="arguments">The arguments, one per parameter of the function, in order.</param>
  /// <returns>A queryable over the rows.</returns>
  /// <exception cref="ArgumentException">The entity is not a table function's rows, or the arguments do not fit.
  /// </exception>
  /// <remarks>The arguments are bound when the query runs, so one statement serves every call.</remarks>
  public static IQueryable<TRow> TableFunction<TRow>(this IEntitySession session, params object?[] arguments)
      where TRow : class {
    var (table, function) = GetTableFunction(session, typeof(TRow));
    var parameters = function.Parameters.Select(p => (p.Name, p.Type)).ToList();
    return new TableFunctionQuery<TRow>(GetEntitySession(session).QueryProvider, table, parameters, arguments);
  }

  /// <summary>Starts a by-name query over the rows a function of the server returns, the arguments given as nodes.
  /// </summary>
  /// <param name="session">The session.</param>
  /// <param name="entityName">The name of the entity the module declared as the function's rows.</param>
  /// <param name="arguments">The arguments, one per parameter: parameters, constants or values read off a row.
  /// </param>
  /// <returns>A rowset node to compose over.</returns>
  public static QueryNode TableFunction(this IEntitySession session, string entityName, params QueryNode[] arguments) {
    var entity = GetEntitySession(session).Model.FindEntity(entityName)
      ?? throw new ArgumentException($"'{entityName}' is not an entity of the model.", nameof(entityName));
    var (table, function) = GetTableFunction(session, entity.InterfaceType!);
    if (arguments.Length != function.Parameters.Count)
      throw new ArgumentException($"'{function.Name}' takes {function.Parameters.Count} argument(s) and {arguments.Length} were given.", nameof(arguments));
    return QueryMaker.TableFunction(table, arguments);
  }

  private static (DbTableInfo Table, TableFunctionInfo Function) GetTableFunction(IEntitySession session, Type rowType) {
    var entitySession = GetEntitySession(session);
    var entity = entitySession.GetEntityTypeInfo(rowType);
    var function = entity.TableFunction
      ?? throw new ArgumentException($"'{entity.Name}' is not a table function's rows; a module declares one with RegisterTableFunction.", nameof(rowType));
    var table = entitySession.Database.DbModel.GetTable(entity)
      ?? throw new InvalidOperationException($"'{entity.Name}' has no table in this database.");
    return (table, function);
  }

  /// <summary>Asynchronously runs a query and returns the records it produced, tracked like anything else the
  /// session reads.</summary>
  /// <param name="session">The session.</param>
  /// <param name="query">The query tree.</param>
  /// <param name="arguments">The values for the parameters the query declares with <c>Parameter(name, type)</c>, in
  /// the order the tree names them; a parameter made with its value needs none. A collection argument is wrapped,
  /// as <c>[values]</c>, so that a reference-type array does not spread into one argument per element; the count is
  /// checked.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the records;
  /// <c>record.EntityInstance</c> is the typed view of one.</returns>
  public static async Task<IReadOnlyList<EntityRecord>> ExecuteQueryAsync(this IEntitySession session, QueryNode query,
      params object?[] arguments) =>
    await RunAsync(session, query, arguments) as IReadOnlyList<EntityRecord> ?? [];

  /// <summary>Asynchronously runs an aggregate and returns the one value it produced.</summary>
  /// <typeparam name="T">The type of the value.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="query">The query tree.</param>
  /// <param name="arguments">The values for the parameters the query declares.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the value, or
  /// <see langword="null"/> for an aggregate over no rows; a count answers zero.</returns>
  public static async Task<T?> ExecuteScalarAsync<T>(this IEntitySession session, QueryNode query,
      params object?[] arguments) =>
    (T?)await RunAsync(session, query, arguments);

  /// <summary>Asynchronously runs a projection and returns its rows: named values over a structure the engine made
  /// for the shape.</summary>
  /// <param name="session">The session.</param>
  /// <param name="query">The query tree.</param>
  /// <param name="arguments">The values for the parameters the query declares.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the rows, which have no
  /// identity and are not tracked.</returns>
  /// <remarks>A projection of one expression is run with <see cref="ExecuteValuesAsync{T}"/>.</remarks>
  public static async Task<IReadOnlyList<ProjectionData>> ExecuteRowsAsync(this IEntitySession session, QueryNode query,
      params object?[] arguments) =>
    await RunAsync(session, query, arguments) as List<ProjectionData> ?? [];

  /// <summary>Asynchronously runs a projection of one expression and returns its column.</summary>
  /// <typeparam name="T">The type of the values.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="query">The query tree.</param>
  /// <param name="arguments">The values for the parameters the query declares.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the values.</returns>
  /// <remarks>A projection with one slot translates to a column of values, so asking for its rows is an error.
  /// </remarks>
  public static async Task<IReadOnlyList<T?>> ExecuteValuesAsync<T>(this IEntitySession session, QueryNode query,
      params object?[] arguments) =>
    [.. (await RunAsync(session, query, arguments) as List<object?> ?? []).Select(v => (T?)v)];

  /// <summary>Asynchronously runs a mutation built by name, a query ending in <c>QueryMaker</c>'s <c>Delete</c>,
  /// <c>Update</c> or <c>Insert</c>.</summary>
  /// <param name="session">The session.</param>
  /// <param name="mutation">The mutation tree.</param>
  /// <param name="arguments">The values for the parameters the mutation declares.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// affected.</returns>
  /// <remarks>What a mutation does and does not do is said on <c>MutationQueryableExtensions</c>, and holds here.
  /// </remarks>
  public static async Task<int> ExecuteMutationAsync(this IEntitySession session, QueryNode mutation,
      params object?[] arguments) =>
    Convert.ToInt32(await RunAsync(session, mutation, arguments));

  /// <summary>Holds a mutation built by name for the session's next save, to run within the save's transaction.
  /// </summary>
  /// <param name="session">The session.</param>
  /// <param name="mutation">The mutation tree.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save the mutation runs.</param>
  /// <param name="arguments">The values for the parameters the mutation declares.</param>
  /// <remarks>The by-name form of <c>ScheduleDelete</c> and its siblings on a typed query.</remarks>
  public static void ScheduleMutation(this IEntitySession session, QueryNode mutation, CommandSchedule schedule,
      params object?[] arguments) {
    ArgumentNullException.ThrowIfNull(mutation);
    GetEntitySession(session).Schedule(mutation, arguments, schedule);
  }

  /// <summary>Adds an include to every query the session runs that returns the entity, so that the references and
  /// lists the selector names load with the rows, and to every row of the entity such a query's includes load.
  /// </summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="selector">An expression that names the references and lists to load.</param>
  public static void AddInclude<TEntity>(this IEntitySession session, Expression<Func<TEntity, object?>> selector) =>
    GetEntitySession(session).AddInclude(selector);

  /// <summary>Runs a query tree with its arguments through the session, with its includes, and returns what came
  /// back for the caller to read as what it asked for.</summary>
  private static Task<object?> RunAsync(IEntitySession session, QueryNode query, object?[] arguments) {
    var entSession = GetEntitySession(session);
    return entSession.RunQueryAsync(new DataRequest(entSession, query, arguments));
  }

  // ----- the full-text index ------------------------------------------------------------------------------------------

  /// <summary>Asynchronously determines whether an entity's full-text index has caught up with its rows, so that a
  /// search run now sees what was just written.</summary>
  /// <typeparam name="T">The entity interface.</typeparam>
  /// <param name="session">The session.</param>
  /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the
  /// index is current; otherwise, <see langword="false"/>.</returns>
  /// <remarks>Always <see langword="true"/> on a server that indexes at the commit. On one that indexes a moment
  /// later, such as SQL Server's change tracking, a caller that needs it waits and asks again.</remarks>
  public static Task<bool> IsFullTextIndexCurrentAsync<T>(this IEntitySession session) where T : class =>
    IsFullTextIndexCurrentAsync(session, typeof(T));

  /// <summary>Asynchronously determines whether an entity's full-text index has caught up with its rows, with the
  /// entity named by its type.</summary>
  /// <param name="session">The session.</param>
  /// <param name="entityType">The entity interface.</param>
  /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the
  /// index is current; otherwise, <see langword="false"/>.</returns>
  public static async Task<bool> IsFullTextIndexCurrentAsync(this IEntitySession session, Type entityType) {
    ArgumentNullException.ThrowIfNull(entityType);
    var entSession = GetEntitySession(session);
    var entity = entSession.Context.App.Model!.FindEntity(entityType)
      ?? throw new ArgumentException($"'{entityType.Name}' is not an entity of this application's model.", nameof(entityType));
    var table = entSession.Database.DbModel.GetTable(entity)
      ?? throw new InvalidOperationException($"'{entity.Name}' has no table in this database.");
    var pendingSql = entSession.Database.Provider.SqlDialect.BuildFullTextPendingQuery(table);
    return pendingSql == null || await session.RunSqlScalarAsync<int>(pendingSql) == 0;
  }

  // ----- views -------------------------------------------------------------------------------------------------------------

  /// <summary>Asynchronously recomputes the rows of an entity's materialized view, so that a read sees what the
  /// tables hold now.</summary>
  /// <typeparam name="TView">The view entity, declared with <see cref="ViewOptions.Materialized"/>.</typeparam>
  /// <param name="session">The session to run the refresh through.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>Does nothing on a server that has no materialized views, where the view is an ordinary one and its rows
  /// are live.</remarks>
  /// <exception cref="InvalidOperationException">The entity is not a view declared materialized.</exception>
  public static Task RefreshMaterializedViewAsync<TView>(this IEntitySession session) where TView : class =>
    RefreshMaterializedViewAsync(session, typeof(TView));

  /// <summary>Asynchronously recomputes the rows of an entity's materialized view, with the entity named by its type.
  /// </summary>
  /// <param name="session">The session to run the refresh through.</param>
  /// <param name="entityType">The view entity's interface.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  public static async Task RefreshMaterializedViewAsync(this IEntitySession session, Type entityType) {
    ArgumentNullException.ThrowIfNull(entityType);
    var entSession = GetEntitySession(session);
    var entity = entSession.Context.App.Model!.FindEntity(entityType)
      ?? throw new ArgumentException($"'{entityType.Name}' is not an entity of this application's model.", nameof(entityType));
    if (entity.View is not { IsMaterialized: true })
      throw new InvalidOperationException($"'{entity.Name}' is not a view declared materialized; there is nothing to refresh.");
    var table = entSession.Database.DbModel.GetTable(entity)
      ?? throw new InvalidOperationException($"'{entity.Name}' has no view in this database.");
    // Where the server has no materialized views, the view is an ordinary one and its rows are already current.
    if (table.View is not { IsMaterialized: true })
      return;
    await session.RunSqlNonQueryAsync(string.Format(entSession.Database.Provider.SqlDialect.RefreshMaterializedView, table.RefName));
  }

  /// <summary>Recomputes the rows of an entity's materialized view.</summary>
  /// <typeparam name="TView">The view entity, declared with <see cref="ViewOptions.Materialized"/>.</typeparam>
  /// <param name="session">The session to run the refresh through.</param>
  public static void RefreshMaterializedView<TView>(this IEntitySession session) where TView : class =>
    GetEntitySession(session).RunSync(() => RefreshMaterializedViewAsync(session, typeof(TView)));

  // ----- temp tables -------------------------------------------------------------------------------------------------------

  /// <summary>Registers an empty temp table of this session, for set-based inserts to fill, and returns the set to
  /// query it as.</summary>
  /// <typeparam name="TTemp">The entity interface a module declared with
  /// <see cref="EntityModule.RegisterTempTable{TTemp}"/>.</typeparam>
  /// <param name="session">The session the table belongs to.</param>
  /// <returns>The set, to query the table through.</returns>
  /// <remarks>The table is created on the session's connection before the first request that follows; an
  /// <c>ExecuteInsert</c> into it from a query is such a request.</remarks>
  public static IQueryable<TTemp> CreateTempTable<TTemp>(this IEntitySession session) where TTemp : class =>
    CreateTempTable<TTemp>(session, []);

  /// <summary>Registers a temp table of this session, filled with rows, and returns the set to query it as.</summary>
  /// <typeparam name="TTemp">The entity interface a module declared with
  /// <see cref="EntityModule.RegisterTempTable{TTemp}"/>.</typeparam>
  /// <param name="session">The session the table belongs to.</param>
  /// <param name="rows">The rows: objects whose readable properties match the entity's members by name, ignoring
  /// case; an anonymous type or a record as well as an entity instance. A member a row gives nothing for must be
  /// nullable.</param>
  /// <returns>The set, to query the table through.</returns>
  /// <remarks>Nothing is sent now: the table is created and filled on the session's connection before the first
  /// request that follows, a query or a save, so that a scheduled mutation over it runs inside the save. The table
  /// is dropped when the session ends. A temp table is filled once per session.</remarks>
  public static IQueryable<TTemp> CreateTempTable<TTemp>(this IEntitySession session, IEnumerable<object> rows)
      where TTemp : class {
    ArgumentNullException.ThrowIfNull(rows);
    var entSession = GetEntitySession(session);
    var entity = entSession.Context.Model.FindEntity(typeof(TTemp))
      ?? throw new ArgumentException($"'{typeof(TTemp).Name}' is not an entity of this application's model.", nameof(TTemp));
    entSession.AddTempTable(entity, rows);
    return session.EntitySet<TTemp>();
  }

  /// <summary>Asynchronously creates and fills a temp table of this session now, and returns the set to query it as.
  /// </summary>
  /// <typeparam name="TTemp">The entity interface a module declared with
  /// <see cref="EntityModule.RegisterTempTable{TTemp}"/>.</typeparam>
  /// <param name="session">The session the table belongs to.</param>
  /// <param name="rows">The rows, as for
  /// <see cref="CreateTempTable{TTemp}(IEntitySession, IEnumerable{object})"/>.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the set.</returns>
  /// <remarks>For a caller who runs direct SQL against the table next; a query or a save creates a registered table
  /// by itself.</remarks>
  public static async Task<IQueryable<TTemp>> CreateTempTableAsync<TTemp>(this IEntitySession session,
      IEnumerable<object> rows) where TTemp : class {
    var staged = CreateTempTable<TTemp>(session, rows);
    await GetEntitySession(session).CreatePendingTempTablesAsync();
    return staged;
  }

  // ----- keys --------------------------------------------------------------------------------------------------------------

  /// <summary>Builds the identity of a row from its key values, without a record to read it off.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="values">The key values, one per column of the primary key.</param>
  /// <returns>The key.</returns>
  /// <remarks>For a composite key, which cannot be passed to <c>GetEntity</c> as one value: a caller builds the key
  /// here and passes that instead. The count of values is verified.</remarks>
  /// <example><code>
  /// var key = session.CreatePrimaryKey&lt;ITripLeg&gt;(tripId, 2);
  /// var leg = session.GetEntity&lt;ITripLeg&gt;(key);
  /// </code></example>
  public static EntityKey CreatePrimaryKey<TEntity>(this IEntitySession session, params object?[] values)
    where TEntity : class => CreatePrimaryKey(session, typeof(TEntity), values);

  /// <summary>Builds the identity of a row from its key values, with the entity named by its type.</summary>
  /// <param name="session">The session.</param>
  /// <param name="entityType">The entity interface.</param>
  /// <param name="values">The key values, one per column of the primary key.</param>
  /// <returns>The key.</returns>
  public static EntityKey CreatePrimaryKey(this IEntitySession session, Type entityType, params object?[] values) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(entityType);
    ArgumentNullException.ThrowIfNull(values);

    var entity = session.Context.Model.FindEntity(entityType)
      ?? throw new KeyNotFoundException(
        $"'{entityType.Name}' is not an entity of model '{session.Context.Model.Name}'.");
    var key = entity.PrimaryKey
      ?? throw new InvalidOperationException($"Entity '{entity.Name}' has no primary key.");

    var expected = key.MembersExpanded.Count;
    if (values.Length != expected)
      throw new ArgumentException(
        $"'{entity.Name}' has a primary key of {expected} "
        + $"({string.Join(", ", key.MembersExpanded.Select(m => m.Member.Name))}), and {values.Length} "
        + (values.Length == 1 ? "value was given." : "values were given."), nameof(values));

    return expected == 1 ? new EntityKey(key, values[0]) : new EntityKey(key, values);
  }

  // ----- server values ----------------------------------------------------------------------------------------------

  /// <summary>Asynchronously reads the server's current time, in UTC.</summary>
  /// <param name="session">The session to read through.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the time, of kind UTC.
  /// </returns>
  /// <exception cref="NotSupportedException">The server offers no time.</exception>
  public static async Task<DateTime> GetServerTimeUtcAsync(this IEntitySession session) {
    var time = await ReadServerValueAsync<DateTime?>(session, "time", d => d.SelectServerTimeUtc)
      ?? throw new InvalidOperationException("The server answered nothing for its time.");
    return DateTime.SpecifyKind(time, DateTimeKind.Utc);
  }

  /// <summary>Asynchronously reads the name the connection's user is known to the server by.</summary>
  /// <param name="session">The session to read through.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the name.</returns>
  /// <exception cref="NotSupportedException">The server has no users.</exception>
  public static async Task<string> GetServerUserAsync(this IEntitySession session) =>
    await ReadServerValueAsync<string>(session, "user", d => d.SelectServerUser)
      ?? throw new InvalidOperationException("The server answered nothing for its user.");

  /// <summary>Asynchronously reads the name of the database the connection is in.</summary>
  /// <param name="session">The session to read through.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the name, for SQLite the
  /// file; or <see langword="null"/> where the connection is in no database, as on MySQL with no default schema.
  /// </returns>
  public static Task<string?> GetServerDatabaseAsync(this IEntitySession session) =>
    ReadServerValueAsync<string>(session, "database", d => d.SelectServerDatabase);

  /// <summary>Runs the dialect's statement for a server value and reads the one value back, or
  /// <see langword="null"/> where the server answered none.</summary>
  private static Task<T?> ReadServerValueAsync<T>(IEntitySession session, string what, Func<SqlDialect, string> select) {
    var provider = GetEntitySession(session).Database.Provider;
    var sql = select(provider.SqlDialect);
    if (sql.Length == 0)
      throw new NotSupportedException($"{provider.ServerKind} has no server {what} to read.");
    return session.RunSqlScalarAsync<T>(sql);
  }

  // ----- sequences ----------------------------------------------------------------------------------------------------

  /// <summary>Asynchronously draws the next value of a sequence.</summary>
  /// <param name="session">The session to draw through.</param>
  /// <param name="sequenceName">The sequence's name, with its schema in front where two schemas have one of that
  /// name, as <c>schema.name</c>.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the value.</returns>
  /// <exception cref="NotSupportedException">The server has no sequences.</exception>
  /// <exception cref="ArgumentException">The model has no sequence of that name, or more than one.</exception>
  public static async Task<long> GetNextSequenceValueAsync(this IEntitySession session, string sequenceName,
      CancellationToken cancellation = default) =>
    (await GetNextSequenceValuesAsync(session, sequenceName, 1, cancellation))[0];

  /// <summary>Asynchronously draws the next values of a sequence, in one round trip.</summary>
  /// <param name="session">The session to draw through.</param>
  /// <param name="sequenceName">The sequence's name, with its schema in front where two schemas have one of that
  /// name, as <c>schema.name</c>.</param>
  /// <param name="count">How many values to draw, at least one.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the values, in the order
  /// drawn.</returns>
  /// <exception cref="NotSupportedException">The server has no sequences.</exception>
  /// <exception cref="ArgumentException">The model has no sequence of that name, or more than one.</exception>
  public static Task<IReadOnlyList<long>> GetNextSequenceValuesAsync(this IEntitySession session, string sequenceName,
      int count, CancellationToken cancellation = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    var entitySession = GetEntitySession(session);
    var database = entitySession.Database;
    if (!database.Provider.Supports(DbFeatures.Sequences))
      throw new NotSupportedException($"{database.Provider.ServerKind} has no sequences; nothing to draw '{sequenceName}' from.");
    var sequence = FindSequence(database.DbModel, sequenceName);
    return database.RequestExecutor.DrawSequenceValuesAsync(entitySession, sequence, count, cancellation);
  }

  /// <summary>Finds the db model's sequence a name denotes: by schema and name where a schema is written, by name
  /// alone otherwise.</summary>
  private static DbSequenceInfo FindSequence(DbModel dbModel, string written) {
    var dot = written.LastIndexOf('.');
    var schema = dot < 0 ? null : written[..dot];
    var name = dot < 0 ? written : written[(dot + 1)..];
    var matches = dbModel.Sequences.Where(s =>
      string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
      && (schema == null || string.Equals(s.Schema.Name, schema, StringComparison.OrdinalIgnoreCase))).ToList();
    if (matches.Count == 1)
      return matches[0];
    throw new ArgumentException(matches.Count == 0
      ? $"'{written}' is not a sequence of the model."
      : $"'{written}' names a sequence in {matches.Count} schemas — write it as schema.name.", nameof(written));
  }

  internal static EntitySession GetEntitySession(IEntitySession session) {
    ArgumentNullException.ThrowIfNull(session);
    return session as EntitySession
      ?? throw new ArgumentException($"{session.GetType().Name} is not a MIC entity session.", nameof(session));
  }
}
