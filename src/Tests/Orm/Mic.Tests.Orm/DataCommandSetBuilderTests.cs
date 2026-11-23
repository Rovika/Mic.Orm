using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE CONSTRUCTION HALF, PROVEN WITH NO DATABASE ANYWHERE — which is the whole reason it is separate from execution.
// A real provider is used because building a command needs a driver; nothing here connects, and the connection string
// is deliberately nonsense to make that impossible to do by accident.
[TestClass]
public class DataCommandSetBuilderTests {
  const string NoServer = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  private const string NeverConnects = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  private static Database DatabaseOf(params Type[] types) => AppAndDatabase(types).Database;

  private static (EntityApp App, Database Database) AppAndDatabase(params Type[] types) =>
    AppAndDatabase(new PostgresProvider(), types);

  private static (EntityApp App, Database Database) AppAndDatabase(DbProvider provider, params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module);

    app.TryInit();
    if (app.Initialized)
      AppInitHelper.CreateDatabase(app, new DbSettings(provider, NoServer));
    Assert.IsTrue(app.Initialized, app.Report());
    return (app, new Database(app.MappedModel(), new DbSettings(provider, NeverConnects)));
  }

  /// <summary>Public because an EMITTED class has to implement it, and a dynamic assembly cannot reach a private
  /// nested type — `TypeLoadException: attempting to implement an inaccessible interface`. Every other test here only
  /// builds SQL and never needs an instance.</summary>
  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  /// <summary>A second entity, so that "two records" and "two statements" can be told apart.</summary>
  public interface ICompany {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
  }

  [TestMethod]
  public async Task OneWriteStatement_GetsNoTransactionTwoDo() {
    // A single write statement is already atomic — the server wraps it in an implicit transaction — so asking for an
    // explicit one spends a round trip to begin and another to commit around one INSERT.
    //
    // THE UNIT IS STATEMENTS, WHICH IS NOT THE SAME AS RECORDS, and this is where the difference shows: two records of
    // ONE entity collapse into a single multi-row insert, so they need no transaction either. All or nothing is what a
    // statement already is. Two records of DIFFERENT entities are two statements and do need one.
    //
    // Asserted HERE rather than against a database, because it is a fact about what was PLANNED: a transaction that is
    // never opened leaves nothing behind to observe from the other end.
    var (app, database) = AppAndDatabase(typeof(IPerson), typeof(ICompany));
    var builder = new DataCommandSetBuilder(database);
    var session = new EntitySession(new OperationContext(app));

    var person = ((EntityBase)(object)session.NewEntity<IPerson>()).Record;
    var singleSet = new DataRequest(session, [person]);
    builder.Build(singleSet);
    Assert.IsFalse(singleSet.RequiresTransaction, "a save of one record opened a transaction to wrap one statement");
    Assert.AreEqual(1, singleSet.ExpectedDataCommandCount);

    var otherPerson = ((EntityBase)(object)session.NewEntity<IPerson>()).Record;
    var collapsed = new DataRequest(session, [person, otherPerson]);
    builder.Build(collapsed);
    Assert.AreEqual(1, collapsed.StatementCount, "two rows of one entity are one multi-row insert");
    Assert.AreEqual(1, collapsed.ExpectedDataCommandCount);
    Assert.IsFalse(collapsed.RequiresTransaction, "one statement is already all-or-nothing");

    // TWO STATEMENTS, ONE COMMAND. They cannot share a STATEMENT — different tables, different column lists — but
    // they share a round trip, and the transaction that makes them all-or-nothing is inside that command's text
    // rather than begun through the driver. So the executor still needs none of its own.
    var company = ((EntityBase)(object)session.NewEntity<ICompany>()).Record;
    var two = new DataRequest(session, [person, company]);
    builder.Build(two);
    Assert.AreEqual(2, two.StatementCount, "two entities cannot share a statement");
    Assert.AreEqual(1, two.ExpectedDataCommandCount, "but they do share a command");
    Assert.IsFalse(two.RequiresTransaction, "the batch carries its transaction in its own text");
  }

  /// <summary>Two ceilings, one method: the ROW ceiling that cuts a statement, and the STATEMENT ceiling that cuts a
  /// command.</summary>
  /// <remarks>A statement of any size is not the goal — the ceilings are real, and SQL Server's 1000-row VALUES clause
  /// and 2100 parameters are the tight ones. Neither half is reached by parameters here: PostgreSQL's spendable budget
  /// is 65335, and the 2001 updates below bind 4002 between them, a fortieth of it. So nothing but the two ceilings can
  /// be what cuts either.</remarks>
  [TestMethod]
  public async Task ABulkInsertIsCutAtTheROWCeilingAndACommandAtTheSTATEMENTCeiling() {
    var (app, database) = AppAndDatabase(typeof(IPerson));
    var builder = new DataCommandSetBuilder(database);
    var session = new EntitySession(new OperationContext(app));

    // ----- 2500 inserts, cut into statements of 1000 -----------------------------------------------------------------
    var inserts = new List<EntityRecord>();
    for (var i = 0; i < 2500; i++)
      inserts.Add(((EntityBase)(object)session.NewEntity<IPerson>()).Record);

    var bulk = new DataRequest(session, inserts);
    builder.Build(bulk);

    Assert.AreEqual(3, bulk.StatementCount, "2500 rows at 1000 per statement is three statements");
    Assert.AreEqual(1, bulk.ExpectedDataCommandCount, "and they fit one command, since PostgreSQL's parameter budget is 65535");
    Assert.IsFalse(bulk.RequiresTransaction, "the batch carries its transaction in its own text");

    // ----- and one update past MaxStatementsPerCommand starts a second command ----------------------------------------
    //
    // An update never collapses, so every record is its own statement.
    var entity = app.Model!.FindEntity(typeof(IPerson))!;
    var count = DataCommandSetBuilder.MaxStatementsPerCommand + 1;
    var updates = new List<EntityRecord>(count);
    for (var i = 0; i < count; i++)
      updates.Add(ModifiedPerson(entity, session, i));

    var cut = new DataRequest(session, updates);
    builder.Build(cut);

    Assert.AreEqual(count, cut.StatementCount);
    Assert.AreEqual(2, cut.ExpectedDataCommandCount, "the one past the ceiling starts a second command");
  }

  /// <summary>A record that looks like a row that was READ and then written to — which is what makes it an UPDATE.
  /// </summary>
  static EntityRecord ModifiedPerson(Mic.Orm.Model.EntityTypeInfo entity, EntitySession session, int index) {
    var record = new EntityRecord(entity, session) { Status = EntityStatus.Loaded };
    record.SetLoadedValue(entity.GetMember("Id"), Guid.NewGuid());
    record.SetLoadedValue(entity.GetMember("LastName"), "before");
    record.SetValue(entity.GetMember("LastName"), $"after{index}");
    Assert.AreEqual(EntityStatus.Modified, record.Status);
    return record;
  }

  /// <summary>A tight parameter budget, so the batch ceiling can be reached with a handful of records instead of tens
  /// of thousands.</summary>
  /// <remarks>205, not 5: the grouper spends up to `MaxParameters - ParameterHeadroom`, so this leaves FIVE usable and
  /// the arithmetic below is the reserve's as much as the count's — drop the headroom and four updates fit one
  /// command again.</remarks>
  private sealed class TinyBudgetProvider : PostgresProvider {
    public override int MaxParameters => 205;
  }

  /// <summary>A statement that could not be collapsed still COSTS its parameters, and the batch is cut on the real
  /// number.</summary>
  /// <remarks>It used to be cut on `Math.Max(1, …)` of a count that answered zero for anything non-collapsible — so
  /// every UPDATE, however wide, was budgeted at one parameter. The error is in the direction that OVERRUNS the
  /// server's limit: a save of many single-row updates was scored at a fraction of what it would bind.
  ///
  /// An update of `IPerson` binds two — the changed `LastName`, and the `Id` its WHERE matches on. At a budget of five
  /// that is two updates per command, so four updates are two commands; counted at one apiece they would all have
  /// fitted in one.</remarks>
  [TestMethod]
  public async Task ABatchIsCutOnWhatTheStatementsACTUALLYBind() {
    var (app, database) = AppAndDatabase(new TinyBudgetProvider(), typeof(IPerson));
    var session = new EntitySession(new OperationContext(app));
    var entity = app.Model!.FindEntity(typeof(IPerson))!;

    var records = new List<EntityRecord>();
    for (var i = 0; i < 4; i++)
      records.Add(ModifiedPerson(entity, session, i));

    var set = new DataRequest(session, records);
    new DataCommandSetBuilder(database).Build(set);

    Assert.AreEqual(4, set.StatementCount, "an update never collapses, so four records are four statements");
    Assert.AreEqual(2, set.ExpectedDataCommandCount,
      "four updates binding two parameters each do not fit one command at a spendable budget of five");
  }
}
