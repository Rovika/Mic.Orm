using System.Linq.Expressions;
using Mic.Orm.Db;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides what an entity instance cannot answer about itself: the record behind it, its session, its
/// declared interface, and its members by name.</summary>
/// <remarks>Static methods taking <see langword="object"/> rather than extension methods, so that nothing offers
/// <c>GetRecord()</c> on a string. A non-entity is refused by name.</remarks>
public static class EntityHelper {
  static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

  /// <summary>Gets the record behind an entity instance.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <returns>The record.</returns>
  public static EntityRecord GetRecord(object entity) {
    ArgumentNullException.ThrowIfNull(entity);
    return entity as EntityBase is { } based
      ? based.Record
      : throw new ArgumentException(
        $"'{entity.GetType().Name}' is not an entity — it was not created by an entity session.", nameof(entity));
  }

  /// <summary>Gets the session an entity belongs to.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <returns>The session, or <see langword="null"/> for an entity detached from any.</returns>
  public static EntitySession? GetSession(object entity) => GetRecord(entity).Session;

  /// <summary>Gets the interface an entity was declared as, rather than the class emitted at run time.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <returns>The entity interface.</returns>
  /// <exception cref="InvalidOperationException">The entity was declared in metadata and has no interface.</exception>
  public static Type GetEntityType(object entity) {
    var entityInfo = GetRecord(entity).Entity;
    return entityInfo.InterfaceType
      ?? throw new InvalidOperationException(
        $"Entity '{entityInfo.Name}' was declared in metadata and has no interface type.");
  }

  /// <summary>Gets the model's description of an entity.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <returns>The entity type.</returns>
  public static EntityTypeInfo GetEntityInfo(object entity) => GetRecord(entity).Entity;

  /// <summary>Reads a member by name, ignoring case.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <param name="propertyName">The member name.</param>
  /// <returns>The member's value.</returns>
  /// <remarks>For code that works over entities it was not compiled against: a designer, a serializer, a diff.
  /// </remarks>
  public static object? GetProperty(object entity, string propertyName) {
    var record = GetRecord(entity);
    return record.GetValue(GetMember(record, propertyName));
  }

  /// <summary>Reads a member by name, ignoring case, as <typeparamref name="T"/>.</summary>
  /// <typeparam name="T">The type to read the value as.</typeparam>
  /// <param name="entity">The entity instance.</param>
  /// <param name="propertyName">The member name.</param>
  /// <returns>The member's value, or <see langword="default"/> if it is <see langword="null"/> or not a
  /// <typeparamref name="T"/>.</returns>
  public static T? GetProperty<T>(object entity, string propertyName) =>
    GetProperty(entity, propertyName) is T value ? value : default;

  /// <summary>Writes a member by name, ignoring case, through the same accessor an emitted property uses.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <param name="propertyName">The member name.</param>
  /// <param name="value">The value to write.</param>
  public static void SetProperty(object entity, string propertyName, object? value) {
    var record = GetRecord(entity);
    record.SetValue(GetMember(record, propertyName), value);
  }

  /// <summary>Counts a member as changed without writing it, so that the next save writes its column.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <param name="memberName">The member name, ignoring case.</param>
  /// <remarks>A JSON document changed in place needs this call: a save writes a document only where its member
  /// was assigned or marked. A reference is marked through its key columns.</remarks>
  /// <exception cref="ArgumentException">The member has no column an update writes: a list, a computed or
  /// transient member, a primary-key member or the row version.</exception>
  public static void MarkModified(object entity, string memberName) {
    var record = GetRecord(entity);
    var member = GetMember(record, memberName);
    if (member.Kind == MemberKind.Reference && member.ReferenceInfo is { } reference) {
      foreach (var key in reference.FromKey.MembersExpanded)
        MarkColumnModified(record, key.Member);
      return;
    }
    MarkColumnModified(record, member);
  }

  /// <summary>Counts a member as changed without writing it, so that the next save writes its column.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity instance.</param>
  /// <param name="member">The member, as a property of the entity: <c>x => x.Settings</c>.</param>
  /// <remarks>See <see cref="MarkModified(object, string)"/>.</remarks>
  public static void MarkModified<TEntity>(TEntity entity, Expression<Func<TEntity, object?>> member)
      where TEntity : class =>
    MarkModified(entity, GetMemberName(member));

  /// <summary>Gets a JSON member's document as text, without making its object.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <param name="memberName">The member name, ignoring case.</param>
  /// <returns>The JSON text, or <see langword="null"/> for a null document. A document not read since it was
  /// loaded is the text the server sent, in the server's spacing and property order.</returns>
  /// <exception cref="ArgumentException">The member is not a JSON member.</exception>
  public static string? GetJson(object entity, string memberName) {
    var record = GetRecord(entity);
    return JsonMemberHelper.GetJson(record, GetJsonMember(record, memberName));
  }

