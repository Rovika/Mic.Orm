
namespace Mic.Orm.Db.Provider;

/// <summary>Specifies the capabilities a provider declares for its server, which the base gates its own calls on.
/// </summary>
[Flags]
public enum DbFeatures : long {
  None = 0,

  /// <summary>Named schemas within a database.</summary>
  Schemas = 1,

  /// <summary>Enforced foreign key constraints.</summary>
  ReferentialConstraints = 1 << 1,

  /// <summary>Clustered storage, where the table's rows are the index leaf and the engine maintains the physical
  /// order, as with SQL Server's clustered index and InnoDB's primary key.</summary>
  ClusteredStorage = 1 << 2,

  /// <summary>A clustering marker: an index can be designated as the clustering one and the server reorders the
  /// table to match once, on demand, without maintaining the order afterwards, as PostgreSQL does.</summary>
  /// <remarks>Issuing the reorder takes an exclusive lock and rewrites the table, so it is a decision separate from
  /// recording the marker.</remarks>
  ClusteringMarker = 1 << 13,

  /// <summary>Tables without clustered storage; absent where the engine forces it, as InnoDB does.</summary>
  HeapTables = 1 << 3,

  /// <summary>A sort direction per column within an index.</summary>
  OrderedColumnsInIndexes = 1 << 4,

  /// <summary>Index columns carried in the leaf but not part of the key.</summary>
  IncludeColumnsInIndexes = 1 << 5,

  /// <summary>Partial or filtered indexes.</summary>
  FilterInIndexes = 1 << 6,

  Sequences = 1 << 7,
  Views = 1 << 8,

  /// <summary>An index the server creates on every foreign key by itself, as InnoDB does.</summary>
  /// <remarks>With this flag, generation adds no index of its own and introspection does not report the server's as
  /// a difference.</remarks>
  ForeignKeysAutoIndexed = 1 << 9,

  /// <summary>A foreign key that may only target a primary key; absent, any unique key will do.</summary>
  ForeignKeyMustTargetPrimaryKey = 1 << 10,

  /// <summary>Constraint checking deferred to the end of a transaction, which makes a circular reference between
  /// rows insertable.</summary>
  DeferredConstraintCheck = 1 << 11,

  // ----- what an upgrade can do to a table that already exists ------------------------------------------------------

  /// <summary>A column's type, nullability or default changed in place.</summary>
  AlterColumn = 1 << 14,

  /// <summary>A column alteration that restates the whole definition rather than amending one part of it, as
  /// MySQL's <c>MODIFY COLUMN</c> does.</summary>
  /// <remarks>A server that declares this gets one statement built from the column definition instead of one per
  /// changed part.</remarks>
  AlterColumnRestatesDefinition = 1 << 20,

  /// <summary>A column default that is a separate named constraint rather than a property of the column, as on SQL
  /// Server.</summary>
  /// <remarks>Such a default is dropped by its server-generated name, which introspection reads, and is stated
  /// separately from a restated column definition. Inside a CREATE TABLE it is written inline.</remarks>
  NamedDefaultConstraints = 1 << 22,

  /// <summary>An identity column that must already be part of a key when the table is created, as on MySQL.</summary>
  /// <remarks>For such a table the primary key goes inside the CREATE TABLE statement.</remarks>
  IdentityRequiresInlineKey = 1 << 21,

  /// <summary>A column dropped.</summary>
  DropColumn = 1 << 15,

  /// <summary>Keys and reference constraints added and dropped after the table exists; absent, they have to be part
  /// of CREATE TABLE and an existing table's constraints cannot be changed.</summary>
  AddConstraintsAfterCreate = 1 << 16,

  /// <summary>The combination of <see cref="AlterColumn"/>, <see cref="DropColumn"/> and
  /// <see cref="AddConstraintsAfterCreate"/>.</summary>
  FullAlterTable = AlterColumn | DropColumn | AddConstraintsAfterCreate,

