using System.Linq.Expressions;
using System.Text;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Sql;

/// <summary>Writes the SQL of a translation's SQL tree as a <see cref="SqlTemplate"/> template for one database,
/// using its dialect, its db model and its provider.</summary>
/// <remarks>Everything that varies with data is a placeholder, so the text varies with the query's shape alone: a
/// constant is written as a literal unless the server has no literal form for it, and a list is always a placeholder.
/// An empty select list means the whole row, every stored member of the result set's entity in the model's order,
/// each aliased as its own name where the column is called something else.</remarks>
public sealed class SqlBuilder {
  // A character no SQL contains, bracketing a parameter's index in the buffer until the cut; a literal that holds it
  // is bound instead of written.
  const char Mark = '';

  // A null as every dialect writes it, which says no type where nothing beside it gives one.
  const string NullLiteral = "NULL";

  // The width a string column of a parameter's rows is declared with when nothing says: the widest bounded form every
  // server takes.
  const int UnsizedStringWidth = 4000;

  readonly DbModel _dbModel;
  readonly SqlDialect _dialect;
  readonly List<SqlPlaceholder> _placeholders = [];
  readonly Dictionary<string, SqlPlaceholder> _byName = new(StringComparer.Ordinal);
  readonly Dictionary<JsonRowsNode, JsonRowsColumn[]> _rowsColumns = new(ReferenceEqualityComparer.Instance);
  int _argumentCount;

  /// <summary>The selects being written, outermost first, each with the full-text rowsets a match or a rank inside it
  /// asked to stand beside one of its tables, on a server that reaches the index through a join.</summary>
  readonly List<TextJoinFrame> _textFrames = [];

  /// <summary>Represents one joined full-text rowset: the table it stands beside, the index, the query it holds the
  /// matches of, <see langword="null"/> for a mirror, and its alias.</summary>
  sealed record TextJoin(TableNode Table, DbKeyInfo Index, string? Query, TextMatchKind Kind, string Alias);

  /// <summary>The statement's full-text labels, one per distinct query over a row; see
  /// <see cref="SqlDialect.BuildTextMatch"/>.</summary>
  readonly Dictionary<string, int> _textLabels = new(StringComparer.Ordinal);

  /// <summary>The root select's own table while its FROM is written, where its rows are held for update and the
  /// dialect spells the lock as a hint on the table; <see langword="null"/> otherwise.</summary>
  RowSetNode? _lockedSource;
  string _lockHint = "";

  sealed class TextJoinFrame(SelectNode select) {
    public SelectNode Select { get; } = select;
    public List<TextJoin> Joins { get; } = [];
  }

  // ----- layout ----------------------------------------------------------------------------------------------------------
  //
  // One clause per line, a join on its own line under the FROM, a nested select indented as a block, a long select
  // list wrapped. The line ending is always \n, so the text and its cache key are the same on every machine.

  /// <summary>How deep the select being written is nested, which a line break indents by.</summary>
  int _depth;

  const int WrapWidth = 100;

  /// <summary>Starts a new line at a depth.</summary>
  private static void NewLine(StringBuilder sql, int depth) => sql.Append('\n').Append(' ', depth * 2);

  /// <summary>Writes a nested select as an indented block inside brackets, closed on a line of their own.</summary>
  private void WriteBracketedSelect(StringBuilder sql, Action<StringBuilder> write) {
    sql.Append('(');
    _depth++;
    NewLine(sql, _depth);
    write(sql);
    _depth--;
    NewLine(sql, _depth);
    sql.Append(')');
  }

  private SqlBuilder(DbModel dbModel) {
    _dbModel = dbModel;
    _dialect = dbModel.Provider.SqlDialect;
  }

  /// <summary>Builds the statement for a translation's SQL tree against a database.</summary>
  /// <param name="translation">The translation, which must have a SQL tree and a result description.</param>
  /// <param name="dbModel">The db model of the database.</param>
  /// <returns>The statement template.</returns>
  public static SqlTemplate Build(TranslatedQuery translation, DbModel dbModel) {
    ArgumentNullException.ThrowIfNull(translation);
    ArgumentNullException.ThrowIfNull(dbModel);
    var root = translation.SqlRoot;
    var builder = new SqlBuilder(dbModel);
    // The root is written first and the WITH list put in front of it; a parameter is found by its slot, not by where
    // it stands, so the order of writing is free.
    var statement = builder.WriteRoot(root);
    return builder.Finish(builder.WriteWith(translation.Ctes) + statement, translation);
  }

  /// <summary>Writes the statement's <c>WITH</c> list: each definition by name, a recursive one with its columns
  /// named, and the word that says any of them recurses.</summary>
  private string WriteWith(IReadOnlyList<CteNode> ctes) {
    if (ctes.Count == 0)
      return "";
    var recursive = ctes.FirstOrDefault(c => c.IsRecursive);
    if (recursive != null && _dialect.WithRecursive == null)
      throw new NotSupportedException($"A recursive query is not written for {_dbModel.Provider.ServerKind} — "
        + $"'{recursive.CteName}' cannot be translated for this server.");
    var sql = new StringBuilder(recursive != null ? _dialect.WithRecursive : "WITH");
    for (var i = 0; i < ctes.Count; i++) {
      var cte = ctes[i];
      if (i > 0)
        sql.Append(',');
      sql.Append(' ').Append(_dialect.Quote(cte.CteName));
      if (cte.Columns != null)
        sql.Append(" (").AppendJoin(", ", cte.Columns.Select(c => _dialect.Quote(c.Name!))).Append(')');
      sql.Append(" AS").Append(cte.CteKind switch {
        CteKind.Materialized => _dialect.CteMaterialized,
        CteKind.Inlined => _dialect.CteNotMaterialized,
        _ => "",
      }).Append(' ');
      WriteBracketedSelect(sql, text => {
        if (cte.Body is SetOperationNode combined)
          WriteSetOperation(text, combined);
        else
          WriteSelect(text, (SelectNode)cte.Body);
      });
      // The level is the first column of a recursive definition, and what Oracle has to be told to bound by.
      if (cte.Columns != null)
        sql.AppendFormat(_dialect.RecursiveCteSuffix, _dialect.Quote(cte.Columns[0].Name!));
    }
    return sql.Append('\n').ToString();
  }

  // ----- the root, and a select ------------------------------------------------------------------------------------

  /// <summary>Writes the whole statement: a select, a set operation, a mutation, or, for a query that is one
  /// condition, the condition selected as a value, since most servers have no Boolean to select.</summary>
  private string WriteRoot(QueryNode root) {
    var sql = new StringBuilder();
    switch (root) {
      case SelectNode select:
        WriteSelect(sql, select, isRoot: true);
        return sql.ToString();
      case SetOperationNode combined:
        WriteSetOperation(sql, combined);
        return sql.ToString();
      case ExistsNode or LogicalOpNode:
        Write(sql, root);
        return _dialect.BuildValueSelect($"CASE WHEN {sql} THEN 1 ELSE 0 END");
      case DeleteNode delete:
        WriteDelete(sql, delete);
        return sql.ToString();
      case UpdateNode update:
        WriteUpdate(sql, update);
        return sql.ToString();
      case InsertNode insert:
        WriteInsert(sql, insert);
        return sql.ToString();
      default:
        throw new NotSupportedException($"A '{root}' at the root of the SQL tree is not a statement.");
    }
  }

