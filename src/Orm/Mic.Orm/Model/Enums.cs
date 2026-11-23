namespace Mic.Orm.Model;

/// <summary>Specifies how the ORM handles and persists a member.</summary>
/// <remarks>Independent of what the member's CLR type is: a JSON-backed member is an object to the application and a
/// column underneath.</remarks>
public enum MemberKind {
  /// <summary>A scalar persisted in a column of its own.</summary>
  Column,
  /// <summary>An association to another entity, with a foreign key underneath.</summary>
  Reference,
  /// <summary>A collection: one-to-many, or many-to-many through a link entity.</summary>
  List,
  /// <summary>An in-memory member: computed, or a value the database never sees.</summary>
  Transient,
}

/// <summary>Specifies what the ORM knows about a member beyond its name, type and nullability.</summary>
[Flags]
public enum MemberFlags {
  None = 0,
  /// <summary>A member that is part of the entity's primary key.</summary>
  PrimaryKey = 1,
  /// <summary>A member whose value the database produces; the ORM never writes it and its setter throws.</summary>
  DbGenerated = 1 << 1,
  /// <summary>A member with no length limit, such as <c>nvarchar(max)</c>; its size does not apply.</summary>
  Unlimited = 1 << 2,
  /// <summary>A member settable when the entity is created and never afterwards.</summary>
  NoUpdate = 1 << 3,

  /// <summary>A column an upgrade never alters, whatever it looks like in the database.</summary>
  AsIs = 1 << 4,

  /// <summary>A string member whose oversized values are cut rather than refused, from
  /// <c>[Size(n, SizeOptions.AutoTrim)]</c>.</summary>
  AutoTrim = 1 << 5,

  /// <summary>A JSON column, from <c>[Json]</c>: the member's value is an object serialized to JSON on its way to the
  /// server and read back from it.</summary>
  /// <remarks>The column is the server's JSON type where it has one, its unlimited text where it has not.</remarks>
  Json = 1 << 6,

  /// <summary>A column of a unique key other than the primary key.</summary>
  /// <remarks>A session keeps the value such a column had in its row when the column is first changed, so that a
  /// save knows which unique values it frees.</remarks>
  InUniqueKey = 1 << 7,

  /// <summary>A vector column, from <c>[Vector(n)]</c>: the member's value is an array of <c>n</c> floats held in
  /// the server's vector type.</summary>
  /// <remarks>The number of dimensions is the member's size.</remarks>
  Vector = 1 << 8,

  /// <summary>A column left out of a read of the entity, from <c>[OnDemand]</c>.</summary>
  /// <remarks>A record read from the database does not hold the member until it is asked for.</remarks>
  OnDemand = 1 << 9,
}

/// <summary>Specifies how a list is served: by a foreign key on the target itself, or on a link entity between the
/// two.</summary>
public enum ListKind { OneToMany, ManyToMany }

/// <summary>Specifies what the model works out about an entity type from the shape of the reference graph.</summary>
/// <remarks>Every flag is set by one pass over the type graph at model build.</remarks>
[Flags]
public enum EntityTypeFlags {
  None = 0,

  /// <summary>A type some other entity points at; without it, the type's rows can go to either end of a save.</summary>
  Referenced = 1,

  /// <summary>A type whose rows need ordering among themselves, because it shares a topological index with others in
  /// a reference cycle or references itself.</summary>
  InTopologyGroup = 1 << 1,

  /// <summary>A type a member of which references the type itself, so that a delete of its rows cannot collapse into
  /// one statement.</summary>
  SelfReferencing = 1 << 2,

  /// <summary>A type with a reference to an identity-keyed type, whose rows get their foreign keys brought into step
  /// after a batch that carried generated ids.</summary>
  ReferencesIdentity = 1 << 3,
}

// `KeyFlags` lives in `Mic.Orm.Db`, with the vocabulary of the database model.

/// <summary>Specifies how a view is defined: by a query the engine translates, by SQL text written per server, or
/// not at all, since the view exists in the database already.</summary>
public enum ViewDefinitionKind {
  /// <summary>A query over the model's entities, translated into the view's SQL when the db model is built.</summary>
  Query,
  /// <summary>SQL text the module wrote, one per server kind.</summary>
  Sql,
  /// <summary>A view that exists in the database and that MIC never creates, changes or drops.</summary>
  Existing,
}
