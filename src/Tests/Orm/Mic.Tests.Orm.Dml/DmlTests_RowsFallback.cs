using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// THE ROWS OF A PARAMETER PAST THE THRESHOLD: staged in a temp table the statement reads instead of a JSON value it
// binds, created and filled before it and dropped after it, the statement's text otherwise the same.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task RowsParameter_PastTheThresholdIsStagedInATempTable() {
    var tag = "rows-" + Guid.NewGuid().ToString("N")[..6];
    var settings = Db.Settings;
    var threshold = settings.RowsTempTableThreshold;
    await using var session = NewSession();
    var people = new List<Dml.IPerson>();
    for (var i = 0; i < 3; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{i}";
      person.Rank = i;
      people.Add(person);
    }
    await session.SaveChangesAsync();

    ILinqEntity entity = session.GetLinqModel().FindEntity("Person")!;
    var keys = QueryMaker.Rows(QueryMaker.Parameter("keys", typeof(object)), entity.FindMember("Id")!);
    QueryNode ByKeys() => session.From("Person")
      .Join(keys, (p, k) => p.Member("Id").Equal(k.Member("Id")), (p, k) => [p])
      .OrderBy(p => p.Member("Rank"));
    var wanted = new List<object?[]> { new object?[] { people[0].Id }, new object?[] { people[2].Id } };

    try {
      // ----- under the threshold: one statement over a bound JSON value -------------------------------------------
      var bound = await session.ExecuteQueryAsync(ByKeys(), wanted);
      CollectionAssert.AreEqual(new[] { 0, 2 }, bound.Select(r => ((Dml.IPerson)r.EntityInstance).Rank).ToList());
      Assert.AreEqual(1, session.LastCommands.Count, "the rows travel as one value");

      // ----- past it: the same query, the rows staged first and the table gone after ------------------------------
      settings.RowsTempTableThreshold = 0;
      var staged = await session.ExecuteQueryAsync(ByKeys(), wanted);
      CollectionAssert.AreEqual(new[] { 0, 2 }, staged.Select(r => ((Dml.IPerson)r.EntityInstance).Rank).ToList());
      var commands = session.LastCommands;
      Assert.AreEqual(4, commands.Count, "create, fill, the statement, drop");
      StringAssert.Contains(commands[0].Sql.ToUpperInvariant(), Db.Provider.SqlDialect.CreateTempTable.Split(' ')[0]);
      StringAssert.Contains(commands[1].Sql.ToUpperInvariant(), "INSERT");
      // Where the server has a bulk channel the fill is one write over it, and its text says so.
      if (Db.Provider.Supports(DbFeatures.BulkInsert))
        StringAssert.StartsWith(commands[1].Sql, "-- bulk insert into");
      StringAssert.Contains(commands[2].Sql.ToUpperInvariant(), "SELECT");
      StringAssert.Contains(commands[3].Sql.ToUpperInvariant(), "DROP");
      Assert.IsFalse(commands[2].Sql.Contains("keys", StringComparison.OrdinalIgnoreCase)
        && commands[2].Sql.Contains("JSON", StringComparison.OrdinalIgnoreCase), "the statement reads the table, not a JSON value");
    } finally {
      settings.RowsTempTableThreshold = threshold;
    }
  }

  [TestMethod]
  public async Task RowsParameter_StagedKeepsEveryValue() {
    // The rows in a temp table, whichever way the server fills it: a number, a text outside ASCII, a date and time,
    // and a null.
    var settings = Db.Settings;
    var threshold = settings.RowsTempTableThreshold;
    await using var session = NewSession();
    var rows = QueryMaker.Rows("rows", ("n", typeof(int)), ("label", typeof(string)), ("at", typeof(DateTime)));
    var at = new DateTime(2024, 2, 2, 3, 4, 5);
    var given = new List<object?[]> {
      new object?[] { 2, "жук 日本", at },
      new object?[] { 1, null, new DateTime(2024, 1, 1) },
    };
    settings.RowsTempTableThreshold = 0;
    try {
      var read = await session.ExecuteRowsAsync(rows.OrderBy(r => r.Member("n")), given);
      Assert.IsTrue(session.LastCommands.Count >= 4, "create, fill, the statement, drop");
      Assert.AreEqual(2, read.Count);
      Assert.AreEqual(1, read[0].GetValue("n"));
      Assert.IsNull(read[0].GetValue("label"), "a null stays a null");
      Assert.AreEqual(new DateTime(2024, 1, 1), read[0].GetValue("at"));
      Assert.AreEqual(2, read[1].GetValue("n"));
      Assert.AreEqual("жук 日本", read[1].GetValue("label"));
      Assert.AreEqual(at, read[1].GetValue("at"));
    } finally {
      settings.RowsTempTableThreshold = threshold;
    }
  }

  [TestMethod]
  public async Task LongList_TravelsAsTheRowsOfOneParameter() {
    // A caller's list past the dialect's limit is read as the rows of one JSON parameter on a server without array
    // parameters, under one statement text whatever the list, at a length past Oracle's 32,767 bytes of a string bind
    // too, and is staged in a temp table past the threshold as rows are; a short list is still written in as
    // literals.
    var tag = "list-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var people = new List<Dml.IPerson>();
    for (var i = 0; i < 3; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{i}";
      person.Rank = i;
      people.Add(person);
    }
    await session.SaveChangesAsync();
    var bindsArrays = Db.Provider.Supports(DbFeatures.ArrayParameters);
    static List<Guid> MakeIds(Guid first, int strangers, Guid last) {
      var ids = new List<Guid> { first };
      for (var i = 0; i < strangers; i++)
        ids.Add(Guid.NewGuid());
      ids.Add(last);
      return ids;
    }
    async Task<List<int>> RanksInAsync(List<Guid> ids) {
      var found = await session.EntitySet<Dml.IPerson>().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Rank)
        .ToListAsync();
      return found.Select(p => p.Rank).ToList();
    }

    // ----- a short list: literals ---------------------------------------------------------------------------------
    CollectionAssert.AreEqual(new[] { 0, 2 }, await RanksInAsync(MakeIds(people[0].Id, 1, people[2].Id)));
    var sql = session.LastCommands[^1].Sql;
    if (!bindsArrays)
      Assert.IsFalse(sql.Contains("JSON", StringComparison.OrdinalIgnoreCase), "a short list is written in: " + sql);

    // ----- past the dialect's limit: the rows of one JSON parameter -----------------------------------------------
    CollectionAssert.AreEqual(new[] { 0, 2 }, await RanksInAsync(MakeIds(people[0].Id, 298, people[2].Id)));
    Assert.AreEqual(1, session.LastCommands.Count, "the list travels as one value");
    sql = session.LastCommands[^1].Sql;
    Assert.IsTrue(sql.Length < 1_000, "the ids are not in the text: " + sql);
    if (!bindsArrays)
      StringAssert.Contains(sql.ToUpperInvariant(), Fixture.ServerKind == DbServerKind.Db2 ? "XMLTABLE" : "JSON",
        "the list is read as the rows of a parameter");

    // ----- 1,500 ids, over 50,000 characters of JSON and past Oracle's cap of 1,000 items: still one value -------
    CollectionAssert.AreEqual(new[] { 0, 2 }, await RanksInAsync(MakeIds(people[0].Id, 1_498, people[2].Id)));
    Assert.AreEqual(1, session.LastCommands.Count, "the list travels as one value");

    // ----- past the threshold: a temp table -----------------------------------------------------------------------
    var settings = Db.Settings;
    var threshold = settings.RowsTempTableThreshold;
    settings.RowsTempTableThreshold = 30_000;
    try {
      CollectionAssert.AreEqual(new[] { 0, 2 }, await RanksInAsync(MakeIds(people[0].Id, 1_498, people[2].Id)));
      var commands = session.LastCommands;
      if (bindsArrays) {
        Assert.AreEqual(1, commands.Count, "an array binds whole");
      } else {
        Assert.IsTrue(commands.Count >= 4, "create, fill, the statement, drop: " + commands.Count);
        StringAssert.Contains(commands[0].Sql.ToUpperInvariant(), Db.Provider.SqlDialect.CreateTempTable.Split(' ')[0]);
        StringAssert.Contains(commands[^2].Sql.ToUpperInvariant(), "SELECT");
        StringAssert.Contains(commands[^1].Sql.ToUpperInvariant(), "DROP");
        Assert.IsTrue(commands[^2].Sql.Length < 1_000, "the ids are not in the text: " + commands[^2].Sql);
      }
    } finally {
      settings.RowsTempTableThreshold = threshold;
    }
  }

  [TestMethod]
  public async Task ListOfStrings_IsMatchedAsAList() {
    // List<string>.Contains has the name and the one argument of string.Contains, and is a list all the same.
    var tag = "names-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var lastNames = new[] { $"{tag}-a", $"{tag}-жук", $"{tag}-c" };
    for (var i = 0; i < lastNames.Length; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = lastNames[i];
      person.Rank = i;
    }
    await session.SaveChangesAsync();

    var list = new List<string> { lastNames[0], lastNames[1], "no such name" };
    var byList = await session.EntitySet<Dml.IPerson>().Where(p => list.Contains(p.LastName)).OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 0, 1 }, byList.Select(p => p.Rank).ToList(), "a List<string>");

    var array = list.ToArray();
    var byArray = await session.EntitySet<Dml.IPerson>().Where(p => array.Contains(p.LastName)).OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 0, 1 }, byArray.Select(p => p.Rank).ToList(), "an array of strings");
  }
}