  /// <summary>Writes an insert from a select: the table and the members' columns in the select list's order, then
  /// the select, the same on every server.</summary>
  private void WriteInsert(StringBuilder sql, InsertNode insert) {
    var table = (DbTableInfo)insert.Target.Table;
    sql.Append("INSERT INTO ").Append(table.RefName).Append(" (");
    for (var i = 0; i < insert.Members.Count; i++) {
      var column = FindColumn(insert.Target, insert.Members[i])
        ?? throw new InvalidOperationException($"'{insert.Target.Entity?.Name}.{insert.Members[i].Name}' has no column in '{table.Name}'.");
      if (i > 0)
        sql.Append(", ");
      sql.Append(column.QuotedName);
    }
    sql.Append(')');
    NewLine(sql, _depth + 1);
    // The select is a block under the INSERT line, its own clauses a level under it.
    _depth++;
    WriteSelect(sql, insert.Rows);
    _depth--;
  }

  /// <summary>Writes an update: the dialect's head over the table, its alias and the SET list, then either the WHERE
  /// clause or, where the values come out of a query, the dialect's whole spelling of the join to the query's derived
  /// table.</summary>
  private void WriteUpdate(StringBuilder sql, UpdateNode update) {
    var table = (DbTableInfo)update.Target.Table;
    var alias = update.Target.Alias
      ?? throw new InvalidOperationException($"'{update.Target}' has no alias for its values to name it by.");
    var assignments = new StringBuilder();
    for (var i = 0; i < update.Set.ChildCount; i++) {
      var item = (SelectedValueNode)update.Set.Children[i];
      var member = update.Target.Entity!.FindMember(item.Name!)!;
      var column = FindColumn(update.Target, member)
        ?? throw new InvalidOperationException($"'{update.Target.Entity.Name}.{member.Name}' has no column in '{table.Name}'.");
      if (i > 0) {
        assignments.Append(',');
        NewLine(assignments, _depth + 3);
      }
      assignments.Append(
        string.Format(_dialect.UpdateAssignment, column.QuotedName, RenderOperand(item.Source), alias));
    }
    if (update.Source is not { } join) {
      sql.Append(string.Format(_dialect.UpdateWhere, table.RefName, alias, assignments));
      WriteClause(sql, update.Where, "WHERE ", " AND ", asConditions: true);
      return;
    }
    var derived = (DerivedRowSetNode)join.Target;
    var source = new StringBuilder();
    _depth++;
    WriteBracketedSelect(source, text => WriteSelect(text, derived.Select));
    _depth--;
    sql.Append(string.Format(_dialect.UpdateFrom, table.RefName, alias, assignments, source, derived.Alias, RenderCondition(join.Condition)));
  }

  /// <summary>Writes a delete: the dialect's head over the table and its alias, then the WHERE clause, led by
  /// <c>key IN (select)</c> or an <c>EXISTS</c> over the derived table for a composite key where the rows are found
  /// through a query.</summary>
  private void WriteDelete(StringBuilder sql, DeleteNode delete) {
    var table = (DbTableInfo)delete.Target.Table;
    var alias = delete.Target.Alias
      ?? throw new InvalidOperationException($"'{delete.Target}' has no alias for its conditions to name it by.");
    sql.Append(string.Format(_dialect.DeleteFrom, table.RefName, alias));
    if (delete.Source is not { } join) {
      WriteClause(sql, delete.Where, "WHERE ", " AND ", asConditions: true);
      return;
    }
    var derived = (DerivedRowSetNode)join.Target;
    NewLine(sql, _depth + 1);
    sql.Append("WHERE ");
    if (derived.Select.SelectList.ChildCount == 1) {
      // The pairing is target.key = derived.key: the left side is the target's column, the select is the list.
      var pairing = (BinaryNode)join.Condition;
      Write(sql, pairing.Left);
      sql.Append(" IN ");
      if (_dbModel.Provider.Supports(DbFeatures.DeleteSubQueryWrappedMySql))
        WriteBracketedSelect(sql, text => {
          text.Append("SELECT ");
          Write(text, pairing.Right);
          NewLine(text, _depth + 1);
          text.Append("FROM ");
          WriteSource(text, derived);
        });
      else
        WriteBracketedSelect(sql, text => WriteSelect(text, derived.Select));
    } else {
      sql.Append("EXISTS ");
      WriteBracketedSelect(sql, text => {
        text.Append("SELECT 1");
        NewLine(text, _depth + 1);
        text.Append("FROM ");
        WriteSource(text, derived);
        NewLine(text, _depth + 1);
        text.Append("WHERE ");
        WriteCondition(text, join.Condition);
      });
    }
    foreach (var condition in delete.Where.Children) {
      sql.Append(" AND ");
      WriteCondition(sql, condition);
    }
  }

  /// <summary>Writes one select, clause by clause, paged by the dialect where it has a page.</summary>
  /// <param name="sql">The buffer to write into.</param>
  /// <param name="select">The select.</param>
  /// <param name="asExists"><see langword="true"/> to select <c>1</c> rather than the list, inside an <c>EXISTS</c>;
  /// otherwise, <see langword="false"/>.</param>
  /// <param name="isRoot"><see langword="true"/> for the statement's own select, the one place the row numbers of
  /// spread pages trail the ordering; otherwise, <see langword="false"/>.</param>
  /// <param name="asCount"><see langword="true"/> to select a count rather than the list; otherwise,
  /// <see langword="false"/>.</param>
  private void WriteSelect(StringBuilder sql, SelectNode select, bool asExists = false, bool isRoot = false, bool asCount = false) {
    // The full-text joins are inserted behind the FROM after the clauses are written, since writing them finds the
    // matches.
    var frame = new TextJoinFrame(select);
    _textFrames.Add(frame);
    var text = new StringBuilder("SELECT ");
    if (select.IsDistinct)
      text.Append("DISTINCT ");
    if (asCount)
      text.Append("COUNT(*)");
    else if (asExists)
      text.Append('1');
    else
      WriteSelectList(text, select);
    NewLine(text, _depth + 1);
    text.Append("FROM ");
    // Where the lock is a table hint, it goes on the root select's own table, written with the FROM.
    _lockedSource = select.LocksForUpdate && isRoot && _dialect.TableHintForUpdate.Length > 0 ? select.Source : null;
    _lockHint = select.SkipsLocked ? _dialect.TableHintForUpdateSkipLocked : _dialect.TableHintForUpdate;
    WriteFrom(text, select);
    _lockedSource = null;
    var joinAt = text.Length;
    WriteClause(text, select.Where, "WHERE ", " AND ", asConditions: true);
    WriteClause(text, select.GroupBy, "GROUP BY ", ", ");
    WriteClause(text, select.Having, "HAVING ", " AND ", asConditions: true);
    WriteClause(text, select.OrderBy, "ORDER BY ", ", ");
    if (isRoot)
      for (var i = 0; i < select.SpreadOrder.Count; i++) {
        if (i == 0 && select.OrderBy.IsEmpty) {
          NewLine(text, _depth + 1);
          text.Append("ORDER BY ");
        } else
          text.Append(", ");
        Write(text, select.SpreadOrder[i]);
      }
    _textFrames.RemoveAt(_textFrames.Count - 1);
    if (frame.Joins.Count > 0)
      text.Insert(joinAt, WriteTextJoins(frame));
    // Where the lock is a clause, it follows the whole select, page included.
    var lockSuffix = !select.LocksForUpdate || !isRoot ? ""
      : select.SkipsLocked ? _dialect.SelectForUpdateSkipLocked : _dialect.SelectForUpdate;
    // Skip is rendered first so its parameter takes the earlier argument slot, whatever order the dialect writes them.
    var skip = select.Skip.IsEmpty ? null : Render(select.Skip.Children[0]);
    var take = select.Take.IsEmpty ? null : Render(select.Take.Children[0]);
    // The page is a line of its own at this select's depth.
    if (skip == null && take == null) {
      sql.Append(text).Append(lockSuffix);
      return;
    }
    if (select.LocksForUpdate && isRoot && _dbModel.Provider.Supports(DbFeatures.NoRowLimitWithLock))
      throw new NotSupportedException(
        $"{_dbModel.Provider.ServerKind} takes no row limit on a locked select (DbFeatures.NoRowLimitWithLock): "
        + "drop the Take or Skip, or the lock option.");
    var paged = _dialect.BuildPage(text.ToString(), skip, take);
    sql.Append(paged, 0, text.Length);
    NewLine(sql, _depth + 1);
    sql.Append(paged, text.Length + 1, paged.Length - text.Length - 1).Append(lockSuffix);
  }

