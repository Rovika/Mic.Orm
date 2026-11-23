using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Sql;

namespace Mic.Orm.Db.Provider;

/// <summary>Holds the templates and spellings a server's SQL is written with, standard SQL by default.</summary>
/// <remarks>A provider subclasses this and assigns its server's differences in the constructor. The templates are
/// fields, assigned rather than overridden; a method is overridden only where a spelling takes arguments no template
/// holds.</remarks>
public class SqlDialect {
  // ----- lexical --------------------------------------------------------------------------------------------------

  /// <summary>Specifies the opening quote of an identifier. Identifiers are always quoted.</summary>
  public string QuoteLeft = "\"";
  public string QuoteRight = "\"";

  /// <summary>Specifies how a closing quote inside an identifier is escaped; standard SQL doubles it.</summary>
  public string QuoteEscape = "\"\"";

  public string LineBreak = "\n";
  public string Indent = "  ";

  // ----- writing a name --------------------------------------------------------------------------------------------

  /// <summary>Quotes an identifier.</summary>
  /// <param name="name">The identifier.</param>
  /// <returns>The identifier, quoted, with any quote inside it escaped.</returns>
  public virtual string Quote(string name) =>
    QuoteLeft + name.Replace(QuoteRight, QuoteEscape) + QuoteRight;

  /// <summary>Builds a schema-qualified name, or a bare quoted name where the schema is empty.</summary>
  /// <param name="schema">The name of the schema, or an empty string.</param>
  /// <param name="name">The name of the object.</param>
  /// <returns>The qualified name.</returns>
  public virtual string QualifiedName(string schema, string name) =>
    schema.Length == 0 ? Quote(name) : Quote(schema) + "." + Quote(name);

  /// <summary>Gets the name a statement refers to a table by.</summary>
  public virtual string GetTableRef(DbTableInfo table) => QualifiedName(table.Schema.Name, table.Name);

  /// <summary>Writes a string as a literal, doubling the quotes inside it.</summary>
  /// <param name="value">The string.</param>
  /// <returns>The literal.</returns>
  /// <remarks>A dialect whose server has another escape convention, such as MySQL's backslash, overrides this; a value
  /// that escapes its quoting is a SQL injection.</remarks>
  public virtual string GetLiteral(string value) =>
    StringQuote + value.Replace("'", "''") + StringQuote;

  /// <summary>Specifies the quote of a string literal.</summary>
  public string StringQuote = "'";

  // ----- values as literals ---------------------------------------------------------------------------------------

  /// <summary>Specifies the longest string or binary written into a statement as a literal; a longer one is bound.
  /// </summary>
  public int MaxLiteralLength = 100;

  /// <summary>Decides whether a value can be written into a statement as a literal.</summary>
  /// <param name="value">The value, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> if the value is <see langword="null"/>, a string or binary within
  /// <see cref="MaxLiteralLength"/>, a Boolean, a Guid, a date or time, a number or an enum; otherwise,
  /// <see langword="false"/>, and the caller binds a parameter.</returns>
  public virtual bool CanUseLiteral(object? value) => value switch {
    null or DBNull => true,
    string s => s.Length <= MaxLiteralLength,
    byte[] bytes => bytes.Length * 2 <= MaxLiteralLength,
    bool or Guid or DateTime or DateOnly or TimeOnly => true,
    byte or sbyte or short or ushort or int or uint or long or ulong => true,
    decimal or double or float => true,
    _ => value.GetType().IsEnum,
  };

  /// <summary>Writes a value as a literal, as this server spells it, using the invariant culture.</summary>
  /// <param name="value">The value, or <see langword="null"/>.</param>
  /// <returns>The literal, <c>NULL</c> for <see langword="null"/>; <see langword="null"/> if the value has to be
  /// bound instead.</returns>
  public virtual string? GetLiteral(object? value) {
    if (!CanUseLiteral(value))
      return null;
    return value switch {
      null or DBNull => "NULL",
      string s => GetLiteral(s),
      bool b => BooleanLiteral(b),
      Guid g => GuidLiteral(g),
      byte[] bytes => BinaryLiteral(bytes),
      DateTime dt => DateTimeLiteral(dt),
      DateOnly d => StringQuote + d.ToString("yyyy-MM-dd", Invariant) + StringQuote,
      TimeOnly t => StringQuote + t.ToString("HH:mm:ss.fff", Invariant) + StringQuote,
      decimal m => m.ToString(Invariant),
      double d => d.ToString("R", Invariant),
      float f => f.ToString("R", Invariant),
      // An enum travels as its underlying integer, which is what the column holds.
      Enum e => Convert.ToInt64(e, Invariant).ToString(Invariant),
      IFormattable number => number.ToString(null, Invariant),
      _ => null,
    };
  }

  protected static readonly System.Globalization.CultureInfo Invariant =
    System.Globalization.CultureInfo.InvariantCulture;

  /// <summary>Writes a Boolean as a literal: <c>TRUE</c> or <c>FALSE</c>, or <c>1</c> or <c>0</c> on a server
  /// without a Boolean type.</summary>
  public virtual string BooleanLiteral(bool value) => value ? "TRUE" : "FALSE";

  /// <summary>Writes a Guid as a literal of whatever type the column holds on this server.</summary>
  public virtual string GuidLiteral(Guid value) => StringQuote + value.ToString("D") + StringQuote;

  /// <summary>Writes a binary value as a hex literal, <c>X'…'</c> by the standard or <c>0x…</c> on several servers.
  /// </summary>
  public virtual string BinaryLiteral(byte[] value) => "X'" + Convert.ToHexString(value) + "'";

  /// <summary>Writes a timestamp as a literal, to milliseconds.</summary>
  public virtual string DateTimeLiteral(DateTime value) =>
    StringQuote + value.ToString("yyyy-MM-dd HH:mm:ss.fff", Invariant) + StringQuote;

  /// <summary>Specifies what starts a comment line in a script rendered as text.</summary>
  public string LineComment = "--";

  /// <summary>Specifies what terminates a statement in a script rendered as text.</summary>
  /// <remarks>Statements are executed one at a time and carry no terminator of their own.</remarks>
  public string StatementEnd = ";";

  /// <summary>Specifies the cheapest statement that proves the server is answering, run once when connecting.</summary>
  public string SelectOne = "SELECT 1";

  /// <summary>Specifies a select of one expression over no table; <c>{0}</c> is the expression.</summary>
  public string SelectValue = "SELECT {0}";

  /// <summary>Specifies the form a condition takes where a value is wanted, such as a column of a select list, an
  /// ordering term or an assigned value; <c>{0}</c> is the condition. <see langword="null"/> on a server that takes
  /// a condition as a value.</summary>
  /// <remarks>Set on a server with no Boolean value, where a comparison or an <c>EXISTS</c> is not one.</remarks>
  public string? ConditionAsValue;

  /// <summary>Builds the form a parameter or a null takes where nothing beside it gives it a type, such as a column
  /// of a select list, a result of a <c>CASE</c> or an argument of <c>COALESCE</c>: the value itself by default.
  /// </summary>
  /// <param name="value">The value as the statement writes it, a parameter or <c>NULL</c>.</param>
  /// <param name="clrType">The CLR type of the value.</param>
  /// <param name="type">The database type a parameter of the CLR type maps to, or <see langword="null"/> if none is
  /// registered.</param>
  /// <returns>The value in the form the server takes there.</returns>
  /// <remarks>A dialect overrides this where the server takes a parameter's type from what stands beside it and
  /// refuses one that stands alone, as Db2 does.</remarks>
  public virtual string BuildUntypedValue(string value, Type clrType, DbTypeInfo? type) => value;

