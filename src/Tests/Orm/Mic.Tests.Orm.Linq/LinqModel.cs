using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Linq;

// ONE MODEL FOR THE WHOLE QUERY SUITE, and one SEEDING of it per vendor. Everything a query test needs is here and
// nothing else is: a reference to walk, a nullable to test for null, an enum, a decimal, a date, a string, and an
// integer with a KNOWN ORDER so that a page can be asserted by value rather than by count.
//
// A MODEL OF ITS OWN rather than the DML suite's, and the reason is what each suite is about: that one's model is
// shaped for save ordering — a self-reference, a link entity, a type per literal spelling — and grew that way because
// those are the cases writing has. Reading has different ones, and a query test that had to explain which of an
// unrelated model's oddities it was leaning on would be the worse for it.
//
// SEEDED, AND NEVER CHANGED. Every test here reads; nothing writes. That is what lets one schema and one seeding
// serve a whole vendor's run, and it is a rule to keep — a test that needs its own rows should make an entity of its
// own rather than disturb these.
public static class Linq {
  public enum ProductStatus { Draft = 0, Live = 1, Retired = 2 }

  public interface ICategory {
    [PrimaryKey] Guid Id { get; set; }

    /// <summary>The same relation as <see cref="Products"/>, declared as a QUERY — and declared HERE, ahead of the
    /// other members, so that it takes a position and no storage slot and everything after it has the two apart.</summary>
    [OneToMany("Category")] IQueryable<IProduct> ProductQuery { get; }

    [Size(40)] string Name { get; set; }

    /// <summary>The list this suite asks questions OF — a one-to-many, which is a correlated sub-query and never a
    /// join.</summary>
    /// <remarks>NAMED EXPLICITLY, because `IProduct` points at a category TWICE — once as its own and once as its
    /// secondary — so which of the two backs this list is a question the model cannot answer by itself.</remarks>
    [OneToMany("Category")] IList<IProduct> Products { get; }
  }

  /// <summary>A document a product carries: what the JSON functions read into. `Weight` declares its JSON name, so
  /// the path a property walk writes is the declared one.</summary>
  public class ProductSpec {
    [System.Text.Json.Serialization.JsonPropertyName("weight")] public double Weight { get; set; }
    public string? Origin { get; set; }
    public bool Fragile { get; set; }
    public DateOnly Since { get; set; }
    public ProductDims Dims { get; set; } = new();
    public List<SpecItem> Items { get; set; } = [];
    /// <summary>An array of SCALARS inside the document — the rows a spread over it reads are the values.</summary>
    public List<string> Tags { get; set; } = [];
  }

  public class ProductDims {
    public int Height { get; set; }
  }

  /// <summary>An element of an array INSIDE the document — the rows a spread over `Spec.Items` reads.</summary>
  public class SpecItem {
    public string Sku { get; set; } = "";
    public int Qty { get; set; }
  }

  /// <summary>An element of an array that IS the column — the rows a spread over `Notes` reads.</summary>
  public class ProductNote {
    public string Text { get; set; } = "";
    public int Stars { get; set; }
  }

  [Index("Name,Description", IndexType = DbIndexType.FullText)]
  public interface IProduct {
    [PrimaryKey] Guid Id { get; set; }

    /// <summary>The many-to-many of <see cref="Tags"/>, declared as a QUERY — in the middle of the members, for the
    /// same reason.</summary>
    [ManyToMany(typeof(IProductTag))] IQueryable<ITag> TagQuery { get; }

    /// <summary>1 to <see cref="LinqSeed.ProductCount"/>, unique and gapless — so an ordered page can be asserted as
    /// the exact ranks it should contain rather than as a count that any page would satisfy.</summary>
    int Rank { get; set; }

    [Size(40)] string Name { get; set; }

    /// <summary>A few words of text, searched by the FULL-TEXT INDEX over it and the name — see `LinqSeed.DescriptionOf`.
    /// </summary>
    [Size(200)] string Description { get; set; }
    ICategory Category { get; set; }
    [Column(Precision = 9, Scale = 2)] decimal Price { get; set; }
    ProductStatus Status { get; set; }
    DateOnly ReleasedOn { get; set; }

    /// <summary>A JSON column, null on every tenth product.</summary>
    [Json] ProductSpec? Spec { get; set; }

    /// <summary>A JSON column that IS an array: one note on every fifth product, none elsewhere.</summary>
    [Json] List<ProductNote>? Notes { get; set; }

    /// <summary>Null for a product still sold — the nullable an `IS NULL` test needs.</summary>
    DateOnly? RetiredOn { get; set; }

    /// <summary>A SECOND category, and NULLABLE — which is what makes reaching through it a LEFT OUTER join. Set for
    /// a quarter of the products, so an inner join through it would drop the other three quarters and a test can see
    /// the difference by counting.</summary>
    ICategory? Secondary { get; set; }

