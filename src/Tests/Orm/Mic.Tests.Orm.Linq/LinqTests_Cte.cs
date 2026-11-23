using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using Mic.Tests.Orm.Db;
using static Mic.Linq.AsyncQueryableExtensions;
using static Mic.Linq.CteQueryableExtensions;

namespace Mic.Tests.Orm.Linq;

// COMMON TABLE EXPRESSIONS: a named query written once in the WITH list, and a recursive one walking a hierarchy.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task NamedQuery_IsWrittenOnceInTheWithList() {
    var session = NewSession();
    var dearRanks = Ranks(r => LinqSeed.PriceOf(r) > 100m);
    int Occurrences(string text, string part) => text.Split(part).Length - 1;

    WriteTestTitle("a local query, named: defined in WITH, read by name");
    var dear = session.EntitySet<Linq.IProduct>().Where(p => p.Price > 100m).AsCte("dear");
    var joined = await session.EntitySet<Linq.ICategory>()
      .Join(dear, c => c.Id, p => p.Category.Id, (c, p) => new { c.Name, p.Rank })
      .ToListAsync();
    CollectionAssert.AreEquivalent(dearRanks, joined.Select(x => x.Rank).ToList());
    Assert.IsTrue(joined.All(x => x.Name == LinqSeed.CategoryOf(x.Rank)));
    StringAssert.StartsWith(session.LastCommands.Single().Sql, "WITH ");

    WriteTestTitle("the rows of a named query are the rows of the query — whole entities, tracked");
    var whole = await dear.OrderBy(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(dearRanks, whole.Select(p => p.Rank).ToList());
    Assert.AreSame(whole[0], await session.GetEntityAsync<Linq.IProduct>(whole[0].Id));

    WriteTestTitle("read TWICE, written ONCE — the name is the identity");
    var pairs = await dear
      .Join(dear, a => a.Category.Id, b => b.Category.Id, (a, b) => new { A = a.Rank, B = b.Rank })
      .Where(x => x.A < x.B)
      .CountAsync();
    Assert.AreEqual(dearRanks.Sum(a => dearRanks.Count(b => a < b && LinqSeed.CategoryOf(a) == LinqSeed.CategoryOf(b))), pairs);
    Assert.AreEqual(1, Occurrences(session.LastCommands.Single().Sql, " AS ("), "one definition for two reads");

    WriteTestTitle("a grouping as a named query — rows of a shape, joined like any other");
    var totals = session.EntitySet<Linq.IProduct>()
      .GroupBy(p => p.Category.Id)
      .Select(g => new { CategoryId = g.Key, Total = g.Sum(p => p.Price) })
      .AsCte("totals");
    var perCategory = await session.EntitySet<Linq.ICategory>()
      .Join(totals, c => c.Id, t => t.CategoryId, (c, t) => new { c.Name, t.Total })
      .ToListAsync();
    foreach (var category in LinqSeed.Categories)
      Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == category).Sum(LinqSeed.PriceOf),
        perCategory.Single(x => x.Name == category).Total, category);

    WriteTestTitle("the hint is written where the server has the word, and changes no answer");
    var kept = await session.EntitySet<Linq.IProduct>().Where(p => p.Price > 100m).AsCte("kept", CteKind.Materialized)
      .CountAsync();
    Assert.AreEqual(dearRanks.Count, kept);
    var hinted = Fixture.ServerKind is DbServerKind.Postgres or DbServerKind.SQLite;
    Assert.AreEqual(hinted, session.LastCommands.Single().Sql.Contains(" AS MATERIALIZED ("), session.LastCommands.Single().Sql);

    WriteTestTitle("inside a lambda it is still the statement's: defined once, asked about per row");
    var stocked = await session.EntitySet<Linq.ICategory>()
      .Where(c => dear.Any(p => p.Category == c))
      .CountAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, stocked);

    WriteTestTitle("the untyped builder says the same thing");
    var untyped = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Price").Greater(QueryMaker.Constant(100m)))
      .AsCte("dear")
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(dearRanks, RanksOf(untyped));

    WriteTestTitle("one name, two queries — and a named query tied to a row — are refused");
    var cheap = session.EntitySet<Linq.IProduct>().Where(p => p.Price < 20m).AsCte("dear");
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => dear
      .Join(cheap, a => a.Category.Id, b => b.Category.Id, (a, b) => a.Rank).ToListAsync());
    var products = session.EntitySet<Linq.IProduct>();
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.EntitySet<Linq.ICategory>()
      .Where(c => products.Where(p => p.Category == c).AsCte("tied").Any()).ToListAsync());
  }

  [TestMethod]
  public async Task RecursiveQuery_WalksAHierarchy() {
    var session = NewSession();
    var folders = session.EntitySet<Linq.IFolder>();

    // DB2 TAKES NO JOIN WITH `ON` in the recursive part of a common table expression, which is how MIC writes one:
    // the query is refused at translation, by the name of its definition.
    if (Fixture.ServerKind == DbServerKind.Db2) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => folders.Where(f => f.Name == "A")
        .AsRecursiveCte("under", found => found.SelectMany(f => f.Children)).ToListAsync());
      StringAssert.Contains(refused.Message, "'under'");
      return;
    }

    WriteTestTitle("DOWN, by spreading the list — everything under a folder, to any depth");
    var under = await folders.Where(f => f.Name == "A")
      .AsRecursiveCte("under", found => found.SelectMany(f => f.Children))
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Under("A"), under.Select(f => f.Name).ToList());
    Assert.AreSame(under[0], await session.GetEntityAsync<Linq.IFolder>(under[0].Id), "tracked entities, like any rows");

    WriteTestTitle("DOWN again, the join written out — the same statement");
    var joined = await folders.Where(f => f.Name == "A")
      .AsRecursiveCte("under", found => folders.Join(found, f => f.Parent!.Id, p => p.Id, (f, p) => f))
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Under("A"), joined.Select(f => f.Name).ToList());

    WriteTestTitle("UP, through the reference — a folder's chain to its root");
    var above = await folders.Where(f => f.Name == "Deep")
      .AsRecursiveCte("above", found => found.Where(f => f.Parent != null).Select(f => f.Parent!))
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Above("Deep"), above.Select(f => f.Name).ToList());

    WriteTestTitle("composed over like any query");
    var leaves = await folders.Where(f => f.Name == "Root")
      .AsRecursiveCte("tree", found => found.SelectMany(f => f.Children))
      .Where(f => f.Name.StartsWith("A1"))
      .OrderByDescending(f => f.Name)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "A1y", "A1x", "A1" }, leaves.Select(f => f.Name).ToArray());

    WriteTestTitle("the depth is a limit — and the limit is what stops a CYCLE, on every server alike");
    var shallow = await folders.Where(f => f.Name == "Root")
      .AsRecursiveCte("tree", found => found.SelectMany(f => f.Children), maxDepth: 1)
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Under("Root", 1), shallow.Select(f => f.Name).ToList());
    var looping = await folders.Where(f => f.Name == LinqSeed.FolderLoop[0])
      .AsRecursiveCte("loop", found => found.SelectMany(f => f.Children), maxDepth: 5)
      .CountAsync();
    Assert.AreEqual(6, looping, "the anchor and five steps round the loop, and then it stops");

    WriteTestTitle("the LEVEL, handed to a result selector");
    var levels = await folders.Where(f => f.Name == "Root")
      .AsRecursiveCte("tree", found => found.SelectMany(f => f.Children), (f, level) => new { f.Name, Level = level })
      .ToListAsync();
    foreach (var (name, _) in LinqSeed.FolderTree)
      Assert.AreEqual(LinqSeed.Above(name).Count - 1, levels.Single(x => x.Name == name).Level, name);

    WriteTestTitle("the untyped builder says the same thing");
    var untyped = await session.ExecuteQueryAsync(session.From("Folder")
      .Where(row => row.Member("Name").Equal(QueryMaker.Constant("A")))
      .AsRecursiveCte("under", found => found.SelectMany(f => f.Member("Children"))));
    CollectionAssert.AreEquivalent(LinqSeed.Under("A"), untyped.Select(r => ((Linq.IFolder)r.EntityInstance).Name).ToList());

    WriteTestTitle("the recursive part reads the rows found so far ONCE");
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => folders.Where(f => f.Name == "A")
      .AsRecursiveCte("twice", found => found.Join(found, a => a.Id, b => b.Parent!.Id, (a, b) => b))
      .ToListAsync());
  }
}
