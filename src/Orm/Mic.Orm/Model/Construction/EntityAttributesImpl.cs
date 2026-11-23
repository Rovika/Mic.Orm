using System.Reflection;
using System.Text.Json.Serialization;
using Mic.Orm.Db;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

// The behaviour half of the attributes declared in EntityAttributes.cs; the namespace is the same, the folder is not.
namespace Mic.Orm;

public partial class EntityAttribute {
  // Name is not applied here: an entity's name is fixed by its constructor, and the builder reads it directly.
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Early)
      return;
    entity.TableName = TableName;
    entity.PluralName = PluralName;
  }
}

public partial class ViewAttribute {
  // The module's registration, applied after the attributes, replaces this with the view's body where it gives one
  // and keeps the name and the option.
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Early)
      return;
    entity.View = new ViewInfo(entity, ViewDefinitionKind.Existing) { IsMaterialized = Materialized };
    if (Name != null)
      entity.TableName = Name;
  }
}

public partial class OldNamesAttribute {
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Early)
      return;

    var parsed = MemberListHelper.Parse(OldNames, $"OldNames on '{entity.Name}'", allowDirection: false);
    context.Absorb(parsed, entity.Name);
    if (parsed.HasErrors)
      return;

    entity.OldNames = parsed.Members.Select(m => m.Name).ToList();
  }

  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Early)
      return;

    var location = $"{member.Entity?.Name}.{member.Name}";
    var parsed = MemberListHelper.Parse(OldNames, $"OldNames on '{location}'", allowDirection: false);
    context.Absorb(parsed, location);
    if (!parsed.HasErrors)
      member.OldNames = parsed.Members.Select(m => m.Name).ToList();
  }
}

public partial class AsIsAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Middle)
      member.MemberFlags |= MemberFlags.AsIs;
  }
}

public abstract partial class KeyAttribute {
  /// <summary>Builds a key from a resolved member list, for an entity-level or a member-level key attribute.</summary>
  private protected void BuildKey(BuilderContext context, EntityTypeInfo entity, KeyFlags flags,
      IEnumerable<KeyMember>? members, IndexOptions? index, string location, EntityMemberInfo? ownerMember = null) {
    var list = members?.ToList() ?? [];
    if (list.Count == 0)
      return;                                             // whatever went wrong has already been reported

    if (flags.HasFlag(KeyFlags.PrimaryKey)) {
      if (entity.PrimaryKey != null) {
        context.AddError("has more than one primary key.", entity.Name);
        return;
      }
      foreach (var km in list)
        km.Member.MemberFlags |= MemberFlags.PrimaryKey;
    }

    // The same key stated twice is an error. Compared on flags and members, so two different indexes over the same
    // column, one filtered say, are still allowed.
    var duplicate = entity.Keys.FirstOrDefault(k => k.Flags == flags && SameMembers(k, list));
    if (duplicate != null) {
      context.AddError($"declares the same key twice ('{duplicate.Name}').", location);
      return;
    }

    var name = Name ?? context.App.Settings.NamingPolicy.GetKeyName(entity.Name, flags, list.Select(m => m.Member.Name));
    var key = new EntityKeyInfo(name, entity, flags) { Index = index, OwnerMember = ownerMember };
    foreach (var km in list)
      key.AddMember(km.Member, km.Descending);
    entity.AddKey(key);
  }

  private static bool SameMembers(EntityKeyInfo key, List<KeyMember> members) =>
    key.Members.Count == members.Count
    && key.Members.Select(m => m.Member.Name).SequenceEqual(members.Select(m => m.Member.Name));

  /// <summary>Resolves a member list written as a string, reporting an unknown name.</summary>
  private protected static List<KeyMember> ResolveMembers(BuilderContext context, EntityTypeInfo entity, string spec,
      string location, bool allowDirection = true) {
    var parsed = MemberListHelper.Parse(spec, location, allowDirection);
    context.Absorb(parsed, entity.Name);
    if (parsed.HasErrors)
      return [];

    var result = new List<KeyMember>();
    foreach (var reference in parsed.Members) {
      var member = entity.FindMember(reference.Name);
      if (member == null) {
        context.AddError($"names member '{reference.Name}', which '{entity.Name}' does not have.", location);
        continue;
      }
      result.Add(new KeyMember(member, reference.Descending));
    }
    return result;
  }
}

