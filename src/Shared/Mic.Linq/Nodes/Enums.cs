using Mic.Linq.Model;

namespace Mic.Linq.Nodes;

/// <summary>Specifies what a node is, in the engine's own vocabulary, and the child layout each kind defines.</summary>
/// <remarks>An operation is a kind rather than a method, since the primary path builds against the model by name. The
/// child layout is part of each kind's meaning and is documented on the member, as an untyped tree has no other place
/// to say it.</remarks>
public enum NodeKind {
  /// <summary>No kind.</summary>
  None = 0,

  /// <summary>A converted expression, before anything has been decided about what it means: an
  /// <see cref="ExpressionNode"/>, whose <c>NodeType</c> carries which expression it was.</summary>
  /// <remarks>This is the one kind that is not a query concept. A tree of these is what the converter produces and
  /// what the translator binds in place.</remarks>
  Expression,

  // ----- sources and set operations. Every one of these produces ROWS, and its `Entity` says whose. ---------------------

  /// <summary>All the records of one entity, as a leaf whose <c>Entity</c> is the whole of it.</summary>
  EntitySet,

  /// <summary>The rows of a JSON array, a <c>JsonRowsNode</c> whose first child is the document: a parameter whose
  /// value is a list of rows, or a JSON column of the outer row or a path into one.</summary>
  JsonRows,

  /// <summary>The rows a function of the server returns, a <c>TableFunctionNode</c> whose children are the
  /// arguments of the call and whose <c>Entity</c> is the shape of its rows.</summary>
  TableFunction,

  /// <summary>A query with the members to load with its rows, an <c>IncludeNode</c> whose first child is the query.
  /// </summary>
  /// <remarks>The node is hashed into the key with the shape and stripped off the root by the translator, which
  /// records the includes on what it produces.</remarks>
  Include,

  /// <summary>A query read without the filters its entities' extensions attach, all of them or one extension's: an
  /// <c>IgnoreFiltersNode</c> whose first child is the query.</summary>
  IgnoreFilters,

  /// <summary>Two whole selects combined, a <c>SetOperationNode</c> whose first child is the left side and second the
  /// right, and whose <c>OpType</c> says which of the four.</summary>
  /// <remarks>This is the one shape that is not <c>SELECT … FROM …</c>: a scope's aliases, joins and WHERE belong
  /// to each side and not to the pair, so composing over one makes it a derived table.</remarks>
  SetOperation,

  // ----- values ---------------------------------------------------------------------------------------------------------

  /// <summary>One row of a rowset, which a condition or an ordering term is written in terms of: a leaf
  /// <c>RowNode</c> pointing at its rowset, not a child of it.</summary>
  /// <remarks>Every member access on a row holds the same row node as its child, so a tree containing one is a
  /// directed acyclic graph. A pass that mutates a row node does so once per reference.</remarks>
  Row,

  /// <summary>A member access, whose first child is what the member is taken from: a row, or another member where a
  /// reference is walked through. The node carries the <c>Member</c> it names.</summary>
  Member,

  /// <summary>A literal, as a leaf carrying <c>Value</c>.</summary>
  Constant,

  /// <summary>A value supplied at execution rather than built in, as a leaf carrying <c>Name</c> and
  /// <c>DataType</c>.</summary>
  /// <remarks>Anything that varies with data is a parameter and never part of a query's text or its cache key.
  /// </remarks>
  Parameter,

  // ----- operators ------------------------------------------------------------------------------------------------------

  /// <summary><c>And</c>, <c>Or</c> or <c>Not</c>: a <see cref="LogicalOpNode"/>, whose <c>OpType</c> says which.
  /// <c>And</c> and <c>Or</c> take any number of children, flattened; <c>Not</c> takes its operand as the first
  /// child.</summary>
  /// <remarks>These are spelled the same on every server, unlike a <see cref="NamedOp"/>, and take any number of
  /// operands, unlike a <see cref="Binary"/>.</remarks>
  LogicalOp,