  /// <summary>A column whose value the server assigns on insert, an identity or auto-increment column.</summary>
  /// <remarks>Absent, the designation is dropped with a warning and the application supplies the value.</remarks>
  IdentityColumns = 1 << 19,

  /// <summary>A foreign key constraint that keeps the name it was created with and reports it back.</summary>
  /// <remarks>Absent, the comparer matches reference constraints by their columns and their target.</remarks>
  NamedRefConstraints = 1 << 18,

  /// <summary>A named lock held for the life of a connection, unrelated to any row or table, such as
  /// <c>pg_try_advisory_lock</c>, <c>sp_getapplock</c>, <c>GET_LOCK</c> or <c>DBMS_LOCK</c>.</summary>
  /// <remarks>The upgrade lock needs one; a row lock cannot serve on a database with no tables yet.</remarks>
  SessionLocks = 1 << 17,

  /// <summary>Comments kept in the definitions the server stores, so a definition hash can be embedded in an
  /// object's own SQL.</summary>
  ServerPreservesComments = 1 << 12,

  /// <summary>A Guid stored as 16 bytes rather than as text, on a server with no Guid type of its own.</summary>
  /// <remarks>The bytes are in RFC order, not .NET's default layout, so a time-ordered Guid keeps its ordering in the
  /// column.</remarks>
  BinaryGuids = 1 << 23,

  /// <summary>A multi-row insert that gives each row its own generated key.</summary>
  /// <remarks>Oracle's <c>INSERT ALL</c> evaluates a sequence once for the whole statement. Absent, inserts of rows
  /// with a generated key stay one statement per row under <see cref="SaveOptions.InsertAndForget"/>.</remarks>
  BulkInsertAdvancesIdentity = 1 << 25,

  /// <summary>A whole collection bound as one parameter, so an <c>IN</c> list needs no values in the text.</summary>
  /// <remarks>A provider that declares this must also spell the comparison the way its array parameter needs, in
  /// <see cref="SqlDialect.BuildInList"/>; PostgreSQL compares with <c>= ANY(…)</c>, not <c>IN (…)</c>.</remarks>
  ArrayParameters = 1 << 26,

  /// <summary>The rows of a JSON parameter as a table source, such as <c>jsonb_to_recordset</c>, <c>OPENJSON</c>,
  /// <c>JSON_TABLE</c> or <c>json_each</c>, so a set of key tuples travels as one bound value. See
  /// <see cref="SqlDialect.JsonRowsSource"/>.</summary>
  JsonRows = 1 << 27,

  /// <summary>A full-text match and rank over an entity's full-text index, such as <c>@@</c> a <c>tsquery</c>,
  /// <c>MATCH … AGAINST</c> or <c>CONTAINS</c>, and the index the statement needs. See
  /// <see cref="SqlDialect.BuildTextMatch"/>.</summary>
  FullText = 1 << 28,

  /// <summary>A generated key carried through a batch: an insert leaves it in a slot of the command, an output
  /// parameter or a session variable, and a later statement of the same command reads the slot where the key goes.
  /// See <see cref="SqlDialect.BatchCaptureIdentity"/>.</summary>
  /// <remarks>Absent, an insert whose key is wanted runs alone, one round trip per row.</remarks>
  IdentityCarriedInBatch = 1 << 29,

  /// <summary>A row held for writing by the statement that reads it for the rest of the transaction, such as
  /// <c>FOR UPDATE</c> or <c>WITH (UPDLOCK)</c>, and a transaction begun at a level that reads a consistent snapshot.
  /// </summary>
  /// <remarks>A server without it accepts <c>LoadFlags.WriteLock</c> and <c>LoadFlags.ReadLock</c> and reads
  /// plainly.</remarks>
  RowLocks = 1 << 30,

