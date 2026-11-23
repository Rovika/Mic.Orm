namespace Mic.Linq.Model;

/// <summary>Defines a model as the query engine runs over it: the entities, a lookup by name, and for one database the
/// table an entity is stored in and the column a member is stored in.</summary>
/// <remarks>The storage lookups answer <see langword="null"/> for an entity the database does not store, a member it
/// carries no column for, or a model with no database behind it; the engine treats either as a query it cannot build
/// here, not as a fault.</remarks>
public interface ILinqDataModel {
  /// <summary>Gets the name of the model.</summary>
  string Name { get; }

  /// <summary>Gets the entities of the model.</summary>
  IReadOnlyList<ILinqEntity> Entities { get; }

  /// <summary>Finds the entity with a name.</summary>
  /// <param name="name">The name of the entity.</param>
  /// <returns>The entity, or <see langword="null"/> if the model has none of that name.</returns>
  /// <remarks>Matching follows the model's own rule. The ORM's entity names are unique only within a schema, so its
  /// lookup throws on a short name two entities share rather than picking one.</remarks>
  ILinqEntity? FindEntity(string name);

  /// <summary>Finds the entity the typed path declares as a CLR type.</summary>
  /// <param name="entityType">The interface an entity set is of.</param>
  /// <returns>The entity, or <see langword="null"/> if the model declares none for the type.</returns>
  /// <remarks>A model with no CLR types behind its entities answers <see langword="null"/>.</remarks>
  ILinqEntity? FindEntity(Type entityType);

  /// <summary>Finds the table an entity is stored in.</summary>
  /// <param name="entity">The entity.</param>
  /// <returns>The table, or <see langword="null"/> if this database does not store the entity.</returns>
  ILinqTable? FindTable(ILinqEntity entity);

  /// <summary>Finds the column a member is stored in.</summary>
  /// <param name="entity">The entity the member is asked for on, since an inherited member is stored in the concrete
  /// entity's table.</param>
  /// <param name="member">The member.</param>
  /// <returns>The column, or <see langword="null"/> if this database carries none for the member.</returns>
  ILinqColumn? FindColumn(ILinqEntity entity, ILinqEntityMember member);

  /// <summary>Creates a transient entity for a query: named, with the given value members in the given order, and
  /// nothing stored behind it.</summary>
  /// <param name="name">The name of the entity.</param>
  /// <param name="members">The value members, in order.</param>
  /// <returns>The entity, finished on return.</returns>
  ILinqEntity CreateTransientEntity(string name, IReadOnlyList<LinqMemberSpec> members);
}

/// <summary>Specifies what a member is as far as a query is concerned, as the normal form of the models' own member
/// kinds.</summary>
public enum LinqMemberKind {
  /// <summary>A single value, which a comparison, an ordering or a projection slot can be.</summary>
  Value,
  /// <summary>An association to one other entity, with a key pairing behind it.</summary>
  Reference,
  /// <summary>A collection of another entity's records.</summary>
  List,
  /// <summary>A member with no data behind it, which cannot appear in a query.</summary>
  Computed,
}

/// <summary>Defines a member of an entity, as a query reads it.</summary>
public interface ILinqEntityMember {
  /// <summary>Gets the name of the member.</summary>
  string Name { get; }

  /// <summary>Gets the CLR type the member holds, with any nullable wrapper unwrapped, or <see langword="null"/> for
  /// a reference or a list.</summary>
  /// <remarks>A <see cref="LinqMemberKind.Computed"/> member has a type: it holds a value, with no data behind it.
  /// </remarks>
  Type? DataType { get; }

  /// <summary>Gets a value that indicates whether the member may be <see langword="null"/>.</summary>
  bool IsNullable { get; }

  /// <summary>Gets one of the enumeration values that specifies what the member is to a query.</summary>
  LinqMemberKind Kind { get; }

  /// <summary>Gets the entity the member belongs to.</summary>
  ILinqEntity? Entity { get; }

  /// <summary>Gets the key pairing behind a reference member, or <see langword="null"/> for every other kind.</summary>
  ILinqReferenceInfo? ReferenceInfo { get; }

  /// <summary>Gets what backs a list member, or <see langword="null"/> for every other kind and for a list with
  /// nothing recorded behind it.</summary>
  /// <remarks>A list with nothing recorded behind it cannot be navigated.</remarks>
  ILinqMemberListInfo? ListInfo { get; }

