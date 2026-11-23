using Mic.Orm.Db;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Orm;

/// <summary>Defines the core behavior of a MIC ORM declaration attribute and provides a base for derived classes.
/// </summary>
/// <remarks>The model builder collects every attribute of this base in one pass and runs over them once per build
/// phase; an override checks <c>BuilderContext.Phase</c> to decide whether it is its turn. An attribute modifies an
/// existing model object; an entity's name is fixed when the entity is constructed and is read by the builder
/// directly.</remarks>
public abstract partial class EntityModelAttributeBase : Attribute {
  /// <summary>When overridden in a derived class, applies this attribute to the entity it decorates.</summary>
  internal virtual void ApplyToEntity(BuilderContext context, EntityTypeInfo entity) { }

  /// <summary>When overridden in a derived class, applies this attribute to the member it decorates.</summary>
  internal virtual void ApplyToMember(BuilderContext context, EntityMemberInfo member) { }
}

// ----- Identity and structure -------------------------------------------------------------------------------------

/// <summary>Specifies that an interface is an entity.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public partial class EntityAttribute : EntityModelAttributeBase {
  public string? Name { get; set; }
  public string? PluralName { get; set; }
  public string? TableName { get; set; }
}

/// <summary>Specifies that an entity reads through a database view, and names the view.</summary>
/// <remarks>Alone, the view is one that exists in the database already, which MIC never creates, changes or drops;
/// a <c>RegisterView</c> call in the module gives it a body, which MIC then manages. The name and
/// <see cref="Materialized"/> apply either way.</remarks>
[AttributeUsage(AttributeTargets.Interface)]
public partial class ViewAttribute : EntityModelAttributeBase {
  public ViewAttribute(string? name = null) { Name = name; }

  /// <summary>Gets or sets the name of the view in the database, or <see langword="null"/> for the naming policy's
  /// table name of the entity.</summary>
  public string? Name { get; set; }

  /// <summary>Gets or sets a value that indicates whether the server stores the view's rows rather than its query.
  /// </summary>
  public bool Materialized { get; set; }
}

/// <summary>Specifies the database column behind a member.</summary>
/// <remarks>Optional; every property has a default.</remarks>
public partial class ColumnAttribute : EntityModelAttributeBase {
  public ColumnAttribute() { }
  public ColumnAttribute(string columnName) { ColumnName = columnName; }

  public string? ColumnName { get; set; }
  public string? Default { get; set; }
  public int Size { get; set; }
  public byte Scale { get; set; }
  public byte Precision { get; set; }
  public string? DbTypeSpec { get; set; }
}

/// <summary>Specifies the former names of a table or column, comma-separated, so that an upgrade renames the
/// existing database object rather than dropping and recreating it.</summary>
/// <remarks>Safe to leave in place indefinitely; once the database object matches the new name, it has no effect.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public partial class OldNamesAttribute : EntityModelAttributeBase {
  public OldNamesAttribute(string oldNames) { OldNames = oldNames; }
  public string OldNames { get; }
}

/// <summary>Specifies that a member exists in memory only and has no database column.</summary>
public partial class NoColumnAttribute : EntityModelAttributeBase { }

// ----- Keys and indexes -------------------------------------------------------------------------------------------

/// <summary>Defines the core behavior of a key or index attribute and provides a base for derived classes.</summary>
/// <remarks>On a property the key is that one member; on an entity, <see cref="MembersSpec"/> names the members in
/// the member-list syntax of <c>MemberListHelper</c>.</remarks>
public abstract partial class KeyAttribute : EntityModelAttributeBase {
  public string? MembersSpec { get; set; }

  /// <summary>Gets or sets the name of the key or index in the database, or <see langword="null"/> for the name
  /// the naming policy derives from the entity and the members.</summary>
  /// <remarks>Used where the model has to match a database that already exists, so that renaming a member does not
  /// rename the index.</remarks>
  public string? Name { get; set; }
}

