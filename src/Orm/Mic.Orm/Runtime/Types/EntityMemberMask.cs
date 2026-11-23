using Mic.Common;

namespace Mic.Orm.Runtime;

/// <summary>Represents one bit per member of an entity, the shape of every per-record mask.</summary>
/// <remarks>A copy of a mask of more than 64 members shares its overflow array, so an in-place change through either
/// copy is visible through the other; <see cref="Copy"/> makes an independent one, and a mask kept as a dictionary
/// key has to be copied on insert while a lookup key can alias safely. The struct must not be used through an
/// interface or as <c>object</c>, which boxes it. There is no complement: a mask does not carry its width, so one is
/// taken against the type's all-members mask.</remarks>
public struct EntityMemberMask : IEquatable<EntityMemberMask> {
  const int Width = 64;

  ulong _bits;
  ulong[]? _more;      // null for ≤64 members, which is nearly always

  /// <summary>Initializes a new instance of the <see cref="EntityMemberMask"/> struct wide enough for a number of
  /// members.</summary>
  /// <param name="memberCount">The number of members the mask covers.</param>
  /// <remarks>The overflow array is allocated here rather than on the first set past 64, so that every copy shares
  /// it.</remarks>
  public EntityMemberMask(int memberCount) {
    _bits = 0;
    _more = memberCount > Width ? new ulong[(memberCount - 1) / Width] : null;
  }

  public readonly bool IsSet(int index) {
    if (index < Width)
      return (_bits & (1UL << index)) != 0;
    var word = index / Width - 1;
    return _more != null && word < _more.Length && (_more[word] & (1UL << (index % Width))) != 0;
  }

  public void Set(int index) {
    if (index < Width) {
      _bits |= 1UL << index;
      return;
    }
    _more![index / Width - 1] |= 1UL << (index % Width);
  }

  public void Clear(int index) {
    if (index < Width) {
      _bits &= ~(1UL << index);
      return;
    }
    if (_more is { } more && index / Width - 1 < more.Length)
      more[index / Width - 1] &= ~(1UL << (index % Width));
  }

  /// <summary>Sets every bit below <paramref name="memberCount"/>.</summary>
  /// <param name="memberCount">The number of members the mask covers.</param>
  public void SetAll(int memberCount) {
    for (var i = 0; i < memberCount; i++)
      Set(i);
  }

  /// <summary>Unsets every bit.</summary>
  /// <remarks>On a wide mask this clears the shared overflow array, so every copy sees it.</remarks>
  public void Clear() {
    _bits = 0;
    if (_more != null)
      Array.Clear(_more);
  }

  public readonly bool IsEmpty {
    get {
      if (_bits != 0)
        return false;
      if (_more != null)
        foreach (var word in _more)
          if (word != 0)
            return false;
      return true;
    }
  }

  /// <summary>Determines whether every bit set in another mask is set in this one.</summary>
  /// <param name="other">The mask to look for.</param>
  /// <returns><see langword="true"/> if no bit of <paramref name="other"/> is unset here; otherwise,
  /// <see langword="false"/>.</returns>
  public readonly bool Contains(EntityMemberMask other) {
    if ((other._bits & ~_bits) != 0)
      return false;
    if (other._more is not { } theirs)
      return true;
    for (var i = 0; i < theirs.Length; i++)
      if ((theirs[i] & ~(_more != null && i < _more.Length ? _more[i] : 0)) != 0)
        return false;
    return true;
  }

  /// <summary>Gets the indexes that are set, in ascending order.</summary>
  /// <returns>The set indexes.</returns>
  public readonly IEnumerable<int> GetSetIndexes() {
    var bits = _bits;
    for (var i = 0; i < Width; i++)
      if ((bits & (1UL << i)) != 0)
        yield return i;

    if (_more is not { } more)
      yield break;
    for (var word = 0; word < more.Length; word++)
      for (var i = 0; i < Width; i++)
        if ((more[word] & (1UL << i)) != 0)
          yield return (word + 1) * Width + i;
  }

  /// <summary>Returns an independent copy of the mask, cloning the overflow array a plain assignment would
  /// share.</summary>
  /// <returns>The copy.</returns>
  public readonly EntityMemberMask Copy() {
    var copy = this;
    if (_more != null)
      copy._more = (ulong[])_more.Clone();
    return copy;
  }

  // ----- equality: exact, and hashed with FNV ----------------------------------------------------------------------
  //
  // FNV rather than System.HashCode, which is seeded randomly per process: a mask's hash has to mean the same thing
  // twice.

  public readonly bool Equals(EntityMemberMask other) {
    if (_bits != other._bits)
      return false;
    var mine = _more;
    var theirs = other._more;
    var length = Math.Max(mine?.Length ?? 0, theirs?.Length ?? 0);
    for (var i = 0; i < length; i++) {
      var myWord = mine != null && i < mine.Length ? mine[i] : 0;
      var theirWord = theirs != null && i < theirs.Length ? theirs[i] : 0;
      if (myWord != theirWord)
        return false;
    }
    return true;
  }

  public override readonly bool Equals(object? obj) => obj is EntityMemberMask other && Equals(other);

  public override readonly int GetHashCode() {
    var hash = Fnv1aHash.Compute([_bits]);
    if (_more != null)
      foreach (var word in _more)
        hash = Fnv1aHash.Combine(hash, word);
    return hash.GetHashCode();
  }

  public static bool operator ==(EntityMemberMask left, EntityMemberMask right) => left.Equals(right);
  public static bool operator !=(EntityMemberMask left, EntityMemberMask right) => !left.Equals(right);

  public override readonly string ToString() => $"[{string.Join(",", GetSetIndexes())}]";
}
