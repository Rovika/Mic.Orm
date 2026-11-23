namespace Mic.Orm;

/// <summary>Specifies how a session's database calls are made.</summary>
/// <remarks>A synchronous member of a session sets <see cref="Sync"/> for its duration; the same code then runs with
/// the driver's synchronous calls, so that every task on the way is complete when it is returned and nothing waits
/// on a thread.</remarks>
public enum RunMode {
  /// <summary>The driver's asynchronous calls, awaited.</summary>
  Async,

  /// <summary>The driver's synchronous calls, made on the calling thread.</summary>
  Sync,
}

/// <summary>Specifies the tiers of the application that hold an entity's rows whole in memory.</summary>
/// <remarks>Declared with <see cref="EntityApp.SetCached"/> before the model is built and never changed at runtime.
/// The two tiers are independent flags; an entity cached in both is declared with both.</remarks>
[Flags]
public enum CacheType {
  NoCache = 0,

  /// <summary>The rows are held in the application server, the process that runs the ORM, so a read of one never
  /// reaches the database server.</summary>
  Server = 1,

  /// <summary>The rows are held in the UI client. The ORM carries the mark in the model for the client runtime and
  /// holds nothing itself.</summary>
  Client = 1 << 1,
}

// ----- the session ------------------------------------------------------------------------------------------------

/// <summary>Specifies how <c>IEntitySession.GetEntity</c> answers when the entity is not already loaded.</summary>
[Flags]
public enum LoadFlags {
  /// <summary>The loaded entity, or <see langword="null"/> if it is not loaded.</summary>
  None = 0,

  /// <summary>A stub if the entity is not loaded: an entity with only its primary key set, whose other members load
  /// when something reads them.</summary>
  Stub = 1,

  /// <summary>The row read from the server, whatever the entity cache holds; a tracked entity is still returned
  /// first.</summary>
  SkipEntityCache = 1 << 2,

  /// <summary>A read lock on the row.</summary>
  ReadLock = 1 << 4,
  /// <summary>A write lock on the row.</summary>
  WriteLock = 1 << 5,

  Default = None,
}

/// <summary>Specifies how a session departs from its defaults, given when the session is opened.</summary>
[Flags]
public enum EntitySessionOptions {
  None = 0,

  /// <summary>No loading of the same member of every sibling the session holds when one is asked for; a stub, a
  /// list or a queryable property loads alone.</summary>
  /// <remarks>Smart loading changes how many statements run, never what an application sees.</remarks>
  DisableSmartLoad = 1,

  /// <summary>No batching of a save's statements into commands; one command per statement.</summary>
  /// <remarks>A failed save then names the statement that failed. The save runs slower.</remarks>
  DisableBatchMode = 1 << 1,

  /// <summary>The session's operations take turns: one started while another runs waits for it to end.</summary>
  /// <remarks>For a session kept across the events of a user interface, or handed to another thread, where an
  /// operation can start before the previous one ends. A session opened for one request needs none of it. A
  /// synchronous member called on a user-interface thread while an operation runs cannot wait and throws
  /// <see cref="InvalidOperationException"/>. The turns are of single calls: a read, a change and a save made by
  /// one handler can have another handler's call between them. An entity is not made safe to change from two
  /// threads at once.</remarks>
  AddThreadSafety = 1 << 2,
}

/// <summary>Specifies what a save does beyond writing.</summary>
[Flags]
public enum SaveOptions {
  None = 0,

  /// <summary>A save that writes the records, reads back only what the save itself needs, and stops tracking them.
  /// </summary>
  /// <remarks>For a save nobody reads afterwards, such as log rows or an import. Only the keys the save needs are read
  /// back: a parent's server-made key that a child of the same save points at. Every other identity insert asks for no
  /// key and can join a multi-row statement. The records, those parents included, are evicted and become
  /// <c>EntityStatus.Fantom</c>.</remarks>
  InsertAndForget = 1,
}

/// <summary>Specifies whether a user is authenticated, and what kind of account it is.</summary>
public enum UserKind {
  /// <summary>An unauthenticated user.</summary>
  Anonymous = 0,
  /// <summary>An authenticated user.</summary>
  AuthenticatedUser = 1,
  /// <summary>A virtual system account, with no restriction on what it may read or write.</summary>
  System = 2,
}

// ----- what an entity declaration says ------------------------------------------------------------------------------