/// <summary>Specifies the primary key of an entity.</summary>
/// <remarks>Every entity must have one; it is never inferred from a member named <c>Id</c>.</remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public partial class PrimaryKeyAttribute : KeyAttribute {
  public PrimaryKeyAttribute() { }
  /// <summary>Initializes a new instance of the <see cref="PrimaryKeyAttribute"/> class with the key's members.
  /// </summary>
  /// <param name="membersSpec">The key members, such as <c>"TripId,LegNo"</c> or <c>"Name -DateCreated"</c>.</param>
  public PrimaryKeyAttribute(string membersSpec) { MembersSpec = membersSpec; }

  public bool Clustered { get; set; }
}

/// <summary>Specifies an index.</summary>
/// <remarks>On a property with no arguments it covers that member; on an entity it names its members.</remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property, AllowMultiple = true)]
public partial class IndexAttribute : KeyAttribute {
  public IndexAttribute() { }
  public IndexAttribute(string membersSpec) { MembersSpec = membersSpec; }

  public bool Clustered { get; set; }
  public bool Unique { get; set; }

  /// <summary>Gets or sets the members carried in the index leaf that are not part of the key.</summary>
  /// <remarks>A direction given here has no meaning.</remarks>
  public string? IncludeMembers { get; set; }

  /// <summary>Gets or sets the condition of a filtered (partial) index, in the dialect's SQL.</summary>
  /// <remarks>Compared during an upgrade through a stored hash rather than a text match.</remarks>
  public string? Filter { get; set; }

  /// <summary>Gets or sets the access method of the index.</summary>
  /// <value>One of the enumeration values that specifies the access method. The default is the server's own.</value>
  /// <remarks>A server that does not have the method asked for creates an ordinary index and says so in the log.
  /// </remarks>
  public DbIndexType IndexType { get; set; }
}

// Saying the same thing twice, as in [Unique] beside [Index(Unique = true)], is reported as an error.

/// <summary>Specifies a unique index.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property, AllowMultiple = true)]
public partial class UniqueAttribute : IndexAttribute {
  public UniqueAttribute() { Unique = true; }
  public UniqueAttribute(string membersSpec) : base(membersSpec) { Unique = true; }
}

/// <summary>Declares a check constraint on the entity's table: a condition every row has to meet.</summary>
/// <remarks>The condition is SQL as the server reads it. A member of the entity written as <c>{Name}</c> stands for
/// its column, quoted as the server quotes, so one text often serves every server; a declaration that names a server
/// kind applies to that server alone and takes the place of the general one of the same name there. The constraint
/// is named <c>CK_</c>, the table, <see cref="Name"/> and a hash of the condition. An upgrade compares names: a
/// changed condition drops the constraint and adds it under its new name, and a constraint not named this way is
/// left alone.</remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public partial class CheckAttribute : EntityModelAttributeBase {
  /// <summary>Gets the name of the check within its entity, of letters, digits and underscores.</summary>
  public string Name { get; }

  /// <summary>Gets the condition, in the server's SQL.</summary>
  public string Expression { get; }

  /// <summary>Gets the server kind the declaration is written for, or <see langword="null"/> for every server.
  /// </summary>
  public DbServerKind? ServerKind { get; }

  public CheckAttribute(string name, string expression) {
    Name = name;
    Expression = expression;
  }

  public CheckAttribute(DbServerKind serverKind, string name, string expression) : this(name, expression) {
    ServerKind = serverKind;
  }
}

/// <summary>Specifies a clustered index, the key that decides physical placement.</summary>
/// <remarks>Not assumed to be the primary key; a child table is usually clustered by its parent.</remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public partial class ClusteredIndexAttribute : IndexAttribute {
  public ClusteredIndexAttribute() { Clustered = true; }
  public ClusteredIndexAttribute(string membersSpec) : base(membersSpec) { Clustered = true; }
}

