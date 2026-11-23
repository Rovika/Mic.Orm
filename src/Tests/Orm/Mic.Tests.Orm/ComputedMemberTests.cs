using Mic.Orm;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// A MEMBER A METHOD COMPUTES, without a server: where the method is found, what the model records, that the value is
// the method's on every read and cannot be assigned, and what is refused.
[TestClass]
public class ComputedMemberTests {
  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string FirstName { get; set; }
    [Size(50)] string LastName { get; set; }

    [Computed(nameof(PersonFunctions.GetFullName), typeof(PersonFunctions))]
    string FullName { get; }

    /// <summary>Computed by the module's own method, named without a class.</summary>
    [Computed("GetInitials")]
    string Initials { get; }

    /// <summary>Computed, and kept in a column the save writes.</summary>
    [Computed(nameof(PersonFunctions.GetNameLength), typeof(PersonFunctions), Stored = true)]
    int NameLength { get; }
  }

  public static class PersonFunctions {
    public static string GetFullName(IPerson person) => $"{person.FirstName} {person.LastName}";
    public static int GetNameLength(IPerson person) => person.LastName.Length;
  }

  /// <summary>The module the entity belongs to, with a function of its own.</summary>
  public class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) { }
    private static string GetInitials(IPerson person) => $"{person.FirstName[0]}{person.LastName[0]}";
  }

  static EntityApp NewApp(params Type[] entities) {
    var app = new EntityApp("Computed", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new PeopleModule();
    module.RegisterEntities(entities.Length > 0 ? entities : [typeof(IPerson)]);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void ComputedMember_IsTheMethodsValueOnEveryRead() {
    var app = NewApp();
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IPerson))!;
    var fullName = entity.GetMember("FullName");
    Assert.AreEqual(MemberKind.Transient, fullName.Kind);
    Assert.AreEqual(nameof(PersonFunctions.GetFullName), fullName.Computed!.Method.Name);
    Assert.AreEqual(typeof(PeopleModule), entity.GetMember("Initials").Computed!.Method.DeclaringType,
      "found on the module when no class is named");
    var table = app.MapToPostgres().GetTable(entity)!;
    Assert.IsNull(table.FindColumn("FullName"), "no column behind it");

    // ----- a persisted one keeps its column, read like any other by a query ---------------------------------------
    var nameLength = entity.GetMember("NameLength");
    Assert.AreEqual(MemberKind.Column, nameLength.Kind);
    Assert.IsTrue(nameLength.Computed!.Stored);
    Assert.IsNotNull(table.FindColumn("NameLength"));
    Assert.AreEqual(0, app.Diagnostics().Count(d => d.Severity == Severity.Warning), app.Report());

    var session = app.WithSqlite().OpenSession();
    var person = session.NewEntity<IPerson>();
    person.FirstName = "Ann";
    person.LastName = "Lee";
    Assert.AreEqual("Ann Lee", person.FullName);
    Assert.AreEqual("AL", person.Initials);
    Assert.AreEqual(3, person.NameLength);
    person.LastName = "Park";
    Assert.AreEqual("Ann Park", person.FullName, "computed again on the next read");
    Assert.AreEqual(4, person.NameLength);
    var record = ((EntityBase)person).Record;
    Assert.ThrowsExactly<InvalidOperationException>(() => record.SetValue(fullName, "x"));
    Assert.ThrowsExactly<InvalidOperationException>(() => record.SetValue(nameLength, 9));

    // ----- the slot is written when the record is about to be saved ----------------------------------------------
    Assert.AreEqual(0, record.GetRawValue(nameLength), "the type's default in the slot before a save");
    MemberValueAccessorHelper.ComputeStoredValues(record);
    Assert.AreEqual(4, record.GetRawValue(nameLength));
  }

  [TestMethod]
  public void ComputedMember_RefusesWhatItCannotBe() {
    var refused = NewApp(typeof(IBadPerson));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "has no static method of");
    StringAssert.Contains(report, "must take the entity");
    StringAssert.Contains(report, "with a setter");
  }

  public interface IBadPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    [Computed("NoSuchMethod", typeof(PersonFunctions))] string Missing { get; }
    [Computed(nameof(PersonFunctions.GetFullName), typeof(PersonFunctions))] string WrongEntity { get; }
    [Computed(nameof(BadFunctions.GetLength), typeof(BadFunctions))] string Settable { get; set; }
  }

  public static class BadFunctions {
    public static int GetLength(IBadPerson person) => person.LastName.Length;
    public static string GetLength(IBadPerson person, int unused) => "";
  }
}