  /// <summary>Specifies an average over an integer term on a server whose <c>AVG</c> of integers is an integer, cut
  /// of its fraction; <c>{0}</c> is the term. <see langword="null"/> where the server's average has the fraction.
  /// </summary>
  public string? IntegerAverage;

  /// <summary>Specifies the head of a delete written from a query; <c>{0}</c> is the table, <c>{1}</c> the alias its
  /// conditions name it by. The WHERE clause follows.</summary>
  /// <remarks>The servers differ in how the alias is declared: with <c>AS</c>, without it on Oracle, and named again
  /// before the FROM on SQL Server.</remarks>
  public string DeleteFrom = "DELETE FROM {0} AS {1}";

  /// <summary>Specifies one item of an update's SET list; <c>{0}</c> is the column, <c>{1}</c> the value, <c>{2}</c>
  /// the table's alias.</summary>
  /// <remarks>PostgreSQL refuses an alias on the left of an assignment; MySQL wants the column qualified.</remarks>
  public string UpdateAssignment = "{0} = {1}";

  /// <summary>Specifies the head of an update of a table's own filtered rows, written from a query; <c>{0}</c> is
  /// the table, <c>{1}</c> the alias the values and conditions name it by, <c>{2}</c> the SET list. The WHERE clause
  /// follows.</summary>
  public string UpdateWhere = "UPDATE {0} AS {1}\n  SET {2}";

  /// <summary>Specifies the whole of an update whose values are read out of a query; <c>{0}</c> is the table,
  /// <c>{1}</c> its alias, <c>{2}</c> the SET list taking each value off the query's row, <c>{3}</c> the query as a
  /// bracketed select, <c>{4}</c> its alias, <c>{5}</c> the condition joining the two on the key.</summary>
  /// <remarks>The statement carries no WHERE clause; the target's own filter is in the join condition. Each server
  /// spells this differently, and Oracle says it as a <c>MERGE</c>.</remarks>
  public string UpdateFrom = "UPDATE {0} AS {1}\n  SET {2}\n  FROM {3} {4}\n  WHERE {5}";

  /// <summary>Builds the statement that tells the server to order a table's rows by an index.</summary>
  /// <returns>The statement, or <see langword="null"/> where the server has none, which is every server without a
  /// clustering marker.</returns>
  /// <remarks>The statement takes an exclusive lock and rewrites the table; issuing it is a maintenance decision
  /// separate from recording the marker.</remarks>
  public virtual string? ClusterTable(DbTableInfo table, DbKeyInfo key) => null;

  /// <summary>Gets how the server spells an access method in a CREATE INDEX: a prefix before the noun and a clause
  /// placed where the dialect's own template puts <c>{5}</c>.</summary>
  /// <returns>The prefix and the clause, both empty in the base.</returns>
  /// <remarks>This is called only for a method the provider declares in <see cref="DbProvider.IndexTypes"/>.</remarks>
  public virtual (string Prefix, string Clause) IndexTypeSyntax(DbIndexType type) => ("", "");

  /// <summary>Gets the statement to run on a schema-reading connection once it is open, or <see langword="null"/>
  /// where the server needs none.</summary>
  /// <remarks>The statement makes the reader the deadlock victim on a server that has to choose one, so a DDL
  /// statement is never aborted half-way through an upgrade.</remarks>
  public virtual string? SchemaConnectionSetup => null;

  /// <summary>Gets the query that returns the complete <c>CREATE</c> statement of a routine, or
  /// <see langword="null"/> where the server cannot produce one; <c>{0}</c> is the schema and <c>{1}</c> the name,
  /// both as string literals.</summary>
  /// <remarks>The query may return several rows, one per overload.</remarks>
  public virtual string? RoutineDefinitionQuery => null;

  // ----- templates ------------------------------------------------------------------------------------------------

  public string CreateSchema = "CREATE SCHEMA {0}";

  /// <summary>Specifies a CREATE TABLE; <c>{0}</c> is the table, <c>{1}</c> the column list.</summary>
  public string CreateTable = "CREATE TABLE {0} (\n{1}\n)";

  /// <summary>Specifies the creation of a temp table that lives as long as the session; <c>{0}</c> is the table,
  /// <c>{1}</c> the column list.</summary>
  public string CreateTempTable = "CREATE TEMP TABLE {0} (\n{1}\n)";

  public string DropTempTable = "DROP TABLE {0}";

  /// <summary>Gets the name a temp table is created and referred to by, from the name the policy gave it: the name
  /// itself by default, with a prefix where the server tells a temp table by one.</summary>
  /// <param name="name">The name the naming policy gave the table.</param>
  /// <returns>The temp table's name, unquoted.</returns>
  public virtual string GetTempTableName(string name) => name;

  /// <summary>Gets the name a statement refers to a temp table by: the bare quoted name by default, since a temp
  /// table lives in the session and not in a schema.</summary>
  /// <param name="schema">The schema of the model the table serves.</param>
  /// <param name="name">The temp table's name, as <see cref="GetTempTableName"/> gave it.</param>
  /// <returns>The reference.</returns>
  public virtual string GetTempTableRef(string schema, string name) => Quote(name);

  // ----- table functions ---------------------------------------------------------------------------------------------

  /// <summary>Specifies how a function's rows are selected from; <c>{0}</c> is the function's qualified name,
  /// <c>{1}</c> the arguments, <c>{2}</c> the alias. <see langword="null"/> where the server has no table-valued
  /// functions.</summary>
  public string? TableFunctionSource = "{0}({1}) AS {2}";

  /// <summary>Specifies a DROP FUNCTION; <c>{0}</c> is the function's qualified name.</summary>
  public string DropTableFunction = "DROP FUNCTION {0}";

  /// <summary>Specifies a lateral join, a source evaluated per row of what comes before it in the FROM; <c>{0}</c>
  /// is the source with its alias. <see langword="null"/> where the server cannot join a function's rows so.
  /// </summary>
  public string? LateralJoin = "CROSS JOIN LATERAL {0}";

  /// <summary>Builds the statement that creates a table function MIC manages, or replaces it, over the parameters
  /// and columns of its table and the body with the hash comment written into it.</summary>
  /// <param name="table">The function's table.</param>
  /// <param name="body">The defining select, with the hash comment in front.</param>
  /// <returns>The statement, or <see langword="null"/> where the server has no table-valued functions.</returns>
  public virtual string? BuildTableFunction(DbTableInfo table, string body) => null;

  // ----- geographic points ------------------------------------------------------------------------------------------

  /// <summary>Specifies how a statement reads a geographic point column as well-known binary; <c>{0}</c> is the
  /// column. The column itself where the server holds the binary.</summary>
  public string GeoPointRead = "{0}";

  /// <summary>Specifies how well-known binary becomes the server's geographic point in an insert or an update;
  /// <c>{0}</c> is the bound value or the literal. The value itself where the server holds the binary.</summary>
  public string GeoPointWrite = "{0}";

  /// <summary>Gets the statement that makes the server able to hold a geographic point, run before anything that
  /// creates such a column; <see langword="null"/> where the server needs nothing.</summary>
  public string? GeoPointPrerequisite;

  /// <summary>Builds the statements that run before a spatial index is created, such as the metadata Oracle wants
  /// registered for the column first.</summary>
  /// <param name="index">The index, over one geographic point column.</param>
  /// <returns>The statements, or <see langword="null"/> where the server needs nothing.</returns>
  public virtual IReadOnlyList<string>? BuildSpatialIndexPrerequisites(DbKeyInfo index) => null;