public partial class PrimaryKeyAttribute {
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = $"[PrimaryKey] on '{entity.Name}'";
    if (MembersSpec == null) {
      context.AddError("on an entity must name its members, e.g. [PrimaryKey(\"TripId,LegNo\")].", location);
      return;
    }
    BuildKey(context, entity, GetKeyFlags(), ResolveMembers(context, entity, MembersSpec, location), null, location);
  }

  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var entity = member.Entity!;
    var location = $"[PrimaryKey] on '{entity.Name}.{member.Name}'";
    if (MembersSpec != null) {
      context.AddError("on a member cannot also name members — put it on the entity instead.", location);
      return;
    }
    BuildKey(context, entity, GetKeyFlags(), [new KeyMember(member)], null, location, member);
  }

  private KeyFlags GetKeyFlags() => KeyFlags.PrimaryKey | (Clustered ? KeyFlags.Clustered : KeyFlags.None);
}

public partial class IndexAttribute {
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = $"[{GetType().Name}] on '{entity.Name}'";
    if (MembersSpec == null) {
      context.AddError("on an entity must name its members.", location);
      return;
    }
    BuildKey(context, entity, GetKeyFlags(), ResolveMembers(context, entity, MembersSpec, location),
      BuildOptions(context, entity, location), location);
  }

  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var entity = member.Entity!;
    var location = $"[{GetType().Name}] on '{entity.Name}.{member.Name}'";
    if (MembersSpec != null) {
      context.AddError("on a member cannot also name members — put it on the entity instead.", location);
      return;
    }
    BuildKey(context, entity, GetKeyFlags(), [new KeyMember(member)], BuildOptions(context, entity, location), location, member);
  }

  private KeyFlags GetKeyFlags() =>
    KeyFlags.Index
    | (Unique ? KeyFlags.Unique : KeyFlags.None)
    | (Clustered ? KeyFlags.Clustered : KeyFlags.None);

  /// <summary>Builds the index-only options, or <see langword="null"/> when nothing asks for them.</summary>
  private IndexOptions? BuildOptions(BuilderContext context, EntityTypeInfo entity, string location) {
    if (IncludeMembers == null && Filter == null && IndexType == DbIndexType.Default)
      return null;
    var options = new IndexOptions { Filter = Filter, IndexType = IndexType };
    if (IncludeMembers != null)
      // An included member is carried in the leaf, not ordered by, so a direction is reported.
      foreach (var km in ResolveMembers(context, entity, IncludeMembers, location + ", IncludeMembers", allowDirection: false))
        options.AddIncludedMember(km.Member);
    return options;
  }
}

public partial class CheckAttribute {
  internal override void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = $"[Check] on '{entity.Name}'";
    if (string.IsNullOrEmpty(Name) || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) {
      context.AddError($"is named '{Name}'; a check's name is letters, digits and underscores.", location);
      return;
    }
    if (string.IsNullOrWhiteSpace(Expression)) {
      context.AddError($"'{Name}' has no condition.", location);
      return;
    }
    if (entity.Checks.Any(c => c.ServerKind == ServerKind
        && string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase))) {
      context.AddError($"declares the check '{Name}' twice.", location);
      return;
    }
    entity.AddCheck(new EntityCheckInfo(Name, Expression, ServerKind));
  }
}

public partial class NullableAttribute {
  // Overrides what the CLR type said: explicit beats inferred.
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Early)
      member.IsNullable = true;
  }
}

