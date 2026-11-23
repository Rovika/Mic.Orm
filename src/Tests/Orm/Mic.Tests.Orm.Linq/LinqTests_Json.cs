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

// JSON IN A QUERY: the rows of a parameter as a source joined like a table, a JSON column read by path in a WHERE, an
// ORDER BY and a SELECT, and a JSON array in a column spread as rows.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task ParameterRows_AreASourceJoinedLikeATable() {
    var session = NewSession();
    var model = session.GetLinqModel();
    var database = App.DataAccess.FindDatabase()!;
    var cache = database.QueryCache;

    WriteTestTitle("the rows alone — a source of nothing but the parameter, typed by its columns");
    var rows = QueryMaker.Rows("rows", ("n", typeof(int)), ("label", typeof(string)), ("at", typeof(DateTime)));
    var read = await session.ExecuteRowsAsync(rows.OrderBy(r => r.Member("n")),
      new List<object?[]> { new object?[] { 2, "two", new DateTime(2024, 2, 2, 3, 4, 5) }, new object?[] { 1, "one", new DateTime(2024, 1, 1) } });
    Assert.AreEqual(2, read.Count);
    Assert.AreEqual(1, read[0].GetValue("n"));
    Assert.AreEqual("one", read[0].GetValue("label"));
    Assert.AreEqual(new DateTime(2024, 1, 1), read[0].GetValue("at"));
    Assert.AreEqual(2, read[1].GetValue("n"));
    Assert.AreEqual(new DateTime(2024, 2, 2, 3, 4, 5), read[1].GetValue("at"));

    WriteTestTitle("joined to a table on its key — the shape a load by keys takes");
    var products = await session.ExecuteQueryAsync(session.From("Product"));
    var wanted = products.Where(r => ((Linq.IProduct)r.EntityInstance).Rank is 3 or 7 or 11)
      .Select(r => new object?[] { ((Linq.IProduct)r.EntityInstance).Id }).ToList();
    ILinqEntity product = model.FindEntity("Product")!;
    var keys = QueryMaker.Rows(QueryMaker.Parameter("keys", typeof(object)), product.FindMember("Id")!);
    var byKeys = await session.ExecuteQueryAsync(session.From("Product")
      .Join(keys, (p, k) => p.Member("Id").Equal(k.Member("Id")), (p, k) => [p])
      .OrderBy(p => p.Member("Rank")), wanted);
    CollectionAssert.AreEqual(new[] { 3, 7, 11 }, RanksOf(byKeys));

    WriteTestTitle("a COMPOSITE match — every column of the row joined on, and one statement for any list");
    QueryNode ByPair() => session.From("Product")
      .Join(QueryMaker.Rows("pairs", ("rank", typeof(int)), ("name", typeof(string))),
        (p, k) => p.Member("Rank").Equal(k.Member("rank")) & p.Member("Name").Equal(k.Member("name")), (p, k) => [p])
      .OrderBy(p => p.Member("Rank"));
    var misses = cache.Misses;
    var matched = await session.ExecuteQueryAsync(ByPair(),
      new List<object?[]> { new object?[] { 5, "Product 05" }, new object?[] { 6, "not its name" }, new object?[] { 9, "Product 09" } });
    CollectionAssert.AreEqual(new[] { 5, 9 }, RanksOf(matched), "a row matches on BOTH columns or not at all");
    var rematched = await session.ExecuteQueryAsync(ByPair(), new List<object?[]> { new object?[] { 1, "Product 01" } });
    CollectionAssert.AreEqual(new[] { 1 }, RanksOf(rematched));
    Assert.AreEqual(misses + 1, cache.Misses, "one translation served both lists");

    // THE TEXT IS FIXED WHATEVER THE LIST HOLDS: one parameter, not a list, and no value written in.
    var byPair = ByPair();
    var template = SqlBuilder.Build(QueryTranslator.Translate(QueryTreeScanner.Scan(byPair), byPair,
      database.DbModel), database.DbModel);
    Console.WriteLine(template);
    Assert.AreEqual(1, template.Placeholders.Count);
    Assert.IsFalse(template.Placeholders[0].IsList, "one JSON value, not a list rendered as literals");
    StringAssert.DoesNotMatch(template.ToString(), new System.Text.RegularExpressions.Regex("Product 0"));
  }

  [TestMethod]
  public async Task JsonColumn_IsReadByPath() {
    var session = NewSession();
    static bool Specified(int rank) => rank % 10 != 0;

    WriteTestTitle("a property of the document is a path into it, read as the property's type");
    var heavy = await session.EntitySet<Linq.IProduct>().Where(p => p.Spec!.Weight > 20).OrderBy(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r * 0.5 > 20), heavy.Select(p => p.Rank).ToList());
    Console.WriteLine(session.LastCommands.Single().Sql);

    WriteTestTitle("text, a boolean, a date, and a hop further down");
    var tools = await session.EntitySet<Linq.IProduct>().Where(p => p.Spec!.Origin == "Tools").CountAsync();
    Assert.AreEqual(Ranks(r => Specified(r) && LinqSeed.CategoryOf(r) == "Tools").Count, tools);
    var fragile = await session.EntitySet<Linq.IProduct>().Where(p => p.Spec!.Fragile).CountAsync();
    Assert.AreEqual(Ranks(r => Specified(r) && r % 3 == 0).Count, fragile);
    var since = LinqSeed.ReleasedOn(30);
    var recent = await session.EntitySet<Linq.IProduct>().Where(p => p.Spec!.Since >= since).CountAsync();
    Assert.AreEqual(Ranks(r => Specified(r) && r >= 30).Count, recent);
    var tall = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec!.Dims.Height == 7).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r % 10 == 7), tall.ToList());

    WriteTestTitle("ordered by the document, and the value selected out of it");
    var heaviest = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null)
      .OrderByDescending(p => p.Spec!.Weight).Take(3)
      .Select(p => new { p.Rank, p.Spec!.Weight })
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { 59, 58, 57 }, heaviest.Select(x => x.Rank).ToArray());
    Assert.AreEqual(29.5, heaviest[0].Weight);

    WriteTestTitle("JsonFunctions.Value for a path the type does not name, and a path that names nothing");
    var byFunction = await session.EntitySet<Linq.IProduct>()
      .Where(p => JsonFunctions.Value<string>(p.Spec, "$.Origin") == "Tools").CountAsync();
    Assert.AreEqual(tools, byFunction);
    var nothing = await session.EntitySet<Linq.IProduct>()
      .Where(p => JsonFunctions.Value<string>(p.Spec, "$.Nope") == null).CountAsync();
    Assert.AreEqual(LinqSeed.ProductCount, nothing, "a missing path is null, and so is a null document");

    WriteTestTitle("by name, typed");
    var untyped = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Spec").JsonValue("$.weight", typeof(double)).Greater(QueryMaker.Constant(20.0)))
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(heavy.Select(p => p.Rank).ToList(), RanksOf(untyped));

    WriteTestTitle("over a CACHED entity, the same question is answered in memory");
    var cached = NewApp(a => a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]));
    var memory = NewContext(cached).OpenSession();
    var inMemory = await memory.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec!.Weight > 20 && p.Spec.Origin == "Tools" && p.Spec.Since >= since)
      .OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r * 0.5 > 20 && LinqSeed.CategoryOf(r) == "Tools" && r >= 30), inMemory.ToList());
  }

  [TestMethod]
  public async Task JsonArray_SpreadsAsRows() {
    var session = NewSession();
    static bool Specified(int rank) => rank % 10 != 0;
    // What the seed put in: the items of every specified product, and the notes of every fifth.
    var seededItems = Enumerable.Range(1, LinqSeed.ProductCount).Where(Specified)
      .SelectMany(r => LinqSeed.SpecOf(r)!.Items.Select(i => (Rank: r, i.Sku, i.Qty))).ToList();

    // DB2 SPREADS NO JSON ARRAY INTO ROWS: the query is refused at translation.
    if (Fixture.ServerKind == DbServerKind.Db2) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.EntitySet<Linq.IProduct>()
        .SelectMany(p => p.Spec!.Items, (p, i) => new { p.Rank, i.Sku, i.Qty }).ToListAsync());
      StringAssert.Contains(refused.Message, "JSON array");
      return;
    }

    WriteTestTitle("an array INSIDE the document, spread beside the row and projected with it");
    var spread = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Spec!.Items, (p, i) => new { p.Rank, i.Sku, i.Qty })
      .Where(x => x.Qty >= 2)
      .OrderBy(x => x.Rank).ThenBy(x => x.Qty)
      .ToListAsync();
    Console.WriteLine(session.LastCommands.Single().Sql);
    var expected = seededItems.Where(i => i.Qty >= 2).OrderBy(i => i.Rank).ThenBy(i => i.Qty).ToList();
    CollectionAssert.AreEqual(expected.Select(i => i.Sku).ToList(), spread.Select(x => x.Sku).ToList());
    CollectionAssert.AreEqual(expected.Select(i => i.Rank).ToList(), spread.Select(x => x.Rank).ToList());

    WriteTestTitle("the array FILTERED as it spreads");
    // Ordered by rank and then by SKU, whose texts differ only in their last digit within a rank — an ordering no
    // collation reads differently from C#'s.
    var filtered = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Spec!.Items.Where(i => i.Qty >= 2), (p, i) => new { p.Rank, i.Sku })
      .OrderBy(x => x.Rank).ThenBy(x => x.Sku)
      .ToListAsync();
    CollectionAssert.AreEqual(expected.Select(i => i.Sku).ToList(), filtered.Select(x => x.Sku).ToList());

    WriteTestTitle("counted in a select list, and asked whether it has any in a WHERE");
    var counted = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null && p.Rank <= 8)
      .OrderBy(p => p.Rank)
      .Select(p => new { p.Rank, Items = p.Spec!.Items.Count() })
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r <= 8).Select(r => r % 4).ToList(), counted.Select(x => x.Items).ToList());
    var starred = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Notes!.Any(n => n.Stars == 2))
      .OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r % 5 == 0 && r % 3 == 2), starred.ToList());

    WriteTestTitle("an array that IS the column, spread — and by name");
    var notes = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Notes!, (p, n) => new { p.Rank, n.Text })
      .OrderBy(x => x.Rank)
      .ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r % 5 == 0).Select(r => $"note {r}").ToList(), notes.Select(x => x.Text).ToList());

    var byName = await session.ExecuteRowsAsync(session.From("Product")
      .SelectMany(row => row.Member("Notes").JsonRows(typeof(Linq.ProductNote)),
        (product, note) => [product.Member("Rank"), note.Member("Stars")])
      .OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(r => r % 5 == 0).Select(r => r % 3).ToList(), byName.Select(x => (int)x.GetValue("Stars")!).ToList());

    WriteTestTitle("an array of SCALARS: the element is the value — spread, filtered, asked, counted, and by name");
    var fragile = await session.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Spec!.Tags, (p, t) => new { p.Rank, Tag = t })
      .Where(x => x.Tag == "fragile").OrderBy(x => x.Rank).Select(x => x.Rank).ToListAsync();
    Console.WriteLine(session.LastCommands.Single().Sql);
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r % 3 == 2), fragile.ToList());
    var bulk = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec!.Tags.Any(t => t == "bulk")).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r % 3 != 0), bulk.ToList());
    var bulkByContains = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec!.Tags.Contains("bulk")).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(bulk.ToList(), bulkByContains.ToList(), "`Contains` over the array is `Any` with an equality");
    var tagged = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null && p.Rank <= 6).OrderBy(p => p.Rank)
      .Select(p => new { p.Rank, Tags = p.Spec!.Tags.Count() }).ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 0, 1, 2, 0 }, tagged.Select(x => x.Tags).ToArray());
    // Count as a property is the same count, in a select list and in a condition.
    var taggedByProperty = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null && p.Rank <= 6).OrderBy(p => p.Rank)
      .Select(p => new { p.Rank, Tags = p.Spec!.Tags.Count }).ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 0, 1, 2, 0 }, taggedByProperty.Select(x => x.Tags).ToArray());
    var twoTags = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null && p.Rank <= 6 && p.Spec!.Tags.Count == 2)
      .OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(new[] { 2, 5 }, twoTags.ToArray());
    var values = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 5).SelectMany(p => p.Spec!.Tags).ToListAsync();
    CollectionAssert.AreEqual(new[] { "bulk", "fragile" }, values.ToArray(), "the spread alone is the values");
    var valuesByName = await session.ExecuteValuesAsync<string>(session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Constant(5)))
      .SelectMany(row => row.Member("Spec").JsonValue("$.Tags", typeof(List<string>)).JsonRows(typeof(string)),
        (product, tag) => [tag.Member("value")]));
    CollectionAssert.AreEqual(new[] { "bulk", "fragile" }, valuesByName.ToArray());

    WriteTestTitle("over a CACHED entity, a spread and an aggregate over the array are answered in memory");
    var cached = NewApp(a => a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]));
    var memory = NewContext(cached).OpenSession();
    var inMemory = await memory.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec != null && p.Spec.Items.Any(i => i.Qty >= 3)).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Specified(r) && r % 4 == 3), inMemory.ToList());
    var skus = await memory.EntitySet<Linq.IProduct>().Where(p => p.Rank == 7)
      .SelectMany(p => p.Spec!.Items).Where(i => i.Qty >= 2).Select(i => i.Sku).ToListAsync();
    CollectionAssert.AreEqual(new[] { "S7-1", "S7-2" }, skus.ToArray());
    var tagsInMemory = await memory.EntitySet<Linq.IProduct>().Where(p => p.Rank == 5).SelectMany(p => p.Spec!.Tags).ToListAsync();
    CollectionAssert.AreEqual(new[] { "bulk", "fragile" }, tagsInMemory.ToArray());
    var bulkInMemory = await memory.EntitySet<Linq.IProduct>()
      .Where(p => p.Spec!.Tags.Contains("bulk")).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(bulk.ToList(), bulkInMemory.ToList(), "…and `Contains` over the array, in memory");
    var noted = await memory.EntitySet<Linq.IProduct>()
      .Where(p => p.Notes!.Any(n => n.Stars == 2)).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => r % 5 == 0 && r % 3 == 2), noted.ToList(), "the array that IS the column, off the object");

    WriteTestTitle("in memory too: a spread WITH a result selector, and a projection of several values");
    var pairsInMemory = await memory.EntitySet<Linq.IProduct>()
      .SelectMany(p => p.Spec!.Items, (p, i) => new { p.Rank, i.Sku, i.Qty })
      .Where(x => x.Qty >= 2)
      .OrderBy(x => x.Rank).ThenBy(x => x.Qty)
      .ToListAsync();
    CollectionAssert.AreEqual(expected.Select(i => i.Sku).ToList(), pairsInMemory.Select(x => x.Sku).ToList());
    CollectionAssert.AreEqual(expected.Select(i => i.Rank).ToList(), pairsInMemory.Select(x => x.Rank).ToList());
    var projectedInMemory = await memory.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 3).OrderBy(p => p.Rank).Select(p => new { p.Rank, Weight = p.Spec!.Weight }).ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, projectedInMemory.Select(x => x.Rank).ToArray());
    Assert.AreEqual(0.5, projectedInMemory[0].Weight);
  }
}
