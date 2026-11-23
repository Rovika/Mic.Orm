using System.Linq.Expressions;
using Mic.Linq.Nodes;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Resolves what an include names against the model: a selector or a path to the chains of members it
/// walks, a reference or a list of the entity, then a member of that member's target, and so on. This class cannot
/// be inherited.</summary>
/// <remarks>Resolution happens per run; the entity an include applies to is only known where the rows are.</remarks>
internal static class IncludeHelper {
  /// <summary>Gets the member chains an include names on an entity, several for a <c>new { … }</c> selector, each
  /// starting at a member of the entity with each hop a reference or a list, and an on-demand member as the last.
  /// </summary>
  /// <param name="entity">The entity the include applies to.</param>
  /// <param name="include">The include, as a selector or a path.</param>
  /// <returns>The member chains.</returns>
  /// <remarks>A value member that is not on demand, a queryable property or a name the entity does not have is
  /// refused by name.</remarks>
  public static IReadOnlyList<IReadOnlyList<EntityMemberInfo>> GetChains(EntityTypeInfo entity, QueryInclude include) {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(include);
    if (include.Path is { } path)
      return [ResolvePath(entity, path.Split('.'), path)];
    var selector = include.Selector!;
    var body = StripConversion(selector.Body);
    if (body is NewExpression created)
      return [.. created.Arguments.Select(argument => ResolveMember(entity, StripConversion(argument), selector))];
    return [ResolveMember(entity, body, selector)];
  }

  // A selector's body is `object`, so the compiler boxes a value or converts a reference: the conversion is noise.
  private static Expression StripConversion(Expression expression) {
    while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
      expression = convert.Operand;
    return expression;
  }

  // `o.Customer.Region` as the names walked from the parameter outward.
  private static IReadOnlyList<EntityMemberInfo> ResolveMember(EntityTypeInfo entity, Expression expression,
      LambdaExpression selector) {
    var names = new List<string>();
    for (var at = expression; at is not ParameterExpression;) {
      if (at is not MemberExpression member || member.Expression == null)
        throw new NotSupportedException(
          $"An include names members of the entity — a reference, a list, an on-demand member, or a chain of them — "
          + $"not '{selector}'.");
      names.Insert(0, member.Member.Name);
      at = StripConversion(member.Expression);
    }
    return ResolvePath(entity, names, selector.ToString());
  }

  private static IReadOnlyList<EntityMemberInfo> ResolvePath(EntityTypeInfo entity, IReadOnlyList<string> names, string written) {
    if (names.Count == 0)
      throw new NotSupportedException($"An include names at least one member: '{written}'.");
    var chain = new List<EntityMemberInfo>(names.Count);
    var owner = entity;
    for (var i = 0; i < names.Count; i++) {
      var name = names[i];
      var member = owner.FindMember(name)
        ?? throw new NotSupportedException($"'{owner.Name}' has no member '{name}', which the include '{written}' names.");
      // An on-demand member is a value: it ends the chain.
      if (member.IsOnDemand && i == names.Count - 1) {
        chain.Add(member);
        break;
      }
      if (member.Kind is not (MemberKind.Reference or MemberKind.List) || member.IsQueryable || member.TargetEntity == null)
        throw new NotSupportedException(
          $"'{owner.Name}.{name}' is not a reference, a list or an on-demand member, and an include loads nothing "
          + $"else: '{written}'.");
      chain.Add(member);
      owner = member.TargetEntity;
    }
    return chain;
  }
}