  /// <summary>Writes the select list, or the whole row where the list is empty, wrapped at a width.</summary>
  private void WriteSelectList(StringBuilder sql, SelectNode select) {
    var lineStart = sql.Length - "SELECT ".Length;
    var scratch = new StringBuilder();
    var first = true;
    void Place() {
      if (!first) {
        sql.Append(',');
        if (sql.Length - lineStart + 1 + scratch.Length > WrapWidth) {
          NewLine(sql, _depth + 1);
          lineStart = sql.Length - (_depth + 1) * 2;
        } else
          sql.Append(' ');
      }
      first = false;
      sql.Append(scratch);
      scratch.Clear();
    }
    if (!select.SelectList.IsEmpty) {
      foreach (var item in select.SelectList.Children) {
        Write(scratch, item);
        Place();
      }
      return;
    }
    var row = select.ResultSet.Row;
    var entity = row.Entity
      ?? throw new InvalidOperationException($"A select over '{select.ResultSet}' names no entity to select the row of.");
    foreach (var member in GetRowMembers(entity, select.ResultSet)) {
      WriteColumn(scratch, row, member, aliasAsMember: true);
      Place();
    }
  }

  /// <summary>Gets the members a whole row comes back as, in the order the result description names them.</summary>
  private IReadOnlyList<ILinqEntityMember> GetRowMembers(ILinqEntity entity, RowSetNode resultSet) {
    // A stored entity's whole row is its stored members whether it comes off its table or through a derived table,
    // without the on-demand ones.
    if (entity is not EntityTypeInfo typed || _dbModel.GetTable(typed) is not { } table)
      return entity.Members;
    var members = new List<ILinqEntityMember>();
    foreach (var member in typed.Members)
      if (!member.IsOnDemand && table.GetColumn(member) != null)
        members.Add(member);
    return members;
  }

  /// <summary>Writes the FROM clause: the source, then the joins in the order they were made.</summary>
  /// <remarks>No <c>AS</c> is written before a table alias; Oracle rejects it.</remarks>
  private void WriteFrom(StringBuilder sql, SelectNode select) {
    WriteSource(sql, select.Source);
    for (var i = 1; i < select.From.ChildCount; i++) {
      var join = (JoinNode)select.From.Children[i];
      NewLine(sql, _depth + 2);
      // The rows of a document read off the row are joined as the dialect says; one server wants APPLY.
      if (join.Target is JsonRowsNode { IsCorrelated: true } rows) {
        _depth += 2;
        var source = new StringBuilder();
        WriteSource(source, rows);
        _depth -= 2;
        var condition = join.JoinType == JoinType.Cross ? null : RenderCondition(join.Condition);
        sql.Append(_dialect.JoinJsonRows(source.ToString(), rows.Alias!, condition, join.JoinType == JoinType.LeftOuter));
        continue;
      }
      // A lateral join is spelled whole by the dialect, the source inside it.
      if (join.JoinType == JoinType.Lateral) {
        var spelling = _dialect.LateralJoin
          ?? throw new NotSupportedException($"{_dbModel.Provider.ServerKind} cannot call a function over the outer row — call '{join.Target}' with a value instead.");
        _depth += 2;
        var source = new StringBuilder();
        WriteSource(source, join.Target);
        _depth -= 2;
        sql.Append(string.Format(spelling, source));
        continue;
      }
      sql.Append(join.JoinType switch {
        JoinType.LeftOuter => "LEFT OUTER JOIN ",
        JoinType.Cross => "CROSS JOIN ",
        _ => "INNER JOIN ",
      });
      // The joined side is a level under its join line, so a derived table there is a block under it.
      _depth += 2;
      WriteSource(sql, join.Target);
      _depth -= 2;
      if (join.JoinType == JoinType.Cross)
        continue;
      sql.Append(" ON ");
      WriteCondition(sql, join.Condition);
    }
  }

  /// <summary>Writes a rowset as a FROM entry: the table, a bracketed select, a CTE's name or the rows of a parameter,
  /// and its alias.</summary>
  private void WriteSource(StringBuilder sql, RowSetNode source) {
    switch (source) {
      case TableNode table:
        sql.Append(((DbTableInfo)table.Table).RefName);
        break;
      // A function's rows: the call with its arguments, the alias inside the dialect's spelling.
      case TableFunctionNode function: {
        var spelling = _dialect.TableFunctionSource
          ?? throw new NotSupportedException($"{_dbModel.Provider.ServerKind} has no table-valued functions — '{function.Table.Name}' cannot be read on this server.");
        var arguments = string.Join(", ", function.Arguments.Select(Render));
        var alias = function.Alias ?? throw new InvalidOperationException($"'{function}' has no alias to be selected from under.");
        sql.Append(string.Format(spelling, ((DbTableInfo)function.Table).RefName, arguments, alias));
        return;
      }
      case DerivedRowSetNode derived:
        WriteBracketedSelect(sql, text => WriteSelect(text, derived.Select));
        break;
      case SetOperationNode combined:
        WriteBracketedSelect(sql, text => WriteSetOperation(text, combined));
        break;
      case CteRefNode reference:
        sql.Append(_dialect.Quote(reference.Cte.CteName));
        break;
      // The alias goes inside the dialect's spelling, since some servers put the column list after it.
      case JsonRowsNode rows: {
        if (!_dbModel.Provider.Supports(DbFeatures.JsonRows))
          throw new NotSupportedException("This server has no spelling for the rows of a JSON document.");
        var columns = GetRowsColumns(rows);
        var alias = rows.Alias ?? throw new InvalidOperationException($"'{rows}' has no alias to be selected from under.");
        // A parameter's rows are the whole source, rendered per call: the JSON spelling over the bound value, or a
        // temp table where the rows are many.
        if (rows.Parameter != null)
          WriteSlot(sql, AddRowsPlaceholder(rows, columns, alias));
        else if (IsArrayRows(rows))
          sql.Append(string.Format(_dialect.ArrayRowsSource
            ?? throw new NotSupportedException($"{_dbModel.Provider.ServerKind} has no array columns to read rows of."),
            Render(rows.Document), alias, _dialect.Quote(columns[0].Name)));
        else if (_dbModel.Provider.Supports(DbFeatures.JsonRowsOfParameterOnlyDb2))
          throw new NotSupportedException($"{_dbModel.Provider.ServerKind} spreads no JSON array into rows — the "
            + "items of a JSON member cannot be queried on this server.");
        else
          sql.Append(_dialect.JsonRowsSource(Render(rows.Document), columns, alias));
        return;
      }
      default:
        throw new NotSupportedException($"A '{source}' cannot be selected from.");
    }
    sql.Append(' ').Append(source.Alias
      ?? throw new InvalidOperationException($"'{source}' has no alias to be selected from under."));
    if (ReferenceEquals(source, _lockedSource))
      sql.Append(_lockHint);
  }