  /// <summary>Builds the statements that run after a spatial index is dropped, alone or with its table, such as the
  /// removal of the metadata Oracle holds for the column.</summary>
  /// <param name="index">The index, over one geographic point column.</param>
  /// <returns>The statements, or <see langword="null"/> where the server keeps nothing beside the index.</returns>
  public virtual IReadOnlyList<string>? BuildSpatialIndexCleanup(DbKeyInfo index) => null;

  // ----- vectors -----------------------------------------------------------------------------------------------------

  /// <summary>Specifies how a statement reads a vector column in a form the driver returns as an array of floats;
  /// <c>{0}</c> is the column. The column itself where the driver reads the server's type.</summary>
  public string VectorRead = "{0}";

  /// <summary>Specifies how a bound array of floats becomes the server's vector in an insert or an update;
  /// <c>{0}</c> is the bound value. The value itself where the driver binds the server's type.</summary>
  public string VectorWrite = "{0}";

  /// <summary>Gets the statement that makes the server able to hold a vector, run before anything that creates such
  /// a column; <see langword="null"/> where the server needs nothing.</summary>
  public string? VectorPrerequisite;

  /// <summary>Specifies the cosine distance between two vectors; <c>{0}</c> and <c>{1}</c> are the vectors.
  /// <see langword="null"/> where the server has none.</summary>
  public string? VectorCosineDistance;

  /// <summary>Specifies the Euclidean distance between two vectors; <c>{0}</c> and <c>{1}</c> are the vectors.
  /// <see langword="null"/> where the server has none.</summary>
  public string? VectorEuclideanDistance;

  /// <summary>Specifies the negated dot product of two vectors; <c>{0}</c> and <c>{1}</c> are the vectors.
  /// <see langword="null"/> where the server has none.</summary>
  public string? VectorDotProductDistance;

  /// <summary>Gets how a statement writes the distance between two vectors by a metric.</summary>
  /// <param name="metric">One of the enumeration values that specifies the metric.</param>
  /// <returns>The format, with <c>{0}</c> and <c>{1}</c> for the vectors, or <see langword="null"/> where the
  /// server has no such distance.</returns>
  public string? GetVectorDistanceFormat(VectorMetric metric) => metric switch {
    VectorMetric.Cosine => VectorCosineDistance,
    VectorMetric.Euclidean => VectorEuclideanDistance,
    VectorMetric.DotProduct => VectorDotProductDistance,
    _ => null,
  };

  /// <summary>Builds the column list of a vector index, where the server states the index's metric with the
  /// column.</summary>
  /// <param name="index">The index, over one vector column, with its metric.</param>
  /// <returns>The column list, or <see langword="null"/> where it is the plain one.</returns>
  public virtual string? BuildVectorIndexColumns(DbKeyInfo index) => null;

  /// <summary>Gets how a statement reads a column whose value travels in another form than the server holds it in:
  /// a geographic point or a vector.</summary>
  /// <param name="column">The column.</param>
  /// <returns>The format, with <c>{0}</c> for the column, or <see langword="null"/> for a column read as it is.
  /// </returns>
  public string? GetColumnReadFormat(DbColumnInfo column) =>
    column.IsGeoPoint ? GeoPointRead : column.IsVector ? VectorRead : null;

  /// <summary>Gets how an insert or an update writes a column whose value travels in another form than the server
  /// holds it in: a geographic point or a vector.</summary>
  /// <param name="column">The column.</param>
  /// <returns>The format, with <c>{0}</c> for the bound value, or <see langword="null"/> for a column written as it
  /// is.</returns>
  public string? GetColumnWriteFormat(DbColumnInfo column) =>
    column.IsGeoPoint ? GeoPointWrite : column.IsVector ? VectorWrite : null;

  /// <summary>Specifies a column definition; <c>{0}</c> is the column, <c>{1}</c> the type, <c>{2}</c> the
  /// modifiers.</summary>
  public string ColumnSpec = "{0} {1}{2}";

  /// <summary>Specifies the order of a column's modifiers; <c>{0}</c> is the nullability, <c>{1}</c> the default,
  /// <c>{2}</c> the identity clause.</summary>
  /// <remarks>Oracle wants its identity clause before <c>NOT NULL</c>.</remarks>
  public string ColumnModifiers = "{0}{1}{2}";

  public string NotNull = " NOT NULL";
  public string DefaultValue = " DEFAULT {0}";

  /// <summary>Specifies the identity clause of a column; SQL Server wants <c>IDENTITY(1,1)</c>.</summary>
  public string Identity = " GENERATED BY DEFAULT AS IDENTITY";

  /// <summary>Specifies a computed column's definition; <c>{0}</c> is the column, <c>{1}</c> the type, <c>{2}</c>
  /// the expression, <c>{3}</c> the storage clause, <c>{4}</c> the hash of the expression.</summary>
  /// <remarks>SQL Server declares no type, since the expression gives one. A dialect with no statement for
  /// <see cref="CommentOnColumn"/> writes the hash here.</remarks>
  public string ComputedColumnSpec = "{0} {1} GENERATED ALWAYS AS ({2}){3}";

  /// <summary>Specifies the statement that writes the hash of a computed column's expression beside the column, or
  /// <see langword="null"/> where <see cref="ComputedColumnSpec"/> carries it; <c>{0}</c> is the column qualified
  /// by its table, <c>{1}</c> the hash as a literal, <c>{2}</c>, <c>{3}</c> and <c>{4}</c> the schema, the table
  /// and the column as literals.</summary>
  public string? CommentOnColumn = "COMMENT ON COLUMN {0} IS {1}";

  /// <summary>Specifies the storage clause of a computed column whose value the server stores.</summary>
  public string ComputedStored = " STORED";

  /// <summary>Specifies the storage clause of a computed column the server computes on each read.</summary>
  public string ComputedVirtual = " VIRTUAL";

  /// <summary>Specifies whether a computed column is stored or virtual as declared, or always one of them where the
  /// server has or can add only that.</summary>
  public ComputedStorage ComputedStorage = ComputedStorage.AsDeclared;

  /// <summary>Specifies an ADD PRIMARY KEY; <c>{0}</c> is the table, <c>{1}</c> the constraint name, <c>{2}</c> the
  /// columns.</summary>
  public string AddPrimaryKey = "ALTER TABLE {0} ADD CONSTRAINT {1} PRIMARY KEY ({2})";

  /// <summary>Specifies a primary key constraint inside a CREATE TABLE; <c>{0}</c> is the constraint name, <c>{1}</c>
  /// the columns.</summary>
  public string InlinePrimaryKey = "CONSTRAINT {0} PRIMARY KEY ({1})";

  /// <summary>Specifies a foreign key constraint inside a CREATE TABLE; <c>{0}</c> is the constraint name, <c>{1}</c>
  /// the columns, <c>{2}</c> the target table, <c>{3}</c> the target columns.</summary>
  public string InlineRefConstraint =
    "CONSTRAINT {0} FOREIGN KEY ({1}) REFERENCES {2} ({3})";

  /// <summary>Specifies a CREATE INDEX; <c>{0}</c> is the index name, <c>{1}</c> the table, <c>{2}</c> the columns,
  /// <c>{3}</c> the index name qualified by its table's schema, <c>{4}</c> and <c>{5}</c> the prefix and clause of
  /// the access method from <see cref="IndexTypeSyntax"/>.</summary>
  /// <remarks>PostgreSQL rejects a qualified index name; Oracle creates an unqualified one in the connected user's
  /// schema rather than the table's. The access method has two slots because Oracle writes it before the noun,
  /// PostgreSQL between the table and the columns, and MySQL after them or both. Both slots are empty for the default
  /// method.</remarks>
  public string CreateIndex = "CREATE {4}INDEX {0} ON {1} ({2}){5}";
  public string CreateUniqueIndex = "CREATE UNIQUE {4}INDEX {0} ON {1} ({2}){5}";

