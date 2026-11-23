using System.Reflection;
using Mic.Common;
using Mic.Orm.Db;

namespace Mic.Orm.Model.Construction;

/// <summary>Runs the reference pass of the model builder: a reference member gets the foreign key underneath it, and
/// a list member is told which member on the other side owns that key.</summary>
/// <remarks>Driven by a member's kind rather than by an attribute; <c>[EntityRef]</c> and <c>[OneToMany]</c> only
/// adjust what the pass does. A foreign key is generated from the columns of the target's primary key, and a primary
/// key can itself be a reference, so expansion cascades: the work runs as a bounded fixpoint, and a model that leaves
/// work undone is circular and is reported.</remarks>
internal class ReferenceBuilder(BuilderContext context) {
  // Real chains are two or three deep; the stall check normally ends the loop long before this.
  private const int MaxRounds = 20;

  private sealed class PendingReference {
    public required EntityTypeInfo Entity;
    public required EntityMemberInfo Member;
    public required EntityTypeInfo Target;
    public required EntityKeyInfo ToKey;
    public EntityRefAttribute? Attribute;
    public bool Done;
  }

  public void BuildReferences() {
    var pending = CollectReferences();
    RunExpansionLoop(pending);
    BuildLists();
  }

  /// <summary>Collects every reference member, paired with the key it points at.</summary>
  private List<PendingReference> CollectReferences() {
    var pending = new List<PendingReference>();
    foreach (var entity in context.Model.Entities) {
      foreach (var member in entity.Members.ToList()) {
        if (member.Kind != MemberKind.Reference)
          continue;
        var location = $"{entity.Name}.{member.Name}";
        if (member.TargetEntity is not { } target)
          continue;

        var toKey = target.PrimaryKey;
        if (toKey == null) {
          context.AddError($"references '{target.Name}', which has no primary key.", location);
          continue;
        }
        pending.Add(new PendingReference {
          Entity = entity, Member = member, Target = target, ToKey = toKey,
          Attribute = FindAttribute<EntityRefAttribute>(entity, member.Name),
        });
      }
    }
    return pending;
  }

  private void RunExpansionLoop(List<PendingReference> pending) {
    for (var round = 0; round < MaxRounds; round++) {
      var progress = false;

      // A reference can be materialised as soon as the key it points at is made of columns.
      foreach (var reference in pending) {
        if (reference.Done || !reference.ToKey.IsExpanded)
          continue;
        MaterializeReference(reference);
        reference.Done = true;
        progress = true;
      }

      // And a key can be expanded as soon as every reference it names has been materialised.
      foreach (var entity in context.Model.Entities)
        foreach (var key in entity.Keys.ToList())
          if (TryExpand(entity, key))
            progress = true;

      if (pending.All(r => r.Done) && context.Model.Entities.All(e => e.Keys.All(k => k.IsExpanded)))
        return;
      if (!progress)
        break;
    }

    ReportStall(pending);
  }

  /// <summary>Expands a key once every member it names is a column or a reference whose own key is expanded.</summary>
  private bool TryExpand(EntityTypeInfo entity, EntityKeyInfo key) {
    if (key.IsExpanded)
      return false;

    var expanded = new List<KeyMember>();
    foreach (var km in key.Members) {
      switch (km.Member.Kind) {
        case MemberKind.Column:
          expanded.Add(km);
          break;
        case MemberKind.Reference:
          var fromKey = km.Member.ReferenceInfo?.FromKey;
          if (fromKey is not { IsExpanded: true })
            return false;                                 // not yet — another round may get there
          // The direction the developer wrote applies to every column the reference expands into.
          foreach (var fk in fromKey.MembersExpanded)
            expanded.Add(new KeyMember(fk.Member, km.Descending));
          break;
        default:
          context.AddError($"key '{key.Name}' names '{km.Member.Name}', which is not a column or a reference.",
            entity.Name);
          key.SetMembersExpanded(key.Members);            // stop the loop retrying a key that cannot work
          return true;
      }
    }

    key.SetMembersExpanded(expanded);

    // A primary key over a reference: the generated columns are the identity and carry the flag.
    if (key.IsPrimaryKey)
      foreach (var km in expanded)
        km.Member.MemberFlags |= MemberFlags.PrimaryKey;

    ExpandIncludedMembers(key);
    return true;
  }

