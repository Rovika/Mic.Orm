namespace Mic.Orm.Db;

/// <summary>Specifies the server vendor a database model is for.</summary>
public enum DbServerKind { Postgres, MySql, MsSql, SQLite, Oracle, SybaseAse, Db2 }

/// <summary>Specifies what a column is beyond its type and nullability, limited to facts a database holds.</summary>
[Flags]
public enum DbColumnFlags {
  None = 0,
  /// <summary>An identity or auto-increment column, whose value the server assigns on insert.</summary>
  Identity = 1,
  /// <summary>A row version or timestamp column, maintained by the server on update.</summary>
  RowVersion = 1 << 1,
  /// <summary>A column the server computes from an expression over the row, which nothing writes.</summary>
  Computed = 1 << 2,
  /// <summary>A computed column whose value the server stores rather than computes on each read.</summary>
  Stored = 1 << 3,
}

/// <summary>Specifies what a key is. The flags combine: a foreign key's own columns are normally indexed too, and a
/// primary key may also be the clustering key.</summary>
/// <remarks>A foreign key relationship is not a flag; it is a separate constraint object pairing two keys.
/// <see cref="ForeignKey"/> marks the referencing table's own columns, and that key's name is the constraint's name.
/// </remarks>
[Flags]
public enum KeyFlags {
  None = 0,
  PrimaryKey = 1,
  Unique = 1 << 1,
  Index = 1 << 2,
  ForeignKey = 1 << 3,
  /// <summary>The key that decides physical placement, which is not assumed to be the primary key.</summary>
  Clustered = 1 << 4,
}

/// <summary>Specifies the access method an index uses: a b-tree, a hash, or one of PostgreSQL's specialised
/// structures.</summary>
/// <remarks>The values are a union across vendors, so most are meaningless on most servers. A provider declares the
/// ones it has; one it does not have degrades to the provider's default with a warning. <see cref="Default"/> means
/// no method was stated, and the model builder resolves it to the provider's default before two models are compared.
/// SQL Server's columnstore, XML and full-text indexes are their own statements, not access methods, and are not
/// represented.</remarks>
public enum DbIndexType {
  /// <summary>No stated access method, resolved to the provider's default while the database model is built.</summary>
  Default = 0,

  /// <summary>The ordinary ordered index, which every server has.</summary>
  BTree,

  /// <summary>An equality-only index, with no ordering and no ranges.</summary>
  /// <remarks>PostgreSQL has it; MySQL accepts the syntax and ignores it on InnoDB.</remarks>
  Hash,

  /// <summary>PostgreSQL's extensible tree, for geometry, ranges and full text.</summary>
  Gist,

  /// <summary>PostgreSQL's inverted index, with one entry per value inside a composite column: arrays, jsonb, text
  /// search.</summary>
  Gin,

  /// <summary>PostgreSQL's space-partitioned GiST.</summary>
  SpGist,

  /// <summary>PostgreSQL's block-range index, useful only where the physical order tracks the value.</summary>
  Brin,

  /// <summary>A text-search index: MySQL's <c>FULLTEXT</c>.</summary>
  /// <remarks>PostgreSQL does the same job with GIN over a tsvector, so this is not a synonym there.</remarks>
  FullText,

  /// <summary>MySQL's <c>SPATIAL</c> index.</summary>
  /// <remarks>PostgreSQL spells the same intent as GiST.</remarks>
  Spatial,

  /// <summary>Oracle's bitmap index, for low-cardinality columns; hostile to concurrent writes.</summary>
  Bitmap,

  /// <summary>pgvector's graph index over a vector column, which finds the nearest vectors by one metric,
  /// approximately.</summary>
  Hnsw,
}

/// <summary>Specifies what a DDL statement does, so that the upgrade can order and interleave statements.</summary>
public enum DdlKind {
  /// <summary>A statement that makes the server able to hold what the model declares, such as an extension, run
  /// before anything is created.</summary>
  Prerequisite,
  CreateSchema,
  CreateSequence,
  CreateTable,
  AddPrimaryKey,
  CreateIndex,
  AddRefConstraint,
  CreateView,
  CreateFunction,
  Comment,

  // The ALTER half, which a fresh CREATE never needs.
  AddColumn,
  AlterColumn,
  ColumnInit,
  AlterSequence,
  RenameTable,
  RenameColumn,
  DropColumn,
  DropTable,
  DropView,
  DropFunction,
  DropIndex,
  DropConstraint,
  DropSequence,
  DropSchema,
  AddCheckConstraint,
}

