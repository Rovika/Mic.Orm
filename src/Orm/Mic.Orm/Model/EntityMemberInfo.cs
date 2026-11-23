using System.Reflection;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Orm.Runtime;
namespace Mic.Orm.Model;

/// <summary>Represents a member of an entity.</summary>
public class EntityMemberInfo : ILinqEntityMember {
  public EntityMemberInfo(string name, Type dataType, MemberKind kind, bool isNullable = false) {
    Name = name;
    DataType = Nullable.GetUnderlyingType(dataType) ?? dataType;
    Kind = kind;
    // Only Nullable<> widens it; a reference type is nullable by its annotation, not by being a reference type.
    IsNullable = isNullable || Nullable.GetUnderlyingType(dataType) != null;
  }

  public string Name { get; }

  ulong _loadCacheKey, _loadManyOwnersCacheKey;

  /// <summary>Gets the key the query cache holds the member's load under: one owner's rows of a list member, or an
  /// on-demand member of the records that lack it.</summary>
  /// <remarks>A fixed key rather than a hash of the query, since the model fixes the shape. See
  /// <see cref="EntityKeyInfo.SelectByKeyCacheKey"/>.</remarks>
  public ulong LoadCacheKey => _loadCacheKey != 0 ? _loadCacheKey
    : _loadCacheKey = Fnv1aHash.Combine(Fnv1aHash.Combine(Entity!.NameHash, Name), "load");

  /// <summary>Gets the key the query cache holds the load of many owners' rows of a list member, in one statement,
  /// under.</summary>
  public ulong LoadManyOwnersCacheKey => _loadManyOwnersCacheKey != 0 ? _loadManyOwnersCacheKey
    : _loadManyOwnersCacheKey = Fnv1aHash.Combine(Fnv1aHash.Combine(Entity!.NameHash, Name), "load-many-owners");

  /// <summary>Gets the CLR type this member holds, with any <c>Nullable&lt;&gt;</c> unwrapped.</summary>
  public Type DataType { get; }

  public bool IsNullable { get; set; }

  /// <summary>Gets the position of this member in its entity's <see cref="EntityTypeInfo.Members"/>.</summary>
  /// <remarks>Anything holding an array parallel to the member list indexes it by this, not by
  /// <see cref="DataIndex"/>.</remarks>
  public int Index { get; internal set; } = -1;

  /// <summary>Gets the slot this member's value sits in within a record's value array, or -1 for a member with no
  /// slot.</summary>
  /// <remarks>Assigned by <see cref="EntityTypeInfo.AddMember"/>. Read only by the record and the accessors that
  /// write one; nothing may rely on it agreeing with <see cref="Index"/>.</remarks>
  public int DataIndex { get; internal set; } = -1;

  public MemberKind Kind { get; }
  public MemberFlags MemberFlags { get; set; }

  /// <summary>Gets or sets the interface property this member was built from.</summary>
  /// <value>The property, or <see langword="null"/> for a member no property declares, such as a generated foreign-key
  /// member or a member of an entity declared in stored metadata.</value>
  public PropertyInfo? Property { get; set; }

  /// <summary>Gets the entity this member was added to.</summary>
  public EntityTypeInfo? Entity { get; internal set; }

  /// <summary>Gets or sets the entity at the other end of a reference or a list member.</summary>
  /// <remarks>This is what the reference builder runs from; <see cref="ReferenceInfo"/> and <see cref="ListInfo"/>
  /// carry the resolved detail once it has run.</remarks>
  public EntityTypeInfo? TargetEntity { get; set; }

  // Declaration facts, from [Size(50)] and the like; they become part of a type only in the db model.
  public int Size { get; set; }
  public byte Precision { get; set; }
  public byte Scale { get; set; }

  /// <summary>Gets or sets the database type named outright, from <c>[Column(DbTypeSpec = …)]</c>.</summary>
  /// <remarks>For a type the ORM's mapping would never choose, or one that is not the default for the member's CLR
  /// type. It is per-vendor: a spec captured from one server is a hint to re-map for another, not an instruction.
  /// </remarks>
  public string? DbTypeSpec { get; set; }