/// <summary>Specifies a unique clustered index.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public partial class UniqueClusteredIndexAttribute : IndexAttribute {
  public UniqueClusteredIndexAttribute() { Unique = true; Clustered = true; }
  public UniqueClusteredIndexAttribute(string membersSpec) : base(membersSpec) { Unique = true; Clustered = true; }
}

// ----- Relationships ----------------------------------------------------------------------------------------------

/// <summary>Specifies the foreign key columns or constraint name of an entity reference.</summary>
/// <remarks>Optional; a reference member is recognised by its type. Used where the model has to match a database
/// that already exists.</remarks>
public partial class EntityRefAttribute : EntityModelAttributeBase {
  public EntityRefAttribute(string? keyColumns = null) {
    KeyColumns = keyColumns?.Replace(" ", string.Empty);
  }

  public string? KeyColumns { get; set; }
  public string? ForeignKeyName { get; set; }
  public string? TargetUniqueIndexAlias { get; set; }

  /// <summary>Gets or sets a value that indicates whether the database enforces the reference with a foreign key.
  /// </summary>
  /// <value><see langword="true"/> if the database enforces the reference; otherwise, <see langword="false"/>. The
  /// default is <see langword="true"/>.</value>
  public bool HasDbForeignKey { get; set; } = true;
}

/// <summary>Specifies that a list member reaches another entity set through a link entity.</summary>
public partial class ManyToManyAttribute : EntityModelAttributeBase {
  public ManyToManyAttribute(Type linkEntity) { LinkEntity = linkEntity; }

  public Type LinkEntity { get; }

  /// <summary>Gets or sets the member of the link entity that points back at this entity.</summary>
  /// <remarks>Needed only when more than one member of the link entity does.</remarks>
  public string? ThisEntityRef { get; set; }
  /// <summary>Gets or sets the member of the link entity that points at the other entity.</summary>
  /// <remarks>Needed only when more than one member of the link entity does.</remarks>
  public string? OtherEntityRef { get; set; }
}

/// <summary>Specifies which reference of the child entity owns a one-to-many list.</summary>
/// <remarks>Optional; needed when the child has more than one reference back to this entity.</remarks>
public partial class OneToManyAttribute : EntityModelAttributeBase {
  public OneToManyAttribute(string? thisEntityRef = null) { ThisEntityRef = thisEntityRef; }
  public string? ThisEntityRef { get; set; }
}

/// <summary>Specifies that a member is derived from a one-to-one relation, in which the child's primary key is its
/// reference back to this entity, so the member has no column of its own.</summary>
public partial class OneToOneAttribute : EntityModelAttributeBase { }

/// <summary>Specifies <c>ON DELETE CASCADE</c> on the foreign key behind a reference.</summary>
public partial class CascadeDeleteAttribute : EntityModelAttributeBase { }

/// <summary>Specifies the default sort order of lists of an entity, such as <c>"LastName -DateCreated"</c>.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public partial class OrderByAttribute : EntityModelAttributeBase {
  public OrderByAttribute(string orderByList) { OrderByList = orderByList; }
  public string OrderByList { get; }
}

/// <summary>Specifies the member of the target or link entity that keeps an explicit list order.</summary>
public partial class PersistOrderInAttribute : EntityModelAttributeBase {
  public PersistOrderInAttribute(string property) { Property = property; }
  public string Property { get; }
}

// ----- Generated values -------------------------------------------------------------------------------------------

/// <summary>Specifies that a member's value is assigned automatically.</summary>
public partial class AutoAttribute : EntityModelAttributeBase {
  public AutoAttribute(AutoType type = AutoType.NewGuid) { Type = type; }

  public AutoType Type { get; }
  public string? SequenceName { get; set; }
}

/// <summary>Specifies an identity column, whose value the database produces.</summary>
/// <remarks>Integer members only.</remarks>
public partial class IdentityAttribute : AutoAttribute {
  public IdentityAttribute() : base(AutoType.Identity) { }
}

