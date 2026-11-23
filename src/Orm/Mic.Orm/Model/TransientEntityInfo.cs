using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Orm.Model;

/// <summary>Represents a transient entity the query engine asked for, for a query: a projection's rows, a grouping's
/// keys, a bundle's slots or the rows of a JSON array.</summary>
/// <remarks>Named, typed, nullable value members in a fixed order, with no key, table or record. Made in one call by
/// <see cref="ILinqDataModel.CreateTransientEntity"/> and never changed after. A row of it is a
/// <see cref="Runtime.ProjectionData"/>.</remarks>
public sealed class TransientEntityInfo : ILinqEntity {
  public string Name { get; }

  public IReadOnlyList<TransientMemberInfo> Members { get; }

  public TransientEntityInfo(string name, IReadOnlyList<LinqMemberSpec> members) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(members);
    Name = name;
    var made = new TransientMemberInfo[members.Count];
    for (var i = 0; i < made.Length; i++)
      made[i] = new TransientMemberInfo(this, members[i], i);
    Members = made;
  }

  /// <summary>Finds a member by name, ignoring case.</summary>
  /// <param name="name">The member name.</param>
  /// <returns>The member, or <see langword="null"/> if none has that name.</returns>
  public TransientMemberInfo? FindMember(string name) {
    foreach (var member in Members)
      if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
        return member;
    return null;
  }

  /// <summary>Gets a member by its exact name.</summary>
  /// <param name="name">The member name.</param>
  /// <returns>The member.</returns>
  /// <exception cref="KeyNotFoundException">The entity has no member of that name.</exception>
  public TransientMemberInfo GetMember(string name) {
    foreach (var member in Members)
      if (member.Name == name)
        return member;
    throw new KeyNotFoundException($"Member '{name}' not found on '{Name}'.");
  }

  public override string ToString() => Name;

  IReadOnlyList<ILinqEntityMember> ILinqEntity.Members => Members;

  ILinqEntityMember? ILinqEntity.FindMember(string name) => FindMember(name);

  IReadOnlyList<ILinqEntityMember> ILinqEntity.PrimaryKeyMembers => [];

  bool ILinqEntity.IsTransient => true;
  bool ILinqEntity.IsCached => false;
  bool ILinqEntity.IsReadOnly => true;
}

/// <summary>Represents a value member of a <see cref="TransientEntityInfo"/>.</summary>
public sealed class TransientMemberInfo : ILinqEntityMember {
  public TransientEntityInfo Entity { get; }

  public string Name { get; }

  public Type DataType { get; }

  public bool IsNullable { get; }

  /// <summary>Gets the position of the member's value in a row of the entity.</summary>
  public int DataIndex { get; }

  internal TransientMemberInfo(TransientEntityInfo entity, LinqMemberSpec spec, int dataIndex) {
    Entity = entity;
    Name = spec.Name;
    DataType = spec.DataType;
    IsNullable = spec.IsNullable;
    DataIndex = dataIndex;
  }

  public override string ToString() => $"{Entity.Name}.{Name}";

  Type? ILinqEntityMember.DataType => DataType;

  LinqMemberKind ILinqEntityMember.Kind => LinqMemberKind.Value;

  ILinqEntity? ILinqEntityMember.Entity => Entity;

  ILinqReferenceInfo? ILinqEntityMember.ReferenceInfo => null;

  ILinqMemberListInfo? ILinqEntityMember.ListInfo => null;

  bool ILinqEntityMember.IsOnDemand => false;
}
