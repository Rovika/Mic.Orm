using Mic.Linq.Model;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sql;
using BeautyShop;

namespace Mic.Tests.Orm;

// THE ENGINE'S MODEL SURFACE, answered by the ORM's model: what a query reads of an entity, a member and a relationship,
// through the interfaces in `Mic.Linq.Model` and nothing of the model's own vocabulary — and then the SEAM, the same
// entity asked where its data physically lives.
[TestClass]
public class QueryModelTests {
  [TestMethod]
  public void OrmModel_AnswersTheLinqModelAndTheSqlTarget() {
    // ----- the ORM's model, built for real from the Beauty Shop declarations ---------------------------------------------
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());

    ILinqDataModel ormModel = app.MapToPostgres();
    var employee = ormModel.FindEntity("Employee");
    Assert.IsNotNull(employee, "the ORM model finds an entity by name");
    Assert.IsTrue(ormModel.Entities.Count > 20, "and lists them all");

    // CASE-INSENSITIVE, which is the contract and not this model's accident — a name reaching the engine came from a
    // document or a string, and neither is reliably cased the way the declaration was.
    Assert.IsNotNull(employee!.FindMember("hourlyrate"), "member lookup ignores case");
    Assert.AreEqual(typeof(decimal), employee.FindMember("HourlyRate")!.DataType);
    Assert.IsTrue(employee.FindMember("TerminatedOn")!.IsNullable, "a nullable member says so");

    AssertPrimaryKey(employee, "Id");
    AssertReference(employee, "HomeShop", "Shop");
    // The self-reference, which is a nullable one — the pairing is built the same way whatever it points at.
    AssertReference(employee, "Manager", "Employee");

    var shop = ormModel.FindEntity("Shop")!;
    var employees = shop.FindMember("Employees")!;
    Assert.AreEqual(LinqMemberKind.List, employees.Kind);
    Assert.AreEqual("Employee", employees.ListInfo?.TargetEntity.Name);
    // NULL EVEN HERE, where this model holds `IList<IEmployee>` and would happily hand it over. The client's model has
    // no type for a navigation at all, so the interface refuses one from either — see ILinqEntityMember.DataType.
    Assert.IsNull(employees.DataType, "a list has no single value type");
    Assert.IsNull(employee.FindMember("HomeShop")!.DataType, "nor does a reference");

    // ----- the SEAM: the same entity, now asked where its data physically lives ------------------------------------------
    //
    // No server is involved — mapping a model to a provider is everything that happens before one is reached — so this
    // belongs in the model-only suite rather than waiting for a database.
    var mapping = ormModel;
    var table = mapping.FindTable(employee)!;
    Assert.AreEqual("Employee", table.Name);
    Assert.AreEqual("beauty", table.Schema, "qualified by schema");
    Assert.AreEqual("HourlyRate", mapping.FindColumn(employee, employee.FindMember("HourlyRate")!)!.Name);
  }

  private static void AssertPrimaryKey(ILinqEntity entity, params string[] memberNames) {
    CollectionAssert.AreEqual(memberNames, entity.PrimaryKeyMembers.Select(m => m.Name).ToArray(),
      $"'{entity.Name}' primary key members");
    foreach (var member in entity.PrimaryKeyMembers)
      Assert.AreEqual(LinqMemberKind.Value, member.Kind, "a key member is a value, expanded from any reference");
  }

  // THE SHARED WALK. Everything a join needs, asked of a reference member without knowing which model answered.
  private static void AssertReference(ILinqEntity owner, string memberName, string targetName) {
    var member = owner.FindMember(memberName);
    Assert.IsNotNull(member, $"'{owner.Name}.{memberName}'");
    Assert.AreEqual(LinqMemberKind.Reference, member!.Kind);
    Assert.AreEqual(targetName, member.GetTargetEntity()?.Name);

    // TWO ORDERED PARALLEL LISTS, which is the normal form the two models are reduced to: FromMembers[i] against
    // ToMembers[i] IS the join condition, and a pairing of unequal halves would not be one.
    var pairing = member.ReferenceInfo;
    Assert.IsNotNull(pairing, $"'{owner.Name}.{memberName}' has a reference info");
    Assert.AreEqual(targetName, pairing!.TargetEntity.Name, "the info names the target");
    Assert.AreNotEqual(0, pairing.FromMembers.Count, $"'{owner.Name}.{memberName}' has a key behind it");
    Assert.AreEqual(pairing.FromMembers.Count, pairing.ToMembers.Count, "the two halves of the pairing");
    CollectionAssert.AreEqual(
      pairing.TargetEntity.PrimaryKeyMembers.Select(m => m.Name).ToArray(),
      pairing.ToMembers.Select(m => m.Name).ToArray(),
      "the far half of the pairing is the target's primary key");
    foreach (var from in pairing.FromMembers)
      Assert.AreEqual(LinqMemberKind.Value, from.Kind, "the near half is columns, never the reference itself");
  }
}
