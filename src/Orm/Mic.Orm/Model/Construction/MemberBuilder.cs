using System.Reflection;
using Mic.Common;

namespace Mic.Orm.Model.Construction;

/// <summary>Runs the member pass of the model builder, turning an entity's interface properties into members, and
/// applies member attributes and defaults.</summary>
/// <remarks>The pass settles what a member is: its CLR type, its kind and its nullability. Inherited members are
/// flattened in, so entities are built independently of each other and in any order.</remarks>
internal class MemberBuilder(BuilderContext context) {
  // Scalars the ORM stores in a column, beyond primitives and enums; anything else that is not an entity or a list of
  // entities is a reported error.
  private static readonly HashSet<Type> ScalarTypes = [
    typeof(string), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly),
    typeof(TimeSpan), typeof(decimal), typeof(byte[]), typeof(GeoPoint),
  ];

  public void BuildMembers() {
    foreach (var entity in context.Model.Entities) {
      if (entity.InterfaceType == null)
        continue;
      foreach (var property in CollectProperties(entity))
        BuildMember(entity, property);
    }
  }

  /// <summary>Collects every property of the interface and of every interface it inherits, flattened into this one
  /// entity.</summary>
  /// <remarks>Inheritance among entity interfaces is member inheritance, never entity-hierarchy mapping: a base
  /// interface registered as an entity of its own is simply another table.</remarks>
  private List<PropertyInfo> CollectProperties(EntityTypeInfo entity) {
    var type = entity.InterfaceType!;
    var properties = new List<PropertyInfo>(type.GetProperties());
    foreach (var iface in type.GetInterfaces())
      properties.AddRange(iface.GetProperties());

    // A property reached by several paths of a diamond lands once; two bases declaring one name with different types
    // is a collision.
    var result = new List<PropertyInfo>();
    foreach (var group in properties.GroupBy(p => p.Name)) {
      var distinctTypes = group.Select(p => p.PropertyType).Distinct().ToList();
      if (distinctTypes.Count > 1) {
        context.AddError(
          $"is inherited more than once with different types ({string.Join(", ", distinctTypes.Select(t => t.Name))}).",
          $"{entity.Name}.{group.Key}");
        continue;
      }
      result.Add(group.First());
    }
    return result;
  }

  private void BuildMember(EntityTypeInfo entity, PropertyInfo property) {
    var location = $"{entity.Name}.{property.Name}";
    if (entity.FindMember(property.Name) != null) {
      context.AddError("declared more than once.", location);
      return;
    }

    var (kind, target, ok) = Classify(property, location);
    if (!ok)
      return;                                             // Classify has already reported why

    var member = new EntityMemberInfo(property.Name, property.PropertyType, kind, IsNullable(property)) {
      TargetEntity = target,
      Property = property,
    };
    if (kind == MemberKind.Column && property.GetCustomAttribute<JsonAttribute>() == null
        && property.GetCustomAttribute<VectorAttribute>() == null)
      member.ArrayItemType = GetArrayItemType(property.PropertyType);
    entity.AddMember(member);
  }

  /// <summary>Classifies a property as a column, a reference, a list or a transient member, reporting one the ORM
  /// cannot map.</summary>
  private (MemberKind Kind, EntityTypeInfo? Target, bool Ok) Classify(
      PropertyInfo property, string location) {
    var type = property.PropertyType;

    // [NoColumn] wins over everything, whatever the type.
    if (property.GetCustomAttribute<NoColumnAttribute>() != null)
      return (MemberKind.Transient, null, true);

    // A computed member has no column either, unless it stores its value: it is a method's, computed on each read.
    if (property.GetCustomAttribute<ComputedAttribute>() is { Stored: false })
      return (MemberKind.Transient, null, true);

    if (context.EntitiesByType.TryGetValue(type, out var target))
      return (MemberKind.Reference, target, true);

    var itemType = GetListItemType(type);
    if (itemType != null) {
      if (context.EntitiesByType.TryGetValue(itemType, out var item))
        return (MemberKind.List, item, true);
      context.AddError($"is a list of '{itemType.Name}', which is not an entity.", location);
      return (MemberKind.List, null, false);
    }

    var underlying = Nullable.GetUnderlyingType(type) ?? type;
    if (underlying.IsPrimitive || underlying.IsEnum || ScalarTypes.Contains(underlying))
      return (MemberKind.Column, null, true);

    // A JSON column holds an object of any type the serializer can write.
    if (property.GetCustomAttribute<JsonAttribute>() != null)
      return (MemberKind.Column, null, true);

    // An array or a List of scalars is an array column, on a server that has array types.
    if (GetArrayItemType(type) != null)
      return (MemberKind.Column, null, true);

    if (type.IsInterface)
      context.AddError($"is of type '{type.Name}', which is not a registered entity — is its module missing it?", location);
    else
      context.AddError($"is of type '{type.Name}', which the ORM cannot map.", location);
    return (MemberKind.Column, null, false);
  }

  /// <summary>Gets the item type of an array or a <see cref="List{T}"/> whose items are scalars.</summary>
  /// <returns>The item type, or <see langword="null"/> for any other type, a byte array included: that one is a
  /// binary value.</returns>
  private static Type? GetArrayItemType(Type type) {
    var item = type.IsArray && type.GetArrayRank() == 1 ? type.GetElementType()
      : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
      : null;
    if (item == null || type == typeof(byte[]) || item.IsEnum)
      return null;
    return item.IsPrimitive || ScalarTypes.Contains(item) ? item : null;
  }

  private static Type? GetListItemType(Type type) {
    if (type == typeof(string) || !type.IsGenericType)
      return null;
    var definition = type.GetGenericTypeDefinition();
    // IQueryable<> is a list too, one declared as a query; see EntityMemberInfo.IsQueryable.
    return definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>)
        || definition == typeof(IQueryable<>)
      ? type.GetGenericArguments()[0]
      : null;
  }

  /// <summary>Reads nullability off the CLR type and its nullable annotation.</summary>
  /// <remarks>An assembly compiled without nullable reference types reports no annotation, so its references are
  /// non-nullable unless <c>[Nullable]</c> says so; that attribute is applied later, in the early phase.</remarks>
  private static bool IsNullable(PropertyInfo property) {
    if (Nullable.GetUnderlyingType(property.PropertyType) != null)
      return true;
    var info = new NullabilityInfoContext().Create(property);
    return info.ReadState == NullabilityState.Nullable;
  }

  /// <summary>Applies the application's default string size and decimal precision and scale to members that declare
  /// none.</summary>
  /// <remarks>Runs after every attribute has been applied. A JSON document held as a string gets no default size:
  /// its column is the server's JSON type or unlimited text.</remarks>
  public void ApplyDefaults() {
    var app = context.App;
    foreach (var entity in context.Model.Entities) {
      foreach (var member in entity.Members) {
        if (member.Kind != MemberKind.Column)
          continue;
        var type = member.DataType;
        var isSized = !member.IsJson && !member.MemberFlags.HasFlag(MemberFlags.Unlimited);
        if (type == typeof(string) && member.Size == 0 && isSized)
          member.Size = app.Settings.DefaultStringSize;
        if (type == typeof(decimal) && member.Precision == 0) {
          member.Precision = app.Settings.DefaultDecimalPrecision;
          member.Scale = app.Settings.DefaultDecimalScale;
        }
      }
    }
  }

  /// <summary>Applies one entity's member attributes for the phase the context is in.</summary>
  public void ApplyMemberAttributes(EntityTypeInfo entity) {
    foreach (var member in entity.Members) {
      var property = FindProperty(entity.InterfaceType!, member.Name);
      if (property == null)
        continue;
      foreach (var attr in property.GetCustomAttributes<EntityModelAttributeBase>())
        attr.ApplyToMember(context, member);
    }
  }

  private static PropertyInfo? FindProperty(Type type, string name) =>
    type.GetProperty(name) ?? type.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);
}
