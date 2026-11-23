using Mic.Linq.Model;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Assigns each member of an entity the read and write behaviour of its kind, once, when the model is built.
///</summary>
public static class MemberValueAccessorHelper {
  /// <summary>Gives every member of an entity its reading and writing behaviour.</summary>
  /// <param name="entity">The entity whose members to set up.</param>
  public static void AssignAccessors(EntityTypeInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    foreach (var member in entity.Members)
      AssignAccessors(member);
  }

  public static void AssignAccessors(EntityMemberInfo member) {
    switch (member.Kind) {
      case MemberKind.Reference:
        member.GetValueFunc = GetReferenceValue;
        member.SetValueAction = SetReferenceValue;
        break;

      case MemberKind.List:
        member.GetValueFunc = member.IsQueryable ? GetQueryableValue : GetListValue;
        member.SetValueAction = IgnoreValue;
        break;

      case MemberKind.Transient or MemberKind.Column when member.Computed != null:
        member.GetValueFunc = GetComputedValue;
        member.SetValueAction = RefuseComputedValue;
        break;

      // A JSON document is text until it is read.
      case MemberKind.Column when member.IsJson:
        member.GetValueFunc = GetJsonValue;
        member.SetValueAction = SetColumnValue;
        break;

      // A column and a transient member read and write alike; whether the value is persisted is the save's business.
      default:
        member.GetValueFunc = GetColumnValue;
        member.SetValueAction = member.HashMember != null ? SetHashedColumnValue : SetColumnValue;
        break;
    }
  }

  // ----- columns and transients ----------------------------------------------------------------------------------------

  /// <summary>Gets a column member's value, loading it first if the record does not hold it yet.</summary>
  /// <remarks>The load is synchronous: the record's row, which fills every member but the on-demand ones, or the
  /// member alone where it is on demand. A <see langword="null"/> slot is coalesced to the member's default, so a
  /// key member nobody has set reads as the default of its type; <see cref="EntityRecord.GetRawValue"/> still sees
  /// the null.</remarks>
  private static object? GetColumnValue(EntityRecord record, EntityMemberInfo member) {
    if (!record.Loaded.IsSet(member.DataIndex) && record.Session is { } session)
      session.LoadValue(record, member);

    return record.GetRawValue(member) ?? member.DefaultValue;
  }

  /// <summary>Gets a JSON member's object, loading it first if the record does not hold it yet and making the object
  /// from the document's text on the first read.</summary>
  private static object? GetJsonValue(EntityRecord record, EntityMemberInfo member) {
    if (!record.Loaded.IsSet(member.DataIndex) && record.Session is { } session)
      session.LoadValue(record, member);

    return JsonMemberHelper.GetObject(record, member) ?? member.DefaultValue;
  }

  /// <summary>Writes a column member and clears the reference it belongs to, if any, so that the next read resolves
  /// the target again.</summary>
  /// <remarks>The reference is cleared rather than re-resolved because a composite foreign key is written one column
  /// at a time.</remarks>
  private static void SetColumnValue(EntityRecord record, EntityMemberInfo member, object? value) {
    record.SetRawValue(member, value);
    if (member.OwnerReference is { } reference)
      record.SetRawValue(reference, null);
  }

  /// <summary>Writes a text member that another member holds a hash of, and the hash with it.</summary>
  private static void SetHashedColumnValue(EntityRecord record, EntityMemberInfo member, object? value) {
    SetColumnValue(record, member, value);
    var hashMember = member.HashMember!;
    var hash = EntityAppExtensions.ComputeStableHash64(value as string);
    var isLong = (Nullable.GetUnderlyingType(hashMember.DataType) ?? hashMember.DataType) == typeof(long);
    // Boxed as the member's own type: the slot is compared and bound as it is.
    record.SetRawValue(hashMember, isLong ? hash : (object)EntityAppExtensions.FoldHash(hash));
  }

  // ----- computed members ------------------------------------------------------------------------------------------------

  /// <summary>Gets a computed member's value, calling its method over the entity instance on every read.</summary>
  /// <remarks>A record with no session has no entity instance to compute over, so the slot is read: what a persisted
  /// member was loaded with, or the default.</remarks>
  private static object? GetComputedValue(EntityRecord record, EntityMemberInfo member) =>
    record.Session == null ? record.GetRawValue(member) ?? member.DefaultValue
      : member.Computed!.Compute(record.EntityInstance);

  /// <summary>Refuses a write to a computed member.</summary>
  private static void RefuseComputedValue(EntityRecord record, EntityMemberInfo member, object? value) =>
    throw new InvalidOperationException($"'{member.Entity?.Name}.{member.Name}' is computed and cannot be assigned.");

  /// <summary>Writes the computed value of every persisted computed member into the record's slot, so that the
  /// statement about to be built from the record carries it.</summary>
  /// <param name="record">The record about to become an insert or an update.</param>
  /// <remarks>A value that differs from the slot's marks the member modified, so an update names it only when it
  /// changed.</remarks>
  public static void ComputeStoredValues(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    if (record.Status is not (EntityStatus.New or EntityStatus.Modified))
      return;
    foreach (var member in record.Entity.Members)
      if (member.Computed is { Stored: true } computed)
        record.SetRawValue(member, computed.Compute(record.EntityInstance));
  }

  // ----- references ------------------------------------------------------------------------------------------------------

