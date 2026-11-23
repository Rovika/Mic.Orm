using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Linq;

// FULL-TEXT SEARCH: a match over the entity's full-text index — words with one meaning on every server that has it,
// each dialect spelling them; a rank to order by; and the by-name form of both. The seed gives every product "A
// {material} {thing} for {use}", the three lists cycling at periods 4, 3 and 5, so a word is in a known fraction of
// the products and a pair of words in the product of two fractions.
public abstract partial class LinqTests {
  [TestMethod]
  public async Task FullText_FindsTheRowsOfAnUpdateAndADelete() {
    if (!Db.Provider.Supports(DbFeatures.FullText))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no full-text search MIC writes yet.");
    // Three products of its own, with a word no seeded product has and ranks past the seed's; every one of them is
    // gone at the end, whatever happens, since the suite counts the seeded products.
    const int firstRank = 9001;
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    var category = (await session.EntitySet<Linq.ICategory>().ToListAsync())[0];
    var descriptions = new[] {
      "A zirconium gasket for pumps", "A zirconium flange for pumps", "A zirconium gasket for valves",
    };
    for (var i = 0; i < descriptions.Length; i++) {
      var product = session.NewEntity<Linq.IProduct>();
      product.Id = Guid.CreateVersion7();
      product.Rank = firstRank + i;
      product.Name = $"Special {i}";
      product.Description = descriptions[i];
      product.Category = category;
      product.ReleasedOn = new DateOnly(2024, 1, 1);
    }
    await session.SaveChangesAsync();
    try {
      await WaitForFullTextAsync();

      WriteTestTitle("an update of the rows a match finds, in the simple form");
      var repriced = await products.Where(p => FullText.Matches(p, "zirconium gasket"))
        .Select(p => new { Price = 5.5m }).ExecuteUpdateAsync<Linq.IProduct>();
      Assert.AreEqual(2, repriced);
      // Where the index is a joined table, the simple form has no select to join it into and asks it by key.
      if (Db.Provider.ServerKind == Mic.Orm.Db.DbServerKind.SQLite)
        StringAssert.Contains(session.LastCommands[^1].Sql, " IN (SELECT ");
      var prices = await products.Where(p => p.Rank >= firstRank).OrderBy(p => p.Rank).Select(p => p.Price)
        .ToListAsync();
      CollectionAssert.AreEqual(new[] { 5.5m, 0m, 5.5m }, prices.ToList());

      WriteTestTitle("the same match with another condition beside it");
      await WaitForFullTextAsync();
      Assert.AreEqual(1, await products.Where(p => FullText.Matches(p, "zirconium") && p.Rank == firstRank + 1)
        .Select(p => new { Price = 7m }).ExecuteUpdateAsync<Linq.IProduct>());

      WriteTestTitle("a delete of the rows a match finds, by key: the match stands in a select");
      await WaitForFullTextAsync();
      Assert.AreEqual(1, await products.Where(p => FullText.Matches(p, "zirconium flange")).OrderBy(p => p.Rank).Take(1)
        .ExecuteDeleteAsync());

      WriteTestTitle("and in the simple form");
      await WaitForFullTextAsync();
      Assert.AreEqual(2, await products.Where(p => FullText.Matches(p, "zirconium")).ExecuteDeleteAsync());
      Assert.AreEqual(0, await products.Where(p => p.Rank >= firstRank).CountAsync());

      WriteTestTitle("refused: a rank in a mutation's own WHERE where the index is joined");
      if (Db.Provider.ServerKind == Mic.Orm.Db.DbServerKind.SQLite)
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
          products.Where(p => FullText.Rank(p, "zirconium") > 0).ExecuteDeleteAsync());
    } finally {
      await products.Where(p => p.Rank >= firstRank).ExecuteDeleteAsync();
      await WaitForFullTextAsync();
    }

