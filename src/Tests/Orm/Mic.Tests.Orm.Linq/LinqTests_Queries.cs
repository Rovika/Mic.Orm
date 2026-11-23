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

// A QUERY OVER ONE TABLE: filters on every kind of value, ordering and paging, an aggregate over a page's filter, an
// IN list rendered per vendor, the same condition answered alike in SQL and in memory, a function named by the engine
// and spelled by the vendor, one row cut by the server, and a static member read per call.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task Query_FiltersOnEveryKindOfValue() {
    var session = NewSession();

    WriteTestTitle("everything, unfiltered");
    var products = await session.ExecuteQueryAsync(session.From("Product"));
    Assert.AreEqual(LinqSeed.ProductCount, products.Count);

    WriteTestTitle("a bound parameter, and the same one twice");
    var byRank = session.From("Product")
      .Where(row => QueryMaker.And(
        row.Member("Rank").GreaterOrEqual(QueryMaker.Parameter("from", typeof(int))),
        row.Member("Rank").LessOrEqual(QueryMaker.Parameter("to", typeof(int)))));
    Assert.AreEqual(10, (await session.ExecuteQueryAsync(byRank, 11, 20)).Count);
    // ONE TRANSLATION, MANY VALUES — the whole reason a varying value is a parameter and not a constant.
    Assert.AreEqual(5, (await session.ExecuteQueryAsync(byRank, 1, 5)).Count);

    WriteTestTitle("an enum constant, written into the text");
    var live = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live))));
    Assert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Count, live.Count);

    WriteTestTitle("IS NULL, and IS NOT NULL");
    var stillSold = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("RetiredOn").Equal(QueryMaker.Constant(null))));
    Assert.AreEqual(Ranks(r => LinqSeed.RetiredOn(r) == null).Count, stillSold.Count);
    var retired = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("RetiredOn").NotEqual(QueryMaker.Constant(null))));
    Assert.AreEqual(LinqSeed.ProductCount - stillSold.Count, retired.Count);

    WriteTestTitle("a decimal and a date, compared");
    var dear = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Price").Greater(QueryMaker.Parameter("min", typeof(decimal)))), 100m);
    Assert.AreEqual(Ranks(r => LinqSeed.PriceOf(r) > 100m).Count, dear.Count);
    var early = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("ReleasedOn").Less(QueryMaker.Constant(new DateOnly(2021, 1, 1)))));
    Assert.AreEqual(Ranks(r => LinqSeed.ReleasedOn(r) < new DateOnly(2021, 1, 1)).Count, early.Count);

    WriteTestTitle("OR, NOT, and arithmetic");
    var ends = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => QueryMaker.Or(
        row.Member("Rank").LessOrEqual(QueryMaker.Constant(3)),
        row.Member("Rank").Greater(QueryMaker.Constant(LinqSeed.ProductCount - 2)))));
    Assert.AreEqual(5, ends.Count);
    var notLive = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)).Not()));
    Assert.AreEqual(LinqSeed.ProductCount - live.Count, notLive.Count);
    var doubled = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Rank").Multiply(QueryMaker.Constant(2)).Greater(QueryMaker.Constant(100))));
    Assert.AreEqual(Ranks(r => r * 2 > 100).Count, doubled.Count);

    WriteTestTitle("what comes back is a tracked record");
    var record = (await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Constant(7)))))[0];
    var product = (Linq.IProduct)record.EntityInstance;
    Assert.AreEqual("Product 07", product.Name);
    Assert.AreEqual(LinqSeed.PriceOf(7), product.Price);
    // A REFERENCE MEMBER STILL LOADS, by the by-key path that now runs through the engine as well.
    Assert.AreEqual(LinqSeed.CategoryOf(7), product.Category.Name);
    // And the identity map answers the second read with the object the first one produced.
    var reread = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Rank").Equal(QueryMaker.Constant(7))));
    Assert.AreSame(record, reread[0]);
  }

  [TestMethod]
  public async Task Ordering_AndPagingReturnTheRightRanks() {
    var session = NewSession();

    WriteTestTitle("ascending, descending, and a second term");
    var ascending = await session.ExecuteQueryAsync(session.From("Product").OrderBy(row => row.Member("Rank")));
    CollectionAssert.AreEqual(Ranks(_ => true), RanksOf(ascending), "every row, in rank order");

    var descending = await session.ExecuteQueryAsync(
      session.From("Product").OrderBy(row => row.Member("Rank"), descending: true));
    CollectionAssert.AreEqual(Ranks(_ => true).AsEnumerable().Reverse().ToList(), RanksOf(descending));

    // TWO TERMS, and the second is what decides within a group of equal firsts. Ordering by status puts three
    // products of each rank-mod-3 together, and rank is what orders them inside that.
    var byStatusThenRank = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Status"))
      .OrderBy(row => row.Member("Rank"), descending: true));
    var expected = Ranks(_ => true)
      .OrderBy(r => (int)LinqSeed.StatusOf(r)).ThenByDescending(r => r).ToList();
    CollectionAssert.AreEqual(expected, RanksOf(byStatusThenRank), "APPENDED, so the first call is the primary sort");

    WriteTestTitle("a page, by exact ranks rather than by count");
    var page = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank"))
      .Skip(QueryMaker.Constant(10)).Take(QueryMaker.Constant(5)));
    CollectionAssert.AreEqual(Ranks(r => r is >= 11 and <= 15), RanksOf(page));

    WriteTestTitle("take with no skip, and skip with no take — the three templates");
    var firstThree = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank")).Take(QueryMaker.Constant(3)));
    CollectionAssert.AreEqual(Ranks(r => r <= 3), RanksOf(firstThree));

    var afterFiftySeven = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Rank")).Skip(QueryMaker.Constant(LinqSeed.ProductCount - 3)));
    CollectionAssert.AreEqual(Ranks(r => r > LinqSeed.ProductCount - 3), RanksOf(afterFiftySeven));

    WriteTestTitle("a page whose bounds are parameters — one statement, every page");
    var paged = session.From("Product")
      .OrderBy(row => row.Member("Rank"))
      .Skip(QueryMaker.Parameter("skip", typeof(int)))
      .Take(QueryMaker.Parameter("take", typeof(int)));
    CollectionAssert.AreEqual(Ranks(r => r is >= 1 and <= 4), RanksOf(await session.ExecuteQueryAsync(paged, 0, 4)));
    CollectionAssert.AreEqual(Ranks(r => r is >= 5 and <= 8), RanksOf(await session.ExecuteQueryAsync(paged, 4, 4)));

    WriteTestTitle("filtered, ordered and paged together");
    var listing = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)))
      .OrderBy(row => row.Member("Price"), descending: true)
      .Skip(QueryMaker.Constant(2)).Take(QueryMaker.Constant(3)));
    var expectedListing = Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live)
      .OrderByDescending(LinqSeed.PriceOf).Skip(2).Take(3).ToList();
    CollectionAssert.AreEqual(expectedListing, RanksOf(listing));

    WriteTestTitle("a page of an unordered set is refused");
    // ON EVERY VENDOR, not only the two that would reject the SQL. Rows skipped from an order the server never
    // promised are arbitrary, so page 2 may repeat page 1 and omit what it did not have.
    var unordered = session.From("Product").Take(QueryMaker.Constant(5));
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => session.ExecuteQueryAsync(unordered));
  }

  [TestMethod]
  public async Task Aggregate_AnswersOverThePagesFilter() {
    var session = NewSession();

    WriteTestTitle("count, over everything and over a filter");
    Assert.AreEqual(LinqSeed.ProductCount, await session.ExecuteScalarAsync<long>(session.From("Product").Count()));

    // THE PAIR A LISTING NEEDS: the same filter asked for a page and for its total, so a pager can say "11–15 of 20".
    var live = session.From("Product")
      .Where(row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)));
    var expectedLive = Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live);
    Assert.AreEqual(expectedLive.Count, await session.ExecuteScalarAsync<long>(live.Count()));

    WriteTestTitle("sum, min, max and average");
    Assert.AreEqual(expectedLive.Sum(LinqSeed.PriceOf),
      await session.ExecuteScalarAsync<decimal>(live.Sum(row => row.Member("Price"))));
    Assert.AreEqual(expectedLive.Min(LinqSeed.PriceOf),
      await session.ExecuteScalarAsync<decimal>(live.Min(row => row.Member("Price"))));
    Assert.AreEqual(expectedLive.Max(LinqSeed.PriceOf),
      await session.ExecuteScalarAsync<decimal>(live.Max(row => row.Member("Price"))));
    // AVERAGED AS A DECIMAL WHATEVER THE SERVER RETURNS — the point of the statement carrying the type the caller
    // asked for rather than the type the column came back as.
    Assert.AreEqual(expectedLive.Average(LinqSeed.PriceOf),
      await session.ExecuteScalarAsync<decimal>(live.Average(row => row.Member("Price"))));
    Assert.AreEqual(expectedLive.Min(r => r), await session.ExecuteScalarAsync<int>(live.Min(row => row.Member("Rank"))));
    // AN AVERAGE OF INTEGERS HAS ITS FRACTION, as LINQ's has, also where the server's own average of integers is an
    // integer; and the typed extension answers a double, not the selector's type.
    var expectedRank = expectedLive.Average(r => (double)r);
    Assert.AreEqual(expectedRank,
      await session.ExecuteScalarAsync<double>(live.Average(row => row.Member("Rank"))), 1e-9);
    Assert.AreEqual(expectedRank, await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Status == Linq.ProductStatus.Live).AverageAsync(p => p.Rank), 1e-9);

    WriteTestTitle("a count with a bound parameter, and one matching nothing");
    var above = session.From("Product")
      .Where(row => row.Member("Price").Greater(QueryMaker.Parameter("min", typeof(decimal))))
      .Count();
    Assert.AreEqual(Ranks(r => LinqSeed.PriceOf(r) > 100m).Count, await session.ExecuteScalarAsync<long>(above, 100m));
    Assert.AreEqual(0L, await session.ExecuteScalarAsync<long>(above, 10_000m));

    WriteTestTitle("an aggregate over no rows is null, and a count of none is zero");
    var none = session.From("Product").Where(row => row.Member("Rank").Greater(QueryMaker.Constant(10_000)));
    Assert.IsNull(await session.ExecuteScalarAsync<decimal?>(none.Max(row => row.Member("Price"))));
    Assert.AreEqual(0L, await session.ExecuteScalarAsync<long>(none.Count()));

    // Counting a PAGE is a count of the page and needs the page to exist first — see the derived-table test.
  }

  [TestMethod]
  public async Task InList_RendersPerVendorAndMatches() {
    var session = NewSession();

    WriteTestTitle("a list constant — part of the query's shape");
    var draftOrRetired = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Status").In(QueryMaker.Constant(new[] { Linq.ProductStatus.Draft, Linq.ProductStatus.Retired }))));
    Assert.AreEqual(
      Ranks(r => LinqSeed.StatusOf(r) is Linq.ProductStatus.Draft or Linq.ProductStatus.Retired).Count,
      draftOrRetired.Count);

    WriteTestTitle("a list parameter — ONE TRANSLATION, LISTS OF DIFFERENT LENGTHS");
    // This is the whole reason a statement is a template. On PostgreSQL the collection binds as one array and the SQL
    // never changes; on the other four the values go into the text, so the very same cached artifact renders shorter
    // here and longer there — and both have to come back with the same rows.
    var byRanks = session.From("Product")
      .Where(row => row.Member("Rank").In(QueryMaker.Parameter("ranks", typeof(int[]))))
      .OrderBy(row => row.Member("Rank"));

    CollectionAssert.AreEqual(new List<int> { 3 }, RanksOf(await session.ExecuteQueryAsync(byRanks, [new[] { 3 }])));
    CollectionAssert.AreEqual(new List<int> { 2, 5, 9 },
      RanksOf(await session.ExecuteQueryAsync(byRanks, [new[] { 9, 2, 5 }])));
    var many = Enumerable.Range(1, 30).ToArray();
    CollectionAssert.AreEqual(Ranks(r => r <= 30), RanksOf(await session.ExecuteQueryAsync(byRanks, [many])));

    WriteTestTitle("values not present, and a list of none");
    Assert.AreEqual(0, (await session.ExecuteQueryAsync(byRanks, [new[] { 9_998, 9_999 }])).Count);
    // AN EMPTY LIST MATCHES NOTHING and must not become `IN ()`, which no server parses.
    Assert.AreEqual(0, (await session.ExecuteQueryAsync(byRanks, [Array.Empty<int>()])).Count);

    WriteTestTitle("lists of other types, and an IN alongside everything else");
    var byName = session.From("Product")
      .Where(row => row.Member("Name").In(QueryMaker.Parameter("names", typeof(string[]))));
    Assert.AreEqual(2, (await session.ExecuteQueryAsync(byName, [new[] { "Product 01", "Product 02" }])).Count);

    var combined = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => QueryMaker.And(
        row.Member("Status").In(QueryMaker.Parameter("statuses", typeof(Linq.ProductStatus[]))),
        row.Member("Price").Greater(QueryMaker.Parameter("min", typeof(decimal)))))
      .OrderBy(row => row.Member("Rank"))
      .Take(QueryMaker.Constant(4)),
      // Two arguments, so `params` packs them whatever their types — the spread only bites a single collection
      // argument standing alone, which is why every one of those below is written `[values]`.
      new[] { Linq.ProductStatus.Live }, 60m);
    var expected = Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live && LinqSeed.PriceOf(r) > 60m)
      .Take(4).ToList();
    CollectionAssert.AreEqual(expected, RanksOf(combined));

    WriteTestTitle("a count over an IN — the aggregate and the list in one statement");
    Assert.AreEqual(3L, await session.ExecuteScalarAsync<long>(
      session.From("Product")
        .Where(row => row.Member("Rank").In(QueryMaker.Parameter("ranks", typeof(int[]))))
        .Count(),
      [new[] { 1, 2, 3 }]));
  }

  [TestMethod]
  public async Task SameCondition_AnswersAlikeInSqlAndInMemory() {
    var session = NewSession();
    var products = await session.ExecuteQueryAsync(session.From("Product"));
    Assert.AreEqual(LinqSeed.ProductCount, products.Count);

    // The query's prepared tree, with the operations above the entity set run by the evaluator over `products`.
    Mic.Linq.Translation.NodeEvaluator EvaluatorOf(QueryNode query, object?[] arguments) {
      var root = Mic.Linq.Translation.QueryTranslator.Translate(QueryTreeScanner.Scan(query, arguments), query, App.MappedModel()).PreparedRoot;
      return new Mic.Linq.Translation.NodeEvaluatorBuilder(((MethodCallNode)root).Argument(0)).Build(root);
    }
    DataRequest RunOver(object?[] arguments) =>
      new(session, session.From("Product"), arguments) { ResultRows = products };
    IReadOnlyList<EntityRecord> RunInMemory(QueryNode query, params object?[] arguments) =>
      [.. ((IEnumerable<object?>)EvaluatorOf(query, arguments).Evaluate(RunOver(arguments))!).Cast<EntityRecord>()];

    // THE POINT OF THE WHOLE EXERCISE: one tree, two ways to run it, and the answers must be the same set. A cached
    // entity's rows never reach a server, so whatever a query asks of them is asked by the evaluator instead — and an
    // evaluator that disagreed would make the answer depend on where the data happened to be.
    async Task AssertAgrees(string what, Func<QueryNode, QueryNode> condition, params object?[] arguments) {
      var query = session.From("Product").Where(condition);
      WriteTestTitle(what);
      var fromServer = RanksOf(await session.ExecuteQueryAsync(query, arguments)).Order().ToList();
      // THE SAME TREE, RUN BY THE EVALUATOR: translated as any query is, then the filter run over the records the
      // server returned for the whole set — the way the C# above a statement runs over what the statement returned.
      // A FRESH TREE for the in-memory run: translation transforms the tree it is given, so the one the server ran is
      // already SQL-shaped under its lambdas.
      var inMemory = RunInMemory(session.From("Product").Where(condition), arguments)
        .Select(r => ((Linq.IProduct)r.EntityInstance).Rank).Order().ToList();
      CollectionAssert.AreEqual(fromServer, inMemory, what);
      Assert.AreNotEqual(0, fromServer.Count, $"{what} matched nothing, so it proves nothing");
    }

    await AssertAgrees("a plain comparison",
      row => row.Member("Rank").Greater(QueryMaker.Parameter("from", typeof(int))), 40);
    await AssertAgrees("an equality on an enum",
      row => row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)));
    await AssertAgrees("IS NULL", row => row.Member("RetiredOn").Equal(QueryMaker.Constant(null)));
    await AssertAgrees("IS NOT NULL", row => row.Member("RetiredOn").NotEqual(QueryMaker.Constant(null)));

    // A COMPARISON AGAINST A NULL COLUMN IS UNKNOWN, and a WHERE keeps only what is TRUE — so the rows with no
    // retirement date are absent from both answers rather than present in one.
    await AssertAgrees("a comparison a null makes UNKNOWN",
      row => row.Member("RetiredOn").Greater(QueryMaker.Constant(new DateOnly(2022, 6, 1))));

    // AND `NOT UNKNOWN` IS STILL UNKNOWN. Two-valued logic would bring every null row back here, which is the single
    // most likely way an in-memory filter quietly disagrees with a server.
    await AssertAgrees("NOT over that same comparison",
      row => row.Member("RetiredOn").Greater(QueryMaker.Constant(new DateOnly(2022, 6, 1))).Not());

    await AssertAgrees("an unknown inside an OR",
      row => QueryMaker.Or(
        row.Member("Status").Equal(QueryMaker.Constant(Linq.ProductStatus.Live)),
        row.Member("RetiredOn").Greater(QueryMaker.Constant(new DateOnly(2022, 6, 1)))));
    await AssertAgrees("an unknown inside an AND",
      row => QueryMaker.And(
        row.Member("Rank").Greater(QueryMaker.Constant(10)),
        row.Member("RetiredOn").Less(QueryMaker.Constant(new DateOnly(2023, 1, 1)))));

    await AssertAgrees("IN over a list parameter",
      row => row.Member("Rank").In(QueryMaker.Parameter("ranks", typeof(int[]))),
      new[] { 3, 9, 27, 81 });

    // THROUGH A REFERENCE, where the evaluator walks the object graph and the server joins — and through a NULLABLE
    // one, where a missing target reads as null on one side and a LEFT OUTER JOIN produces null on the other.
    await AssertAgrees("through a reference",
      row => row.Member("Category").Member("Name").Equal(QueryMaker.Constant("Tools")));
    await AssertAgrees("through a NULLABLE reference",
      row => row.Member("Secondary").Member("Name").Equal(QueryMaker.Constant("Paint")));

    WriteTestTitle("the predicate says which parameters it takes, and in what order");
    var twoParams = session.From("Product").Where(row => QueryMaker.And(
      row.Member("Rank").GreaterOrEqual(QueryMaker.Parameter("from", typeof(int))),
      row.Member("Rank").LessOrEqual(QueryMaker.Parameter("to", typeof(int)))));
    var built = EvaluatorOf(twoParams, [11, 20]);
    CollectionAssert.AreEqual(new[] { "from", "to" }, built.ParameterNames.ToArray());
    Assert.AreEqual(10, RunInMemory(twoParams, 11, 20).Count);
  }

  [TestMethod]
  public async Task Function_NamedByTheEngineSpelledByTheVendor() {
    var session = NewSession();

    WriteTestTitle("strings");
    // `UPPER` on four servers and `UCASE` was the alternative; `LENGTH` is `LEN` on SQL Server and `CHAR_LENGTH` on
    // MySQL, where plain `LENGTH` would count BYTES. One tree, five spellings, and the same answer.
    var shouty = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Category.Name.ToUpper() == "TOOLS")
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.CategoryOf(r) == "Tools").Count, shouty.Count);

    // Every seeded name is `Product NN`, so length is a constant 10 and a mismatch would show at once.
    Assert.AreEqual(LinqSeed.ProductCount,
      await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Length == 10).CountAsync());

    var concatenated = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank == 1)
      .Select(p => p.Name + "!")
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "Product 01!" }, concatenated.ToArray());

    // SUBSTRING IS 1-BASED IN SQL AND 0-BASED IN C#, and the one is added when the query is built — so what a caller
    // writes means what C# means.
    var initials = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank <= 2).OrderBy(p => p.Rank)
      .Select(p => p.Name.Substring(0, 7))
      .ToListAsync();
    CollectionAssert.AreEqual(new[] { "Product", "Product" }, initials.ToArray());

    WriteTestTitle("LIKE, and the wildcards a caller did not ask for");
    var numbered = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Name.StartsWith("Product 1"))
      .ToListAsync();
    Assert.AreEqual(Ranks(r => $"Product {r:D2}".StartsWith("Product 1")).Count, numbered.Count);

    var ending = await session.EntitySet<Linq.IProduct>().Where(p => p.Name.EndsWith("07")).ToListAsync();
    Assert.AreEqual(1, ending.Count);

    var containing = await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Contains("t 2")).ToListAsync();
    Assert.AreEqual(Ranks(r => $"Product {r:D2}".Contains("t 2")).Count, containing.Count);

    // THE PATTERN IS THE VALUE, SO THE CALLER'S OWN WILDCARDS ARE LITERAL. Nothing in the seed contains a percent
    // sign, so an unescaped `%` — which would match everything — is the difference between 0 and 60.
    var literalPercent = await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Contains("%")).ToListAsync();
    Assert.AreEqual(0, literalPercent.Count, "a percent sign in the search text means a percent sign");

    var literalUnderscore = await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Contains("_")).ToListAsync();
    Assert.AreEqual(0, literalUnderscore.Count, "an underscore means an underscore, not any character");

    // AND A CHARACTER CLASS, WHICH ONLY SQL SERVER HAS. `%[0-9]%` unescaped matches every name here, since all of
    // them contain a digit; escaped it matches the literal text and nothing does. The other four read `[` as an
    // ordinary character and answer nought either way — so this asserts the same thing everywhere and can only fail
    // where the escaping is wrong.
    var literalClass = await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Contains("[0-9]")).ToListAsync();
    Assert.AreEqual(0, literalClass.Count, "a bracket is a bracket, not a character class");

    // AND ONE STATEMENT SERVES EVERY SEARCH TERM, because the wildcards go on the bound value and never into the SQL.
    var cache = App.DataAccess.FindDatabase()!.QueryCache;
    cache.Clear();
    async Task<int> NamedLikeAsync(string term) =>
      await session.EntitySet<Linq.IProduct>().Where(p => p.Name.Contains(term)).CountAsync();
    Assert.AreEqual(1, await NamedLikeAsync("t 03"));
    Assert.AreEqual(1, await NamedLikeAsync("t 04"));
    Assert.AreEqual(1, cache.Misses, "one statement for every search term");

    WriteTestTitle("dates, and a nullable");
    var released2020 = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.ReleasedOn.Year == 2020)
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.ReleasedOn(r).Year == 2020).Count, released2020.Count);

    // `.HasValue` IS A NULL TEST and `.Value` is the column — neither is a thing the database has.
    var retired = await session.EntitySet<Linq.IProduct>().Where(p => p.RetiredOn.HasValue).ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.RetiredOn(r) != null).Count, retired.Count);

    var retiredLate = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.RetiredOn.HasValue && p.RetiredOn.Value.Year >= 2022)
      .ToListAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.RetiredOn(r)?.Year >= 2022).Count, retiredLate.Count);

    WriteTestTitle("what a server has not got is refused by name");
    // SQLite has no CEILING it can be relied on to have, so the query is refused when it is BUILT rather than sent
    // and rejected. Every other vendor answers it.
    if (Fixture.ServerKind == DbServerKind.SQLite) {
      var missing = await Assert.ThrowsExactlyAsync<NotSupportedException>(
        () => session.EntitySet<Linq.IProduct>().Where(p => Math.Ceiling(p.Price) > 100).ToListAsync());
      StringAssert.Contains(missing.Message, "Ceiling");
      // AND THE SAME FOR THE REST OF THE MATHS, which are the same optional extension: refused by name here rather
      // than sent to a server that would answer "no such function".
      await Assert.ThrowsExactlyAsync<NotSupportedException>(
        () => session.EntitySet<Linq.IProduct>().Where(p => Math.Sqrt((double)p.Price) > 5).ToListAsync());
    } else {
      Assert.AreEqual(Ranks(r => Math.Sign(LinqSeed.PriceOf(r) - 50m) > 0).Count,
        await session.EntitySet<Linq.IProduct>().CountAsync(p => Math.Sign(p.Price - 50m) > 0));
      Assert.AreEqual(Ranks(r => Math.Pow((double)LinqSeed.PriceOf(r), 2) > 10000).Count,
        await session.EntitySet<Linq.IProduct>().CountAsync(p => Math.Pow((double)p.Price, 2) > 10000));
      var rounded = await session.EntitySet<Linq.IProduct>()
        .Where(p => Math.Ceiling(p.Price) > 100)
        .ToListAsync();
      Assert.AreEqual(Ranks(r => Math.Ceiling(LinqSeed.PriceOf(r)) > 100).Count, rounded.Count);
    }

    WriteTestTitle("IndexOf, where two conventions differ at once");
    // SQL COUNTS FROM ONE AND SAYS 0 FOR ABSENT; C# counts from zero and says -1. ONE subtraction reconciles both,
    // which is why the two seeded assertions below — a hit and a miss — are the whole test.
    // Four spellings: `POSITION(… IN …)`, `INSTR`, `CHARINDEX`, and the argument order reversed on two of them.
    Assert.AreEqual(LinqSeed.ProductCount, await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Name.IndexOf("roduct") == 1).CountAsync());
    Assert.AreEqual(LinqSeed.ProductCount, await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Name.IndexOf("zzz") == -1).CountAsync());

    WriteTestTitle("REPLACE, and rounding to a number of places");
    var renamed = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 7)
      .Select(p => p.Name.Replace("Product", "Item")).ToListAsync();
    CollectionAssert.AreEqual(new[] { "Item 07" }, renamed.ToArray());

    // `ROUND(x, n)` IS A SECOND KIND rather than a second arity of `Round`, since a dialect's table holds one form
    // per kind. Halves are avoided: the vendors disagree about the midpoint and this is not the test for that.
    var toThePenny = await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 3)
      .Select(p => Math.Round(p.Price / 3m, 2)).ToListAsync();
    CollectionAssert.AreEqual(new[] { Math.Round(LinqSeed.PriceOf(3) / 3m, 2) }, toThePenny.ToArray());

  }

  [TestMethod]
  public async Task OneRow_IsCutByTheServerAndOrdered() {
    var session = NewSession();

    WriteTestTitle("first, last and single over a stated ordering");
    Assert.AreEqual(1, (await session.EntitySet<Linq.IProduct>().OrderBy(p => p.Rank).FirstAsync()).Rank);
    Assert.AreEqual(LinqSeed.ProductCount,
      (await session.EntitySet<Linq.IProduct>().OrderBy(p => p.Rank).LastAsync()).Rank);
    // AND `Last` REVERSES THE WHOLE ORDERING, not its first term — with two terms the answer is only right if both
    // flipped, so this would fail if the reversal stopped at the primary sort.
    var byCategoryThenRank = Ranks(_ => true)
      .OrderBy(LinqSeed.CategoryOf, StringComparer.Ordinal).ThenBy(r => r).ToList();
    Assert.AreEqual(byCategoryThenRank[^1], (await session.EntitySet<Linq.IProduct>()
      .OrderBy(p => p.Category.Name).ThenBy(p => p.Rank).LastOrDefaultAsync())!.Rank);

    Assert.AreEqual(5, (await session.EntitySet<Linq.IProduct>().Where(p => p.Rank == 5).SingleAsync()).Rank);

    WriteTestTitle("an unordered query is still asked in an order");
    // Two servers will not take a limit without an ORDER BY at all, and on the other three the answer would be
    // whatever the plan produced. Ordering by the primary key makes it repeatable — which is what anybody writing
    // this assumed they were getting.
    var once = await session.EntitySet<Linq.IProduct>().FirstOrDefaultAsync();
    var twice = await session.EntitySet<Linq.IProduct>().FirstOrDefaultAsync();
    Assert.IsNotNull(once);
    Assert.AreEqual(once.Rank, twice!.Rank, "the same call twice is the same row");

    // AN ORDERING ANYWHERE IN THIS SELECT COUNTS, not only one written last — so no second ordering is added here.
    Assert.AreEqual(11, (await session.EntitySet<Linq.IProduct>()
      .Where(p => p.Rank > 10).OrderBy(p => p.Rank).FirstAsync()).Rank);

    // BUT ORDERING AND THEN FILTERING PUTS THE ORDERING IN AN INNER SELECT, where it decides nothing about which row
    // the outer one returns — and answering by the primary key instead would be a different question quietly
    // substituted for the one asked.
    var backwards = await Assert.ThrowsExactlyAsync<NotSupportedException>(
      () => session.EntitySet<Linq.IProduct>().OrderBy(p => p.Rank).Where(p => p.Rank > 0).FirstAsync());
    StringAssert.Contains(backwards.Message, "Filter before ordering");

    WriteTestTitle("nothing matched, and too much did");
    Assert.IsNull(await session.EntitySet<Linq.IProduct>().Where(p => p.Rank < 0).FirstOrDefaultAsync());
    await Assert.ThrowsExactlyAsync<InvalidOperationException>(
      () => session.EntitySet<Linq.IProduct>().Where(p => p.Rank < 0).FirstAsync());

    // `Single` ASKS THE SERVER FOR TWO ROWS, so "more than one matched" is something it was told rather than
    // something noticed after reading a table.
    await Assert.ThrowsExactlyAsync<InvalidOperationException>(
      () => session.EntitySet<Linq.IProduct>().Where(p => p.Rank > 0).SingleAsync());
    Assert.IsNull(await session.EntitySet<Linq.IProduct>().Where(p => p.Rank < 0).SingleOrDefaultAsync());

    WriteTestTitle("any, all, and distinct");
    Assert.IsTrue(await session.EntitySet<Linq.IProduct>().AnyAsync());
    Assert.IsFalse(await session.EntitySet<Linq.IProduct>().Where(p => p.Rank < 0).AnyAsync());
    Assert.IsTrue(await session.EntitySet<Linq.IProduct>().AllAsync(p => p.Price > 0));
    Assert.IsFalse(await session.EntitySet<Linq.IProduct>().AllAsync(p => p.Rank > 1));

    // DISTINCT EARNS ITS PLACE OVER A PROJECTION, where two rows of the table can select the same thing — sixty
    // products across three categories.
    var categories = await session.EntitySet<Linq.IProduct>()
      .Select(p => p.Category.Name)
      .Distinct()
      .ToListAsync();
    CollectionAssert.AreEquivalent(LinqSeed.Categories, categories.ToArray());
  }

  /// <summary>Two static members of the same NAME on different types — what a cache key has to tell apart.</summary>
  private static class FakeString {
    public static readonly string Empty = "Product 01";
  }

  [TestMethod]
  public async Task StaticMember_ReadPerCallKeyedByType() {
    var session = NewSession();
    var statements = App.DataAccess.FindDatabase()!.QueryCache;
    statements.Clear();

    WriteTestTitle("a static member is a parameter, read when the query runs");
    // NOT A CONSTANT, which matters most for the ones that move: fixing `DateTime.Today` at translation would hand
    // every later call the day the statement was first built.
    var blank = await session.EntitySet<Linq.IProduct>().Where(p => p.Name == string.Empty).ToListAsync();
    Assert.AreEqual(0, blank.Count);

    var released = await session.EntitySet<Linq.IProduct>()
      .Where(p => p.ReleasedOn < DateOnly.FromDateTime(DateTime.Today)).CountAsync();
    Assert.AreEqual(Ranks(r => LinqSeed.ReleasedOn(r) < DateOnly.FromDateTime(DateTime.Today)).Count, released);

    WriteTestTitle("two `Empty`s on different types are two statements");
    // A static member has no target to identify it, so only its DECLARING TYPE tells these apart. Keyed by the name
    // alone, the second query would have been served the first's statement — and bound the wrong value.
    var mine = await session.EntitySet<Linq.IProduct>().Where(p => p.Name == FakeString.Empty).ToListAsync();
    Assert.AreEqual(1, mine.Count, "Blank.Empty is 'Product 01', not the empty string");
    Assert.AreEqual(1, mine[0].Rank);
    Assert.AreEqual(3, statements.Misses, "three shapes, not two");
  }
}