  private void WriteSetOperation(StringBuilder sql, SetOperationNode combined) {
    WriteSelect(sql, (SelectNode)combined.Left);
    NewLine(sql, _depth);
    sql.Append(_dialect.GetSetOperator(combined.OpType));
    NewLine(sql, _depth);
    WriteSelect(sql, (SelectNode)combined.Right);
  }

  /// <summary>Writes a clause on a line of its own, at the select's depth, or nothing for an empty clause.</summary>
  private void WriteClause(StringBuilder sql, ClauseNode clause, string keyword, string separator, bool asConditions = false) {
    if (clause.IsEmpty)
      return;
    NewLine(sql, _depth + 1);
    sql.Append(keyword);
    WriteItems(sql, clause, separator, asConditions);
  }

  /// <summary>Writes the items of a clause, a descending ordering term with its direction and a condition as a
  /// condition.</summary>
  private void WriteItems(StringBuilder sql, ClauseNode clause, string separator, bool asConditions = false) {
    for (var i = 0; i < clause.ChildCount; i++) {
      if (i > 0)
        sql.Append(separator);
      var item = clause.Children[i];
      if (asConditions)
        WriteCondition(sql, item);
      else
        WriteOperand(sql, item);
      if (item.Flags.HasFlag(NodeFlags.Descending))
        sql.Append(" DESC");
    }
  }

  /// <summary>Writes a node standing where a condition is wanted, comparing a Boolean value to true, since a
  /// server with no Boolean has none to test on its own.</summary>
  private void WriteCondition(StringBuilder sql, QueryNode node) {
    Write(sql, node);
    if (IsBooleanValue(node))
      sql.Append(" = ").Append(RenderLiteral(true) ?? "1");
  }

  /// <summary>Writes a node standing where a value is wanted: a condition in the form the dialect has for one, or
  /// bracketed on a server that takes a condition as a value; anything else as it is.</summary>
  private void WriteOperand(StringBuilder sql, QueryNode node) {
    if (!IsCondition(node)) {
      Write(sql, node);
      return;
    }
    if (_dialect.ConditionAsValue is { } form) {
      sql.Append(string.Format(form, RenderCondition(node)));
      return;
    }
    // An EXISTS and an AND or OR delimit themselves.
    var bracket = node is not (ExistsNode or LogicalOpNode { OpType: LogicalOpType.And or LogicalOpType.Or });
    if (bracket)
      sql.Append('(');
    Write(sql, node);
    if (bracket)
      sql.Append(')');
  }

  // ----- values ----------------------------------------------------------------------------------------------------

  private string Render(QueryNode node) {
    var sql = new StringBuilder();
    Write(sql, node);
    return sql.ToString();
  }

  private string RenderCondition(QueryNode node) {
    var sql = new StringBuilder();
    WriteCondition(sql, node);
    return sql.ToString();
  }

  private string RenderOperand(QueryNode node) {
    var sql = new StringBuilder();
    WriteOperand(sql, node);
    return sql.ToString();
  }

  private void Write(StringBuilder sql, QueryNode node) {
    switch (node) {
      case ColumnNode column: {
        var alias = column.Row.RowSet.Alias ?? throw new InvalidOperationException($"'{column.Row.RowSet}' has no alias.");
        // A column of a document's rows is spelled by the dialect.
        if (column.Row.RowSet is JsonRowsNode rows) {
          sql.Append(GetRowsColumnRef(rows, alias, column.Name!));
          return;
        }
        sql.Append(alias).Append('.')
          .Append(column.Column is DbColumnInfo stored ? stored.QuotedName : _dialect.Quote(column.Name!));
        return;
      }

      // A geographic point or a vector selected as a value is read in the form it travels in, as a whole row reads it.
      case SelectedValueNode { Source: ColumnNode { Column: DbColumnInfo stored } source } selected
          when _dialect.GetColumnReadFormat(stored) is { } readFormat: {
        var reference = new StringBuilder();
        Write(reference, source);
        sql.Append(string.Format(readFormat, reference)).Append(" AS ").Append(_dialect.Quote(selected.Name!));
        return;
      }

      // A server with no Boolean to select is given a condition in the form its dialect has for one.
      case SelectedValueNode selected when _dialect.ConditionAsValue is { } form && IsCondition(selected.Source):
        sql.Append(string.Format(form, RenderCondition(selected.Source)));
        sql.Append(" AS ").Append(_dialect.Quote(selected.Name!));
        return;

      case SelectedValueNode selected:
        WriteUntypedValue(sql, selected.Source);
        sql.Append(" AS ").Append(_dialect.Quote(selected.Name!));
        return;

      // A geographic point, a constant or a parameter, is bound as its bytes and written as the server's point.
      case ConstantNode or QueryParameterNode when IsPointValue(node): {
        var bytes = new StringBuilder();
        WriteValue(bytes, node);
        sql.Append(string.Format(_dialect.GeoPointWrite, bytes));
        return;
      }

      case ConstantNode or QueryParameterNode:
        WriteValue(sql, node);
        return;

      case BinaryNode binary:
        WriteBinary(sql, binary);
        return;

      // Always bracketed: NOT binds looser than a comparison and tighter than AND, and an OR inside an AND needs its
      // own.
      case LogicalOpNode { OpType: LogicalOpType.Not } not:
        sql.Append("NOT (");
        WriteCondition(sql, not.Children[0]);
        sql.Append(')');
        return;
      case LogicalOpNode logical: {
        sql.Append('(');
        for (var i = 0; i < logical.ChildCount; i++) {
          if (i > 0)
            sql.Append(logical.OpType == LogicalOpType.And ? " AND " : " OR ");
          WriteCondition(sql, logical.Children[i]);
        }
        sql.Append(')');
        return;
      }

      // An IN over a select is one form on every server; an IN over a bound collection is the dialect's to compose.
      case NamedOpNode { OpType: NamedOpType.In } inList:
        if (inList.Children[1] is SelectNode subSelect) {
          Write(sql, inList.Children[0]);
          sql.Append(" IN ");
          WriteBracketedSelect(sql, text => WriteSelect(text, subSelect));
        } else {
          sql.Append(_dialect.BuildInList(Render(inList.Children[0]), Render(inList.Children[1])));
        }
        return;

      case NamedOpNode { OpType: NamedOpType.StartsWith or NamedOpType.EndsWith or NamedOpType.Contains or NamedOpType.Like } like:
        WriteLike(sql, like);
        return;

      case NamedOpNode { OpType: NamedOpType.TextMatch or NamedOpType.TextRank } text:
        WriteTextSearch(sql, text);
        return;

      // A value read out of a JSON document is text unless the node's type is another, which the server converts to.
      case NamedOpNode { OpType: NamedOpType.JsonValue } jsonValue: {
        var document = Render(jsonValue.Children[0]);
        var path = Render(jsonValue.Children[1]);
        // A path to an object or an array answers with the JSON itself.
        if (jsonValue.DataType is { } fragmentType && !fragmentType.IsScalarType()) {
          sql.Append(_dialect.BuildJsonFragment(document, path));
          return;
        }
        var type = jsonValue.DataType is { } dataType && (Nullable.GetUnderlyingType(dataType) ?? dataType) != typeof(string)
          ? _dbModel.Provider.TypeRegistry.MapType(dataType)
            ?? throw new NotSupportedException($"A JSON value cannot be read as '{dataType.Name}' on this server.")
          : null;
        sql.Append(_dialect.BuildJsonValue(document, path, type));
        return;
      }

      // One spelling on every server. The test is written as a condition and each result as a value, whatever
      // either is in the tree.
      case NamedOpNode { OpType: NamedOpType.Conditional } conditional:
        sql.Append("CASE WHEN ");
        WriteCondition(sql, conditional.Children[0]);
        sql.Append(" THEN ");
        WriteUntypedValue(sql, conditional.Children[1]);
        sql.Append(" ELSE ");
        WriteUntypedValue(sql, conditional.Children[2]);
        sql.Append(" END");
        return;

      case NamedOpNode { OpType: NamedOpType.VectorDistance } distance:
        WriteVectorDistance(sql, distance);
        return;

      // The dialect spells a named function.
      case NamedOpNode op: {
        var arguments = new string[op.ChildCount];
        for (var i = 0; i < arguments.Length; i++)
          arguments[i] = op.OpType == NamedOpType.Coalesce ? RenderUntypedValue(op.Children[i])
            : RenderOperand(op.Children[i]);
        sql.Append(_dialect.BuildNamedOpText(op.OpType, arguments));
        return;
      }

      // A server whose EXISTS answers nothing over a document's rows read off the outer row is asked for their count.
      case ExistsNode exists when _dbModel.Provider.Supports(DbFeatures.CountsJsonRowsForExistsMySql)
          && exists.Select.Source is JsonRowsNode { IsCorrelated: true }:
        WriteBracketedSelect(sql, text => WriteSelect(text, exists.Select, asExists: true, asCount: true));
        sql.Append(" > 0");
        return;

      case ExistsNode exists:
        sql.Append("EXISTS ");
        WriteBracketedSelect(sql, text => WriteSelect(text, exists.Select, asExists: true));
        return;

      // COUNT(*) rather than COUNT(column), which counts only the rows where the column is not null.
      case AggregateValueNode aggregate:
        if (aggregate.Term == null) {
          sql.Append("COUNT(*)");
          return;
        }
        if (aggregate.OpType == AggregateType.Average && _dialect.IntegerAverage != null
            && IsIntegerTerm(aggregate.Term)) {
          sql.Append(string.Format(_dialect.IntegerAverage, Render(aggregate.Term)));
          return;
        }
        sql.Append(aggregate.OpType switch {
          AggregateType.Sum => "SUM(",
          AggregateType.Min => "MIN(",
          AggregateType.Max => "MAX(",
          AggregateType.Average => "AVG(",
          _ => throw new NotSupportedException($"'{aggregate.OpType}' over a term is not an aggregate SQL has."),
        });
        Write(sql, aggregate.Term);
        sql.Append(')');
        return;

      // ROW_NUMBER has one spelling on every server, which is why a page per outer row is written this way.
      case RowNumberNode number: {
        sql.Append("ROW_NUMBER() OVER (");
        for (var i = 0; i < number.ChildCount; i++) {
          if (i == 0 && number.PartitionCount > 0)
            sql.Append("PARTITION BY ");
          else if (i == number.PartitionCount)
            sql.Append(i == 0 ? "ORDER BY " : " ORDER BY ");
          else
            sql.Append(", ");
          WriteOperand(sql, number.Children[i]);
          if (i >= number.PartitionCount && number.Children[i].Flags.HasFlag(NodeFlags.Descending))
            sql.Append(" DESC");
        }
        sql.Append(')');
        return;
      }

      // A select where a value is wanted is a scalar sub-query.
      case SelectNode select:
        WriteBracketedSelect(sql, text => WriteSelect(text, select));
        return;

      case ExpressionNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
        Write(sql, convert.Children[0]);
        return;

      default:
        throw new NotSupportedException($"A '{node}' node has no SQL — the tree it is in was not fully transformed.");
    }
  }

