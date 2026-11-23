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

// TWO TABLES IN ONE STATEMENT: a reference joined in, a list asked as a sub-query rather than joined, SelectMany as
// the one join allowed to multiply rows, an explicit join putting two rows in scope, query syntax over two sets, and
// GroupJoin with DefaultIfEmpty as the left-outer idiom.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task Join_KeepsEveryRowAndAddsNone() {
    var session = NewSession();

    WriteTestTitle("through a non-nullable reference — an inner join");
    var tools = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Category").Member("Name").Equal(QueryMaker.Parameter("name", typeof(string))))
      .OrderBy(row => row.Member("Rank")),
      "Tools");
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), RanksOf(tools));

    WriteTestTitle("through a NULLABLE one — and the count is what proves it is outer");
    // ORDERING BY A NULLABLE REFERENCE'S COLUMN MUST NOT DROP ROWS. Three quarters of the products have no secondary
    // category, so an inner join here would return 15 instead of 60 — which is the whole difference between the two
    // join kinds, made visible by a number rather than by reading the SQL.
    var bySecondary = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Secondary").Member("Name"))
      .OrderBy(row => row.Member("Rank")));
    Assert.AreEqual(LinqSeed.ProductCount, bySecondary.Count, "every row, including those with no secondary");

    // And filtering ON it narrows to exactly the ones that have it.
    var secondaryPaint = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Secondary").Member("Name").Equal(QueryMaker.Constant("Paint")))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == "Paint"), RanksOf(secondaryPaint));

    WriteTestTitle("two hops, the first optional — outerness is INHERITED");
    // `Substitute` is nullable and `Category` is not. An inner join on the second hop would drop every product with
    // no substitute — 48 of the 60 — so the count is again what says the chain stayed outer.
    var bySubstituteCategory = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Substitute").Member("Category").Member("Name"))
      .OrderBy(row => row.Member("Rank")));
    Assert.AreEqual(LinqSeed.ProductCount, bySubstituteCategory.Count);

    var substitutedTools = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Substitute").Member("Category").Member("Name").Equal(QueryMaker.Constant("Tools")))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(
      Ranks(r => LinqSeed.SubstituteOf(r) is { } s && LinqSeed.CategoryOf(s) == "Tools"),
      RanksOf(substitutedTools));

    WriteTestTitle("a self-join: the table joined to itself under a second alias");
    var cheaperThanSubstitute = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Price").Less(row.Member("Substitute").Member("Price")))
      .OrderBy(row => row.Member("Rank")));
    // Price rises with rank and a substitute is always one rank BACK, so nothing is cheaper than its substitute —
    // and the empty answer is the right one rather than an error.
    Assert.AreEqual(0, cheaperThanSubstitute.Count);
    var dearerThanSubstitute = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Price").Greater(row.Member("Substitute").Member("Price")))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.SubstituteOf(r) != null), RanksOf(dearerThanSubstitute));

    WriteTestTitle("one join per path, however many times the path is named");
    var reused = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Category").Member("Name").NotEqual(QueryMaker.Constant("Timber")))
      .OrderBy(row => row.Member("Category").Member("Name"))
      .OrderBy(row => row.Member("Rank"), descending: true));
    var expected = Ranks(r => LinqSeed.CategoryOf(r) != "Timber")
      .OrderBy(LinqSeed.CategoryOf, StringComparer.Ordinal).ThenByDescending(r => r).ToList();
    CollectionAssert.AreEqual(expected, RanksOf(reused));

    WriteTestTitle("a join under a page, and under an aggregate");
    var page = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Category").Member("Name").Equal(QueryMaker.Constant("Paint")))
      .OrderBy(row => row.Member("Rank"))
      .Skip(QueryMaker.Constant(2)).Take(QueryMaker.Constant(3)));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Paint").Skip(2).Take(3).ToList(), RanksOf(page));

    Assert.AreEqual((long)Ranks(r => LinqSeed.CategoryOf(r) == "Paint").Count,
      await session.ExecuteScalarAsync<long>(session.From("Product")
        .Where(row => row.Member("Category").Member("Name").Equal(QueryMaker.Constant("Paint")))
        .Count()));
  }

  [TestMethod]
  public async Task List_IsAskedNotJoined() {
    var session = NewSession();

    WriteTestTitle("has any at all");
    var withProducts = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Any()));
    Assert.AreEqual(LinqSeed.Categories.Length, withProducts.Count, "every category has products");

    // AND THE OUTER ROW IS UNTOUCHED however many inner rows match. Joining a one-to-many would have returned a
    // category once per product — sixty rows instead of three — which is the whole reason this is a sub-query.
    Assert.AreEqual(LinqSeed.Categories.Length,
      await session.ExecuteScalarAsync<long>(session.From("Category")
        .Where(row => row.Member("Products").Any()).Count()));

    WriteTestTitle("has any that match, and has none");
    var withHighRanks = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Any(p =>
        p.Member("Rank").Greater(QueryMaker.Parameter("rank", typeof(int))))),
      55);
    var expectedNames = Ranks(r => r > 55).Select(LinqSeed.CategoryOf).Distinct().Order().ToList();
    CollectionAssert.AreEqual(expectedNames, NamesOf(withHighRanks));

    var withoutHighRanks = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Any(p =>
        p.Member("Rank").Greater(QueryMaker.Constant(55))).Not()));
    Assert.AreEqual(LinqSeed.Categories.Length - expectedNames.Count, withoutHighRanks.Count);

    WriteTestTitle("a condition that compares the INNER row to the OUTER one");
    // "not the highest-ranked product in its category" — the sub-query's row and the outer row in one comparison,
    // each resolving to its own alias.
    var notTheLatest = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Category").Member("Products").Any(p =>
        p.Member("Rank").Greater(row.Member("Rank"))))
      .OrderBy(row => row.Member("Rank")));
    var expected = Ranks(r => Ranks(s => LinqSeed.CategoryOf(s) == LinqSeed.CategoryOf(r)).Max() > r);
    CollectionAssert.AreEqual(expected, RanksOf(notTheLatest));

    WriteTestTitle("a JOIN inside the sub-query, and a sub-query under a join");
    // The list is reached THROUGH a reference — a join to Category, then a sub-query correlated to that join's alias
    // — and the sub-query's own condition walks a nullable reference, which is a LEFT OUTER join inside it.
    var sharingASecondary = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Category").Member("Products").Any(p =>
        p.Member("Secondary").Member("Name").Equal(QueryMaker.Constant("Paint"))))
      .OrderBy(row => row.Member("Rank")));
    var categoriesWithPaintSecondary = Ranks(r => LinqSeed.SecondaryOf(r) == "Paint")
      .Select(LinqSeed.CategoryOf).ToHashSet();
    CollectionAssert.AreEqual(Ranks(r => categoriesWithPaintSecondary.Contains(LinqSeed.CategoryOf(r))),
      RanksOf(sharingASecondary));
  }

  [TestMethod]
  public async Task List_AnswersAsAValueAndThroughALink() {
    var session = NewSession();

    WriteTestTitle("a list as a VALUE — a scalar sub-query");
    // `Category.Products.Count() > n`, which is the list asked what it comes to rather than whether it has any.
    var perCategory = LinqSeed.Categories.ToDictionary(c => c, c => Ranks(r => LinqSeed.CategoryOf(r) == c).Count);
    var big = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Count().Greater(QueryMaker.Parameter("least", typeof(int)))),
      perCategory.Values.Min());
    CollectionAssert.AreEqual(
      perCategory.Where(p => p.Value > perCategory.Values.Min()).Select(p => p.Key).Order().ToList(),
      NamesOf(big));

    // And an aggregate over a column of the list, not just a count.
    var dearest = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("Products").Max(p => p.Member("Price"))
        .Greater(QueryMaker.Constant(LinqSeed.PriceOf(LinqSeed.ProductCount - 1)))));
    var expectedDearest = LinqSeed.Categories
      .Where(c => Ranks(r => LinqSeed.CategoryOf(r) == c).Max(LinqSeed.PriceOf)
        > LinqSeed.PriceOf(LinqSeed.ProductCount - 1))
      .Order().ToList();
    CollectionAssert.AreEqual(expectedDearest, NamesOf(dearest));

    WriteTestTitle("a MANY-TO-MANY: the sub-query sources from the link and joins out to the target");
    var onSale = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Tags").Any(t => t.Member("Name").Equal(QueryMaker.Parameter("tag", typeof(string)))))
      .OrderBy(row => row.Member("Rank")),
      "Sale");
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Contains("Sale")), RanksOf(onSale));

    var untagged = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Tags").Any().Not())
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Length == 0), RanksOf(untagged));

    // COUNTED THROUGH THE LINK — the same two-legged sub-query, selecting a count instead of `1`.
    var twoTags = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Tags").Count().Equal(QueryMaker.Constant(2)))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Length == 2), RanksOf(twoTags));

    WriteTestTitle("a many-to-many reached through a reference, and a list of the other end");
    var taggedNew = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Substitute").Member("Tags").Any(t =>
        t.Member("Name").Equal(QueryMaker.Constant("New"))))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(
      Ranks(r => LinqSeed.SubstituteOf(r) is { } s && LinqSeed.TagsOf(s).Contains("New")),
      RanksOf(taggedNew));
  }

  [TestMethod]
  public async Task SelectMany_MayMultiplyRows() {
    var session = NewSession();

    WriteTestTitle("a one-to-many, joined rather than made a sub-query");
    // Every product of every category — which is every product, once. The join multiplies, and that is the answer
    // being asked for rather than a fault: `Any` refuses the same join because multiplying would corrupt its count.
    var everyProduct = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products)
      .ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, everyProduct.Count);
    Assert.IsInstanceOfType<Linq.IProduct>(everyProduct[0]);

    // AND WHAT COMES BACK IS ROWS OF THE FAR ENTITY, so everything composed on top reads against it with no special
    // case for having arrived through a list.
    var liveOnly = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products)
      .Where(p => p.Status == Linq.ProductStatus.Live)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live),
      liveOnly.Select(p => p.Rank).ToList());

    // A member path off the far row joins on further, exactly as it would from a table.
    var toolsOnly = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products)
      .Where(p => p.Category.Name == "Tools")
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools").Count, toolsOnly.Count);

    WriteTestTitle("filtering the SOURCE before spreading it");
    var toolsProducts = await session.EntitySet<Linq.ICategory>()
      .Where(c => c.Name == "Tools")
      .SelectMany(c => c.Products)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"),
      toolsProducts.Select(p => p.Rank).ToList());

    WriteTestTitle("a many-to-many spreads through the link");
    // One row per (product, tag) pair — so the count is the number of links, not the number of products.
    var everyTagging = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Tags)
      .ToListAsync();
    Assert.AreEqual(Ranks(_ => true).Sum(r => LinqSeed.TagsOf(r).Length), everyTagging.Count);
    Assert.IsInstanceOfType<Linq.ITag>(everyTagging[0]);

    // A PRODUCT WITH NO TAGS CONTRIBUTES NOTHING, which is what an inner join means and what SelectMany means —
    // keeping it with nulls would be GroupJoin plus DefaultIfEmpty, a different operation.
    var salePairs = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Tags)
      .Where(t => t.Name == LinqSeed.Tags[0])
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Contains(LinqSeed.Tags[0])).Count, salePairs.Count);

    WriteTestTitle("counting what was spread, and paging it");
    Assert.AreEqual(Ranks(_ => true).Sum(r => LinqSeed.TagsOf(r).Length),
      await session.EntitySet<Linq.IProduct>().SelectMany(p => p.Tags).CountAsync());

    var firstFew = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products)
      .OrderBy(p => p.Rank)
      .Take(3)
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, firstFew.Select(p => p.Rank).ToArray());

    WriteTestTitle("the untyped builder says the same thing");
    var untyped = await session.ExecuteQueryAsync(session.From("Category")
      .SelectMany(row => row.Member("Products"))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(_ => true), RanksOf(untyped));

  }

  [TestMethod]
  public async Task ExplicitJoin_PutsTwoRowsInScope() {
    var session = NewSession();

    WriteTestTitle("a projection over BOTH rows");
    // The first operation whose rows outlive it: `(p, c) => new { … }` names columns of two tables in one select
    // list, which nothing before this could express.
    // FILTERED ON THE SOURCE, before the join — a `WHERE` and a `JOIN` coexist in one select whichever order they
    // were written in, which is why a join shares the filter's stage in the chain walk.
    var lines = (await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 3)
      .Join(session.EntitySet<Linq.ICategory>(),
        p => p.Category.Id, c => c.Id,
        (p, c) => new { p.Rank, Category = c.Name, p.Price })
      .ToListAsync())
      .OrderBy(x => x.Rank).ToList();
    Assert.AreEqual(3, lines.Count);
    for (var i = 0; i < lines.Count; i++) {
      var rank = i + 1;
      Assert.AreEqual(rank, lines[i].Rank);
      Assert.AreEqual(LinqSeed.CategoryOf(rank), lines[i].Category);
      Assert.AreEqual(LinqSeed.PriceOf(rank), lines[i].Price);
    }

    // ORDERING BEFORE THE JOIN puts the ordering inside a derived table, where it decides nothing — refused rather
    // than producing SQL two servers reject. (Ordering AFTER a joined projection is the composable case and works;
    // see the projection test.)
    var orderedFirst = await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>()
        .OrderBy(p => p.Rank)
        .Join(session.EntitySet<Linq.ICategory>(), p => p.Category.Id, c => c.Id, (p, c) => p)
        .ToListAsync());
    StringAssert.Contains(orderedFirst.Message, "derived table");

    WriteTestTitle("selecting one side whole");
    // `(p, c) => c` is not a projection — it is a change of which table the rows come from, which the scope says
    // with an alias rather than with a select list. Sixty products, so sixty category rows, one per pair.
    var categoriesPerProduct = await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>(), p => p.Category.Id, c => c.Id, (p, c) => c)
      .ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, categoriesPerProduct.Count);
    Assert.IsInstanceOfType<Linq.ICategory>(categoriesPerProduct[0]);

    var productsOfTools = await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>(), p => p.Category.Id, c => c.Id, (p, c) => p)
      .Where(p => p.Category.Name == "Tools")
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"),
      productsOfTools.Select(p => p.Rank).ToList());

    WriteTestTitle("a join on something that is not a foreign key");
    // The condition is whatever the caller wrote, which is the whole point of an explicit join — here every product
    // paired with the product one rank below it.
    var successors = (await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 2)
      .Join(session.EntitySet<Linq.IProduct>(), p => p.Rank, q => q.Rank, (p, q) => new { p.Rank, Other = q.Name })
      .ToListAsync())
      .OrderBy(x => x.Rank).ToList();
    CollectionAssert.AreEqual(new[] { "Product 01", "Product 02" }, successors.Select(x => x.Other).ToArray());

    WriteTestTitle("counting a join, and the untyped builder saying the same thing");
    Assert.AreEqual(LinqSeed.ProductCount, await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>(), p => p.Category.Id, c => c.Id, (p, c) => p)
      .CountAsync());

    var model = session.GetLinqModel();
    var untyped = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Rank").LessOrEqual(QueryMaker.Constant(3)))
      .Join(QueryMaker.From(model.FindEntity("Category")!),
        (p, c) => p.Member("Category").Member("Id").Equal(c.Member("Id")),
        (p, c) => [p])
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, RanksOf(untyped).ToArray());

    WriteTestTitle("a FILTERED inner side — a derived table after the JOIN");
    // The filter is NOT folded into the `ON`. The two say the same thing for an inner join and different things for
    // an outer one, so the inner side is built as a query of its own and joined to as a table.
    var tools = await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>().Where(c => c.Name == "Tools"),
        p => p.Category.Id, c => c.Id, (p, c) => p)
      .OrderBy(p => p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), tools.Select(p => p.Rank).ToList());

    WriteTestTitle("a PROJECTED inner side — its columns named by the select list that made them");
    // `c.Label` is a column of no table, so both the `ON` and the outer select list name it bare rather than through
    // the model — the same rule composing over a projection already needed.
    var labelled = (await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 3)
      .Join(session.EntitySet<Linq.ICategory>().Select(c => new { c.Id, Label = c.Name }),
        p => p.Category.Id, c => c.Id, (p, c) => new { p.Rank, c.Label })
      .ToListAsync())
      .OrderBy(x => x.Rank).ToList();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, labelled.Select(x => x.Rank).ToArray());
    CollectionAssert.AreEqual(Enumerable.Range(1, 3).Select(LinqSeed.CategoryOf).ToArray(),
      labelled.Select(x => x.Label).ToArray());

    WriteTestTitle("a PAGED inner side");
    var firstCategory = await session.EntitySet<Linq.IProduct>()
      .Join(session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).Take(1),
        p => p.Category.Id, c => c.Id, (p, c) => p)
      .CountAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Paint").Count, firstCategory);

    WriteTestTitle("an ORDERED inner side with nothing to cut is still refused");
    // A sub-select promises no order, so ordering one and taking no page of it decides nothing — and two of the five
    // servers reject the SQL outright rather than ignoring it.
    var ordered = await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>()
        .Join(session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name),
          p => p.Category.Id, c => c.Id, (p, c) => p)
        .ToListAsync());
    StringAssert.Contains(ordered.Message, "decides nothing");
  }

  [TestMethod]
  public async Task QuerySyntax_ResolvesTransparentIdentifiers() {
    var session = NewSession();
    // A SET NAMED INSIDE A QUERY IS A LOCAL: `session.EntitySet<T>()` is a call on the session, and a query cannot be
    // started inside a lambda — a captured query carries its own expression, which is shape.
    var categories = session.EntitySet<Linq.ICategory>();
    var tags = session.EntitySet<Linq.ITag>();

    WriteTestTitle("two `from` clauses and a `where` — what C# compiles to a bundle");
    // The compiler does not hand the `where` two parameters: it projects `new { p, c }` and gives it ONE, whose
    // members are the two rows. So `x.p.Rank` is not a column of anything — it is the compiler saying "the first
    // row" — and resolving that is what makes this one statement over two tables.
    var pairs = (from p in session.EntitySet<Linq.IProduct>()
                 from c in categories
                 where p.Category.Id == c.Id && p.Rank <= 3
                 select new { p.Rank, Category = c.Name })
                .ToListAsync();
    var lines = (await pairs).OrderBy(x => x.Rank).ToList();
    Assert.AreEqual(3, lines.Count);
    for (var i = 0; i < lines.Count; i++)
      Assert.AreEqual(LinqSeed.CategoryOf(i + 1), lines[i].Category);

    WriteTestTitle("the same thing in method syntax");
    var byMethod = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => categories, (p, c) => new { p, c })
      .Where(x => x.p.Category.Id == x.c.Id && x.p.Rank <= 3)
      .Select(x => x.c.Name)
      .ToListAsync();
    CollectionAssert.AreEquivalent(
      Ranks(r => r <= 3).Select(LinqSeed.CategoryOf).ToArray(), byMethod.ToArray());

    WriteTestTitle("a bundle survives an ordering, and one side can be selected whole");
    var ordered = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => categories, (p, c) => new { p, c })
      .Where(x => x.p.Category.Id == x.c.Id && x.c.Name == "Tools")
      .OrderBy(x => x.p.Rank)
      .Select(x => x.p.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), ordered.ToList());

    var whole = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => categories, (p, c) => p)
      .Where(p => p.Rank <= 2)
      .ToListAsync();
    // Every product against every category, then narrowed — three categories, so each product appears three times.
    Assert.AreEqual(2 * LinqSeed.Categories.Length, whole.Count);
    Assert.IsInstanceOfType<Linq.IProduct>(whole[0]);

    WriteTestTitle("spreading a LIST with a result selector, which is the same machinery");
    var tagged = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Tags, (p, t) => new { p.Rank, Tag = t.Name })
      .ToListAsync();
    Assert.AreEqual(Ranks(_ => true).Sum(r => LinqSeed.TagsOf(r).Length), tagged.Count);
    CollectionAssert.AreEquivalent(LinqSeed.TagsOf(4), tagged.Where(x => x.Rank == 4).Select(x => x.Tag).ToArray());

    WriteTestTitle("a COMPOSED second `from` — the cross join's far side is a query of its own");
    // The filter belongs to the second `from`, not to the `where` beside it. Written this way it has to survive as
    // the far side's OWN condition, which means the far side is a derived table rather than a table name.
    var toolsOnly = await (from p in session.EntitySet<Linq.IProduct>()
                           from c in categories.Where(c => c.Name == "Tools")
                           where p.Category.Id == c.Id
                           select p.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.CategoryOf(r) == "Tools"), toolsOnly.ToList());

    // AND A ROW'S LIST IS STILL A LIST, which is the discrimination this case turns on: `p.Tags` is spread, and only
    // an expression that is a QUERY becomes a join.
    var topTags = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 4)
      .SelectMany(p => p.Tags, (p, t) => new { p.Rank, Tag = t.Name })
      .ToListAsync();
    Assert.AreEqual(Ranks(r => r <= 4).Sum(r => LinqSeed.TagsOf(r).Length), topTags.Count);

    WriteTestTitle("a bundle inside a bundle — three sets and a clause after them");
    // A `select` that immediately follows the last `from` is FUSED into it by the compiler; a clause in between —
    // this `where` — makes C# nest one transparent identifier inside another, so `x.p.Rank` is two hops through
    // names and one into the model.
    var three = await (from p in session.EntitySet<Linq.IProduct>()
                       from c in categories
                       from t in tags
                       where p.Category.Id == c.Id && t.Name == LinqSeed.Tags[0] && p.Rank <= 4
                       select new { p.Rank, Category = c.Name }).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r <= 4), three.Select(x => x.Rank).Order().ToList());
    foreach (var row in three)
      Assert.AreEqual(LinqSeed.CategoryOf(row.Rank), row.Category);
  }

  // A KEY WRITTEN AS AN ENTITY: a reference is its owner's foreign-key columns and a row its primary key, on either
  // side of the join and inside a composite key, so the referenced table is joined once.
  [TestMethod]
  public async Task Join_OnAnEntity_IsAJoinOnItsKeyColumns() {
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var categories = session.EntitySet<Linq.ICategory>();
    string GetSql() => session.LastCommands.Single().Sql;
    int CountJoins() => GetSql().Split("JOIN").Length - 1;

    WriteTestTitle("the joined side's reference against the row");
    var byInner = await (from c in categories
                         join p in products on c equals p.Category
                         select new { p.Rank, Category = c.Name }).ToListAsync();
    Assert.AreEqual(LinqSeed.ProductCount, byInner.Count);
    foreach (var row in byInner)
      Assert.AreEqual(LinqSeed.CategoryOf(row.Rank), row.Category);
    Assert.AreEqual(1, CountJoins(), GetSql());
    StringAssert.Contains(GetSql(), "Category_Id");

    WriteTestTitle("the outer side's reference: one join, on the foreign key");
    var byOuter = await (from p in products
                         join c in categories on p.Secondary equals c
                         select new { p.Rank, Second = c.Name }).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) != null), byOuter.Select(x => x.Rank).ToList());
    foreach (var row in byOuter)
      Assert.AreEqual(LinqSeed.SecondaryOf(row.Rank), row.Second);
    Assert.AreEqual(1, CountJoins(), GetSql());
    StringAssert.Contains(GetSql(), "Secondary_Id");

    WriteTestTitle("a reference against a row of its own table");
    var substituted = await (from p in products
                             join s in products on p.Substitute equals s
                             select new { p.Rank, Substitute = s.Rank }).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SubstituteOf(r) != null),
      substituted.Select(x => x.Rank).ToList());
    foreach (var row in substituted)
      Assert.AreEqual(LinqSeed.SubstituteOf(row.Rank), row.Substitute);
    Assert.AreEqual(1, CountJoins(), GetSql());

    WriteTestTitle("a reference on both sides");
    var paired = await (from p in products
                        join q in products on p.Category equals q.Secondary
                        where p.Rank <= 6
                        select new { p.Rank, Other = q.Rank }).ToListAsync();
    Assert.AreEqual(2 * Ranks(r => LinqSeed.SecondaryOf(r) != null).Count, paired.Count,
      "two of the first six products in each category");
    foreach (var row in paired)
      Assert.AreEqual(LinqSeed.CategoryOf(row.Rank), LinqSeed.SecondaryOf(row.Other));
    Assert.AreEqual(1, CountJoins(), GetSql());

    WriteTestTitle("inside a composite key");
    var composite = await (from c in categories
                           join p in products on new { Category = c, Rank = 7 } equals new { p.Category, p.Rank }
                           select new { c.Name, p.Rank }).ToListAsync();
    Assert.AreEqual(1, composite.Count);
    Assert.AreEqual(LinqSeed.CategoryOf(7), composite[0].Name);

    WriteTestTitle("grouped: each category with the count of the products in it");
    var counted = await (from c in categories
                         join p in products on c equals p.Category into matches
                         select new { c.Name, Count = matches.Count() }).ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, counted.Count);
    foreach (var row in counted)
      Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == row.Name).Count, row.Count);

    WriteTestTitle("left outer, over a filtered inner side: every category, one of them matched");
    var outer = await (from c in categories
                       join p in products.Where(p => p.Rank == 4) on c equals p.Secondary into matches
                       from p in matches.DefaultIfEmpty()
                       select new { c.Name, Rank = (int?)p.Rank }).ToListAsync();
    Assert.AreEqual(LinqSeed.Categories.Length, outer.Count);
    Assert.AreEqual(LinqSeed.SecondaryOf(4), outer.Single(x => x.Rank != null).Name);
  }

  [TestMethod]
  public async Task GroupJoin_WithDefaultIfEmptyIsLeftOuter() {
    var session = NewSession();

    WriteTestTitle("every product, whether or not it has a SECOND category");
    // `Secondary` is set for a quarter of the products, so an INNER join here would drop three quarters — which is
    // the whole difference a left outer join makes, and it shows in the count.
    var joined = (from p in session.EntitySet<Linq.IProduct>()
                  join c in session.EntitySet<Linq.ICategory>() on p.Secondary.Id equals c.Id into g
                  from c in g.DefaultIfEmpty()
                  select new { p.Rank, Second = c.Name })
                 .ToListAsync();
    var rows = (await joined).OrderBy(x => x.Rank).ToList();
    Assert.AreEqual(LinqSeed.ProductCount, rows.Count, "an outer join keeps the rows that matched nothing");
    foreach (var row in rows)
      Assert.AreEqual(LinqSeed.SecondaryOf(row.Rank), row.Second);

    // AND THE UNMATCHED SIDE IS NULL rather than absent, which is the point of `DefaultIfEmpty`.
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == null).Count, rows.Count(x => x.Second == null));

    WriteTestTitle("the inner join written the same way, for contrast");
    var matched = (from p in session.EntitySet<Linq.IProduct>()
                   join c in session.EntitySet<Linq.ICategory>() on p.Secondary.Id equals c.Id
                   select new { p.Rank, Second = c.Name })
                  .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) != null).Count, (await matched).Count,
      "an inner join drops the rows that matched nothing");

    WriteTestTitle("filtering after the outer join");
    var unmatched = await (from p in session.EntitySet<Linq.IProduct>()
                           join c in session.EntitySet<Linq.ICategory>() on p.Secondary.Id equals c.Id into g
                           from c in g.DefaultIfEmpty()
                           where c.Name == null
                           select p.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) == null), unmatched.ToList());

    WriteTestTitle("a FILTERED inner side, outer-joined");
    // The group holds only the categories that passed the filter, so a product whose second category is not Tools
    // has an EMPTY group and `DefaultIfEmpty` gives it null — the rows are all still here, which is the difference
    // between filtering the inner side and filtering after the join.
    var toolsOnly = (await (from p in session.EntitySet<Linq.IProduct>()
                            join c in session.EntitySet<Linq.ICategory>().Where(c => c.Name == "Tools")
                              on p.Secondary.Id equals c.Id into g
                            from c in g.DefaultIfEmpty()
                            select new { p.Rank, Second = c.Name })
                           .ToListAsync())
                    .OrderBy(x => x.Rank).ToList();
    Assert.AreEqual(LinqSeed.ProductCount, toolsOnly.Count);
    foreach (var row in toolsOnly)
      Assert.AreEqual(LinqSeed.SecondaryOf(row.Rank) == "Tools" ? "Tools" : null, row.Second);

    WriteTestTitle("a PAGED inner side, which cannot be folded into the `ON` at all");
    // One category, alphabetically first. A filter could have been written into the join condition instead; a page
    // could not, which is why the inner side is built as a query of its own whatever was composed onto it.
    var firstOnly = await (from p in session.EntitySet<Linq.IProduct>()
                           join c in session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).Take(1)
                             on p.Secondary.Id equals c.Id into g
                           from c in g.DefaultIfEmpty()
                           where c.Name != null
                           select p.Rank).ToListAsync();
    CollectionAssert.AreEquivalent(Ranks(r => LinqSeed.SecondaryOf(r) == "Paint"), firstOnly.ToList());

    WriteTestTitle("three tables, two of them outer-joined");
    // The third `from` nests one transparent identifier inside another, which is what the bundle work made
    // resolvable — `x.p.Rank` is two hops through names and one into the model.
    var deep = await (from p in session.EntitySet<Linq.IProduct>()
                      join c in session.EntitySet<Linq.ICategory>() on p.Secondary.Id equals c.Id into g
                      from c in g.DefaultIfEmpty()
                      join s in session.EntitySet<Linq.IProduct>() on p.Substitute.Id equals s.Id into gs
                      from s in gs.DefaultIfEmpty()
                      where p.Rank <= 10
                      select new { p.Rank, Second = c.Name, Substitute = s.Name }).ToListAsync();
    Assert.AreEqual(10, deep.Count);
    foreach (var row in deep) {
      Assert.AreEqual(LinqSeed.SecondaryOf(row.Rank), row.Second);
      Assert.AreEqual(LinqSeed.SubstituteOf(row.Rank) is { } sub ? $"Product {sub:D2}" : null, row.Substitute);
    }
  }
}