  /// <summary>Gets a value that indicates whether the member's column is left out of a select of the entity's rows.
  /// </summary>
  /// <remarks>A query that names the member reads its column as any other.</remarks>
  bool IsOnDemand { get; }
}

/// <summary>Defines what a reference member is made of: the target entity and the key pairing that joins to it.
/// </summary>
public interface ILinqReferenceInfo {
  /// <summary>Gets the entity the reference points at.</summary>
  ILinqEntity TargetEntity { get; }

  /// <summary>Gets the referencing entity's own members that hold the key, in order.</summary>
  /// <remarks>Each member is matched against the member at the same position in <see cref="ToMembers"/>, which is the
  /// join condition. An empty list is a navigation declared with no foreign key behind it, from which no join can be
  /// built.</remarks>
  IReadOnlyList<ILinqEntityMember> FromMembers { get; }

  /// <summary>Gets the target's members the key points at, in the same order as <see cref="FromMembers"/>.</summary>
  IReadOnlyList<ILinqEntityMember> ToMembers { get; }
}

/// <summary>Defines what a list member is made of: a query over the entity its inverse belongs to, correlated to the
/// owner through the inverse's key pairing read the other way round.</summary>
public interface ILinqMemberListInfo {
  /// <summary>Gets the entity the list holds. For a many-to-many list this is the far entity, not the link.</summary>
  ILinqEntity TargetEntity { get; }

  /// <summary>Gets a value that indicates whether the list goes through a link entity, on which the inverse is and
  /// from which <see cref="LinkTargetMember"/> leads on to the target.</summary>
  bool IsManyToMany { get; }

  /// <summary>Gets the reference, on the target or on the link, that points back at the owner, or
  /// <see langword="null"/> if the model does not say.</summary>
  /// <remarks>A list whose inverse the model does not say cannot be navigated.</remarks>
  ILinqEntityMember? InverseMember { get; }

  /// <summary>Gets the entity in the middle of a many-to-many list, whose two references the list traverses, or
  /// <see langword="null"/> for any other list.</summary>
  ILinqEntity? LinkEntity { get; }

  /// <summary>Gets the reference, on the link entity of a many-to-many list, that points at the target, or
  /// <see langword="null"/> for any other list.</summary>
  /// <remarks>A link with two references to the target cannot be told apart by looking, so the model says which one
  /// it is.</remarks>
  ILinqEntityMember? LinkTargetMember { get; }

  /// <summary>Gets the order the list's rows come in when nothing else orders them: the member's own, else the
  /// target's, else its primary key; empty where the model declares none.</summary>
  /// <remarks>A term may name a reference, which orders by its foreign-key columns.</remarks>
  IReadOnlyList<LinqOrderTerm> DefaultOrder { get; }
}

/// <summary>Represents one term of an ordering: a member, and whether it orders from the greatest value down.</summary>
/// <param name="Member">The member to order by.</param>
/// <param name="Descending"><see langword="true"/> to order from the greatest value down; otherwise,
/// <see langword="false"/>.</param>
public readonly record struct LinqOrderTerm(ILinqEntityMember Member, bool Descending = false);

/// <summary>Defines an entity: a set of records, and the members that make one up.</summary>
public interface ILinqEntity {
  /// <summary>Gets the name of the entity.</summary>
  string Name { get; }

  /// <summary>Gets the members of the entity.</summary>
  IReadOnlyList<ILinqEntityMember> Members { get; }

  /// <summary>Finds the member with a name, ignoring case.</summary>
  /// <param name="name">The name of the member.</param>
  /// <returns>The member, or <see langword="null"/> if the entity has none of that name.</returns>
  ILinqEntityMember? FindMember(string name);

  /// <summary>Gets the members that identify a record, in key order; empty for an entity with no primary key, as a
  /// view's or a function's rows may be.</summary>
  /// <remarks>A key declared over a reference member reports the foreign key's columns, not the reference. Rows
  /// with no key are read, filtered and ordered by their members; the engine refuses, naming the entity, a query
  /// that compares, joins or groups by such a row itself.</remarks>
  IReadOnlyList<ILinqEntityMember> PrimaryKeyMembers { get; }

  /// <summary>Gets a value that indicates whether the entity is transient: one the engine asked the model to make for
  /// a query through <see cref="ILinqDataModel.CreateTransientEntity"/>, such as a projection's rows, a grouping's
  /// keys or the rows of a JSON array.</summary>
  /// <remarks>Nothing stores a transient entity; a row of it is an <see cref="ILinqRow"/>, and its members are
  /// values.</remarks>
  bool IsTransient { get; }