/// <summary>Specifies the layout of the Guids the ORM mints for keys, session ids and transaction ids.</summary>
/// <remarks>The layout is permanent in effect: changing it does not rewrite stored keys, so a table then holds a mix.
/// The scheme a database was last upgraded under is recorded in its <c>DbInfo</c> row.</remarks>
public enum GuidScheme {
  /// <summary>The scheme the database's provider declares: <see cref="V7"/> on a server that compares a Guid by its
  /// bytes first to last, <see cref="V8SqlServer"/> on SQL Server.</summary>
  Auto,
  /// <summary>RFC 9562 version 7: the Unix millisecond in the first six bytes, random after; ordered by time on a
  /// server that compares bytes first to last.</summary>
  V7,
  /// <summary>RFC 9562 version 8 in MIC's SQL Server layout: the Unix millisecond in the last six bytes, which SQL
  /// Server compares first; ordered by time there, random to any other reader.</summary>
  V8SqlServer,
  /// <summary>RFC 9562 version 4: random throughout, for scattered inserts or a key that must not date its row.
  /// </summary>
  V4,
}

/// <summary>Specifies the kinds of automatically assigned value.</summary>
public enum AutoType {
  None,
  /// <summary>A new Guid on creation, generated by the ORM and never by the database.</summary>
  /// <remarks>The default scheme is time-ordered (RFC 9562 v7/v8), so a graph can be wired up in memory before
  /// anything is saved.</remarks>
  NewGuid,
  CreatedOn,
  UpdatedOn,
  CreatedBy,
  UpdatedBy,
  CreatedById,
  UpdatedById,
  /// <summary>A value produced by the database on insert.</summary>
  Identity,
  /// <summary>A row version (timestamp), incremented by the database on update.</summary>
  RowVersion,
  /// <summary>A value produced by a named database sequence.</summary>
  Sequence,
}

/// <summary>Specifies the metric the distance between two vectors is measured by.</summary>
public enum VectorMetric {
  /// <summary>One minus the cosine of the angle between the vectors: 0 for the same direction, 2 for the opposite.
  /// </summary>
  Cosine,
  /// <summary>The length of the straight line between the vectors.</summary>
  Euclidean,
  /// <summary>The dot product of the vectors, negated, so that a smaller value is a nearer vector as with the other
  /// metrics.</summary>
  DotProduct,
}

/// <summary>Specifies the options of <see cref="SizeAttribute"/>.</summary>
[Flags]
public enum SizeOptions {
  None = 0,
  /// <summary>An oversized value is trimmed instead of rejected, as for log and generated messages.</summary>
  /// <remarks>Without it, exceeding the size is an error.</remarks>
  AutoTrim = 1,
}

/// <summary>Specifies what happens to a connection between commands.</summary>
public enum DbConnectionReuseMode {
  /// <summary>A close as soon as the operation is done.</summary>
  NoReuse,

  /// <summary>An open connection kept for the next command.</summary>
  /// <remarks>Returning a connection to the pool costs a session reset on the server. With this mode the pool is
  /// sized for concurrent requests rather than concurrent queries, and an idle open connection still counts against
  /// the server's connection limit, so a web application closes the connection at the end of each request.</remarks>
  KeepOpen,
}


/// <summary>Specifies what a record is to the database and which statement a save turns it into.</summary>
/// <remarks>The status says which kind of statement a record needs; the
/// <see cref="Runtime.EntityRecord.Modified"/> mask says which members an update carries. A new record with every
/// member set and a loaded one with every member changed have the same mask and different statuses.</remarks>
public enum EntityStatus {
  /// <summary>A record created by the application that the database has never seen; a save inserts it.</summary>
  New,
  /// <summary>A record read from the database and unchanged since.</summary>
  Loaded,
  /// <summary>A record read from the database and written to; a save updates its changed members.</summary>
  Modified,
  /// <summary>A record marked for removal; a save deletes it.</summary>
  Deleted,
  /// <summary>A record holding its primary key and nothing else, made from a foreign key and not fetched.</summary>
  Stub,
  /// <summary>A record saved and no longer tracked by the session, so that nothing more is written for it.</summary>
  /// <remarks>A deleted record reaches this status after the save, and so does one saved under
  /// <see cref="SaveOptions.InsertAndForget"/>; <see cref="Runtime.EntityRecord.StatusBeforeSave"/> says which. Its
  /// values remain readable, writing one throws, and it cannot be expanded.</remarks>
  Fantom,
}

/// <summary>Specifies when a scheduled statement runs within the save's transaction.</summary>
/// <remarks>Clearing rows the save is about to recreate belongs at the start; a recalculation over what the save just
/// wrote belongs at the end.</remarks>
public enum CommandSchedule {
  /// <summary>After the save's own statements, before the commit. This is the default.</summary>
  TransactionEnd = 0,

  /// <summary>Before the save's own statements.</summary>
  TransactionStart,
}

/// <summary>Specifies what a view is beyond its definition.</summary>
[Flags]
public enum ViewOptions {
  None = 0,
  /// <summary>The server stores the view's rows rather than its query, where it can; elsewhere an ordinary view.
  /// </summary>
  Materialized = 1,
}