  private static void ExpandIncludedMembers(EntityKeyInfo key) {
    if (key.Index == null || !key.Index.IncludedMembers.Any(m => m.Kind == MemberKind.Reference))
      return;
    var includes = new List<EntityMemberInfo>();
    foreach (var member in key.Index.IncludedMembers) {
      if (member.Kind != MemberKind.Reference)
        includes.Add(member);
      else if (member.ReferenceInfo != null)
        includes.AddRange(member.ReferenceInfo.FromKey.GetMembers());
    }
    key.Index.SetIncludedMembersExpanded(includes);
  }

  /// <summary>Determines whether two members have the same type with nullability set aside, since an optional
  /// reference makes its foreign key nullable while the target key column is not.</summary>
  private static bool SameUnderlyingType(EntityMemberInfo member, EntityMemberInfo targetMember) =>
    member.DataType == targetMember.DataType;

  private void MaterializeReference(PendingReference reference) {
    var (entity, member, target, toKey) = (reference.Entity, reference.Member, reference.Target, reference.ToKey);
    var location = $"{entity.Name}.{member.Name}";
    var attr = reference.Attribute;

    // Neither a view nor a temp table has a key the database enforces, so nothing can reference one.
    if (target.IsReadOnly) {
      context.AddError($"references '{target.Name}', which is {target.ReadOnlyKindName} and cannot be referenced.", location);
      return;
    }

    var targetColumns = toKey.MembersExpanded;
    var columnNames = SplitColumnNames(attr?.KeyColumns);
    if (columnNames != null && columnNames.Count != targetColumns.Count) {
      context.AddError(
        $"names {columnNames.Count} key column(s), but '{target.Name}' is keyed on {targetColumns.Count}.", location);
      return;
    }

    var fkMembers = new List<EntityMemberInfo>();
    for (var i = 0; i < targetColumns.Count; i++) {
      var targetMember = targetColumns[i].Member;
      var name = columnNames?[i]
        ?? context.App.Settings.NamingPolicy.GetForeignKeyMemberName(member.Name, targetMember.Name);

      // A member of that name may already be declared, to read the key without loading the target, or two references
      // may share one column; the existing member is taken in both cases.
      var existing = entity.FindMember(name);
      if (existing != null) {
        if (!SameUnderlyingType(existing, targetMember)) {
          context.AddError(
            $"would use the existing member '{name}' as its foreign key, but its type does not match "
            + $"'{target.Name}.{targetMember.Name}'.", location);
          return;
        }
        existing.OwnerReference ??= member;
        fkMembers.Add(existing);
        continue;
      }

      // The column takes the target column's type and the reference's nullability.
      var fk = new EntityMemberInfo(name, targetMember.DataType, MemberKind.Column, member.IsNullable) {
        Size = targetMember.Size,
        Precision = targetMember.Precision,
        Scale = targetMember.Scale,
        DbTypeSpec = targetMember.DbTypeSpec,
        OwnerReference = member,
        // Renaming the reference renames this column, so its old names follow, or the upgrade drops and adds it.
        OldNames = [.. member.OldNames.Select(old =>
          context.App.Settings.NamingPolicy.GetForeignKeyMemberName(old, targetMember.Name))],
      };
      entity.AddMember(fk);
      fkMembers.Add(fk);
    }

    // The from key's name is the constraint's name. Not indexed: a foreign key serves the lookup of the target by its
    // own key, and an index here would only help the reverse direction, which [Index] asks for where it matters.
    const KeyFlags keyFlags = KeyFlags.ForeignKey;
    var keyName = attr?.ForeignKeyName
      ?? context.App.Settings.NamingPolicy.GetKeyName(entity.Name, keyFlags, fkMembers.Select(m => m.Name));

    var fromKey = new EntityKeyInfo(keyName, entity, keyFlags) { OwnerMember = member };
    foreach (var fk in fkMembers)
      fromKey.AddMember(fk);
    fromKey.SetMembersExpanded(fromKey.Members);          // generated columns: nothing left to expand
    entity.AddKey(fromKey);

    // [CascadeDelete] is read here rather than applying itself, since the ReferenceInfo does not exist until now.
    member.ReferenceInfo = new ReferenceInfo(fromKey, toKey) {
      // A view enforces nothing and owns no rows of its own, so its references are plain columns.
      HasDbConstraint = !entity.IsReadOnly && (attr?.HasDbForeignKey ?? true),
      CascadeDelete = FindAttribute<CascadeDeleteAttribute>(entity, member.Name) != null,
    };
    if (!entity.IsReadOnly)
      toKey.Entity.AddIncomingReference(member);
  }