  /// <summary>Specifies a CREATE INDEX where the index is the table's storage, with the arguments of
  /// <see cref="CreateIndex"/>.</summary>
  /// <remarks>Only a provider with <see cref="DbFeatures.ClusteredStorage"/> uses these; a server that marks a
  /// clustering creates an ordinary index and is told about the order through <see cref="ClusterTable"/>.</remarks>
  public string CreateClusteredIndex = "CREATE CLUSTERED INDEX {0} ON {1} ({2})";
  public string CreateUniqueClusteredIndex = "CREATE UNIQUE CLUSTERED INDEX {0} ON {1} ({2})";

  /// <summary>Specifies an ADD PRIMARY KEY for a key that is explicitly not the table's storage, with the arguments
  /// of <see cref="AddPrimaryKey"/>.</summary>
  /// <remarks>SQL Server makes a primary key clustered unless told otherwise, and a table has one clustered index, so
  /// a table clustered on another key needs this.</remarks>
  public string AddPrimaryKeyNonClustered = "ALTER TABLE {0} ADD CONSTRAINT {1} PRIMARY KEY NONCLUSTERED ({2})";
  public string InlinePrimaryKeyNonClustered = "CONSTRAINT {0} PRIMARY KEY NONCLUSTERED ({1})";
  public string IndexInclude = " INCLUDE ({0})";
  public string IndexFilter = " WHERE {0}";
  public string Descending = " DESC";

  /// <summary>Specifies an ADD FOREIGN KEY; <c>{0}</c> is the table, <c>{1}</c> the constraint name, <c>{2}</c> the
  /// columns, <c>{3}</c> the target table, <c>{4}</c> the target columns.</summary>
  public string AddRefConstraint =
    "ALTER TABLE {0} ADD CONSTRAINT {1} FOREIGN KEY ({2}) REFERENCES {3} ({4})";
  public string CascadeDelete = " ON DELETE CASCADE";

  /// <summary>Specifies a CREATE SEQUENCE; <c>{0}</c> is the name, <c>{1}</c> the start value, <c>{2}</c> the
  /// increment.</summary>
  public string CreateSequence = "CREATE SEQUENCE {0} START WITH {1} INCREMENT BY {2}";

  /// <summary>Specifies an ALTER SEQUENCE; <c>{0}</c> is the name, <c>{1}</c> the increment.</summary>
  /// <remarks>The start value is not restated; a used sequence is past it, and a restart would hand out numbers a
  /// table already holds.</remarks>
  public string AlterSequence = "ALTER SEQUENCE {0} INCREMENT BY {1}";

  /// <summary>Specifies the statement that draws the next values from a sequence as one column of rows; <c>{0}</c>
  /// is the qualified sequence name, <c>{1}</c> the number of values. Empty where the server has no sequences.
  /// </summary>
  public string SequenceNextValues = "";

  // ----- CRUD -------------------------------------------------------------------------------------------------------

  /// <summary>Specifies an insert; <c>{0}</c> is the table, <c>{1}</c> the columns, <c>{2}</c> the value
  /// placeholders.</summary>
  public string Insert = "INSERT INTO {0} ({1})\n  VALUES ({2})";

  /// <summary>Specifies an insert that hands back the key the server generated; <c>{0}</c> is the table, <c>{1}</c>
  /// the columns, <c>{2}</c> the values, <c>{3}</c> the identity column.</summary>
  /// <remarks>The whole insert is the template because the returning clause goes in a different place on each
  /// server. Empty where the server has no identity columns to hand back, and on Oracle, whose
  /// <c>RETURNING … INTO</c> needs an output parameter and whose provider overrides the CRUD builder instead.
  /// </remarks>
  public string InsertReturningIdentity = "";

  /// <summary>Specifies an update; <c>{0}</c> is the table, <c>{1}</c> the assignments, <c>{2}</c> the WHERE
  /// condition.</summary>
  public string Update = "UPDATE {0}\n  SET {1}\n  WHERE {2}";

  /// <summary>Specifies a multi-row insert; <c>{0}</c> is the table, <c>{1}</c> the columns, <c>{2}</c> the rows,
  /// already assembled and joined.</summary>
  /// <remarks>Oracle has no multi-row <c>VALUES</c> and spells the statement as <c>INSERT ALL</c>, repeating the table
  /// and columns on every row, which is what <see cref="InsertManyRow"/> is for.</remarks>
  public string InsertMany = "INSERT INTO {0} ({1})\n  VALUES\n{2}";

  /// <summary>Specifies one row of an <see cref="InsertMany"/>; <c>{0}</c> is the table, <c>{1}</c> the columns,
  /// <c>{2}</c> the row's values.</summary>
  /// <remarks>Consecutive rows are joined by <see cref="InsertManyRowSeparator"/>, a comma in a <c>VALUES</c> list and
  /// nothing where each row carries its own <c>INTO</c>.</remarks>
  public string InsertManyRow = "    ({2})";

  public string InsertManyRowSeparator = ",\n";

  /// <summary>Specifies a delete; <c>{0}</c> is the table, <c>{1}</c> the WHERE condition.</summary>
  public string Delete = "DELETE FROM {0}\n  WHERE {1}";

  /// <summary>Specifies a delete of many rows by a single-column key; <c>{0}</c> is the table, <c>{1}</c> the key
  /// column, <c>{2}</c> the values.</summary>
  /// <remarks>An <c>IN</c> over a composite key has no portable form, so a composite key stays one statement per row.
  /// </remarks>
  public string DeleteMany = "DELETE FROM {0}\n  WHERE {1} IN ({2})";

  /// <summary>Specifies a select; <c>{0}</c> is the columns, <c>{1}</c> the table, <c>{2}</c> the WHERE condition.
  /// </summary>
  public string Select = "SELECT {0} FROM {1} WHERE {2}";

  /// <summary>Specifies what joins the parts of a WHERE condition over a composite key.</summary>
  public string And = " AND ";

  // ----- paging: three templates, because a page with no end is spelled differently everywhere -----------------------
  //
  // {0} is the select as far as ORDER BY, {1} how many rows to skip, {2} how many to take. LIMIT is not optional
  // before OFFSET on MySQL or SQLite, so skip with no take needs a template of its own.

  public string PageSkipTake = "{0} LIMIT {2} OFFSET {1}";

  /// <summary>Specifies a page with a take and no skip; <c>{1}</c> is empty.</summary>
  public string PageTakeOnly = "{0} LIMIT {2}";

  /// <summary>Specifies a page with a skip and no take; <c>{2}</c> is empty.</summary>
  public string PageSkipOnly = "{0} LIMIT ALL OFFSET {1}";

  // ----- a row held for writing: the root select's lock, spelled two ways -------------------------------------------------

  /// <summary>Specifies what follows the root select whose rows are held for update, such as <c> FOR UPDATE</c>;
  /// empty where the lock is a hint on the table or the server has no row locks.</summary>
  public string SelectForUpdate = "";

  /// <summary>Specifies what follows the root select's table when its rows are held for update, such as SQL
  /// Server's <c> WITH (UPDLOCK, ROWLOCK)</c>; empty elsewhere.</summary>
  public string TableHintForUpdate = "";

