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

// LISTS AND THE GRAPH: a page of a list spread per row, a queryable property as a list inside a query, and Include
// loading the graph with the query.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task PageOfAList_IsSpreadPerRow() {
    var session = NewSession();
    // Rank decides both the category (rank % 3: Tools, Paint, Timber) and the price, so each category's dearest
    // products are its highest ranks.
    List<int> RanksIn(string category) => Ranks(r => LinqSeed.CategoryOf(r) == category);

    WriteTestTitle("the dearest two products of EACH category — a page per row, not a page of the result");
    var dearest = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products.OrderByDescending(p => p.Price).Take(2), (c, p) => new { c, p })
      .OrderBy(x => x.c.Name)
      .Select(x => new { Category = x.c.Name, x.p.Rank })
      .ToListAsync();
    Assert.AreEqual(6, dearest.Count, "two per category, where one LIMIT would have answered two in all");
    foreach (var category in LinqSeed.Categories)
      // IN THE PAGE'S OWN ORDER within each category: the row number trails whatever the caller ordered by.
      CollectionAssert.AreEqual(RanksIn(category).OrderByDescending(r => r).Take(2).ToList(),
        dearest.Where(x => x.Category == category).Select(x => x.Rank).ToList(), category);

    WriteTestTitle("filtered before it is numbered, and a Skip with the Take");
    var second = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products.Where(p => p.Rank > 10).OrderBy(p => p.Rank).Skip(1).Take(2))
      .ToListAsync();
    Assert.IsInstanceOfType<Linq.IProduct>(second[0]);
    foreach (var category in LinqSeed.Categories)
      CollectionAssert.AreEqual(RanksIn(category).Where(r => r > 10).Skip(1).Take(2).ToList(),
        second.Where(p => LinqSeed.CategoryOf(p.Rank) == category).Select(p => p.Rank).ToList(), category);

    WriteTestTitle("the same page over a QUERY tied to the row by an entity comparison");
    var products = session.EntitySet<Linq.IProduct>();
    var cheapest = await session.EntitySet<Linq.ICategory>()
      .SelectMany(c => products.Where(p => p.Category == c).OrderBy(p => p.Price).Take(1))
      .ToListAsync();
    CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, cheapest.Select(p => p.Rank).ToArray());

    WriteTestTitle("a many-to-many: numbered within the LINK's owner, answered with the target");
    var firstTag = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 6)
      .SelectMany(p => p.Tags.OrderBy(t => t.Name).Take(1), (p, t) => new { p.Rank, Tag = t.Name })
      .ToListAsync();
    var expectedTags = Enumerable.Range(1, 6).Where(r => LinqSeed.TagsOf(r).Length > 0)
      .Select(r => $"{r}:{LinqSeed.TagsOf(r).Order().First()}").ToList();
    CollectionAssert.AreEquivalent(expectedTags, firstTag.Select(x => $"{x.Rank}:{x.Tag}").ToList());

    WriteTestTitle("DefaultIfEmpty keeps the row whose list is empty");
    var withEmpty = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 6)
      .SelectMany(p => p.Tags.OrderBy(t => t.Name).Take(1).DefaultIfEmpty(), (p, t) => new { p.Rank, Tag = t.Name })
      .ToListAsync();
    Assert.AreEqual(6, withEmpty.Count);
    Assert.IsNull(withEmpty.Single(x => x.Rank == 6).Tag, "rank 6 has no tags, and is still there");

    WriteTestTitle("the untyped builder says the same thing");
    var untyped = await session.ExecuteQueryAsync(session.From("Category")
      .SelectMany(c => c.Member("Products").OrderBy(p => p.Member("Price"), descending: true).Take(QueryMaker.Constant(2))));
    CollectionAssert.AreEquivalent(LinqSeed.Categories.SelectMany(c => RanksIn(c).OrderByDescending(r => r).Take(2)).ToList(),
      RanksOf(untyped));

    WriteTestTitle("an ordering with no page decides nothing, and says so");
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.EntitySet<Linq.ICategory>()
      .SelectMany(c => c.Products.OrderBy(p => p.Rank))
      .ToListAsync());
  }

  [TestMethod]
  public async Task QueryableProperty_IsAListInsideAQuery() {
    var session = NewSession();
    // `ProductQuery` and `TagQuery` are the relations `Products` and `Tags` are, declared as `IQueryable<T>`. Inside
    // a query each has to be the list it mirrors, so every answer here is asserted against the list's own.
    var products = session.EntitySet<Linq.IProduct>();
    var categories = session.EntitySet<Linq.ICategory>();

    WriteTestTitle("asked whether it has any, filtered, counted — a correlated sub-query, as a list is");
    Assert.AreEqual(await categories.Where(c => c.Products.Any(p => p.Price > 100m)).CountAsync(),
      await categories.Where(c => c.ProductQuery.Any(p => p.Price > 100m)).CountAsync());
    Assert.AreEqual(LinqSeed.Categories.Length,
      await categories.Where(c => c.ProductQuery.Where(p => p.Status == Linq.ProductStatus.Live).Count() >= 0).CountAsync());
    var big = await categories.Where(c => c.ProductQuery.Count() > 19).OrderBy(c => c.Name).ToListAsync();
    CollectionAssert.AreEqual(LinqSeed.Categories.Order().ToList(), big.Select(c => c.Name).ToList(),
      "and the rows read back whole, with a member that owns no slot sitting among their members");

    WriteTestTitle("a many-to-many, through its link");
    Assert.AreEqual(Ranks(r => LinqSeed.TagsOf(r).Contains(LinqSeed.Tags[0])).Count,
      await products.Where(p => p.TagQuery.Any(t => t.Name == LinqSeed.Tags[0])).CountAsync());

    WriteTestTitle("spread — and a PAGE of it spread per row");
    Assert.AreEqual(LinqSeed.ProductCount, await categories.SelectMany(c => c.ProductQuery).CountAsync());
    var dearest = await categories
      .SelectMany(c => c.ProductQuery.OrderByDescending(p => p.Price).Take(2))
      .ToListAsync();
    CollectionAssert.AreEquivalent(
      LinqSeed.Categories.SelectMany(c => Ranks(r => LinqSeed.CategoryOf(r) == c).OrderByDescending(r => r).Take(2)).ToList(),
      dearest.Select(p => p.Rank).ToList());

    WriteTestTitle("the untyped builder says the same thing");
    var untyped = await session.ExecuteQueryAsync(session.From("Category")
      .Where(row => row.Member("ProductQuery").Any(p => p.Member("Price").Greater(QueryMaker.Constant(100m)))));
    Assert.AreEqual(LinqSeed.Categories.Length, untyped.Count);

    // ----- read off an ENTITY: a query over that owner's rows, composed and run like any other -------------------------
    // One owner at a time here: the many-owners form has its own section below.
    session.Options = EntitySessionOptions.DisableSmartLoad;
    WriteTestTitle("read off an entity: the owner's rows, unordered — a query is ordered where it is written");
    var category = big[0];
    var own = Ranks(r => LinqSeed.CategoryOf(r) == category.Name);
    var ownProducts = await category.ProductQuery.ToListAsync();
    CollectionAssert.AreEquivalent(own, ownProducts.Select(p => p.Rank).ToList());
    var sql = session.LastCommands.Single().Sql;
    Assert.IsFalse(sql.Contains("ORDER BY"), "no ordering the caller did not write: " + sql);
    Assert.IsFalse(sql.Contains("\"Category\" ") || sql.Contains("[Category] ") || sql.Contains("`Category` "),
      "the owner is a parameter, not a table: " + sql);
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => category.ProductQuery.Take(3).ToListAsync());

    WriteTestTitle("filtered, ordered and paged by the caller");
    var top = await category.ProductQuery.Where(p => p.Price > 100m).OrderByDescending(p => p.Rank).Take(3).ToListAsync();
    CollectionAssert.AreEqual(own.Where(r => LinqSeed.PriceOf(r) > 100m).OrderByDescending(r => r).Take(3).ToList(),
      top.Select(p => p.Rank).ToList());
    Assert.AreSame(category, top[0].Category, "and the rows point back at the tracked owner");

    WriteTestTitle("counted, and asked for one");
    Assert.AreEqual(own.Count, await category.ProductQuery.CountAsync());
    Assert.AreEqual(own.Max(), await category.ProductQuery.MaxAsync(p => p.Rank));
    Assert.AreEqual(own.Min(), (await category.ProductQuery.OrderBy(p => p.Rank).FirstAsync()).Rank);

    WriteTestTitle("ANOTHER owner runs the same statement with its own key");
    var otherCategory = big[1];
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == otherCategory.Name).Count, await otherCategory.ProductQuery.CountAsync());

    WriteTestTitle("a many-to-many: the targets the link ties to this owner");
    var product = await products.Where(p => p.Rank == 4).SingleAsync();
    var tags = await product.TagQuery.ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.TagsOf(4), tags.Select(t => t.Name).ToList());
    Assert.AreEqual(LinqSeed.TagsOf(4).Count(t => t != LinqSeed.TagsOf(4)[0]),
      await product.TagQuery.Where(t => t.Name != LinqSeed.TagsOf(4)[0]).CountAsync());

    // ----- SMART: the same chain over the same property of every tracked sibling, in ONE statement ---------------------
    WriteTestTitle("SMART, paged: the dearest two of EVERY tracked category, numbered per owner in one statement");
    var smart = NewSession();
    var owners = await smart.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).ToListAsync();
    List<int> DearestOf(Linq.ICategory c) =>
      Ranks(r => LinqSeed.CategoryOf(r) == c.Name).OrderByDescending(r => r).Take(2).ToList();
    var dearestTwo = await owners[0].ProductQuery.OrderByDescending(p => p.Price).Take(2).ToListAsync();
    var paged = smart.LastCommands.Single();
    StringAssert.Contains(paged.Sql, "ROW_NUMBER()");
    CollectionAssert.AreEqual(DearestOf(owners[0]), dearestTwo.Select(p => p.Rank).ToList());
    foreach (var sibling in owners.Skip(1))
      CollectionAssert.AreEqual(DearestOf(sibling),
        (await sibling.ProductQuery.OrderByDescending(p => p.Price).Take(2).ToListAsync()).Select(p => p.Rank).ToList());
    Assert.AreSame(paged, smart.LastCommands.Single(), "the siblings were answered from what the first ask brought back");

    WriteTestTitle("SMART, unpaged: filtered by a captured value — and ANOTHER value is another answer");
    var floor = 100m;
    foreach (var owner in owners)
      CollectionAssert.AreEquivalent(
        Ranks(r => LinqSeed.CategoryOf(r) == owner.Name && LinqSeed.PriceOf(r) > floor),
        (await owner.ProductQuery.Where(p => p.Price > floor).ToListAsync()).Select(p => p.Rank).ToList());
    var unpaged = smart.LastCommands.Single();
    Assert.AreNotSame(paged, unpaged);
    floor = 140m;
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == owners[1].Name && LinqSeed.PriceOf(r) > floor).Count,
      (await owners[1].ProductQuery.Where(p => p.Price > floor).ToListAsync()).Count);
    Assert.AreNotSame(unpaged, smart.LastCommands.Single(), "the same shape with another value ran again");

    WriteTestTitle("SMART, many-to-many: each product's first tag by name — one of them has none");
    var six = await smart.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 6).OrderBy(p => p.Rank).ToListAsync();
    var firstTag = await six[3].TagQuery.OrderBy(t => t.Name).Take(1).ToListAsync();
    var linked = smart.LastCommands.Single();
    // The owners are said twice: of the owners' rows, and inside the numbered rows of the link, so that the server
    // numbers their links and not the whole table's.
    Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(linked.Sql, "EXISTS").Count, linked.Sql);
    CollectionAssert.AreEqual(LinqSeed.TagsOf(4).Order().Take(1).ToList(), firstTag.Select(t => t.Name).ToList());
    foreach (var item in six)
      CollectionAssert.AreEqual(LinqSeed.TagsOf(item.Rank).Order().Take(1).ToList(),
        (await item.TagQuery.OrderBy(t => t.Name).Take(1).ToListAsync()).Select(t => t.Name).ToList());
    Assert.AreSame(linked, smart.LastCommands.Single());

    WriteTestTitle("SMART, many-to-many UNPAGED: read through the link, where whose row it is can be seen");
    var notSale = LinqSeed.Tags[0];
    foreach (var item in six)
      CollectionAssert.AreEqual(LinqSeed.TagsOf(item.Rank).Where(t => t != notSale).OrderDescending().ToList(),
        (await item.TagQuery.Where(t => t.Name != notSale).OrderByDescending(t => t.Name).ToListAsync()).Select(t => t.Name).ToList());
    var throughLink = smart.LastCommands.Single();
    Assert.AreNotSame(linked, throughLink);
    StringAssert.Contains(throughLink.Sql, "ProductTag");
    Assert.IsFalse(throughLink.Sql.Contains("\"Product\" ") || throughLink.Sql.Contains("[Product] ") || throughLink.Sql.Contains("`Product` "),
      "no owner table: the link says whose row it is — " + throughLink.Sql);

    WriteTestTitle("SMART, one row: First, Last and FirstOrDefault are a page of one — and answer every sibling");
    var fresh = NewSession();
    var cats = await fresh.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).ToListAsync();
    List<int> RanksIn(Linq.ICategory c) => Ranks(r => LinqSeed.CategoryOf(r) == c.Name);
    var dearestOne = await cats[0].ProductQuery.OrderByDescending(p => p.Price).FirstAsync();
    var oneRow = fresh.LastCommands.Single();
    StringAssert.Contains(oneRow.Sql, "ROW_NUMBER()", "a page of one per owner");
    Assert.AreEqual(RanksIn(cats[0]).Max(), dearestOne.Rank);
    foreach (var c in cats.Skip(1))
      Assert.AreEqual(RanksIn(c).Max(), (await c.ProductQuery.OrderByDescending(p => p.Price).FirstAsync()).Rank);
    Assert.AreSame(oneRow, fresh.LastCommands.Single(), "the siblings were answered from the one statement");
    // `Last` is the ordering reversed; unordered, the key orders it; none is a default, not a throw.
    Assert.AreEqual(RanksIn(cats[1]).Min(), (await cats[1].ProductQuery.OrderByDescending(p => p.Price).LastAsync()).Rank);
    Assert.IsNull(await cats[2].ProductQuery.Where(p => p.Price > 100000m).FirstOrDefaultAsync(), "none is a default, not a throw");
    Assert.IsNotNull(await cats[2].ProductQuery.FirstOrDefaultAsync());

    WriteTestTitle("SMART, a value: Count, Sum, Max and Any are GROUPED by owner — one value each, kept per owner");
    var counted = await cats[0].ProductQuery.CountAsync();
    var grouped = fresh.LastCommands.Single();
    StringAssert.Contains(grouped.Sql, "GROUP BY", "one group per owner");
    Assert.AreEqual(RanksIn(cats[0]).Count, counted);
    foreach (var c in cats.Skip(1))
      Assert.AreEqual(RanksIn(c).Count, await c.ProductQuery.CountAsync());
    Assert.AreSame(grouped, fresh.LastCommands.Single(), "every sibling's count came from the one statement");
    // A filter narrows the rows before they are grouped; the value is the caller's type; over no rows it is the
    // default, as the one-owner statement answers.
    Assert.AreEqual(RanksIn(cats[1]).Count(r => LinqSeed.PriceOf(r) > 100m), await cats[1].ProductQuery.CountAsync(p => p.Price > 100m));
    Assert.AreEqual(RanksIn(cats[1]).Sum(LinqSeed.PriceOf), await cats[1].ProductQuery.SumAsync(p => p.Price));
    Assert.AreEqual(RanksIn(cats[2]).Max(), await cats[2].ProductQuery.MaxAsync(p => p.Rank));
    Assert.IsTrue(await cats[2].ProductQuery.AnyAsync());
    Assert.IsFalse(await cats[2].ProductQuery.AnyAsync(p => p.Price > 100000m));
    Assert.AreEqual(0, await cats[2].ProductQuery.CountAsync(p => p.Price > 100000m));
    Assert.AreEqual(0m, await cats[2].ProductQuery.Where(p => p.Price > 100000m).SumAsync(p => p.Price));
    // And through a link: the group is of link rows, the value read off the far side.
    Assert.AreEqual(LinqSeed.TagsOf(4).Length, await six[3].TagQuery.CountAsync());
    var linkGrouped = smart.LastCommands.Single();
    StringAssert.Contains(linkGrouped.Sql, "GROUP BY");
    foreach (var item in six)
      Assert.AreEqual(LinqSeed.TagsOf(item.Rank).Where(t => t != notSale).Count(),
        await item.TagQuery.CountAsync(t => t.Name != notSale));
    Assert.AreEqual(LinqSeed.TagsOf(1).Max(), await six[0].TagQuery.MaxAsync(t => t.Name));

    WriteTestTitle("SMART, a projection: the caller's slots with the owner's key beside them — rows kept per owner");
    var pairs = await cats[0].ProductQuery.OrderBy(p => p.Rank).Select(p => new { p.Rank, p.Price }).ToListAsync();
    var projected = fresh.LastCommands.Single();
    StringAssert.Contains(projected.Sql, "OwnerKey");
    CollectionAssert.AreEqual(RanksIn(cats[0]), pairs.Select(x => x.Rank).ToList());
    Assert.IsTrue(pairs.All(x => x.Price == LinqSeed.PriceOf(x.Rank)));
    foreach (var c in cats.Skip(1))
      CollectionAssert.AreEqual(RanksIn(c),
        (await c.ProductQuery.OrderBy(p => p.Rank).Select(p => new { p.Rank, p.Price }).ToListAsync()).Select(x => x.Rank).ToList());
    Assert.AreSame(projected, fresh.LastCommands.Single(), "every sibling's rows came from the one statement");
    // One value is a list of values; a whole row among the slots is the tracked entity, as anywhere.
    CollectionAssert.AreEquivalent(RanksIn(cats[1]).Select(LinqSeed.PriceOf).ToList(),
      (await cats[1].ProductQuery.Select(p => p.Price).ToListAsync()).ToList());
    var withRow = await cats[2].ProductQuery.Where(p => p.Price > 100m).Select(p => new { Product = p, p.Rank }).ToListAsync();
    Assert.AreEqual(RanksIn(cats[2]).Count(r => LinqSeed.PriceOf(r) > 100m), withRow.Count);
    Assert.IsTrue(withRow.All(x => x.Product.Rank == x.Rank && ReferenceEquals(x.Product.Category, cats[2])));
    CollectionAssert.AreEqual(LinqSeed.TagsOf(4).Order().ToList(),
      (await six[3].TagQuery.OrderBy(t => t.Name).Select(t => t.Name).ToListAsync()).ToList());
  }

  [TestMethod]
  public async Task Includes_LoadTheGraphWithTheQuery() {
    // WHAT AN INCLUDE PROVES: after the query, walking what it named runs NO statement — `LastCommands` is the same
    // snapshot before and after the walk, since a load would have replaced it.
    var session = NewSession();
    WriteTestTitle("a reference and a list included, in one `new { … }`");
    var products = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 12).OrderBy(p => p.Rank)
      .Include(p => new { p.Category, p.Tags }).ToListAsync();
    var afterQuery = session.LastCommands;
    var described = products.Select(p => $"{p.Name} in {p.Category.Name} with {p.Tags.Count} tags").ToList();
    Assert.AreEqual(12, described.Count);
    Assert.AreSame(afterQuery, session.LastCommands, "the categories and the tags were loaded with the rows");

    WriteTestTitle("an include for the rows an include loads, and a chain");
    session = NewSession();
    var categories = await session.EntitySet<Linq.ICategory>()
      .Include(c => c.Products).Include((Linq.IProduct p) => p.Tags).ToListAsync();
    afterQuery = session.LastCommands;
    var tagged = categories.Sum(c => c.Products.Sum(p => p.Tags.Count));
    Assert.IsTrue(tagged > 0, "the seed tags products");
    Assert.AreSame(afterQuery, session.LastCommands, "the products and their tags were loaded with the categories");
    session = NewSession();
    var chained = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank <= 5)
      .Include(p => p.Category.Products).ToListAsync();
    afterQuery = session.LastCommands;
    var siblings = chained.Sum(p => p.Category.Products.Count);
    Assert.IsTrue(siblings >= 5, "a product is among its category's products");
    Assert.AreSame(afterQuery, session.LastCommands, "the chain loaded the categories and then their products");

    WriteTestTitle("an include written before the filter, not last");
    session = NewSession();
    var early = await session.EntitySet<Linq.IProduct>().Include(p => p.Category).Where(p => p.Rank <= 3).ToListAsync();
    afterQuery = session.LastCommands;
    Assert.AreEqual(3, early.Count);
    Assert.IsTrue(early.All(p => p.Category.Name.Length > 0), "the categories came with the rows");
    Assert.AreSame(afterQuery, session.LastCommands, "an include anywhere in the chain is taken out and honoured");

    WriteTestTitle("by name, on one row, and carried by the session");
    session = NewSession();
    var byName = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row["Rank"] <= 6).Include("Category", "Tags"));
    afterQuery = session.LastCommands;
    foreach (var record in byName) {
      var product = (Linq.IProduct)record.EntityInstance;
      _ = product.Category.Name + product.Tags.Count;
    }
    Assert.AreEqual(6, byName.Count);
    Assert.AreSame(afterQuery, session.LastCommands, "the paths were loaded with the records");
    session = NewSession();
    session.AddInclude<Linq.IProduct>(p => p.Category);
    var third = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 3).FirstAsync();
    afterQuery = session.LastCommands;
    Assert.AreEqual(LinqSeed.CategoryOf(3), third.Category.Name);
    Assert.AreSame(afterQuery, session.LastCommands, "the session's include applied, to one row as to many");

    // The CONTEXT's include applies to every session of the context, this one included.
    session.Context.AddInclude<Linq.IProduct>(p => p.Tags);
    var fourth = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 4).FirstAsync();
    afterQuery = session.LastCommands;
    _ = fourth.Tags.Count + fourth.Category.Name.Length;
    Assert.AreSame(afterQuery, session.LastCommands, "the context's include and the session's both applied");
    session.Context.RemoveInclude<Linq.IProduct>(p => p.Tags);

    // A VALUE MEMBER is nothing to load, and is refused by name.
    var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      session.EntitySet<Linq.IProduct>().Include(p => p.Name).ToListAsync());
    StringAssert.Contains(refused.Message, "Name");
  }

  [TestMethod]
  public async Task QueryableProperty_WithACachedSubQuery_IsAnsweredForAllOwners() {
    // A sub-query over the cached category becomes a parameter evaluated in memory when the statement runs; the
    // smart load's result key leaves it out, as it leaves out every placeholder read off the run.
    var session = NewSession();
    var categories = await session.EntitySet<Linq.ICategory>().OrderBy(c => c.Name).ToListAsync();
    var named = session.EntitySet<Linq.ICategory>().Where(c => c.Name.Length > 0).Select(c => c.Id);
    var first = await categories[0].ProductQuery.Where(p => named.Contains(p.Category.Id)).ToListAsync();
    var products = session.EntitySet<Linq.IProduct>();
    Assert.AreEqual(await products.CountAsync(p => p.Category.Id == categories[0].Id), first.Count);
    var asked = session.LastCommands;
    var second = await categories[1].ProductQuery.Where(p => named.Contains(p.Category.Id)).ToListAsync();
    Assert.AreSame(asked, session.LastCommands, "the sibling was answered by the batch");
    Assert.AreEqual(await products.CountAsync(p => p.Category.Id == categories[1].Id), second.Count);
  }
}