  /// <summary>Gets or sets the column name the developer asked for, from <c>[Column("…")]</c>.</summary>
  public string? ColumnName { get; set; }

  /// <summary>Gets or sets the member of the same entity whose value says which of several types a JSON member's
  /// document is.</summary>
  /// <value>The discriminator member, or <see langword="null"/> where the discriminator travels inside the document or
  /// there is only one type.</value>
  public EntityMemberInfo? JsonDiscriminatorMember { get; set; }

  /// <summary>Gets or sets the types the application mapped for a JSON member's base type, with each discriminator
  /// converted to the <see cref="JsonDiscriminatorMember"/>'s type.</summary>
  public JsonTypeMap? JsonTypes { get; set; }

  /// <summary>Gets or sets where the database computes this member's value from, for a member declared with
  /// <c>[FromJson]</c>: a path inside a JSON member's document; or <see langword="null"/> for any other member.
  /// </summary>
  /// <remarks>Such a member is <see cref="IsDbGenerated"/>: read-only, and out of every insert and update.</remarks>
  public FromJsonInfo? FromJson { get; set; }

  /// <summary>Gets or sets how the database computes this member's value, for a member declared with
  /// <c>[DbComputed]</c>: a SQL expression per server; or <see langword="null"/> for any other member.</summary>
  /// <remarks>Such a member is <see cref="IsDbGenerated"/>: read-only, and out of every insert and update.</remarks>
  public DbComputedInfo? DbComputed { get; set; }

  /// <summary>Gets or sets how this member's value is computed on the client, for a member declared with
  /// <c>[Computed]</c>: a static method over the entity, called on every read; or <see langword="null"/> for any
  /// other member.</summary>
  /// <remarks>Such a member is read-only; it has no column unless it persists its value, in which case the column is
  /// written with the computed value at every insert and update.</remarks>
  public ComputedInfo? Computed { get; set; }

  /// <summary>Gets the former names of the column, from <c>[OldNames]</c>, which turn a drop and add into a rename
  /// when the model is compared with a database.</summary>
  public IReadOnlyList<string> OldNames { get; internal set; } = [];

  /// <summary>Gets or sets the default expression of the column, as SQL text.</summary>
  public string? DefaultExpression { get; set; }

  // Kind-specific data; each is non-null only for its own kind.
  public ReferenceInfo? ReferenceInfo { get; set; }
  public ListInfo? ListInfo { get; set; }

  /// <summary>Gets or sets the reference member a generated foreign-key member belongs to.</summary>
  public EntityMemberInfo? OwnerReference { get; set; }

  /// <summary>Gets or sets what produces this member's value without anybody assigning one, from <c>[Auto(…)]</c>.
  /// </summary>
  public AutoType AutoValue { get; set; }

  /// <summary>Gets or sets the name of the database sequence a member with <see cref="AutoType.Sequence"/> draws
  /// its value from, as written in <c>[Sequence]</c>, with a schema in dotted notation where one was given.</summary>
  public string? SequenceName { get; set; }

  /// <summary>Gets or sets the sequence <see cref="SequenceName"/> resolved to, among those the modules declared;
  /// set by the model builder.</summary>
  public SequenceInfo? Sequence { get; set; }

  /// <summary>Gets a value that indicates whether this is a list-like member declared as a query, such as
  /// <c>IQueryable&lt;IOrder&gt; Orders { get; }</c>, for a relation too big to load whole.</summary>
  /// <remarks>The same kind as a list, with the same <see cref="ListInfo"/> and attributes, but it owns no storage
  /// slot: every read is a new query.</remarks>
  public bool IsQueryable =>
    Kind == MemberKind.List && DataType.IsGenericType && DataType.GetGenericTypeDefinition() == typeof(IQueryable<>);

  /// <summary>Gets or sets the member that holds a hash of this member's text, declared with <c>[HashFor]</c>, or
  /// <see langword="null"/> for a member with none.</summary>
  /// <remarks>Every assignment of this member writes the hash into that one.</remarks>
  public EntityMemberInfo? HashMember { get; set; }

