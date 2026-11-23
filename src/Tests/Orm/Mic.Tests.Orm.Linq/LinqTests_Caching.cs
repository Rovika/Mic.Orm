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

// THE TWO CACHES: the statement cache, where a repeated query translates once and the by-key read never enters, and
// the entity cache, where a cached entity is answered from memory with each session still getting its own records.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task RepeatedQuery_TranslatesOnce() {
    var session = NewSession();
    var cache = App.DataAccess.FindDatabase()!.QueryCache;
    cache.Clear();

    WriteTestTitle("the same SHAPE, many values — one translation");
    var byRank = session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Parameter("rank", typeof(int))));
    for (var rank = 1; rank <= 5; rank++)
      Assert.AreEqual(1, (await session.ExecuteQueryAsync(byRank, rank)).Count);
    Assert.AreEqual(1, cache.Misses, "translated once");
    Assert.AreEqual(4, cache.Hits);

    // A TREE BUILT AFRESH keys the same, which is the whole point — an application builds its query per call and must
    // still find the statement its last call translated.
    var rebuilt = session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Parameter("rank", typeof(int))));
    Assert.AreEqual(byRank.GetCacheKey(), rebuilt.GetCacheKey());
    await session.ExecuteQueryAsync(rebuilt, 7);
    Assert.AreEqual(1, cache.Misses, "still one shape");

    WriteTestTitle("a different SHAPE is a different entry");
    // A constant is written into the SQL, so two constants are two statements — where two parameter VALUES are one.
    await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live))));
    await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Draft))));
    Assert.AreEqual(3, cache.Misses, "two more shapes");

    // And so are a different member, a different operator, and a different ordering.
    var shapes = new[] {
      session.From("Product").Where(row => row.Member("Rank").Greater(QueryMaker.Parameter("r", typeof(int)))),
      session.From("Product").Where(row => row.Member("Rank").Less(QueryMaker.Parameter("r", typeof(int)))),
      session.From("Product").Where(row => row.Member("Price").Greater(QueryMaker.Parameter("r", typeof(decimal)))),
      session.From("Product").OrderBy(row => row.Member("Rank")),
      session.From("Product").OrderBy(row => row.Member("Rank"), descending: true),
    };
    Assert.AreEqual(shapes.Length, shapes.Select(s => s.GetCacheKey()).Distinct().Count(),
      "every one of these is a different statement, so every one is a different key");

    WriteTestTitle("the by-key read enters the cache ONCE, under the key's own fixed key");
    var before = cache.Count;
    var afterFirst = -1;
    var product = (Linq.IProduct)(await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Constant(11)))))[0].EntityInstance;
    var reader = NewSession();
    for (var i = 0; i < 3; i++) {
      var reread = reader.GetEntity<Linq.IProduct>(product.Id);
      Assert.AreEqual("Product 11", reread.Name);
      // A reference member follows the same path.
      Assert.AreEqual(LinqSeed.CategoryOf(11), reread.Category.Name);
      if (i == 0)
        afterFirst = cache.Count;
      reader = NewSession();
    }
    // The query above, a product by its id, a category by its id: three shapes at most, and none of them twice —
    // a by-key read is looked up under a key computed once from the model, so nothing is built or hashed per call.
    Assert.IsTrue(cache.Count <= before + 3, $"{cache.Count - before} new entries");
    Assert.AreEqual(afterFirst, cache.Count, "the by-key reads were entered once and found afterwards");
  }

  [TestMethod]
  public async Task CachedEntity_AnsweredFromMemoryPerSession() {
    var session = NewSession();
    var cache = App.DataAccess.FindDatabase()!.EntityCache;

    WriteTestTitle("the whole block, and a filter over it");
    var categories = await session.ExecuteQueryAsync(session.From("Category"));
    CollectionAssert.AreEquivalent(LinqSeed.Categories, NamesOf(categories).ToArray());

    var before = cache.Hits;
    var tools = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Name").Equal(QueryMaker.Parameter("name", typeof(string)))),
      "Tools");
    Assert.AreEqual(1, tools.Count);
    Assert.AreEqual("Tools", ((Linq.ICategory)tools[0].EntityInstance).Name);
    // THE COUNTER IS THE PROOF: it is incremented only on the path that returns without reaching the executor.
    Assert.AreEqual(before + 1, cache.Hits, "answered from memory");

    WriteTestTitle("a by-key read, which is what the block's lookup exists for");
    var id = ((Linq.ICategory)tools[0].EntityInstance).Id;
    var reader = NewSession();
    before = cache.Hits;
    var byKey = reader.GetEntity<Linq.ICategory>(id);
    Assert.AreEqual("Tools", byKey.Name);
    Assert.AreEqual(before + 1, cache.Hits);

    // SKIPPING THE CACHE BY FLAG reads the server, under the by-key shape's statement; in a session of its own, since
    // the identity map answers ahead of everything.
    var skipper = NewSession();
    before = cache.Hits;
    var fresh = await skipper.GetEntityAsync<Linq.ICategory>(id, LoadFlags.SkipEntityCache);
    Assert.AreEqual("Tools", fresh.Name);
    Assert.AreEqual(before, cache.Hits, "the cache was not consulted");
    StringAssert.Contains(skipper.LastCommands[^1].Sql, "SELECT", "the row came from the server");

    // AND FOLLOWING A REFERENCE takes the same path — which is the commonest read of a cached entity there is. In a
    // SESSION OF ITS OWN, because the identity map answers ahead of the cache and `reader` has already loaded this
    // category: that ordering is right, and it would make the count say nothing here.
    var follower = NewSession();
    var product = (Linq.IProduct)(await follower.ExecuteQueryAsync(follower.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Constant(3)))))[0].EntityInstance;
    before = cache.Hits;
    Assert.AreEqual(LinqSeed.CategoryOf(3), product.Category.Name);
    Assert.AreEqual(before + 1, cache.Hits);

    WriteTestTitle("EACH SESSION GETS ITS OWN RECORD, and its own only once");
    // The block holds a detached value snapshot, never a record. Two sessions sharing one would share every
    // uncommitted change either made — so what they get is a copy each, and the identity map still promises one per
    // identity WITHIN a session.
    var otherSession = NewSession();
    var mine = reader.GetEntity<Linq.ICategory>(id);
    var theirs = otherSession.GetEntity<Linq.ICategory>(id);
    Assert.AreNotSame(mine, theirs, "two sessions, two records");
    Assert.AreSame(mine, reader.GetEntity<Linq.ICategory>(id), "one session, one record");
    Assert.AreEqual(theirs.Name, mine.Name);

    WriteTestTitle("what the evaluator can do runs in memory; what it cannot goes to the server, unchanged");
    // AN ORDERING IS THE EVALUATOR'S: the rows are here and sorting them is nothing. A condition asking a question OF
    // A LIST is not — a cached row is detached, and its lists cannot be asked — so that one is answered elsewhere,
    // not refused. The count says which was which.
    before = cache.Hits;
    var ordered = await session.ExecuteQueryAsync(session.From("Category").OrderBy(row => row.Member("Name")));
    CollectionAssert.AreEqual(LinqSeed.Categories.Order().ToArray(), NamesOf(ordered).ToArray());
    Assert.AreEqual(before + 1, cache.Hits, "ordered in memory");
    var withProducts = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Any()));
    Assert.AreEqual(LinqSeed.Categories.Length, withProducts.Count);
    Assert.AreEqual(before + 1, cache.Hits, "the list question was not the cache's to answer");

    // A CONDITIONAL IS THE EVALUATOR'S TOO: one of two values by a test over the row, chosen where the rows are.
    before = cache.Hits;
    var kinds = await session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name)
      .Select(c => new { c.Name, Kind = c.Name == "Tools" ? "hard" : "soft" }).ToListAsync();
    CollectionAssert.AreEqual(LinqSeed.Categories.Order().Select(n => n == "Tools" ? "hard" : "soft").ToArray(),
      kinds.Select(k => k.Kind).ToArray());
    Assert.AreEqual(before + 1, cache.Hits, "chosen in memory");
  }

  [TestMethod]
  public async Task SkipEntityCache_ReachesACachedSubQuery() {
    // A sub-query over a cached entity inside a query for the server is answered from memory and sent as a
    // parameter. Under the flag it is the server's too. The category is renamed behind the cache's back, by a
    // statement the cache is not told of, and put back at the end; the cached block learns of neither.
    var session = NewSession();
    const string renamed = "Tools-renamed";
    var categories = Db.GetTableRef("Category");
    async Task<int> CountInAsync(string categoryName, QueryOptions options) {
      var named = session.EntitySet<Linq.ICategory>().Where(c => c.Name == categoryName).Select(c => c.Id);
      return await session.EntitySet<Linq.IProduct>(options).Where(p => named.Contains(p.Category.Id)).CountAsync();
    }
    Task<int> RenameAsync(string from, string to) =>
      Db.ExecuteAsync($"UPDATE {categories} SET {Db.Quote("Name")} = '{to}' WHERE {Db.Quote("Name")} = '{from}'");

    var inTools = await CountInAsync("Tools", QueryOptions.None);
    Assert.IsTrue(inTools > 0);
    Assert.IsFalse(session.LastCommands[^1].Sql.Contains(categories), "the categories came from memory");

    Assert.AreEqual(1, await RenameAsync("Tools", renamed));
    try {
      // ----- without the flag the sub-query is the cache's, which still says Tools ---------------------------------
      Assert.AreEqual(inTools, await CountInAsync("Tools", QueryOptions.None));
      Assert.AreEqual(0, await CountInAsync(renamed, QueryOptions.None));

      // ----- with it the whole read is the server's ------------------------------------------------------------------
      Assert.AreEqual(0, await CountInAsync("Tools", QueryOptions.SkipEntityCache), "the server has no Tools now");
      StringAssert.Contains(session.LastCommands[^1].Sql, categories, "the statement reads the categories itself");
      Assert.AreEqual(inTools, await CountInAsync(renamed, QueryOptions.SkipEntityCache));

      // ----- and the shape without the flag is as it was -------------------------------------------------------------
      Assert.AreEqual(inTools, await CountInAsync("Tools", QueryOptions.None));
      Assert.IsFalse(session.LastCommands[^1].Sql.Contains(categories));
    } finally {
      await RenameAsync(renamed, "Tools");
    }
  }

  [TestMethod]
  public async Task TypedQuery_OverACachedEntityAnsweredFromMemory() {
    var session = NewSession();
    var database = App.DataAccess.FindDatabase()!;
    var cache = database.EntityCache;
    var statements = database.QueryCache;

    // `Category` IS CACHED FOR THE WHOLE SUITE — see the fixture — so a typed query over it should never reach the
    // server, and must still answer exactly what the database holds.
    WriteTestTitle("the same answer, without a statement");
    // FILLING A BLOCK IS ITSELF A QUERY — one select of every row, translated like any other — so the count only
    // means anything once the block is there.
    var categories = await session.EntitySet<Linq.ICategory>().ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, categories.Count);
    statements.Clear();
    var before = cache.Hits;

    var tools = await session.EntitySet<Linq.ICategory>().Where(c => c.Name == "Tools").ToListAsync();
    Assert.AreEqual(1, tools.Count);
    Assert.AreEqual("Tools", tools[0].Name);
    Assert.IsTrue(cache.Hits > before, "the cache answered it");
    Assert.AreEqual(0, statements.Misses, "and nothing was translated");

    // A CAPTURED VALUE READS THROUGH THE SAME GETTER the SQL binder would have used — the argument is the compiler's
    // display class and the parameter knows how to reach the field, on both paths.
    var wanted = LinqSeed.Categories[2];
    var byLocal = await session.EntitySet<Linq.ICategory>().Where(c => c.Name == wanted).ToListAsync();
    Assert.AreEqual(1, byLocal.Count);
    Assert.AreEqual(wanted, byLocal[0].Name);
    Assert.AreEqual(0, statements.Misses, "still nothing translated");

    WriteTestTitle("an ordering runs in memory too — the rows are here, and sorting them is nothing");
    // The translator marks what the evaluator can do over cached rows, and an ordering is in that set; the answer
    // is the same either way, which is what makes the cache an optimisation rather than a second set of rules.
    var ordered = await session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).ToListAsync();
    CollectionAssert.AreEqual(LinqSeed.Categories.Order().ToArray(), ordered.Select(c => c.Name).ToArray());
    Assert.AreEqual(0, statements.Misses, "nothing was translated for the server");

    WriteTestTitle("the shape is decided ONCE, and the skip flag picks the other slot");
    // ONE TRANSLATION, BOTH SLOTS. The shape was decided answerable from memory AND given its statement at the same
    // time — the read that fills a block has the very shape a caller's own `EntitySet<T>()` has — so asking the same
    // shape with `SkipEntityCache` finds the statement already there.
    var translations = statements.Misses;
    var skipped = await session.EntitySet<Linq.ICategory>(QueryOptions.SkipEntityCache).ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Categories, skipped.Select(c => c.Name).ToArray());
    Assert.AreEqual(translations, statements.Misses, "the skipping call translated nothing");

    var hitsBefore = cache.Hits;
    var fromServer = await session.EntitySet<Linq.ICategory>(QueryOptions.SkipEntityCache).ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, fromServer.Count);
    Assert.AreEqual(translations, statements.Misses, "nor the next");
    Assert.AreEqual(hitsBefore, cache.Hits, "the cache was not consulted");

    // AND THE SAME SHAPE WITHOUT THE FLAG still comes from memory — the option picks between the slots rather than
    // replacing what is in them.
    var fromMemoryAgain = await session.EntitySet<Linq.ICategory>().ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, fromMemoryAgain.Count);
    Assert.IsTrue(cache.Hits > hitsBefore, "the cache answered it");
    Assert.AreEqual(translations, statements.Misses, "and nothing more was translated");

    WriteTestTitle("an uncached entity never pays for the attempt");
    // `Product` is not cached, so the scan says so and no tree is built to be told — which is what keeps the fast
    // path a hash lookup for every query that is not over a cached entity.
    statements.Clear();
    var products = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 1).ToListAsync();
    Assert.AreEqual(1, products.Count);
    Assert.AreEqual(1, statements.Misses, "it went to the server");
  }
}
