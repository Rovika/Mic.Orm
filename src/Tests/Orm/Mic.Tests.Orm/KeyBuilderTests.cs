using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// Keys and indexes: both forms — on a member, where the key is that member, and on the entity, where it names its
// members as text. The entity form is the only place a member is referred to by string rather than by the compiler,
// which is why the diagnostics matter as much as the keys.
[TestClass]
public class KeyBuilderTests {
  [Index("LastName -DateCreated", IncludeMembers = "FirstName", Filter = "[LastName] IS NOT NULL")]
  private interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    string LastName { get; set; }
    string FirstName { get; set; }
    [Unique] string Email { get; set; }
    DateTime DateCreated { get; set; }
  }

  [PrimaryKey("TripId,LegNo")]
  private interface ITripLeg {
    Guid TripId { get; set; }
    int LegNo { get; set; }
  }

  [PrimaryKey("Id", Clustered = true)]
  private interface IClusteredRow {
    Guid Id { get; set; }
  }

  private static EntityApp Build(params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module, "s");
    app.TryInit();
    return app;
  }

  private static EntityTypeInfo Entity(string name, params Type[] types) => Build(types).Model!.FindEntity(name)!;

  // ----- both forms of key, and everything either carries ---------------------------------------------------------------
  //
  // Eight methods over three tiny entities. All of it is one build, and the assertions are what cost nothing.
  [TestMethod]
  public void Key_DeclaredOnAMemberOrTheEntity() {
    var app = Build(typeof(IPerson), typeof(ITripLeg), typeof(IClusteredRow));
    Assert.IsTrue(app.Initialized, app.Report());
    var person = app.Model!.FindEntity("Person")!;

    // ----- on a MEMBER, the key is that member ------------------------------------------------------------------------
    var pk = person.PrimaryKey!;
    Assert.IsNotNull(pk);
    Assert.AreEqual(1, pk.Members.Count);
    Assert.AreEqual("Id", pk.Members[0].Member.Name);
    Assert.IsFalse(pk.IsComposite);

    // …and its members are MARKED, which is what later lets a record find its own identity without consulting the key.
    Assert.IsTrue(person.GetMember("Id").IsPrimaryKey);
    Assert.IsFalse(person.GetMember("Email").IsPrimaryKey);

    // ----- on the ENTITY, it names its members as text ------------------------------------------------------------------
    var leg = app.Model.FindEntity("TripLeg")!;
    Assert.IsTrue(leg.PrimaryKey!.IsComposite);
    CollectionAssert.AreEqual(new[] { "TripId", "LegNo" },
      leg.PrimaryKey.Members.Select(m => m.Member.Name).ToArray());
    Assert.IsTrue(leg.GetMember("TripId").IsPrimaryKey);
    Assert.IsTrue(leg.GetMember("LegNo").IsPrimaryKey);

    // …and carries CLUSTERED where it was asked for.
    Assert.IsTrue(app.Model.FindEntity("ClusteredRow")!.PrimaryKey!.Flags.HasFlag(KeyFlags.Clustered));

    // ----- [Unique] on a member is a unique INDEX --------------------------------------------------------------------------
    var unique = person.Keys.Single(k => k.Flags.HasFlag(KeyFlags.Unique) && !k.IsPrimaryKey);
    Assert.AreEqual("Email", unique.Members[0].Member.Name);
    Assert.IsTrue(unique.IsUnique);

    // ----- while an entity-level index carries its ORDER, its INCLUDES and its FILTER ---------------------------------------
    var index = person.Keys.Single(k => k.Index != null);
    CollectionAssert.AreEqual(new[] { "LastName", "DateCreated" }, index.Members.Select(m => m.Member.Name).ToArray());
    Assert.IsFalse(index.Members[0].Descending);
    Assert.IsTrue(index.Members[1].Descending);            // the '-' marks the member after it
    Assert.AreEqual("FirstName", index.Index!.IncludedMembers.Single().Name);
    Assert.AreEqual("[LastName] IS NOT NULL", index.Index.Filter);

    // ----- and every one of them is NAMED by the policy ------------------------------------------------------------------
    Assert.AreEqual("PK_Person_Id", pk.Name);
    Assert.IsTrue(person.Keys.Any(k => k.Name == "UQ_Person_Email"));
    Assert.IsTrue(person.Keys.Any(k => k.Name == "IX_Person_LastName_DateCreated"));

    // …which an application can replace.
    var loud = new EntityApp("App", new Version(1, 0), new EntityAppSettings { NamingPolicy = new LoudPolicy() });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IClusteredRow));
    loud.AddModule(module, "s");
    loud.TryInit();
    Assert.AreEqual("pk_clusteredrow", loud.Model!.FindEntity("ClusteredRow")!.PrimaryKey!.Name);
  }

  private sealed class LoudPolicy : NamingPolicy {
    public override string GetKeyName(string entityName, KeyFlags flags, IEnumerable<string> memberNames) =>
      base.GetKeyName(entityName, flags, []).ToLowerInvariant();
  }

  // ----- what is reported -------------------------------------------------------------------------------------------

  [PrimaryKey("Id")]
  private interface ITwoPrimaryKeys {
    [PrimaryKey] Guid Id { get; set; }
  }

  [PrimaryKey("NoSuchMember")]
  private interface IUnknownKeyMember {
    Guid Id { get; set; }
  }

  private interface IPrimaryKeyOnMemberWithSpec {
    [PrimaryKey("Id")] Guid Id { get; set; }
  }

  private interface INoPrimaryKeySpec {
    Guid Id { get; set; }
  }

  private interface ISameIndexTwice {
    [PrimaryKey] Guid Id { get; set; }
    [Unique][Index(Unique = true)] string Email { get; set; }
  }

  [Index("LastName", IncludeMembers = "-FirstName")]
  private interface IDirectionOnInclude {
    [PrimaryKey] Guid Id { get; set; }
    string LastName { get; set; }
    string FirstName { get; set; }
  }

  // The entity form is the only place a member is referred to by STRING rather than by the compiler, so these messages
  // are the whole safety net under it — and a database can never produce one of them.
  [TestMethod]
  public void Key_ThatCannotMeanWhatItSaysIsReported() {
    // ----- a primary key stated twice ---------------------------------------------------------------------------------
    var two = Build(typeof(ITwoPrimaryKeys));
    Assert.IsFalse(two.Initialized);
    Assert.IsTrue(two.Diagnostics().Any(d => d.Message.Contains("more than one primary key")));

    // ----- a member name that is a TYPO, named alongside the entity ----------------------------------------------------
    //
    // The payoff of the string form having real diagnostics: a typo here is otherwise a silently wrong key.
    var unknown = Build(typeof(IUnknownKeyMember));
    Assert.IsFalse(unknown.Initialized);
    Assert.IsTrue(unknown.Diagnostics().Any(d =>
      d.Message.Contains("NoSuchMember") && d.Message.Contains("UnknownKeyMember")));

    // ----- the two forms MIXED -------------------------------------------------------------------------------------------
    var mixed = Build(typeof(IPrimaryKeyOnMemberWithSpec));
    Assert.IsFalse(mixed.Initialized);
    Assert.IsTrue(mixed.Diagnostics().Any(d => d.Message.Contains("put it on the entity")));

    // ----- the same key stated twice ----------------------------------------------------------------------------------------
    //
    // [Unique] beside [Index(Unique = true)] over one member says one thing twice — reported, never resolved by keeping
    // whichever came first.
    var twice = Build(typeof(ISameIndexTwice));
    Assert.IsFalse(twice.Initialized);
    Assert.IsTrue(twice.Diagnostics().Any(d => d.Message.Contains("same key twice")));

    // ----- and a DIRECTION where an index cannot order by one -------------------------------------------------------------
    //
    // An included member is carried in the index leaf, not ordered by — so a '-' there is a misunderstanding rather than
    // something to ignore.
    var directed = Build(typeof(IDirectionOnInclude));
    Assert.IsFalse(directed.Initialized);
    Assert.IsTrue(directed.Diagnostics().Any(d => d.Message.Contains("not meaningful")));

    // ----- while an entity-level [PrimaryKey] naming NOTHING is simply not a key ---------------------------------------------
    //
    // It names nothing, so there is nothing to build and nothing to complain about.
    var nameless = Build(typeof(INoPrimaryKeySpec));
    Assert.IsNull(nameless.Model!.FindEntity("NoPrimaryKeySpec")!.PrimaryKey);
  }
}