    /// <summary>A nullable SELF-reference: the table joins to itself under a second alias, and `Substitute.Category`
    /// is two hops where the first is optional and the second is not — which is where outerness has to be inherited
    /// or the query quietly becomes "products that HAVE a substitute".</summary>
    IProduct? Substitute { get; set; }

    /// <summary>A MANY-TO-MANY: the sub-query sources from the link and the tag is one join further out.</summary>
    [ManyToMany(typeof(IProductTag))] IList<ITag> Tags { get; }
  }

  public interface ITag {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
  }

  /// <summary>The LINK, and it stays an ordinary entity — which is the point of MIC not hiding one: it is where data
  /// about the association itself would go.</summary>
  public interface IProductTag {
    [PrimaryKey] Guid Id { get; set; }
    IProduct Product { get; set; }
    ITag Tag { get; set; }
  }

  /// <summary>A HIERARCHY, which is what a recursive query exists to walk — an entity of its own rather than a new
  /// shape for the products every other test counts. See <see cref="LinqSeed.FolderTree"/> for its known shape.
  /// </summary>
  public interface IFolder {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
    IFolder? Parent { get; set; }
    [OneToMany("Parent")] IList<IFolder> Children { get; }
  }
}

public class LinqApp : EntityApp {
  public LinqApp(string schema)
    : base("LinqTests", new Version(1, 0), new EntityAppSettings { DefaultSchema = schema }) {
    var module = new EntityModule("Linq", new Version(1, 0));
    module.RegisterEntities(typeof(Linq.ICategory), typeof(Linq.IProduct),
      typeof(Linq.ITag), typeof(Linq.IProductTag), typeof(Linq.IFolder));
    AddModule(module);
  }
}

// THE ROWS, DECIDED IN CODE RATHER THAN WRITTEN OUT. Everything about a product is a function of its rank, so a test
// can say what it expects — "ranks 11 to 15", "the 24 that are Live" — without a table of literals to keep in step.
public static class LinqSeed {
  public const int ProductCount = 60;
  public static readonly string[] Categories = ["Tools", "Paint", "Timber"];

  public static string CategoryOf(int rank) => Categories[rank % Categories.Length];

  /// <summary>The document of a product: null on every tenth, the weight half the rank, the origin its category,
  /// fragile every third, the height the rank's last digit.</summary>
  public static Linq.ProductSpec? SpecOf(int rank) => rank % 10 == 0 ? null : new Linq.ProductSpec {
    Weight = rank * 0.5,
    Origin = CategoryOf(rank),
    Fragile = rank % 3 == 0,
    Since = ReleasedOn(rank),
    Dims = new Linq.ProductDims { Height = rank % 10 },
    // The rank modulo four items, the i-th with quantity i + 1: `S12-0`, `S12-1`, `S12-2` for rank 12, none for 8.
    Items = [.. Enumerable.Range(0, rank % 4).Select(i => new Linq.SpecItem { Sku = $"S{rank}-{i}", Qty = i + 1 })],
    // The rank modulo three tags: none, `bulk`, or `bulk` and `fragile`.
    Tags = [.. Enumerable.Range(0, rank % 3).Select(i => i == 0 ? "bulk" : "fragile")],
  };

  /// <summary>One note on every fifth product, starred by the rank's remainder by three; null elsewhere.</summary>
  public static List<Linq.ProductNote>? NotesOf(int rank) =>
    rank % 5 == 0 ? [new Linq.ProductNote { Text = $"note {rank}", Stars = rank % 3 }] : null;
  public static decimal PriceOf(int rank) => 5m + rank * 2.5m;

  public static readonly string[] Materials = ["steel", "brass", "oak", "glass"];
  public static readonly string[] Things = ["widget", "bracket", "hinge"];
  public static readonly string[] Uses = ["kitchens", "gardens", "workshops", "boats", "bicycles"];

  /// <summary>"A steel widget for kitchens" — three word lists cycled at three different periods, so a word is in a
  /// known fraction of the products and a pair of words in the product of two fractions.</summary>
  public static string DescriptionOf(int rank) =>
    $"A {Materials[rank % Materials.Length]} {Things[rank % Things.Length]} for {Uses[rank % Uses.Length]}";
  public static Linq.ProductStatus StatusOf(int rank) => (Linq.ProductStatus)(rank % 3);
  public static DateOnly ReleasedOn(int rank) => new DateOnly(2020, 1, 1).AddDays(rank * 10);

  /// <summary>Retired only where the status says so, so the nullable and the enum agree — a query may use either.
  /// </summary>
  public static DateOnly? RetiredOn(int rank) =>
    StatusOf(rank) == Linq.ProductStatus.Retired ? ReleasedOn(rank).AddYears(2) : null;

  /// <summary>A second category for a QUARTER of the products, and null for the rest.</summary>
  public static string? SecondaryOf(int rank) =>
    rank % 4 == 0 ? Categories[(rank + 1) % Categories.Length] : null;

