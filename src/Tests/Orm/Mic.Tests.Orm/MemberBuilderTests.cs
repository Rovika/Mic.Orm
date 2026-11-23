using Mic.Orm;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Tests.Orm;

// The member pass: interface properties become EntityMemberInfo, with their kind, CLR type, nullability and size.
// Driven through a real EntityApp, because the path an application takes is the one that has to work.
[TestClass]
public class MemberBuilderTests {
  // A base interface that is NOT registered — so it is not an entity, and its members are flattened into whatever
  // derives from it. Registration, not position in a chain, is what makes an entity.
  private interface IAppEntityBase {
    [PrimaryKey] Guid Id { get; set; }
    DateTime CreatedOn { get; set; }
  }

  private interface IAddress {
    [PrimaryKey] Guid Id { get; set; }
    string City { get; set; }
    IPerson? Owner { get; set; }          // what makes IPerson.PreviousAddresses a one-to-many
  }

  private interface IPerson : IAppEntityBase {
    [Size(80)] string LastName { get; set; }
    string? MiddleName { get; set; }
    [Nullable] string FirstName { get; set; }
    int Age { get; set; }
    int? Rating { get; set; }
    decimal Salary { get; set; }
    [Column(Precision = 9, Scale = 2)] decimal Bonus { get; set; }
    [Unlimited] string Notes { get; set; }
    [NoUpdate] DateTime HiredOn { get; set; }
    [Auto(AutoType.RowVersion)] long Version { get; set; }
    [NoColumn] string FullName { get; }
    IAddress Address { get; set; }
    IList<IAddress> PreviousAddresses { get; }
  }

  // A registered base — this one IS an entity, so it becomes the model's base type rather than being flattened.
  private interface IDocument {
    [PrimaryKey] Guid Id { get; set; }
    string Title { get; set; }
  }

  private interface IInvoice : IDocument {
    decimal Amount { get; set; }
  }

