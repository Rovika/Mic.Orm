using System.Globalization;
using Mic.Common;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Db2;

/// <summary>Provides the Db2 SQL dialect.</summary>
/// <remarks>A select reads a table always, so a select of a value alone reads <c>SYSIBM.SYSDUMMY1</c>, the
/// catalog's table of one row. A batch is a compound statement, <c>BEGIN ATOMIC … END</c>, which the server runs
/// whole or not at all. The rows of a parameter travel as XML and are read with <c>XMLTABLE</c>, since
/// <c>JSON_TABLE</c> spreads no array into rows.</remarks>
public class Db2SqlDialect : SqlDialect {
  // The type text takes where the server has to be told one: a parameter standing alone, a value read out of a JSON
  // document.
  const string UntypedTextType = "VARCHAR(4000)";

  public Db2SqlDialect() {
    SelectOne = "SELECT 1 FROM SYSIBM.SYSDUMMY1";
    SelectValue = "SELECT {0} FROM SYSIBM.SYSDUMMY1";
    // CURRENT TIMEZONE is the server's offset from UTC as a duration.
    SelectServerTimeUtc = "SELECT CURRENT TIMESTAMP - CURRENT TIMEZONE FROM SYSIBM.SYSDUMMY1";
    SelectServerUser = "SELECT CURRENT USER FROM SYSIBM.SYSDUMMY1";
    SelectServerDatabase = "SELECT CURRENT SERVER FROM SYSIBM.SYSDUMMY1";

    PageSkipTake = "{0} OFFSET {1} ROWS FETCH NEXT {2} ROWS ONLY";
    PageTakeOnly = "{0} FETCH FIRST {2} ROWS ONLY";
    PageSkipOnly = "{0} OFFSET {1} ROWS";

    // The key comes back as the row of a select over the insert.
    InsertReturningIdentity = "SELECT {3} FROM FINAL TABLE (INSERT INTO {0} ({1}) VALUES ({2}))";

    // No statement begins a transaction, and a command is one statement: the batch is a compound statement, atomic
    // by itself, and under the executor's transaction where there is one.
    BatchBegin = "BEGIN ATOMIC";
    BatchEnd = "END";
    BatchBeginTransaction = "";
    BatchCommitTransaction = "";

    // AVG over an integer column is an integer.
    IntegerAverage = "AVG(CAST({0} AS DOUBLE))";

    // The recursive part of a common table expression takes no join with ON (SQL0345N), which is how MIC writes
    // one; a recursive query is refused.
    WithRecursive = null;

    // A function's rows are named with TABLE, and the call may read the row before it in the FROM as it stands.
    TableFunctionSource = "TABLE({0}({1})) AS {2}";
    LateralJoin = "CROSS JOIN {0}";

    // The lock is a clause of the isolation: the rows read are held as an update would hold them, until the
    // transaction ends.
    SelectForUpdate = "\n  WITH RS USE AND KEEP UPDATE LOCKS";
    SelectForUpdateSkipLocked = "\n  WITH RS USE AND KEEP UPDATE LOCKS SKIP LOCKED DATA";

    // No generator of rows: a recursive list of numbers gives as many rows as values are wanted.
    SequenceNextValues = "WITH n (i) AS (SELECT 1 FROM SYSIBM.SYSDUMMY1 UNION ALL SELECT i + 1 FROM n WHERE i < {1})\n"
      + "SELECT NEXT VALUE FOR {0} FROM n";

    // A declared temporary table lives in the schema SESSION, as long as the connection, and needs a user temporary
    // table space in the database.
    CreateTempTable = "DECLARE GLOBAL TEMPORARY TABLE {0} (\n{1}\n) ON COMMIT PRESERVE ROWS NOT LOGGED";

    // An index goes to the schema its name is qualified with, and to the connected user's where the name is bare.
    CreateIndex = "CREATE {4}INDEX {3} ON {1} ({2}){5}";
    CreateUniqueIndex = "CREATE UNIQUE {4}INDEX {3} ON {1} ({2}){5}";
    DropSchema = "DROP SCHEMA {0} RESTRICT";
    AlterColumnType = "ALTER TABLE {0} ALTER COLUMN {1} SET DATA TYPE {2}";
    RenameTable = "RENAME TABLE {0} TO {1}";
    // A generated column is stored; there is no virtual kind and no word for either.
    ComputedStored = "";
    ComputedVirtual = "";
    ComputedStorage = ComputedStorage.Stored;
    // A table that exists takes a generated column only with its integrity checking off (SQL20054N, reason 22);
    // turning it back on computes the column for the rows there are.
    BeforeAddComputedColumn = "SET INTEGRITY FOR {0} OFF";
    AfterAddComputedColumn = "SET INTEGRITY FOR {0} IMMEDIATE CHECKED FORCE GENERATED";

    // A dropped column, a changed type or a changed nullability leaves the table closed to every statement but a
    // few until it is reorganized (SQL0668N, reason 7). REORG is a command, run through the procedure that takes
    // one.
    AfterTableModified = "CALL SYSPROC.ADMIN_CMD('REORG TABLE {0}')";

    // The standard's SUBSTRING and POSITION want the string unit named; these count as the column does.
    FunctionTemplates[NamedOpType.Substring] = "SUBSTR({0}, {1}, {2})";
    FunctionTemplates[NamedOpType.IndexOf] = "LOCATE({1}, {0})";
    FunctionTemplates[NamedOpType.Modulo] = "MOD({0}, {1})";
    FunctionTemplates[NamedOpType.Round] = "ROUND({0}, 0)";
  }

