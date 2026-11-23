using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// The METADATA tier: construction, containment and lookup. Built by hand here — the builder that will derive these
// from declarations is a later step, and these types have to be right before anything derives them.
[TestClass]
public class EntityModelTests {
  private static Type Scalar<T>() => typeof(T);

  private static EntityMemberInfo Column(string name, Type type, MemberFlags flags = MemberFlags.None) =>
    new(name, type, MemberKind.Column) { MemberFlags = flags };

  // A small model: one module, Person and Address, Person referencing Address.
  private static EntityAppModel NewModel(out EntityTypeInfo person, out EntityTypeInfo address) {
    var model = new EntityAppModel("TestApp", new Version(1, 0));
    var module = new EntityModule("People", new Version(1, 0)) { Schema = "people" };
    model.AddModule(module);

    // A module declares its schema as a NAME; the entity carries the resolved object. The builder does this step for a
    // real model — here it is by hand, which is the only reason it is visible.
    var schema = model.GetOrAddSchema("people");

    address = new EntityTypeInfo("Address", module) { Schema = schema };
    address.AddMember(Column("Id", Scalar<Guid>(), MemberFlags.PrimaryKey));
    address.AddMember(Column("City", Scalar<string>()));
    model.AddEntity(address);

    person = new EntityTypeInfo("Person", module) { Schema = schema };
    person.AddMember(Column("Id", Scalar<Guid>(), MemberFlags.PrimaryKey));
    person.AddMember(Column("Name", Scalar<string>()));
    person.AddMember(new EntityMemberInfo("Address", typeof(object), MemberKind.Reference) { TargetEntity = address });
    model.AddEntity(person);
    return model;
  }

  // ----- ONE hand-built model, everything containment and lookup do ------------------------------------------------------
  //
  // Nine methods over the same three-entity fixture. All of it is construction and asking — no state any of them shares
  // is disturbed, because the two mutating cases are both REFUSALS.
  [TestMethod]
  public void Model_HoldsAndLooksUpItsParts() {
    var model = NewModel(out var person, out var address);
    Assert.AreEqual(1, model.Modules.Count);
    Assert.AreEqual(2, model.Entities.Count);
    Assert.AreSame(person, model.FindEntity("Person"));

    // ----- an entity names the module that DECLARED it ----------------------------------------------------------------
    //
    // The declaration object, not a metadata copy. There is only ONE list of entities, the model's.
    Assert.AreSame(model.FindModule("People"), person.Module);
    Assert.AreEqual(new Version(1, 0), person.Module.Version);

    // ----- and a schema is shared BY NAME ------------------------------------------------------------------------------
    Assert.AreSame(model.GetOrAddSchema("people"), model.FindSchema("people"));
    Assert.AreEqual(1, model.Schemas.Count);

    // ----- members carry their SLOT and their owner ---------------------------------------------------------------------
    Assert.AreEqual(0, person.GetMember("Id").DataIndex);
    Assert.AreEqual(1, person.GetMember("Name").DataIndex);
    Assert.AreSame(person, person.GetMember("Name").Entity);

    // ----- member kind and CLR type stay independent ---------------------------------------------------------------------
    //
    // The axis that must not be collapsed: the CLR side is one thing, how the ORM persists it another.
    var reference = person.GetMember("Address");
    Assert.AreEqual(MemberKind.Reference, reference.Kind);
    Assert.AreSame(address, reference.TargetEntity);

    // ----- a name finds its member whatever its casing, and a SECOND of that name is refused --------------------------------
    //
    // The pair is the point. The lookup is case-insensitive because the rest of the model already deduped names that
    // way, and the refusal is what makes that safe: without it, `Title` and `title` would both be added and the lookup
    // would answer with whichever won, having quietly lost the other.
    Assert.AreSame(person.GetMember("Name"), person.FindMember("name"));
    Assert.AreSame(person.GetMember("Name"), person.FindMember("NAME"));
    Assert.IsNull(person.FindMember("NoSuchMember"));

    var clash = Assert.ThrowsExactly<InvalidOperationException>(
      () => person.AddMember(Column("name", Scalar<string>())));
    StringAssert.Contains(clash.Message, "case-insensitive");
    Assert.AreEqual(person.Members.Count, person.DataCount, "nothing was handed out to the member that was refused");

    // ----- and a member belongs to ONE entity ------------------------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => address.AddMember(person.GetMember("Name")));

    // ----- nullability and db-generation are the MEMBER's own ---------------------------------------------------------------
    //
    // The model-level "not writable" fact is what lets the emitted setter simply throw, with no runtime check.
    Assert.IsTrue(new EntityMemberInfo("Note", typeof(string), MemberKind.Column, isNullable: true).IsNullable);
    Assert.IsFalse(Column("Name", Scalar<string>()).IsNullable);
    Assert.IsTrue(Column("RowVersion", Scalar<int>(), MemberFlags.DbGenerated).IsDbGenerated);
    Assert.IsFalse(Column("Name", Scalar<string>()).IsDbGenerated);
  }