/// <summary>Specifies that a member's value comes from a database sequence.</summary>
/// <remarks>Dotted notation names a schema, as in <c>"dbo.MySequence"</c>.</remarks>
public partial class SequenceAttribute : AutoAttribute {
  public SequenceAttribute(string sequenceName) : base(AutoType.Sequence) { SequenceName = sequenceName; }
}

/// <summary>Specifies a row version (timestamp) column.</summary>
public partial class RowVersionAttribute : AutoAttribute {
  public RowVersionAttribute() : base(AutoType.RowVersion) { }
}

/// <summary>Specifies that a member holds a hash of another member's text, so that a long text value can be indexed
/// without indexing the text.</summary>
/// <remarks>The member is an <c>int</c> or a <c>long</c>, and is written whenever the text member is assigned.
/// The hash is FNV-1a over the trimmed text: 64 bits in a <c>long</c>, folded to 32 in an <c>int</c>; a text that
/// is null or blank hashes to 0. <see cref="EntityAppExtensions.ComputeStableHash"/> and
/// <see cref="EntityAppExtensions.ComputeStableHash64(EntityApp, string?)"/> give the same value for a query.
/// </remarks>
public partial class HashForAttribute : EntityModelAttributeBase {
  public HashForAttribute(string propertyName) { PropertyName = propertyName; }
  public string PropertyName { get; }
}

// ----- Type detail ------------------------------------------------------------------------------------------------

/// <summary>Specifies the maximum length of a character or binary column.</summary>
/// <remarks>The compact spelling of <c>Column(Size = …)</c>; saying both on one member is an error. A string member
/// with no size gets <see cref="EntityAppSettings.DefaultStringSize"/>.</remarks>
public partial class SizeAttribute : EntityModelAttributeBase {
  /// <summary>Initializes a new instance of the <see cref="SizeAttribute"/> class with a length.</summary>
  /// <param name="size">The length, in characters or bytes.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies how an oversized value is
  /// treated.</param>
  public SizeAttribute(int size, SizeOptions options = SizeOptions.None) {
    Size = size;
    Options = options;
  }

  /// <summary>Initializes a new instance of the <see cref="SizeAttribute"/> class with a named size.</summary>
  /// <param name="sizeCode">The name of the size, resolved against the module's size table and then the
  /// application's, which is how an application adjusts the column sizes of a module it did not write.</param>
  /// <param name="defaultSize">The length used when the name resolves nowhere.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies how an oversized value is
  /// treated.</param>
  public SizeAttribute(string sizeCode, int defaultSize = 0, SizeOptions options = SizeOptions.None) {
    SizeCode = sizeCode;
    Size = defaultSize;
    Options = options;
  }

  public int Size { get; }
  public string? SizeCode { get; }
  public SizeOptions Options { get; }
}

/// <summary>Specifies unlimited-length text or binary, such as <c>nvarchar(max)</c> and its equivalents.</summary>
public partial class UnlimitedAttribute : EntityModelAttributeBase { }

/// <summary>Specifies that a member's column is left out of a read of the entity and loaded when the member is asked
/// for.</summary>
/// <remarks>For a column that is large and seldom read: a document's text, a picture, an embedding. A query that
/// names the member reads its column as any other. The member is a column member outside the primary key, the
/// unique keys and the row version, of an entity that has a primary key and is not cached.</remarks>
public partial class OnDemandAttribute : EntityModelAttributeBase { }

/// <summary>Specifies a JSON column: the member's value is an object of the declared type, serialized to JSON on its
/// way to the server and read back from it.</summary>
/// <remarks>The column is the server's JSON type where it has one. The declared type is the base type of what the
/// column may hold; a column holding one of several types names them on the base type with
/// <c>[JsonDerivedType(typeof(Circle), "circle")]</c>. With nothing more, the discriminator travels inside the
/// document (<c>$type</c>, or what <c>[JsonPolymorphic]</c> names). With <see cref="Discriminator"/> naming another
/// member of the entity, the discriminator lives in that column instead: the document is written as its concrete
/// type with no discriminator in it, the column is stamped from the object's type at a save, and a row is read as the
/// type the column says.</remarks>
public partial class JsonAttribute : EntityModelAttributeBase {
  /// <summary>Gets or sets the member of the same entity whose value says which of the derived types the document is,
  /// as one of the discriminators the base type's <c>[JsonDerivedType]</c> attributes name.</summary>
  public string? Discriminator { get; set; }
}