  /// <summary>Gets a value that indicates whether the host holds the entity whole in memory, so that a query over it
  /// runs there.</summary>
  bool IsCached { get; }

  /// <summary>Gets a value that indicates whether a mutation statement cannot target the entity, its rows being a
  /// query's: a view's or a table function's.</summary>
  bool IsReadOnly { get; }
}

/// <summary>Represents what a member of a transient entity is made from: its name, the type of its value, and
/// whether it may be <see langword="null"/>.</summary>
/// <param name="Name">The name of the member.</param>
/// <param name="DataType">The type of the value.</param>
/// <param name="IsNullable"><see langword="true"/> if the value may be <see langword="null"/>; otherwise,
/// <see langword="false"/>.</param>
public readonly record struct LinqMemberSpec(string Name, Type DataType, bool IsNullable = true);

// ----- the storage side ---------------------------------------------------------------------------------------------------

/// <summary>Defines a table as a query names it: its name, its schema, its columns, and the entity it holds.</summary>
public interface ILinqTable {
  /// <summary>Gets the name of the table.</summary>
  string Name { get; }

  /// <summary>Gets the schema of the table, or <see langword="null"/> if the database has none.</summary>
  string? Schema { get; }

  /// <summary>Gets the columns of the table.</summary>
  IReadOnlyList<ILinqColumn> Columns { get; }

  /// <summary>Gets the entity whose records the table stores, or <see langword="null"/> for a table no entity maps
  /// to.</summary>
  ILinqEntity? Entity { get; }

  /// <summary>Gets the conditions extensions attach to every read of the table, such as a tenant's rows or the rows
  /// not soft-deleted, one per extension, each a template prepared once over a placeholder row named
  /// <see cref="Nodes.QueryTreeTemplate.RowPlaceholder"/>; empty if the entity has none.</summary>
  /// <remarks>The translator instantiates the templates for each table node a query reads, with that node's row, and
  /// ANDs them, leaving out those a query says to ignore. A condition's shape is the model's; only the value a
  /// context parameter in it reads is the call's.</remarks>
  IReadOnlyList<LinqTableFilter> Filters { get; }
}

/// <summary>Represents one extension's filter on a table: the condition, and the extension that attached it.</summary>
/// <param name="Owner">The type of the extension that attached the filter, or <see langword="null"/> for a filter
/// attached by the application directly.</param>
/// <param name="Template">The condition, as a template over the placeholder row.</param>
public sealed record LinqTableFilter(Type? Owner, Nodes.QueryTreeTemplate Template);

/// <summary>Defines a column as a query names it: its name, its type, its table, and the member it holds.</summary>
public interface ILinqColumn {
  /// <summary>Gets the name of the column.</summary>
  string Name { get; }

  /// <summary>Gets the CLR type a value of the column reads as, or <see langword="null"/> if the model knows none.
  /// </summary>
  Type? DataType { get; }

  /// <summary>Gets a value that indicates whether the column may hold <see langword="null"/>.</summary>
  bool IsNullable { get; }

  /// <summary>Gets the table the column belongs to.</summary>
  ILinqTable Table { get; }

  /// <summary>Gets the member the column stores, or <see langword="null"/> for a column no member maps to.</summary>
  ILinqEntityMember? Member { get; }
}

/// <summary>Defines a row of a shape the engine synthesised, such as a projection's row or a composite group key, as
/// the engine reads and writes it.</summary>
/// <remarks>The host supplies the row, through <see cref="Translation.ILinqCallContext.CreateRow"/>.</remarks>
public interface ILinqRow {
  /// <summary>Gets the shape the row is a row of.</summary>
  ILinqEntity Entity { get; }

  /// <summary>Gets the value of a member.</summary>
  /// <param name="member">The member.</param>
  /// <returns>The value.</returns>
  object? GetValue(ILinqEntityMember member);

  /// <summary>Sets the value of a member.</summary>
  /// <param name="member">The member.</param>
  /// <param name="value">The value.</param>
  void SetValue(ILinqEntityMember member, object? value);

  /// <summary>Gets the value of the member with a name.</summary>
  /// <param name="name">The name of the member.</param>
  /// <returns>The value.</returns>
  object? GetValue(string name);
}
