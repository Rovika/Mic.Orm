using Mic.Linq.Model;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Represents one row of a transient entity, such as a projection's row or a composite group key: named
/// values in the order of the entity's members.</summary>
/// <remarks>The query engine reads and writes it as an <see cref="ILinqRow"/>. It has no identity, no change
/// tracking and no entity instance.</remarks>
public sealed class ProjectionData : ILinqRow {
  public TransientEntityInfo Entity { get; }

  readonly object?[] _values;

  public ProjectionData(TransientEntityInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    Entity = entity;
    _values = new object?[entity.Members.Count];
  }

  public object? GetValue(TransientMemberInfo member) => _values[member.DataIndex];

  public void SetValue(TransientMemberInfo member, object? value) => _values[member.DataIndex] = value;

  /// <summary>Gets the value of a member by its exact name.</summary>
  /// <param name="memberName">The member name.</param>
  /// <returns>The value.</returns>
  /// <exception cref="KeyNotFoundException">The row's entity has no member of that name.</exception>
  public object? GetValue(string memberName) => GetValue(Entity.GetMember(memberName));

  /// <summary>Sets the value of a member by its exact name.</summary>
  /// <param name="memberName">The member name.</param>
  /// <param name="value">The value.</param>
  /// <exception cref="KeyNotFoundException">The row's entity has no member of that name.</exception>
  public void SetValue(string memberName, object? value) => SetValue(Entity.GetMember(memberName), value);

  public object? GetValue(ILinqEntityMember member) => GetValue((TransientMemberInfo)member);

  public void SetValue(ILinqEntityMember member, object? value) => SetValue((TransientMemberInfo)member, value);

  public override string ToString() =>
    string.Join(", ", Entity.Members.Select(m => $"{m.Name}={GetValue(m) ?? "null"}"));

  ILinqEntity ILinqRow.Entity => Entity;
}
