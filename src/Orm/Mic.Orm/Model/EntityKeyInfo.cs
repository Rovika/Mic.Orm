using Mic.Common;
using Mic.Orm.Db;

namespace Mic.Orm.Model;

/// <summary>Represents one member of a key, with its sort direction.</summary>
public readonly struct KeyMember {
  public KeyMember(EntityMemberInfo member, bool descending = false) {
    Member = member;
    Descending = descending;
  }

  public EntityMemberInfo Member { get; }
  public bool Descending { get; }
}

/// <summary>Holds the data a key has only when it is an index.</summary>
public class IndexOptions {
  readonly List<EntityMemberInfo> _includedMembers = new();
  readonly List<EntityMemberInfo> _includedExpanded = new();

  /// <summary>Gets the members carried in the index leaf that are not part of the key, as declared.</summary>
  /// <remarks>A reference named here stays a reference; <see cref="IncludedMembersExpanded"/> has its columns.
  /// </remarks>
  public IReadOnlyList<EntityMemberInfo> IncludedMembers => _includedMembers;

  public IReadOnlyList<EntityMemberInfo> IncludedMembersExpanded =>
    _includedExpanded.Count > 0 ? _includedExpanded : _includedMembers;

  internal void SetIncludedMembersExpanded(IEnumerable<EntityMemberInfo> members) {
    _includedExpanded.Clear();
    _includedExpanded.AddRange(members);
  }

  /// <summary>Gets or sets the condition of a filtered (partial) index, in the dialect's SQL.</summary>
  /// <remarks>Compared during an upgrade through a stored hash rather than a text match.</remarks>
  public string? Filter { get; set; }

  /// <summary>Gets or sets the access method of the index, as declared.</summary>
  /// <value>One of the enumeration values that specifies the access method. <c>Default</c> means the author said
  /// nothing; the db model resolves it to the server's own default.</value>
  public DbIndexType IndexType { get; set; }

  public void AddIncludedMember(EntityMemberInfo member) => _includedMembers.Add(member);
}

/// <summary>Represents a key: a named set of members of one entity, serving as a primary key, a foreign key, a unique
/// constraint or an index as its <see cref="Flags"/> say.</summary>
public class EntityKeyInfo {
  public string Name { get; }

  readonly List<KeyMember> _members = new();
  readonly List<KeyMember> _membersExpanded = new();

  public EntityKeyInfo(string name, EntityTypeInfo entity, KeyFlags flags) {
    Name = name;
    Entity = entity;
    Flags = flags;
  }

  public EntityTypeInfo Entity { get; }
  public KeyFlags Flags { get; set; }

  ulong _nameHash;

  /// <summary>Gets the hash of this key's identity, combining the entity's name hash with the key's name.</summary>
  /// <remarks>Computed once. An <c>EntityKey</c> combines it with its values.</remarks>
  public ulong NameHash => _nameHash != 0 ? _nameHash : _nameHash = Fnv1aHash.Combine(Entity.NameHash, Name);

  ulong _selectByKeyCacheKey, _selectByKeysCacheKey;

  /// <summary>Gets the key the query cache holds the read of one row by this key under.</summary>
  /// <remarks>A fixed key rather than a hash of the query, since the model fixes the shape; salted by the form, so the
  /// same key's two reads and an application's own shapes never share an entry.</remarks>
  public ulong SelectByKeyCacheKey => _selectByKeyCacheKey != 0 ? _selectByKeyCacheKey
    : _selectByKeyCacheKey = Fnv1aHash.Combine(NameHash, "select-by-key");

  /// <summary>Gets the key the query cache holds the read of the rows of several values of this key under, which
  /// expands every stub at once.</summary>
  public ulong SelectByKeysCacheKey => _selectByKeysCacheKey != 0 ? _selectByKeysCacheKey
    : _selectByKeysCacheKey = Fnv1aHash.Combine(NameHash, "select-by-keys");

  /// <summary>Gets the member this key belongs to: the reference that generated a foreign key, or the member a
  /// member-level <c>[PrimaryKey]</c> or <c>[Index]</c> sits on.</summary>
  /// <value>The owner member, or <see langword="null"/> for a key declared at entity level.</value>
  public EntityMemberInfo? OwnerMember { get; internal set; }

  /// <summary>Gets the members of the key as declared, where a member may be a reference.</summary>
  /// <remarks><see cref="MembersExpanded"/> has every reference replaced by the columns of its foreign key, which is
  /// what the database gets.</remarks>
  public IReadOnlyList<KeyMember> Members => _members;

  /// <summary>Gets the members of the key with every reference replaced by the columns of its foreign key.</summary>
  /// <remarks>Falls back to <see cref="Members"/> until the key has been expanded; <see cref="IsExpanded"/> says
  /// whether it has.</remarks>
  public IReadOnlyList<KeyMember> MembersExpanded => IsExpanded ? _membersExpanded : _members;

  /// <summary>Gets a value that indicates whether every reference in this key has been replaced by its columns.
  /// </summary>
  /// <remarks>Expansion cascades: a key over a reference whose target's own key is a reference cannot finish until
  /// that one has.</remarks>
  public bool IsExpanded { get; private set; }

  /// <summary>Gets or sets the index-only data of the key, or <see langword="null"/> for a key that is not an index.
  /// </summary>
  public IndexOptions? Index { get; set; }

  public bool IsPrimaryKey => Flags.HasFlag(KeyFlags.PrimaryKey);
  public bool IsForeignKey => Flags.HasFlag(KeyFlags.ForeignKey);
  public bool IsUnique => Flags.HasFlag(KeyFlags.Unique) || IsPrimaryKey;

  /// <summary>Gets a value that indicates whether the key has more than one column.</summary>
  /// <remarks>Measured on the expanded list: one reference to a composite target is a composite key.</remarks>
  public bool IsComposite => MembersExpanded.Count > 1;

  /// <summary>Sets the expanded members, once every member of this key resolves to columns.</summary>
  internal void SetMembersExpanded(IEnumerable<KeyMember> members) {
    _membersExpanded.Clear();
    _membersExpanded.AddRange(members);
    IsExpanded = true;
  }

  /// <summary>Adds a member to the key.</summary>
  /// <param name="member">The member; it belongs to the key's entity, by declaration or by flattening.</param>
  /// <param name="descending"><see langword="true"/> to sort the member descending; otherwise, <see langword="false"/>.
  /// </param>
  /// <exception cref="InvalidOperationException">The member belongs to a different entity.</exception>
  public void AddMember(EntityMemberInfo member, bool descending = false) {
    ArgumentNullException.ThrowIfNull(member);
    if (member.Entity != null && !Entity.Members.Contains(member))
      throw new InvalidOperationException(
        $"Key '{Name}' on '{Entity.Name}': member '{member.Name}' belongs to '{member.Entity.Name}'.");
    _members.Add(new KeyMember(member, descending));
  }
}