  /// <summary>Specifies what follows the root select whose rows are held for update with the held ones skipped, such
  /// as <c> FOR UPDATE SKIP LOCKED</c>; empty where the lock is a hint on the table or the server has no row locks.
  /// </summary>
  public string SelectForUpdateSkipLocked = "";

  /// <summary>Specifies what follows the root select's table when its rows are held for update with the held ones
  /// skipped, such as SQL Server's <c> WITH (UPDLOCK, READPAST, ROWLOCK)</c>; empty elsewhere.</summary>
  public string TableHintForUpdateSkipLocked = "";

  // ----- full-text search: a match and a rank over an entity's full-text index, spelled five ways ---------------------

  /// <summary>When overridden in a derived class, builds the condition that a row matches a full-text query.</summary>
  /// <param name="alias">The alias of the row's table.</param>
  /// <param name="index">The full-text index the match is over, whose columns say what text.</param>
  /// <param name="query">The query, rendered as a parameter slot or a literal.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words match.</param>
  /// <param name="label">The number of the match within its statement, the same for a match and the rank of the same
  /// query over the same row, for a server that ties the two by a number, as Oracle does.</param>
  /// <returns>The condition.</returns>
  /// <exception cref="NotSupportedException">the dialect has no spelling for a full-text match.</exception>
  public virtual string BuildTextMatch(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    throw new NotSupportedException($"{GetType().Name} has no spelling for a full-text match.");

  /// <summary>When overridden in a derived class, builds the condition that a row matches a full-text query where
  /// no select holds the row's table for the index's rowset to join, as in the WHERE of a delete or an update of the
  /// table itself.</summary>
  /// <param name="tableAlias">The alias of the row's table.</param>
  /// <param name="index">The full-text index the match is over.</param>
  /// <param name="query">The query, rendered as a parameter slot or a literal.</param>
  /// <returns>The condition, or <see langword="null"/> where the dialect has no spelling without the join.</returns>
  /// <remarks>Asked only of a dialect whose <see cref="FullTextJoin"/> is <see cref="FullTextJoinKind.Mirror"/>; on
  /// the others <see cref="BuildTextMatch"/> needs no join.</remarks>
  public virtual string? BuildTextMatchByKey(string tableAlias, DbKeyInfo index, string query) => null;

  /// <summary>When overridden in a derived class, builds the rank of a row for a full-text query, a number the
  /// server computes, higher for a better match and comparable within one statement only.</summary>
  /// <exception cref="NotSupportedException">the dialect has no spelling for a full-text rank.</exception>
  public virtual string BuildTextRank(string alias, DbKeyInfo index, string query, TextMatchKind kind, int label) =>
    throw new NotSupportedException($"{GetType().Name} has no spelling for a full-text rank.");

  /// <summary>Builds what a full-text index is created over: the index's columns, quoted, or on PostgreSQL the
  /// text-search vector expression its index is over.</summary>
  public virtual string BuildFullTextIndexColumns(DbKeyInfo index) =>
    string.Join(", ", index.Columns.Select(c => c.Column.QuotedName));

  /// <summary>Builds the server's query text for the words of a full-text query under a match kind, which the
  /// parameter's value becomes when the statement is bound.</summary>
  /// <returns>The query text, or <see langword="null"/> where the kind needs no rewriting of the plain words on this
  /// server.</returns>
  public virtual string? BuildTextQuery(string words, TextMatchKind kind, int distance) => null;

  /// <summary>Splits a query into its words, each cleaned to its letters and digits.</summary>
  protected static string[] SplitTextWords(string words) =>
    words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
      .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToArray();

  /// <summary>Specifies how a match and a rank reach the full-text index on this server. <see cref="BuildTextJoin"/>
  /// spells the join where there is one.</summary>
  public FullTextJoinKind FullTextJoin = FullTextJoinKind.None;

  /// <summary>When overridden in a derived class, builds the whole join of the full-text index's rowset to the
  /// entity's table, for a server whose <see cref="FullTextJoin"/> is not <see cref="FullTextJoinKind.None"/>.
  /// </summary>
  /// <param name="joinAlias">The alias of the joined rowset.</param>
  /// <param name="tableAlias">The alias of the entity's table.</param>
  /// <param name="index">The full-text index.</param>
  /// <param name="query">The query the rowset holds the matches of, or <see langword="null"/> for a mirror, which
  /// holds every row.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words match.</param>
  /// <returns>The join clause, an outer one for ranked rows, since a rank is asked of every row.</returns>
  /// <exception cref="NotSupportedException">the dialect has no spelling for a full-text join.</exception>
  public virtual string BuildTextJoin(string joinAlias, string tableAlias, DbKeyInfo index, string? query, TextMatchKind kind) =>
    throw new NotSupportedException($"{GetType().Name} has no spelling for a full-text join.");

  /// <summary>Builds the statements that create a full-text index where it is more than a <c>CREATE INDEX</c> of the
  /// server's own kind, such as SQLite's mirror table with what fills it and keeps it in step.</summary>
  /// <returns>The statements, one each, or <see langword="null"/> where the index is a <c>CREATE INDEX</c> and
  /// nothing else.</returns>
  public virtual IReadOnlyList<string>? BuildFullTextIndex(DbKeyInfo index) => null;

  /// <summary>Builds the statements that drop a full-text index with whatever came with it.</summary>
  /// <returns>The statements, or <see langword="null"/> where a <c>DROP INDEX</c> is the whole of it.</returns>
  public virtual IReadOnlyList<string>? BuildDropFullTextIndex(DbKeyInfo index) => null;

  /// <summary>Builds a one-value statement that answers how far a table's full-text index is behind its rows, zero
  /// once a search sees every committed row.</summary>
  /// <returns>The statement, or <see langword="null"/> where the index is current at the commit, which is every
  /// server but SQL Server.</returns>
  public virtual string? BuildFullTextPendingQuery(DbTableInfo table) => null;

  // ----- common table expressions: one keyword and one hint vary, and nothing else ----------------------------------

  /// <summary>Specifies what starts a <c>WITH</c> list holding a recursive definition; SQL Server and Oracle say plain
  /// <c>WITH</c>. <see langword="null"/> where MIC writes no recursive query for the server, and one is refused at
  /// translation.</summary>
  public string? WithRecursive = "WITH RECURSIVE";

  /// <summary>Specifies what follows a recursive definition's closing bracket, with its leading space; <c>{0}</c> is
  /// the level column, quoted. Empty everywhere but Oracle.</summary>
  /// <remarks>Oracle raises an error when a walk revisits a row; its <c>CYCLE</c> clause over the level column, which
  /// differs at every step, turns that off so the walk is bounded by the level alone.</remarks>
  public string RecursiveCteSuffix = "";

  /// <summary>Specifies what asks for a CTE to be computed once and kept, after <c>AS</c>, with its leading space;
  /// empty where the server has no such word.</summary>
  public string CteMaterialized = "";

  /// <summary>Specifies what asks for a CTE to be folded into the query that reads it.</summary>
  public string CteNotMaterialized = "";

  /// <summary>Builds a <c>value IN list</c> comparison, both parts already written and the list a placeholder.
  /// </summary>
  /// <remarks>A provider that declares <see cref="DbFeatures.ArrayParameters"/> overrides this, since an array
  /// parameter is compared with <c>= ANY(…)</c>.</remarks>
  public virtual string BuildInList(string value, string list) => $"{value} IN ({list})";

  /// <summary>Specifies the most values of a list written into the text as literals; a longer list travels as the
  /// rows of one JSON parameter, read by a select inside the <c>IN</c>, so that the text is the same for every list
  /// and no cap on written values applies.</summary>
  /// <remarks>Not read on a server with <see cref="DbFeatures.ArrayParameters"/>, which binds every list whole.
  /// </remarks>
  public int MaxLiteralListItems = 100;

  public virtual string BuildValueSelect(string value) => string.Format(SelectValue, value);

  /// <summary>Specifies a table source over the items of an array column, one row per item; <c>{0}</c> is the
  /// column, <c>{1}</c> the source's alias, <c>{2}</c> the name of its one column. <see langword="null"/> where
  /// the server has no array columns.</summary>
  public string? ArrayRowsSource;

  /// <summary>When overridden in a derived class, builds a table source over the rows of a JSON parameter, a JSON
  /// array of objects bound as one parameter.</summary>
  /// <param name="parameter">The parameter as the statement writes it, prefix and name.</param>
  /// <param name="columns">The columns a row has, in the order the JSON objects name them, each with the type the
  /// spelling declares or converts to.</param>
  /// <param name="alias">The alias the source's columns are read through, written as given.</param>
  /// <returns>The table source.</returns>
  /// <exception cref="NotSupportedException">the dialect has no spelling for the rows of a JSON parameter.</exception>
  /// <remarks>A Guid a server holds as bytes travels as hex, and the spelling turns it back.</remarks>
  public virtual string JsonRowsSource(string parameter, IReadOnlyList<JsonRowsColumn> columns, string alias) =>
    throw new NotSupportedException("This server has no spelling for the rows of a JSON parameter.");

  // ----- functions: one template table per vendor ------------------------------------------------------------------
  //
  // A function absent from the table is refused by name at translation.

  /// <summary>Specifies how a binary operator is written, by its expression type: the standard's <c>&lt;&gt;</c> for
  /// not-equal, and the four arithmetic operators without their checked distinction.</summary>
  protected readonly Dictionary<ExpressionType, string> OperatorTexts = new() {
    [ExpressionType.Equal] = "=",
    [ExpressionType.NotEqual] = "<>",
    [ExpressionType.LessThan] = "<",
    [ExpressionType.LessThanOrEqual] = "<=",
    [ExpressionType.GreaterThan] = ">",
    [ExpressionType.GreaterThanOrEqual] = ">=",
    [ExpressionType.Add] = "+",
    [ExpressionType.AddChecked] = "+",
    [ExpressionType.Subtract] = "-",
    [ExpressionType.SubtractChecked] = "-",
    [ExpressionType.Multiply] = "*",
    [ExpressionType.MultiplyChecked] = "*",
    [ExpressionType.Divide] = "/",
  };

  /// <summary>Gets the text of a binary operator.</summary>
  /// <param name="type">The operator's expression type.</param>
  /// <returns>The text, without spaces around it.</returns>
  /// <exception cref="NotSupportedException">The operator is not one the dialect writes.</exception>
  public string GetOperatorText(ExpressionType type) =>
    OperatorTexts.TryGetValue(type, out var text) ? text : throw new NotSupportedException($"A '{type}' operator is not translated.");

  protected readonly Dictionary<NamedOpType, string> FunctionTemplates = new() {
    [NamedOpType.Upper] = "UPPER({0})",
    [NamedOpType.Lower] = "LOWER({0})",
    [NamedOpType.Length] = "LENGTH({0})",
    [NamedOpType.Trim] = "TRIM({0})",
    // One-based; the converter has already added the one.
    [NamedOpType.Substring] = "SUBSTRING({0} FROM {1} FOR {2})",
    [NamedOpType.Concat] = "({0} || {1})",
    [NamedOpType.Replace] = "REPLACE({0}, {1}, {2})",
    // The standard form takes the arguments in the reverse of the function's order.
    [NamedOpType.IndexOf] = "POSITION({1} IN {0})",
    // The escape character is an argument, a literal the engine put in the tree from LikeEscaping, so the character
    // the value was escaped with and the one the SQL declares are the same.
    [NamedOpType.Like] = "({0} LIKE {1} ESCAPE {2})",
    [NamedOpType.Year] = "EXTRACT(YEAR FROM {0})",
    [NamedOpType.Month] = "EXTRACT(MONTH FROM {0})",
    [NamedOpType.Day] = "EXTRACT(DAY FROM {0})",
    [NamedOpType.Date] = "CAST({0} AS DATE)",
    [NamedOpType.Abs] = "ABS({0})",
    [NamedOpType.Round] = "ROUND({0})",
    [NamedOpType.RoundTo] = "ROUND({0}, {1})",
    [NamedOpType.Floor] = "FLOOR({0})",
    [NamedOpType.Ceiling] = "CEILING({0})",
    [NamedOpType.Power] = "POWER({0}, {1})",
    [NamedOpType.Sqrt] = "SQRT({0})",
    [NamedOpType.Sign] = "SIGN({0})",
    [NamedOpType.Modulo] = "({0} % {1})",
    [NamedOpType.Coalesce] = "COALESCE({0}, {1})",
    [NamedOpType.IsNull] = "{0} IS NULL",
    [NamedOpType.IsNotNull] = "{0} IS NOT NULL",
  };

  /// <summary>Gets how the server wants a <c>LIKE</c> pattern escaped: the escape character and the characters it
  /// goes in front of.</summary>
  /// <remarks>A vendor overrides this where a backslash is awkward or where it reads more characters as wildcards
  /// than <c>%</c> and <c>_</c>.</remarks>
  public virtual LikeEscaping LikeEscaping => LikeEscaping.Default;

  /// <summary>Gets the keyword that combines two selects.</summary>
  /// <remarks>Oracle calls <c>EXCEPT</c> <c>MINUS</c> and overrides this.</remarks>
  public virtual string GetSetOperator(SetOperationType opType) => opType switch {
    SetOperationType.Union => "UNION",
    SetOperationType.Concat => "UNION ALL",
    SetOperationType.Intersect => "INTERSECT",
    SetOperationType.Except => "EXCEPT",
    _ => throw new NotSupportedException($"'{opType}' is not a set operation."),
  };

  /// <summary>Builds a function call from the function's template, with its arguments already written.</summary>
  /// <exception cref="NotSupportedException">the dialect has no template for the function.</exception>
  public virtual string BuildNamedOpText(NamedOpType function, IReadOnlyList<string> arguments) {
    if (!FunctionTemplates.TryGetValue(function, out var template))
      throw new NotSupportedException(
        $"{GetType().Name} has no form for '{function}' — the query cannot be translated for this server.");
    return arguments.Count switch {
      1 => string.Format(template, arguments[0]),
      2 => string.Format(template, arguments[0], arguments[1]),
      _ => string.Format(template, arguments.ToArray()),
    };
  }

  /// <summary>Builds a column reference into the rows of a JSON document from <see cref="JsonRowsSource"/>, read
  /// under the source's alias.</summary>
  /// <remarks>A server whose rows source hands out each element whole reads the column out of the element here.
  /// </remarks>
  public virtual string JsonRowsColumn(string alias, JsonRowsColumn column) => $"{alias}.{Quote(column.Name)}";

  /// <summary>Builds the source the rows of a parameter are read from where they were staged in a temp table rather
  /// than bound: the table under the alias the JSON spelling would have used, so every column reference stays as
  /// written.</summary>
  /// <param name="tableRef">The temp table, as the statement refers to it.</param>
  /// <param name="alias">The alias the rows are read under.</param>
  /// <returns>The source, alias included.</returns>
  public virtual string JsonRowsTempSource(string tableRef, string alias) => $"{tableRef} AS {alias}";

  /// <summary>Gets the columns of the temp table the rows of a parameter are staged in: the columns as
  /// <see cref="JsonRowsSource"/> declares them, so that <see cref="JsonRowsColumn"/> reads them unchanged.</summary>
  /// <param name="columns">The rows' columns.</param>
  /// <returns>The temp table's columns, each with the form a value takes in it.</returns>
  public virtual IReadOnlyList<JsonRowsTempColumn> GetJsonRowsTempColumns(IReadOnlyList<JsonRowsColumn> columns) =>
    [.. columns.Select(c => new JsonRowsTempColumn(c.Name, c.Type.TypeSpec, RowsTempValueForm.Value))];

  public virtual string JoinJsonRows(string source, string alias, string? condition, bool outer) =>
    condition == null ? $"CROSS JOIN {source}"
    : outer ? $"LEFT OUTER JOIN {source} ON {condition}"
    : $"INNER JOIN {source} ON {condition}";

  /// <summary>Builds the read of an object or an array out of a JSON document by path, as JSON, the SQL standard's
  /// <c>JSON_QUERY</c> by default.</summary>
  /// <returns>The expression, which evaluates to null where the path names nothing.</returns>
  public virtual string BuildJsonFragment(string document, string path) => $"JSON_QUERY({document}, {path})";

  /// <summary>Builds the read of a value out of a JSON document by path, as text or converted to a type, the SQL
  /// standard's <c>JSON_VALUE</c> and a <c>CAST</c> by default.</summary>
  /// <param name="document">The document as the statement writes it, a column or a parameter.</param>
  /// <param name="path">The path as the statement writes it, a literal such as <c>'$.a.b'</c>.</param>
  /// <param name="type">The type to read the value as, or <see langword="null"/> for text.</param>
  /// <returns>The expression, which evaluates to null where the path names nothing.</returns>
  public virtual string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    var text = $"JSON_VALUE({document}, {path})";
    return type == null ? text : BuildCast(text, type);
  }