  /// <summary>A comparison or an arithmetic operation, whose first child is the left operand and second the right:
  /// a <see cref="BinaryNode"/>, whose <c>NodeType</c> says which operator.</summary>
  /// <remarks>One kind serves every operator, since a binary operator means the same thing unbound and bound; binding
  /// settles only its result type.</remarks>
  Binary,

  /// <summary>A named operation, whose children are its arguments: a <see cref="NamedOpNode"/>, whose <c>OpType</c>
  /// says which operation.</summary>
  /// <remarks>A <see cref="Binary"/> is an operator every dialect writes identically; a named operation is one whose
  /// text varies by vendor. One kind serves every operation, since all are uniform in shape: arguments that are
  /// values, producing a value.</remarks>
  NamedOp,

  // ----- the SQL tree: selects with clauses, tables, joins, columns ------------------------------------------------

  /// <summary>One SELECT, a <c>SelectNode</c> whose children are its eight clauses in a fixed order.</summary>
  Select,

  /// <summary>One clause of a select, a <c>ClauseNode</c> whose <c>ClauseType</c> says which and whose children are
  /// the clause's items.</summary>
  Clause,

  /// <summary>A table in a FROM clause, a <c>TableNode</c> over the mapping's table.</summary>
  Table,

  /// <summary>A select used as a source, a <c>DerivedRowSetNode</c> whose first child is the select.</summary>
  DerivedRowSet,

  /// <summary>A rowset joined into a FROM clause, a <c>JoinNode</c> whose first child is the rowset and second the
  /// condition.</summary>
  Join,

  /// <summary>A column of a row, a <c>ColumnNode</c> pointing at the row it is read off, not a child of it.</summary>
  Column,

  /// <summary>One output value of a select, a <c>SelectedValueNode</c> whose first child is what it selects.</summary>
  SelectedValue,

  /// <summary>An <c>EXISTS (select)</c> condition, an <c>ExistsNode</c> whose first child is the select.</summary>
  Exists,

  /// <summary>An aggregate on a select list, an <c>AggregateValueNode</c> whose first child is the term where there
  /// is one.</summary>
  AggregateValue,

  /// <summary>A <c>ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)</c> value, a <c>RowNumberNode</c> whose children
  /// are the partition terms and then the ordering terms.</summary>
  RowNumber,

  /// <summary>A common table expression's definition, a <c>CteNode</c> whose first child is its body: a select, or
  /// for a recursive one the anchor and the recursive part combined.</summary>
  /// <remarks>The definition belongs to the statement, not to a select.</remarks>
  Cte,

  /// <summary>A use of a common table expression in a FROM clause, a <c>CteRefNode</c> pointing at its definition and
  /// written as the name and an alias.</summary>
  CteRef,

  /// <summary>A DELETE statement, a <c>DeleteNode</c>: the first child is the table whose rows go, the second the
  /// WHERE clause, and the third, where the rows are found by key through a query, the join to that query's derived
  /// table on the key.</summary>
  /// <remarks>A statement is a root only; nothing else reads it.</remarks>
  Delete,

  /// <summary>An UPDATE statement, an <c>UpdateNode</c>: the first child is the table, the second the SET list as
  /// output values named for the members they write, the third the WHERE clause, and the fourth, where the values are
  /// read out of a query, the join to that query's derived table on the key.</summary>
  Update,

  /// <summary>An <c>INSERT … SELECT</c> statement, an <c>InsertNode</c>: the first child is the table, the second the
  /// select whose output values are the rows, and the node carries the members its columns are, in the select list's
  /// order.</summary>
  Insert,
}

