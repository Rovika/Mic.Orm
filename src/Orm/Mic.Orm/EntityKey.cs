using Mic.Common;
using Mic.Orm.Db;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Represents the value of one key of an entity: which key, and the values of its members.</summary>
/// <remarks>Two values of different keys over the same members are not equal, so a primary-key value and a
/// foreign-key value of one entity can share a dictionary. A key of one member allocates nothing; a composite key
/// holds an array. The key carries no reference to its record.</remarks>
public readonly struct EntityKey : IEquatable<EntityKey> {
  readonly object? _value;
  readonly object?[]? _values;
  readonly int _hash;

  public EntityKey(EntityKeyInfo keyInfo, object? value) {
    ArgumentNullException.ThrowIfNull(keyInfo);
    KeyInfo = keyInfo;
    _value = value;
    _values = null;
    _hash = Hash(keyInfo, value);
  }

  public EntityKey(EntityKeyInfo keyInfo, object?[] values) {
    ArgumentNullException.ThrowIfNull(keyInfo);
    ArgumentNullException.ThrowIfNull(values);
    KeyInfo = keyInfo;
    if (values.Length == 1) {
      _value = values[0];
      _values = null;
      _hash = Hash(keyInfo, _value);
    } else {
      _value = null;
      _values = values;
      var hash = keyInfo.NameHash;
      foreach (var value in values)
        hash = Fnv1aHash.Combine(hash, (ulong)(value?.GetHashCode() ?? 0));
      _hash = hash.GetHashCode();
    }
  }

  private static int Hash(EntityKeyInfo keyInfo, object? value) =>
    Fnv1aHash.Combine(keyInfo.NameHash, (ulong)(value?.GetHashCode() ?? 0)).GetHashCode();

  /// <summary>Gets the key this is a value of.</summary>
  public EntityKeyInfo KeyInfo { get; }

  public EntityTypeInfo Entity => KeyInfo.Entity;

  /// <summary>Gets the flags the key was declared with: primary, unique, foreign or index.</summary>
  public KeyFlags KeyType => KeyInfo.Flags;

  /// <summary>Gets a value that indicates whether the key has no value yet, which is the ordinary state of a new
  /// entity until one is assigned.</summary>
  public bool IsEmpty => _value == null && _values == null;

  /// <summary>Gets the value of a key of one member.</summary>
  public object? SingleValue => _values == null ? _value : _values[0];

  /// <summary>Gets the value at a position of a composite key.</summary>
  /// <param name="index">The zero-based index of the key member.</param>
  /// <returns>The value of the member at that position.</returns>
  public object? ValueAt(int index) => _values == null ? (index == 0 ? _value : null) : _values[index];

  /// <summary>Reads a record's value of a key from its members.</summary>
  /// <param name="keyInfo">The key to read.</param>
  /// <param name="record">The record to read the key members from.</param>
  /// <returns>The record's value of the key.</returns>
  /// <remarks>A record's own primary key is available as <see cref="EntityRecord.PrimaryKey"/>.</remarks>
  public static EntityKey ForRecord(EntityKeyInfo keyInfo, EntityRecord record) {
    ArgumentNullException.ThrowIfNull(keyInfo);
    ArgumentNullException.ThrowIfNull(record);

    var members = keyInfo.MembersExpanded;
    if (members.Count == 1)
      return new EntityKey(keyInfo, record.GetRawValue(members[0].Member));

    var values = new object?[members.Count];
    for (var i = 0; i < members.Count; i++)
      values[i] = record.GetRawValue(members[i].Member);
    return new EntityKey(keyInfo, values);
  }

  public bool Equals(EntityKey other) {
    // The key definition is compared by reference: two values of different keys over the same members are different
    // lookups.
    if (!ReferenceEquals(KeyInfo, other.KeyInfo))
      return false;
    if (_values == null && other._values == null)
      return Equals(_value, other._value);
    if (_values == null || other._values == null || _values.Length != other._values.Length)
      return false;
    for (var i = 0; i < _values.Length; i++)
      if (!Equals(_values[i], other._values[i]))
        return false;
    return true;
  }

  public override bool Equals(object? obj) => obj is EntityKey other && Equals(other);

  /// <summary>Returns the hash code, computed once when the key was built.</summary>
  /// <returns>The hash code for the key.</returns>
  public override int GetHashCode() => _hash;

  public static bool operator ==(EntityKey left, EntityKey right) => left.Equals(right);
  public static bool operator !=(EntityKey left, EntityKey right) => !left.Equals(right);

  public override string ToString() =>
    $"{KeyInfo.Entity.Name}.{KeyInfo.Name}({(_values == null ? _value : string.Join(", ", _values))})";
}