  /// <summary>A user variable written with the parameter prefix, as MySQL's <c>@total</c> is, so that a hand-written
  /// statement's <c>@name</c> with no value supplied is one and is left in the text.</summary>
  ParameterPrefixSharedWithVariables = 1L << 31,

  /// <summary>A view whose rows the server stores, <c>CREATE MATERIALIZED VIEW</c>.</summary>
  /// <remarks>Absent, a view declared materialized is an ordinary view, with a warning.</remarks>
  MaterializedViews = 1L << 32,

  /// <summary>A locked select takes no row limit: a query with <c>Take</c> or <c>Skip</c> under
  /// <c>QueryOptions.LockForUpdate</c> or <c>SkipLocked</c> is refused at translation.</summary>
  /// <remarks>Oracle rewrites <c>OFFSET … FETCH</c> into an inline view and refuses <c>FOR UPDATE</c> over it
  /// (<c>ORA-02014</c>).</remarks>
  NoRowLimitWithLock = 1L << 33,

  /// <summary>Rows written into a table over a channel of the server's own that is faster than an insert statement:
  /// <see cref="DbProvider.BulkInsertAsync"/>.</summary>
  /// <remarks>Declared where the channel measured faster than the multi-row insert. A temp table is filled through
  /// it.</remarks>
  BulkInsert = 1L << 34,

  // ----- traits of one server; the suffix of a flag names the server that declares it ------------------------------

  /// <summary>Rows as a table source from a bound parameter only: the items of a JSON array held in a column are
  /// refused at translation.</summary>
  /// <remarks>Db2 spreads no JSON array into rows, and takes the rows of a parameter as XML. See
  /// <see cref="SqlDialect.JsonRowsSource"/>.</remarks>
  JsonRowsOfParameterOnlyDb2 = 1L << 40,

  /// <summary>A null for a Guid column written as the literal rather than bound.</summary>
  /// <remarks>Db2 holds a Guid as bytes, and IBM's driver sends a bound null as text, which a binary column refuses
  /// (<c>SQL0301N</c>).</remarks>
  NullGuidAsLiteralDb2 = 1L << 41,

  /// <summary>A delete's <c>key IN (select)</c> that reads the finding select through a derived table rather than
  /// directly.</summary>
  /// <remarks>MySQL refuses a sub-query that reads the table it deletes from (error 1093), and accepts the same
  /// select as a derived table, which is materialized first.</remarks>
  DeleteSubQueryWrappedMySql = 1L << 42,

  /// <summary>An <c>EXISTS</c> over the rows of a document read off the outer row written as a count compared to
  /// zero.</summary>
  /// <remarks>MySQL 8.4 answers <c>EXISTS</c> over a <c>JSON_TABLE</c> that reads the outer row with nothing,
  /// whatever the rows hold; a <c>COUNT(*)</c> over the same source is right.</remarks>
  CountsJsonRowsForExistsMySql = 1L << 43,

  /// <summary>A spatial index that wants its column declared <c>NOT NULL</c>, so that a nullable point gets no
  /// index.</summary>
  SpatialIndexNeedsNotNullMySql = 1L << 44,

  /// <summary>An identity that is a shape and not a marker: the server makes one of a column by its type and its key
  /// alone.</summary>
  /// <remarks>The SQLite loader reads the shape, so a key of that shape is read as an identity whether the model
  /// declares one or not, and the comparison does not report it.</remarks>
  IdentityIsImpliedSqlite = 1L << 45,

  /// <summary>A temp table whose columns take no <c>NOT NULL</c>, so every column of one is declared nullable.
  /// </summary>
  /// <remarks>Oracle's private temporary table takes no constraint (<c>ORA-14451</c>).</remarks>
  NoTempTableNotNullOracle = 1L << 46,
}

/// <summary>Specifies how a server stores a computed column.</summary>
public enum ComputedStorage {
  /// <summary>Stored or virtual as the member declares.</summary>
  AsDeclared,
  /// <summary>Always stored, on a server with no virtual generated columns.</summary>
  Stored,
  /// <summary>Always virtual, on a server that cannot store one or cannot add a stored one to a table.</summary>
  Virtual,
}

