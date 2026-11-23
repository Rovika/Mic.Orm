using System.Data.Common;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;

namespace Mic.Tests.Orm.Db;

// THE ROWS OF A PARAMETER, on every vendor: one bound value, a JSON array of objects or on Db2 an XML document, read
// back as a table source with typed columns, and joined to a real table on a Guid key. What the smart load over
// composite keys will rest on.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task JsonParameterRows_ComeThroughTypedAndJoin() {
    var provider = Db.Provider;
    var dialect = provider.SqlDialect;
    var registry = provider.TypeRegistry;
    Assert.IsTrue(provider.Supports(DbFeatures.JsonRows), "every one of the six spells it");

    JsonRowsColumn Column(string name, Type type, byte precision = 0, byte scale = 0) =>
      new(name, registry.MapType(type, precision: precision, scale: scale)
        ?? throw new InvalidOperationException($"no database type for {type.Name}"));
    // The alias bare on both sides, as the SQL builder writes one.
    string K(JsonRowsColumn column) => dialect.JsonRowsColumn("k", column);

    // ----- four kinds of value, three rows: each comes back as what it was ----------------------------------------
    var columns = new[] {
      Column("a", typeof(int)), Column("b", typeof(Guid)), Column("c", typeof(decimal), 18, 4), Column("d", typeof(DateTime)),
    };
    var guids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    object?[][] rows = [
      [1, guids[0], 12.3456m, new DateTime(2024, 5, 6, 7, 8, 9)],
      [2, guids[1], -0.5m, new DateTime(2000, 1, 1)],
      [3, guids[2], 1234567890.1234m, new DateTime(2024, 12, 31, 23, 59, 59)],
    ];
    var json = provider.SerializeRows(columns, rows);
    var source = dialect.JsonRowsSource(provider.ParameterPrefix + "p", columns, "k");
    Console.WriteLine($"-- rows of a JSON parameter, typed:\n{source}\n-- {json}");

    var read = await ReadAsync($"SELECT {K(columns[0])}, {K(columns[1])}, {K(columns[2])}, {K(columns[3])} FROM {source} ORDER BY {K(columns[0])}", json,
      [typeof(int), typeof(Guid), typeof(decimal), typeof(DateTime)]);
    Assert.AreEqual(3, read.Count);
    for (var i = 0; i < rows.Length; i++)
      CollectionAssert.AreEqual(rows[i], read[i], $"row {i + 1}");

    // ----- joined to a table on a Guid key — the shape the smart load takes ---------------------------------------
    await using var session = NewSession();
    var people = new List<Dml.IPerson>();
    for (var i = 0; i < 3; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"JsonRows {i}";
      person.Rank = i;
      people.Add(person);
    }
    await session.SaveChangesAsync();

    var keyColumns = new[] { Column("id", typeof(Guid)) };
    var keyJson = provider.SerializeRows(keyColumns, [[people[0].Id], [people[2].Id]]);
    var keySource = dialect.JsonRowsSource(provider.ParameterPrefix + "p", keyColumns, "k");
    var matched = await ReadAsync(
      $"SELECT {dialect.Quote("p")}.{Db.Quote("Rank")} FROM {Db.GetTableRef("Person")} {dialect.Quote("p")} "
      + $"INNER JOIN {keySource} ON {dialect.Quote("p")}.{Db.Quote("Id")} = {K(keyColumns[0])} ORDER BY {dialect.Quote("p")}.{Db.Quote("Rank")}",
      keyJson, [typeof(int)]);
    CollectionAssert.AreEqual(new object?[] { 0, 2 }, matched.Select(r => r[0]).ToArray(),
      "the join found exactly the two keys in the parameter, typed as the column is");
  }

  /// <summary>Run a statement with the one JSON parameter and read every row, each value converted to its type.
  /// </summary>
  private async Task<List<object?[]>> ReadAsync(string sql, string json, Type[] types) {
    var provider = Db.Provider;
    await using var connection = SharedDatabase.CreateConnection();
    await connection.OpenAsync(RunMode.Async);
    using var command = provider.CreateDbCommand();
    command.Connection = connection.DbConnection;
    command.CommandText = sql;
    provider.AddParameter(command, "p", json);
    var rows = new List<object?[]>();
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      var row = new object?[types.Length];
      for (var i = 0; i < types.Length; i++)
        row[i] = reader.IsDBNull(i) ? null : provider.ConvertReadValue(reader.GetValue(i), types[i]);
      rows.Add(row);
    }
    return rows;
  }
}
