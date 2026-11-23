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

// THE SHAPE OF WHAT COMES BACK: a projection of named values, a page composed over as a derived table, grouping with
// its keys and aggregates, set operations over two whole selects, and a sub-query standing where a value is wanted.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task DerivedTable_IsAPageComposedOver() {
    var session = NewSession();

    WriteTestTitle("a filter OF a page, not a filter and a page");
    // The page has to exist before it can be filtered, so it becomes a select of its own. The difference from
    // filtering FIRST is the whole point, and it shows in the answer: the top ten by rank, then the Live ones among
    // them — not the first ten Live ones.
    var topTen = Ranks(_ => true).OrderByDescending(r => r).Take(10).ToList();
    var liveInTopTen = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank"), descending: true)
      .Take(QueryMaker.Constant(10))
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live))));
    CollectionAssert.AreEquivalent(
      topTen.Where(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).ToList(), RanksOf(liveInTopTen));

    // Filtering first is a different query and a different answer — asserted alongside so the difference is visible
    // rather than asserted about.
    var topTenLive = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)))
      .OrderBy(row => row.Member("Rank"), descending: true)
      .Take(QueryMaker.Constant(10)));
    Assert.AreEqual(10, topTenLive.Count);
    Assert.AreNotEqual(topTenLive.Count, liveInTopTen.Count, "the two orders are not the same query");

    WriteTestTitle("an aggregate takes its source WHOLE");
    // `SELECT COUNT(*) … LIMIT 10` would count all sixty and hand back one row of that — so the page is cut off
    // underneath the count, and the answer is ten.
    Assert.AreEqual(10L, await session.ExecuteScalarAsync<long>(session.From("Product")
      .OrderBy(row => row.Member("Rank")).Take(QueryMaker.Constant(10)).Count()));

    Assert.AreEqual(topTen.Sum(LinqSeed.PriceOf), await session.ExecuteScalarAsync<decimal>(session.From("Product")
      .OrderBy(row => row.Member("Rank"), descending: true).Take(QueryMaker.Constant(10))
      .Sum(row => row.Member("Price"))));

    WriteTestTitle("a join off a derived table, and a page of a page's remainder");
    // The foreign key is among the columns the derived table exposes, so the outer scope joins through it exactly as
    // it would through a table's.
    var toolsInTopTwenty = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank"), descending: true)
      .Take(QueryMaker.Constant(20))
      .Where(row => row.Member("Category").Member("Name").Equal(QueryMaker.Constant("Tools")))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(
      Ranks(_ => true).OrderByDescending(r => r).Take(20).Where(r => LinqSeed.CategoryOf(r) == "Tools")
        .Order().ToList(),
      RanksOf(toolsInTopTwenty));

    // AND A LIST QUESTION ASKED OF A PAGED SET — the sub-query correlates to the derived table's alias.
    var taggedInTopTen = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank"), descending: true)
      .Take(QueryMaker.Constant(10))
      .Where(row => row.Member("Tags").Any())
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(topTen.Where(r => LinqSeed.TagsOf(r).Length > 0).Order().ToList(),
      RanksOf(taggedInTopTen));
  }

  [TestMethod]
  public async Task Projection_ReturnsNamedValues() {
    var session = NewSession();

    WriteTestTitle("a column of values — one slot, collapsed");
    var names = await session.ExecuteValuesAsync<string>(session.From("Product")
      .OrderBy(row => row.Member("Rank"))
      .Take(QueryMaker.Constant(3))
      .Select(row => [row.Member("Name")]));
    CollectionAssert.AreEqual(new[] { "Product 01", "Product 02", "Product 03" }, names.ToArray());

    // Of a value type, and through a reference — a projection joins like anything else.
    var ranks = await session.ExecuteValuesAsync<int>(session.From("Product")
      .Where(row => row.Member("Category").Member("Name").Equal(QueryMaker.Constant("Tools")))
      .OrderBy(row => row.Member("Rank"))
      .Select(row => [row.Member("Rank")]));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), ranks.Cast<int>().ToList());

    WriteTestTitle("rows of named values");
    var rows = await session.ExecuteRowsAsync(session.From("Product")
      .Where(row => row.Member("Rank").LessOrEqual(QueryMaker.Parameter("top", typeof(int))))
      .OrderBy(row => row.Member("Rank"))
      .Select(row => [
        row.Member("Rank"),
        row.Member("Category").Member("Name").As("Category"),
        row.Member("Price").Multiply(QueryMaker.Constant(2m)).As("Doubled"),
      ]),
      4);
    Assert.AreEqual(4, rows.Count);
    CollectionAssert.AreEqual(new[] { "Rank", "Category", "Doubled" },
      rows[0].Entity.Members.Select(m => m.Name).ToArray());
    for (var i = 0; i < rows.Count; i++) {
      var rank = i + 1;
      Assert.AreEqual(rank, rows[i].GetValue("Rank"));
      Assert.AreEqual(LinqSeed.CategoryOf(rank), rows[i].GetValue("Category"));
      Assert.AreEqual(LinqSeed.PriceOf(rank) * 2, rows[i].GetValue("Doubled"));
    }

    WriteTestTitle("a nullable slot, and one reached through an OUTER join");
    // Three quarters of the products have no secondary category, so an inner join would return fifteen rows and the
    // nulls would not be there to see.
    var secondaries = await session.ExecuteRowsAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank"))
      .Select(row => [row.Member("Rank"), row.Member("Secondary").Member("Name").As("Secondary")]));
    Assert.AreEqual(LinqSeed.ProductCount, secondaries.Count);
    foreach (var row in secondaries)
      Assert.AreEqual(LinqSeed.SecondaryOf((int)row.GetValue("Rank")!), row.GetValue("Secondary"));
  }

  [TestMethod]
  public async Task Grouping_ReturnsKeysAndAggregates() {
    var session = NewSession();

    WriteTestTitle("the keys alone are the distinct values");
    var categories = await session.ExecuteValuesAsync<string>(session.From("Product")
      .GroupBy(row => [row.Member("Category").Member("Name").As("Category")]));
    CollectionAssert.AreEquivalent(LinqSeed.Categories, categories.ToArray());

    WriteTestTitle("keys, then the aggregates over them");
    var perCategory = await session.ExecuteRowsAsync(session.From("Product")
      .GroupBy(row => [row.Member("Category").Member("Name").As("Category")])
      .Select(g => [
        g.Count().As("Products"),
        g.Sum(p => p.Member("Price")).As("Total"),
        g.Max(p => p.Member("Rank")).As("Newest"),
      ]));
    CollectionAssert.AreEqual(new[] { "Category", "Products", "Total", "Newest" },
      perCategory[0].Entity.Members.Select(m => m.Name).ToArray());
    Assert.AreEqual(LinqSeed.Categories.Length, perCategory.Count);
    foreach (var row in perCategory) {
      var ranks = Ranks(r => LinqSeed.CategoryOf(r) == (string)row.GetValue("Category")!);
      Assert.AreEqual((long)ranks.Count, row.GetValue("Products"));
      Assert.AreEqual(ranks.Sum(LinqSeed.PriceOf), row.GetValue("Total"));
      Assert.AreEqual(ranks.Max(), row.GetValue("Newest"));
    }

    WriteTestTitle("a filter applies to the ROWS, before they are grouped");
    var livePerCategory = await session.ExecuteRowsAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)))
      .GroupBy(row => [row.Member("Category").Member("Name").As("Category")])
      .Select(g => [g.Count().As("Products")]));
    foreach (var row in livePerCategory)
      Assert.AreEqual(
        (long)Ranks(r => LinqSeed.CategoryOf(r) == (string)row.GetValue("Category")!
          && LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Count,
        row.GetValue("Products"));

    WriteTestTitle("two keys — one through a join, one through an OUTER one, so a key may be NULL");
    // Category cycles every three ranks and the secondary every four, so the two are genuinely independent — and
    // three quarters of the products have no secondary at all, which groups as its own NULL key rather than
    // vanishing. An inner join here would have lost forty-five rows.
    var pairs = await session.ExecuteRowsAsync(session.From("Product")
      .GroupBy(row => [
        row.Member("Category").Member("Name").As("Category"),
        row.Member("Secondary").Member("Name").As("Secondary"),
      ])
      .Select(g => [g.Count().As("Products")]));
    var expectedPairs = Ranks(_ => true)
      .Select(r => (Category: LinqSeed.CategoryOf(r), Secondary: LinqSeed.SecondaryOf(r)))
      .Distinct().ToList();
    Assert.AreEqual(expectedPairs.Count, pairs.Count);
    Assert.IsTrue(pairs.Any(p => p.GetValue("Secondary") == null), "the products with no secondary are a group");
    foreach (var row in pairs)
      Assert.AreEqual(
        (long)Ranks(r => LinqSeed.CategoryOf(r) == (string)row.GetValue("Category")!
          && LinqSeed.SecondaryOf(r) == (string?)row.GetValue("Secondary")).Count,
        row.GetValue("Products"));

    WriteTestTitle("HAVING — a condition about the GROUPS rather than about the rows");
    // WHERE and HAVING are the SAME `Where` node, and where it was written is the whole difference: SQL applies
    // FROM, WHERE, GROUP BY, HAVING, so a condition above the grouping is about groups and one below is about rows.
    // Grouping by the secondary category gives one large NULL group and several small ones, so a threshold really
    // discriminates rather than keeping everything or nothing.
    var busy = await session.ExecuteRowsAsync(session.From("Product")
      .GroupBy(row => [row.Member("Secondary").Member("Name").As("Secondary")])
      .Where(g => g.Count().Greater(QueryMaker.Parameter("least", typeof(long))))
      .Select(g => [g.Count().As("Products")]),
      10L);
    var expectedBusy = Ranks(_ => true).GroupBy(LinqSeed.SecondaryOf).Where(g => g.Count() > 10).ToList();
    Assert.AreEqual(expectedBusy.Count, busy.Count);
    Assert.AreNotEqual(0, expectedBusy.Count, "the threshold has to keep something");
    Assert.AreNotEqual(Ranks(_ => true).GroupBy(LinqSeed.SecondaryOf).Count(), expectedBusy.Count,
      "and it has to drop something, or HAVING is doing nothing");
    foreach (var row in busy)
      Assert.AreEqual((long)Ranks(r => LinqSeed.SecondaryOf(r) == (string?)row.GetValue("Secondary")).Count,
        row.GetValue("Products"));

    // AND A CONDITION BELOW THE GROUPING IS STILL A `WHERE`, narrowing the rows before they are grouped at all — the
    // two coexist in one statement and mean different things. Live products are all of one category, so this is one
    // group of twenty rather than three of seven.
    var liveOnly = await session.ExecuteRowsAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)))
      .GroupBy(row => [row.Member("Category").Member("Name").As("Category")])
      .Where(g => g.Count().Greater(QueryMaker.Parameter("least", typeof(long))))
      .Select(g => [g.Count().As("Products")]),
      1L);
    var expectedLive = Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live)
      .GroupBy(LinqSeed.CategoryOf).Where(g => g.Count() > 1).ToList();
    Assert.AreEqual(expectedLive.Count, liveOnly.Count);
    Assert.AreEqual((long)expectedLive[0].Count(), liveOnly[0].GetValue("Products"));

    WriteTestTitle("ordering a grouped result");
    // The ordering goes over the same expression the GROUP BY has, and lands in the same select — which is what
    // LINQ_01 refused for wanting a derived table it turns out not to need.
    var ordered = await session.ExecuteRowsAsync(session.From("Product")
      .GroupBy(row => [row.Member("Category").Member("Name").As("Category")])
      .OrderBy(row => row.Member("Category").Member("Name"), descending: true)
      .Select(g => [g.Count().As("Products")]));
    CollectionAssert.AreEqual(LinqSeed.Categories.OrderDescending().ToArray(),
      ordered.Select(r => (string)r.GetValue("Category")!).ToArray());
  }

  [TestMethod]
  public async Task Projection_ComposedOverAsADerivedTable() {
    var session = NewSession();

    // COMPOSING BACKWARDS IS WHAT NEEDS A SELECT OF ITS OWN. `.OrderBy(…).Select(…)` is one statement — a projection
    // replaces the select list of the scope it sits on — while `.Select(…).OrderBy(…)` asks for an ordering of what
    // the projection PRODUCED, and that is a derived table. The shape it exposes is the projection's own, built
    // where the slots were named, which is what lets `x.Rank` resolve to a column of it.
    WriteTestTitle("ordering and filtering a projection");
    var ordered = await session.EntitySet<Linq.IProduct>()
      .Select(p => new { p.Rank, p.Name })
      .OrderBy(x => x.Rank).Take(3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ordered.Select(x => x.Rank).ToArray());
    Assert.AreEqual("Product 01", ordered[0].Name);

    var filtered = await session.EntitySet<Linq.IProduct>()
      .Select(p => new { p.Rank, p.Name })
      .Where(x => x.Rank <= 3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, filtered.Select(x => x.Rank).Order().ToArray());

    WriteTestTitle("a JOINED projection, which is the commonest way to arrive here");
    // EVERY SLOT IS NAMED IN THE SQL for this to work — `c."Name" AS "Category"` — because the outer scope refers to
    // the derived table's columns by the names the select list gave them, and `Name` is not `Category`.
    var joined = await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>(), p => p.Category.Id, c => c.Id,
        (p, c) => new { p.Rank, Category = c.Name })
      .OrderBy(x => x.Rank).Take(3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, joined.Select(x => x.Rank).ToArray());
    for (var i = 0; i < joined.Count; i++)
      Assert.AreEqual(LinqSeed.CategoryOf(joined[i].Rank), joined[i].Category);

    WriteTestTitle("a ONE-COLUMN projection, where the row IS the column");
    // `Select(p => p.Rank)` collapses to a column of values, and `r` in the next clause means that column — a bare
    // row reaching a comparison would be nothing the translator could write.
    var ranks = await session.EntitySet<Linq.IProduct>()
      .Select(p => p.Rank).Where(r => r <= 3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ranks.Order().ToArray());

    WriteTestTitle("an aggregate over a projection, and a projection over a PAGE");
    // COUNTING A PROJECTION COUNTS ITS ROWS, so the projection is cut off exactly as a page under an aggregate is —
    // and if it were absorbed instead, a `Distinct` on it would be counted away.
    Assert.AreEqual(LinqSeed.ProductCount, await session.EntitySet<Linq.IProduct>()
      .Select(p => new { p.Rank, p.Name }).CountAsync());
    Assert.AreEqual(LinqSeed.Categories.Length, await session.EntitySet<Linq.IProduct>()
      .Select(p => p.Category.Name).Distinct().CountAsync());

    // TWO DERIVED TABLES DEEP: the page is one, the projection over it is another, and the filter is over that.
    var pagedThenProjected = await session.EntitySet<Linq.IProduct>()
      .OrderBy(p => p.Rank).Take(10)
      .Select(p => new { p.Rank, p.Name })
      .Where(x => x.Rank > 8)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 9, 10 }, pagedThenProjected.Select(x => x.Rank).Order().ToArray());
  }

  [TestMethod]
  public async Task TypedGrouping_ProjectsKeyAndAggregates() {
    var session = NewSession();

    WriteTestTitle("one key, and aggregates over the group");
    // `g.Key` is not a member of anything the database has — it is the expression the grouping groups on, which is
    // already in the select list. So the slot for it is DROPPED rather than selected twice; naming it is all it did.
    var perCategory = (await session.EntitySet<Linq.IProduct>()
      .GroupBy(p => p.Category.Name)
      .Select(g => new { Category = g.Key, Products = g.Count(), Total = g.Sum(p => p.Price) })
      .ToListAsync())
      .OrderBy(x => x.Category).ToList();
    Assert.AreEqual(LinqSeed.Categories.Length, perCategory.Count);
    foreach (var row in perCategory) {
      var mine = Ranks(r => LinqSeed.CategoryOf(r) == row.Category);
      Assert.AreEqual(mine.Count, row.Products);
      Assert.AreEqual(mine.Sum(LinqSeed.PriceOf), row.Total);
    }

    WriteTestTitle("a composite key, named through g.Key");
    var perPair = await session.EntitySet<Linq.IProduct>()
      .GroupBy(p => new { p.Category.Name, p.Status })
      .Select(g => new { g.Key.Name, g.Key.Status, Products = g.Count() })
      .ToListAsync();
    var expectedPairs = Ranks(_ => true)
      .Select(r => (Name: LinqSeed.CategoryOf(r), Status: LinqSeed.StatusOf(r))).Distinct().ToList();
    Assert.AreEqual(expectedPairs.Count, perPair.Count);
    foreach (var row in perPair)
      Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == row.Name && LinqSeed.StatusOf(r) == row.Status).Count,
        row.Products);

    WriteTestTitle("an ELEMENT selector — what the group is a sequence OF");
    // `GroupBy(k, p => p.Price)` makes each group a sequence of PRICES, so `g.Sum()` sums those. It changes nothing
    // about the SQL — the grouping and its keys are the same — and only decides what an aggregate written with no
    // selector is over. The selector is kept as the EXPRESSION it was written as and re-converted per aggregate,
    // which is why `Sum` and `Max` below can each hand out a row of their own.
    var pricesByCategory = await session.EntitySet<Linq.IProduct>()
      .GroupBy(p => p.Category.Name, p => p.Price)
      .Select(g => new { Category = g.Key, Total = g.Sum(), Dearest = g.Max(), Products = g.Count() })
      .ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, pricesByCategory.Count);
    foreach (var row in pricesByCategory) {
      var ranks = Ranks(r => LinqSeed.CategoryOf(r) == row.Category);
      Assert.AreEqual(ranks.Sum(LinqSeed.PriceOf), row.Total);
      Assert.AreEqual(ranks.Max(LinqSeed.PriceOf), row.Dearest);
      Assert.AreEqual(ranks.Count, row.Products);
    }

    // AND A SELECTOR ON TOP OF ONE IS REFUSED rather than misread: `x` there is the ELEMENT, not a row, so
    // converting it the ordinary way would aggregate something else entirely and say nothing about it.
    var doubled = await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>()
        .GroupBy(p => p.Category.Name, p => p.Price)
        .Select(g => new { Category = g.Key, Total = g.Sum(x => x * 2) })
        .ToListAsync());
    StringAssert.Contains(doubled.Message, "takes no selector");

    WriteTestTitle("filtered before grouping, and the keys alone");
    var liveByCategory = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Status == Linq.ProductStatus.Live)
      .GroupBy(p => p.Category.Name)
      .Select(g => new { Category = g.Key, Products = g.Count() })
      .ToListAsync();
    var expectedLive = Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live)
      .GroupBy(LinqSeed.CategoryOf).ToList();
    Assert.AreEqual(expectedLive.Count, liveByCategory.Count);

    // A PROJECTION OF NOTHING BUT KEYS is a bare grouping — the distinct values, which is what the keys already are.
    var distinctCategories = await session.EntitySet<Linq.IProduct>()
      .GroupBy(p => p.Category.Name)
      .Select(g => g.Key)
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Categories, distinctCategories.ToArray());

    WriteTestTitle("what it still refuses, and says so");
    // THE RESULT-SELECTOR OVERLOAD reduces the group at the `GroupBy` itself — which is what the `.Select(g => …)`
    // one step later already does, and the only form of it the engine has.
    var resultSelector = await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>()
        .GroupBy(p => p.Category.Name, p => p.Price, (name, prices) => new { Category = name, Total = prices.Sum() })
        .ToListAsync());
    StringAssert.Contains(resultSelector.Message, "RESULT selector");
  }

  [TestMethod]
  public async Task SetOperations_CombineTwoWholeSelects() {
    var session = NewSession();

    IQueryable<Linq.IProduct> Low() => session.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 10);
    IQueryable<Linq.IProduct> Even() => session.EntitySet<Linq.IProduct>().Where(p => p.Rank % 2 == 0);

    // DB2 HOLDS UNLIMITED TEXT AND A JSON MEMBER AS A CLOB, which it compares with nothing: a union, an intersect
    // or an except over whole products is the server's to refuse. UNION ALL compares nothing, and the three over
    // columns that are not a CLOB run.
    if (Fixture.ServerKind == DbServerKind.Db2) {
      var refused = await Assert.ThrowsExactlyAsync<DataAccessException>(() => Low().Union(Even()).ToListAsync());
      StringAssert.Contains(refused.Message, "SQL0134N");
      var kept = await Low().Concat(Even()).ToListAsync();
      Assert.AreEqual(Ranks(r => r <= 10).Count + Ranks(r => r % 2 == 0).Count, kept.Count);
      IQueryable<int> LowRanks() => Low().Select(p => p.Rank);
      IQueryable<int> EvenRanks() => Even().Select(p => p.Rank);
      Assert.AreEqual(Ranks(r => r <= 10 || r % 2 == 0).Count,
        (await LowRanks().Union(EvenRanks()).ToListAsync()).Count);
      Assert.AreEqual(Ranks(r => r <= 10 && r % 2 == 0).Count,
        (await LowRanks().Intersect(EvenRanks()).ToListAsync()).Count);
      Assert.AreEqual(Ranks(r => r <= 10 && r % 2 != 0).Count,
        (await LowRanks().Except(EvenRanks()).ToListAsync()).Count);
      return;
    }

    WriteTestTitle("union, and union all");
    // `Union` discards duplicates and `Concat` keeps them — the whole difference between UNION and UNION ALL, and
    // the ten ranks in both halves are what shows it.
    var union = await Low().Union(Even()).ToListAsync();
    var expectedUnion = Ranks(r => r <= 10 || r % 2 == 0);
    Assert.AreEqual(expectedUnion.Count, union.Count);

    var concat = await Low().Concat(Even()).ToListAsync();
    Assert.AreEqual(Ranks(r => r <= 10).Count + Ranks(r => r % 2 == 0).Count, concat.Count);
    Assert.AreNotEqual(union.Count, concat.Count, "duplicates are kept by one and not the other");

    WriteTestTitle("intersect and except");
    var lowAndEven = await Low().Intersect(Even()).ToListAsync();
    Assert.AreEqual(Ranks(r => r <= 10 && r % 2 == 0).Count, lowAndEven.Count);

    // Oracle spells this one `MINUS`, which is why the keyword crosses the target seam.
    var onlyLow = await Low().Except(Even()).ToListAsync();
    Assert.AreEqual(Ranks(r => r <= 10 && r % 2 != 0).Count, onlyLow.Count);

    WriteTestTitle("composing over one makes it a derived table");
    // A union is not `SELECT … FROM …`, so anything written over it needs a select of its own to sit in — which is
    // what the translator's recursion has said only a set operation can be since it was written.
    var ordered = await Low().Union(Even()).OrderBy(p => p.Rank).Take(3).ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ordered.Select(p => p.Rank).ToArray());

    Assert.AreEqual(expectedUnion.Count, await Low().Union(Even()).CountAsync());

    WriteTestTitle("over projections, and the untyped builder saying the same thing");
    var names = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 2).Select(p => p.Name)
      .Union(session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 2).Select(p => p.Name))
      .ToListAsync();
    CollectionAssert.AreEquivalent(new[] { "Product 01", "Product 02" }, names.ToArray());

    var untyped = await session.ExecuteQueryAsync(
      session.From("Product").Where(row => row.Member("Rank").LessOrEqual(QueryMaker.Constant(3)))
        .Union(session.From("Product").Where(row => row.Member("Rank").Equal(QueryMaker.Constant(9)))));
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 9 }, RanksOf(untyped).ToArray());

    // A union of two single-STRING columns is legitimate whatever entities they came from, and answers as one —
    // which is why the mismatch below has to be written by name. C#'s own types make it unwritable typed.
    var mixed = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 1).Select(p => p.Name)
      .Union(session.EntitySet<Linq.ICategory>().Select(c => c.Name))
      .ToListAsync();
    Assert.AreEqual(1 + LinqSeed.Categories.Length, mixed.Count);

    WriteTestTitle("two different shapes are refused by name");
    // Both sides return ENTITY rows, and different entities — so the columns do not correspond and the server would
    // be asked to line up a Product with a Category. The widths differ too, and that is what it says first.
    var mismatched = Assert.ThrowsExactly<NotSupportedException>(() => session.ExecuteQueryAsync(
      session.From("Product").Union(session.From("Category"))).GetAwaiter().GetResult());
    StringAssert.Contains(mismatched.Message, "different shapes");

    var widths = Assert.ThrowsExactly<NotSupportedException>(() => session.ExecuteRowsAsync(
      session.From("Product").Select(row => [row.Member("Name"), row.Member("Rank")])
        .Union(session.From("Category").Select(row => [row.Member("Name")]))).GetAwaiter().GetResult());
    StringAssert.Contains(widths.Message, "different shapes");
  }

  [TestMethod]
  public async Task SubQuery_StandsWhereAValueIsWanted() {
    var session = NewSession();
    // A SUB-QUERY IS A LOCAL VARIABLE holding the query — `session.EntitySet<T>()` cannot be called inside a lambda.
    var categories = session.EntitySet<Linq.ICategory>();
    var products = session.EntitySet<Linq.IProduct>();

    // THE SECOND WAY TO OPEN A SCOPE, and the uncorrelated one. Every other sub-query in this suite starts from a
    // LIST MEMBER and is tied to the outer row by the model's own key pairing; these start from a QUERY and are tied
    // to nothing, which is why they are worth their own test rather than a case in the list one.
    WriteTestTitle("IN (SELECT …) — a query where a list of values would go");
    var inTools = await session.EntitySet<Linq.IProduct>()
      .Where(p => categories.Where(c => c.Name == "Tools").Select(c => c.Id)
        .Contains(p.Category.Id))
      .OrderBy(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), inTools.Select(p => p.Rank).ToList());

    // AND `Category` IS A CACHED ENTITY, and a sub-query over one is EVALUATED IN MEMORY: it becomes a parameter whose getter
    // runs it over the block, and the statement binds the values it produced.
    WriteTestTitle("an aggregate of ANOTHER set, used as a value");
    var pastCategoryCount = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank > categories.Count())
      .OrderBy(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r > LinqSeed.Categories.Length),
      pastCategoryCount.Select(p => p.Rank).ToList());

    // THE SAME ENTITY ON BOTH SIDES, which is what shows the aliases come from one counter: the outer table and the
    // sub-query's are both Product and must not be the same alias, or the sub-query would read the outer row.
    var dearest = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Price >= products.Max(x => x.Price))
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { LinqSeed.ProductCount }, dearest.Select(p => p.Rank).ToArray());

    WriteTestTitle("composed over, and filtered by the caller's own value");
    // The sub-query is an ordinary query and takes anything one can: a filter of its own, and a parameter bound from
    // the caller's closure — which is bound once for the statement however deep in it the value sits.
    var cheap = 40m;
    var cheaper = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank > products.Where(x => x.Price < cheap).Count())
      .OrderBy(p => p.Rank).Take(3).ToListAsync();
    var cheapCount = Ranks(r => LinqSeed.PriceOf(r) < cheap).Count;
    CollectionAssert.AreEqual(new[] { cheapCount + 1, cheapCount + 2, cheapCount + 3 },
      cheaper.Select(p => p.Rank).ToArray());

    WriteTestTitle("a query used as a value has to produce one column");
    // `IN (SELECT * FROM …)` is not a value comparison on any server, so it is refused by name rather than sent.
    var wholeRows = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.ExecuteQueryAsync(
      session.From("Product").Where(row =>
        row.Member("Rank").In(QueryMaker.From(session.GetLinqModel(), "Category")))));
    StringAssert.Contains(wholeRows.Message, "ONE column");
  }

  [TestMethod]
  public async Task TypedProjection_SelectsAConditionAsAColumn() {
    // A comparison and an Any() in a projection are conditions the server evaluates per row. A server with no
    // Boolean to select, SQL Server, is given each as a value; the others select it as written.
    var session = NewSession();
    var flagged = (await session.EntitySet<Linq.IProduct>()
      .Select(p => new { p.Rank, Top = p.Rank > 8, Tagged = p.TagQuery.Any() })
      .ToListAsync())
      .OrderBy(x => x.Rank).ToList();
    Assert.AreEqual(LinqSeed.ProductCount, flagged.Count);
    CollectionAssert.AreEqual(Ranks(r => r > 8), flagged.Where(x => x.Top).Select(x => x.Rank).ToList());
    var tagged = (await session.EntitySet<Linq.IProduct>().Where(p => p.TagQuery.Any()).Select(p => p.Rank)
      .ToListAsync()).Order().ToList();
    CollectionAssert.AreEqual(tagged, flagged.Where(x => x.Tagged).Select(x => x.Rank).ToList(),
      "the column agrees with the same condition as a filter");
    Assert.IsTrue(tagged.Count > 0 && tagged.Count < LinqSeed.ProductCount, "some tagged, some not: " + tagged.Count);
  }

  // A GROUPING RETURNED AS GROUPS is done in memory over the rows the statement selects, and comes back as the typed
  // groups the query declares: the key as written, the rows as entities or values.
  [TestMethod]
  public async Task Grouping_ReturnedAsGroups_IsTypedGroups() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var statuses = Ranks(_ => true).GroupBy(LinqSeed.StatusOf).ToDictionary(g => g.Key, g => g.ToList());

    WriteTestTitle("by a value: the key typed, the rows entities the session tracks");
    var byStatus = await products.GroupBy(p => p.Status).ToListAsync();
    Assert.AreEqual(statuses.Count, byStatus.Count);
    foreach (var group in byStatus)
      CollectionAssert.AreEquivalent(statuses[group.Key], group.Select(p => p.Rank).ToList());
    var first = byStatus[0].First();
    Assert.AreSame(first, session.GetEntity<Linq.IProduct>(first.Id));
    StringAssert.Contains(session.LastCommands.Last().Sql, "SELECT");
    Assert.IsFalse(session.LastCommands.Last().Sql.Contains("GROUP BY"), "the statement selects the rows");

    WriteTestTitle("by a reference's member, by an entity, and by a new { … } of two values and of one");
    var byName = await products.GroupBy(p => p.Category.Name).ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Categories, byName.Select(g => g.Key).ToArray());
    var byCategory = await products.GroupBy(p => p.Category).ToListAsync();
    foreach (var group in byCategory)
      Assert.IsTrue(group.All(p => LinqSeed.CategoryOf(p.Rank) == group.Key.Name));
    var byTwo = await products.GroupBy(p => new { p.Status, Category = p.Category.Name }).ToListAsync();
    Assert.AreEqual(Ranks(_ => true).Select(r => (LinqSeed.StatusOf(r), LinqSeed.CategoryOf(r))).Distinct().Count(),
      byTwo.Count);
    foreach (var group in byTwo)
      Assert.IsTrue(group.All(p => LinqSeed.StatusOf(p.Rank) == group.Key.Status
        && LinqSeed.CategoryOf(p.Rank) == group.Key.Category));
    var byOne = await products.GroupBy(p => new { p.Status }).ToListAsync();
    CollectionAssert.AreEquivalent(statuses.Keys.ToList(), byOne.Select(g => g.Key.Status).ToList());

    WriteTestTitle("the rows as one value each");
    var prices = await products.GroupBy(p => p.Status, p => p.Price).ToListAsync();
    foreach (var group in prices)
      Assert.AreEqual(statuses[group.Key].Sum(LinqSeed.PriceOf), group.Sum());

    WriteTestTitle("filtered before the grouping; the groups filtered and ordered after it");
    var early = await products.Where(p => p.Rank <= 10).GroupBy(p => p.Status).ToListAsync();
    Assert.AreEqual(10, early.Sum(g => g.Count()));
    var busiest = await products.GroupBy(p => p.Category.Name).Where(g => g.Count() > 5).OrderBy(g => g.Key)
      .ToListAsync();
    CollectionAssert.AreEqual(LinqSeed.Categories.Order().ToArray(), busiest.Select(g => g.Key).ToArray());

    WriteTestTitle("an include on the session reaches the rows inside the groups");
    var including = NewSession();
    including.AddInclude<Linq.IProduct>(p => p.Tags);
    var withTags = await including.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 6).GroupBy(p => p.Status)
      .ToListAsync();
    var commands = including.LastCommands;
    Assert.AreEqual(6, withTags.SelectMany(g => g).Count(p => p.Tags.Count >= 0));
    Assert.AreSame(commands, including.LastCommands, "reading the tags ran nothing: they came with the query");

    WriteTestTitle("what it refuses: a new { … } as the rows of a group");
    var refused = await Assert.ThrowsAsync<NotSupportedException>(
      () => products.GroupBy(p => p.Status, p => new { p.Rank, p.Name }).ToListAsync());
    StringAssert.Contains(refused.Message, "Project the groups");
  }

  public record RankRecord(int Rank);

  public class RankHolder {
    public int Rank { get; set; }
    public string? Label { get; set; }
  }

  // AN OBJECT OF ONE VALUE: the engine answers a projection of one value with the values, and the typed query builds
  // the object the caller wrote around each.
  [TestMethod]
  public async Task TypedProjection_OfOneValue_IsTheObjectWritten() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var everyRank = Ranks(_ => true);

    WriteTestTitle("an anonymous type, a record and an initializer of one value");
    var anonymous = await products.Select(p => new { p.Rank }).ToListAsync();
    CollectionAssert.AreEquivalent(everyRank, anonymous.Select(x => x.Rank).ToList());
    var records = await products.Select(p => new RankRecord(p.Rank)).ToListAsync();
    CollectionAssert.AreEquivalent(everyRank, records.Select(x => x.Rank).ToList());
    var holders = await products.Select(p => new RankHolder { Rank = p.Rank }).ToListAsync();
    CollectionAssert.AreEquivalent(everyRank, holders.Select(x => x.Rank).ToList());
    Assert.IsTrue(holders.All(x => x.Label == null), "the member not selected is left as made");

    WriteTestTitle("with a filter, an ordering and a page after it, and as one row");
    var paged = await products.Select(p => new { p.Rank }).Where(x => x.Rank > 10).OrderBy(x => x.Rank).Take(3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 11, 12, 13 }, paged.Select(x => x.Rank).ToArray());
    var one = await products.Where(p => p.Rank == 5).Select(p => new { p.Rank }).OrderBy(x => x.Rank).FirstAsync();
    Assert.AreEqual(5, one.Rank);

    WriteTestTitle("the result of a join");
    var joined = await (from p in products
                        join c in session.EntitySet<Linq.ICategory>() on p.Category equals c
                        where p.Rank <= 3
                        select new { c.Name }).ToListAsync();
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }.Select(LinqSeed.CategoryOf).ToList(),
      joined.Select(x => x.Name).ToList());

    WriteTestTitle("a value that is null is an object holding null; a null selected bare stays null");
    var retired = await products.Select(p => new { p.RetiredOn }).ToListAsync();
    var bare = await products.Select(p => p.RetiredOn).ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, retired.Count);
    Assert.IsTrue(retired.All(x => x != null));
    Assert.IsTrue(bare.Any(x => x == null), "the seed retires only some");
    Assert.AreEqual(bare.Count(x => x == null), retired.Count(x => x.RetiredOn == null));
    var specs = await products.Select(p => p.Spec).ToListAsync();
    Assert.AreEqual(Ranks(r => r % 10 == 0).Count, specs.Count(x => x == null),
      "a document selected bare is not rebuilt");

    WriteTestTitle("one reference: the entity inside the object");
    var categories = await products.Where(p => p.Rank <= 3).Select(p => new { p.Category }).ToListAsync();
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }.Select(LinqSeed.CategoryOf).ToList(),
      categories.Select(x => x.Category.Name).ToList());
  }

  // AN OPTIONAL REFERENCE SELECTED WHOLE: the row of the outer join is all nulls where the reference points at
  // nothing, and the entity is null.
  [TestMethod]
  public async Task OptionalReference_SelectedWhole_IsNullWhereItPointsAtNothing() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var withSecondary = Ranks(r => LinqSeed.SecondaryOf(r) != null).Count;

    WriteTestTitle("selected bare");
    var secondaries = await products.Select(p => p.Secondary).ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, secondaries.Count);
    Assert.AreEqual(withSecondary, secondaries.Count(c => c != null));

    WriteTestTitle("as a slot beside a value, and as the one slot");
    var pairs = await products.Select(p => new { p.Rank, p.Secondary }).ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, pairs.Count);
    foreach (var pair in pairs)
      Assert.AreEqual(LinqSeed.SecondaryOf(pair.Rank), pair.Secondary?.Name);
    var alone = await products.Select(p => new { p.Secondary }).ToListAsync();
    Assert.AreEqual(withSecondary, alone.Count(x => x.Secondary != null));
    Assert.AreEqual(LinqSeed.ProductCount - withSecondary, alone.Count(x => x != null && x.Secondary == null));

    WriteTestTitle("the row of a left outer join");
    var outer = await (from p in products
                       join c in session.EntitySet<Linq.ICategory>() on p.Secondary equals c into matches
                       from c in matches.DefaultIfEmpty()
                       select c).ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount - withSecondary, outer.Count(c => c == null));

    WriteTestTitle("with an include on the session: nothing is included on a null");
    var including = NewSession();
    including.AddInclude<Linq.ICategory>(c => c.Products);
    var included = await including.EntitySet<Linq.IProduct>().Select(p => p.Secondary).ToListAsync();
    Assert.AreEqual(withSecondary, included.Count(c => c != null));
  }
}