  /// <summary>The rank of the product this one substitutes, or null — every fifth, pointing one rank back.</summary>
  public static int? SubstituteOf(int rank) => rank > 1 && rank % 5 == 0 ? rank - 1 : null;

  public static readonly string[] Tags = ["Sale", "New", "Clearance"];

  /// <summary>The tags of a product: none, one or two, so a many-to-many is asked of every cardinality.</summary>
  public static string[] TagsOf(int rank) => (rank % 6) switch {
    0 => [],
    1 => [Tags[0]],
    2 => [Tags[1]],
    3 => [Tags[2]],
    4 => [Tags[0], Tags[1]],
    _ => [Tags[1], Tags[2]],
  };

  public static async Task FillAsync(EntityApp app, OperationContext context) {
    var session = context.OpenSession();
    var categories = new Dictionary<string, Linq.ICategory>();
    foreach (var name in Categories) {
      var category = session.NewEntity<Linq.ICategory>();
      category.Id = Guid.CreateVersion7();
      category.Name = name;
      categories[name] = category;
    }

    var products = new Dictionary<int, Linq.IProduct>();
    for (var rank = 1; rank <= ProductCount; rank++) {
      var product = session.NewEntity<Linq.IProduct>();
      product.Id = Guid.CreateVersion7();
      product.Rank = rank;
      product.Name = $"Product {rank:D2}";
      product.Description = DescriptionOf(rank);
      product.Category = categories[CategoryOf(rank)];
      product.Price = PriceOf(rank);
      product.Status = StatusOf(rank);
      product.ReleasedOn = ReleasedOn(rank);
      product.RetiredOn = RetiredOn(rank);
      product.Spec = SpecOf(rank);
      product.Notes = NotesOf(rank);
      if (SecondaryOf(rank) is { } secondary)
        product.Secondary = categories[secondary];
      products[rank] = product;
    }

    // THE SELF-REFERENCE IN A SECOND PASS, because a product can only point at one that has been made. The save sorts
    // the rows among themselves afterwards — a type that references itself is exactly the case that needs it.
    foreach (var (rank, product) in products)
      if (SubstituteOf(rank) is { } substitute)
        product.Substitute = products[substitute];

    var tags = new Dictionary<string, Linq.ITag>();
    foreach (var name in Tags) {
      var tag = session.NewEntity<Linq.ITag>();
      tag.Id = Guid.CreateVersion7();
      tag.Name = name;
      tags[name] = tag;
    }

    // THE LINK ROWS ARE MADE BY HAND, which is what a link entity being an ordinary entity means.
    foreach (var (rank, product) in products)
      foreach (var name in TagsOf(rank)) {
        var link = session.NewEntity<Linq.IProductTag>();
        link.Id = Guid.CreateVersion7();
        link.Product = product;
        link.Tag = tags[name];
      }

    var folders = new Dictionary<string, Linq.IFolder>();
    foreach (var (name, parent) in FolderTree.Concat(FolderLoop.Select(n => (n, (string?)null)))) {
      var folder = session.NewEntity<Linq.IFolder>();
      folder.Id = Guid.CreateVersion7();
      folder.Name = name;
      if (parent != null)
        folder.Parent = folders[parent];
      folders[name] = folder;
    }

    await session.SaveChangesAsync();

    // THE CYCLE IS CLOSED ONE ROW AT A TIME, in saves of their own: two rows pointing at each other cannot be
    // INSERTED in any order a foreign key accepts, and each update here changes one row whose target already exists.
    folders[FolderLoop[0]].Parent = folders[FolderLoop[1]];
    await session.SaveChangesAsync();
    folders[FolderLoop[1]].Parent = folders[FolderLoop[0]];
    await session.SaveChangesAsync();
  }

  /// <summary>A tree four levels below its root, each folder with its parent — parents first, so one pass builds it.
  /// </summary>
  public static readonly (string Name, string? Parent)[] FolderTree = [
    ("Root", null),
    ("A", "Root"), ("B", "Root"),
    ("A1", "A"), ("A2", "A"), ("B1", "B"),
    ("A1x", "A1"), ("A1y", "A1"),
    ("Deep", "A1x"),
  ];

  /// <summary>Two folders that are each other's parent — data a recursive query must not run away over.</summary>
  public static readonly string[] FolderLoop = ["Loop1", "Loop2"];

  /// <summary>The names under a folder, itself included, to any depth — what walking DOWN should find.</summary>
  public static List<string> Under(string name, int maxDepth = int.MaxValue) {
    var names = new List<string> { name };
    if (maxDepth > 0)
      foreach (var (child, parent) in FolderTree)
        if (parent == name)
          names.AddRange(Under(child, maxDepth - 1));
    return names;
  }

  /// <summary>The names from a folder up to its root, itself included — what walking UP should find.</summary>
  public static List<string> Above(string name) {
    var names = new List<string>();
    for (string? at = name; at != null; at = FolderTree.First(f => f.Name == at).Parent)
      names.Add(at);
    return names;
  }
}