/// <summary>Specifies how a server is asked to treat a common table expression, where it can be asked.</summary>
/// <remarks>PostgreSQL inlines a common table expression read once and materializes one read twice, which is an
/// optimization fence. A server with no such word ignores the kind.</remarks>
public enum CteKind {
  /// <summary>The server's own choice.</summary>
  Default,
  /// <summary>The common table expression is materialized.</summary>
  Materialized,
  /// <summary>The common table expression is inlined.</summary>
  Inlined,
}

/// <summary>Specifies which clause of a select a <see cref="ClauseNode"/> is, in the order a select's children hold
/// them.</summary>
public enum ClauseType {
  /// <summary>The select list.</summary>
  Select,
  /// <summary>The FROM clause.</summary>
  From,
  /// <summary>The WHERE clause.</summary>
  Where,
  /// <summary>The GROUP BY clause.</summary>
  GroupBy,
  /// <summary>The HAVING clause.</summary>
  Having,
  /// <summary>The ORDER BY clause.</summary>
  OrderBy,
  /// <summary>The number of rows to skip.</summary>
  Skip,
  /// <summary>The number of rows to take.</summary>
  Take,
}

/// <summary>Specifies how far along its clauses a select has been built, in the order the clauses apply, numbered so
/// that a higher stage is an earlier clause.</summary>
/// <remarks>A select at a stage takes an operation at that stage or a later one; an operation at an earlier stage,
/// the chain having gone backwards, turns everything so far into a derived table. A fresh select is
/// <see cref="Open"/>, which takes anything.</remarks>
public enum SelectStage {
  /// <summary>An aggregate on the select list, the last thing a select does.</summary>
  Aggregating = -2,
  /// <summary>A projection.</summary>
  Projecting = -1,
  /// <summary>A page: a skip or a take.</summary>
  Paging = 0,
  /// <summary>An ordering.</summary>
  Ordering = 1,
  /// <summary>A filter over the groups.</summary>
  Having = 2,
  /// <summary>A grouping.</summary>
  Grouping = 3,
  /// <summary>A filter over the rows, the first thing a select does.</summary>
  Filtering = 4,
  /// <summary>Nothing taken yet.</summary>
  Open = int.MaxValue,
}

/// <summary>Specifies how a rowset is joined.</summary>
public enum JoinType {
  /// <summary>An inner join.</summary>
  Inner,
  /// <summary>A left outer join.</summary>
  LeftOuter,
  /// <summary>A cross join: every pair, with nothing tying the second source to the first and a condition of
  /// <see langword="true"/>.</summary>
  Cross,
  /// <summary>A lateral join: the second source is evaluated per row of the first, which it reads, as a function
  /// called with the row's values is; <c>CROSS APPLY</c> or <c>CROSS JOIN LATERAL</c>, with no condition.</summary>
  Lateral,
}

/// <summary>Specifies which logical operator a <see cref="NodeKind.LogicalOp"/> node is.</summary>
public enum LogicalOpType {
  /// <summary>No operator.</summary>
  None = 0,
  /// <summary>A conjunction.</summary>
  And,
  /// <summary>A disjunction.</summary>
  Or,
  /// <summary>A negation.</summary>
  Not,
}

/// <summary>Specifies which aggregate an <see cref="AggregateValueNode"/> is.</summary>
public enum AggregateType {
  /// <summary>No aggregate.</summary>
  None = 0,

  /// <summary>The number of rows, as <c>COUNT(*)</c>, with no selector.</summary>
  Count,

  /// <summary>The sum of the selected values.</summary>
  Sum,

  /// <summary>The least of the selected values.</summary>
  Min,

  /// <summary>The greatest of the selected values.</summary>
  Max,

  /// <summary>The average of the selected values, converted to the type the caller asks for, since averaging integers
  /// gives an integer on some servers and a decimal on others.</summary>
  Average,

  /// <summary>Whether the source has any row, or with a condition any row that matches: an <c>EXISTS</c>, the one
  /// aggregate that is a predicate rather than a number.</summary>
  /// <remarks>A list becomes a correlated sub-query with a source, an alias and joins of its own, since joining a
  /// one-to-many would multiply the rows it was asked about.</remarks>
  Any,
}