  private static EntityApp Build(Action<EntityApp>? configure = null, params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module, "s");
    configure?.Invoke(app);
    app.TryInit();
    return app;
  }

  private static EntityTypeInfo Person(EntityApp app) => app.Model!.FindEntity("Person")!;

  private static EntityApp People(Action<EntityApp>? configure = null) =>
    Build(configure, typeof(IPerson), typeof(IAddress));

  // ----- ONE model, every facet the member pass produces ---------------------------------------------------------------
  //
  // Ten methods, all asking the same Person a different question about the same build.
  [TestMethod]
  public void Property_BecomesAMemberWithKindNullabilitySizePolicy() {
    var app = People();
    Assert.IsTrue(app.Initialized, app.Report());
    var person = Person(app);

    Assert.IsNotNull(person.FindMember("LastName"));
    Assert.IsNotNull(person.FindMember("Address"));

    // ----- inherited members are FLATTENED in -----------------------------------------------------------------------
    //
    // IAppEntityBase is not registered, so it is not an entity and has no table; Person owns Id and CreatedOn outright.
    // This is what makes the shared-base idiom work with no special case.
    Assert.IsNotNull(person.FindMember("Id"));
    Assert.IsNotNull(person.FindMember("CreatedOn"));

    // ----- each member is CLASSIFIED --------------------------------------------------------------------------------
    Assert.AreEqual(MemberKind.Column, person.GetMember("LastName").Kind);
    Assert.AreEqual(MemberKind.Reference, person.GetMember("Address").Kind);
    Assert.AreEqual(MemberKind.List, person.GetMember("PreviousAddresses").Kind);
    Assert.AreEqual(MemberKind.Transient, person.GetMember("FullName").Kind);
    Assert.IsNotNull(person.GetMember("Address").TargetEntity, "kind and target stay independent");

    // ----- NULLABILITY comes from the CLR type, unless an attribute says otherwise -----------------------------------
    Assert.IsTrue(person.GetMember("MiddleName").IsNullable);     // string?
    Assert.IsTrue(person.GetMember("Rating").IsNullable);         // int?
    Assert.IsFalse(person.GetMember("LastName").IsNullable);      // string
    Assert.IsFalse(person.GetMember("Age").IsNullable);           // int
    Assert.IsTrue(person.GetMember("FirstName").IsNullable, "a non-nullable string carrying [Nullable]");

    // ----- SIZE and PRECISION come from the attribute or the application default -------------------------------------
    Assert.AreEqual(80, person.GetMember("LastName").Size);
    Assert.AreEqual(50, person.GetMember("MiddleName").Size);     // the application default
    Assert.AreEqual(18, person.GetMember("Salary").Precision);
    Assert.AreEqual(4, person.GetMember("Salary").Scale);
    Assert.AreEqual(9, person.GetMember("Bonus").Precision);
    Assert.AreEqual(2, person.GetMember("Bonus").Scale);

    // …and [Unlimited] suppresses the default size outright.
    var notes = person.GetMember("Notes");
    Assert.IsTrue(notes.MemberFlags.HasFlag(MemberFlags.Unlimited));
    Assert.AreEqual(0, notes.Size);

    // ----- and the POLICY flags are carried through -------------------------------------------------------------------
    Assert.IsTrue(person.GetMember("HiredOn").MemberFlags.HasFlag(MemberFlags.NoUpdate));
    // Auto(RowVersion) — the ORM counts it, not the database: an ordinary column the update sets one up and matches on.
    var version = person.GetMember("Version");
    Assert.AreEqual(AutoType.RowVersion, version.AutoValue);
    Assert.IsFalse(version.IsDbGenerated);
    Assert.AreSame(version, person.RowVersionMember);
    Assert.IsFalse(person.GetMember("Age").IsDbGenerated);
  }

  [TestMethod]
  public void ARegisteredBaseInterfaceIsJUSTANOTHERENTITY() {
    // The point that must not drift: this is MEMBER inheritance, never entity-hierarchy mapping. A base interface
    // that happens to be registered changes nothing — Document and Invoice are two independent tables, and Invoice's
    // columns merely happen to be a superset. No TPT, no shared table, no base link. A relational database is not an
    // object graph.
    var app = Build(null, typeof(IDocument), typeof(IInvoice));
    Assert.IsTrue(app.Initialized, app.Report());
    var invoice = app.Model!.FindEntity("Invoice")!;
    var document = app.Model.FindEntity("Document")!;

    CollectionAssert.AreEqual(new[] { "Amount", "Id", "Title" }, invoice.Members.Select(m => m.Name).Order().ToArray());
    CollectionAssert.AreEqual(new[] { "Id", "Title" }, document.Members.Select(m => m.Name).Order().ToArray());

    // Each table owns its own storage: the same member name is a DIFFERENT member with its own slot in each.
    Assert.AreNotSame(document.GetMember("Title"), invoice.GetMember("Title"));

    // ----- and registration ORDER does not matter --------------------------------------------------------------------
    //
    // Nothing waits on anything: flattening leaves entities independent of one another.
    var reversed = Build(null, typeof(IInvoice), typeof(IDocument)).Model!.FindEntity("Invoice")!;
    CollectionAssert.AreEqual(
      invoice.Members.Select(m => m.Name).ToArray(),
      reversed.Members.Select(m => m.Name).ToArray());
  }

  [TestMethod]
  public void AppDefaults_AreChangeable() {
    var person = Person(People(app => {
      app.Settings.DefaultStringSize = 250;
      app.Settings.DefaultDecimalPrecision = 12;
      app.Settings.DefaultDecimalScale = 3;
    }));
    Assert.AreEqual(250, person.GetMember("MiddleName").Size);
    Assert.AreEqual(12, person.GetMember("Salary").Precision);
    Assert.AreEqual(3, person.GetMember("Salary").Scale);
  }

  private interface IRawDocument {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    [Json] string? Raw { get; set; }
    [Json, Size(200)] string? Short { get; set; }
  }

  // A JSON document held as text is a string member whose column has no size, so the default size is not its limit.
  [TestMethod]
  public void JsonText_TakesNoDefaultSize() {
    var document = Build(null, typeof(IRawDocument)).Model!.FindEntity("RawDocument")!;
    Assert.AreEqual(50, document.GetMember("Name").Size);
    Assert.AreEqual(0, document.GetMember("Raw").Size);
    Assert.AreEqual(200, document.GetMember("Short").Size, "a size the declaration states is kept");
  }

  // ----- what is reported rather than thrown -----------------------------------------------------------------------

  private interface IUnmappableRow {
    [PrimaryKey] Guid Id { get; set; }
    System.Text.StringBuilder Whatever { get; set; }
  }

  private interface IUnregisteredRef {
    [PrimaryKey] Guid Id { get; set; }
    IAddress Address { get; set; }
  }

  private interface ISizeTwice {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)][Column(Size = 40)] string Name { get; set; }
  }

  // Legal C#: interfaces allow multiple inheritance, and a name collision across two bases is not an error where it
  // is DECLARED — only at the call site, where `x.Name` is ambiguous and needs a cast. So this compiles, reflection
  // reports both properties, and the model has to say something rather than silently keeping whichever came first.
  private interface INamedText { string Name { get; set; } }
  private interface INamedNumber { int Name { get; set; } }
  private interface IClashingRow : INamedText, INamedNumber {
    [PrimaryKey] Guid Id { get; set; }
  }

  // Six methods, each a build and one message. The messages are the whole content: none of these models ever reaches a
  // database, so nothing downstream can report on them.
  [TestMethod]
  public void Member_TheBuilderCannotMakeIsReported() {
    // ----- one NAME inherited with two TYPES --------------------------------------------------------------------------
    var clashing = Build(null, typeof(IClashingRow));
    Assert.IsFalse(clashing.Initialized);
    Assert.IsTrue(clashing.Diagnostics().Any(d => d.Message.Contains("different types")));

    // ----- a CLR type with no column behind it -------------------------------------------------------------------------
    var unmappable = Build(null, typeof(IUnmappableRow));
    Assert.IsFalse(unmappable.Initialized);
    Assert.IsTrue(unmappable.Diagnostics().Any(d => d.Message.Contains("cannot map")));

    // ----- an entity type nobody registered ------------------------------------------------------------------------------
    //
    // The mistake worth naming precisely: it looks like a reference and is not one, and the fix is to register the
    // target rather than to change the member.
    var unregistered = Build(null, typeof(IUnregisteredRef));
    Assert.IsFalse(unregistered.Initialized);
    Assert.IsTrue(unregistered.Diagnostics().Any(d => d.Message.Contains("not a registered entity")));

    // ----- a size stated TWICE ---------------------------------------------------------------------------------------------
    //
    // No precedence: a rule that silently picked a winner would be invisible in the source and then depended on.
    var twice = Build(null, typeof(ISizeTwice));
    Assert.IsFalse(twice.Initialized);
    Assert.IsTrue(twice.Diagnostics().Any(d => d.Message.Contains("stated twice")));

    // ----- and ONE run reports every fault, rather than the first -----------------------------------------------------------
    var several = Build(null, typeof(IUnmappableRow), typeof(IUnregisteredRef), typeof(ISizeTwice));
    Assert.AreEqual(3, several.Diagnostics().Count(d => d.Severity == Severity.Error));
  }
}
