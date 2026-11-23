using Mic.Orm;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE FIRST STEP WITH AN OBJECT IN IT. Everything before this describes; here a model becomes a class an application
// can hold, with values in it.
//
// The seam under test is narrow on purpose: a property reads and writes the record at the member's slot, and that is
// all an entity class does today. What a value MEANS — a stub, an unloaded member, a change worth saving — belongs to
// the session and is not here yet.
[TestClass]
public class EntityRuntimeTests {
  public interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    [Size(60)] string City { get; set; }
  }

  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    string? MiddleName { get; set; }
    int Rank { get; set; }
    decimal Salary { get; set; }
    DayOfWeek PayDay { get; set; }
    DateTime? RetiredOn { get; set; }
    byte[]? Photo { get; set; }
    IAddress Address { get; set; }
  }

  sealed class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) =>
      RegisterEntities(typeof(IPerson), typeof(IAddress));
  }

  static EntityAppModel Init() {
    var app = new EntityApp("People", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new PeopleModule());
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    return app.Model!;
  }

  static readonly EntityAppModel Model = Init();
  static EntityTypeInfo Person => Model.FindEntity("Person")!;
  static EntityTypeInfo Address => Model.FindEntity("Address")!;

  static readonly EntityClassFactory Factory = new("Mic.Orm.Entities.Test.1");

  static IPerson NewPerson() => (IPerson)(object)Factory.CreateInstance(Person);

  // ----- the emitted class ---------------------------------------------------------------------------------------------

  [TestMethod]
  public void EntityInterface_BecomesAClassEmittedOnce() {
    var person = NewPerson();
    Assert.IsInstanceOfType<IPerson>(person);
    Assert.IsInstanceOfType<EntityBase>(person);
    StringAssert.StartsWith(person.GetType().Name, "Person_Emitted",
      "the class is named for the ENTITY, which is what the rest of MIC calls it");

    // ----- emitted ONCE and kept ------------------------------------------------------------------------------------
    //
    // A model is built once and every instance of an entity shares its class.
    Assert.AreSame(Factory.GetEntityClass(Person), Factory.GetEntityClass(Person));
    Assert.AreNotSame(Factory.GetEntityClass(Person), Factory.GetEntityClass(Address));

    // ----- and TWO models in one process do not collide ---------------------------------------------------------------
    //
    // One process can hold several entity models — two applications hosted together, or one model connected to two
    // databases. Nothing FAILS if their dynamic assemblies share a name; the types stay distinct and no exception is
    // thrown. What breaks is naming: two different types with one AssemblyQualifiedName, and two assemblies a stack
    // trace cannot tell apart.
    var second = new EntityClassFactory("Mic.Orm.Entities.Test.2");
    var mine = Factory.GetEntityClass(Person);
    var theirs = second.GetEntityClass(Person);

    Assert.AreNotSame(mine, theirs, "two factories must not share a class");
    Assert.AreNotEqual(mine.Assembly.GetName().Name, theirs.Assembly.GetName().Name,
      "two factories emitted into assemblies of the same name — their types now share an AssemblyQualifiedName");
    Assert.AreNotEqual(mine.AssemblyQualifiedName, theirs.AssemblyQualifiedName);
  }

  [TestMethod]
  public void EveryValueKind_RoundTripsThroughTheRecord() {
    var person = NewPerson();

    // ----- an unset member is its type's default, AND THE DEFAULT IS IN THE SLOT ------------------------------------
    //
    // Not merely what a reader coalesces to: the record's array starts as a copy of the entity's `InitialValues`, so
    // nothing is ever "unassigned". Asserted through the RAW accessor, which is the only way to tell the two apart —
    // the typed property answered 0 either way, which is exactly how the untyped read went on handing out nulls.
    var record = ((EntityBase)person).Record;
    Assert.IsNull(person.LastName);
    Assert.IsNull(person.RetiredOn);
    Assert.AreEqual(0, person.Rank);
    Assert.AreEqual(0, record.GetRawValue(Person.GetMember(nameof(IPerson.Rank))));
    Assert.AreEqual(0m, record.GetRawValue(Person.GetMember(nameof(IPerson.Salary))));

    // …and the UNTYPED read agrees, which is the one that used to throw: an application casting
    // `(int)record.GetValue(member)` met a null on a record nobody had touched.
    Assert.AreEqual(0, record.GetValue(Person.GetMember(nameof(IPerson.Rank))));
    Assert.AreEqual(DayOfWeek.Sunday, record.GetValue(Person.GetMember(nameof(IPerson.PayDay))));

    // ----- KEY members are the exception, and only below the accessors ------------------------------------------------
    //
    // A primary key and a foreign key start EMPTY, because `EntityKey.IsEmpty` and reference resolution ask the raw
    // slot whether a key has a value at all — and `Guid.Empty` is a value. Nothing above `GetRawValue` can be handed
    // that null: both reads below answer `Guid.Empty`.
    Assert.IsNull(record.GetRawValue(Person.GetMember(nameof(IPerson.Id))), "a key with no value");
    Assert.IsNull(record.GetRawValue(Person.GetMember("Address_Id")));

    Assert.AreEqual(Guid.Empty, person.Id);
    Assert.AreEqual(Guid.Empty, record.GetValue(Person.GetMember(nameof(IPerson.Id))));
    Assert.AreEqual(Guid.Empty, record.GetValue(Person.GetMember("Address_Id")),
      "reading an unassigned foreign-key COLUMN must answer, not throw");

    // ----- and a written one is IN THE RECORD -----------------------------------------------------------------------
    //
    // Not "the class happens to agree with itself" — the values are in the record, which is the claim.
    person.LastName = "Larsen";
    person.Rank = 3;

    Assert.AreEqual("Larsen", person.LastName);
    Assert.AreEqual(3, person.Rank);

    Assert.AreEqual("Larsen", record.GetValue(Person.GetMember(nameof(IPerson.LastName))));
    Assert.AreEqual(3, record.GetValue(Person.GetMember(nameof(IPerson.Rank))));

    // ----- whatever its type, through the boxing an object[] costs ---------------------------------------------------
    //
    // The cost has to be invisible, and this is what invisible means.
    var photo = new byte[] { 9, 8, 7 };
    var id = Guid.NewGuid();

    person.Id = id;
    person.Salary = 1234.56m;
    person.PayDay = DayOfWeek.Friday;
    person.RetiredOn = new DateTime(2031, 4, 1);
    person.Photo = photo;

    Assert.AreEqual(id, person.Id);
    Assert.AreEqual(1234.56m, person.Salary);
    Assert.AreEqual(DayOfWeek.Friday, person.PayDay);
    Assert.AreEqual(new DateTime(2031, 4, 1), person.RetiredOn);
    Assert.AreSame(photo, person.Photo);
  }

  // ----- references: the part that is NOT an ordinary slot ---------------------------------------------------------------

  static IAddress NewAddress() => (IAddress)(object)Factory.CreateInstance(Address);

  static object? Raw(object entity, string member) {
    var record = ((EntityBase)entity).Record;
    return record.GetRawValue(record.Entity.GetMember(member));
  }

  // ----- ONE pair of entities, the whole two-views-of-one-fact rule -----------------------------------------------------
  //
  // A reference and its foreign key are the same fact seen twice, and the four methods this was were the four ways
  // that can be written to.
  [TestMethod]
  public void Reference_AndForeignKeyAreTwoViewsOfOneFact() {
    // ----- assigning a reference COPIES the target's key -------------------------------------------------------------
    //
    // What makes two ordinary things free: `person.Address.Id` never loads anything because the id is already here,
    // and `person2.Address = person1.Address` moves an id rather than an object.
    var person = NewPerson();
    var address = NewAddress();
    address.Id = Guid.NewGuid();

    person.Address = address;

    Assert.AreSame(address, person.Address);
    Assert.AreEqual(address.Id, Raw(person, "Address_Id"),
      "the foreign key behind the association was not written");

    // ----- and CLEARING it clears the key -----------------------------------------------------------------------------
    person.Address = null!;

    Assert.IsNull(person.Address);
    Assert.IsNull(Raw(person, "Address_Id"));

    // ----- while writing the KEY invalidates the reference --------------------------------------------------------------
    //
    // Writing it behind the association's back would leave it stale. Clearing rather than re-resolving, because a
    // composite key is written one column at a time and nothing here can tell a finished key from a half-written one —
    // the decision belongs to whoever next wants the target.
    person.Address = address;
    var record = ((EntityBase)person).Record;
    record.SetValue(record.Entity.GetMember("Address_Id"), Guid.NewGuid());

    Assert.IsNull(person.Address, "the association still points at the old target");

    // ----- and what is not an entity at all cannot be assigned to one ------------------------------------------------
    var ex = Assert.ThrowsExactly<ArgumentException>(() =>
      record.SetValue(record.Entity.GetMember(nameof(IPerson.Address)), "not an entity"));
    StringAssert.Contains(ex.Message, "not a MIC entity");
  }

  // ----- the loaded mask ---------------------------------------------------------------------------------------------------

  [TestMethod]
  public void LoadedMask_DiffersFromTheModifiedOne() {
    // ----- a new record is fully LOADED ------------------------------------------------------------------------------
    //
    // Not a formality: a new entity has nothing to fetch, so every member holds real data — the default of its type
    // until somebody says otherwise. The first thing to read the mask finds the invariant already true.
    var person = NewPerson();
    var record = ((EntityBase)person).Record;
    foreach (var member in Person.Members)
      Assert.IsTrue(record.Loaded.IsSet(member.DataIndex), $"{member.Name} was not marked loaded");

    // ----- a write marks its member LOADED as well as MODIFIED --------------------------------------------------------
    var rank = Person.GetMember(nameof(IPerson.Rank)).DataIndex;
    record.Loaded.Clear();

    person.Rank = 7;

    Assert.IsTrue(record.Loaded.IsSet(rank));
    Assert.IsTrue(record.Modified.IsSet(rank));

    // ----- and one that CHANGES nothing is not a change to save -----------------------------------------------------------
    record.AcceptChanges();

    person.Rank = 7;

    Assert.IsTrue(record.Loaded.IsSet(rank));
    Assert.IsFalse(record.Modified.IsSet(rank));

    // ----- unless the record does NOT HOLD the member ------------------------------------------------------------------
    //
    // The slot of a member that was never loaded is empty, not the row's value: a write equal to it says nothing of
    // what the row holds, so it is a change, and saved.
    record.Loaded.Clear();

    person.Rank = 7;

    Assert.IsTrue(record.Loaded.IsSet(rank));
    Assert.IsTrue(record.Modified.IsSet(rank));
  }

  [TestMethod]
  public void Members_DoNotShareSlots() {
    // The emitter's own member index would collide across interfaces; the MODEL's numbering is what is used, and this
    // is the test that says so rather than the reasoning. Both numbers are checked, because a property reaches its
    // value through both — `Index` says which member, `DataIndex` says which slot — and a collision in either would
    // have one property reading another's value.
    var person = NewPerson();
    person.LastName = "one";
    person.MiddleName = "two";
    person.Rank = 3;

    Assert.AreEqual("one", person.LastName);
    Assert.AreEqual("two", person.MiddleName);
    Assert.AreEqual(3, person.Rank);

    CollectionAssert.AllItemsAreUnique(Person.Members.Select(m => m.Index).ToList());
    CollectionAssert.AllItemsAreUnique(Person.Members.Select(m => m.DataIndex).ToList());
  }

  // ----- the changed mask ------------------------------------------------------------------------------------------------

  [TestMethod]
  public void ModifiedMask_HoldsWhatAnUpdateCarries() {
    // ----- a new record has changed NOTHING ---------------------------------------------------------------------------
    var person = NewPerson();
    var record = ((EntityBase)person).Record;
    Assert.IsTrue(record.Modified.IsEmpty);

    // ----- a write marks ITS member and no other ----------------------------------------------------------------------
    person.Rank = 7;

    Assert.IsFalse(record.Modified.IsEmpty);
    CollectionAssert.AreEqual(new[] { nameof(IPerson.Rank) }, record.ModifiedMembers.Select(m => m.Name).ToList());

    // ----- ACCEPTING forgets the changes and keeps the values -----------------------------------------------------------
    person.LastName = "Larsen";
    record.AcceptChanges();

    Assert.IsTrue(record.Modified.IsEmpty);
    Assert.AreEqual("Larsen", person.LastName, "accepting is not undoing");
    Assert.AreEqual(7, person.Rank);

    // ----- and a write that CHANGES nothing marks nothing ----------------------------------------------------------------
    //
    // The mask decides which columns an UPDATE carries and keys the cached statement for it, so a write of the value
    // already there would put a column in the statement AND produce a second cached statement differing only by it.
    person.LastName = "Larsen";
    Assert.IsTrue(record.Modified.IsEmpty);

    person.LastName = "Someone Else";
    Assert.IsFalse(record.Modified.IsEmpty);

    // ----- while a second record of the same entity is untouched by any of it ----------------------------------------------
    var otherPerson = NewPerson();
    Assert.AreEqual(0, otherPerson.Rank);
    Assert.IsTrue(((EntityBase)otherPerson).Record.Modified.IsEmpty);
  }

  // ----- the mask itself ---------------------------------------------------------------------------------------------------

  static bool HasOverflow(EntityMemberMask mask) =>
    typeof(EntityMemberMask).GetField("_more", System.Reflection.BindingFlags.Instance
      | System.Reflection.BindingFlags.NonPublic)!.GetValue(mask) != null;

  // ----- the struct on its own -------------------------------------------------------------------------------------------
  //
  // Five methods over a type with four operations. Everything here uses a mask wider than one 64-bit word, which is
  // where an off-by-one would show, and the narrow case is asserted where it matters — the allocation.
  [TestMethod]
  public void Mask_RemembersHashesAndCopies() {
    var mask = new EntityMemberMask(70);
    Assert.IsTrue(mask.IsEmpty);

    mask.Set(0);
    mask.Set(65);
    Assert.IsFalse(mask.IsEmpty);
    CollectionAssert.AreEqual(new[] { 0, 65 }, mask.GetSetIndexes().ToList());

    mask.Clear(0);
    CollectionAssert.AreEqual(new[] { 65 }, mask.GetSetIndexes().ToList());

    mask.Clear();
    Assert.IsTrue(mask.IsEmpty);

    // ----- a NARROW mask allocates nothing ---------------------------------------------------------------------------
    //
    // The whole argument for the struct: a mask over an ordinary entity is an inline ulong, and the overflow array is
    // the wide case only. If this ever stops being true, every record in a session pays for it.
    Assert.IsFalse(HasOverflow(new EntityMemberMask(64)), "a 64-member mask allocated an overflow array");
    Assert.IsTrue(HasOverflow(new EntityMemberMask(65)));

    // ----- masks with the same bits are EQUAL and hash alike -----------------------------------------------------------
    //
    // Which is what makes a mask usable as the key of the CRUD SQL cache — the reason it is a type at all.
    var first = new EntityMemberMask(70);
    var second = new EntityMemberMask(70);
    first.Set(3);
    second.Set(3);
    first.Set(64);
    second.Set(64);

    Assert.AreEqual(first, second);
    Assert.AreEqual(first.GetHashCode(), second.GetHashCode());

    second.Set(5);
    Assert.AreNotEqual(first, second);

    // ----- and a WIDE mask's copy does not follow the original -----------------------------------------------------------
    //
    // A struct copy SHARES the overflow array, so a wide mask kept as a key has to be copied on the way in — one that
    // went on changing after being hashed would lose the entry it still holds. The narrow case is free.
    var wide = new EntityMemberMask(70);
    wide.Set(65);
    var copy = wide.Copy();
    var shared = wide;

    wide.Set(66);

    CollectionAssert.AreEqual(new[] { 65 }, copy.GetSetIndexes().ToList());
    CollectionAssert.AreEqual(new[] { 65, 66 }, shared.GetSetIndexes().ToList(),
      "a plain assignment shares the overflow array — which is exactly why Copy exists");

    // ----- while the WIDTH it was built with is not part of it -----------------------------------------------------------
    //
    // Deliberately: the struct is bits and nothing else, and how many members an entity has is the TYPE's business. It
    // is also why there is no complement — Not is undefined without a width.
    Assert.AreEqual(new EntityMemberMask(8), new EntityMemberMask(9));
  }

  // ----- what it refuses ------------------------------------------------------------------------------------------------

  [TestMethod]
  public void EntityWithNoInterface_SaysSo() {
    // The metadata-defined case, which is real rather than defensive: such an entity is served by the untyped path.
    var module = new EntityModule("Stored", new Version(1, 0));
    var stored = new EntityTypeInfo("Stored", module);

    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => Factory.GetEntityClass(stored));
    StringAssert.Contains(ex.Message, "untyped path");
  }
}
