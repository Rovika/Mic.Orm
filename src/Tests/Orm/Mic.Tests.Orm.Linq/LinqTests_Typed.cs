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

// THE TYPED PATH: `EntitySet<T>()` as ordinary LINQ onto the same engine, every operator the untyped tests prove again
// through C#'s own expressions, and an entity compared as a whole becoming a comparison of its key.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task TypedQuery_IsOrdinaryLinq() {
    var session = NewSession();
    var cache = App.DataAccess.FindDatabase()!.QueryCache;
    cache.Clear();

    WriteTestTitle("a whole set, and instances rather than records");
    var products = await session.EntitySet<Linq.IProduct>().ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, products.Count);
    // The typed path answers with what the caller asked for. The record is still underneath — it is what the identity
    // map holds and what a save works on — and `products[0]` is the view on one.
    Assert.IsInstanceOfType<Linq.IProduct>(products[0]);

    WriteTestTitle("a captured local becomes a parameter, so one statement serves every value");
    // `from` and `to` are locals the compiler hoisted into a display class. Nothing about them reaches the cache key
    // or the SQL: the statement says `Rank >= @p0 AND Rank <= @p1` and the display class is what gets bound.
    async Task<List<int>> BetweenAsync(int from, int to) =>
      [.. (await session.EntitySet<Linq.IProduct>()
        .Where(p => p.Rank >= from && p.Rank <= to)
        .OrderBy(p => p.Rank)
        .ToListAsync()).Select(p => p.Rank)];

    CollectionAssert.AreEqual(Ranks(r => r is >= 11 and <= 20), await BetweenAsync(11, 20));
    CollectionAssert.AreEqual(Ranks(r => r is >= 1 and <= 5), await BetweenAsync(1, 5));
    Assert.AreEqual(2, cache.Misses, "the whole set, then one shape for both rank ranges");

    // A LITERAL IS A DIFFERENT MATTER, and the rule is the untyped path's: a constant is part of the shape, is
    // written into the SQL, and two constants are two statements.
    var live = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Status == Linq.ProductStatus.Live).ToListAsync();
    var draft = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Status == Linq.ProductStatus.Draft).ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Count, live.Count);
    Assert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Draft).Count, draft.Count);
    Assert.AreEqual(4, cache.Misses, "two constants are two statements");

    WriteTestTitle("null, a reference walked through, and a page");
    var stillSold = await session.EntitySet<Linq.IProduct>().Where(p => p.RetiredOn == null).ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.RetiredOn(r) == null).Count, stillSold.Count);

    var tools = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category.Name == "Tools")
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), tools.Select(p => p.Rank).ToList());

    var page = await session.EntitySet<Linq.IProduct>()
      .OrderBy(p => p.Rank).Skip(10).Take(5).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r is >= 11 and <= 15), page.Select(p => p.Rank).ToList());

    // TWO TERMS, AND THE SECOND IS `ThenBy` — a second `OrderBy` reverses the terms in LINQ and is refused rather
    // than quietly sorted the other way round.
    var byCategoryThenRank = await session.EntitySet<Linq.IProduct>()
      .OrderBy(p => p.Category.Name).ThenByDescending(p => p.Rank).Take(3).ToListAsync();
    Assert.AreEqual(3, byCategoryThenRank.Count);
    await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>().OrderBy(p => p.Rank).OrderBy(p => p.Name).ToListAsync());

    WriteTestTitle("a list asked a question, and asked what it comes to");
    var tag = LinqSeed.Tags[0];
    var onSale = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Tags.Any(t => t.Name == tag))
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Contains(tag)), onSale.Select(p => p.Rank).ToList());

    // `All` IS THE SAME SUB-QUERY, TWICE NEGATED — `NOT EXISTS (… WHERE NOT …)` — so it needed nothing of its own.
    // THE EMPTY LIST ANSWERS TRUE, which is what C# means by `All` and what `NOT EXISTS` says in SQL: the untagged
    // products are in this answer, and that agreement is the whole reason no correction is applied.
    var allOnSale = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Tags.All(t => t.Name == tag))
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).All(t => t == tag)),
      allOnSale.Select(p => p.Rank).ToList());
    Assert.IsTrue(allOnSale.Any(p => LinqSeed.TagsOf(p.Rank).Length == 0), "an empty list satisfies All");

    WriteTestTitle("a list NARROWED before it is asked");
    // `Count(t => …)` is written out as `Where(t => …).Count()`, so the filter lands in the sub-query's own WHERE
    // beside the correlation — and a caller who writes the long form gets the same statement.
    var oneMatchingTag = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Tags.Count(t => t.Name == tag) == 1)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Count(t => t == tag) == 1),
      oneMatchingTag.Select(p => p.Rank).ToList());

    var sameTheLongWay = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Tags.Where(t => t.Name == tag).Count() == 1)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(oneMatchingTag.Select(p => p.Rank).ToList(),
      sameTheLongWay.Select(p => p.Rank).ToList());

    // AND EVERY OTHER AGGREGATE OVER THE FILTERED LIST, which is why the filter is a `Where` node rather than a slot
    // on `Count`: the selector slot Sum, Min, Max and Average already keep is untouched.
    var dearestEarlyTools = await session.EntitySet<Linq.ICategory>()
      .Where(c => c.Name == "Tools")
      .Select(c => c.Products.Where(p => p.Rank <= 30).Max(p => p.Price))
      .ToListAsync();
    CollectionAssert.AreEqual(
      new[] { Ranks(r => LinqSeed.CategoryOf(r) == "Tools" && r <= 30).Max(LinqSeed.PriceOf) },
      dearestEarlyTools.ToArray());

    WriteTestTitle("the predicate overloads of the terminals, and Any as a real EXISTS");
    Assert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Count,
      await session.EntitySet<Linq.IProduct>().CountAsync(p => p.Status == Linq.ProductStatus.Live));
    // `SELECT CASE WHEN EXISTS (…) THEN 1 ELSE 0 END` — a condition cannot be a statement's answer on four of the
    // five servers, and Oracle needs a `FROM dual` besides. `All` is the same statement over the counter-examples.
    Assert.IsTrue(await session.EntitySet<Linq.IProduct>().AnyAsync(p => p.Rank == LinqSeed.ProductCount));
    Assert.IsFalse(await session.EntitySet<Linq.IProduct>().AnyAsync(p => p.Rank > LinqSeed.ProductCount));
    Assert.IsTrue(await session.EntitySet<Linq.IProduct>().AnyAsync());
    Assert.IsTrue(await session.EntitySet<Linq.IProduct>().AllAsync(p => p.Rank <= LinqSeed.ProductCount));
    Assert.IsFalse(await session.EntitySet<Linq.IProduct>().AllAsync(p => p.Rank > 1));

    // AND `Any` OVER ANOTHER SET, as a condition — the set held in a local, since a query cannot be started inside a
    // lambda; and `Category` being cached, the question is answered in memory and bound as a value.
    var categories = session.EntitySet<Linq.ICategory>();
    var whileToolsExist = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 3 && categories.Any(c => c.Name == "Tools"))
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, whileToolsExist.Select(p => p.Rank).ToArray());

    var least = 20;
    var busyCategory = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category.Products.Count() > least)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    Assert.AreEqual(
      Ranks(r => Ranks(o => LinqSeed.CategoryOf(o) == LinqSeed.CategoryOf(r)).Count > least).Count,
      busyCategory.Count);

    WriteTestTitle("IN, over a collection the caller holds");
    var wanted = new[] { 3, 7, 11, 99 };
    var matched = await session.EntitySet<Linq.IProduct>()
      .Where(p => wanted.Contains(p.Rank))
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 3, 7, 11 }, matched.Select(p => p.Rank).ToArray());
    var wantedList = wanted.ToList();                   // the instance Contains of a List<T>, respelled as the static
    var listed = await session.EntitySet<Linq.IProduct>().Where(p => wantedList.Contains(p.Rank)).OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 3, 7, 11 }, listed.Select(p => p.Rank).ToArray());

    WriteTestTitle("a column of values, and the aggregates");
    var names = await session.EntitySet<Linq.IProduct>()
      .OrderBy(p => p.Rank).Take(3).Select(p => p.Name).ToListAsync();
    CollectionAssert.AreEqual(new[] { "Product 01", "Product 02", "Product 03" }, names.ToArray());

    Assert.AreEqual(LinqSeed.ProductCount, await session.EntitySet<Linq.IProduct>().CountAsync());
    Assert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Count,
      await session.EntitySet<Linq.IProduct>().Where(p => p.Status == Linq.ProductStatus.Live).CountAsync());
    Assert.AreEqual(Ranks(_ => true).Sum(LinqSeed.PriceOf),
      await session.EntitySet<Linq.IProduct>().SumAsync(p => p.Price));
    Assert.AreEqual(LinqSeed.PriceOf(1), await session.EntitySet<Linq.IProduct>().MinAsync(p => p.Price));
    Assert.AreEqual(LinqSeed.PriceOf(LinqSeed.ProductCount),
      await session.EntitySet<Linq.IProduct>().MaxAsync(p => p.Price));

    WriteTestTitle("a projection into an anonymous type");
    // The rows come back as named values and the TYPE PROJECTED INTO is what turns them into instances — matched by
    // name, so nothing depends on the order the columns arrived in.
    var summaries = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 3)
      .OrderBy(p => p.Rank)
      .Select(p => new { p.Rank, Category = p.Category.Name, Doubled = p.Price * 2 })
      .ToListAsync();
    Assert.AreEqual(3, summaries.Count);
    for (var i = 0; i < summaries.Count; i++) {
      var rank = i + 1;
      Assert.AreEqual(rank, summaries[i].Rank);
      Assert.AreEqual(LinqSeed.CategoryOf(rank), summaries[i].Category);
      Assert.AreEqual(LinqSeed.PriceOf(rank) * 2, summaries[i].Doubled);
    }

    // AND INTO A DECLARED TYPE, through an object initializer — the same rows, matched by property name.
    var lines = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 2)
      .OrderBy(p => p.Rank)
      .Select(p => new ProductLine { Rank = p.Rank, Name = p.Name })
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "Product 01", "Product 02" }, lines.Select(l => l.Name).ToArray());
    CollectionAssert.AreEqual(new[] { 1, 2 }, lines.Select(l => l.Rank).ToArray());

    WriteTestTitle("what it refuses, and says so");
    // SYNCHRONOUS EXECUTION. There is none, on either half of the framework, and this is where a developer who
    // wrote `foreach` finds out — with a message that names what to write instead.
    var sync = Assert.ThrowsExactly<NotSupportedException>(() => session.EntitySet<Linq.IProduct>().ToList());
    StringAssert.Contains(sync.Message, "ToListAsync");

  }

  [TestMethod]
  public async Task EntityComparison_BecomesAKeyComparison() {
    var session = NewSession();
    var cache = App.DataAccess.FindDatabase()!.QueryCache;
    cache.Clear();

    WriteTestTitle("a reference compared to an entity the caller holds");
    // `p.Category == category` is a question about `p`'s FOREIGN KEY COLUMN and a value — never a join to the
    // category table, and never a load of the entity to read its id first.
    var first = (await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 1).ToListAsync())[0];
    var category = first.Category;
    var sameCategory = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category == category)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == category.Name),
      sameCategory.Select(p => p.Rank).ToList());

    // ONE STATEMENT FOR EVERY CATEGORY, because the entity is a captured value and its key is one more link on the
    // parameter's chain — so a different category is a different argument, not a different query.
    var secondCategory = (await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 2).ToListAsync())[0].Category;
    var otherCategory = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category == secondCategory)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == secondCategory.Name),
      otherCategory.Select(p => p.Rank).ToList());
    Assert.AreNotEqual(category.Name, secondCategory.Name, "the two categories differ, so the answers should");

    WriteTestTitle("not equal, and null");
    var elsewhere = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category != category)
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) != category.Name).Count, elsewhere.Count);

    // A NULLABLE REFERENCE COMPARED TO NULL is IS NULL on the foreign key, with no join at all.
    var noSecondary = await session.EntitySet<Linq.IProduct>().Where(p => p.Secondary == null).ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == null).Count, noSecondary.Count);

    var withSecondary = await session.EntitySet<Linq.IProduct>().Where(p => p.Secondary != null).ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) != null).Count, withSecondary.Count);

    WriteTestTitle("two references compared to each other");
    // Both sides are columns, lined up pairwise — the products whose second category is also their first.
    var sameBoth = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Secondary == p.Category)
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == LinqSeed.CategoryOf(r)).Count, sameBoth.Count);

    WriteTestTitle("the row itself, compared to an entity");
    var justOne = await session.EntitySet<Linq.IProduct>().Where(p => p == first).ToListAsync();
    Assert.AreEqual(1, justOne.Count);
    Assert.AreEqual(1, justOne[0].Rank);

    WriteTestTitle("two rows of one query compared to each other: by their keys");
    var products = session.EntitySet<Linq.IProduct>();
    var pairs = await (
      from a in products
      from b in products
      where a.Rank <= 3 && b.Rank <= 3 && a != b
      select new { First = a.Rank, Second = b.Rank }).ToListAsync();
    Assert.AreEqual(6, pairs.Count, "each of three products with the two others");
    Assert.IsTrue(pairs.All(p => p.First != p.Second));
    var selves = await (
      from a in products
      from b in products
      where a.Rank <= 3 && b.Rank <= 3 && a == b
      select a.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, selves.ToList());

    WriteTestTitle("the row of an outer join tested for null: by its key");
    var categories = session.EntitySet<Linq.ICategory>();
    var unmatched = await (
      from p in products
      join c in categories on p.Secondary!.Id equals c.Id into matches
      from c in matches.DefaultIfEmpty()
      where c == null
      select p.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) == null), unmatched.ToList());
    var matched = await (
      from p in products
      join c in categories on p.Secondary!.Id equals c.Id into matches
      from c in matches.DefaultIfEmpty()
      where c != null
      select p.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) != null), matched.ToList());

    // AND INSIDE A SUB-QUERY, where the comparison is over the inner row.
    var tagged = await session.EntitySet<Linq.ICategory>()
      .Where(c => c.Products.Any(p => p.Secondary == category))
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == category.Name).Select(LinqSeed.CategoryOf).Distinct().Count(),
      tagged.Count);

    // ----- BY NAME: the same comparisons, the values brought the way a query built by name brings them -----------------
    WriteTestTitle("by name: a parameter that CARRIES its value — what a captured local is to C#");
    var misses = cache.Misses;
    QueryNode DearerThan(decimal price) => session.From("Product")
      .Where(p => p.Member("Price").Greater(QueryMaker.Parameter("price", price)))
      .OrderBy(p => p.Member("Rank"));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.PriceOf(r) > 100m), RanksOf(await session.ExecuteQueryAsync(DearerThan(100m))));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.PriceOf(r) > 140m), RanksOf(await session.ExecuteQueryAsync(DearerThan(140m))));
    Assert.AreEqual(misses + 1, cache.Misses, "the value is no part of the shape: two values, one statement");

    WriteTestTitle("by name: an ENTITY is a value — given as the argument, or carried");
    var byArgument = session.From("Product")
      .Where(p => p.Member("Category").Equal(QueryMaker.Parameter("cat", typeof(Linq.ICategory))))
      .OrderBy(p => p.Member("Rank"));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == category.Name),
      RanksOf(await session.ExecuteQueryAsync(byArgument, category)));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == secondCategory.Name),
      RanksOf(await session.ExecuteQueryAsync(byArgument, secondCategory)), "the same tree, run again with another entity");
    var carried = session.From("Product")
      .Where(p => p.Member("Category").Equal(QueryMaker.Parameter("cat", secondCategory)))
      .OrderBy(p => p.Member("Rank"));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == secondCategory.Name), RanksOf(await session.ExecuteQueryAsync(carried)));
    StringAssert.Contains(session.LastCommands.Single().Sql, "Category_Id", "the foreign key against a key — no join");

    WriteTestTitle("by name: a LIST of entities is a list of their keys; carried and declared parameters together");
    var two = new[] { category, secondCategory };
    var among = session.From("Product")
      .Where(p => QueryMaker.And(
        p.Member("Category").In(QueryMaker.Parameter("cats", two)),
        p.Member("Price").Greater(QueryMaker.Parameter("price", typeof(decimal)))))
      .OrderBy(p => p.Member("Rank"));
    CollectionAssert.AreEqual(
      Ranks(r => (LinqSeed.CategoryOf(r) == category.Name || LinqSeed.CategoryOf(r) == secondCategory.Name) && LinqSeed.PriceOf(r) > 100m),
      RanksOf(await session.ExecuteQueryAsync(among, 100m)), "the caller passes only what the tree does not carry");
    await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.ExecuteQueryAsync(among));

    WriteTestTitle("by name: one name is one value");
    Assert.ThrowsExactly<InvalidOperationException>(() => QueryTreeScanner.Scan(session.From("Product").Where(p => QueryMaker.And(
      p.Member("Rank").Greater(QueryMaker.Parameter("n", 1)), p.Member("Rank").Less(QueryMaker.Parameter("n", 9))))));

    // ----- BY NAME, IN C#'S OWN SIGNS: `row["Price"] > floor & row["Rank"] < 40` ----------------------------------------
    WriteTestTitle("by name, in signs: comparisons, `&` `|` `!`, arithmetic — and a bare value is a PARAMETER");
    QueryNode InSigns(decimal floor, int below) => session.From("Product")
      .Where(p => p["Price"] > floor & (p["Rank"] < below | !(p["Rank"] * 2 >= 100)) & p["Category"]["Name"].Equal("Tools"))
      .OrderBy(p => p["Rank"]);
    List<int> Expected(decimal floor, int below) =>
      Ranks(r => LinqSeed.PriceOf(r) > floor && (r < below || !(r * 2 >= 100)) && LinqSeed.CategoryOf(r) == "Tools");
    misses = cache.Misses;
    CollectionAssert.AreEqual(Expected(60m, 20), RanksOf(await session.ExecuteQueryAsync(InSigns(60m, 20))));
    var signs = session.LastCommands.Single().Sql;
    CollectionAssert.AreEqual(Expected(90m, 45), RanksOf(await session.ExecuteQueryAsync(InSigns(90m, 45))));
    Assert.AreEqual(misses + 1, cache.Misses, "other values, the same statement: a bare value never reaches the shape");
    Assert.IsFalse(signs.Contains("> 60") || signs.Contains("'Tools'"), "bound, not written in: " + signs);
  }

  // A VARIABLE THAT HOLDS NULL is compared as a null written out: `==` finds the rows that have none, `!=` the rows
  // that have one. The comparison is written when the statement is bound, by the value, so one query serves both.
  [TestMethod]
  public async Task NullVariable_IsComparedAsANullWrittenOut() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var retired = Ranks(r => LinqSeed.RetiredOn(r) != null);
    var day = LinqSeed.RetiredOn(retired[0])!.Value;
    string GetSql() => session.LastCommands.Single().Sql;
    async Task<List<int>> GetRanksAsync(IQueryable<Linq.IProduct> query) =>
      [.. await query.Select(p => p.Rank).ToListAsync()];
    Task<List<int>> GetRetiredOnAsync(DateOnly? when) => GetRanksAsync(products.Where(p => p.RetiredOn == when));

    WriteTestTitle("a value, null, and a value again through one query");
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.RetiredOn(r) == day), await GetRetiredOnAsync(day));
    var valueSql = GetSql();
    Assert.IsFalse(valueSql.Contains("IS NULL"), valueSql);
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.RetiredOn(r) == null), await GetRetiredOnAsync(null));
    StringAssert.Contains(GetSql(), "IS NULL");
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.RetiredOn(r) == day), await GetRetiredOnAsync(day));
    Assert.AreEqual(valueSql, GetSql(), "the text with a value is the one kept");

    WriteTestTitle("not equal: null is the rows that have a value; a value leaves out the rows that have none");
    DateOnly? none = null;
    CollectionAssert.AreEquivalent(retired, await GetRanksAsync(products.Where(p => p.RetiredOn != none)));
    StringAssert.Contains(GetSql(), "IS NOT NULL");
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.RetiredOn(r) != null && LinqSeed.RetiredOn(r) != day),
      await GetRanksAsync(products.Where(p => p.RetiredOn != day)));

    WriteTestTitle("an entity the variable does not hold");
    Linq.ICategory? nothing = null;
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) == null),
      await GetRanksAsync(products.Where(p => p.Secondary == nothing)));
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) != null),
      await GetRanksAsync(products.Where(p => p.Secondary != nothing)));

    WriteTestTitle("by name: a parameter given null");
    var byName = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("RetiredOn").Equal(QueryMaker.Parameter("when", typeof(DateOnly?)))),
      new object?[] { null });
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.RetiredOn(r) == null), RanksOf(byName));

    WriteTestTitle("over a cached entity, in memory: the same rule");
    string? noName = null;
    var categories = session.EntitySet<Linq.ICategory>();
    Assert.AreEqual(LinqSeed.Categories.Length, (await categories.Where(c => c.Name != noName).ToListAsync()).Count);
    Assert.AreEqual(0, (await categories.Where(c => c.Name == noName).ToListAsync()).Count);
  }

  [TestMethod]
  public async Task GroupingKey_IsReadByMember_AndOrdersAndFiltersTheGroups() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var seeded = Ranks(_ => true);

    WriteTestTitle("a key written as new { … } with one member, read by that member");
    var byStatus = await products.GroupBy(p => new { p.Status })
      .Select(g => new { g.Key.Status, Count = g.Count() }).ToListAsync();
    CollectionAssert.AreEquivalent(
      seeded.GroupBy(LinqSeed.StatusOf).Select(g => (g.Key, g.Count())).ToList(),
      byStatus.Select(r => (r.Status, r.Count)).ToList());
    // Over a cached entity the same key is the same statement: the evaluator reads no key by member.
    var byName = await session.EntitySet<Linq.ICategory>().GroupBy(c => new { c.Name })
      .Select(g => new { g.Key.Name, Count = g.Count() }).ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Categories, byName.Select(r => r.Name).ToArray());
    Assert.IsTrue(byName.All(r => r.Count == 1));

    WriteTestTitle("two members, the groups ordered by them and filtered by one before the projection");
    var byBoth = await products.GroupBy(p => new { p.Status, Odd = p.Rank % 2 })
      .Where(g => g.Key.Status != Linq.ProductStatus.Live)
      .OrderBy(g => g.Key.Status).ThenByDescending(g => g.Key.Odd)
      .Select(g => new { g.Key.Status, g.Key.Odd, Count = g.Count(), Top = g.Max(p => p.Rank) }).ToListAsync();
    var expectedBoth = seeded.GroupBy(r => (Status: LinqSeed.StatusOf(r), Odd: r % 2))
      .Where(g => g.Key.Status != Linq.ProductStatus.Live)
      .OrderBy(g => g.Key.Status).ThenByDescending(g => g.Key.Odd)
      .Select(g => (g.Key.Status, g.Key.Odd, g.Count(), g.Max())).ToList();
    CollectionAssert.AreEqual(expectedBoth, byBoth.Select(r => (r.Status, r.Odd, r.Count, r.Top)).ToList());

    // A member of a reference as a part of the key, ordered by: the join is the grouped select's own.
    var byCategory = await products.GroupBy(p => new { Category = p.Category.Name, p.Status })
      .OrderBy(g => g.Key.Category).ThenBy(g => g.Key.Status)
      .Select(g => new { g.Key.Category, g.Key.Status, Count = g.Count() }).ToListAsync();
    var expectedByCategory = seeded.GroupBy(r => (Category: LinqSeed.CategoryOf(r), Status: LinqSeed.StatusOf(r)))
      .OrderBy(g => g.Key.Category, StringComparer.Ordinal).ThenBy(g => g.Key.Status)
      .Select(g => (g.Key.Category, g.Key.Status, g.Count())).ToList();
    CollectionAssert.AreEqual(expectedByCategory,
      byCategory.Select(r => (r.Category, r.Status, r.Count)).ToList());

    WriteTestTitle("one value as the key, in an ordering and in a condition over the groups");
    var descending = await products.GroupBy(p => p.Status).OrderByDescending(g => g.Key)
      .Select(g => g.Key).ToListAsync();
    CollectionAssert.AreEqual(
      new[] { Linq.ProductStatus.Retired, Linq.ProductStatus.Live, Linq.ProductStatus.Draft }, descending.ToArray());
    var notLive = await products.GroupBy(p => p.Status).Where(g => g.Key != Linq.ProductStatus.Live)
      .Select(g => new { g.Key, Top = g.Max(p => p.Rank) }).ToListAsync();
    CollectionAssert.AreEquivalent(
      seeded.GroupBy(LinqSeed.StatusOf).Where(g => g.Key != Linq.ProductStatus.Live)
        .Select(g => (g.Key, g.Max())).ToList(),
      notLive.Select(r => (r.Key, r.Top)).ToList());

    WriteTestTitle("a new { … } key read whole is refused, naming the way to read it");
    var grouped = products.GroupBy(p => new { p.Status, Odd = p.Rank % 2 });
    await AssertRefusedAsync(grouped.Select(g => new { g.Key, Count = g.Count() }), "g.Key.Status");
    await AssertRefusedAsync(grouped.OrderBy(g => g.Key).Select(g => g.Count()), "g.Key.Status");
    await AssertRefusedAsync(
      products.GroupBy(p => new { p.Rank }).Select(g => new { g.Key, Count = g.Count() }), "g.Key.Rank");

    // ----- local functions
    static async Task AssertRefusedAsync<T>(IQueryable<T> query, string advice) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => query.ToListAsync());
      StringAssert.Contains(refused.Message, "'Key' is read whole");
      StringAssert.Contains(refused.Message, advice);
    }
  }

  /// <summary>A declared target for a projection, to prove the object-initializer form alongside the anonymous one.
  /// </summary>
  public class ProductLine {
    public int Rank { get; set; }
    public string Name { get; set; } = "";
  }
}
