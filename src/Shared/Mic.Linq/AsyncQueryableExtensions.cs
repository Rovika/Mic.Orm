using System.Linq.Expressions;
using System.Reflection;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Linq;

/// <summary>Provides the asynchronous terminal operations of a typed query.</summary>
/// <remarks>Each method appends the corresponding <c>Queryable</c> call to the query's expression and hands the whole
/// expression to the query's executor, so a terminal call and the same call written inside a sub-query are translated
/// alike.</remarks>
public static class AsyncQueryableExtensions {

  /// <summary>Asynchronously runs the query and returns its rows.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the rows.</returns>
  public static async Task<IReadOnlyList<T>> ToListAsync<T>(this IQueryable<T> source,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(source);
    var result = await GetExecutor(source).ExecuteAsync(source.Expression, cancellationToken);
    if (result is IReadOnlyList<T> already)
      return already;
    if (result is not System.Collections.IEnumerable rows)
      throw new InvalidOperationException($"This query produced a single {result?.GetType().Name ?? "null"}, not rows.");
    // A projection into an object comes back as named values; the projected type turns them into instances.
    if (result is System.Collections.IList { Count: > 0 } projected && projected[0] is ILinqRow)
      return ProjectionReadHelper.Materialize<T>(projected);
    // A grouping returned as groups comes back with its keys and rows untyped; the typed groups are built over them.
    if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IGrouping<,>))
      return ProjectionReadHelper.MaterializeGroups<T>(rows,
        FindSingleValueName(source.Expression, typeof(T).GetGenericArguments()[0]));
    // A projection into an object of one value comes back as the values; the object is built around each.
    if (FindSingleValueName(source.Expression, typeof(T)) is { } valueName)
      return ProjectionReadHelper.MaterializeValues<T>(rows, valueName);
    var list = new List<T>();
    foreach (var row in rows) {
      // An aggregate over no rows is NULL on every server; unboxing that into a value type would throw from inside
      // the framework with nothing in the message to act on.
      if (row is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
        throw new InvalidOperationException(
          $"The query produced a NULL where a '{typeof(T).Name}' was expected — an aggregate over no rows is null on "
          + "every server. Project it as a nullable, or narrow the query so it cannot be empty.");
      list.Add((T)row!);
    }
    return list;
  }

  /// <summary>Asynchronously returns the first row of the query, or a default value if the query has no rows.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the first row, or the
  /// default value of <typeparamref name="T"/> if the query has no rows.</returns>
  public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source,
      CancellationToken cancellationToken = default) =>
    OneAsync(source, QueryableMethods<T>.FirstOrDefault, allowNone: true, allowMany: true, cancellationToken);

  /// <summary>Asynchronously returns the first row of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the first row.</returns>
  /// <exception cref="InvalidOperationException">The query has no rows.</exception>
  public static async Task<T> FirstAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    (await OneAsync(source, QueryableMethods<T>.First, allowNone: false, allowMany: true, cancellationToken))!;

  /// <summary>Asynchronously returns the only row of the query, or a default value if the query has no rows.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the only row, or the
  /// default value of <typeparamref name="T"/> if the query has no rows.</returns>
  /// <exception cref="InvalidOperationException">The query has more than one row.</exception>
  public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source,
      CancellationToken cancellationToken = default) =>
    OneAsync(source, QueryableMethods<T>.SingleOrDefault, allowNone: true, allowMany: false, cancellationToken);

  /// <summary>Asynchronously returns the only row of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the only row.</returns>
  /// <exception cref="InvalidOperationException">The query has no rows.
  /// -or-
  /// The query has more than one row.</exception>
  public static async Task<T> SingleAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    (await OneAsync(source, QueryableMethods<T>.Single, allowNone: false, allowMany: false, cancellationToken))!;

  /// <summary>Asynchronously returns the last row of the query, or a default value if the query has no rows.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the last row, or the
  /// default value of <typeparamref name="T"/> if the query has no rows.</returns>
  /// <remarks>The statement reverses the ordering and takes one row; the whole result is not read.</remarks>
  public static Task<T?> LastOrDefaultAsync<T>(this IQueryable<T> source,
      CancellationToken cancellationToken = default) =>
    OneAsync(source, QueryableMethods<T>.LastOrDefault, allowNone: true, allowMany: true, cancellationToken);

  /// <summary>Asynchronously returns the last row of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the last row.</returns>
  /// <exception cref="InvalidOperationException">The query has no rows.</exception>
  public static async Task<T> LastAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    (await OneAsync(source, QueryableMethods<T>.Last, allowNone: false, allowMany: true, cancellationToken))!;

  /// <summary>Asynchronously determines whether the query has any rows.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if
  /// the query has any rows; otherwise, <see langword="false"/>.</returns>
  /// <remarks>The statement is an <c>EXISTS</c> condition, which stops at the first row.</remarks>
  public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    Run<T, bool>(source, QueryableMethods<T>.Any, cancellationToken);

  /// <summary>Asynchronously determines whether any row of the query satisfies a condition.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="predicate">A function to test each row for a condition.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if
  /// any row satisfies the condition; otherwise, <see langword="false"/>.</returns>
  public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate,
      CancellationToken cancellationToken = default) =>
    source.Where(predicate).AnyAsync(cancellationToken);

  /// <summary>Asynchronously determines whether every row of the query satisfies a condition.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="predicate">A function to test each row for a condition.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if
  /// every row satisfies the condition; otherwise, <see langword="false"/>.</returns>
  public static async Task<bool> AllAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(predicate);
    // The predicate is negated in the expression, so what runs is an ordinary EXISTS over the counter-examples.
    var negated = Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);
    return !await source.Where(negated).AnyAsync(cancellationToken);
  }

  /// <summary>Asynchronously returns the number of rows of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows.</returns>
  public static Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    Run<T, int>(source, QueryableMethods<T>.Count, cancellationToken);

  /// <summary>Asynchronously returns the number of rows of the query that satisfy a condition.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="predicate">A function to test each row for a condition.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows that
  /// satisfy the condition.</returns>
  public static Task<int> CountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate,
      CancellationToken cancellationToken = default) =>
    source.Where(predicate).CountAsync(cancellationToken);

  /// <summary>Asynchronously returns the number of rows of the query as a 64-bit integer.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows.</returns>
  public static Task<long> LongCountAsync<T>(this IQueryable<T> source,
      CancellationToken cancellationToken = default) =>
    Run<T, long>(source, QueryableMethods<T>.LongCount, cancellationToken);

  /// <summary>Asynchronously returns the number of rows of the query that satisfy a condition as a 64-bit integer.
  /// </summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="predicate">A function to test each row for a condition.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows that
  /// satisfy the condition.</returns>
  public static Task<long> LongCountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate,
      CancellationToken cancellationToken = default) =>
    source.Where(predicate).LongCountAsync(cancellationToken);

  /// <summary>Asynchronously returns the least value a selector produces over the rows of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <typeparam name="TResult">The type of the value the selector produces.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="selector">A function to apply to each row.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the least value, or the
  /// default value of <typeparamref name="TResult"/> if the query has no rows.</returns>
  public static Task<TResult> MinAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector,
      CancellationToken cancellationToken = default) =>
    Run<T, TResult>(source, QueryableMethods<T, TResult>.Min, selector, cancellationToken);

  /// <summary>Asynchronously returns the greatest value a selector produces over the rows of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <typeparam name="TResult">The type of the value the selector produces.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="selector">A function to apply to each row.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the greatest value, or the
  /// default value of <typeparamref name="TResult"/> if the query has no rows.</returns>
  public static Task<TResult> MaxAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector,
      CancellationToken cancellationToken = default) =>
    Run<T, TResult>(source, QueryableMethods<T, TResult>.Max, selector, cancellationToken);

  /// <summary>Asynchronously returns the sum of the values a selector produces over the rows of the query.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <typeparam name="TResult">The numeric type of the value the selector produces.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="selector">A function to apply to each row.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the sum.</returns>
  /// <exception cref="NotSupportedException"><c>Queryable.Sum</c> has no overload for
  /// <typeparamref name="TResult"/>.</exception>
  public static Task<TResult> SumAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector,
      CancellationToken cancellationToken = default) =>
    Run<T, TResult>(source, GetSelectorAggregateMethod<T, TResult>(nameof(Queryable.Sum)), selector, cancellationToken);

  /// <summary>Asynchronously returns the average of the values a selector produces over the rows of the query.
  /// </summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <typeparam name="TResult">The numeric type of the value the selector produces.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="selector">A function to apply to each row.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the average.</returns>
  /// <exception cref="NotSupportedException"><c>Queryable.Average</c> has no overload for
  /// <typeparamref name="TResult"/>.</exception>
  public static Task<TResult> AverageAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector,
      CancellationToken cancellationToken = default) =>
    Run<T, TResult>(source, GetSelectorAggregateMethod<T, TResult>(nameof(Queryable.Average)), selector, cancellationToken);

  /// <summary>Asynchronously returns the average of the integers a selector produces over the rows of the query, as
  /// a double-precision number.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to run.</param>
  /// <param name="selector">A function to apply to each row.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the average, with its
  /// fraction.</returns>
  public static Task<double> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, int>> selector,
      CancellationToken cancellationToken = default) =>
    RunAverage<T, int, double>(source, selector, cancellationToken);

  /// <summary>Asynchronously returns the average of the nullable integers a selector produces over the rows of the
  /// query, as a double-precision number.</summary>
  /// <inheritdoc cref="AverageAsync{T}(IQueryable{T}, Expression{Func{T, int}}, CancellationToken)"/>
  /// <returns>A task that represents the asynchronous operation. The task result contains the average of the
  /// values that are not null, or <see langword="null"/> if there is none.</returns>
  public static Task<double?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, int?>> selector,
      CancellationToken cancellationToken = default) =>
    RunAverage<T, int?, double?>(source, selector, cancellationToken);

  /// <summary>Asynchronously returns the average of the 64-bit integers a selector produces over the rows of the
  /// query, as a double-precision number.</summary>
  /// <inheritdoc cref="AverageAsync{T}(IQueryable{T}, Expression{Func{T, int}}, CancellationToken)"/>
  public static Task<double> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, long>> selector,
      CancellationToken cancellationToken = default) =>
    RunAverage<T, long, double>(source, selector, cancellationToken);

  /// <summary>Asynchronously returns the average of the nullable 64-bit integers a selector produces over the rows
  /// of the query, as a double-precision number.</summary>
  /// <inheritdoc cref="AverageAsync{T}(IQueryable{T}, Expression{Func{T, int?}}, CancellationToken)"/>
  public static Task<double?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, long?>> selector,
      CancellationToken cancellationToken = default) =>
    RunAverage<T, long?, double?>(source, selector, cancellationToken);

  // ----- plumbing -------------------------------------------------------------------------------------------------------

  private static Task<TResult> Run<T, TResult>(IQueryable<T> source, MethodInfo method,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(source);
    return RunScalar<TResult>(GetExecutor(source), Expression.Call(method, source.Expression), cancellationToken);
  }

  private static Task<TResult> Run<T, TResult>(IQueryable<T> source, MethodInfo method,
      Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selector);
    return RunScalar<TResult>(GetExecutor(source),
      Expression.Call(method, source.Expression, Expression.Quote(selector)), cancellationToken);
  }

  /// <summary>Runs an average whose result is of another type than the values averaged, as an average of integers
  /// is.</summary>
  private static Task<TResult> RunAverage<T, TValue, TResult>(IQueryable<T> source,
      Expression<Func<T, TValue>> selector, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selector);
    var method = GetSelectorAggregateMethod<T, TValue>(nameof(Queryable.Average));
    return RunScalar<TResult>(GetExecutor(source),
      Expression.Call(method, source.Expression, Expression.Quote(selector)), cancellationToken);
  }

  /// <summary>Runs an aggregate and returns its one value as the caller's type, or the default value of
  /// <typeparamref name="TResult"/> if the aggregate produced <see langword="null"/>.</summary>
  private static async Task<TResult> RunScalar<TResult>(IQueryExecutor executor, Expression expression,
      CancellationToken cancellationToken) {
    var result = await executor.ExecuteAsync(expression, cancellationToken);
    if (result is TResult typed)
      return typed;
    if (result == null)
      return default!;
    // A number of another type than the caller's is converted, not dropped.
    var resultType = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
    return (TResult)Convert.ChangeType(result, resultType, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>Runs a query cut to one row and returns that row, or throws when there is not exactly one.</summary>
  /// <remarks>The cut is the statement's: <c>First</c> asks the server for one row and <c>Single</c> for two.</remarks>
  private static async Task<T?> OneAsync<T>(IQueryable<T> source, MethodInfo method, bool allowNone, bool allowMany,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(source);
    var rows = await ToListAsync(
      new EntityQuery<T>((EntityQueryProvider)source.Provider, Expression.Call(method, source.Expression)),
      cancellationToken);
    if (rows.Count == 0)
      return allowNone ? default : throw new InvalidOperationException("The query matched no rows.");
    if (rows.Count > 1 && !allowMany)
      throw new InvalidOperationException("The query matched more than one row, and one was asked for.");
    return rows[0];
  }

  /// <summary>Finds the name of the one value the rows of a query are constructed from, as in
  /// <c>Select(p => new { p.Rank })</c>.</summary>
  /// <param name="query">The query's expression.</param>
  /// <param name="rowType">The type of the query's rows.</param>
  /// <returns>The name the constructed type takes the value under; or <see langword="null"/> if the rows are not
  /// constructed by the query, or from another number of values.</returns>
  /// <remarks>The engine answers a projection of one value with the values. The calls over the projection that keep
  /// its rows, such as an ordering or a page, are looked through.</remarks>
  private static string? FindSingleValueName(Expression query, Type rowType) {
    if (rowType.IsScalarType())
      return null;
    var current = query;
    for (; current is MethodCallExpression { Arguments.Count: > 0 } call; current = call.Arguments[0])
      for (var i = call.Arguments.Count - 1; i > 0; i--) {
        var argument = call.Arguments[i] is UnaryExpression { NodeType: ExpressionType.Quote } quote
          ? quote.Operand : call.Arguments[i];
        switch ((argument as LambdaExpression)?.Body) {
          case NewExpression { Arguments.Count: 1, Constructor: { } constructor } made when made.Type == rowType:
            return made.Members?[0].Name ?? constructor.GetParameters()[0].Name;
          case MemberInitExpression { NewExpression.Arguments.Count: 0, Bindings: [MemberAssignment only] } made
              when made.Type == rowType:
            return only.Member.Name;
          case NewExpression or MemberInitExpression:
            return null;
        }
      }
    return null;
  }

  private static IQueryExecutor GetExecutor<T>(IQueryable<T> source) =>
    source.Provider is EntityQueryProvider provider
      ? provider.Executor
      : throw new ArgumentException(
        $"This is not a MIC query — its provider is {source.Provider.GetType().Name}.", nameof(source));

  private static MethodInfo GetSelectorAggregateMethod<T, TResult>(string name) =>
    (SelectorAggregates.TryGetValue((name, typeof(TResult)), out var method)
      ? method
      : throw new NotSupportedException($"{name} is not defined over '{typeof(TResult).Name}'."))
    .MakeGenericMethod(typeof(T));

  /// <summary>Holds the <c>Queryable.Sum</c> and <c>Queryable.Average</c> overloads, keyed by the type their
  /// selector returns.</summary>
  private static readonly Dictionary<(string Name, Type Result), MethodInfo> SelectorAggregates = BuildSelectorAggregates();

  private static Dictionary<(string, Type), MethodInfo> BuildSelectorAggregates() {
    var table = new Dictionary<(string, Type), MethodInfo>();
    foreach (var method in typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)) {
      if (method.Name is not (nameof(Queryable.Sum) or nameof(Queryable.Average)))
        continue;
      var parameters = method.GetParameters();
      if (parameters.Length != 2 || !parameters[1].ParameterType.IsGenericType)
        continue;
      // The selector is Expression<Func<TSource, TValue>>; TValue is the numeric type the overload is for.
      var selector = parameters[1].ParameterType.GetGenericArguments()[0];
      var valueType = selector.GetGenericArguments()[1];
      if (!valueType.IsGenericParameter)
        table[(method.Name, valueType)] = method;
    }
    return table;
  }
}