  /// <summary>Builds the conversion of a value to a type, as this server spells it.</summary>
  public virtual string BuildCast(string value, DbTypeInfo type) => $"CAST({value} AS {type.TypeSpec})";

  /// <summary>Builds an update's value for a JSON column changed in place: the column with only the given paths
  /// written into it, a missing path created and a path marked <see cref="JsonPatch.IsRemoval"/> removed.</summary>
  /// <param name="column">The column as the statement writes it.</param>
  /// <param name="patches">The paths to write.</param>
  /// <param name="bind">A function that binds a value, as JSON text in a <see cref="JsonText"/> or as a scalar, and
  /// returns what the statement writes for it.</param>
  /// <returns>The value, or <see langword="null"/> where this server has no spelling for these patches, in which case
  /// the whole document is written.</returns>
  public virtual string? BuildJsonSet(string column, IReadOnlyList<JsonPatch> patches, Func<object?, string> bind) => null;

  /// <summary>Builds a paged select; either bound may be absent, and with both absent the select is returned
  /// untouched.</summary>
  public virtual string BuildPage(string select, string? skip, string? take) => (skip, take) switch {
    (not null, not null) => string.Format(PageSkipTake, select, skip, take),
    (null, not null) => string.Format(PageTakeOnly, select, "", take),
    (not null, null) => string.Format(PageSkipOnly, select, skip, ""),
    _ => select,
  };