  /// <summary>Writes a Guid as a binary literal, since the column holding one is <c>binary(16)</c>.</summary>
  /// <param name="value">The Guid.</param>
  /// <returns>The literal, the bytes in RFC order.</returns>
  public override string GuidLiteral(Guid value) => BinaryLiteral(ConvertHelper.GuidToBytes(value));

  /// <summary>Writes a binary value as a binary string literal, <c>BX'…'</c>.</summary>
  /// <param name="value">The bytes.</param>
  /// <returns>The literal.</returns>
  /// <remarks>A plain <c>X'…'</c> is a character string <c>FOR BIT DATA</c>, which a binary column refuses in a
  /// comparison.</remarks>
  public override string BinaryLiteral(byte[] value) => "BX'" + Convert.ToHexString(value) + "'";

  /// <summary>Writes a timestamp as a literal in the <c>TIMESTAMP '…'</c> form, to milliseconds.</summary>
  /// <param name="value">The timestamp.</param>
  /// <returns>The literal.</returns>
  /// <remarks>A bare quoted string is a character value wherever nothing beside it says timestamp, as in a select
  /// list.</remarks>
  public override string DateTimeLiteral(DateTime value) =>
    "TIMESTAMP '" + value.ToString("yyyy-MM-dd HH:mm:ss.fff", Invariant) + "'";

  /// <summary>Writes a value as a literal, a date in the <c>DATE '…'</c> form and a time in the <c>TIME '…'</c>
  /// form, to the second.</summary>
  /// <param name="value">The value, or <see langword="null"/>.</param>
  /// <returns>The literal, or <see langword="null"/> if the value has to be bound.</returns>
  public override string? GetLiteral(object? value) => value switch {
    DateOnly date => "DATE '" + date.ToString("yyyy-MM-dd", Invariant) + "'",
    TimeOnly time => "TIME '" + time.ToString("HH:mm:ss", Invariant) + "'",
    _ => base.GetLiteral(value),
  };

  /// <summary>Builds a parameter or a null with its type stated, as a cast.</summary>
  /// <param name="value">The value as the statement writes it.</param>
  /// <param name="clrType">The CLR type of the value.</param>
  /// <param name="type">The database type a parameter of the CLR type maps to, or <see langword="null"/>.</param>
  /// <returns>The cast, or the value itself where no type is known.</returns>
  /// <remarks>The server types a parameter by what stands beside it and refuses one that stands alone
  /// (SQL0418N). Text is typed <c>VARCHAR(4000)</c>, since a <c>CLOB</c> takes no comparison and no ordering; a
  /// longer value in such a place is refused.</remarks>
  public override string BuildUntypedValue(string value, Type clrType, DbTypeInfo? type) {
    var typeSpec = clrType == typeof(string) ? UntypedTextType : type?.TypeSpec;
    return typeSpec == null ? value : $"CAST({value} AS {typeSpec})";
  }

  /// <summary>Gets the name a statement refers to a temp table by, in the schema <c>SESSION</c>.</summary>
  /// <param name="schema">The schema of the model the table serves, which a declared temporary table is not in.
  /// </param>
  /// <param name="name">The temp table's name.</param>
  /// <returns>The reference.</returns>
  public override string GetTempTableRef(string schema, string name) => "SESSION." + Quote(name);