public partial class SizeAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    // Both [Size] and [Column(Size = …)] write here, so whichever runs second reports the duplicate, whatever order
    // reflection hands the attributes back in.
    if (member.Size != 0) {
      context.AddError("size is stated twice — use either [Size] or [Column(Size = …)], not both.", GetLocation(member));
      return;
    }
    member.Size = Size;

    if (Options.HasFlag(SizeOptions.AutoTrim))
      member.MemberFlags |= MemberFlags.AutoTrim;
  }

  internal static string GetLocation(EntityMemberInfo member) => $"{member.Entity?.Name}.{member.Name}";
}

public partial class ColumnAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    if (Size != 0) {
      if (member.Size != 0) {
        context.AddError("size is stated twice — use either [Size] or [Column(Size = …)], not both.",
          SizeAttribute.GetLocation(member));
        return;
      }
      member.Size = Size;
    }
    if (Precision != 0)
      member.Precision = Precision;
    if (Scale != 0)
      member.Scale = Scale;
    if (DbTypeSpec != null)
      member.DbTypeSpec = DbTypeSpec;
    if (ColumnName != null)
      member.ColumnName = ColumnName;
    if (Default != null)
      member.DefaultExpression = Default;
  }
}

public partial class UnlimitedAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Middle)
      member.MemberFlags |= MemberFlags.Unlimited;
  }
}

public partial class OnDemandAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Middle)
      member.MemberFlags |= MemberFlags.OnDemand;
  }
}

public partial class HashForAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    // Late, so the member it names has had its own attributes applied.
    if (context.Phase != AttributePhase.Late)
      return;
    var location = SizeAttribute.GetLocation(member);
    var hashType = Nullable.GetUnderlyingType(member.DataType) ?? member.DataType;
    if (hashType != typeof(int) && hashType != typeof(long)) {
      context.AddError(
        $"[HashFor] is on a member of type '{member.DataType.Name}'; a hash is an int or a long.", location);
      return;
    }
    var hashed = member.Entity?.FindMember(PropertyName);
    if (hashed == null || hashed.Kind != MemberKind.Column || hashed.DataType != typeof(string) || hashed.IsJson
        || hashed.Computed != null) {
      context.AddError($"[HashFor(\"{PropertyName}\")] names no text member of '{member.Entity?.Name}'.", location);
      return;
    }
    if (hashed.HashMember != null) {
      context.AddError(
        $"[HashFor(\"{PropertyName}\")]: '{hashed.Name}' is hashed into '{hashed.HashMember.Name}' already.", location);
      return;
    }
    hashed.HashMember = member;
  }
}

public partial class VectorAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = SizeAttribute.GetLocation(member);
    if (member.DataType != typeof(float[])) {
      context.AddError($"[Vector] is on a member of type '{member.DataType.Name}'; a vector is a float[].", location);
      return;
    }
    if (member.Property?.GetCustomAttribute<JsonAttribute>() != null) {
      context.AddError("has [Vector] and [Json]; a member is a vector or a JSON document, not both.", location);
      return;
    }
    if (Dimensions <= 0) {
      context.AddError($"[Vector({Dimensions})] states no dimensions; the number is one or more.", location);
      return;
    }
    if (member.Size != 0) {
      context.AddError("has a size beside [Vector], which states the member's dimensions itself.", location);
      return;
    }
    member.MemberFlags |= MemberFlags.Vector;
    member.Size = Dimensions;
    member.VectorMetric = Metric;
  }
}