  /// <summary>Gets a reference member's target, resolving it from the foreign key as a stub when the slot is empty
  /// and the key is not.</summary>
  /// <remarks>Resolving fetches nothing: the stub carries its primary key only, and comes from the session so that
  /// one identity has one object. The stub is kept in the slot as loaded, not modified; writing the foreign key
  /// clears it again. A missing part of the key, or no session, yields <see langword="null"/>.</remarks>
  private static object? GetReferenceValue(EntityRecord record, EntityMemberInfo member) {
    if (record.GetRawValue(member) is { } target)
      return target;

    var reference = member.ReferenceInfo;
    if (reference == null || record.Session is not { } session)
      return null;

    var members = reference.FromKey.MembersExpanded;
    var values = new object?[members.Count];
    for (var i = 0; i < members.Count; i++) {
      // Any missing key member means no target, which includes a half-written composite key.
      if ((values[i] = record.GetRawValue(members[i].Member)) == null)
        return null;
    }

    // The reference may target a unique key rather than the primary key; the stub is built from the key it names.
    var stub = session.GetStub(new EntityKey(reference.ToKey, values));

    // Loaded, not modified: resolving a reference is not a change to save.
    record.SetLoadedValue(member, stub);
    return stub;
  }

  /// <summary>Assigns a reference member's target and copies the target's key into the foreign-key
  /// members.</summary>
  /// <remarks>A <see langword="null"/> clears the foreign-key members. The copy goes through the raw accessors at both
  /// ends: writing the foreign key through the column accessor would clear the reference just assigned.</remarks>
  private static void SetReferenceValue(EntityRecord record, EntityMemberInfo member, object? value) {
    record.SetRawValue(member, value);

    var reference = member.ReferenceInfo;
    if (reference == null)
      return;

    var fromMembers = reference.FromKey.MembersExpanded;
    var toMembers = reference.ToKey.MembersExpanded;

    if (value == null) {
      foreach (var from in fromMembers)
        record.SetRawValue(from.Member, null);
      return;
    }

    var target = (value as EntityBase)?.Record
      ?? throw new ArgumentException(
        $"'{member.Entity?.Name}.{member.Name}' was given {value.GetType().Name}, which is not a MIC entity.");

    CopyKey(record, reference, target);
  }

  /// <summary>Brings every resolved reference's foreign-key members back into step with its target's key.</summary>
  /// <param name="record">The record whose foreign keys to refresh.</param>
  /// <remarks>A target's key can change after the reference was assigned, as a server-generated key does at its
  /// insert. Call this when the record is about to become a statement. An empty reference slot is left alone; its
  /// foreign key is then the only truth.</remarks>
  public static void RefreshForeignKeys(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    foreach (var member in record.Entity.Members) {
      // An empty slot is not stale; filling it would load a target nobody asked for.
      if (member.Kind != MemberKind.Reference || member.ReferenceInfo is not { } reference)
        continue;
      if (record.GetRawValue(member) is EntityBase { Record: { } target })
        CopyKey(record, reference, target);
    }
  }

  /// <summary>Copies the target's key into the referencing record's foreign-key members, position by position,
  /// writing only the values that differ.</summary>
  /// <remarks>Skipping unchanged values keeps an update from naming a foreign key nobody moved.</remarks>
  private static void CopyKey(EntityRecord record, ReferenceInfo reference, EntityRecord target) {
    var fromMembers = reference.FromKey.MembersExpanded;
    var toMembers = reference.ToKey.MembersExpanded;
    var count = Math.Min(fromMembers.Count, toMembers.Count);
    for (var i = 0; i < count; i++) {
      var value = target.GetRawValue(toMembers[i].Member);
      if (!Equals(value, record.GetRawValue(fromMembers[i].Member)))
        record.SetRawValue(fromMembers[i].Member, value);
    }
  }

  // ----- lists -------------------------------------------------------------------------------------------------------------

  /// <summary>Gets a queryable member's value in ordinary code, such as <c>customer.Orders</c>: a query over the
  /// target, filtered to this owner, built on every read and fetching nothing until it runs.</summary>
  /// <remarks>Inside a query the engine reads the member off the model and never reaches here.</remarks>
  private static object? GetQueryableValue(EntityRecord record, EntityMemberInfo member) {
    var session = record.Session
      ?? throw new InvalidOperationException(
        $"'{member.Entity?.Name}.{member.Name}' is a query, and this entity has no session to run one in.");
    return QueryablePropertyHelper.BuildQuery(session.QueryProvider, record, member);
  }

  /// <summary>Gets a list member's list object, making it on the first read and keeping it in the slot as
  /// loaded.</summary>
  /// <remarks>The list is a <see cref="MemberEntityList{TEntity}"/> over the target's interface, which is what the
  /// emitted property returns; the closed type is made once per member and kept on the model.</remarks>
  private static object? GetListValue(EntityRecord record, EntityMemberInfo member) {
    if (record.GetRawValue(member) is { } existing)
      return existing;
    var info = member.ListInfo
      ?? throw new InvalidOperationException($"'{member.Entity?.Name}.{member.Name}' is a list with no list info.");
    var listType = info.ListType ??= typeof(MemberEntityList<>).MakeGenericType(info.TargetEntity.InterfaceType
      ?? throw new InvalidOperationException(
        $"'{member.Entity?.Name}.{member.Name}': '{info.TargetEntity.Name}' has no interface to type a list by."));
    var list = Activator.CreateInstance(listType, record, member)!;
    record.SetLoadedValue(member, list);
    return list;
  }

  /// <summary>Ignores a write to a list member; items are added and removed through the list itself.</summary>
  private static void IgnoreValue(EntityRecord record, EntityMemberInfo member, object? value) { }
}