  /// <summary>Builds the statement that creates a SQL table function or replaces it, with its columns declared and
  /// the body as the select it returns.</summary>
  /// <param name="table">The function's table.</param>
  /// <param name="body">The defining select, with the hash comment in front.</param>
  /// <returns>The statement.</returns>
  /// <remarks>A parameter's name is written bare, so the body names it in any case.</remarks>
  public override string? BuildTableFunction(DbTableInfo table, string body) {
    var function = table.TableFunction!;
    var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name} {p.TypeSpec}"));
    var columns = string.Join(", ", table.Columns.Select(c => $"{Quote(c.Name)} {c.TypeSpec}"));
    return $"CREATE OR REPLACE FUNCTION {table.RefName}({parameters}) RETURNS TABLE ({columns})\n"
      + "LANGUAGE SQL READS SQL DATA RETURN\n" + body;
  }

  // ----- the rows of a parameter: XML read by XMLTABLE ---------------------------------------------------------------

  /// <summary>Builds the table source over the rows of a parameter, an XML document that
  /// <see cref="XmlRowsHelper.Serialize"/> wrote, read with <c>XMLTABLE</c>.</summary>
  /// <param name="parameter">The parameter as the statement writes it.</param>
  /// <param name="columns">The columns of a row.</param>
  /// <param name="alias">The alias of the source.</param>
  /// <returns>The source.</returns>
  /// <remarks><c>XMLTABLE</c> casts no value to a binary or a Boolean type: a Guid is declared as its hex text and a
  /// Boolean as a small integer, and <see cref="JsonRowsColumn"/> turns both back. White space is preserved, so a
  /// string of spaces arrives as it left.</remarks>
  public override string JsonRowsSource(string parameter, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    var declared = string.Join(", ", columns.Select((c, i) => $"{Quote(c.Name)} {GetRowsColumnType(c)} PATH "
      + $"'{XmlRowsHelper.ColumnPrefix}{i.ToString(CultureInfo.InvariantCulture)}'"));
    return $"XMLTABLE('$d/{XmlRowsHelper.RootName}/{XmlRowsHelper.RowName}' "
      + $"PASSING XMLPARSE(DOCUMENT CAST({parameter} AS CLOB(16M)) PRESERVE WHITESPACE) AS \"d\" "
      + $"COLUMNS {declared}) AS {alias}";
  }

  /// <summary>Builds the reference to a column of the rows of a parameter, a Guid turned back from its hex text
  /// and a Boolean from its number.</summary>
  /// <param name="alias">The alias of the source.</param>
  /// <param name="column">The column.</param>
  /// <returns>The column expression.</returns>
  public override string JsonRowsColumn(string alias, JsonRowsColumn column) {
    var reference = $"{alias}.{Quote(column.Name)}";
    var clrType = column.Type.ClrType;
    return clrType == typeof(Guid) ? $"HEXTORAW({reference})"
      : clrType == typeof(bool) ? $"CAST({reference} AS BOOLEAN)"
      : reference;
  }

  /// <inheritdoc/>
  /// <remarks>The columns as <c>XMLTABLE</c> declares them: a Guid as the hex text <c>HEXTORAW</c> reads.</remarks>
  public override IReadOnlyList<JsonRowsTempColumn> GetJsonRowsTempColumns(IReadOnlyList<JsonRowsColumn> columns) =>
    [.. columns.Select(c => c.Type.ClrType == typeof(Guid)
      ? new JsonRowsTempColumn(c.Name, "VARCHAR(32)", RowsTempValueForm.GuidText)
      : new JsonRowsTempColumn(c.Name, c.Type.TypeSpec, RowsTempValueForm.Value))];

  // ----- JSON held as text -------------------------------------------------------------------------------------------

  /// <summary>Builds the read of a value out of a JSON document with <c>JSON_VALUE</c>, typed with
  /// <c>RETURNING</c>.</summary>
  /// <param name="document">The document as the statement writes it.</param>
  /// <param name="path">The path as the statement writes it.</param>
  /// <param name="type">The type to read the value as, or <see langword="null"/> for text.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="NotSupportedException">A JSON value cannot be read as the type on Db2.</exception>
  /// <remarks>Without <c>RETURNING</c> the value is a <c>CLOB</c>, which takes no cast and no comparison. Text is
  /// read as <c>VARCHAR(4000)</c>. <c>RETURNING</c> takes no Boolean type, so a Boolean is the text compared with
  /// <c>true</c>.</remarks>
  public override string BuildJsonValue(string document, string path, DbTypeInfo? type) {
    if (type == null)
      return $"JSON_VALUE({document}, {path} RETURNING {UntypedTextType})";
    var clrType = Nullable.GetUnderlyingType(type.ClrType) ?? type.ClrType;
    if (clrType == typeof(Guid) || clrType == typeof(byte[]))
      throw new NotSupportedException($"A JSON value cannot be read as '{clrType.Name}' on Db2.");
    if (clrType == typeof(bool))
      return $"(JSON_VALUE({document}, {path} RETURNING VARCHAR(5)) = 'true')";
    var returning = clrType == typeof(string) ? UntypedTextType : type.TypeSpec;
    return $"JSON_VALUE({document}, {path} RETURNING {returning})";
  }

  private static string GetRowsColumnType(JsonRowsColumn column) {
    var clrType = column.Type.ClrType;
    return clrType == typeof(Guid) ? "VARCHAR(32)"
      : clrType == typeof(bool) ? "SMALLINT"
      : column.Type.TypeSpec;
  }
}