/// <summary>Specifies the kind of a routine, which is the axis on which procedures, functions and their variants
/// differ.</summary>
public enum DbRoutineKind {
  Procedure,
  /// <summary>A function that returns one value.</summary>
  Function,
  /// <summary>A function that returns a row set: PostgreSQL's <c>RETURNS TABLE</c> or <c>SETOF</c>, SQL Server's
  /// table-valued functions, Oracle's pipelined functions.</summary>
  TableFunction,
  Aggregate,
  Window,
}

/// <summary>Specifies which way a routine parameter carries its value.</summary>
public enum DbParameterDirection { In, Out, InOut, Return }

/// <summary>Specifies the kind of a user-defined type, which says which of the type's optional parts is the
/// meaningful one.</summary>
public enum DbCustomTypeKind {
  /// <summary>A named type over a base type, with its own nullability and constraint, such as a PostgreSQL domain or
  /// a SQL Server alias type.</summary>
  Domain,
  /// <summary>A named set of values.</summary>
  Enum,
  /// <summary>A record of attributes, such as a PostgreSQL composite or an Oracle object type.</summary>
  Composite,
  /// <summary>A type whose value is a table, such as a SQL Server table-valued parameter type.</summary>
  Table,
  Range,
}

/// <summary>Specifies when a trigger fires relative to the statement.</summary>
public enum DbTriggerTiming { Before, After, InsteadOf }

/// <summary>Specifies the events a trigger fires on; one trigger commonly answers to several.</summary>
[Flags]
public enum DbTriggerEvents { None = 0, Insert = 1, Update = 2, Delete = 4, Truncate = 8 }

/// <summary>Specifies which parts of a database to read.</summary>
/// <remarks>The default is what the diff needs; an upgrade runs on every startup and does not pay for objects it
/// never compares. <see cref="SourceText"/> is separate from the kinds that have source because it is the bulk of the
/// data and none of it is needed to draw a tree of names.</remarks>
[Flags]
public enum DbModelParts {
  None = 0,

  /// <summary>The schemas, tables, columns, keys, ref constraints and sequences: the set an upgrade compares.</summary>
  Tables = 1,

  Views = 1 << 1,

  /// <summary>The routines by name, with their return type and, where MIC made one, the hash of its definition:
  /// what an upgrade compares.</summary>
  /// <remarks>A routine's parameters and result columns are <see cref="RoutineShapes"/>. Read without them, a
  /// table function a server tells from its result shape is reported as a function.</remarks>
  Routines = 1 << 2,
  Packages = 1 << 3,
  CustomTypes = 1 << 4,
  Triggers = 1 << 5,
  Synonyms = 1 << 6,
  CheckConstraints = 1 << 7,

  /// <summary>The comments and descriptions on tables and columns.</summary>
  Comments = 1 << 8,

  /// <summary>The bodies: a view's definition, a routine's source.</summary>
  /// <remarks>Ask for it only when something will show it.</remarks>
  SourceText = 1 << 9,

  /// <summary>The parameters of the routines and the columns of the row sets they return; the routines are read
  /// with them.</summary>
  /// <remarks>Ask for it only when something will show a routine's shape: on SQL Server the result set of every
  /// procedure is inferred from its body.</remarks>
  RoutineShapes = 1 << 10,

  Default = Tables,

  All = Tables | Views | Routines | Packages | CustomTypes | Triggers | Synonyms | CheckConstraints | Comments
      | SourceText | RoutineShapes,
}

/// <summary>Specifies the kind of a database object a difference between two database models is about.</summary>
/// <remarks>An upgrade compares schemas, sequences, tables, columns, keys, ref constraints, table functions, views
/// and check constraints; no difference is made for the other kinds.</remarks>
public enum DbObjectKind {
  Schema, Sequence, Table, Column, Key, RefConstraint,

  /// <summary>A function returning rows that the model declares and MIC manages, compared through the hash of its
  /// body.</summary>
  TableFunction,

  View, MaterializedView, Routine, Package, CustomType, Trigger, Synonym, CheckConstraint, Parameter,
}

/// <summary>Specifies what a difference between two database models is.</summary>
/// <remarks>The kind is meaningful only in a direction: swap source and target and every add becomes a drop.</remarks>
public enum DbDiffKind { Add, Drop, Modify, Rename }
