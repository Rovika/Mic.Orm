using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// References: the foreign key underneath `person.Address`, and the inverse that defines `person.Phones`. This is
// where the model stops being a list of tables and becomes a graph — and where a member the developer never declared
// (the FK column) appears.
[TestClass]
public class ReferenceBuilderTests {
  private interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    string City { get; set; }
  }

  private interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    IAddress Address { get; set; }
    IAddress? PreviousAddress { get; set; }
    IList<IPhone> Phones { get; }
  }

  private interface IPhone {
    [PrimaryKey] Guid Id { get; set; }
    IPerson Person { get; set; }
    string Number { get; set; }
  }

  private static EntityApp Build(params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module, "s");
    app.TryInit();
    return app;
  }

  private static EntityApp People() => Build(typeof(IPerson), typeof(IAddress), typeof(IPhone));

  // ----- ONE model, everything a plain reference produces --------------------------------------------------------------
  //
  // Eight methods, all of them asking the same three-entity model a different question about the same two references.
  [TestMethod]
  public void Reference_GeneratesItsForeignKeyAndPairsTwoKeys() {
    // The developer declared `Address`, not `AddressId` — the column is the ORM's business, which is what keeps the
    // model expressed as an association rather than as the column beneath it.
    var app = People();
    Assert.IsTrue(app.Initialized, app.Report());
    var model = app.Model!;
    var person = model.FindEntity("Person")!;

    var fk = person.FindMember("Address_Id");
    Assert.IsNotNull(fk);
    Assert.AreEqual(MemberKind.Column, fk!.Kind);
    Assert.AreSame(person.GetMember("Address"), fk.OwnerReference);

    // ----- and it takes the TARGET KEY's type, and its nullability from the reference -------------------------------
    Assert.AreEqual(typeof(Guid), fk.DataType);
    Assert.IsFalse(fk.IsNullable);                                      // IAddress Address
    Assert.IsTrue(person.GetMember("PreviousAddress_Id").IsNullable);   // IAddress? PreviousAddress

    // ----- the relationship itself is a PAIR OF KEYS ----------------------------------------------------------------
    //
    // A foreign key is NOT a key: the constraint is carried by this entity's own key, marked ForeignKey, and the
    // relationship is the pairing of that key with the target's.
    var address = model.FindEntity("Address")!;
    var reference = person.GetMember("Address").ReferenceInfo!;

    Assert.IsNotNull(reference);
    Assert.AreSame(address, reference.TargetEntity);
    Assert.AreSame(address.PrimaryKey, reference.ToKey);
    Assert.IsTrue(reference.FromKey.IsForeignKey);
    Assert.AreEqual("Address_Id", reference.FromKey.Members.Single().Member.Name);
    Assert.AreSame(person, reference.FromKey.Entity);

    // ----- and the key knows the MEMBER it belongs to ----------------------------------------------------------------
    //
    // The walk in the other direction. From a member you can already reach its key; without OwnerMember, going back —
    // given this foreign key, which association is it FOR, and what does it point at — means searching every member of
    // the entity for the one whose reference names it.
    Assert.AreSame(person.GetMember("Address"), reference.FromKey.OwnerMember);
    Assert.AreSame(reference.ToKey, reference.FromKey.OwnerMember!.ReferenceInfo!.ToKey);

    // A member-level key knows its member too; an entity-level one belongs to no single member.
    Assert.AreSame(person.GetMember("Id"), person.PrimaryKey!.OwnerMember);
    Assert.IsNull(Build(typeof(ITripLeg)).Model!.FindEntity("TripLeg")!.PrimaryKey!.OwnerMember);

    // ----- the foreign key's NAME is the constraint's name ------------------------------------------------------------
    //
    // Which is how a naming policy reaches foreign-key constraints by walking keys alone.
    Assert.AreEqual("FK_Person_Address_Id", reference.FromKey.Name);
    Assert.IsTrue(person.Keys.Any(k => k.Name == "FK_Person_Address_Id"));

    // ----- and it is NOT indexed by default ----------------------------------------------------------------------------
    //
    // A foreign key does not imply an index on itself. Navigating person.Address looks Address up by ITS primary key,
    // so the index that serves the lookup is on the target — an index here would only help the REVERSE direction,
    // which most references never see, while charging every insert and update on this table for it.
    Assert.IsFalse(reference.FromKey.Flags.HasFlag(KeyFlags.Index));

    // ----- while a LIST has no column at all, and finds the reference back ---------------------------------------------
    //
    // It is a query, defined by the member on the other side that owns the key.
    var phones = person.GetMember("Phones");
    var phone = model.FindEntity("Phone")!;

    Assert.AreSame(phone, phones.ListInfo!.TargetEntity);
    Assert.AreSame(phone.GetMember("Person"), phones.ListInfo.InverseMember);
  }

  // ----- composite targets, and naming -------------------------------------------------------------------------------

  [PrimaryKey("TripId,LegNo")]
  private interface ITripLeg {
    Guid TripId { get; set; }
    int LegNo { get; set; }
  }

  private interface IStop {
    [PrimaryKey] Guid Id { get; set; }
    ITripLeg Leg { get; set; }
  }

  [TestMethod]
  public void ACompositeTargetGeneratesSeveralColumnsUnderAReplaceableName() {
    // The reason the default name composes the reference with the TARGET KEY's member name: appending "Id" would
    // collide the moment a key has more than one member.
    var app = Build(typeof(IStop), typeof(ITripLeg));
    Assert.IsTrue(app.Initialized, app.Report());
    var stop = app.Model!.FindEntity("Stop")!;

    Assert.IsNotNull(stop.FindMember("Leg_TripId"));
    Assert.IsNotNull(stop.FindMember("Leg_LegNo"));
    var fromKey = stop.GetMember("Leg").ReferenceInfo!.FromKey;
    Assert.IsTrue(fromKey.IsComposite);
    CollectionAssert.AreEqual(new[] { "Leg_TripId", "Leg_LegNo" }, fromKey.Members.Select(m => m.Member.Name).ToArray());

    // ----- and the composing is the NAMING POLICY's, so an application can spell it its own way ----------------------
    var renamed = new EntityApp("App", new Version(1, 0), new EntityAppSettings { NamingPolicy = new SnakePolicy() });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPerson), typeof(IAddress), typeof(IPhone));
    renamed.AddModule(module, "s");
    renamed.TryInit();
    Assert.IsNotNull(renamed.Model!.FindEntity("Person")!.FindMember("Address_Id"));
  }

  private sealed class SnakePolicy : NamingPolicy {
    public override string GetForeignKeyMemberName(string referenceMemberName, string targetKeyMemberName) =>
      $"{referenceMemberName}_{targetKeyMemberName}";
  }

  // ----- explicit control ---------------------------------------------------------------------------------------------

  private interface IExplicitFkRow {
    [PrimaryKey] Guid Id { get; set; }
    [EntityRef("HomeAddrId", ForeignKeyName = "FK_Explicit_Home")] IAddress Home { get; set; }
    [EntityRef(HasDbForeignKey = false)] IAddress Loose { get; set; }
  }

  private interface IIndexedRef {
    [PrimaryKey] Guid Id { get; set; }
    [Index] IAddress Address { get; set; }
  }

  [TestMethod]
  public void EntityRef_NamesTheColumnAndTheConstraint() {
    var app = Build(typeof(IExplicitFkRow), typeof(IAddress));
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity("ExplicitFkRow")!;

    Assert.IsNotNull(entity.FindMember("HomeAddrId"));
    Assert.AreEqual("FK_Explicit_Home", entity.GetMember("Home").ReferenceInfo!.FromKey.Name);

    // ----- and one the database does NOT enforce is still a relationship ---------------------------------------------
    //
    // The model knows about it and can navigate it; the database is simply not asked to police it. That is a property
    // of the RELATIONSHIP, not of either key — so the key is a foreign key like any other.
    var loose = entity.GetMember("Loose").ReferenceInfo!;
    Assert.IsFalse(loose.HasDbConstraint);
    Assert.IsTrue(loose.FromKey.IsForeignKey);
    Assert.IsTrue(entity.GetMember("Home").ReferenceInfo!.HasDbConstraint);

    // ----- an INDEX over a reference is the developer's call, and covers its columns ----------------------------------
    //
    // Where the reverse lookup does matter, it is asked for — rather than being paid for on every reference. And it is
    // written over the ASSOCIATION, which is the right thing to write. BOTH forms are kept: what was written, and what
    // the database gets.
    var indexed = Build(typeof(IIndexedRef), typeof(IAddress)).Model!.FindEntity("IndexedRef")!;
    var index = indexed.Keys.Single(k => k.Flags.HasFlag(KeyFlags.Index) && !k.IsForeignKey);

    Assert.AreEqual("Address", index.Members.Single().Member.Name);
    Assert.AreEqual("Address_Id", index.MembersExpanded.Single().Member.Name);
  }

  [Index("Leg -Id")]
  private interface IStopIndexedByRef {
    [PrimaryKey] Guid Id { get; set; }
    ITripLeg Leg { get; set; }
  }

  [TestMethod]
  public void AKeyOverACompositeReferenceExpandsToAllItsColumns() {
    var app = Build(typeof(IStopIndexedByRef), typeof(ITripLeg));
    Assert.IsTrue(app.Initialized, app.Report());
    var index = app.Model!.FindEntity("StopIndexedByRef")!.Keys.Single(k => k.Flags.HasFlag(KeyFlags.Index));

    // Two members were written; three columns come out — the direction written for the association applies to each of
    // the columns it expands into.
    CollectionAssert.AreEqual(new[] { "Leg", "Id" }, index.Members.Select(m => m.Member.Name).ToArray());
    CollectionAssert.AreEqual(new[] { "Leg_TripId", "Leg_LegNo", "Id" },
      index.MembersExpanded.Select(m => m.Member.Name).ToArray());
    Assert.IsTrue(index.MembersExpanded[2].Descending);

    // A single written member over a composite target IS a composite key, which is why IsComposite reads the expanded
    // list rather than what was declared.
    Assert.IsTrue(index.IsComposite);
  }

  // ----- cascading expansion ----------------------------------------------------------------------------------------
  //
  // A primary key can itself be a REFERENCE — the one-to-one case, where the child's key is its reference back to the
  // parent. A foreign key pointing at such an entity therefore cannot be built until that key has expanded, which is
  // why the pass is a fixpoint loop rather than a sequence.
  //
  //     Employment.Person  →  Person.PK (a reference to Party)  →  Party.PK (a column)

  private interface IParty {
    [PrimaryKey] Guid Id { get; set; }
  }

  private interface IPartyPerson {
    [PrimaryKey] IParty Party { get; set; }
    string Name { get; set; }
  }

  private interface IEmployment {
    [PrimaryKey] Guid Id { get; set; }
    IPartyPerson Person { get; set; }
  }

  [TestMethod]
  public void Expansion_CascadesThroughAReferenceKey() {
    // ----- a primary key may itself BE a reference -------------------------------------------------------------------
    var app = Build(typeof(IEmployment), typeof(IPartyPerson), typeof(IParty));
    Assert.IsTrue(app.Initialized, app.Report());
    var model = app.Model!;
    var person = model.FindEntity("PartyPerson")!;
    var pk = person.PrimaryKey!;

    Assert.AreEqual("Party", pk.Members.Single().Member.Name);            // as declared
    Assert.AreEqual("Party_Id", pk.MembersExpanded.Single().Member.Name); // what the database gets
    // The generated column IS the identity, so it carries the flag the declared member carries.
    Assert.IsTrue(person.GetMember("Party_Id").IsPrimaryKey);

    // ----- and a foreign key pointing at one is generated from ITS columns --------------------------------------------
    //
    // Which only exist once Person's own key has expanded through Party. Registration order above is deliberately
    // worst-first, which is what makes the pass a fixpoint loop rather than a sequence.
    var employment = model.FindEntity("Employment")!;
    Assert.IsNotNull(employment.FindMember("Person_Party_Id"));
    var fromKey = employment.GetMember("Person").ReferenceInfo!.FromKey;
    Assert.AreEqual("Person_Party_Id", fromKey.Members.Single().Member.Name);
    Assert.AreSame(person.PrimaryKey, employment.GetMember("Person").ReferenceInfo!.ToKey);

    // ----- so EVERY key ends up expanded, which is the loop's exit condition -------------------------------------------
    foreach (var entity in model.Entities)
      foreach (var key in entity.Keys)
        Assert.IsTrue(key.IsExpanded, $"{entity.Name}.{key.Name}");
  }

  // A ring: each entity is keyed on a reference to the other, so neither key can ever be made of columns.
  private interface IRingA {
    [PrimaryKey] IRingB B { get; set; }
  }

  private interface IRingB {
    [PrimaryKey] IRingA A { get; set; }
  }

  [TestMethod]
  public void ACircularKeyDependencyIsReportedNotHung() {
    var app = Build(typeof(IRingA), typeof(IRingB));
    Assert.IsFalse(app.Initialized);
    Assert.IsTrue(app.Diagnostics().Any(d => d.Message.Contains("depends on it in turn")));
  }

  // ----- what is reported -----------------------------------------------------------------------------------------

  private interface IKeylessRow {
    string Name { get; set; }
  }

  private interface IPointsAtKeyless {
    [PrimaryKey] Guid Id { get; set; }
    IKeylessRow Target { get; set; }
  }

  private interface IOrphanList {
    [PrimaryKey] Guid Id { get; set; }
    IList<IAddress> Addresses { get; }
  }

  private interface ITwoWaysBack {
    [PrimaryKey] Guid Id { get; set; }
    IParent Home { get; set; }
    IParent Work { get; set; }
  }

  private interface IParent {
    [PrimaryKey] Guid Id { get; set; }
    IList<ITwoWaysBack> Children { get; }
  }

  private interface IParentSaysWhich {
    [PrimaryKey] Guid Id { get; set; }
    [OneToMany(nameof(ITwoWaysBack2.Home))] IList<ITwoWaysBack2> Children { get; }
  }

  private interface ITwoWaysBack2 {
    [PrimaryKey] Guid Id { get; set; }
    IParentSaysWhich Home { get; set; }
    IParentSaysWhich Work { get; set; }
  }

  // ----- what a reference the builder cannot make says -----------------------------------------------------------------
  //
  // Four methods, one per broken shape, each a build and one assertion on the message. The messages are the point:
  // this is the layer a database can never report on, because none of these models reach one.
  [TestMethod]
  public void AReferenceTheBuilderCANNOTMakeIsREPORTEDAndSaysWhatToWrite() {
    // ----- a target with no primary key ------------------------------------------------------------------------------
    //
    // Where a missing primary key actually bites — long before the validation pass, because there is nothing to point
    // at.
    var keyless = Build(typeof(IPointsAtKeyless), typeof(IKeylessRow));
    Assert.IsFalse(keyless.Initialized);
    Assert.IsTrue(keyless.Diagnostics().Any(d => d.Message.Contains("no primary key")));

    // ----- a list with nothing pointing back --------------------------------------------------------------------------
    var orphan = Build(typeof(IOrphanList), typeof(IAddress));
    Assert.IsFalse(orphan.Initialized);
    Assert.IsTrue(orphan.Diagnostics().Any(d => d.Message.Contains("no reference back")));

    // ----- a list with TWO things pointing back, which names both and the attribute that settles it --------------------
    var ambiguous = Build(typeof(IParent), typeof(ITwoWaysBack));
    Assert.IsFalse(ambiguous.Initialized);
    var message = ambiguous.Diagnostics().First(d => d.Message.Contains("more than once")).Message;
    Assert.IsTrue(message.Contains("Home") && message.Contains("Work"));
    Assert.IsTrue(message.Contains("[OneToMany"));

    // ----- and writing that attribute resolves it ----------------------------------------------------------------------
    var resolved = Build(typeof(IParentSaysWhich), typeof(ITwoWaysBack2));
    Assert.IsTrue(resolved.Initialized, resolved.Report());
    Assert.AreEqual("Home",
      resolved.Model!.FindEntity("ParentSaysWhich")!.GetMember("Children").ListInfo!.InverseMember!.Name);
  }

  // ----- a foreign-key member the developer wrote themselves ----------------------------------------------------------

  private interface IDeclaresItsOwn {
    [PrimaryKey] Guid Id { get; set; }
    IAddress Address { get; set; }
    Guid Address_Id { get; set; }              // declared, to read the key without loading the target
  }

  [TestMethod]
  public void ExplicitForeignKeyMember_IsUsedNotRefused() {
    // Two things people legitimately write: a key exposed beside its reference, and two references sharing a column.
    // Generating over the top would be a duplicate; refusing would reject both.
    var app = Build(typeof(IDeclaresItsOwn), typeof(IAddress));
    Assert.IsTrue(app.Initialized, app.Report());

    var entity = app.Model!.FindEntity("DeclaresItsOwn")!;
    Assert.AreEqual(1, entity.Members.Count(m => m.Name == "Address_Id"), "the member was generated a second time");

    var declared = entity.GetMember("Address_Id");
    Assert.AreSame(declared, entity.GetMember("Address").ReferenceInfo!.FromKey.MembersExpanded.Single().Member);
    Assert.AreSame(entity.GetMember("Address"), declared.OwnerReference,
      "the reference is what the column is for, whichever was written first");

    // ----- and NULLABILITY is not a mismatch --------------------------------------------------------------------------
    //
    // An optional reference makes its foreign key nullable while the target key column is not, so Guid? against Guid
    // has to match — comparing the types as written would reject the ordinary case.
    var optional = Build(typeof(IOptionalDeclaresItsOwn), typeof(IAddress));
    Assert.IsTrue(optional.Initialized, optional.Report());
    Assert.IsTrue(optional.Model!.FindEntity("OptionalDeclaresItsOwn")!.GetMember("Address_Id").IsNullable);
  }

  private interface IDeclaresTheWrongType {
    [PrimaryKey] Guid Id { get; set; }
    IAddress Address { get; set; }
    int Address_Id { get; set; }               // an int where the target's key is a Guid
  }

  [TestMethod]
  public void ButNotWhenItsTypeCannotHoldTheKey() {
    var app = Build(typeof(IDeclaresTheWrongType), typeof(IAddress));
    Assert.IsFalse(app.Initialized);
    Assert.IsTrue(app.Diagnostics().Any(d => d.Message.Contains("does not match")), app.Report());
  }

  private interface IOptionalDeclaresItsOwn {
    [PrimaryKey] Guid Id { get; set; }
    IAddress? Address { get; set; }
    Guid? Address_Id { get; set; }             // nullable, because the reference is
  }

  // ----- renaming a reference carries its column ------------------------------------------------------------------------

  [Entity(Name = "Renamer")]
  private interface IRenamerBefore {
    [PrimaryKey] Guid Id { get; set; }
    IAddress Address { get; set; }
  }

  [Entity(Name = "Renamer")]
  private interface IRenamerAfter {
    [PrimaryKey] Guid Id { get; set; }
    [OldNames("Address")] IAddress Location { get; set; }
  }

  [TestMethod]
  public void RenamingAReferenceRenamesTheColumnItGENERATED() {
    // The member nobody wrote still has to be matchable across the rename, or the upgrade sees one column appear and
    // another vanish — and the data goes with it. This is what the whole OldNames matching path was built for.
    var app = Build(typeof(IRenamerAfter), typeof(IAddress));
    Assert.IsTrue(app.Initialized, app.Report());

    var fk = app.Model!.FindEntity("Renamer")!.GetMember("Location_Id");
    CollectionAssert.Contains(fk.OldNames.ToArray(), "Address_Id");
  }
}