/// <summary>Specifies where a server keeps the hash of a view's definition that MIC stores beside the view, so a
/// later run can tell whether the definition changed.</summary>
public enum ViewHashCarrier {
  /// <summary>A comment inside the defining select, which the server keeps verbatim in the definition it reports.
  /// </summary>
  TextComment,
  /// <summary>A comment on the view itself, <c>COMMENT ON</c>, where the server regenerates the definition's text.
  /// </summary>
  Comment,
  /// <summary>A string literal in a condition wrapped around the select, where the server keeps neither comments in
  /// the definition nor comments on views.</summary>
  Literal,
}

/// <summary>Specifies how a server folds an unquoted identifier.</summary>
public enum DbIdentifierCase {
  /// <summary>An identifier stored as written, as on SQL Server.</summary>
  Preserve,
  /// <summary>An identifier folded to lower case, as on PostgreSQL.</summary>
  Lower,
  /// <summary>An identifier folded to upper case, as on Oracle.</summary>
  Upper,
}

/// <summary>Specifies what arguments a database type takes, whether it is unbounded, and whether it is legacy.
/// </summary>
[Flags]
public enum DbTypeFlags {
  None = 0,
  /// <summary>A type that takes a length, such as <c>varchar(50)</c>.</summary>
  TakesSize = 1,
  /// <summary>A type that takes a precision and a scale, such as <c>numeric(18,4)</c>.</summary>
  TakesPrecisionScale = 1 << 1,
  /// <summary>An unbounded type, such as <c>text</c>, <c>bytea</c> or <c>nvarchar(max)</c>.</summary>
  Unlimited = 1 << 2,

  /// <summary>A type its own vendor has superseded, such as SQL Server's <c>text</c>, <c>ntext</c> and <c>image</c>.
  /// </summary>
  /// <remarks>A legacy type is resolvable by name, for a type spec that names it and for introspection, but is never
  /// chosen by forward mapping. The judgement is per vendor: PostgreSQL's <c>text</c> is not legacy.</remarks>
  Legacy = 1 << 3,
}

/// <summary>Specifies the semantic category of a database type, so the base can ask for a category without knowing a
/// vendor's spelling of it.</summary>
public enum DbSpecialType {
  None,
  String,
  StringUnlimited,
  Binary,
  BinaryUnlimited,
  /// <summary>The server's JSON type, such as <c>jsonb</c> or <c>json</c>, for a member held as JSON.</summary>
  /// <remarks>A server with no JSON type registers nothing here, and the member takes the unlimited text type.
  /// </remarks>
  Json,
  /// <summary>The server's vector type, such as pgvector's <c>vector</c>, for a member held as a vector; its size
  /// is the number of dimensions.</summary>
  /// <remarks>A server with no vector type registers nothing here, and the member is an error of the model.
  /// </remarks>
  Vector,
}

/// <summary>Specifies how a full-text match and a rank reach the entity's full-text index on a server. See
/// <see cref="SqlDialect.FullTextJoin"/> and <see cref="SqlDialect.BuildTextJoin"/>.</summary>
public enum FullTextJoinKind {
  /// <summary>A match that is a predicate over the row and a rank that is an expression over it, with nothing
  /// joined, as on PostgreSQL, MySQL and Oracle.</summary>
  None,
  /// <summary>An index that is a table of its own, joined to the entity's table by the key once per table, with the
  /// match and the rank both naming the join's alias, as with SQLite's FTS5 mirror.</summary>
  Mirror,
  /// <summary>A match that is a predicate over the row and a rank read off a rowset of the query's matches, joined
  /// to the entity's table by the key once per query, as with SQL Server's <c>CONTAINSTABLE</c>.</summary>
  RankedRows,
}
