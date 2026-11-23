using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using Mic.Tests.Orm.Db;
using static Mic.Linq.AsyncQueryableExtensions;
using static Mic.Linq.CteQueryableExtensions;

namespace Mic.Tests.Orm.Linq;

// FILTERS AN EXTENSION ATTACHES TO AN ENTITY: applied where the engine resolves a source and not where a query is
// written, in the ON clause of a joined table so an outer join stays outer, and what a filtered AND cached entity has
// to declare about its blocks.
public abstract partial class LinqTests {
  const string TenantKey = "tenant-status";

  /// <summary>A filter of the shape an extension contributes: fixed condition, value from the context.</summary>
  private static QueryNode StatusFilter(QueryNode row) =>
    row.Member("Status").Equal(QueryMaker.Parameter(TenantKey, typeof(Linq.ProductStatus),
      callCtx => ((OperationContext)callCtx.OperationContext!).GetValue<object>(TenantKey)));

  const int FixedRankLimit = 30;

  /// <summary>The OTHER shape an extension contributes: a condition that is the same for every caller, which is what
  /// a soft delete's `DeletedOn IS NULL` is. It reads nothing from the context and so owes no discriminator.</summary>
  private static QueryNode FixedRankFilter(QueryNode row) =>
    row.Member("Rank").LessOrEqual(QueryMaker.Constant(FixedRankLimit));