    // ----- local functions
    async Task WaitForFullTextAsync() {
      // One server indexes a change a moment later.
      for (var attempt = 0; !await session.IsFullTextIndexCurrentAsync<Linq.IProduct>(); attempt++) {
        if (attempt >= 120)
          Assert.Fail("The full-text index did not catch up in 30 seconds.");
        await Task.Delay(250);
      }
    }
  }

  [TestMethod]
  public async Task FullText_MatchesWordsOverTheIndex() {
    if (!Db.Provider.Supports(DbFeatures.FullText))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no full-text search MIC writes yet.");
    var session = NewSession();
    var products = session.EntitySet<Linq.IProduct>();
    static bool Has(int rank, string word) => LinqSeed.DescriptionOf(rank).Split(' ').Contains(word);
    // The seed is in; one server indexes it a moment later.
    for (var attempt = 0; !await session.IsFullTextIndexCurrentAsync<Linq.IProduct>(); attempt++) {
      if (attempt >= 120)
        Assert.Fail("The full-text index did not catch up with the seed in 30 seconds.");
      await Task.Delay(250);
    }

    WriteTestTitle("one word");
    var widgets = await products.Where(p => FullText.Matches(p, "widget")).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Has(r, "widget")), widgets.ToList(), "a third of the products");
    Assert.AreEqual(LinqSeed.ProductCount / 3, widgets.Count);

    WriteTestTitle("two words, both required");
    var query = "steel widget";
    var steelWidgets = await products.Where(p => FullText.Matches(p, query)).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Has(r, "steel") && Has(r, "widget")), steelWidgets.ToList(), "a twelfth");
    Assert.IsTrue(steelWidgets.Count > 0);

    WriteTestTitle("a word in the other column of the index");
    var named = await products.Where(p => FullText.Matches(p, "Product")).CountAsync();
    Assert.AreEqual(LinqSeed.ProductCount, named, "every name starts with it");

    WriteTestTitle("a word nobody has");
    Assert.AreEqual(0, await products.Where(p => FullText.Matches(p, "titanium")).CountAsync());

    WriteTestTitle("any of the words");
    var either = await products.Where(p => FullText.Matches(p, "oak glass", TextMatchKind.AnyWord)).CountAsync();
    Assert.AreEqual(Ranks(r => Has(r, "oak") || Has(r, "glass")).Count, either, "half of them");

    WriteTestTitle("a phrase");
    var phrase = await products.Where(p => FullText.Matches(p, "widget for gardens", TextMatchKind.Phrase)).OrderBy(p => p.Rank)
      .Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Has(r, "widget") && Has(r, "gardens")), phrase.ToList(), "adjacent, in order");
    Assert.AreEqual(0, await products.Where(p => FullText.Matches(p, "gardens widget", TextMatchKind.Phrase)).CountAsync(),
      "the other order is no phrase");

    WriteTestTitle("a prefix");
    var prefixed = await products.Where(p => FullText.Matches(p, "bra", TextMatchKind.Prefix)).CountAsync();
    Assert.AreEqual(Ranks(r => Has(r, "brass") || Has(r, "bracket")).Count, prefixed, "brass and bracket");

    WriteTestTitle("within a distance");
    var near = await products.Where(p => FullText.Matches(p, "widget kitchens", TextMatchKind.Near, 2)).OrderBy(p => p.Rank)
      .Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(Ranks(r => Has(r, "widget") && Has(r, "kitchens")), near.ToList(), "'widget for kitchens' — one word between");
    Assert.AreEqual(0, await products.Where(p => FullText.Matches(p, "steel kitchens", TextMatchKind.Near, 1)).CountAsync(),
      "'steel widget for kitchens' — two words between, one allowed");

    WriteTestTitle("ranked: the products matching more of the words first");
    var ranked = await products.Where(p => FullText.Matches(p, "steel widget", TextMatchKind.AnyWord))
      .OrderByDescending(p => FullText.Rank(p, "steel widget", TextMatchKind.AnyWord)).ThenBy(p => p.Rank)
      .Select(p => new { p.Rank, Score = FullText.Rank(p, "steel widget", TextMatchKind.AnyWord) }).ToListAsync();
    var bothWords = Ranks(r => Has(r, "steel") && Has(r, "widget"));
    CollectionAssert.AreEqual(bothWords, ranked.Take(bothWords.Count).Select(x => x.Rank).ToList(), "both words outrank one");
    Assert.IsTrue(ranked[0].Score > ranked[^1].Score, "and the score says so");
    Assert.AreEqual(Ranks(r => Has(r, "steel") || Has(r, "widget")).Count, ranked.Count);

    WriteTestTitle("by name");
    var byName = await session.ExecuteValuesAsync<int>(session.From("Product")
      .Where(row => row.TextMatches(QueryMaker.Parameter("q", typeof(string))))
      .OrderBy(row => row.Member("Rank")).Select(row => [row.Member("Rank")]), "brass hinge");
    CollectionAssert.AreEqual(Ranks(r => Has(r, "brass") && Has(r, "hinge")), byName.Select(v => v!).ToList());

    WriteTestTitle("refused: a match over a projection's row");
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      products.Select(p => new { p.Rank, p.Name }).Where(x => FullText.Matches(x, "widget")).CountAsync());
  }
}