/// <summary>Specifies which combination a <see cref="NodeKind.SetOperation"/> node is.</summary>
/// <remarks><see cref="Union"/> discards duplicates and <see cref="Concat"/> keeps them, as <c>UNION</c> against
/// <c>UNION ALL</c>. Oracle spells <see cref="Except"/> as <c>MINUS</c>.</remarks>
public enum SetOperationType {
  /// <summary>No combination.</summary>
  None = 0,
  /// <summary>The rows of both sides, duplicates discarded.</summary>
  Union,
  /// <summary>The rows of both sides, duplicates kept.</summary>
  Concat,
  /// <summary>The rows in both sides.</summary>
  Intersect,
  /// <summary>The rows of the left side not in the right.</summary>
  Except,
}

/// <summary>Specifies which operation a <see cref="NodeKind.NamedOp"/> node is: an operation the engine names and the
/// dialect spells, which a vendor without one refuses by name at translation.</summary>
/// <remarks>The evaluator implements every operation too, so that a query answered from the entity cache agrees with
/// the same query answered by a server. Differences beyond spelling, such as a 1-based <see cref="Substring"/>, are
/// settled where the operation is built, so no caller has to know which server it is talking to.</remarks>
public enum NamedOpType {
  /// <summary>No operation.</summary>
  None = 0,

  // ----- strings ---------------------------------------------------------------------------------------------------

  /// <summary>The text uppercased; the argument is the text.</summary>
  Upper,

  /// <summary>The text lowercased; the argument is the text.</summary>
  Lower,

  /// <summary>The number of characters in the text; the argument is the text.</summary>
  Length,

  /// <summary>The text with whitespace off both ends; the argument is the text.</summary>
  Trim,

  /// <summary>A piece of the text; the arguments are the text, the 1-based start and the length.</summary>
  /// <remarks>Every server counts from one; the converter adds one to a zero-based start, so nothing below it sees
  /// the zero-based convention.</remarks>
  Substring,

  /// <summary>Two texts joined; the arguments are the two texts.</summary>
  Concat,

  /// <summary>The text with every occurrence of one piece swapped for another; the arguments are the text, the piece
  /// sought and its replacement.</summary>
  Replace,

  /// <summary>Where a piece starts in the text, 1-based and zero when absent; the arguments are the text and the
  /// piece sought.</summary>
  /// <remarks>The converter subtracts one from the result for the zero-based convention, so that zero becomes -1.
  /// </remarks>
  IndexOf,

  /// <summary>Whether the text starts with a term; the arguments are the text and the term.</summary>
  /// <remarks>The term is the raw value. The pattern is built on the value where the statement is bound, so nothing
  /// the user types can become a pattern operator; in memory the evaluator does the plain string operation.</remarks>
  StartsWith,
  /// <summary>Whether the text ends with a term; the arguments are the text and the term.</summary>
  EndsWith,
  /// <summary>Whether the text contains a term anywhere; the arguments are the text and the term.</summary>
  Contains,

  /// <summary>A match against the caller's own pattern, with <c>%</c> and <c>_</c> meaning what SQL says they mean;
  /// the arguments are the text and the pattern.</summary>
  /// <remarks>A literal wildcard is written escaped with the target's escape character. The operation is not
  /// evaluated in memory and is not reachable from a typed query; <see cref="QueryMaker.NamedOp"/> builds it.</remarks>
  Like,

  // ----- dates -----------------------------------------------------------------------------------------------------

  /// <summary>The year of a date.</summary>
  Year,
  /// <summary>The month of a date.</summary>
  Month,
  /// <summary>The day of a date.</summary>
  Day,

  /// <summary>The date part alone, with any time dropped.</summary>
  Date,

  // ----- numbers ---------------------------------------------------------------------------------------------------