/// <summary>Specifies a vector column: the member, a <c>float[]</c>, is held in the server's vector type.</summary>
/// <remarks>A value saved has to have the number of items the attribute states. A server with no vector type
/// reports the member as an error of the model. Without the attribute a <c>float[]</c> member is an array column.
/// </remarks>
public partial class VectorAttribute : EntityModelAttributeBase {
  /// <summary>Initializes a new instance of the <see cref="VectorAttribute"/> class with a number of dimensions.
  /// </summary>
  /// <param name="dimensions">The number of items in every value of the member.</param>
  public VectorAttribute(int dimensions) {
    Dimensions = dimensions;
  }

  /// <summary>Gets the number of items in every value of the member.</summary>
  public int Dimensions { get; }

  /// <summary>Gets or sets the metric the distance between two values of the member is measured by.</summary>
  /// <value>One of the enumeration values that specifies the metric. The default is
  /// <see cref="VectorMetric.Cosine"/>.</value>
  /// <remarks>It is the metric of <c>VectorFunctions.Distance</c> over the member in a query.</remarks>
  public VectorMetric Metric { get; set; }
}

/// <summary>Specifies that a member is nullable.</summary>
/// <remarks>Usually unnecessary, since nullability is read from the CLR type (<c>string?</c>, <c>int?</c>). Needed
/// in an assembly compiled without nullable reference types, which reports no annotations, so that strings and
/// references are non-nullable unless marked; and for a value type made nullable without <c>?</c>, where
/// <c>default(T)</c> stands in for <see langword="null"/>. This attribute wins over a non-nullable annotation.
/// </remarks>
public partial class NullableAttribute : EntityModelAttributeBase { }

/// <summary>Specifies that a member holds a date with no time part.</summary>
/// <remarks>Only needed for a <c>DateTime</c> member; a member typed <c>DateOnly</c> says it already.</remarks>
public partial class DateOnlyAttribute : EntityModelAttributeBase { }

/// <summary>Specifies that a member holds a UTC date and time; assigned values are converted and read values are
/// always UTC.</summary>
public partial class UtcAttribute : EntityModelAttributeBase { }

// ----- Computed members ----------------------------------------------------------------------------------------------

/// <summary>Specifies a member the database computes from a value inside a JSON member's document: a generated
/// column over the path, which an index can cover and a query can filter on.</summary>
/// <remarks>The member is read-only and takes no part in an insert or an update; its type is what the value is cast
/// to. The column is virtual unless <see cref="Stored"/> says otherwise, stored on a server that has no virtual
/// generated columns, and virtual on one that cannot add a stored column to a table. Naming the member in
/// <c>[Json(Discriminator = …)]</c> of the document it reads makes it the discriminator the serializer writes into
/// the document, exposed as a column.</remarks>
public partial class FromJsonAttribute : EntityModelAttributeBase {
  /// <summary>Initializes a new instance of the <see cref="FromJsonAttribute"/> class.</summary>
  /// <param name="jsonMember">The name of the JSON member of the same entity the value is read from.</param>
  /// <param name="path">The path of the value inside the document, as <c>$.a.b</c>.</param>
  public FromJsonAttribute(string jsonMember, string path) {
    JsonMember = jsonMember;
    Path = path;
  }

  public string JsonMember { get; }
  public string Path { get; }

  /// <summary>Gets or sets a value that indicates whether the server stores the value rather than computes it on
  /// each read.</summary>
  public bool Stored { get; set; }
}

