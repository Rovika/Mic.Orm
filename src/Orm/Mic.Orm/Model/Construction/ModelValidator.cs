
namespace Mic.Orm.Model.Construction;

/// <summary>Runs the checks that need the whole model to exist: that every entity has members, that every entity
/// stored in a table has a primary key, that key names do not collide, that an on-demand member is one that can be
/// left out of a read, and that every required module is present.</summary>
/// <remarks>Only faults that would otherwise surface as wrong SQL or wrong data are checked; a model that could have
/// been written better is left as written. Everything is reported, never thrown.</remarks>
internal class ModelValidator(BuilderContext context) {
  public void Validate() {
    foreach (var entity in context.Model.Entities) {
      CheckHasMembers(entity);
      CheckPrimaryKey(entity);
      CheckKeyNames(entity);
      CheckView(entity);
      CheckOnDemandMembers(entity);
    }
    CheckModuleDependencies();
  }

  /// <summary>Reports what a view or a temp table cannot have: a list, an auto value, or an index, since its rows
  /// are read-only; a temp table has no references either, since its rows are filled as plain values.</summary>
  private void CheckView(EntityTypeInfo entity) {
    if (!entity.IsReadOnly)
      return;
    var what = entity.ReadOnlyKindName;
    foreach (var member in entity.Members) {
      var location = $"{entity.Name}.{member.Name}";
      if (member.Kind == MemberKind.List)
        context.AddError($"is a list on {what}; its rows are read-only and own no lists.", location);
      if (member.AutoValue != AutoType.None)
        context.AddError($"has an auto value on {what}; nothing of the session's makes its rows.", location);
      if (member.Kind == MemberKind.Reference && entity.IsTempTable)
        context.AddError("is a reference on a temp table, whose rows are filled as plain values; give it the key member instead.", location);
    }
    foreach (var key in entity.Keys)
      if (!key.IsPrimaryKey && !key.IsForeignKey)
        context.AddError($"has an index or unique key '{key.Name}' on {what}, which has no indexes.", entity.Name);
  }

  /// <summary>Reports an <c>[OnDemand]</c> member that cannot be left out of a read: one with no column, one the
  /// session identifies or matches a row by, and one of an entity whose rows have no key to load it by.</summary>
  private void CheckOnDemandMembers(EntityTypeInfo entity) {
    foreach (var member in entity.Members) {
      if (!member.IsOnDemand)
        continue;
      var location = $"{entity.Name}.{member.Name}";
      if (member.Kind != MemberKind.Column || member.Computed != null)
        context.AddError("has [OnDemand] and is not a column member; a reference, a list, a computed member and a "
          + "member with no column have nothing to leave out of a read.", location);
      else if (member.IsPrimaryKey)
        context.AddError("has [OnDemand] and is part of the primary key, which every read brings.", location);
      else if (member.OwnerReference != null)
        context.AddError("has [OnDemand] and is a foreign key member; a reference is resolved from it.", location);
      else if (member.AutoValue == AutoType.RowVersion)
        context.AddError("has [OnDemand] and is the row version, which every update matches on.", location);
      else if (entity.Keys.Any(k => k.IsUnique && !k.IsPrimaryKey && k.MembersExpanded.Any(m => m.Member == member)))
        context.AddError("has [OnDemand] and is part of a unique key, whose values a save reads.", location);
      else if (entity.PrimaryKey == null)
        context.AddError("has [OnDemand] on an entity with no primary key, so there is no key to load it by.",
          location);
    }
  }

  private void CheckHasMembers(EntityTypeInfo entity) {
    if (entity.Members.Count == 0)
      context.AddError("has no members.", entity.Name);
  }

  private void CheckPrimaryKey(EntityTypeInfo entity) {
    var key = entity.PrimaryKey;
    if (key == null) {
      // A view's rows and a function's may have no identity at all, and a temp table's are plain values; without a
      // key they are read and not tracked.
      if (!entity.IsReadOnly)
        context.AddError("has no primary key — declare one with [PrimaryKey].", entity.Name);
      return;
    }

    // A nullable primary-key column cannot identify a row, since NULL is not equal to itself.
    foreach (var km in key.MembersExpanded)
      if (km.Member.IsNullable)
        context.AddError($"is part of the primary key and cannot be nullable.", $"{entity.Name}.{km.Member.Name}");
  }

  /// <summary>Reports two keys of one entity that share a name, which a replaced naming policy can produce.</summary>
  private void CheckKeyNames(EntityTypeInfo entity) {
    var duplicate = entity.Keys
      .GroupBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault(g => g.Count() > 1);
    if (duplicate != null)
      context.AddError($"has {duplicate.Count()} keys named '{duplicate.Key}'.", entity.Name);
  }

  /// <summary>Verifies the dependencies the modules recorded with <c>Requires</c>.</summary>
  private void CheckModuleDependencies() {
    var modules = context.App.Modules;
    foreach (var module in modules) {
      foreach (var required in module.Dependencies) {
        if (modules.Any(m => required.IsInstanceOfType(m)))
          continue;
        context.AddError(
          $"requires module '{required.Name}', which this application does not have.", $"module '{module.Name}'");
      }
    }
  }
}