  /// <summary>Writes a member of a row as a column reference: the table's column for a table row, the value's name
  /// for a row of a derived table or a set operation.</summary>
  /// <param name="sql">The text being written.</param>
  /// <param name="row">The row the member is read from.</param>
  /// <param name="member">The member to reference.</param>
  /// <param name="aliasAsMember"><see langword="true"/> to alias the column as the member where it is called
  /// something else, so that whatever reads the row by member name finds it; otherwise, <see langword="false"/>.
  /// </param>
  private void WriteColumn(StringBuilder sql, RowNode row, ILinqEntityMember member, bool aliasAsMember) {
    var alias = row.RowSet.Alias ?? throw new InvalidOperationException($"'{row.RowSet}' has no alias.");
    // A column of a document's rows is spelled by the dialect.
    if (row.RowSet is JsonRowsNode rows) {
      sql.Append(GetRowsColumnRef(rows, alias, member.Name));
      if (aliasAsMember)
        sql.Append(" AS ").Append(_dialect.Quote(member.Name));
      return;
    }
    if (GetTable(row.RowSet) is { } table) {
      var column = FindColumn(table, member)
        ?? throw new InvalidOperationException($"'{row.Entity?.Name}.{member.Name}' has no column in '{table.Name}'.");
      // A geographic point is read as well-known binary and a vector as an array of floats, under the member's name
      // so that whatever reads the row by name finds it.
      if (_dialect.GetColumnReadFormat(column) is { } readFormat) {
        sql.Append(string.Format(readFormat, alias + "." + column.QuotedName))
          .Append(" AS ").Append(_dialect.Quote(member.Name));
        return;
      }
      sql.Append(alias).Append('.').Append(column.QuotedName);
      if (aliasAsMember && column.Name != member.Name)
        sql.Append(" AS ").Append(_dialect.Quote(member.Name));
      return;
    }
    sql.Append(alias).Append('.').Append(_dialect.Quote(member.Name));
  }

  private static DbColumnInfo? FindColumn(TableNode table, ILinqEntityMember member) => FindColumn(table.Table, member);

  private static DbColumnInfo? FindColumn(ILinqTable table, ILinqEntityMember member) =>
    member is EntityMemberInfo owned ? ((DbTableInfo)table).GetColumn(owned) : null;

  /// <summary>Gets the table a rowset reads its columns off: a table's own, or the table of a function's rows; or
  /// <see langword="null"/> for a rowset whose columns are named values.</summary>
  private static ILinqTable? GetTable(RowSetNode rowSet) => rowSet switch {
    TableNode table => table.Table,
    TableFunctionNode function => function.Table,
    _ => null,
  };

  // A comparison against a null constant reached the builder as IsNull or IsNotNull, made by preprocessing; one
  // against a parameter is written by the parameter's value when the statement is bound. Arithmetic is bracketed,
  // since it appears inside a comparison.
  private void WriteBinary(StringBuilder sql, BinaryNode node) {
    if (TryWriteParameterComparison(sql, node))
      return;
    var bracket = node.IsArithmetic;
    if (bracket)
      sql.Append('(');
    WriteOperand(sql, node.Left);
    sql.Append(' ').Append(_dialect.GetOperatorText(node.NodeType)).Append(' ');
    WriteOperand(sql, node.Right);
    if (bracket)
      sql.Append(')');
  }