  // ----- the batch: several statements in one command --------------------------------------------------------------
  //
  // The transaction is in the text rather than begun through the driver, since a driver's begin and commit are the
  // round trips the batch exists to save. Oracle takes no statement list and wraps the batch in a PL/SQL block.

  /// <summary>Specifies what opens a batch command; empty everywhere but Oracle, which needs a PL/SQL block.</summary>
  public string BatchBegin = "";

  /// <summary>Specifies what closes a batch command, the other half of <see cref="BatchBegin"/>.</summary>
  public string BatchEnd = "";

  /// <summary>Specifies what starts the transaction a batch runs in.</summary>
  public string BatchBeginTransaction = "BEGIN TRANSACTION;";

  /// <summary>Specifies what commits the transaction a batch runs in.</summary>
  /// <remarks>The commit is never reached when a statement fails; the executor then sends
  /// <see cref="BatchRollbackTransaction"/> as its own command.</remarks>
  public string BatchCommitTransaction = "COMMIT TRANSACTION;";

  /// <summary>Specifies what undoes a batch whose commit was never reached, sent as its own command after a failure.
  /// </summary>
  public string BatchRollbackTransaction = "ROLLBACK";

  // ----- a generated key carried through the batch; see DbFeatures.IdentityCarriedInBatch ---------------------------

  /// <summary>Specifies the statement that puts the key an insert just made into a slot, written after the insert in
  /// the same batch; <c>{0}</c> is the slot's name with its prefix, such as <c>@id3</c>.</summary>
  /// <remarks>A later statement of the batch writes the slot where the key goes. Empty where the insert captures the
  /// key itself, as Oracle's does, or where the server does not carry one.</remarks>
  public string BatchCaptureIdentity = "";

  /// <summary>Specifies what reads the slots back at the end of a batch where they are session variables rather than
  /// parameters, such as MySQL's <c>SELECT @id3, @id7</c>; <c>{0}</c> is the slots, comma-separated, in the order
  /// their records were inserted. Empty where a slot is an output parameter.</summary>
  public string BatchReadIdentities = "";

  public string StatementSeparator = ";\n\n";

  // ----- server values an application asks the session for; see EntitySessionHelper -------------------------------

  /// <summary>Specifies the statement that selects the server's current time in UTC, as one value. Empty where the
  /// server offers none.</summary>
  public string SelectServerTimeUtc = "";

  /// <summary>Specifies the statement that selects the name the connection's user is known to the server by, as one
  /// value. Empty where the server has no users.</summary>
  public string SelectServerUser = "";

  /// <summary>Specifies the statement that selects the name of the database the connection is in, as one value.
  /// </summary>
  public string SelectServerDatabase = "";

  // ----- the ALTER half: what an upgrade needs and a fresh CREATE never does ---------------------------------------

  /// <summary>Specifies an ADD COLUMN; <c>{0}</c> is the table, <c>{1}</c> the column definition.</summary>
  public string AddColumn = "ALTER TABLE {0} ADD {1}";

  /// <summary>Specifies the statement run before a computed column is added to a table that exists; <c>{0}</c> is
  /// the table. <see langword="null"/> where the ADD COLUMN is enough.</summary>
  /// <remarks>Db2 adds a generated column only to a table whose integrity checking is off.</remarks>
  public string? BeforeAddComputedColumn;

  /// <summary>Specifies the statement run after a computed column is added to a table that exists, the other half
  /// of <see cref="BeforeAddComputedColumn"/>; <c>{0}</c> is the table.</summary>
  public string? AfterAddComputedColumn;

  /// <summary>Specifies the statement run after a column of a table was dropped or its type or its nullability
  /// changed; <c>{0}</c> is the table. <see langword="null"/> where the server needs none.</summary>
  /// <remarks>Db2 refuses to read or write a table after such a change until the table is reorganized
  /// (SQL0668N, reason 7), so its statement is the <c>REORG</c>.</remarks>
  public string? AfterTableModified;

  /// <summary>Specifies a DROP COLUMN; <c>{0}</c> is the table, <c>{1}</c> the column.</summary>
  public string DropColumn = "ALTER TABLE {0} DROP COLUMN {1}";

  /// <summary>Specifies a column type change; <c>{0}</c> is the table, <c>{1}</c> the column, <c>{2}</c> the type.
  /// </summary>
  public string AlterColumnType = "ALTER TABLE {0} ALTER COLUMN {1} TYPE {2}";