  [TestMethod]
  public async Task Filter_AppliedWhereTheSourceIsResolved() {
    var app = NewApp(a => a.AddFilter(typeof(Linq.IProduct), StatusFilter));
    var database = app.DataAccess.FindDatabase()!;

    IEntitySession For(Linq.ProductStatus status) {
      var context = NewContext(app);
      context.SetValue(TenantKey, status);
      return context.OpenSession();
    }

    WriteTestTitle("ONE STATEMENT, TWO ANSWERS — the filter's shape is fixed and only its value varies");
    var live = For(Linq.ProductStatus.Live);
    var draft = For(Linq.ProductStatus.Draft);
    var query = live.From("Product");
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Order().ToList(),
      RanksOf(await live.ExecuteQueryAsync(query)).Order().ToList());
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Draft).Order().ToList(),
      RanksOf(await draft.ExecuteQueryAsync(draft.From("Product"))).Order().ToList());
    // THE CACHE KEY OWES NOTHING TO EXTENSIONS: the filter is part of the model, so both tenants found one statement.
    Assert.AreEqual(1, database.QueryCache.Misses, "one translation served both");

    WriteTestTitle("it narrows what a caller wrote, and takes none of the caller's arguments");
    var dear = await live.ExecuteQueryAsync(live.From("Product")
      .Where(row => row.Member("Price").Greater(QueryMaker.Parameter("min", typeof(decimal)))), 100m);
    CollectionAssert.AreEqual(
      Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live && LinqSeed.PriceOf(r) > 100m).Order().ToList(),
      RanksOf(dear).Order().ToList());

    WriteTestTitle("and it reaches a source the caller never named — the sub-query behind a list");
    var withLive = await live.ExecuteQueryAsync(live.From("Category")
      .Where(row => row.Member("Products").Any(p => p.Member("Rank").Greater(QueryMaker.Constant(58)))));
    // Only ranks 59 and 60 pass the condition, and only one of them is Live — so the filter inside the sub-query is
    // the difference between one category and two.
    var expected = Ranks(r => r > 58 && LinqSeed.StatusOf(r) == Linq.ProductStatus.Live)
      .Select(LinqSeed.CategoryOf).Distinct().Order().ToList();
    CollectionAssert.AreEqual(expected, NamesOf(withLive));

    WriteTestTitle("TWO FILTERS ACCUMULATE and are ANDed — neither extension knows the other is there");
    // Multi-tenancy and soft delete on one entity is the ordinary case: each declares its own condition, and a row
    // has to satisfy both to be visible. A second declaration adds rather than replaces.
    var twiceFiltered = NewApp(a => {
      a.AddFilter(typeof(Linq.IProduct), StatusFilter);
      a.AddFilter(typeof(Linq.IProduct), FixedRankFilter);
    });
    var context = NewContext(twiceFiltered);
    context.SetValue(TenantKey, Linq.ProductStatus.Live);
    var narrowed = context.OpenSession();
    CollectionAssert.AreEqual(
      Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live && r <= FixedRankLimit).Order().ToList(),
      RanksOf(await narrowed.ExecuteQueryAsync(narrowed.From("Product"))).Order().ToList());
  }

  [TestMethod]
  public async Task JoinedTableFilter_GoesInTheOnClause() {
    // THE CASE THAT SAYS WHERE A FILTER GOES. `Secondary` is nullable, so it is a LEFT OUTER JOIN, and three quarters
    // of the products have none. A filter on the JOINED entity put in the WHERE would discard every one of those —
    // turning the outer join inner and dropping rows nobody filtered out. In the ON, they stay.
    var app = NewApp(a => a.AddFilter(typeof(Linq.ICategory),
      row => row.Member("Name").NotEqual(QueryMaker.Constant("Timber"))));
    var session = NewContext(app).OpenSession();

    WriteTestTitle("ordering through a nullable reference to a filtered table");
    var products = await session.ExecuteQueryAsync(session.From("Product")
      .OrderBy(row => row.Member("Secondary").Member("Name"))
      .OrderBy(row => row.Member("Rank")));
    Assert.AreEqual(LinqSeed.ProductCount, products.Count, "every product, filtered secondary or not");

    WriteTestTitle("and the filter still narrows what it should");
    var timberSecondary = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Secondary").Member("Name").Equal(QueryMaker.Constant("Timber"))));
    Assert.AreEqual(0, timberSecondary.Count, "the filter hides that category wherever it is reached");

    var paintSecondary = await session.ExecuteQueryAsync(session.From("Product")
      .Where(row => row.Member("Secondary").Member("Name").Equal(QueryMaker.Constant("Paint"))));
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.SecondaryOf(r) == "Paint").Order().ToList(),
      RanksOf(paintSecondary).Order().ToList());
  }

  [TestMethod]
  public void FilteredCachedEntity_MustSayHowItsBlocksDiffer() {
    // ONE OBLIGATION, NOT TWO. A block of a filtered entity was filled under one tenant's filter, and there is no
    // source resolution to filter at when it is served — so without a discriminator, one tenant would be handed
    // another's rows. Refused at STARTUP rather than leaked at run time, whichever order the pair is declared in.
    StringAssert.Contains(RefusedApp(a => {
      a.AddFilter(typeof(Linq.IProduct), StatusFilter);
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    }), "discriminator");

    StringAssert.Contains(RefusedApp(a => {
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
      a.AddFilter(typeof(Linq.IProduct), StatusFilter);
    }), "discriminator");

    // WITH ONE, it is allowed — and the blocks are then per discriminator.
    var app = NewApp(a => {
      a.AddFilter(typeof(Linq.IProduct), StatusFilter,
        context => ((OperationContext)context!).GetValue<object>(TenantKey)?.ToString() ?? "");
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    });
    Assert.IsNotNull(app.DataAccess.FindDatabase());

    // A FILTER'S SHAPE IS VALIDATED WHEN THE MODEL IS MAPPED to a database, where its columns are known too, rather
    // than on the first query that touched the entity — and what a failed mapping produces is a diagnostic, not an
    // exception. The entity model itself builds: a member name is a question for the database.
    var misspelled = new LinqApp(Db.Schema);
    misspelled.AddFilter(typeof(Linq.IProduct), row => row.Member("Statuss").Equal(QueryMaker.Constant(1)));
    misspelled.Init();
    var mapping = new DbModelBuilder(misspelled, misspelled.Model!, Db.Settings).Build();
    Assert.IsTrue(mapping.BuildReport.HasErrors, "the mapping should have been refused");
    StringAssert.Contains(mapping.BuildReport.Format(), "Statuss");

    // AND AN ENTITY THE APPLICATION DOES NOT HAVE is the same kind of fault, now that the builder is what resolves
    // the type rather than the declaration call.
    StringAssert.Contains(RefusedApp(a => a.SetCached(CacheType.Server, [typeof(IDisposable)])), "not an entity");

    // THE OBLIGATION IS THE VARYING FILTER'S, NOT THE ENTITY'S. A condition that reads nothing from the context is
    // the same for every caller, so one block serves them all and there is nothing to tell apart.
    var fixedAndCached = NewApp(a => {
      a.AddFilter(typeof(Linq.IProduct), FixedRankFilter);
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    });
    Assert.IsNotNull(fixedAndCached.DataAccess.FindDatabase());

    // AND A DISCRIMINATOR ON SUCH A FILTER IS A CONTRADICTION — refused rather than ignored, since it claims a
    // variation the condition does not have and would fragment the cache for nothing.
    StringAssert.Contains(RefusedApp(a => a.AddFilter(typeof(Linq.IProduct), FixedRankFilter, _ => "whatever")),
      "reads nothing from the operation context");

    // TWO FILTERS, ONE VARYING: the varying one owes a discriminator and the fixed one does not, so this is legal —
    // and it is the multi-tenancy-plus-soft-delete arrangement that made the list necessary.
    var mixed = NewApp(a => {
      a.AddFilter(typeof(Linq.IProduct), FixedRankFilter);
      a.AddFilter(typeof(Linq.IProduct), StatusFilter,
        context => ((OperationContext)context!).GetValue<object>(TenantKey)?.ToString() ?? "");
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    });
    Assert.IsNotNull(mixed.DataAccess.FindDatabase());

    // …and dropping the varying one's discriminator is still refused, with the fixed one beside it.
    StringAssert.Contains(RefusedApp(a => {
      a.AddFilter(typeof(Linq.IProduct), FixedRankFilter);
      a.AddFilter(typeof(Linq.IProduct), StatusFilter);
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    }), "discriminator");
  }

  [TestMethod]
  public async Task FilteredCachedEntity_KeepsOneBlockPerDiscriminator() {
    var app = NewApp(a => {
      a.AddFilter(typeof(Linq.IProduct), StatusFilter,
        context => ((OperationContext)context!).GetValue<object>(TenantKey)?.ToString() ?? "");
      a.SetCached(CacheType.Server, [typeof(Linq.IProduct)]);
    });
    var cache = app.DataAccess.FindDatabase()!.EntityCache;

    IEntitySession For(Linq.ProductStatus status) {
      var context = NewContext(app);
      context.SetValue(TenantKey, status);
      return context.OpenSession();
    }

    // TWO CONTEXTS, TWO BLOCKS — and the second must not be served the first's rows, which is the whole point of the
    // obligation the previous test enforces.
    var live = For(Linq.ProductStatus.Live);
    var draft = For(Linq.ProductStatus.Draft);
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Live).Order().ToList(),
      RanksOf(await live.ExecuteQueryAsync(live.From("Product"))).Order().ToList());
    CollectionAssert.AreEqual(Ranks(r => LinqSeed.StatusOf(r) == Linq.ProductStatus.Draft).Order().ToList(),
      RanksOf(await draft.ExecuteQueryAsync(draft.From("Product"))).Order().ToList());
    Assert.AreEqual(2, cache.BlockCount, "one block per discriminator, not one per entity");
  }
}

/// <summary>An application's own filter is a three-line extension: this one attaches a condition to ONE entity, which
/// is what the filter tests declare through <see cref="FilterTestExtensions.AddFilter"/>.</summary>
public sealed class OneEntityFilterExtension(Type entityType, Func<RowNode, QueryNode> build, Func<object?, string>? discriminator)
    : AppExtension {
  public override void OnEntityBuilt(EntityTypeInfo entity) {
    if (entity.InterfaceType == entityType)
      entity.AddFilter(QueryMaker.MakeFilter(entity, build), discriminator);
  }
}

public static class FilterTestExtensions {
  /// <summary>Attach a filter to one entity of the application — as an extension, which is the only way in.</summary>
  public static void AddFilter(this EntityApp app, Type entityType, Func<RowNode, QueryNode> build,
      Func<object?, string>? cacheDiscriminator = null) =>
    app.AddExtension(new OneEntityFilterExtension(entityType, build, cacheDiscriminator));
}
