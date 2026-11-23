using System.Linq.Expressions;
using System.Reflection;

namespace Mic.Linq;

/// <summary>Provides extension methods that name the references, the lists and the on-demand members a query loads
/// together with its rows, before a row is handed to the caller.</summary>
/// <remarks>An include may name a reference or a list member, a chain of them such as <c>o => o.Customer.Region</c>,
/// a member the host loads on demand, alone or at the end of a chain, or several at once in an anonymous object
/// such as <c>o => new { o.Lines, o.Customer }</c>. Any other value member is refused by name when the query runs.
/// </remarks>
public static class IncludeQueryableExtensions {
  static readonly MethodInfo IncludeMethod = Find(genericArguments: 1);
  static readonly MethodInfo IncludeForMethod = Find(genericArguments: 2);

  /// <summary>Names the members of the query's rows to load together with the rows.</summary>
  /// <typeparam name="T">The entity type of the rows.</typeparam>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that selects a reference, a list, a chain of them, or several in an anonymous
  /// object.</param>
  /// <returns>A query whose rows are the rows of <paramref name="source"/>, loaded with the selected members.</returns>
  public static IQueryable<T> Include<T>(this IQueryable<T> source, Expression<Func<T, object?>> selector) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selector);
    return source.Provider.CreateQuery<T>(Expression.Call(null, IncludeMethod.MakeGenericMethod(typeof(T)),
      source.Expression, Expression.Quote(selector)));
  }

  /// <summary>Names the members to load together with every row of <typeparamref name="TLoaded"/> the query loads,
  /// whether as its own rows or as rows another include brought in.</summary>
  /// <typeparam name="T">The entity type of the query's rows.</typeparam>
  /// <typeparam name="TLoaded">The entity type whose rows the selector is written over.</typeparam>
  /// <param name="source">The query.</param>
  /// <param name="selector">A function that selects a reference, a list, a chain of them, or several in an anonymous
  /// object.</param>
  /// <returns>A query whose rows are the rows of <paramref name="source"/>, loaded with the selected members.</returns>
  public static IQueryable<T> Include<T, TLoaded>(this IQueryable<T> source, Expression<Func<TLoaded, object?>> selector) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selector);
    return source.Provider.CreateQuery<T>(Expression.Call(null, IncludeForMethod.MakeGenericMethod(typeof(T), typeof(TLoaded)),
      source.Expression, Expression.Quote(selector)));
  }

  private static MethodInfo Find(int genericArguments) =>
    typeof(IncludeQueryableExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Single(m => m.Name == nameof(Include) && m.GetGenericArguments().Length == genericArguments);
}