  /// <summary>Reports every reference and key left unresolved by a circular model, so the report names the whole
  /// ring.</summary>
  private void ReportStall(List<PendingReference> pending) {
    foreach (var reference in pending.Where(r => !r.Done))
      context.AddError(
        $"cannot be resolved: '{reference.Target.Name}' is keyed on something that depends on it in turn.",
        $"{reference.Entity.Name}.{reference.Member.Name}");

    foreach (var entity in context.Model.Entities)
      foreach (var key in entity.Keys.Where(k => !k.IsExpanded))
        context.AddError($"key '{key.Name}' names a reference that cannot be resolved.", entity.Name);
  }

  private void BuildLists() {
    foreach (var entity in context.Model.Entities.ToList())
      foreach (var member in entity.Members.ToList())
        if (member.Kind == MemberKind.List)
          BuildList(entity, member);
  }

  private void BuildList(EntityTypeInfo entity, EntityMemberInfo member) {
    var location = $"{entity.Name}.{member.Name}";
    if (member.TargetEntity is not { } target)
      return;

    var manyToMany = FindAttribute<ManyToManyAttribute>(entity, member.Name);
    if (manyToMany != null) {
      BuildManyToMany(entity, member, target, manyToMany, location);
      return;
    }

    var info = new ListInfo(target);
    member.ListInfo = info;
    AssignListOrder(entity, member, info, location);

    var attr = FindAttribute<OneToManyAttribute>(entity, member.Name);
    var candidates = target.Members
      .Where(m => m.Kind == MemberKind.Reference && ReferenceEquals(m.ReferenceInfo?.ToKey.Entity, entity))
      .ToList();

    if (attr?.ThisEntityRef != null) {
      var named = candidates.FirstOrDefault(m => m.Name == attr.ThisEntityRef);
      if (named == null) {
        context.AddError(
          $"names '{attr.ThisEntityRef}' as the reference back, but '{target.Name}' has no such reference to '{entity.Name}'.",
          location);
        return;
      }
      info.InverseMember = named;
      return;
    }

    switch (candidates.Count) {
      case 0:
        context.AddError($"is a list of '{target.Name}', which has no reference back to '{entity.Name}'.", location);
        return;
      case 1:
        info.InverseMember = candidates[0];
        return;
      default:
        context.AddError(
          $"is a list of '{target.Name}', which references '{entity.Name}' more than once "
          + $"({string.Join(", ", candidates.Select(c => c.Name))}) — say which with [OneToMany(\"…\")].",
          location);
        return;
    }
  }

  /// <summary>Decides the order a list loads in, so that its select is never unordered: <c>[OrderBy]</c> on the list
  /// member, else <c>[OrderBy]</c> on the target entity, else the <c>[PersistOrderIn]</c> member, else the target's
  /// primary key.</summary>
  /// <remarks>The persisted-order member is recorded whether or not it orders the list, since the save renumbers it
  /// either way. A name is looked up on the target first and then on the link.</remarks>
  private void AssignListOrder(EntityTypeInfo entity, EntityMemberInfo member, ListInfo info, string location) {
    var target = info.TargetEntity;

    // A query is ordered where it is written, so an attribute that would order it is refused.
    if (member.IsQueryable) {
      if (FindAttribute<OrderByAttribute>(entity, member.Name) != null || FindAttribute<PersistOrderInAttribute>(entity, member.Name) != null)
        context.AddError("is a query, which is ordered where it is written; [OrderBy] and [PersistOrderIn] do not apply to it.", location);
      return;
    }

    if (FindAttribute<PersistOrderInAttribute>(entity, member.Name) is { } persist) {
      var orderMember = FindListMember(info, persist.Property);
      if (orderMember == null)
        context.AddError($"persists its order in '{persist.Property}', which neither '{target.Name}' nor its link has.",
          location);
      else if (!ReflectionHelper.IsIntegerType(orderMember.DataType))
        context.AddError($"persists its order in '{orderMember.Name}', which is not an integer member.", location);
      else
        info.PersistOrderMember = orderMember;
    }

    var ownSpec = FindAttribute<OrderByAttribute>(entity, member.Name)?.OrderByList;
    var targetSpec = target.InterfaceType?.GetCustomAttribute<OrderByAttribute>()?.OrderByList;
    var spec = ownSpec ?? targetSpec;
    if (spec != null) {
      var parsed = MemberListHelper.Parse(spec, $"OrderBy on '{(ownSpec != null ? location : target.Name)}'");
      context.Absorb(parsed, location);
      if (parsed.HasErrors)
        return;
      var order = new List<KeyMember>();
      foreach (var reference in parsed.Members) {
        var listMember = FindListMember(info, reference.Name);
        if (listMember == null)
          context.AddError($"orders by '{reference.Name}', which neither '{target.Name}' nor its link has.", location);
        else
          order.Add(new KeyMember(listMember, reference.Descending));
      }
      info.OrderBy = order;
      return;
    }

    if (info.PersistOrderMember is { } position)
      info.OrderBy = [new KeyMember(position)];
    else if (target.PrimaryKey is { } key)
      info.OrderBy = key.Members;
    // A keyless target is the validator's to report; an empty order here is the consequence, not a second fault.
  }