  /// <summary>The absolute value.</summary>
  Abs,
  /// <summary>The value rounded to an integer.</summary>
  Round,

  /// <summary>The value rounded to a number of places; the arguments are the value and the number of digits.</summary>
  RoundTo,

  /// <summary>The value rounded down to an integer.</summary>
  Floor,
  /// <summary>The value rounded up to an integer.</summary>
  Ceiling,

  /// <summary>The value raised to an exponent; the arguments are the value and the exponent.</summary>
  /// <remarks>This operation, <see cref="Sqrt"/> and <see cref="Sign"/> are absent on SQLite, where a query using one
  /// is refused by name.</remarks>
  Power,
  /// <summary>The square root of the value.</summary>
  Sqrt,
  /// <summary>The sign of the value.</summary>
  Sign,

  /// <summary>The remainder of a division; the arguments are the two values.</summary>
  /// <remarks>Oracle has no <c>%</c> operator and spells it <c>MOD(a, b)</c>.</remarks>
  Modulo,

  // ----- values ----------------------------------------------------------------------------------------------------

  /// <summary>The first of two values that is not <see langword="null"/>.</summary>
  Coalesce,

  /// <summary>One of two values, chosen by a test, as <c>test ? a : b</c> says it; the arguments are the test and
  /// the two values.</summary>
  /// <remarks>Written as a <c>CASE</c>, so the second value is the result where the test is neither true nor false.
  /// </remarks>
  Conditional,

  /// <summary>Whether a value is <see langword="null"/>, as <c>x == null</c> says it; one argument.</summary>
  IsNull,

  /// <summary>Whether a value is not <see langword="null"/>, as <c>x != null</c> says it; one argument.</summary>
  IsNotNull,

  /// <summary>Whether a value is one of a list; the arguments are the value and the list: a constant collection, a
  /// list parameter, or a query selecting one column.</summary>
  /// <remarks>A collection binds as an array on PostgreSQL and is written into the text elsewhere, so the SQL cannot
  /// be finished at translation.</remarks>
  In,

  // ----- JSON ------------------------------------------------------------------------------------------------------

  /// <summary>A value read out of a JSON document by path; the arguments are the document and the path, such as
  /// <c>$.a.b</c>, and the node's type is the type the value is read as, text unless said otherwise.</summary>
  /// <remarks>The value is <see langword="null"/> where the path names nothing.</remarks>
  JsonValue,

  // ----- full-text search ------------------------------------------------------------------------------------------

  /// <summary>Whether a row matches a full-text query over its entity's full-text index; the arguments are the row,
  /// the query text, the <see cref="TextMatchKind"/> and the distance for a match within one.</summary>
  /// <remarks>The query is plain words, and what the kind says of them is built into the server's own query form on
  /// the value where the statement is bound, never in the text. The operation is not evaluated in memory.</remarks>
  TextMatch,

  /// <summary>The rank of a row for a full-text query, with the same arguments as <see cref="TextMatch"/>: a number
  /// higher for a better match, comparable within one statement only.</summary>
  TextRank,

  // ----- geographic points -----------------------------------------------------------------------------------------

  /// <summary>The distance in metres between two geographic points along the Earth's surface; the arguments are
  /// the two points.</summary>
  /// <remarks>A server measures on its own figure of the Earth, a spheroid or a sphere, and the evaluator on a
  /// sphere; the figures agree within a fraction of a percent.</remarks>
  GeoDistance,

  // ----- vectors -----------------------------------------------------------------------------------------------------

  /// <summary>The distance between two vectors, smaller for a nearer one; the arguments are the two vectors.
  /// </summary>
  /// <remarks>The metric is the one the vector member among the arguments declares, which the model that writes the
  /// statement knows. The operation is not evaluated in memory.</remarks>
  VectorDistance,
}

/// <summary>Specifies what a full-text query's words mean, alike on every server.</summary>
public enum TextMatchKind {
  /// <summary>Every word has to appear, in any order and any place.</summary>
  AllWords = 0,