public partial class JsonAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Middle)
      member.MemberFlags |= MemberFlags.Json;
    // Late, so the member it names has had its own attributes applied.
    if (context.Phase != AttributePhase.Late || Discriminator == null)
      return;
    var location = SizeAttribute.GetLocation(member);
    var discriminator = member.Entity?.FindMember(Discriminator);
    if (discriminator == null || discriminator.Kind != MemberKind.Column || discriminator.IsJson) {
      context.AddError($"[Json(Discriminator = \"{Discriminator}\")] names no column member of '{member.Entity?.Name}'.", location);
      return;
    }
    // A discriminator the database reads out of the document itself: the serializer writes it under its own name,
    // the column exposes it, and the document is written and read as its declared type, so no type map is needed.
    if (discriminator.FromJson is { } fromJson) {
      CheckDiscriminatorPath(context, member, discriminator, fromJson, location);
      return;
    }
    var declared = context.Model.FindJsonTypes(member.DataType);
    if (declared == null) {
      context.AddError($"[Json(Discriminator = …)] needs the types of '{member.DataType.Name}' mapped on a module with "
        + "MapJsonTypes(baseType, (discriminator, type)…), and none are.", location);
      return;
    }
    try {
      member.JsonTypes = declared.ConvertTo(discriminator.DataType);
    } catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException) {
      context.AddError($"a discriminator of '{member.DataType.Name}' is not a '{discriminator.DataType.Name}', which "
        + $"'{discriminator.Name}' holds: {ex.Message}", location);
      return;
    }
    member.JsonDiscriminatorMember = discriminator;
  }

  /// <summary>Reports a computed discriminator that reads another document, or a path other than the property the
  /// serializer writes the discriminator under.</summary>
  private static void CheckDiscriminatorPath(BuilderContext context, EntityMemberInfo member,
      EntityMemberInfo discriminator, FromJsonInfo fromJson, string location) {
    if (!ReferenceEquals(fromJson.JsonMember, member)) {
      context.AddError($"[Json(Discriminator = \"{discriminator.Name}\")] names a member computed from "
        + $"'{fromJson.JsonMember.Name}', not from this document.", location);
      return;
    }
    var property = member.DataType.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName ?? "$type";
    if (fromJson.Path != $"$.{property}" && fromJson.Path != $"$.\"{property}\"")
      context.AddError($"'{discriminator.Name}' reads '{fromJson.Path}', and the serializer writes the discriminator "
        + $"of '{member.DataType.Name}' as '{property}' — the path must be '$.{property}'.", location);
  }
}

public partial class FromJsonAttribute {
  // Recorded in the middle phase, so that the document's [Json(Discriminator = …)], which runs in the late one, finds
  // it whatever the order of the members; checked in the late phase, once the document has its own flag.
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    var location = SizeAttribute.GetLocation(member);
    if (context.Phase == AttributePhase.Middle) {
      member.MemberFlags |= MemberFlags.DbGenerated;
      if (member.Kind != MemberKind.Column) {
        context.AddError("[FromJson] is on a member that is not a plain column.", location);
        return;
      }
      var document = member.Entity?.FindMember(JsonMember);
      if (document == null) {
        context.AddError($"[FromJson(\"{JsonMember}\", …)] names no JSON member of '{member.Entity?.Name}'.", location);
        return;
      }
      if (string.IsNullOrWhiteSpace(Path) || !Path.StartsWith('$')) {
        context.AddError($"[FromJson] path '{Path}' does not start at the document's root, '$'.", location);
        return;
      }
      member.FromJson = new FromJsonInfo(document, Path, Stored);
    } else if (context.Phase == AttributePhase.Late && member.FromJson is { } fromJson) {
      if (!fromJson.JsonMember.IsJson || member.IsJson)
        context.AddError($"[FromJson(\"{JsonMember}\", …)] names no JSON member of '{member.Entity?.Name}'.", location);
    }
  }
}

public partial class NoUpdateAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Middle)
      member.MemberFlags |= MemberFlags.NoUpdate;
  }
}