  /// <summary>Gets a JSON member's document as text, without making its object.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity instance.</param>
  /// <param name="member">The member, as a property of the entity: <c>x => x.Settings</c>.</param>
  /// <returns>The JSON text, or <see langword="null"/> for a null document.</returns>
  /// <remarks>See <see cref="GetJson(object, string)"/>.</remarks>
  public static string? GetJson<TEntity>(TEntity entity, Expression<Func<TEntity, object?>> member)
      where TEntity : class =>
    GetJson(entity, GetMemberName(member));

  /// <summary>Replaces a JSON member's document with text and counts the member as changed.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <param name="memberName">The member name, ignoring case.</param>
  /// <param name="json">The JSON text, or <see langword="null"/> for a null document.</param>
  /// <remarks>The text is not checked. The next read of the member makes the object from it.</remarks>
  /// <exception cref="ArgumentException">The member is not a JSON member.</exception>
  public static void SetJson(object entity, string memberName, string? json) {
    var record = GetRecord(entity);
    JsonMemberHelper.SetJson(record, GetJsonMember(record, memberName), json);
  }

  /// <summary>Replaces a JSON member's document with text and counts the member as changed.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity instance.</param>
  /// <param name="member">The member, as a property of the entity: <c>x => x.Settings</c>.</param>
  /// <param name="json">The JSON text, or <see langword="null"/> for a null document.</param>
  /// <remarks>See <see cref="SetJson(object, string, string)"/>.</remarks>
  public static void SetJson<TEntity>(TEntity entity, Expression<Func<TEntity, object?>> member, string? json)
      where TEntity : class =>
    SetJson(entity, GetMemberName(member), json);

  /// <summary>Gets the commands the entity's session last ran; see <see cref="EntitySession.LastCommands"/>.</summary>
  /// <param name="entity">The entity instance.</param>
  /// <returns>The commands, or an empty list for a detached entity.</returns>
  public static IReadOnlyList<DataCommandSnapshot> GetLastCommands(object entity) =>
    GetSession(entity)?.LastCommands ?? [];

  /// <summary>Gets the time stamped into a Guid the ORM generated, such as an entity's key.</summary>
  /// <param name="value">The Guid.</param>
  /// <returns>The UTC time, to the millisecond, of a version 7 Guid and of a version 8 Guid in the ORM's SQL Server
  /// layout; <see langword="null"/> for any other Guid, such as a random one.</returns>
  /// <remarks>Past the 4,096th Guid a session makes before one save, the time is a millisecond ahead for every
  /// 4,096. A version 8 Guid from another source is read as if it had the ORM's layout, since nothing in the value
  /// tells the two apart.</remarks>
  public static DateTime? GetTimestamp(Guid value) {
    // Where the 48-bit Unix millisecond sits, most significant byte first: at the front of a version 7, in the last
    // six bytes of the SQL Server layout, which that server compares first.
    var offset = value.Version switch {
      7 => 0,
      8 => 10,
      _ => -1,
    };
    if (offset < 0)
      return null;
    Span<byte> bytes = stackalloc byte[16];
    value.TryWriteBytes(bytes, bigEndian: true, out _);
    var milliseconds = 0L;
    for (var i = 0; i < 6; i++)
      milliseconds = (milliseconds << 8) | bytes[offset + i];
    return milliseconds > MaxUnixMilliseconds
      ? null
      : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
  }

  private static EntityMemberInfo GetMember(EntityRecord record, string propertyName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
    return record.Entity.FindMember(propertyName)
      ?? throw new KeyNotFoundException(
        $"'{record.Entity.Name}' has no member named '{propertyName}'.");
  }

  private static EntityMemberInfo GetJsonMember(EntityRecord record, string memberName) {
    var member = GetMember(record, memberName);
    return member.IsJson ? member
      : throw new ArgumentException($"'{record.Entity.Name}.{member.Name}' is not a JSON member.", nameof(memberName));
  }

  private static void MarkColumnModified(EntityRecord record, EntityMemberInfo member) {
    if (member.Kind != MemberKind.Column || member.Computed != null || member.IsPrimaryKey
        || member.AutoValue == AutoType.RowVersion)
      throw new ArgumentException(
        $"'{record.Entity.Name}.{member.Name}' has no column an update writes, so it cannot be marked modified.");
    record.Session?.ThrowIfSaving();
    record.MarkModified(member);
  }

  // The name of the property a selector reads off its parameter, as in `x => x.Settings`.
  internal static string GetMemberName(LambdaExpression selector) {
    ArgumentNullException.ThrowIfNull(selector);
    var body = selector.Body;
    // The selector's result is `object`, so the compiler boxes a value or converts a reference.
    while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
      body = convert.Operand;
    return body is MemberExpression { Expression: ParameterExpression } access
      ? access.Member.Name
      : throw new ArgumentException(
        $"A member is named as a property of the entity, as in 'x => x.Name', not '{selector}'.", nameof(selector));
  }
}