  /// <summary>Decides whether a term is an integer under whatever conversions the query wrote over it, which a
  /// statement does not carry.</summary>
  private static bool IsIntegerTerm(QueryNode term) {
    var node = term;
    while (node is ExpressionNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
      node = conversion.Children[0];
    if (node.DataType == null)
      return false;
    var type = Nullable.GetUnderlyingType(node.DataType) ?? node.DataType;
    return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
  }

  /// <summary>Writes an equality or an inequality one side of which is a parameter: the other side, then a slot
  /// rendered when the statement is bound, the operator and the parameter where it holds a value and the null test
  /// where it holds null.</summary>
  /// <returns><see langword="true"/> if the comparison was written; <see langword="false"/> for any other node,
  /// with nothing written.</returns>
  /// <remarks>A null bound under <c>=</c> matches no row, where the caller's variable holding null means the rows
  /// that have none. The parameter is bound under a name of its own, since the same value written elsewhere in the
  /// statement is bound whatever it holds.</remarks>
  private bool TryWriteParameterComparison(StringBuilder sql, BinaryNode node) {
    if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
      return false;
    var (parameter, other) = node.Right is QueryParameterNode right ? (right, node.Left)
      : node.Left is QueryParameterNode left ? (left, node.Right)
      : (null, null);
    if (parameter == null || other == null || other is QueryParameterNode or ConstantNode
        || parameter.Flags.HasFlag(NodeFlags.List) || IsPointValue(parameter))
      return false;
    var isEqual = node.NodeType == ExpressionType.Equal;
    var placeholder = AddArgument(parameter, parameter.DataType, isList: false,
      key: parameter.Name + (isEqual ? " ==" : " !="));
    placeholder.SetComparison(
      _dialect.GetOperatorText(node.NodeType) + " " + _dbModel.Provider.ParameterPrefix + placeholder.Name,
      isEqual ? "IS NULL" : "IS NOT NULL");
    WriteOperand(sql, other);
    sql.Append(' ');
    WriteSlot(sql, placeholder);
    return true;
  }

  /// <summary>Writes a full-text match or rank over a row of an entity's table through the dialect, the query's words
  /// becoming the server's query form on the value: a constant now, a parameter when the statement is bound.</summary>
  private void WriteTextSearch(StringBuilder sql, NamedOpNode node) {
    if (!_dbModel.Provider.Supports(DbFeatures.FullText))
      throw new NotSupportedException($"{_dbModel.Provider.ServerKind} has no full-text search MIC writes yet.");
    if (node.Children[0] is not RowNode { RowSet: TableNode table } row)
      throw new NotSupportedException(
        "A full-text match is over a row of an entity's table, which carries the index — not over a projection's or a "
        + "derived table's row.");
    var dbTable = (DbTableInfo)table.Table;
    var indexes = dbTable.Keys.Where(k => k.IndexType == DbIndexType.FullText).ToList();
    if (indexes.Count != 1)
      throw new NotSupportedException(indexes.Count == 0
        ? $"'{row.Entity?.Name}' has no full-text index to match against — declare one: [Index(…, IndexType = DbIndexType.FullText)]."
        : $"'{row.Entity?.Name}' has {indexes.Count} full-text indexes, and a match does not say which.");
    var kind = (TextMatchKind)(((ConstantNode)node.Children[2]).Value ?? TextMatchKind.AllWords);
    var distance = Convert.ToInt32(((ConstantNode)node.Children[3]).Value ?? 0);
    var queryNode = node.Children[1];
    string query;
    if (queryNode is ConstantNode constant) {
      query = RenderLiteral(_dialect.BuildTextQuery(constant.Value as string ?? "", kind, distance) ?? constant.Value as string)
        ?? throw new InvalidOperationException("A full-text query cannot be null.");
    } else if (queryNode is QueryParameterNode parameterNode) {
      var placeholder = AddArgument(parameterNode);
      placeholder.WrapGetter(value => _dialect.BuildTextQuery(value as string ?? "", kind, distance) ?? value);
      var slot = new StringBuilder();
      WriteSlot(slot, placeholder);
      query = slot.ToString();
    } else {
      throw new NotSupportedException(
        $"A full-text query is a constant or a parameter, not a '{queryNode.Kind}' — the words are a value.");
    }
    // Where the index is reached through a join, the operation names the join's alias.
    var alias = table.Alias!;
    if (_dialect.FullTextJoin == FullTextJoinKind.Mirror) {
      if (JoinTextIndex(table, indexes[0], null, kind) is { } joined) {
        alias = joined;
      } else if (node.OpType == NamedOpType.TextMatch
          && _dialect.BuildTextMatchByKey(alias, indexes[0], query) is { } byKey) {
        // No select holds the table, as in the WHERE of a delete or an update of the table itself: the match is
        // asked of the index by the row's key.
        sql.Append(byKey);
        return;
      } else {
        throw CreateNoTextJoinException(node);
      }
    } else if (_dialect.FullTextJoin == FullTextJoinKind.RankedRows && node.OpType == NamedOpType.TextRank) {
      alias = JoinTextIndex(table, indexes[0], query, kind) ?? throw CreateNoTextJoinException(node);
    }
    // The label ties a rank to its match on a server that numbers them: one per distinct query over a row.
    var labelKey = alias + "\n" + query;
    if (!_textLabels.TryGetValue(labelKey, out var label))
      _textLabels[labelKey] = label = _textLabels.Count + 1;
    sql.Append(node.OpType == NamedOpType.TextMatch
      ? _dialect.BuildTextMatch(alias, indexes[0], query, kind, label)
      : _dialect.BuildTextRank(alias, indexes[0], query, kind, label));
  }

  /// <summary>Registers the full-text rowset of a table with the select the table is a source of, one join per table
  /// and query however many operations name it, and returns the alias the join goes by, such as <c>t0f</c>, or
  /// <see langword="null"/> where no select being written holds the table.</summary>
  private string? JoinTextIndex(TableNode table, DbKeyInfo index, string? query, TextMatchKind kind) {
    for (var i = _textFrames.Count - 1; i >= 0; i--) {
      var frame = _textFrames[i];
      if (!frame.Select.From.Children.Any(c => c == table || c is JoinNode join && join.Target == table))
        continue;
      var existing = frame.Joins.FirstOrDefault(j => j.Table == table && j.Query == query);
      if (existing != null)
        return existing.Alias;
      var ordinal = frame.Joins.Count(j => j.Table == table) + 1;
      var alias = table.Alias + "f" + (ordinal == 1 ? "" : ordinal.ToString());
      frame.Joins.Add(new TextJoin(table, index, query, kind, alias));
      return alias;
    }
    return null;
  }

  private NotSupportedException CreateNoTextJoinException(NamedOpNode node) =>
    new($"On {_dbModel.Provider.ServerKind} a full-text {(node.OpType == NamedOpType.TextRank ? "rank" : "match")} "
      + "stands in a SELECT over the entity's table, which the index's rowset joins — not in a mutation's own "
      + "WHERE.");

  /// <summary>Writes the joins of a select's full-text rowsets, each spelled by the dialect on its own line under the
  /// FROM.</summary>
  private string WriteTextJoins(TextJoinFrame frame) {
    var sql = new StringBuilder();
    foreach (var join in frame.Joins) {
      NewLine(sql, _depth + 2);
      sql.Append(_dialect.BuildTextJoin(join.Alias, join.Table.Alias!, join.Index, join.Query, join.Kind));
    }
    return sql.ToString();
  }

  /// <summary>Writes a <c>LIKE</c> with the vendor's escaping, the wildcards going on the value: a constant term
  /// becomes a literal pattern now, a parameter gains the step that builds the pattern when the statement is bound,
  /// and a raw <c>Like</c> is the caller's own pattern.</summary>
  private void WriteLike(StringBuilder sql, NamedOpNode node) {
    var escaping = _dialect.LikeEscaping;
    var leading = node.OpType != NamedOpType.StartsWith;
    var trailing = node.OpType != NamedOpType.EndsWith;
    var text = Render(node.Children[0]);
    var patternNode = node.Children[1];
    string pattern;
    if (node.OpType == NamedOpType.Like) {
      pattern = Render(patternNode);
    } else if (patternNode is ConstantNode constant) {
      pattern = RenderLiteral(escaping.BuildPattern(constant.Value as string, leading, trailing))
        ?? throw new InvalidOperationException("A LIKE pattern cannot be null.");
    } else if (patternNode is QueryParameterNode parameterNode) {
      // A placeholder of its own per pattern shape: a value written twice as the same pattern shares it, a value also
      // written plainly, or as another pattern, is wrapped once and not again.
      var key = $"{parameterNode.Name}~{(leading ? "%" : "")}like{(trailing ? "%" : "")}";
      var wrapped = _byName.ContainsKey(key);
      var placeholder = AddArgument(parameterNode, parameterNode.DataType, isList: false, key);
      if (!wrapped)
        placeholder.WrapGetter(value => escaping.BuildPattern(value as string, leading, trailing));
      var slot = new StringBuilder();
      WriteSlot(slot, placeholder);
      pattern = slot.ToString();
    } else {
      throw new NotSupportedException(
        $"A LIKE pattern is a constant or a parameter, not a '{patternNode.Kind}' — the wildcards go onto a value.");
    }
    var escape = RenderLiteral(escaping.Escape.ToString())!;
    sql.Append(_dialect.BuildNamedOpText(NamedOpType.Like, [text, pattern, escape]));
  }

  /// <summary>Writes a constant as a literal unless the server has none for it, a list always as a placeholder, and
  /// a parameter as its placeholder.</summary>
  private void WriteValue(StringBuilder sql, QueryNode node) {
    if (node is QueryParameterNode parameter) {
      WriteSlot(sql, AddArgument(parameter));
      return;
    }
    var constant = (ConstantNode)node;
    if (constant.Flags.HasFlag(NodeFlags.List))
      WriteSlot(sql, AddFixed(constant.Value, constant.DataType, isList: true));
    else if (RenderLiteral(constant.Value) is { } literal && !literal.Contains(Mark))
      sql.Append(literal);
    else
      WriteSlot(sql, AddFixed(constant.Value, constant.DataType));
  }

  /// <summary>Writes a value where nothing beside it gives it a type, a column of a select list, a result of a
  /// <c>CASE</c> or an argument of <c>COALESCE</c>: a parameter or a null in the form the dialect has for one there,
  /// anything else as an operand.</summary>
  private void WriteUntypedValue(StringBuilder sql, QueryNode node) {
    if (node is not (ConstantNode or QueryParameterNode) || IsPointValue(node)) {
      WriteOperand(sql, node);
      return;
    }
    var value = RenderOperand(node);
    // A literal says its own type; a parameter and a null do not.
    var clrType = node.DataType ?? (node as ConstantNode)?.Value?.GetType();
    if (clrType == null || !value.Contains(Mark) && value != NullLiteral) {
      sql.Append(value);
      return;
    }
    clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
    sql.Append(_dialect.BuildUntypedValue(value, clrType, _dbModel.Provider.TypeRegistry.MapType(clrType)));
  }

  private string RenderUntypedValue(QueryNode node) {
    var sql = new StringBuilder();
    WriteUntypedValue(sql, node);
    return sql.ToString();
  }

  /// <summary>Determines whether a node is a test the server evaluates, such as a comparison or an <c>EXISTS</c>,
  /// and not a Boolean value such as a column or a parameter.</summary>
  private static bool IsCondition(QueryNode node) => node switch {
    ExistsNode or LogicalOpNode => true,
    NamedOpNode { OpType: NamedOpType.JsonValue or NamedOpType.Coalesce or NamedOpType.Conditional } => false,
    BinaryNode or NamedOpNode => IsBoolean(node),
    _ => false,
  };

  /// <summary>Determines whether a node is a Boolean value and not a test: a column, a parameter, a literal, a
  /// value read out of a document, the first of several that is not null or one of two chosen by a test.</summary>
  private static bool IsBooleanValue(QueryNode node) => node switch {
    ConstantNode { Value: bool } => true,
    ColumnNode or QueryParameterNode => IsBoolean(node),
    NamedOpNode { OpType: NamedOpType.JsonValue or NamedOpType.Coalesce or NamedOpType.Conditional } =>
      IsBoolean(node),
    _ => false,
  };

  private static bool IsBoolean(QueryNode node) =>
    node.DataType is { } type && (Nullable.GetUnderlyingType(type) ?? type) == typeof(bool);

  /// <summary>Writes the distance between two vectors by the metric of the vector column among them, the cosine
  /// metric where neither is one, with a bound vector spelled as the server's.</summary>
  /// <exception cref="NotSupportedException">The dialect has no form for the distance by the metric.</exception>
  private void WriteVectorDistance(StringBuilder sql, NamedOpNode node) {
    var metric = (GetVectorMember(node.Children[0]) ?? GetVectorMember(node.Children[1]))?.VectorMetric
      ?? VectorMetric.Cosine;
    var format = _dialect.GetVectorDistanceFormat(metric) ?? throw new NotSupportedException(
      $"{_dialect.GetType().Name} has no form for the {metric} distance between vectors — the query cannot be "
      + "translated for this server.");
    sql.Append(string.Format(format, RenderVector(node.Children[0]), RenderVector(node.Children[1])));
  }

  /// <summary>Renders an argument of a vector operation: a constant or a parameter bound and written as the
  /// server's vector, anything else as it is.</summary>
  /// <remarks>The array is bound as one value. Elsewhere an array is a list of values, which a server with no array
  /// type gets written out one by one.</remarks>
  private string RenderVector(QueryNode node) {
    var placeholder = node switch {
      QueryParameterNode parameter => AddArgument(parameter, parameter.DataType, isList: false),
      ConstantNode constant => AddFixed(constant.Value, constant.DataType),
      _ => null,
    };
    if (placeholder == null)
      return RenderOperand(node);
    var bound = new StringBuilder();
    WriteSlot(bound, placeholder);
    return string.Format(_dialect.VectorWrite, bound);
  }

  /// <summary>Gets the vector member a node reads the column of, or <see langword="null"/> for any other node.
  /// </summary>
  private static EntityMemberInfo? GetVectorMember(QueryNode node) =>
    node is ColumnNode { Column: DbColumnInfo { Member: { IsVector: true } member } } ? member : null;

  /// <summary>Determines whether a constant or a parameter holds a geographic point.</summary>
  private static bool IsPointValue(QueryNode node) =>
    node is ConstantNode { Value: GeoPoint }
      || node.DataType is { } type && (Nullable.GetUnderlyingType(type) ?? type) == typeof(GeoPoint);

  /// <summary>Writes a value as a literal through the provider's adaptation first, so a literal and the same value
  /// bound agree byte for byte, or returns <see langword="null"/> where the value has to be bound.</summary>
  private string? RenderLiteral(object? value) =>
    _dialect.GetLiteral(value == null ? null : _dbModel.Provider.AdaptParameterValue(value));

  // ----- parameters, and the template ------------------------------------------------------------------------------

  private static void WriteSlot(StringBuilder sql, SqlPlaceholder placeholder) =>
    sql.Append(Mark).Append(placeholder.Index).Append(Mark);

  private SqlPlaceholder AddFixed(object? value, Type? dataType, bool isList = false) =>
    Add(SqlPlaceholder.MakeFixed(MakeNextName(), dataType ?? value?.GetType(), value, isList));

  /// <summary>Adds the parameter for a node, one per name however often it is written: a placeholder reading the
  /// node's argument, or one off the run for a parameter that takes no argument.</summary>
  private SqlPlaceholder AddArgument(QueryParameterNode node) =>
    AddArgument(node, node.DataType, node.Flags.HasFlag(NodeFlags.List));

  /// <summary>Adds the parameter for a node with its type and list-ness given by the caller, for a value the
  /// statement binds in another form than the tree holds it, under the node's name or a key of the caller's where
  /// one node is bound in several forms.</summary>
  private SqlPlaceholder AddArgument(QueryParameterNode node, Type? dataType, bool isList, string? key = null) {
    var name = node.Name ?? throw new InvalidOperationException("A parameter node has no name.");
    key ??= name;
    if (_byName.TryGetValue(key, out var existing))
      return existing;
    var getter = node.ValueGetter ?? throw new InvalidOperationException(
      $"Parameter '{name}' has no getter: the tree's parameters were not laid out before the statement was built.");
    var placeholder = Add(node.ArgIndex >= 0
      ? SqlPlaceholder.MakeArgument(MakeNextName(), dataType, node.ArgIndex, isList, getter)
      // A parameter off the run may be a list: a sub-query over a cached entity, evaluated in memory, is one.
      : SqlPlaceholder.MakeFromContext(MakeNextName(), dataType, getter, isList));
    // A high-water mark, not a count: three parameters reading one captured display class are one argument.
    if (placeholder.ArgumentIndex >= _argumentCount)
      _argumentCount = placeholder.ArgumentIndex + 1;
    _byName[key] = placeholder;
    return placeholder;
  }

  private SqlPlaceholder Add(SqlPlaceholder placeholder) {
    placeholder.Index = _placeholders.Count;
    _placeholders.Add(placeholder);
    return placeholder;
  }

  private string MakeNextName() => "p" + _placeholders.Count;

  /// <summary>Adds the parameter the rows of a source are bound as: one string, the caller's rows serialized in the
  /// form the dialect's spelling reads, a list of records read by the members the columns are typed after.</summary>
  private SqlPlaceholder AddRowsPlaceholder(JsonRowsNode rows, IReadOnlyList<JsonRowsColumn> columns, string alias) {
    // Called for rows that are a parameter, never for a document read off the outer row.
    var node = rows.Parameter!;
    // The rows are bound once; a second source over them is read under its own alias.
    if (node.Name != null && _byName.TryGetValue(node.Name, out var existing))
      return Add(SqlPlaceholder.MakeRowsOccurrence(MakeNextName(), existing, columns, alias));
    var placeholder = AddArgument(node, typeof(string), isList: false);
    var members = rows.Columns;
    // The getter yields the rows; the command builder serializes them, or stages them, when the statement is bound.
    placeholder.WrapGetter(value => JsonRowsHelper.GetRows(value, ReadRecordRow));
    placeholder.RowsColumns = columns;
    placeholder.RowsAlias = alias;
    return placeholder;

    // A record is a row of its members' raw values, with no fetch behind them; an entity instance is its record.
    object?[]? ReadRecordRow(object item) {
      var record = item as EntityRecord ?? (item as EntityBase)?.Record;
      if (record == null)
        return null;
      var values = new object?[members.Count];
      for (var i = 0; i < values.Length; i++)
        values[i] = members[i] is EntityMemberInfo owned
          ? record.GetRawValue(owned)
          : throw new NotSupportedException($"'{members[i].Name}' is no member of '{record.Entity.Name}', so it cannot be read off its records.");
      return values;
    }
  }

  /// <summary>Gets the columns of a document's rows as the dialect declares them, each typed after the model member
  /// it is typed after, size and precision included, mapped once per rowset.</summary>
  private JsonRowsColumn[] GetRowsColumns(JsonRowsNode rows) {
    if (_rowsColumns.TryGetValue(rows, out var known))
      return known;
    return _rowsColumns[rows] = MapRowsColumns(rows);
  }

  /// <summary>Determines whether rows are the items of an array column, which the server spreads typed, rather than
  /// the items of a JSON document.</summary>
  private static bool IsArrayRows(JsonRowsNode rows) =>
    rows.Document is ColumnNode { Column: DbColumnInfo { Member.ArrayItemType: not null } };

  /// <summary>Gets the reference to a column of a document's rows: as the dialect converts it out of JSON, or as it
  /// is where the rows are an array column's items.</summary>
  private string GetRowsColumnRef(JsonRowsNode rows, string alias, string name) =>
    IsArrayRows(rows)
      ? $"{alias}.{_dialect.Quote(name)}"
      : _dialect.JsonRowsColumn(alias, FindRowsColumn(rows, name));

  private JsonRowsColumn FindRowsColumn(JsonRowsNode rows, string name) {
    foreach (var column in GetRowsColumns(rows))
      if (column.Name == name)
        return column;
    throw new InvalidOperationException($"'{name}' is no column of '{rows}'.");
  }

  private JsonRowsColumn[] MapRowsColumns(JsonRowsNode rows) {
    var registry = _dbModel.Provider.TypeRegistry;
    var columns = new JsonRowsColumn[rows.Columns.Count];
    for (var i = 0; i < columns.Length; i++) {
      var member = rows.Columns[i];
      var type = member.DataType ?? typeof(object);
      type = Nullable.GetUnderlyingType(type) ?? type;
      var (size, precision, scale) = member is EntityMemberInfo owned ? (owned.Size, owned.Precision, owned.Scale) : (0, (byte)0, (byte)0);
      // A string with no size is declared wide, since a bare nvarchar is one character on one server and a bare
      // varchar nothing at all on another.
      if (type == typeof(string) && size == 0)
        size = UnsizedStringWidth;
      var mapped = registry.MapType(type, size, precision, scale);
      columns[i] = new JsonRowsColumn(member.Name, mapped
        ?? throw new NotSupportedException($"'{member.Name}' is a {type.Name}, which has no database type to declare a column of the rows as."),
        rows.IsScalar);
    }
    return columns;
  }

  /// <summary>Cuts the finished text at its marks into the template's parts and slots.</summary>
  private SqlTemplate Finish(string text, TranslatedQuery translation) {
    var result = translation.ResultInfo;
    var parts = new List<string>();
    var slots = new List<int>();
    var at = 0;
    while (true) {
      var open = text.IndexOf(Mark, at);
      if (open < 0)
        break;
      var close = text.IndexOf(Mark, open + 1);
      parts.Add(text[at..open]);
      slots.Add(int.Parse(text.AsSpan(open + 1, close - open - 1)));
      at = close + 1;
    }
    parts.Add(text[at..]);
    var rows = result.Kind is QueryResultKind.Entities or QueryResultKind.Rows;
    // A by-name query has its own count, which the caller is held to; see QueryInfo.SlotCount.
    var argumentCount = Math.Max(_argumentCount, translation.Info.SlotCount);
    return new SqlTemplate(parts, slots, _placeholders, argumentCount, result.Kind,
      rows ? result.Entity : null, rows ? result.Members : [], result.ScalarType) {
      StatementKind = translation.StatementKind,
      Result = result,
      PostSqlEvaluator = translation.PostSqlEvaluator,
      CachedEntities = translation.SqlCachedEntities,
      // Asked for every form: a document collapsed to one column is the refusal, not a form to skip.
      JsonTypedColumns = JsonMemberHelper.GetTypedColumns(translation.SqlRoot),
    };
  }
}