  /// <summary>Specifies a column alteration that restates the whole definition, for a server with
  /// <see cref="DbFeatures.AlterColumnRestatesDefinition"/>; <c>{0}</c> is the table, <c>{1}</c> the column
  /// definition.</summary>
  public string ModifyColumn = "ALTER TABLE {0} MODIFY COLUMN {1}";

  /// <summary>Specifies the backfill of a column's null rows; <c>{0}</c> is the table, <c>{1}</c> the column,
  /// <c>{2}</c> the placeholder value.</summary>
  public string InitializeColumn = "UPDATE {0} SET {1} = {2} WHERE {1} IS NULL";

  /// <summary>Specifies a nullability change; <c>{0}</c> is the table, <c>{1}</c> the column.</summary>
  public string SetNotNull = "ALTER TABLE {0} ALTER COLUMN {1} SET NOT NULL";
  public string DropNotNull = "ALTER TABLE {0} ALTER COLUMN {1} DROP NOT NULL";

  /// <summary>Specifies a default change; <c>{0}</c> is the table, <c>{1}</c> the column, <c>{2}</c> the expression,
  /// <c>{3}</c> the default's own name where it has one.</summary>
  /// <remarks><c>{3}</c> is empty except on a server with <see cref="DbFeatures.NamedDefaultConstraints"/>, where the
  /// name came from introspection.</remarks>
  public string SetDefault = "ALTER TABLE {0} ALTER COLUMN {1} SET DEFAULT {2}";
  public string DropDefault = "ALTER TABLE {0} ALTER COLUMN {1} DROP DEFAULT";

  /// <summary>Specifies a table rename; <c>{0}</c> is the table as it is, <c>{1}</c> the new name, unqualified.
  /// </summary>
  public string RenameTable = "ALTER TABLE {0} RENAME TO {1}";

  /// <summary>Specifies a column rename; <c>{0}</c> is the table, <c>{1}</c> the old column, <c>{2}</c> the new
  /// column.</summary>
  public string RenameColumn = "ALTER TABLE {0} RENAME COLUMN {1} TO {2}";

  public string DropTable = "DROP TABLE {0}";

  /// <summary>Specifies a DROP INDEX; <c>{0}</c> is the index name qualified by its table's schema, <c>{1}</c> the
  /// table, <c>{2}</c> the index name alone.</summary>
  /// <remarks>A server where an index belongs to its table names the table and takes the name alone.</remarks>
  public string DropIndex = "DROP INDEX {0}";
  public string DropSequence = "DROP SEQUENCE {0}";
  public string DropSchema = "DROP SCHEMA {0}";

  /// <summary>Specifies a DROP CONSTRAINT; <c>{0}</c> is the table, <c>{1}</c> the constraint.</summary>
  public string DropConstraint = "ALTER TABLE {0} DROP CONSTRAINT {1}";

  /// <summary>Specifies an ADD CHECK; <c>{0}</c> is the table, <c>{1}</c> the constraint name, <c>{2}</c> the
  /// condition.</summary>
  public string AddCheckConstraint = "ALTER TABLE {0} ADD CONSTRAINT {1} CHECK ({2})";

  /// <summary>Specifies a check constraint inside a CREATE TABLE; <c>{0}</c> is the constraint name, <c>{1}</c> the
  /// condition.</summary>
  public string InlineCheckConstraint = "CONSTRAINT {0} CHECK ({1})";

  /// <summary>Specifies the drop of a check constraint; <c>{0}</c> is the table, <c>{1}</c> the constraint.</summary>
  public string DropCheckConstraint = "ALTER TABLE {0} DROP CONSTRAINT {1}";

  /// <summary>Specifies the condition that a text column holds JSON; <c>{0}</c> is the column.
  /// <see langword="null"/> on a server with no such test.</summary>
  /// <remarks>Where a JSON member's column is text, the model gives the column a check constraint with this
  /// condition. A server with a JSON type refuses other text itself and needs none.</remarks>
  public string? JsonTextCheck;

  /// <summary>Specifies the drop of a primary key; <c>{0}</c> is the table, <c>{1}</c> the constraint name.</summary>
  /// <remarks>MySQL has <c>DROP PRIMARY KEY</c> with no name, since it named the key itself.</remarks>
  public string DropPrimaryKey = "ALTER TABLE {0} DROP CONSTRAINT {1}";

  // ----- session locks: {0} is a 64-bit key derived from the application name ---------------------------------------

  /// <summary>Specifies the statement that takes the session lock if it is free and returns a single truthy or
  /// falsy value without blocking; empty where the server has no session locks.</summary>
  public string TryAcquireSessionLock = "";

  public string ReleaseSessionLock = "";

  /// <summary>Specifies the privilege a user needs to take the session lock, named in the warning when the request
  /// fails; empty where the lock needs none beyond a connection.</summary>
  public string SessionLockGrantHint = "";

  /// <summary>Specifies the statement that attaches a definition hash to an index as a comment; <c>{0}</c> is the
  /// qualified index, <c>{1}</c> the comment literal, and <c>{2}</c>, <c>{3}</c> and <c>{4}</c> the schema, table and
  /// index names, each as a literal.</summary>
  /// <remarks>Only a provider without <see cref="DbFeatures.ServerPreservesComments"/> uses this. The separate names
  /// are for SQL Server, whose <c>sp_addextendedproperty</c> wants the object as three levels.</remarks>
  public string CommentOnIndex = "COMMENT ON INDEX {0} IS {1}";

  // ----- views ------------------------------------------------------------------------------------------------------

  /// <summary>Specifies a CREATE VIEW; <c>{0}</c> is the view, <c>{1}</c> its column list in parentheses or empty,
  /// <c>{2}</c> the defining select.</summary>
  public string CreateView = "CREATE VIEW {0}{1} AS\n{2}";

  /// <summary>Specifies a CREATE MATERIALIZED VIEW, with the same placeholders as <see cref="CreateView"/>; used
  /// only where the provider has <see cref="DbFeatures.MaterializedViews"/>.</summary>
  public string CreateMaterializedView = "CREATE MATERIALIZED VIEW {0}{1} AS\n{2}";

  public string DropView = "DROP VIEW {0}";
  public string DropMaterializedView = "DROP MATERIALIZED VIEW {0}";

  /// <summary>Specifies the statement that recomputes a materialized view's rows; <c>{0}</c> is the view.</summary>
  public string RefreshMaterializedView = "REFRESH MATERIALIZED VIEW {0}";

  /// <summary>Specifies where the server keeps the hash of a view's definition for MIC to read back.</summary>
  public ViewHashCarrier ViewHashCarrier = ViewHashCarrier.TextComment;

  /// <summary>Specifies the statement that attaches a definition hash to a view as a comment; <c>{0}</c> is the
  /// view, <c>{1}</c> the comment literal. Used with <see cref="ViewHashCarrier.Comment"/>.</summary>
  public string CommentOnView = "COMMENT ON VIEW {0} IS {1}";

  /// <summary>Specifies the comment statement for a materialized view, with the placeholders of
  /// <see cref="CommentOnView"/>.</summary>
  public string CommentOnMaterializedView = "COMMENT ON MATERIALIZED VIEW {0} IS {1}";

  /// <summary>Specifies the select that wraps a view's defining select to carry the hash as a literal the server
  /// keeps in the definition; <c>{0}</c> is the select, <c>{1}</c> the hash literal. Used with
  /// <see cref="ViewHashCarrier.Literal"/>.</summary>
  public string ViewHashLiteral = "SELECT * FROM (\n{0}\n) AS mic_view WHERE {1} = {1}";
}
