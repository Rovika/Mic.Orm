using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// A TABLE FUNCTION: rows the server computes for the arguments of a call, read through an entity, composed over,
// tracked where they have a key and not where they have none; refused where the server has no such thing.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task TableFunction_ReadsRowsForItsArguments() {
    var tag = "fn-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var ids = new List<Guid>();
    for (var rank = 1; rank <= 3; rank++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      ids.Add(person.Id);
    }
    await session.SaveChangesAsync();

    // ----- a server with no table-valued functions refuses the call by name --------------------------------------
    if (Db.Settings.Provider.SqlDialect.TableFunctionSource == null) {
      var refused = await Assert.ThrowsAsync<NotSupportedException>(
        () => session.TableFunction<Dml.IPersonAbove>(2).ToListAsync());
      StringAssert.Contains(refused.Message, "PersonAbove");
      return;
    }

    // ----- the rows for an argument, composed over like any set --------------------------------------------------
    var above = await session.TableFunction<Dml.IPersonAbove>(2)
      .Where(p => p.LastName.StartsWith(tag)).OrderBy(p => p.LastName).ToListAsync();
    CollectionAssert.AreEqual(new[] { 2, 3 }, above.Select(p => p.Rank).ToList());

    // ----- another argument, the same statement bound again ----------------------------------------------------
    var top = await session.TableFunction<Dml.IPersonAbove>(3).Where(p => p.LastName.StartsWith(tag)).ToListAsync();
    Assert.AreEqual(ids[2], top.Single().Id);
    Assert.AreSame(above[1], top[0], "keyed rows are tracked by their key like any entity's");
    Assert.AreEqual(1, session.LastCommands.Count, "one statement for the call");

    // ----- joined to a table: the person behind each row ---------------------------------------------------------
    var joined = await session.TableFunction<Dml.IPersonAbove>(2)
      .Join(session.EntitySet<Dml.IPerson>(), a => a.Id, p => p.Id, (a, p) => new { a.Rank, p.LastName })
      .Where(x => x.LastName.StartsWith(tag)).OrderBy(x => x.Rank).ToListAsync();
    Assert.AreEqual(2, joined.Count);
    Assert.AreEqual($"{tag}-3", joined[1].LastName);

    // ----- no parameters and no key: read, not tracked ---------------------------------------------------------
    var counts = await session.TableFunction<Dml.IRankCount>().Where(c => c.Rank >= 1).ToListAsync();
    Assert.IsTrue(counts.Any(c => c.Rank == 2 && c.Count >= 1), "the ranks counted");
    var again = await session.TableFunction<Dml.IRankCount>().Where(c => c.Rank == 2).ToListAsync();
    Assert.AreNotSame(counts.Single(c => c.Rank == 2), again.Single(), "a row with no key is a new object each read");

    // ----- by name, the argument a node -------------------------------------------------------------------------
    var byName = await session.ExecuteQueryAsync(
      session.TableFunction("PersonAbove", QueryMaker.Constant(3)).Where(r => r.Member("Id").Equal(QueryMaker.Constant(ids[2]))));
    Assert.AreEqual(1, byName.Count);

    // ----- read-only ----------------------------------------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => session.NewEntity<Dml.IPersonAbove>());
    Assert.ThrowsExactly<ArgumentException>(() => session.TableFunction<Dml.IPersonAbove>(), "one argument wanted");
  }

  [TestMethod]
  public async Task TableFunction_IsCalledOverTheOuterRow() {
    if (Db.Settings.Provider.SqlDialect.TableFunctionSource == null)
      return;
    var tag = "lat-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var ids = new List<Guid>();
    for (var rank = 1; rank <= 3; rank++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      ids.Add(person.Id);
    }
    await session.SaveChangesAsync();

    // ----- typed: each person with everyone at or above their rank, the call reading the row ---------------------
    var query =
      from p in session.EntitySet<Dml.IPerson>()
      where p.LastName.StartsWith(tag)
      from a in TableFunctions.Call<Dml.IPersonAbove, int>(p.Rank)
      where a.LastName.StartsWith(tag)
      orderby p.Rank, a.Rank
      select new { p.Rank, Above = a.Rank };
    if (Db.Settings.Provider.SqlDialect.LateralJoin == null) {
      // ----- a server that cannot: refused by name, not crashed -----------------------------------------------------
      var refused = await Assert.ThrowsAsync<NotSupportedException>(() => query.ToListAsync());
      StringAssert.Contains(refused.Message, "over the outer row");
      return;
    }
    var pairs = await query.ToListAsync();
    var statement = session.LastCommands.Single().Sql;
    CollectionAssert.AreEqual(new[] { "1-1", "1-2", "1-3", "2-2", "2-3", "3-3" },
      pairs.Select(x => $"{x.Rank}-{x.Above}").ToList(), $"got {string.Join(" ", pairs.Select(x => $"{x.Rank}-{x.Above}"))} from\n{statement}");
    StringAssert.Contains(statement, Db.Settings.Provider.SqlDialect.LateralJoin!.Split(' ')[0]);

    // ----- by name: the same spread, the argument a member of the row -----------------------------------------
    var byName = await session.ExecuteQueryAsync(session.From("Person")
      .Where(p => p.Member("Id").In(QueryMaker.Constant(ids)))
      .SelectMany(p => session.TableFunction("PersonAbove", p.Member("Rank")))
      .Where(a => a.Member("Id").In(QueryMaker.Constant(ids))));
    Assert.AreEqual(6, byName.Count);
  }
}