  private static EntityMemberInfo? FindListMember(ListInfo info, string name) =>
    info.TargetEntity.FindMember(name) ?? info.LinkEntity?.FindMember(name);

  /// <summary>Builds a many-to-many list by finding the two references on the link entity: one back to this entity,
  /// one out to the target.</summary>
  private void BuildManyToMany(EntityTypeInfo entity, EntityMemberInfo member,
      EntityTypeInfo target, ManyToManyAttribute attr, string location) {
    if (!context.EntitiesByType.TryGetValue(attr.LinkEntity, out var link)) {
      context.AddError(
        $"names link entity '{attr.LinkEntity.Name}', which is not a registered entity — is its module missing it?",
        location);
      return;
    }

    var info = new ListInfo(target) { Kind = ListKind.ManyToMany, LinkEntity = link };
    member.ListInfo = info;

    // The reference back is resolved first, so that the other leg can exclude it when both ends are one entity.
    info.InverseMember = FindLinkReference(link, entity, attr.ThisEntityRef, nameof(attr.ThisEntityRef),
      location, exclude: null);
    if (info.InverseMember == null)
      return;

    info.OtherMember = FindLinkReference(link, target, attr.OtherEntityRef, nameof(attr.OtherEntityRef),
      location, exclude: info.InverseMember);
    AssignListOrder(entity, member, info, location);
  }

  private EntityMemberInfo? FindLinkReference(EntityTypeInfo link,
      EntityTypeInfo wanted, string? namedMember, string attributeProperty, string location,
      EntityMemberInfo? exclude) {
    var candidates = link.Members
      .Where(m => m.Kind == MemberKind.Reference
        && ReferenceEquals(m.ReferenceInfo?.ToKey.Entity, wanted)
        && !ReferenceEquals(m, exclude))
      .ToList();

    if (namedMember != null) {
      var named = candidates.FirstOrDefault(m => m.Name == namedMember);
      if (named == null)
        context.AddError(
          $"names '{namedMember}' on link entity '{link.Name}', which has no such reference to '{wanted.Name}'.",
          location);
      return named;
    }

    switch (candidates.Count) {
      case 1:
        return candidates[0];
      case 0:
        context.AddError($"link entity '{link.Name}' has no reference to '{wanted.Name}'.", location);
        return null;
      default:
        context.AddError(
          $"link entity '{link.Name}' references '{wanted.Name}' more than once "
          + $"({string.Join(", ", candidates.Select(c => c.Name))}) — say which with [ManyToMany(…, {attributeProperty} = \"…\")].",
          location);
        return null;
    }
  }

  private static List<string>? SplitColumnNames(string? spec) =>
    string.IsNullOrWhiteSpace(spec)
      ? null
      : spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

  private static T? FindAttribute<T>(EntityTypeInfo entity, string memberName) where T : Attribute {
    var type = entity.InterfaceType;
    var property = type?.GetProperty(memberName)
      ?? type?.GetInterfaces().Select(i => i.GetProperty(memberName)).FirstOrDefault(p => p != null);
    return property?.GetCustomAttribute<T>();
  }
}