  [TestMethod]
  public void UnmanagedSchema_SaysSo() {
    // The flag that matters for a database MIC attaches to but does not own — no CREATE SCHEMA for it.
    var model = new EntityAppModel("App", new Version(1, 0));
    var legacy = model.GetOrAddSchema("legacy");
    Assert.IsTrue(legacy.IsManaged);
    legacy.IsManaged = false;
    Assert.IsFalse(model.FindSchema("legacy")!.IsManaged);
  }

  [TestMethod]
  public void Members_AreFlatAndSlotsFollowAddOrder() {
    // THERE IS NO ENTITY-LEVEL INHERITANCE. An entity interface may inherit several interfaces and those are almost
    // never entities — an `IAppEntityBase` declaring `Id` and `CreatedOn` is a bare interface nobody registers. The
    // builder collects the properties of the interface and of every interface it inherits into ONE list, so an entity
    // owns every member it has and there is no base to point at.
    var model = new EntityAppModel("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    model.AddModule(module);

    var doc = new EntityTypeInfo("Document", module);
    doc.AddMember(Column("Id", Scalar<Guid>(), MemberFlags.PrimaryKey));
    doc.AddMember(Column("CreatedOn", Scalar<DateTime>()));
    doc.AddMember(Column("Title", Scalar<string>()));
    model.AddEntity(doc);

    CollectionAssert.AreEqual(new[] { "Id", "CreatedOn", "Title" }, doc.Members.Select(m => m.Name).ToArray());
    Assert.AreEqual(0, doc.GetMember("Id").DataIndex);
    Assert.AreEqual(2, doc.GetMember("Title").DataIndex);
  }

  [TestMethod]
  public void Key_AddedFoundAndOnlyOnePrimary() {
    NewModel(out var person, out var address);

    // ----- the primary key ------------------------------------------------------------------------------------------
    var pk = new EntityKeyInfo("PK_Person", person, KeyFlags.PrimaryKey | KeyFlags.Clustered);
    pk.AddMember(person.GetMember("Id"));
    person.AddKey(pk);

    Assert.AreSame(pk, person.PrimaryKey);
    Assert.IsTrue(pk.IsUnique);                                  // a primary key is unique without saying so
    Assert.IsFalse(pk.IsComposite);
    Assert.ThrowsExactly<InvalidOperationException>(
      () => person.AddKey(new EntityKeyInfo("PK_Again", person, KeyFlags.PrimaryKey)));

    // ----- a FOREIGN key is a key, and the RELATIONSHIP is a pair of them ---------------------------------------------
    //
    // What carries the ForeignKey flag is the referencing entity's own key, and that key's NAME is the constraint's
    // name — which is how the naming policy reaches FK constraints by walking keys alone.
    var addressPk = new EntityKeyInfo("PK_Address", address, KeyFlags.PrimaryKey);
    addressPk.AddMember(address.GetMember("Id"));
    address.AddKey(addressPk);

    var fkColumn = Column("AddressId", Scalar<Guid>());
    person.AddMember(fkColumn);
    var fromKey = new EntityKeyInfo("FK_Person_Address", person, KeyFlags.ForeignKey | KeyFlags.Index);
    fromKey.AddMember(fkColumn);
    person.AddKey(fromKey);

    person.GetMember("Address").ReferenceInfo = new ReferenceInfo(fromKey, addressPk);

    var reference = person.GetMember("Address").ReferenceInfo!;
    Assert.AreSame(address, reference.TargetEntity);
    Assert.IsTrue(fromKey.IsForeignKey);
    Assert.IsFalse(fromKey.IsUnique);                            // an FK constrains nothing about uniqueness
    Assert.IsNull(person.GetMember("Name").ReferenceInfo);           // and a plain column has no relationship at all

    // ----- while an index carries no OPTIONS until it needs any -------------------------------------------------------
    var index = new EntityKeyInfo("IX_Person_Name", person, KeyFlags.Index);
    index.AddMember(person.GetMember("Name"));
    person.AddKey(index);
    Assert.IsNull(index.Index);                                  // nothing meaningless carried by default

    index.Index = new IndexOptions { Filter = "\"Name\" IS NOT NULL" };
    index.Index.AddIncludedMember(person.GetMember("Id"));
    Assert.AreEqual(1, index.Index.IncludedMembers.Count);
  }

  [TestMethod]
  public void CompositeKeys_AreRecognised() {
    // TripLeg (TripId, LegNo) — the case that makes the identity map's key a tuple and denies smart loading a plain
    // `id IN (list)`.
    var model = new EntityAppModel("App", new Version(1, 0));
    var module = new EntityModule("Ops", new Version(1, 0));
    model.AddModule(module);

    var leg = new EntityTypeInfo("TripLeg", module);
    leg.AddMember(Column("TripId", Scalar<Guid>(), MemberFlags.PrimaryKey));
    leg.AddMember(Column("LegNo", Scalar<int>(), MemberFlags.PrimaryKey));
    model.AddEntity(leg);

    var pk = new EntityKeyInfo("PK_TripLeg", leg, KeyFlags.PrimaryKey);
    pk.AddMember(leg.GetMember("TripId"));
    pk.AddMember(leg.GetMember("LegNo"), descending: true);
    leg.AddKey(pk);

    Assert.IsTrue(pk.IsComposite);
    Assert.IsTrue(pk.Members[1].Descending);
    Assert.AreEqual("LegNo", pk.Members[1].Member.Name);
  }

  // ----- one name, two schemas ---------------------------------------------------------------------------------

  /// <summary>Two modules against DIFFERENT schemas, each with a `User`.</summary>
  private static EntityAppModel TwoSchemas(out EntityTypeInfo sales, out EntityTypeInfo support) {
    var model = new EntityAppModel("TwoSchemas", new Version(1, 0));
    var salesModule = new EntityModule("Sales", new Version(1, 0)) { Schema = "sales" };
    var supportModule = new EntityModule("Support", new Version(1, 0)) { Schema = "support" };
    model.AddModule(salesModule);
    model.AddModule(supportModule);

    sales = new EntityTypeInfo("User", salesModule) { Schema = model.GetOrAddSchema("sales") };
    sales.AddMember(Column("Id", Scalar<Guid>(), MemberFlags.PrimaryKey));
    model.AddEntity(sales);

    support = new EntityTypeInfo("User", supportModule) { Schema = model.GetOrAddSchema("support") };
    support.AddMember(Column("Id", Scalar<Guid>(), MemberFlags.PrimaryKey));
    model.AddEntity(support);
    return model;
  }

  [TestMethod]
  public void OneNameInTwoSchemas_BareNameWillNotGuess() {
    // Uniqueness is (schema, name), which is the rule the DATABASE imposes. It used to be the name alone, and that
    // refused a model both the CLR and the server are perfectly happy with — two modules, each with an `IUser`.
    var model = TwoSchemas(out var sales, out var support);

    Assert.AreEqual(2, model.Entities.Count);
    Assert.AreSame(sales, model.FindEntity("User", "sales"));
    Assert.AreSame(support, model.FindEntity("User", "support"));

    // ----- while the BARE name refuses to guess between them ----------------------------------------------------------
    //
    // A caller asking for "User" in this model has asked a question with no answer. Handing back whichever happens to be
    // first is the worst of the three available behaviours, so it says so and names both schemas.
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => model.FindEntity("User"));
    StringAssert.Contains(ex.Message, "more than one schema");
    StringAssert.Contains(ex.Message, "sales");
    StringAssert.Contains(ex.Message, "support");

    // …while staying exact wherever nothing collides, which is every ordinary model.
    var ordinary = NewModel(out var person, out _);
    Assert.AreSame(person, ordinary.FindEntity("Person"));

    // ----- and TWO of one name in ONE schema is refused outright ----------------------------------------------------------
    //
    // The collision that genuinely matters: same schema, so the database could not hold both tables either.
    var clash = new EntityAppModel("Clash", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0)) { Schema = "app" };
    clash.AddModule(module);
    var app = clash.GetOrAddSchema("app");
    clash.AddEntity(new EntityTypeInfo("User", module) { Schema = app });

    var duplicate = Assert.ThrowsExactly<InvalidOperationException>(
      () => clash.AddEntity(new EntityTypeInfo("User", module) { Schema = app }));
    StringAssert.Contains(duplicate.Message, "already in schema");
  }
}
