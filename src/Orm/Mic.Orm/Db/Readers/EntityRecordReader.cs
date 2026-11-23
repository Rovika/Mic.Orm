using System.Data.Common;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

/// <summary>Reads the rows of a statement into records of one entity and attaches them to the session.</summary>
/// <remarks>A row whose identity the session already tracks yields the tracked record, not a fresh one; a tracked
/// stub is filled in, so every reference already handed out stays valid, and a tracked record that lacks members
/// the row has takes them. Every value is set as loaded, so a freshly read record is not modified. A row whose
/// primary-key columns are all null, which an outer join makes where nothing matched, yields
/// <see langword="null"/>.</remarks>
public sealed class EntityRecordReader : IResultReader {
  readonly EntitySession _session;
  readonly EntityTypeInfo _entity;
  readonly IReadOnlyList<EntityMemberInfo> _members;
  readonly bool _refreshTracked;

  /// <summary>Initializes a new instance of the <see cref="EntityRecordReader"/> class.</summary>
  /// <param name="session">The session the records belong to.</param>
  /// <param name="entity">The entity the rows are of.</param>
  /// <param name="members">The members the columns hold, in column order.</param>
  /// <param name="refreshTracked"><see langword="true"/> to have a tracked record take the row's values in the
  /// members that were not assigned, as a read under a lock does; otherwise, <see langword="false"/>.</param>
  public EntityRecordReader(EntitySession session, EntityTypeInfo entity,
      IReadOnlyList<EntityMemberInfo> members, bool refreshTracked = false) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(members);
    _session = session;
    _entity = entity;
    _members = members;
    _refreshTracked = refreshTracked;
  }

  /// <summary>Asynchronously reads every row of the reader into a record.</summary>
  /// <param name="command">The command that produced the reader.</param>
  /// <param name="reader">The reader over the rows.</param>
  /// <param name="cancellation">The token to cancel the read with.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the records, tracked ones
  /// where the session already had them, and <see langword="null"/> for a row that holds no entity.</returns>
  public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader,
      CancellationToken cancellation) {
    var records = new List<EntityRecord?>();
    while (await command.ReadRowAsync(reader, cancellation))
      records.Add(ReadRecord(_session, reader, _entity, _members, 0, refreshTracked: _refreshTracked));
    return records;
  }

  /// <summary>Reads one record out of the reader's current row and attaches it to the session.</summary>
  /// <param name="session">The session the record belongs to.</param>
  /// <param name="reader">The reader positioned on the row.</param>
  /// <param name="entity">The entity the row is of.</param>
  /// <param name="members">The members the columns hold, in column order.</param>
  /// <param name="offset">The ordinal of the first member's column, used when <paramref name="columns"/> is
  /// <see langword="null"/>.</param>
  /// <param name="columns">The ordinal of each member's column, for a statement whose column order is not the ORM's;
  /// or <see langword="null"/> to read the columns from <paramref name="offset"/> on.</param>
  /// <param name="refreshTracked"><see langword="true"/> to have a tracked record take the row's values in the
  /// members that were not assigned; otherwise, <see langword="false"/>.</param>
  /// <returns>The tracked record where the session already holds the identity; the new record otherwise; or
  /// <see langword="null"/> if every primary-key column of the row is null.</returns>
  /// <remarks>The bundle reader shares this method, reading several entities out of one row at their
  /// offsets.</remarks>
  internal static EntityRecord? ReadRecord(EntitySession session, DbDataReader reader, EntityTypeInfo entity,
      IReadOnlyList<EntityMemberInfo> members, int offset, int[]? columns = null, bool refreshTracked = false) {
    // The row of an optional reference that points at nothing: an outer join filled it with nulls.
    if (entity.PrimaryKey != null && IsAbsentRow(reader, members, offset, columns))
      return null;
    var provider = session.Database!.Provider;
    var record = new EntityRecord(entity, session) { Status = EntityStatus.Loaded };
    for (var i = 0; i < members.Count; i++) {
      var member = members[i];
      var column = columns != null ? columns[i] : offset + i;
      // A JSON document stays the text the row holds; the member's first read makes the object.
      if (member.IsJson) {
        JsonMemberHelper.SetLoadedJson(record, member, reader.IsDBNull(column) ? null : reader.GetValue(column));
        continue;
      }
      var value = reader.IsDBNull(column) ? null : provider.ConvertReadValue(reader.GetValue(column), member.DataType);
      record.SetLoadedValue(member, value);
    }
    // A row with no identity, such as a function's, is read and not tracked.
    return entity.PrimaryKey == null ? record : session.Attach(record, refreshTracked);
  }

  /// <summary>Determines whether every primary-key column the row has is null.</summary>
  private static bool IsAbsentRow(DbDataReader reader, IReadOnlyList<EntityMemberInfo> members, int offset,
      int[]? columns) {
    var hasKeyColumn = false;
    for (var i = 0; i < members.Count; i++) {
      if (!members[i].IsPrimaryKey)
        continue;
      if (!reader.IsDBNull(columns != null ? columns[i] : offset + i))
        return false;
      hasKeyColumn = true;
    }
    return hasKeyColumn;
  }
}