public partial class AutoAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    member.AutoValue = Type;
    // A row version is counted by the ORM and a sequence value is fetched by the save before the insert, so neither
    // is DbGenerated: the insert writes both.
    if (Type == AutoType.Identity)
      member.MemberFlags |= MemberFlags.DbGenerated;
    if (Type is AutoType.RowVersion or AutoType.Sequence && member.DataType != typeof(int) && member.DataType != typeof(long))
      context.AddError($"[{Type}] is an int or a long.", SizeAttribute.GetLocation(member));
    if (Type == AutoType.Sequence) {
      if (string.IsNullOrWhiteSpace(SequenceName))
        context.AddError("[Sequence] needs a sequence name.", SizeAttribute.GetLocation(member));
      member.SequenceName = SequenceName;
    }

    // The client-produced values are checked against the member's type here rather than failing at the first save.
    var expected = Type switch {
      AutoType.NewGuid => typeof(Guid),
      AutoType.CreatedOn or AutoType.UpdatedOn => typeof(DateTime),
      AutoType.CreatedBy or AutoType.UpdatedBy => typeof(string),
      AutoType.CreatedById or AutoType.UpdatedById => typeof(Guid),
      _ => null,
    };
    if (expected == null)
      return;
    var actual = member.DataType;
    if (actual != expected && Nullable.GetUnderlyingType(actual ?? typeof(object)) != expected)
      context.AddError($"[Auto({Type})] is on '{member.Name}', which is {actual?.Name ?? "not a simple type"} rather "
        + $"than {expected.Name}.", SizeAttribute.GetLocation(member));
  }
}

public partial class ComputedAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = SizeAttribute.GetLocation(member);
    if (member.Property?.CanWrite == true)
      context.AddError("[Computed] is on a member with a setter; a computed member is read-only.", location);
    var method = FindMethod(context, member, location);
    if (method != null)
      member.Computed = new ComputedInfo(method, Stored);
  }

  /// <summary>Finds the static method the attribute names, on the class it names or else on the entity's module,
  /// taking the entity and returning the member's type.</summary>
  private MethodInfo? FindMethod(BuilderContext context, EntityMemberInfo member, string location) {
    var entityType = member.Entity?.InterfaceType;
    var container = MethodClass ?? member.Entity?.Module.GetType();
    if (entityType == null || container == null) {
      context.AddError("[Computed] is on a member of an entity with no interface or no module.", location);
      return null;
    }
    var memberType = member.Property?.PropertyType ?? member.DataType;
    var candidates = container.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      .Where(m => m.Name == MethodName).ToList();
    if (candidates.Count == 0) {
      context.AddError($"[Computed] names '{MethodName}', which '{container.Name}' has no static method of.", location);
      return null;
    }
    var method = candidates.FirstOrDefault(m => m.GetParameters() is [var parameter]
      && parameter.ParameterType.IsAssignableFrom(entityType) && memberType.IsAssignableFrom(m.ReturnType));
    if (method == null)
      context.AddError($"'{container.Name}.{MethodName}' must take the entity, {entityType.Name}, and return "
        + $"{memberType.Name}, the member's type.", location);
    return method;
  }
}

public partial class DbComputedAttribute {
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase != AttributePhase.Middle)
      return;
    var location = SizeAttribute.GetLocation(member);
    member.MemberFlags |= MemberFlags.DbGenerated;
    if (member.Kind != MemberKind.Column || member.IsJson) {
      context.AddError("[DbComputed] is on a member that is not a plain column.", location);
      return;
    }
    var byServer = new Dictionary<DbServerKind, string>();
    foreach (var expression in member.Property?.GetCustomAttributes<SqlExpressionAttribute>() ?? []) {
      if (!byServer.TryAdd(expression.ServerKind, expression.Expression))
        context.AddError($"has two [SqlExpression] for {expression.ServerKind}.", location);
    }
    if (byServer.Count == 0) {
      context.AddError("[DbComputed] needs the expression written with [SqlExpression(server, text)], one per server.", location);
      return;
    }
    member.DbComputed = new DbComputedInfo(byServer, Stored);
  }
}

public partial class SqlExpressionAttribute {
  // The expressions are read off the member by [DbComputed]; one on a member without it is a mistake.
  internal override void ApplyToMember(BuilderContext context, EntityMemberInfo member) {
    if (context.Phase == AttributePhase.Late && member.DbComputed == null)
      context.AddError("[SqlExpression] is on a member that is not [DbComputed].", SizeAttribute.GetLocation(member));
  }
}
