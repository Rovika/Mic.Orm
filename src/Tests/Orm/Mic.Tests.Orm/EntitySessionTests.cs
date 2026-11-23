using System.Runtime.CompilerServices;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE IDENTITY MAP, AND THE INVARIANT IT EXISTS FOR. Everything the earlier steps deferred was deferred to here: a
// reference could not resolve its target without a map to ask, and an entity was made by a factory answering to
// nobody.
//
// Nothing below touches a database. A stub is created and never expanded, because expanding it is a query.
[TestClass]
public class EntitySessionTests {
  public interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    [Size(60)] string City { get; set; }
  }

  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    IAddress Address { get; set; }
  }

  // A naturally composite key — the case that makes the identity a tuple rather than a value.
  [PrimaryKey("TripId,LegNo")]
  public interface ITripLeg {
    Guid TripId { get; set; }
    int LegNo { get; set; }
    [Size(40)] string? Note { get; set; }
  }

  sealed class TravelModule : EntityModule {
    public TravelModule() : base("Travel", new Version(1, 0)) =>
      RegisterEntities(typeof(IPerson), typeof(IAddress), typeof(ITripLeg));
  }

  static readonly EntityApp App = BuildApp();

  static EntityApp BuildApp() {
    var app = new EntityApp("Travel", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new TravelModule());
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }

  static EntityAppModel Model => App.Model!;

  static EntitySession NewSession() => new(new OperationContext(App));

  static EntityTypeInfo Person => Model.FindEntity("Person")!;
  static EntityTypeInfo Address => Model.FindEntity("Address")!;
  static EntityTypeInfo TripLeg => Model.FindEntity("TripLeg")!;

  /// <summary>A record as a LOAD would produce one — which nothing can do yet, so the tests make it by hand.</summary>
  /// <remarks>No instance is made: a load produces RECORDS, and the class in front of one arrives when somebody asks
  /// for it.</remarks>
  static EntityRecord Loaded(EntitySession session, EntityTypeInfo entity, params (string Member, object? Value)[] values) {
    var record = new EntityRecord(entity, session) { Status = EntityStatus.Loaded };
    foreach (var (member, value) in values)
      record.SetLoadedValue(entity.GetMember(member), value);
    return record;
  }

  // ----- creating -------------------------------------------------------------------------------------------------

  // ----- ONE session, every rule about making an entity ------------------------------------------------------------
  //
  // Seven methods became one. None of this is duplicated by the data suite — the save set, the identity map and the
  // lazily-made instance are IN-MEMORY facts a database cannot observe — so this is a merge on "one method per rule",
  // not a cull.
  [TestMethod]
  public void NewEntity_BelongsToItsSessionAndSaveSet() {
    var session = NewSession();
    var person = session.NewEntity<IPerson>();
    var record = ((EntityBase)person).Record;

    Assert.AreSame(session, record.Session);
    Assert.AreEqual(EntityStatus.New, record.Status);
    Assert.AreSame((object)person, record.EntityInstance, "the record and its instance must know each other");

    // ----- and it is NOT in the identity map, only in the save set -------------------------------------------------
    //
    // The two collections are not two views of one thing. A new entity cannot be keyed — it has no id, and where the
    // database generates one it will not have an id until the insert this save set exists to produce has run — so it
    // belongs to the save set alone until it has an identity.
    Assert.AreEqual(0, session.GetEntityTracker().LoadedCount);
    CollectionAssert.AreEqual(new[] { record }, session.GetEntityTracker().Modified.ToList());

    // ----- creating it IS the change -------------------------------------------------------------------------------
    //
    // An entity made and left alone is still an INSERT.
    Assert.IsTrue(record.Modified.IsEmpty, "nothing has been written to it");
    Assert.AreEqual(1, session.GetEntityTracker().Modified.Count, "…and it is still going to be inserted");

    // ----- a NEW entity may set its key, as often as it likes -------------------------------------------------------
    //
    // How it gets one. There is no row yet, so nothing refers to it by anything.
    var first = Guid.NewGuid();
    person.Id = first;
    person.Id = Guid.NewGuid();
    Assert.AreNotEqual(first, person.Id);
  }

  [TestMethod]
  public void Record_MakesItsInstanceOnlyWhenAsked() {
    // A record is useful without one. A query materialises rows into records, the map holds records, a save reads
    // records — and a metadata-defined entity has no interface to emit a class from at all. Making one per row read
    // would be work nobody asked for.
    var session = NewSession();
    var record = Loaded(session, Person, ("Id", Guid.NewGuid()), ("LastName", "Larsen"));

    Assert.IsFalse(HasInstance(record));
    Assert.AreEqual("Larsen", record.GetValue(Person.GetMember(nameof(IPerson.LastName))),
      "…and it answers about its values without one");

    var instance = record.EntityInstance;

    Assert.IsTrue(HasInstance(record));
    Assert.AreSame(instance, record.EntityInstance, "made once, then kept");
    Assert.AreSame(record, ((EntityBase)instance).Record);

    // ----- but a record with NO session cannot make one --------------------------------------------------------------
    //
    // It has no model to ask for the entity classes — the boundary of what such a record is for rather than a failure:
    // it holds values, and says so instead of answering with something wrong.
    var orphan = new EntityRecord(Person);
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _ = orphan.EntityInstance);
    StringAssert.Contains(ex.Message, "no session");
  }

  [TestMethod]
  public void LoadedEntity_IdentityCannotChange() {
    // Changing an identity has no honest statement behind it: the UPDATE would match on the NEW key and find no row,
    // reporting success having done nothing, while foreign keys elsewhere went on pointing at the old value.
    var session = NewSession();
    var id = Guid.NewGuid();
    var person = (IPerson)session.Attach(Loaded(session, Person, ("Id", id))).EntityInstance;

    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => person.Id = Guid.NewGuid());
    StringAssert.Contains(ex.Message, "identity cannot change");
    StringAssert.Contains(ex.Message, "Person.Id");

    // ----- though setting it to what it ALREADY is is not a change, so not a refusal --------------------------------
    //
    // The write never gets as far as the rule.
    person.Id = id;
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)person).Record.Status);
    Assert.IsTrue(((EntityBase)person).Record.Modified.IsEmpty);
  }

  /// <summary>Whether one has been made — asked of the field, because nothing in the ORM needs to ask and a property
  /// for it would be public surface existing only for this test.</summary>
  static bool HasInstance(EntityRecord record) =>
    typeof(EntityRecord).GetField("_instance", System.Reflection.BindingFlags.Instance
      | System.Reflection.BindingFlags.NonPublic)!.GetValue(record) != null;

  interface INoKeyRow {
    string Name { get; set; }
  }

  sealed class BrokenModule : EntityModule {
    public BrokenModule() : base("Broken", new Version(1, 0)) => RegisterEntities(typeof(INoKeyRow));
  }

  [TestMethod]
  public void ASessionREFUSESToOpenOnAModelThatDidNotBuild() {
    // The check lands here rather than on every property access. A model with errors is left unfinished — its members
    // never got their value accessors — so the honest moment to refuse is when somebody asks to work through it, where
    // the message can name what to look at.
    var app = new EntityApp("Broken", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new BrokenModule());
    app.TryInit();

    Assert.IsFalse(app.Initialized, "this model was supposed to be broken");
    Assert.IsNotNull(app.Model, "the model is KEPT — a broken one is what a developer wants to inspect");
    Assert.AreNotEqual(0, app.Diagnostics().Count, "…alongside the diagnostics saying why it is broken");

    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => app.OpenSession());
    StringAssert.Contains(ex.Message, "Report");
  }

  // ----- the save set -----------------------------------------------------------------------------------------------

  // ----- ONE session, every rule about what joins the save set -------------------------------------------------------
  //
  // The rule is that the TRANSITION is the trigger, not the write — so the four cases are the four ways a write can
  // meet it: the one that causes a transition, the one that comes after it, the one that has nowhere to transition TO,
  // and the one that changes nothing.
  [TestMethod]
  public void Transition_JoinsTheSaveSetNotTheWrite() {
    // ----- the FIRST change joins ----------------------------------------------------------------------------------
    var session = NewSession();
    var person = (IPerson)session.Attach(Loaded(session, Person, ("Id", Guid.NewGuid()))).EntityInstance;
    Assert.AreEqual(0, session.GetEntityTracker().Modified.Count);

    person.LastName = "Larsen";

    Assert.AreEqual(EntityStatus.Modified, ((EntityBase)person).Record.Status);
    CollectionAssert.AreEqual(new[] { ((EntityBase)person).Record }, session.GetEntityTracker().Modified.ToList());

    // ----- and the SECOND does not join it again --------------------------------------------------------------------
    person.LastName = "Someone Else";
    Assert.AreEqual(1, session.GetEntityTracker().Modified.Count);

    // ----- a NEW entity stays NEW however often it is written --------------------------------------------------------
    //
    // An insert does not become an update by being written to twice.
    var fresh = NewSession();
    var made = fresh.NewEntity<IPerson>();
    made.LastName = "Larsen";
    made.LastName = "Someone Else";

    Assert.AreEqual(EntityStatus.New, ((EntityBase)made).Record.Status);
    Assert.AreEqual(1, fresh.GetEntityTracker().Modified.Count);

    // ----- and a write that CHANGES nothing joins nothing -------------------------------------------------------------
    var unchanged = NewSession();
    var untouched = (IPerson)unchanged.Attach(
      Loaded(unchanged, Person, ("Id", Guid.NewGuid()), ("LastName", "Larsen"))).EntityInstance;

    untouched.LastName = "Larsen";

    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)untouched).Record.Status);
    Assert.AreEqual(0, unchanged.GetEntityTracker().Modified.Count);
  }

  // NOT merged into the walk above, and this is the one good reason for it: the entity must be unreachable, and every
  // local in a merged method would go on rooting it — which would make the sweep pass for the wrong reason.
  [TestMethod]
  public void ModifiedEntity_IsNotLostWhenUnheld() {
    // THE REASON THE SAVE SET HOLDS STRONG REFERENCES. The identity map is weak, so a dirty entity nobody happens to
    // hold would be collectable — and the change would simply not happen, silently, and only under memory pressure.
    var session = NewSession();
    ChangeOneAndForgetIt(session);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    Assert.AreEqual(1, session.GetEntityTracker().Modified.Count);
    Assert.AreEqual("Someone Else",
      session.GetEntityTracker().Modified[0].GetValue(Person.GetMember(nameof(IPerson.LastName))));
    Assert.AreEqual(0, session.GetEntityTracker().RemoveDeadReferences(), "the save set is what keeps it alive, so the map entry is still live too");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void ChangeOneAndForgetIt(EntitySession session) {
    var person = (IPerson)session.Attach(
      Loaded(session, Person, ("Id", Guid.NewGuid()), ("LastName", "Larsen"))).EntityInstance;
    person.LastName = "Someone Else";
  }

  // ----- the interface, and the context ------------------------------------------------------------------------------

  // ----- the CONTEXT is who the work is being done FOR, and what it carries while it happens -------------------------
  //
  // Four methods, four independent facts about one small object — the kind of thing that reads better as one walk.
  [TestMethod]
  public void Session_ActsForAContext() {
    // ----- who ------------------------------------------------------------------------------------------------------
    var user = new UserInfo(Guid.NewGuid(), "victoria");
    var operation = new OperationContext(App, user) { CorrelationId = "abc" };
    IEntitySession session = operation.OpenSession();

    Assert.AreSame(user, session.Context.User);
    Assert.AreEqual("abc", session.Context.CorrelationId);
    Assert.AreSame(App, session.Context.App);
    Assert.AreSame(Model, session.Context.Model);

    // ----- and ANONYMOUS is a fact rather than a missing value -------------------------------------------------------
    var context = new OperationContext(App);
    Assert.AreSame(UserInfo.Anonymous, context.User);
    Assert.IsFalse(context.User.IsAuthenticated);
    Assert.IsTrue(UserInfo.System.IsAuthenticated);

    // ----- VALUES carry whatever the layers above wanted --------------------------------------------------------------
    context.SetValue("TenantId", 42);

    Assert.AreEqual(42, context.GetValue<int>("tenantid"), "the keys are written by hand in two places");
    Assert.IsTrue(context.TryGetValue("TenantId", out var value));
    Assert.AreEqual(42, value);

    // Setting null REMOVES, so a caller clearing something need not know which of the two it is doing.
    context.SetValue("TenantId", null);
    Assert.IsFalse(context.TryGetValue("TenantId", out _));
    Assert.AreEqual(0, context.GetValue<int>("TenantId"));

    // ----- and it disposes what was registered with it ---------------------------------------------------------------
    //
    // What makes a connection kept open across calls safe: something closes it when the operation ends.
    var closed = false;
    var disposable = new Closer(() => closed = true);
    context.RegisterDisposable(disposable);

    context.Dispose();

    Assert.IsTrue(closed);
    GC.KeepAlive(disposable);
  }

  sealed class Closer(Action onDispose) : IDisposable {
    public void Dispose() => onDispose();
  }

  // ----- getting and deleting, as far as either goes without a database ----------------------------------------------
  //
  // Both LOAD when they have to, and that half needs a database and lives in the data suite. What is left is the half
  // that answers from the map alone — which is most of the rules, and none of them observable through a server.
  [TestMethod]
  public void GetAndDeleteEntity_AnswerFromTheMap() {
    // ----- an identity the session already holds is answered from the map -------------------------------------------
    //
    // …and this application's database has no tables to fall through to, which is what the second assertion says.
    var session = NewSession();
    IEntitySession api = session;
    var id = Guid.NewGuid();

    var loaded = session.Attach(Loaded(session, Person, ("Id", id))).EntityInstance;
    Assert.AreSame((object)loaded, api.GetEntity<IPerson>(id));

    var ex = Assert.ThrowsExactly<Mic.Orm.Db.DataAccessException>(() => api.GetEntity<IPerson>(Guid.NewGuid()));
    StringAssert.Contains(ex.Message, "no such table");

    // ----- a STUB is made when one is asked for, and tracked like anything else --------------------------------------
    var stubId = Guid.NewGuid();
    var stub = api.GetEntity<IPerson>(stubId, LoadFlags.Stub);

    Assert.IsNotNull(stub);
    Assert.AreEqual(stubId, stub.Id);
    Assert.AreEqual(EntityStatus.Stub, ((EntityBase)stub).Record.Status);
    Assert.AreSame((object)stub, api.GetEntity<IPerson>(stubId), "the stub is tracked, so it is found next time");

    // ----- and a COMPOSITE key is passed as an array ------------------------------------------------------------------
    var trip = Guid.NewGuid();
    var leg = api.GetEntity<ITripLeg>(new object?[] { trip, 2 }, LoadFlags.Stub);
    Assert.AreEqual(trip, leg.TripId);
    Assert.AreEqual(2, leg.LegNo);

    // ----- DELETING a loaded entity makes it pending ------------------------------------------------------------------
    var doomed = (IPerson)session.Attach(Loaded(session, Person, ("Id", Guid.NewGuid()))).EntityInstance;
    var before = session.GetEntityTracker().Modified.Count;

    api.DeleteEntity(doomed);

    Assert.AreEqual(EntityStatus.Deleted, ((EntityBase)doomed).Record.Status);
    Assert.AreEqual(before + 1, session.GetEntityTracker().Modified.Count);

    // ----- but deleting a NEW one just forgets it ---------------------------------------------------------------------
    //
    // There is no row to delete — it was never inserted. Creating something and then deleting it should do nothing at
    // all, rather than send a DELETE against a key the database has never seen.
    var fresh = NewSession();
    var made = fresh.NewEntity<IPerson>();
    Assert.AreEqual(1, fresh.GetEntityTracker().Modified.Count);

    ((IEntitySession)fresh).DeleteEntity(made);

    Assert.AreEqual(0, fresh.GetEntityTracker().Modified.Count);
  }

  [TestMethod]
  public void Session_WorksWithoutADatabaseWhereNoneIsNeeded() {
    IEntitySession session = NewSession();

    // `ValidateChanges` never needed a query, only the records in hand.
    session.ValidateChanges();

    // `EntitySet<T>` composes without a database — the expression is not walked until the query runs — so what an
    // unknown type gets is a model error at the call that named it, and not a deferred surprise.
    Assert.IsNotNull(session.EntitySet<IPerson>().Where(p => p.Address.City == "Springfield"));
    Assert.ThrowsExactly<KeyNotFoundException>(() => session.EntitySet<INotAnEntity>());
  }

  /// <summary>Not registered in any module — what `EntitySet&lt;T&gt;` has to refuse.</summary>
  public interface INotAnEntity {
    Guid Id { get; set; }
  }

  // ----- the single-instance invariant -----------------------------------------------------------------------------

  // ----- ONE session, the whole invariant ---------------------------------------------------------------------------
  //
  // Five methods stating one rule from five sides: the same identity is the same object, and "the same identity" means
  // the TYPE and the WHOLE key.
  [TestMethod]
  public void OneIdentity_IsOneRecordPerSession() {
    // ----- attaching the same identity twice returns the FIRST record ------------------------------------------------
    //
    // The shape of it that matters: what comes back is what is already TRACKED, not what was just passed in. Code
    // assuming the object it materialised is the object it will use is the bug this prevents.
    var session = NewSession();
    var id = Guid.NewGuid();
    var first = Loaded(session, Person, ("Id", id), ("LastName", "Larsen"));
    var second = Loaded(session, Person, ("Id", id), ("LastName", "Larsen"));

    Assert.AreSame(first, session.Attach(first));
    Assert.AreSame(first, session.Attach(second), "the second materialisation should have been discarded");
    Assert.AreEqual(1, session.GetEntityTracker().LoadedCount);

    // ----- so a change through one reference is visible through the other ---------------------------------------------
    //
    // What the invariant buys, stated as the thing an application actually notices.
    var tracked = (IPerson)first.EntityInstance;
    var reattached = (IPerson)session.Attach(Loaded(session, Person, ("Id", id), ("LastName", "Larsen"))).EntityInstance;

    tracked.LastName = "Someone Else";

    Assert.AreSame(tracked, reattached);
    Assert.AreEqual("Someone Else", reattached.LastName);

    // ----- different ENTITIES with the same id do not collide ---------------------------------------------------------
    session.Attach(Loaded(session, Address, ("Id", id)));
    Assert.AreEqual(2, session.GetEntityTracker().LoadedCount, "the key is a PAIR — the type is half of it");

    // ----- and a COMPOSITE key is an identity too ----------------------------------------------------------------------
    var trip = Guid.NewGuid();
    var outbound = session.Attach(Loaded(session, TripLeg, ("TripId", trip), ("LegNo", 1), ("Note", "out")));
    var back = session.Attach(Loaded(session, TripLeg, ("TripId", trip), ("LegNo", 2), ("Note", "back")));
    var outboundAgain = session.Attach(Loaded(session, TripLeg, ("TripId", trip), ("LegNo", 1), ("Note", "out")));

    Assert.AreNotSame(outbound, back, "two legs of one trip are two entities");
    Assert.AreSame(outbound, outboundAgain);
    Assert.AreEqual(4, session.GetEntityTracker().LoadedCount);

    // ----- what has NO key cannot be tracked at all --------------------------------------------------------------------
    var ex = Assert.ThrowsExactly<InvalidOperationException>(
      () => session.Attach(new EntityRecord(Person, session)));
    StringAssert.Contains(ex.Message, "no identity");
  }

  // ----- stubs -----------------------------------------------------------------------------------------------------

  // ----- ONE session, every rule about resolving a reference ---------------------------------------------------------
  //
  // Six methods over the same two-line setup. The whole subject is what reading `person.Address` does BEFORE anything
  // has been loaded — so it is one walk through the answers: what comes back, what it costs, and when nothing does.
  [TestMethod]
  public void ReadingAReference_ProducesAStub() {
    var session = NewSession();
    var addressId = Guid.NewGuid();
    var person = (IPerson)Loaded(session, Person,
      ("Id", Guid.NewGuid()), ("Address_Id", addressId)).EntityInstance;

    var address = person.Address;

    Assert.IsNotNull(address);
    Assert.AreEqual(addressId, address.Id, "the id is already here — reading it must cost nothing");
    Assert.AreEqual(EntityStatus.Stub, ((EntityBase)address).Record.Status);

    // A stub carries its key and nothing else — asserted on the MASK rather than by reading `City`, because reading
    // it is now a load, which is the point of a stub and belongs in a test that has a database.
    var record = ((EntityBase)address).Record;
    Assert.IsTrue(record.Loaded.IsSet(Address.GetMember("Id").DataIndex));
    Assert.IsFalse(record.Loaded.IsSet(Address.GetMember("City").DataIndex));

    // ----- and it is not DIRTY ---------------------------------------------------------------------------------------
    //
    // Its key came from a record that already had it. A stub that reported itself modified would have the first save
    // write a row nobody created.
    Assert.IsTrue(record.Modified.IsEmpty);
    Assert.IsTrue(((EntityBase)person).Record.Modified.IsEmpty,
      "resolving a reference changed nothing, so the holder must not look changed either");

    // ----- reading the reference again gives the SAME stub ------------------------------------------------------------
    Assert.AreSame(address, person.Address);

    // ----- as does a reference from somebody ELSE pointing at the same row --------------------------------------------
    //
    // Through the identity map, not around it — otherwise two people sharing an address hold two objects for it.
    var neighbour = (IPerson)Loaded(session, Person, ("Id", Guid.NewGuid()), ("Address_Id", addressId)).EntityInstance;
    Assert.AreSame(address, neighbour.Address);

    // ----- a reference with NO key is null ---------------------------------------------------------------------------
    var unhoused = (IPerson)Loaded(session, Person, ("Id", Guid.NewGuid())).EntityInstance;
    Assert.IsNull(unhoused.Address);

    // ----- and one with no SESSION cannot resolve either --------------------------------------------------------------
    //
    // A record with no session answers what it holds and refuses to invent the rest — it has no map to ask.
    var orphan = new EntityRecord(Person);
    orphan.SetLoadedValue(Person.GetMember("Address_Id"), Guid.NewGuid());
    Assert.IsNull(((IPerson)App.Model!.EntityClassFactory.CreateInstance(orphan)).Address);
  }

  [TestMethod]
  public void TrackedStub_IsFilledInNotReplaced() {
    // THE ONE CASE THAT IS NOT A DISCARD, and the reason it is the right way round: every reference already handed out
    // points at the stub, so replacing it would leave them all pointing at a placeholder.
    var session = NewSession();
    var addressId = Guid.NewGuid();
    var person = (IPerson)Loaded(session, Person,
      ("Id", Guid.NewGuid()), ("Address_Id", addressId)).EntityInstance;

    var stub = person.Address;                       // handed out BEFORE the real row arrives
    Assert.AreEqual(EntityStatus.Stub, ((EntityBase)stub).Record.Status);

    var arrived = Loaded(session, Address, ("Id", addressId), ("City", "Portland"));
    var attached = session.Attach(arrived);

    Assert.AreSame(((EntityBase)stub).Record, attached, "the stub should have been filled, not replaced");
    Assert.AreSame(stub, person.Address, "the reference handed out earlier must still be the right object");
    Assert.AreEqual("Portland", stub.City);
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)stub).Record.Status);
    Assert.IsTrue(((EntityBase)stub).Record.Modified.IsEmpty, "being filled in is not a change to save");

    // ----- and filling never overwrites with what the OTHER record never had -----------------------------------------
    //
    // A partially loaded row must not blank the members it did not carry — which is what the loaded mask is for.
    var partial = new EntityRecord(Address, session) { Status = EntityStatus.Loaded };
    partial.SetLoadedValue(Address.GetMember("Id"), addressId);
    attached.FillFrom(partial);

    Assert.AreEqual("Portland", attached.GetValue(Address.GetMember("City")));
  }

  [TestMethod]
  public void TrackedRecord_TakesWhatItLacksAndIsRefreshedOnlyWhenAsked() {
    var session = NewSession();
    var personId = Guid.NewGuid();
    var firstAddress = Guid.NewGuid();
    var lastName = Person.GetMember("LastName");
    var addressKey = Person.GetMember("Address_Id");

    // ----- a row that has a member the tracked record does not hold ------------------------------------------------
    var tracked = session.Attach(Loaded(session, Person, ("Id", personId), ("Address_Id", firstAddress)));
    Assert.IsFalse(tracked.Loaded.IsSet(lastName.DataIndex));

    var attached = session.Attach(
      Loaded(session, Person, ("Id", personId), ("LastName", "Diaz"), ("Address_Id", Guid.NewGuid())));

    Assert.AreSame(tracked, attached, "one identity, one record");
    Assert.AreEqual("Diaz", tracked.GetRawValue(lastName), "the member it lacked came with the row");
    Assert.AreEqual(firstAddress, tracked.GetRawValue(addressKey), "a member it held is kept as it was");
    Assert.AreEqual(EntityStatus.Loaded, tracked.Status);
    Assert.IsTrue(tracked.Modified.IsEmpty, "being filled in is not a change to save");

    // ----- a refresh replaces what the application did not assign ---------------------------------------------------
    var person = (IPerson)tracked.EntityInstance;
    Assert.AreEqual(firstAddress, person.Address.Id);      // the reference resolved, and kept in its slot
    person.LastName = "Assigned";
    var secondAddress = Guid.NewGuid();

    session.Attach(
      Loaded(session, Person, ("Id", personId), ("LastName", "Server"), ("Address_Id", secondAddress)),
      refreshTracked: true);

    Assert.AreEqual("Assigned", person.LastName, "an assigned member is the application's");
    Assert.AreEqual(secondAddress, tracked.GetRawValue(addressKey), "a member not assigned is the row's");
    Assert.AreEqual(secondAddress, person.Address.Id, "and the reference is resolved again from the new key");
    Assert.AreEqual(EntityStatus.Modified, tracked.Status, "the record is still to be saved");
    Assert.IsTrue(tracked.Modified.IsSet(lastName.DataIndex));
    Assert.IsFalse(tracked.Modified.IsSet(addressKey.DataIndex), "what the refresh brought is not a change");
  }

  // ----- the weak map ------------------------------------------------------------------------------------------------

  // ----- what the map is keyed BY --------------------------------------------------------------------------------------
  //
  // Four methods about the same struct: what a record's identity is, when it gets one, what makes two of them
  // different, and how it hashes. All four are questions about `EntityKey`, so they are one walk through it.
  [TestMethod]
  public void ARecordOWNSItsPrimaryKeyAndMatchesALookup() {
    // The connection runs record → key. The other way round was tried and pins the map: the identity map is keyed BY
    // an EntityKey, so a key holding its record would have the map's keys keep their own values alive.
    var session = NewSession();
    var id = Guid.NewGuid();
    var record = session.Attach(Loaded(session, Person, ("Id", id)));

    Assert.AreEqual(new EntityKey(Person.PrimaryKey!, id), record.PrimaryKey);
    Assert.AreEqual(new EntityKey(Person.PrimaryKey!, id).GetHashCode(), record.PrimaryKey.GetHashCode());

    // ----- and one that had no id REFRESHES when it is given one -----------------------------------------------------
    //
    // The case that makes this more than bookkeeping: a new entity has no id until the database gives it one, and
    // everything after that — registering it, pointing foreign keys at it — needs the identity it did not have.
    var person = session.NewEntity<IPerson>();
    var made = ((EntityBase)person).Record;
    Assert.IsTrue(made.PrimaryKey.IsEmpty);

    var newId = Guid.NewGuid();
    person.Id = newId;

    Assert.AreEqual(new EntityKey(Person.PrimaryKey!, newId), made.PrimaryKey);
    Assert.AreSame(made, session.Attach(made), "and it can be tracked now, which it could not be before");

    // ----- a key names WHICH key it is a value of --------------------------------------------------------------------
    //
    // A primary-key value and a unique-key value over the same entity are different LOOKUPS, and carrying only the
    // entity would make them indistinguishable — two of them would collide in any dictionary holding both.
    var key = new EntityKey(Person.PrimaryKey!, id);

    Assert.AreSame(Person.PrimaryKey, key.KeyInfo);
    Assert.AreSame(Person, key.Entity);
    Assert.IsTrue(key.KeyType.HasFlag(KeyFlags.PrimaryKey));

    var otherKey = new EntityKey(Person.Keys.First(k => !k.IsPrimaryKey), id);
    Assert.AreNotEqual(key, otherKey);
    Assert.AreNotEqual(key.GetHashCode(), otherKey.GetHashCode());

    // ----- and it hashes the same whichever way it was built ----------------------------------------------------------
    //
    // The hash is computed when the key is built and kept — eagerly, because a struct's lookup passes a COPY and a
    // hash cached on first use would be cached on the copy. Which means the two constructors have to agree.
    var asArray = new EntityKey(Person.PrimaryKey!, [id]);

    Assert.AreEqual(key, asArray);
    Assert.AreEqual(key.GetHashCode(), asArray.GetHashCode());
    Assert.AreEqual(key.GetHashCode(), key.GetHashCode(), "and it must not vary between reads");

    Assert.AreNotEqual(key.GetHashCode(), new EntityKey(Address.PrimaryKey!, id).GetHashCode(),
      "the entity type is half the key, so it has to reach the hash");

    // ----- an identity nobody is tracking is simply not found ---------------------------------------------------------
    Assert.IsNull(session.FindTrackedEntity(new EntityKey(Person.PrimaryKey!, Guid.NewGuid())));
  }

  [TestMethod]
  public void Sweeping_DropsCollectedKeepsHeld() {
    // The failure mode this exists to avoid is named and specific: continuously reading large tables must not exhaust
    // memory. A tracked entity nobody holds is collectable, and its entry goes on the next sweep.
    var session = NewSession();
    TrackOne(session);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    Assert.AreEqual(1, session.GetEntityTracker().LoadedCount, "the entry survives until it is swept");
    Assert.AreEqual(1, session.GetEntityTracker().RemoveDeadReferences());
    Assert.AreEqual(0, session.GetEntityTracker().LoadedCount);

    // ----- while one still HELD survives the same treatment ----------------------------------------------------------
    var held = session.Attach(Loaded(session, Person, ("Id", Guid.NewGuid()))).EntityInstance;

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    Assert.AreEqual(0, session.GetEntityTracker().RemoveDeadReferences());
    Assert.AreEqual(1, session.GetEntityTracker().LoadedCount);
    Assert.IsNotNull(held);
  }

  // Kept out of the test body so no local holds the entity alive.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void TrackOne(EntitySession session) =>
    session.Attach(Loaded(session, Person, ("Id", Guid.NewGuid()), ("LastName", "Larsen")));

  // ----- building an identity without a record ------------------------------------------------------------------------
  //
  // Six methods, and the whole subject is one helper: what `CreatePrimaryKey` builds, and what it refuses. Split in
  // two — what it does, and what it will not do — rather than one method per argument shape.

  [TestMethod]
  public void PrimaryKey_CanBeBuiltFromValues() {
    var session = NewSession();
    var id = Guid.NewGuid();

    var key = EntitySessionHelper.CreatePrimaryKey<IPerson>(session, id);
    Assert.AreEqual(id, key.SingleValue);
    Assert.AreSame(Person.PrimaryKey, key.KeyInfo);

    // And it is the SAME identity a tracked record has — which is the only reason building one by hand is useful.
    var person = session.Attach(Loaded(session, Person, ("Id", id), ("LastName", "Larsen")));
    Assert.AreSame(person.EntityInstance, session.FindTrackedEntity(key));

    // ----- a COMPOSITE key takes its values in order ------------------------------------------------------------------
    //
    // THE COMPOSITE CASE IS THE POINT of the helper. `GetEntity` takes its key as an `object` because for the ordinary
    // entity the key IS one value; a composite key has no single value to pass, so the caller builds an `EntityKey`.
    var trip = Guid.NewGuid();
    var legKey = EntitySessionHelper.CreatePrimaryKey<ITripLeg>(session, trip, 2);
    Assert.AreEqual(trip, legKey.ValueAt(0));
    Assert.AreEqual(2, legKey.ValueAt(1));

    // ----- and `GetEntity` takes it, which is what it is mainly built for ---------------------------------------------
    var tracked = session.Attach(Loaded(session, TripLeg, ("TripId", trip), ("LegNo", 2), ("Note", "back")));
    Assert.AreSame((object)tracked.EntityInstance, (object)session.GetEntity<ITripLeg>(legKey, LoadFlags.Stub));
  }

  [TestMethod]
  public void AKeyThatCouldNotMeanWhatItSaysIsREFUSED() {
    var session = NewSession();

    // ----- a key for a DIFFERENT entity ------------------------------------------------------------------------------
    //
    // Where MIC diverges from Vita, which returns the given key unchecked — so the wrong one is used for the lookup and
    // surfaces later as a cast failure or as a row that was never going to be found.
    var personKey = EntitySessionHelper.CreatePrimaryKey<IPerson>(session, Guid.NewGuid());
    var wrongEntity = Assert.ThrowsExactly<ArgumentException>(
      () => session.GetEntity<IAddress>(personKey, LoadFlags.Stub));
    StringAssert.Contains(wrongEntity.Message, "Person");
    StringAssert.Contains(wrongEntity.Message, "Address");

    // ----- the wrong NUMBER of values, named against what the key IS ---------------------------------------------------
    //
    // A composite key handed one value would otherwise build a key equal to nothing, and the failure would surface much
    // later as a lookup that quietly found no row.
    var wrongCount = Assert.ThrowsExactly<ArgumentException>(
      () => EntitySessionHelper.CreatePrimaryKey<ITripLeg>(session, Guid.NewGuid()));
    StringAssert.Contains(wrongCount.Message, "TripLeg");
    StringAssert.Contains(wrongCount.Message, "TripId");
    StringAssert.Contains(wrongCount.Message, "LegNo");

    // ----- and a type that is not an entity at all --------------------------------------------------------------------
    var notAnEntity = Assert.ThrowsExactly<KeyNotFoundException>(
      () => EntitySessionHelper.CreatePrimaryKey(session, typeof(string), "x"));
    StringAssert.Contains(notAnEntity.Message, "String");
  }

  /// <summary>Nobody has to call `Sweep`: adding to the map is what triggers it.</summary>
  /// <remarks>THE TRIGGER COUNTS ENTRIES, NOT OPERATIONS, which is the whole point — a query materialising a thousand
  /// rows is ONE operation and a thousand entries, so a session reading in batches would grow forever under a trigger
  /// that counted calls. Here every record arrives through the same door and none of them is `Sweep`.
  ///
  /// The count is above `SweepFloor` and a collection is forced, so both conditions the trigger asks about are
  /// satisfied by construction rather than by luck.</remarks>
  [TestMethod]
  public void Map_SweepsItselfAsItGrows() {
    var session = NewSession();
    TrackMany(session, 400);
    Assert.AreEqual(400, session.GetEntityTracker().LoadedCount, "nothing has been collected yet, so nothing can go");

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    // Not a call to Sweep — just more of the same work the session was already doing.
    TrackMany(session, 400);

    Assert.IsTrue(session.GetEntityTracker().LoadedCount < 800,
      $"the map should have swept itself while growing; it holds {session.GetEntityTracker().LoadedCount} of 800");
  }

  // Kept out of the test body so no local holds any of them alive.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void TrackMany(EntitySession session, int count) {
    for (var i = 0; i < count; i++)
      session.Attach(Loaded(session, Person, ("Id", Guid.NewGuid()), ("LastName", "Larsen")));
  }
}