  public bool IsPrimaryKey => MemberFlags.HasFlag(MemberFlags.PrimaryKey);
  public bool IsDbGenerated => MemberFlags.HasFlag(MemberFlags.DbGenerated);
  public bool IsJson => MemberFlags.HasFlag(MemberFlags.Json);
  public bool IsVector => MemberFlags.HasFlag(MemberFlags.Vector);
  public bool IsOnDemand => MemberFlags.HasFlag(MemberFlags.OnDemand);

  /// <summary>Gets or sets the metric a distance over a vector member is measured by, from <c>[Vector]</c>.</summary>
  /// <value>One of the enumeration values that specifies the metric. It has a meaning for a member that
  /// <see cref="IsVector"/> only.</value>
  public VectorMetric VectorMetric { get; set; }

  /// <summary>Gets or sets the type of an item, for a member that is an array or a <see cref="List{T}"/> of scalars
  /// held in an array column; <see langword="null"/> for any other member.</summary>
  /// <remarks>A <c>[Json]</c> member of such a type is a document and has none; a <c>[Vector]</c> member is a
  /// vector and has none.</remarks>
  public Type? ArrayItemType { get; set; }

  object? _defaultValue;

  /// <summary>Gets the value a non-nullable value-type member has when nothing has been assigned, boxed once.</summary>
  /// <value>The boxed default of the member's type, or <see langword="null"/> for a nullable or reference-type
  /// member.</value>
  /// <remarks>Copied into a new record's slot through <see cref="EntityTypeInfo.InitialValues"/>, except for a
  /// member that <see cref="StartsEmpty"/>, and returned by <c>GetColumnValue</c> in place of a
  /// <see langword="null"/> slot, so no read hands application code a <see langword="null"/> for a member whose type
  /// cannot hold one. A required string that nobody set stays <see langword="null"/>.</remarks>
  public object? DefaultValue => _defaultValue;

  /// <summary>Gets a value that indicates whether this member's slot is left empty on a new record rather than
  /// starting at its default.</summary>
  /// <remarks><see langword="true"/> for primary-key and foreign-key members only. An empty slot is how
  /// <c>EntityKey.IsEmpty</c> tells a record with no identity yet from one keyed by a default value, and how
  /// <c>GetReferenceValue</c> tells no target from a stub for the default value. The <see langword="null"/> is
  /// internal; <c>GetColumnValue</c> coalesces it to <see cref="DefaultValue"/> on the way out.</remarks>
  public bool StartsEmpty => IsPrimaryKey || OwnerReference != null;

  /// <summary>Computes what is derived from the builders' decisions, once the member is complete.</summary>
  /// <remarks>Called once, by the model builder, from <see cref="EntityTypeInfo.CompleteSetup"/>.</remarks>
  internal void CompleteSetup() {
    if (!IsNullable && DataType.IsValueType)
      _defaultValue = Activator.CreateInstance(DataType);
  }

  // Per-member read and write behaviour, assigned by MemberValueAccessorHelper at the end of a successful build for
  // every member of every entity. Non-nullable because a record calls them without checking.
  public Func<EntityRecord, EntityMemberInfo, object?> GetValueFunc { get; set; } = null!;
  public Action<EntityRecord, EntityMemberInfo, object?> SetValueAction { get; set; } = null!;

  // ----- ILinqEntityMember: the query engine's view -------------------------------------------------------------------------
  //
  // A reference or a list answers no data type: the client's model has none for a navigation member, and the engine
  // must not come to depend on a type only one of the two models can answer with.
  Type? ILinqEntityMember.DataType => Kind is MemberKind.Reference or MemberKind.List ? null : DataType;

  LinqMemberKind ILinqEntityMember.Kind => Kind switch {
    MemberKind.Reference => LinqMemberKind.Reference,
    MemberKind.List => LinqMemberKind.List,
    MemberKind.Transient => LinqMemberKind.Computed,
    _ => LinqMemberKind.Value,
  };

  ILinqEntity? ILinqEntityMember.Entity => Entity;

  ILinqReferenceInfo? ILinqEntityMember.ReferenceInfo => ReferenceInfo;

  ILinqMemberListInfo? ILinqEntityMember.ListInfo => ListInfo;
}