/// <summary>Specifies a member the database computes from a SQL expression over the row, written per server with
/// <see cref="SqlExpressionAttribute"/>: a generated column, which an index can cover and a query can filter on.
/// </summary>
/// <remarks>The member is read-only and takes no part in an insert or an update. A database whose server kind has
/// no expression is a diagnostic in its db model's build report. The column is virtual unless <see cref="Stored"/>
/// says otherwise, stored on a server that has no virtual generated columns, and virtual on one that cannot add a
/// stored column to a table.</remarks>
public partial class DbComputedAttribute : EntityModelAttributeBase {
  /// <summary>Gets or sets a value that indicates whether the server stores the value rather than computes it on
  /// each read.</summary>
  public bool Stored { get; set; }
}

/// <summary>Specifies the SQL expression a <see cref="DbComputedAttribute"/> member is computed from on one server
/// kind.</summary>
/// <remarks>A member of the entity written as <c>{Name}</c> stands for its column, quoted as the server quotes, so
/// one text often serves every server and survives a naming policy; a column may also be named outright, as the
/// server spells it.</remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public partial class SqlExpressionAttribute : EntityModelAttributeBase {
  public SqlExpressionAttribute(DbServerKind serverKind, string expression) {
    ServerKind = serverKind;
    Expression = expression;
  }

  public DbServerKind ServerKind { get; }
  public string Expression { get; }
}

/// <summary>Specifies that a member's value is computed by a static method over the entity, called on every read.
/// </summary>
/// <remarks>The method takes the entity and returns the member's type; it is found by name on
/// <see cref="MethodClass"/>, or on the entity's module when none is named. The member has no setter, and no column
/// unless <see cref="Stored"/> says so, in which case a query can read it and an index can cover it.</remarks>
public partial class ComputedAttribute : EntityModelAttributeBase {
  public ComputedAttribute(string methodName, Type? methodClass = null) {
    MethodName = methodName;
    MethodClass = methodClass;
  }

  public string MethodName { get; }
  public Type? MethodClass { get; }

  /// <summary>Gets or sets a value that indicates whether the member keeps a column, written with the computed value
  /// whenever the entity is inserted or updated.</summary>
  /// <remarks>A read still computes the value; the column serves queries and indexes, and a record that has no
  /// session to compute in. The server has no expression for the column and never recomputes it.</remarks>
  public bool Stored { get; set; }
}

// ----- Behaviour and policy ---------------------------------------------------------------------------------------

/// <summary>Specifies that a member may be set when the entity is created and never updated afterwards.</summary>
public partial class NoUpdateAttribute : EntityModelAttributeBase { }

/// <summary>Specifies that a member may not be assigned at all.</summary>
/// <remarks><c>System.ComponentModel.ReadOnlyAttribute</c> has the same name, so a file importing that namespace
/// needs to qualify one of them.</remarks>
public partial class ReadOnlyAttribute : EntityModelAttributeBase { }

/// <summary>Specifies a write-only member, which never appears in a <c>SELECT</c> output list but may be used in a
/// <c>WHERE</c> clause, as for a password.</summary>
public partial class SecretAttribute : EntityModelAttributeBase { }

/// <summary>Specifies that a database upgrade leaves the column's type and definition unaltered.</summary>
public partial class AsIsAttribute : EntityModelAttributeBase { }

/// <summary>Specifies a static method that validates an entity, run after the built-in validation.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public partial class ValidatorAttribute : EntityModelAttributeBase {
  public ValidatorAttribute(Type methodClass, string methodName) {
    MethodClass = methodClass;
    MethodName = methodName;
  }

  public Type MethodClass { get; }
  public string MethodName { get; }
}

/// <summary>Specifies that authorization checks are skipped for an entity.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public partial class BypassAuthorizationAttribute : EntityModelAttributeBase { }

// Vita attributes not carried over: TransactionId, PropagateUpdatedOn, DoNotTrack, DiscardOnAbort (a transaction-log
// module's), Display (a caption belongs to MIC's metadata, outside the ORM), GrantAccess (MIC's per-record masks
// replace it), PropertyGroup,
// Groups.
