using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

// A CONDITION WHERE A VALUE IS WANTED, AND A BOOLEAN VALUE WHERE A CONDITION IS: a server with no Boolean takes
// neither as written. A Boolean column filters on its own, a condition orders, is compared, is assigned and is
// selected, with the same rows on every server.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task Condition_StandsAsAValue_AndABooleanValueAsACondition() {
    var tag = "cond-" + Guid.NewGuid().ToString("N")[..6];
    await using (var writer = NewSession()) {
      // a: set and large; b: large; c: set; d: neither.
      (string Name, bool Flag, int Count)[] rows = [("a", true, 10), ("b", false, 10), ("c", true, 1), ("d", false, 1)];
      foreach (var (name, flag, count) in rows) {
        var row = writer.NewEntity<Dml.IEveryType>();
        row.Id = Guid.NewGuid();
        row.Text = tag + name;
        row.Flag = flag;
        row.Count = count;
        row.Grade = Dml.Grade.Gold;
        row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        row.Day = new DateOnly(2031, 4, 1);
        row.Long = "x";
        row.Settings = new Dml.EveryTypeSettings { Theme = "plain", PageSize = 1, Tags = [] };
      }
      // An address with a resident and one without, for a condition that is an EXISTS.
      foreach (var city in new[] { "empty", "lived" }) {
        var address = writer.NewEntity<Dml.IAddress>();
        address.Id = Guid.NewGuid();
        address.City = tag + city;
        if (city == "lived") {
          var person = writer.NewEntity<Dml.IPerson>();
          person.Id = Guid.NewGuid();
          person.LastName = tag;
          person.Address = address;
        }
      }
      await writer.SaveChangesAsync();
    }

    await using var session = NewSession();
    var mine = session.EntitySet<Dml.IEveryType>().Where(e => e.Text.StartsWith(tag));
    async Task<string> ReadNamesAsync(IQueryable<Dml.IEveryType> query) =>
      string.Join(",", (await query.ToListAsync()).Select(e => e.Text[tag.Length..]));

    // ----- a Boolean column where a condition is wanted: alone, negated, beside a comparison -------------------------
    Assert.AreEqual("a,c", await ReadNamesAsync(mine.Where(e => e.Flag).OrderBy(e => e.Text)));
    Assert.AreEqual("b,d", await ReadNamesAsync(mine.Where(e => !e.Flag).OrderBy(e => e.Text)));
    Assert.AreEqual("a", await ReadNamesAsync(mine.Where(e => e.Flag && e.Count > 5).OrderBy(e => e.Text)));

    // ----- a condition compared: to a column, and to another condition ----------------------------------------------
    Assert.AreEqual("a,d", await ReadNamesAsync(mine.Where(e => e.Flag == (e.Count > 5)).OrderBy(e => e.Text)));
    Assert.AreEqual("a,c,d",
      await ReadNamesAsync(mine.Where(e => (e.Count > 5) == e.Text.EndsWith("a")).OrderBy(e => e.Text)));

    // ----- a condition as an ordering term: a comparison, and an EXISTS ----------------------------------------------
    Assert.AreEqual("c,d,a,b", await ReadNamesAsync(mine.OrderBy(e => e.Count > 5).ThenBy(e => e.Text)));
    var byResidents = session.EntitySet<Dml.IAddress>().Where(a => a.City.StartsWith(tag))
      .OrderByDescending(a => a.Residents.Any()).ThenBy(a => a.City);
    // Db2 takes no sub-query over the outer row as an ordering term, and the refusal is the server's own.
    if (Fixture.ServerKind == DbServerKind.Db2) {
      var refused = await Assert.ThrowsExactlyAsync<DataAccessException>(() => byResidents.ToListAsync());
      StringAssert.Contains(refused.Message, "SQL0206N");
    } else {
      var cities = await byResidents.ToListAsync();
      Assert.AreEqual(tag + "lived", cities[0].City);
      Assert.AreEqual(2, cities.Count);
    }

    // ----- a condition selected, with a Boolean column inside it -----------------------------------------------------
    var selected = await mine.Select(e => new { e.Text, Both = e.Flag && e.Count > 5 })
      .OrderBy(e => e.Text).ToListAsync();
    Assert.AreEqual("True,False,False,False", string.Join(",", selected.Select(s => s.Both)));

    // ----- a condition assigned by a statement ---------------------------------------------------------------------
    var updated = await mine.Select(e => new { Flag = e.Count > 5 }).ExecuteUpdateAsync<Dml.IEveryType>();
    Assert.AreEqual(4, updated);
    await using var reader = NewSession();
    var flags = await reader.EntitySet<Dml.IEveryType>().Where(e => e.Text.StartsWith(tag))
      .OrderBy(e => e.Text).ToListAsync();
    Assert.AreEqual("True,True,False,False", string.Join(",", flags.Select(e => e.Flag)));
  }

  [TestMethod]
  public async Task Conditional_IsWrittenAsACase() {
    var tag = "case-" + Guid.NewGuid().ToString("N")[..6];
    await using (var writer = NewSession()) {
      // a: set and large; b: large; c: set; d: neither. The optional text is on a and c.
      (string Name, bool Flag, int Count, decimal Amount)[] rows =
        [("a", true, 10, 10m), ("b", false, 10, 20m), ("c", true, 1, 30m), ("d", false, 1, 40m)];
      foreach (var (name, flag, count, amount) in rows) {
        var row = writer.NewEntity<Dml.IEveryType>();
        row.Id = Guid.NewGuid();
        row.Text = tag + name;
        row.Missing = flag ? "m" + name : null;
        row.Flag = flag;
        row.Count = count;
        row.Amount = amount;
        row.Grade = Dml.Grade.Gold;
        row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        row.Day = new DateOnly(2031, 4, 1);
        row.Long = "x";
        row.Settings = new Dml.EveryTypeSettings { Theme = "plain", PageSize = 1, Tags = [] };
      }
      await writer.SaveChangesAsync();
    }

    await using var session = NewSession();
    var mine = session.EntitySet<Dml.IEveryType>().Where(e => e.Text.StartsWith(tag));
    var ordered = mine.OrderBy(e => e.Text);
    static string Join<T>(IEnumerable<T> values) => string.Join(",", values.Select(v => v?.ToString() ?? "null"));
    async Task<string> ReadNamesAsync(IQueryable<Dml.IEveryType> query) =>
      Join((await query.ToListAsync()).Select(e => e.Text[tag.Length..]));

    // ----- selected: two constants, two values the caller brought, a member, one inside another ----------------------
    Assert.AreEqual("big,big,small,small",
      Join(await ordered.Select(e => e.Count > 5 ? "big" : "small").ToListAsync()));
    var (high, low) = ("H", "L");
    Assert.AreEqual("H,H,L,L", Join(await ordered.Select(e => e.Count > 5 ? high : low).ToListAsync()));
    Assert.AreEqual("10,0,1,0",
      Join((await ordered.Select(e => new { e.Text, Counted = e.Flag ? e.Count : 0 }).ToListAsync())
        .Select(r => r.Counted)));
    Assert.AreEqual("A,B,C,C",
      Join(await ordered.Select(e => e.Count > 5 ? (e.Flag ? "A" : "B") : "C").ToListAsync()));

    // ----- null: as a result, and as what the test asks about ----------------------------------------------------
    Assert.AreEqual("ma,null,none,none",
      Join(await ordered.Select(e => e.Count > 5 ? e.Missing : "none").ToListAsync()));
    Assert.AreEqual("10,null,1,null",
      Join(await ordered.Select(e => e.Flag ? (int?)e.Count : null).ToListAsync()));
    Assert.AreEqual("ma,none,mc,none",
      Join(await ordered.Select(e => e.Missing == null ? "none" : e.Missing).ToListAsync()));

    // ----- in a condition, as an ordering term, as the term of a sum ----------------------------------------------
    Assert.AreEqual("a", await ReadNamesAsync(mine.Where(e => (e.Flag ? e.Count : 0) > 5).OrderBy(e => e.Text)));
    Assert.AreEqual("a,c,b,d", await ReadNamesAsync(mine.OrderBy(e => e.Flag ? 0 : 1).ThenBy(e => e.Text)));
    var sums = await mine.GroupBy(e => e.Flag)
      .Select(g => new { g.Key, Large = g.Sum(e => e.Count > 5 ? e.Amount : 0m) }).ToListAsync();
    Assert.AreEqual(10m, sums.Single(s => s.Key).Large);
    Assert.AreEqual(20m, sums.Single(s => !s.Key).Large);
    Assert.AreEqual(40m, await mine.SumAsync(e => e.Flag ? e.Amount : 0m));

    // ----- Boolean results: the conditional is a value, tested where a condition is wanted ---------------------------
    Assert.AreEqual("a,d", await ReadNamesAsync(mine.Where(e => e.Count > 5 ? e.Flag : !e.Flag).OrderBy(e => e.Text)));
    Assert.AreEqual("True,True,True,False",
      Join(await ordered.Select(e => e.Count > 5 ? true : e.Flag).ToListAsync()));
    Assert.AreEqual("True,False,False,True",
      Join(await ordered.Select(e => e.Count > 5 ? e.Flag : e.Text.EndsWith("d")).ToListAsync()));

    // ----- a result that is several columns is refused, naming the operator ----------------------------------------
    var people = session.EntitySet<Dml.IPerson>();
    var entity = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      people.Select(p => new { Home = p.Rank > 5 ? p.Address : null }).ToListAsync());
    StringAssert.Contains(entity.Message, "conditional operator '?:'");
    StringAssert.Contains(entity.Message, "'Address'");
    var shape = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      people.Select(p => p.Rank > 5 ? new { Name = p.LastName } : new { Name = "x" }).ToListAsync());
    StringAssert.Contains(shape.Message, "conditional operator '?:'");

    // ----- assigned by a statement ---------------------------------------------------------------------------------
    var updated = await mine.Select(e => new { Count = e.Flag ? e.Count + 100 : e.Count })
      .ExecuteUpdateAsync<Dml.IEveryType>();
    Assert.AreEqual(4, updated);
    await using var reader = NewSession();
    var counts = await reader.EntitySet<Dml.IEveryType>().Where(e => e.Text.StartsWith(tag))
      .OrderBy(e => e.Text).ToListAsync();
    Assert.AreEqual("110,10,101,1", Join(counts.Select(e => e.Count)));
  }
}