/// <summary>Holds the <c>Queryable</c> methods generic in the entity type, named through method groups so that the
/// compiler instantiates them where the class is closed.</summary>
file static class QueryableMethods<T> {
  public static readonly MethodInfo Count = ((Func<IQueryable<T>, int>)Queryable.Count).Method;
  public static readonly MethodInfo LongCount = ((Func<IQueryable<T>, long>)Queryable.LongCount).Method;
  public static readonly MethodInfo First = ((Func<IQueryable<T>, T>)Queryable.First).Method;
  public static readonly MethodInfo FirstOrDefault = ((Func<IQueryable<T>, T?>)Queryable.FirstOrDefault).Method;
  public static readonly MethodInfo Single = ((Func<IQueryable<T>, T>)Queryable.Single).Method;
  public static readonly MethodInfo SingleOrDefault = ((Func<IQueryable<T>, T?>)Queryable.SingleOrDefault).Method;
  public static readonly MethodInfo Last = ((Func<IQueryable<T>, T>)Queryable.Last).Method;
  public static readonly MethodInfo LastOrDefault = ((Func<IQueryable<T>, T?>)Queryable.LastOrDefault).Method;

  public static readonly MethodInfo Where =
    ((Func<IQueryable<T>, Expression<Func<T, bool>>, IQueryable<T>>)Queryable.Where).Method;

  public static readonly MethodInfo Any = ((Func<IQueryable<T>, bool>)Queryable.Any).Method;
}

/// <summary>Holds the <c>Queryable.Min</c> and <c>Queryable.Max</c> methods, named through method groups whose
/// return type is nullable, as the methods declare it.</summary>
file static class QueryableMethods<T, TResult> {
  public static readonly MethodInfo Min =
    ((Func<IQueryable<T>, Expression<Func<T, TResult>>, TResult?>)Queryable.Min).Method;

  public static readonly MethodInfo Max =
    ((Func<IQueryable<T>, Expression<Func<T, TResult>>, TResult?>)Queryable.Max).Method;
}
