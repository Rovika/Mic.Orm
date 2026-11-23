using System.Data.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

public sealed class ProjectionReader : IResultReader {
  readonly DbProvider _provider;
  readonly TransientEntityInfo _shape;
  readonly JsonTypedColumn?[] _typedByColumn;

  /// <summary>Initializes a new instance of the <see cref="ProjectionReader"/> class.</summary>
  /// <param name="provider">The provider, which converts each column's value.</param>
  /// <param name="shape">The shape of a row: a member per column.</param>
  /// <param name="typedColumns">The columns holding a JSON document typed by another column of the row, read as the
  /// type that column names.</param>
  public ProjectionReader(DbProvider provider, TransientEntityInfo shape, IReadOnlyList<JsonTypedColumn> typedColumns) {
    _provider = provider;
    _shape = shape;
    _typedByColumn = JsonMemberHelper.GetTypedByColumn(typedColumns, shape.Members.Count);
  }

  public async Task<object?> ReadAsync(DataCommand command, System.Data.Common.DbDataReader reader,
      CancellationToken cancellation) {
    var members = _shape.Members;
    var rows = new List<ProjectionData>();
    while (await command.ReadRowAsync(reader, cancellation)) {
      var row = new ProjectionData(_shape);
      for (var i = 0; i < members.Count; i++)
        row.SetValue(members[i], _typedByColumn[i] is { } typed ? JsonMemberHelper.ReadTypedColumn(_provider, reader, typed)
          : reader.IsDBNull(i) ? null
          : _provider.ConvertReadValue(reader.GetValue(i), members[i].DataType));
      rows.Add(row);
    }
    return rows;
  }
}

/// <summary>Reads the rows of a projection that hands whole rows on, such as <c>new { Shop = s, Emp = e }</c>, as
/// rows of the bundle's shape: each row slot a tracked record read out of its own run of columns, each value slot
/// one converted column.</summary>
public sealed class BundleReader : IResultReader {
  readonly EntitySession _session;
  readonly TransientEntityInfo _shape;
  readonly IReadOnlyList<QueryResultSlot> _slots;
  readonly JsonTypedColumn?[] _typedByColumn;

  /// <summary>Initializes a new instance of the <see cref="BundleReader"/> class.</summary>
  /// <param name="session">The session that tracks the records read.</param>
  /// <param name="shape">The shape of a row of the bundle.</param>
  /// <param name="slots">The slots of a row, in the order of their columns.</param>
  /// <param name="typedColumns">The value slots holding a JSON document typed by another column of the row.</param>
  public BundleReader(EntitySession session, TransientEntityInfo shape, IReadOnlyList<QueryResultSlot> slots,
      IReadOnlyList<JsonTypedColumn> typedColumns) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(shape);
    ArgumentNullException.ThrowIfNull(slots);
    _session = session;
    _shape = shape;
    _slots = slots;
    _typedByColumn = JsonMemberHelper.GetTypedByColumn(typedColumns, slots.Count == 0 ? 0 : slots[^1].Offset + slots[^1].Count);
  }

  public async Task<object?> ReadAsync(DataCommand command, System.Data.Common.DbDataReader reader,
      CancellationToken cancellation) {
    var provider = _session.Database!.Provider;
    var rows = new List<ProjectionData>();
    while (await command.ReadRowAsync(reader, cancellation)) {
      var row = new ProjectionData(_shape);
      foreach (var slot in _slots)
        row.SetValue(slot.Member, slot.Entity is { } entity
          ? EntityRecordReader.ReadRecord(_session, reader, (EntityTypeInfo)entity, [.. slot.Members.Cast<EntityMemberInfo>()], slot.Offset)
          : _typedByColumn[slot.Offset] is { } typed ? JsonMemberHelper.ReadTypedColumn(provider, reader, typed)
          : reader.IsDBNull(slot.Offset) ? null : provider.ConvertReadValue(reader.GetValue(slot.Offset), slot.ValueType!));
      rows.Add(row);
    }
    return rows;
  }
}

/// <summary>Reads the first column of every row as a list of values of the target type.</summary>
public sealed class ValueListReader : IResultReader {
  readonly DbProvider _provider;
  readonly Type _targetType;

  /// <summary>Initializes a new instance of the <see cref="ValueListReader"/> class.</summary>
  public ValueListReader(DbProvider provider, Type targetType) {
    _provider = provider;
    _targetType = targetType;
  }

  public async Task<object?> ReadAsync(DataCommand command, System.Data.Common.DbDataReader reader,
      CancellationToken cancellation) {
    var values = new List<object?>();
    while (await command.ReadRowAsync(reader, cancellation))
      values.Add(reader.IsDBNull(0) ? null : _provider.ConvertReadValue(reader.GetValue(0), _targetType));
    return values;
  }
}

/// <summary>Reads the first column of the first row as a value of the target type; <see langword="null"/> where
/// there is no row or the value is null.</summary>
/// <remarks>The conversion is why this is a reader rather than a scalar execution: the type an aggregate comes
/// back as differs by server.</remarks>
public sealed class ScalarReader : IResultReader {
  readonly DbProvider _provider;
  readonly Type _targetType;

  /// <summary>Initializes a new instance of the <see cref="ScalarReader"/> class.</summary>
  public ScalarReader(DbProvider provider, Type targetType) {
    _provider = provider;
    _targetType = targetType;
  }

  public async Task<object?> ReadAsync(DataCommand command, System.Data.Common.DbDataReader reader,
      CancellationToken cancellation) {
    if (!await command.ReadRowAsync(reader, cancellation) || reader.IsDBNull(0))
      return null;
    return _provider.ConvertReadValue(reader.GetValue(0), _targetType);
  }
}