  /// <summary>Any one of the words appearing matches.</summary>
  AnyWord,

  /// <summary>The words as a phrase, adjacent and in order.</summary>
  Phrase,

  /// <summary>Every word has to appear as the start of a word.</summary>
  Prefix,

  /// <summary>Every word has to appear within a distance of each other, in any order.</summary>
  Near,
}

/// <summary>Specifies what is true of a node beyond its kind.</summary>
[Flags]
public enum NodeFlags {
  /// <summary>No flag.</summary>
  None = 0,

  /// <summary>An ordering term that sorts from the greatest value down.</summary>
  Descending = 1,

  /// <summary>On a <see cref="NodeKind.Constant"/> or a <see cref="NodeKind.Parameter"/>, the value is a collection
  /// and the node's type is its element type.</summary>
  List = 1 << 1,

  /// <summary>On a node in the select's chain, the rows come back with duplicates removed.</summary>
  /// <remarks>Rows of an entity carry a primary key and are already distinct; the flag serves a projection, where two
  /// rows of the table can select the same thing.</remarks>
  Distinct = 1 << 2,

  /// <summary>On a join, an outer row that matched nothing is kept, as a left outer join.</summary>
  /// <remarks>A reference join decides this from the model; an explicit join has to be told. A typed query says it as
  /// <c>GroupJoin</c> followed by <c>DefaultIfEmpty</c>.</remarks>
  Outer = 1 << 3,

  /// <summary>On an ordering term, and on the <c>OrderBy</c> call that makes one, the order is the model's default
  /// rather than the caller's.</summary>
  /// <remarks>An <c>OrderBy</c> written over default terms replaces them; one written over the caller's own is
  /// refused.</remarks>
  DefaultOrder = 1 << 4,
}

/// <summary>Specifies what a translated statement produces, which chooses the reader.</summary>
public enum QueryResultKind {
  /// <summary>Rows of an entity: tracked records, through the identity map.</summary>
  Entities = 0,

  /// <summary>One value, such as an aggregate.</summary>
  Scalar,

  /// <summary>Rows of a projection: named values over a structure the engine synthesized for the shape.</summary>
  Rows,

  /// <summary>One column over many rows: a projection of a single expression, collapsed to a list of values.</summary>
  Values,

  /// <summary>No rows: a mutation, answering with the number of rows it affected.</summary>
  Affected,
}

/// <summary>Specifies the statement a translated query is.</summary>
/// <remarks>What a select returns, rows or one value, is <see cref="QueryResultKind"/>'s to say.</remarks>
public enum StatementKind {
  /// <summary>A select: of rows, of one value, or a set operation over selects.</summary>
  Select = 0,

  /// <summary>A delete of the rows the query finds.</summary>
  Delete,

  /// <summary>An update of the rows the query finds.</summary>
  Update,

  /// <summary>An insert of the rows the query produces.</summary>
  Insert,
}

/// <summary>Specifies where a node's work happens.</summary>
public enum QueryTier {
  /// <summary>In the statement. This is the default.</summary>
  Sql = 0,
  /// <summary>Before the statement runs, as a value computed once and passed in.</summary>
  PreSql,
  /// <summary>Over the rows once they are back, for work SQL cannot express.</summary>
  PostSql,
}

/// <summary>Specifies what the translation has learned about a node, beside its tier.</summary>
/// <remarks>Set by the translation passes, after a query's cache key is taken, and never part of one; the shape a
/// key hashes is <see cref="NodeFlags"/>.</remarks>
[Flags]
public enum TranslationFlags {
  /// <summary>Nothing learned.</summary>
  None = 0,
  /// <summary>The subtree runs in memory over cached entities: the evaluator implements every node in it, and the
  /// rows it reads are cached rows. Set by the cache pass; taken off a chain that goes back to the server after all.
  /// </summary>
  Cached = 1,
}
