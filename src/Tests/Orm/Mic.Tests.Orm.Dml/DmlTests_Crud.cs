using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.SqlServer;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE FIRST TIME THE RUNTIME TOUCHES A DATABASE. Everything before this was the schema half; here a session's changes
// become statements and the statements run — against every vendor in the run, which is what makes it an acceptance
// test rather than a demonstration.
public abstract partial class DmlTests {
  /// <summary>Shuffle the pending records, so a save that comes out right did so because it was SORTED.</summary>
  /// <remarks>Without this an ordering test passes as easily by accident as by design — records happen to be created
  /// parent-first often enough that creation order is right about half the time, and a sort that did nothing at all
  /// would pass a good share of the suite.
  ///
  /// Seeded, and the seed is in the failure message: a shuffle that finds a bug and cannot be repeated is worth much
  /// less than one that can.</remarks>
  static void Shuffle(EntitySession session, int seed) {
    const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var records = (List<EntityRecord>)typeof(EntityTracker).GetField("_modified", Hidden)!.GetValue(session.GetEntityTracker())!;
    var random = new Random(seed);
    for (var i = records.Count - 1; i > 0; i--) {
      var j = random.Next(i + 1);
      (records[i], records[j]) = (records[j], records[i]);
    }
  }

  /// <summary>One column of one row as the DATABASE holds it — read through a session of its own, so the test sees the
  /// row as another observer would and never through the identity map of the session that wrote it.</summary>
  Task<object?> ScalarAsync(string table, string column, Guid id) => ScalarAsync<object>(table, column, id);

  async Task<T?> ScalarAsync<T>(string table, string column, Guid id) {
    await using var probe = NewSession();
    return await probe.RunSqlScalarAsync<T>(
      $"SELECT {Db.Quote(column)} FROM {Db.GetTableRef(table)} WHERE {Db.Quote("Id")} = @id", ("id", id));
  }

  /// <summary>The same observer, for a statement written out: one value, or a row count.</summary>
  async Task<T?> ProbeScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters) {
    await using var probe = NewSession();
    return await probe.RunSqlScalarAsync<T>(sql, parameters);
  }

  async Task<int> ProbeNonQueryAsync(string sql, params (string Name, object? Value)[] parameters) {
    await using var probe = NewSession();
    return await probe.RunSqlNonQueryAsync(sql, parameters);
  }

  // ----- one record at a time ----------------------------------------------------------------------------------------

  /// <summary>ONE ROW, FOLLOWED THROUGH ITS WHOLE LIFE — insert, no-op, update, delete.</summary>
  /// <remarks>Five methods, each of which created a person and saved it to assert one thing about one of those steps.
  /// A row's life is a SEQUENCE, so it reads better as one, and every step's assertions are made where that step
  /// happens.
  ///
  /// `StatusBeforeSave` is checked at each: settling makes every saved record Loaded, which is right and destroys the
  /// one thing an after-save handler most needs — whether this was an insert, an update or a delete. Auditing and
  /// cache invalidation branch on exactly that.</remarks>
  [TestMethod]
  public async Task Row_InsertedUpdatedDeletedAndSaysWhich() {
    await using var session = NewSession();

    // ----- a save with NOTHING pending does nothing -----------------------------------------------------------------
    await session.SaveChangesAsync();
    Assert.AreEqual(0, session.GetEntityTracker().Modified.Count);

    // ----- a new entity BECOMES a row -------------------------------------------------------------------------------
    session.LogMessage("a new entity becomes a row");
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    person.Rank = 3;
    var record = ((EntityBase)person).Record;
    Assert.IsNull(record.StatusBeforeSave, "nothing has saved it yet");

    await session.SaveChangesAsync();

    Assert.AreEqual("Larsen", await ScalarAsync("Person", "LastName", person.Id));
    Assert.AreEqual(EntityStatus.Loaded, record.Status,
      "once the database holds it, it is loaded rather than new");
    Assert.AreEqual(EntityStatus.New, record.StatusBeforeSave, "it was an INSERT");

    // ----- and saving AGAIN does not write the same change twice -----------------------------------------------------
    //
    // A second INSERT of the same row would violate the primary key.
    await session.SaveChangesAsync();
    Assert.AreEqual(0, session.GetEntityTracker().Modified.Count);

    // ----- a change is an UPDATE ------------------------------------------------------------------------------------
    session.LogMessage("a change is an UPDATE");
    person.LastName = "Someone Else";
    await session.SaveChangesAsync();

    Assert.AreEqual(EntityStatus.Modified, record.StatusBeforeSave);
    Assert.AreEqual("Someone Else", await ScalarAsync("Person", "LastName", person.Id));

    // ----- and a DELETE takes the row and the identity with it --------------------------------------------------------
    session.LogMessage("a DELETE takes the row and the identity");
    session.DeleteEntity(person);
    await session.SaveChangesAsync();

    Assert.AreEqual(EntityStatus.Deleted, record.StatusBeforeSave);
    Assert.IsNull(await ScalarAsync("Person", "LastName", person.Id));
    Assert.IsNull(session.GetEntity<Dml.IPerson>(person.Id),
      "a deleted row must not still be found in the identity map");
  }

  /// <summary>The session keeps what its last request actually ran.</summary>
  /// <remarks>A DEBUGGING FACILITY, and this is the only place it can be proven: the snapshots are taken by the
  /// executor, one statement before the driver command is disposed, so nothing short of a real round trip exercises
  /// it. Asserted against every vendor because the SQL is the dialect's, and Oracle is the reason the snapshot is a
  /// copy rather than the command — its driver resets a disposed command's state.</remarks>
  [TestMethod]
  public async Task Session_RemembersWhatItLastRan() {
    await using var session = NewSession();

    Assert.AreEqual(0, session.LastCommands.Count, "nothing has run yet");

    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    await session.SaveChangesAsync();

    var command = session.LastCommands.Single();
    StringAssert.Contains(command.Sql, "INSERT", StringComparison.OrdinalIgnoreCase);
    StringAssert.Contains(command.Sql, "Person");
    Assert.AreEqual(1, command.RowCount);
    Assert.IsTrue(command.TimeMs >= 0);

    // REPLACED, not appended — a second save says what the second save did.
    person.Rank = 7;
    await session.SaveChangesAsync();
    StringAssert.Contains(session.LastCommands.Single().Sql, "UPDATE", StringComparison.OrdinalIgnoreCase);

    // And the same route from an entity, which is where a developer holding one at a breakpoint will look.
    Assert.AreEqual(session.LastCommands.Count, EntityHelper.GetLastCommands(person).Count);
  }

  /// <summary>A failed save says what it was carrying, not just that it failed.</summary>
  /// <remarks>The DDL failures elsewhere in this suite are raw SQL with the values written into the text; a SAVE binds
  /// them, so the statement alone reads `VALUES (@p0, @p1)` and tells you nothing about the row that was refused. This
  /// is the case the parameter rendering exists for, and the only one that proves the values are still on the command
  /// when the exception is built.</remarks>
  [TestMethod]
  public async Task AFailedSaveSaysWhatItWasCarrying() {
    var id = Guid.NewGuid();

    await using (var first = NewSession()) {
      var person = first.NewEntity<Dml.IPerson>();
      person.Id = id;
      person.LastName = "Larsen";
      await first.SaveChangesAsync();
    }

    await using var second = NewSession();
    var duplicate = second.NewEntity<Dml.IPerson>();
    duplicate.Id = id;                        // the same primary key — the server refuses it
    duplicate.LastName = "Larsson";

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(() => second.SaveChangesAsync());

    Assert.IsNotNull(ex.CommandSummary);
    StringAssert.Contains(ex.CommandSummary!, "INSERT", StringComparison.OrdinalIgnoreCase);
    StringAssert.Contains(ex.CommandSummary!, "'Larsson' (String)", "the value that was refused, and its type");
    StringAssert.Contains(ex.Message, ex.CommandSummary!, "a log printing only the message must lose nothing");
  }

  [TestMethod]
  public async Task Update_NamesOnlyTheChangedMembers() {
    // What the changed mask has been for since it was built, and its first real use. An update naming every column
    // would write values nobody touched.
    await using var session = NewSession();
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    person.Rank = 3;
    await session.SaveChangesAsync();

    // Somebody else changes a column this session never touches.
    await ProbeNonQueryAsync($"UPDATE {Db.GetTableRef("Person")} SET {Db.Quote("Rank")} = 99 WHERE {Db.Quote("Id")} = @id",
      ("id", person.Id));

    person.LastName = "Someone Else";
    await session.SaveChangesAsync();

    Assert.AreEqual("Someone Else", await ScalarAsync("Person", "LastName", person.Id));
    Assert.AreEqual(99, await ScalarAsync<int>("Person", "Rank", person.Id),
      "the update wrote a column this session never changed");
  }

  // ----- keys the server makes -------------------------------------------------------------------------------------

  [TestMethod]
  public async Task AKeyTheSERVERMakesComesBackOnTheRecordAndMakesItTRACKABLE() {
    // The prediction this task was written with: Guid keys dodge this machinery entirely, so it had to be asked for
    // deliberately. Without the key coming back, the row is worthless to the session — nothing can be tracked, and
    // nothing that references it can be written.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "Broken tap";
    Assert.AreEqual(0, ticket.Id, "nothing has generated one yet");

    await session.SaveChangesAsync();

    Assert.AreNotEqual(0, ticket.Id, "the server made a key and it did not come back");
    Assert.AreEqual("Broken tap", await ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Title")} FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Id")} = @id", ("id", ticket.Id)));

    // ----- and the record is TRACKED under it --------------------------------------------------------------------------
    //
    // A new record cannot be in the identity map — it has no identity — and this is the moment it gets one. If the key
    // never arrived, the record would settle to Loaded under an empty key and a later read would fetch a second object
    // for the same row.
    Assert.AreSame(ticket, session.GetEntity<Dml.ITicket>(ticket.Id),
      "the saved record was not tracked under the key the server gave it");
    Assert.IsFalse(((EntityBase)ticket).Record.PrimaryKey.IsEmpty);
  }

  [TestMethod]
  public async Task GeneratedIds_EachFindTheirOwnRecord() {
    // Position in the command list would say which key belongs to which record today, and stop saying so the moment
    // one record needs two statements. The command carries its record instead.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    var tickets = new List<Dml.ITicket>();
    for (var i = 0; i < 5; i++) {
      var ticket = session.NewEntity<Dml.ITicket>();
      ticket.Title = "Ticket" + i;
      tickets.Add(ticket);
    }
    await session.SaveChangesAsync();

    var ids = tickets.Select(t => t.Id).ToList();
    CollectionAssert.AllItemsAreUnique(ids);
    for (var i = 0; i < tickets.Count; i++)
      Assert.AreEqual("Ticket" + i, await ProbeScalarAsync<string>(
        $"SELECT {Db.Quote("Title")} FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Id")} = @id", ("id", ids[i])),
        $"the key for Ticket{i} landed on the wrong record");
  }

  [TestMethod]
  public async Task AnUpdateSavedAlone_LeavesTheServersKeyOnTheRecord() {
    // An update saved alone comes back with its row count, 1, which is not a key.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();
    var tickets = new List<Dml.ITicket>();
    for (var i = 0; i < 2; i++) {
      tickets.Add(session.NewEntity<Dml.ITicket>());
      tickets[i].Title = "Kept" + i;
    }
    await session.SaveChangesAsync();
    var ticket = tickets.MaxBy(t => t.Id)!;
    var id = ticket.Id;
    Assert.IsTrue(id > 1, "the second of two keys");

    ticket.Title = "Changed";
    await session.SaveChangesAsync();
    Assert.AreEqual(1, session.GetLastCommands().Count);
    Assert.AreEqual(id, ticket.Id, "the row count was taken for the key");
    Assert.AreSame(ticket, session.GetEntity<Dml.ITicket>(id));
    Assert.AreEqual("Changed", await ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Title")} FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Id")} = @id", ("id", id)));
  }

  [TestMethod]
  public async Task AChildOfAGeneratedIdGetsTheIDTheServerMade() {
    // The case a Guid key never meets: the child's foreign key needs a value that does not exist until the parent's
    // insert has RUN, so nothing can bind it when the statements are built.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "Broken tap";
    var comment = session.NewEntity<Dml.IComment>();
    comment.Text = "Still dripping";
    comment.Ticket = ticket;

    await session.SaveChangesAsync();

    Assert.AreNotEqual(0, ticket.Id);
    Assert.AreEqual(ticket.Id, await ProbeScalarAsync<long>(
      $"SELECT {Db.Quote("Ticket_Id")} FROM {Db.GetTableRef("Comment")} WHERE {Db.Quote("Id")} = @id", ("id", comment.Id)),
      "the child was written with the key its parent had BEFORE the server made one");
  }

  // ----- keys the ORM makes ----------------------------------------------------------------------------------------

  [TestMethod]
  public async Task AKeyTheORMMakesExistsBeforeAnythingIsSent() {
    // The whole argument for a client-generated key, as one assertion: the id is real the moment the entity is, so a
    // graph can be wired together in memory and a save has nothing to read back.
    await using var session = NewSession();

    var invoice = session.NewEntity<Dml.IInvoice>();
    Assert.AreNotEqual(Guid.Empty, invoice.Id, "nothing assigned a key at creation");
    var assigned = invoice.Id;

    invoice.Number = "INV-1";
    await session.SaveChangesAsync();

    Assert.AreEqual(assigned, invoice.Id, "the save changed a key the client had already chosen");
    Assert.AreEqual("INV-1", await ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Number")} FROM {Db.GetTableRef("Invoice")} WHERE {Db.Quote("Id")} = @id", ("id", assigned)),
      "the row is not under the key the client generated");
  }

  [TestMethod]
  public async Task OrmMadeKeys_SortAsMade() {
    // THE TEST THE BYTE ORDER NEEDS, and the only one that can catch it. A Guid written in .NET's byte layout and read
    // back in the same layout round-trips PERFECTLY and still sorts at random, because .NET puts the timestamp
    // somewhere other than the leading bytes — so every value assertion passes while the one reason v7 was chosen is
    // quietly absent. The server's own ordering is the only observable difference there is.
    //
    // ONE SAVE PER ROW, AND SPACED, and both halves are needed. A v7 carries a MILLISECOND, and within one millisecond
    // the remaining bits are random — so what v7 promises is only that a key made in a LATER millisecond sorts after
    // one made earlier, which is what index locality rests on. The gap is comfortably past the coarsest tick Windows
    // is known to have, 15.6ms and not 1ms.
    //
    // And a session mints every key of one save with ONE instant, so that a save's keys agree with its own
    // `CreatedOn` stamps. Four saves is what makes four distinct instants; the order of the keys of ONE save is the
    // next test's.
    //
    // THE LAYOUT IS THE PROVIDER'S, which is what makes this run on all five with nothing registered: SQL Server
    // compares a Guid by its LAST six bytes, so a v7 is random to its index, and its provider declares the v8 layout
    // that puts the timestamp where that server looks.
    var app = NewApp();
    var session = NewContext(app).OpenSession();

    // TAGGED, and read back by the tag. The database is shared, so "every invoice" is not a question this test may
    // ask — and the guid factory it just replaced is why it takes an application of its own as well.
    var tag = "INV-" + Guid.NewGuid().ToString("N")[..8] + "-";
    for (var i = 0; i < 4; i++) {
      if (i > 0)
        await Task.Delay(30);
      var invoice = session.NewEntity<Dml.IInvoice>();
      invoice.Number = tag + i;
      await session.SaveChangesAsync();
    }

    var numbers = await Db.ColumnAsync(
      $"SELECT {Db.Quote("Number")} FROM {Db.GetTableRef("Invoice")} "
      + $"WHERE {Db.Quote("Number")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    CollectionAssert.AreEqual(Enumerable.Range(0, 4).Select(i => tag + i).ToList(), numbers,
      "the server's own order over the keys is not the order they were generated in");
  }

  // The keys of ONE save share an instant, and each carries its place among them in the bits the server compares
  // after the time: the server's own order over them is the order the entities were created in.
  [TestMethod]
  public async Task OrmMadeKeys_OfOneSave_SortAsCreated() {
    var session = NewContext(App).OpenSession();
    var tag = "SEQ-" + Guid.NewGuid().ToString("N")[..8] + "-";
    const int count = 60;
    for (var i = 0; i < count; i++)
      session.NewEntity<Dml.IInvoice>().Number = tag + i.ToString("00");
    await session.SaveChangesAsync();

    var numbers = await Db.ColumnAsync(
      $"SELECT {Db.Quote("Number")} FROM {Db.GetTableRef("Invoice")} "
      + $"WHERE {Db.Quote("Number")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    CollectionAssert.AreEqual(Enumerable.Range(0, count).Select(i => tag + i.ToString("00")).ToList(), numbers,
      "the server's own order over the keys of one save is not the order the entities were created in");
  }

  [TestMethod]
  public async Task Orm_StampsWhoAndWhenAtBothEnds() {
    // ONE INSTANT FOR EVERYTHING A SESSION CREATES UNTIL ITS SAVE: taken when the first new entity is created, carried
    // by every key generated after it, written into the created and updated stamps by the save, and cleared by the
    // save. The stamps are written by the save; the instant they get is the one the keys already carry.
    //
    // Equal to the tick on an insert is the property that matters: it is what makes "has this row ever been changed?"
    // answerable by comparing the two.
    var user = new UserInfo(Guid.NewGuid(), "victoria");
    var session = NewContext(App, user).OpenSession();

    var audited = session.NewEntity<Dml.IAuditedRow>();
    audited.Note = "first";
    var afterFirst = DateTime.UtcNow;
    Assert.AreEqual(default, audited.CreatedOn, "created-on is written by the save");
    // A second entity, created later in the same session.
    await Task.Delay(40);
    var later = session.NewEntity<Dml.IAuditedRow>();
    later.Note = "later";

    await session.SaveChangesAsync();

    var createdOn = audited.CreatedOn;
    Assert.AreNotEqual(default, createdOn, "created-on was not stamped by the insert");
    Assert.IsTrue(createdOn <= afterFirst, $"the instant is the first entity's: {createdOn:O} is after {afterFirst:O}");
    Assert.AreEqual(createdOn, later.CreatedOn, "one instant for every entity created before the save");
    // The keys were generated at the same instant, and carry it to the millisecond.
    var keyTime = EntityHelper.GetTimestamp(audited.Id);
    Assert.IsNotNull(keyTime, "the key carries no time: " + audited.Id);
    Assert.AreEqual(createdOn.Ticks / TimeSpan.TicksPerMillisecond, keyTime.Value.Ticks / TimeSpan.TicksPerMillisecond);
    Assert.AreEqual(keyTime, EntityHelper.GetTimestamp(later.Id));
    Assert.AreEqual("victoria", audited.CreatedBy);
    Assert.AreEqual(createdOn, audited.UpdatedOn, "an untouched row's two stamps must be the same instant");
    Assert.AreEqual(user.UserId, audited.UpdatedById, "updated-by was not stamped on the insert");
    var firstUpdate = audited.UpdatedOn;
    Assert.AreNotEqual(default, firstUpdate, "updated-on was not stamped on the insert");

    // And again on an ordinary change, which is the half that cannot be done at creation. The save cleared the
    // instant, so an entity created now gets a later one.
    await Task.Delay(30);
    audited.Note = "second";
    var next = session.NewEntity<Dml.IAuditedRow>();
    next.Note = "next";
    await session.SaveChangesAsync();

    Assert.IsTrue(next.CreatedOn > createdOn, $"the save did not clear the instant: {next.CreatedOn:O}");
    Assert.IsTrue(EntityHelper.GetTimestamp(next.Id) > keyTime);
    Assert.IsTrue(audited.UpdatedOn > firstUpdate,
      $"updated-on did not move on an update: {audited.UpdatedOn:O} is not after {firstUpdate:O}");
    Assert.AreEqual(createdOn, audited.CreatedOn, "an update rewrote created-on");
    Assert.AreEqual("second", await ScalarAsync("AuditedRow", "Note", audited.Id));
  }

  // ----- reading ---------------------------------------------------------------------------------------------------

  /// <summary>ONE written row, read back in a fresh session and followed through.</summary>
  /// <remarks>Four methods, each of which wrote a person and opened two sessions to assert one thing about the read.
  /// The write is the cost; the assertions are not.</remarks>
  [TestMethod]
  public async Task Row_ComesBackAsAnEntityOnce() {
    // Every value goes through the provider's read conversion, which is where a Guid stored as text and a bool stored
    // as a number become what the member says they are.
    await using var writer = NewSession();
    var written = writer.NewEntity<Dml.IPerson>();
    written.Id = Guid.NewGuid();
    written.LastName = "Larsen";
    written.Rank = 7;
    await writer.SaveChangesAsync();

    // A DIFFERENT session, so nothing is answered from the identity map.
    await using var reader = NewSession();
    var read = await reader.GetEntityAsync<Dml.IPerson>(written.Id);

    Assert.IsNotNull(read);
    Assert.AreEqual(written.Id, read.Id);
    Assert.AreEqual("Larsen", read.LastName);
    Assert.AreEqual(7, read.Rank);
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)read).Record.Status);
    Assert.IsTrue(((EntityBase)read).Record.Modified.IsEmpty, "reading a row is not a change to save");

    // ----- while a row that is not there reads as null ----------------------------------------------------------------
    reader.LogMessage("a row that is not there reads as null");
    Assert.IsNull(await reader.GetEntityAsync<Dml.IPerson>(Guid.NewGuid()));

    // ----- reading the SAME row twice gives the same object -------------------------------------------------------------
    reader.LogMessage("reading the SAME row twice gives the same object");
    //
    // The invariant, against real data: the second read materialises a record and then DISCARDS it in favour of the one
    // already tracked — which is what keeps the pending change below from being overwritten by what the database still
    // holds.
    read!.LastName = "Someone Else";
    var reread = await reader.GetEntityAsync<Dml.IPerson>(written.Id);

    Assert.AreSame(read, reread);
    Assert.AreEqual("Someone Else", reread!.LastName);

    // ----- and the change saves ---------------------------------------------------------------------------------------
    Assert.AreEqual(EntityStatus.Modified, ((EntityBase)read).Record.Status);
    await reader.SaveChangesAsync();

    Assert.AreEqual("Someone Else", await ScalarAsync("Person", "LastName", written.Id));
  }

  /// <summary>A reference read back is a STUB, and it EXPANDS at the moment somebody wants more than its key.</summary>
  /// <remarks>Two methods over the same two-entity write, and the pair is one argument: reading `person.Address` costs
  /// nothing and `person.Address.Id` is free, because the id came out of the person's own row — and the row itself is
  /// fetched only when something beyond the key is read.</remarks>
  [TestMethod]
  public async Task Reference_ReadBackIsAStubWithItsKey() {
    await using var writer = NewSession();
    var address = writer.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "Portland";
    var person = writer.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    person.Address = address;
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var read = await reader.GetEntityAsync<Dml.IPerson>(person.Id);
    var stub = read!.Address;

    Assert.IsNotNull(stub);
    Assert.AreEqual(address.Id, stub.Id, "the key came out of the person's own row, so this fetched nothing");
    Assert.AreEqual(EntityStatus.Stub, ((EntityBase)stub).Record.Status,
      "the target was not fetched, and did not need to be");

    // ----- and it EXPANDS when something beyond its key is read --------------------------------------------------------
    reader.LogMessage("the stub EXPANDS when something beyond its key is read");
    var city = stub.City;

    Assert.AreEqual("Portland", city);
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)stub).Record.Status, "it stopped being a placeholder");
    Assert.IsTrue(((EntityBase)stub).Record.Modified.IsEmpty, "being filled in is not a change to save");
  }

  [TestMethod]
  public async Task Stub_OfAMissingRowReadsAsNothing() {
    // Not an error: a reference declared without a database constraint may legitimately point at a row that is not
    // there, and a row deleted by somebody else between the read and the expansion is a race rather than a fault.
    //
    // Asked ONCE, though — the members are marked loaded even though nothing filled them, or every read of every null
    // member would go back to the database. A query per property access, silently, is the failure to avoid here.
    await using var session = NewSession();
    var stub = session.GetEntity<Dml.IAddress>(Guid.NewGuid(), LoadFlags.Stub);

    Assert.IsNull(stub.City);

    var record = ((EntityBase)stub).Record;
    Assert.IsTrue(record.Loaded.IsSet(Address(App).GetMember("City").DataIndex),
      "the question was asked and the answer was nothing — asking again would change none of it");
  }

  [TestMethod]
  public async Task Stub_AssignedWithoutALoad_SavesWhatWasAssigned() {
    var document = await NewDocumentAsync("stub-" + Guid.NewGuid().ToString("N")[..6], 2);
    Guid first, second;
    await using (var writer = NewSession()) {
      var lines = (await writer.GetEntityAsync<Dml.IDocument>(document)).Lines;
      (first, second) = (lines[0].Id, lines[1].Id);
      lines[0].Value = lines[1].Value = 3;
      await writer.SaveChangesAsync();
    }

    // ----- a member that is not loaded is not judged: the line's name and its document are required ------------------
    await using (var session = NewSession()) {
      var stub = session.GetEntity<Dml.IDocLine>(first, LoadFlags.Stub);
      stub.Value = 5;
      await session.SaveChangesAsync();
      var name = App.Model!.FindEntity("DocLine")!.GetMember("Name");
      Assert.IsFalse(((EntityBase)stub).Record.Loaded.IsSet(name.DataIndex), "the save loaded nothing");
    }
    Assert.AreEqual(5, await ScalarAsync<int>("DocLine", "Value", first));
    Assert.AreEqual("L0", await ScalarAsync<string>("DocLine", "Name", first), "the members not assigned stand");

    // ----- a value equal to the one an empty slot holds is an assignment all the same ---------------------------------
    await using (var session = NewSession()) {
      var stub = session.GetEntity<Dml.IDocLine>(second, LoadFlags.Stub);
      stub.Value = 0;
      await session.SaveChangesAsync();
    }
    Assert.AreEqual(0, await ScalarAsync<int>("DocLine", "Value", second));
  }

  [TestMethod]
  public async Task LoadedRecord_ReadsAMemberItLacksFromItsRow() {
    await using var writer = NewSession();
    var written = writer.NewEntity<Dml.IPerson>();
    written.Id = Guid.NewGuid();
    written.LastName = "Larsen";
    written.Rank = 7;
    await writer.SaveChangesAsync();

    // A record that is loaded and holds some of its members, as a read that leaves a member out makes one.
    await using var reader = NewSession();
    var entity = App.Model!.FindEntity("Person")!;
    var partial = new EntityRecord(entity, reader) { Status = EntityStatus.Loaded };
    partial.SetLoadedValue(entity.GetMember("Id"), written.Id);
    partial.SetLoadedValue(entity.GetMember("LastName"), "held");
    reader.Attach(partial);
    var person = (Dml.IPerson)partial.EntityInstance;

    Assert.AreEqual(7, person.Rank, "the member it lacked is read from the row");
    Assert.AreEqual("held", person.LastName, "the load does not replace a member the record held");
    Assert.AreEqual(EntityStatus.Loaded, partial.Status);
    Assert.IsTrue(partial.Modified.IsEmpty, "being filled in is not a change to save");
    var load = reader.LastCommands.Single();
    Assert.IsNull(person.Address);
    Assert.AreSame(load, reader.LastCommands.Single(), "one load brought every member the row has");
  }

  [TestMethod]
  public async Task AReferenceToSomethingDELETEDButNotYetSavedIsStillThere() {
    // It is still tracked: deleting marks a record and queues a statement, it does not remove the object. Until the
    // save runs, the session holds it and a reference to it must find it rather than go looking in the database for
    // a row that is still perfectly present.
    await using var writer = NewSession();
    var address = writer.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "Portland";
    var person = writer.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    person.Address = address;
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var read = await session.GetEntityAsync<Dml.IPerson>(person.Id);
    var target = read!.Address!;                       // a stub, tracked under the address's identity
    session.DeleteEntity(target);

    Assert.AreEqual(EntityStatus.Deleted, ((EntityBase)target).Record.Status);
    Assert.AreSame(target, read.Address, "the reference resolved through the map to the record it already holds");
  }

  static Mic.Orm.Model.EntityTypeInfo Address(EntityApp app) => app.Model!.FindEntity("Address")!;

  // ----- order -----------------------------------------------------------------------------------------------------

  [TestMethod]
  public async Task Parent_IsWrittenBeforeItsChild() {
    // The whole point of the ordering, and the case a save set in creation order gets wrong: the child was made FIRST,
    // so writing them in that order breaks the foreign key.
    await using var session = NewSession();

    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";

    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "Portland";
    person.Address = address;

    await session.SaveChangesAsync();

    Assert.AreEqual("Portland", await ScalarAsync("Address", "City", address.Id));
    Assert.AreEqual(address.Id, await ScalarAsync<Guid>("Person", "Address_Id", person.Id));
  }

  [TestMethod]
  public async Task Graph_SavesInAnyRecordOrder() {
    // Two types and a chain of managers, saved from a SHUFFLED pending list. This is the test the ordering exists for:
    // with the records in a random order, a save can only succeed if it was sorted.
    //
    // The management chain also reaches the SECOND level of the sort, which nothing else here does — a self-
    // referencing type is a component of one that is a cycle, so its rows must be ordered among themselves.
    for (var seed = 0; seed < 8; seed++) {
      await using var session = NewSession();

      var addresses = new List<Dml.IAddress>();
      for (var i = 0; i < 3; i++) {
        var address = session.NewEntity<Dml.IAddress>();
        address.Id = Guid.NewGuid();
        address.City = "City" + i;
        addresses.Add(address);
      }

      // Each person manages the next, so the chain has to be written from the top down.
      var people = new List<Dml.IPerson>();
      for (var i = 0; i < 6; i++) {
        var person = session.NewEntity<Dml.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = "Person" + i;
        person.Address = addresses[i % addresses.Count];
        if (i > 0)
          person.Manager = people[i - 1];
        people.Add(person);
      }

      Shuffle(session, seed);
      await session.SaveChangesAsync();

      Assert.AreEqual(people[4].Id, await ScalarAsync<Guid>("Person", "Manager_Id", people[5].Id),
        $"seed {seed}: the manager chain did not survive the save");
      Assert.AreEqual(addresses[2].Id, await ScalarAsync<Guid>("Person", "Address_Id", people[2].Id),
        $"seed {seed}");

      // And back out again, which is the same ordering read from the other end.
      foreach (var person in people)
        session.DeleteEntity(person);
      foreach (var address in addresses)
        session.DeleteEntity(address);
      Shuffle(session, seed + 100);
      await session.SaveChangesAsync();

      Assert.IsNull(await ScalarAsync("Person", "LastName", people[0].Id), $"seed {seed}");
      Assert.IsNull(await ScalarAsync("Address", "City", addresses[0].Id), $"seed {seed}");
    }
  }

  [TestMethod]
  public async Task MutualReferences_AreReportedNotAttempted() {
    // The one case no order satisfies: two rows referencing one another, both new. It is detected at the RECORD level
    // — the only place it can be — and said rather than emitted in an order that will certainly fail.
    await using var session = NewSession();

    var first = session.NewEntity<Dml.IPerson>();
    first.Id = Guid.NewGuid();
    first.LastName = "First";
    var second = session.NewEntity<Dml.IPerson>();
    second.Id = Guid.NewGuid();
    second.LastName = "Second";
    first.Manager = second;
    second.Manager = first;

    var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
    StringAssert.Contains(ex.Message, "circle");
  }

  [TestMethod]
  public async Task Child_IsDeletedBeforeItsParent() {
    // The same ordering read from the other end. Deleting the address first would break the foreign key from the
    // person that still points at it.
    await using var session = NewSession();
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Larsen";
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "Portland";
    person.Address = address;
    await session.SaveChangesAsync();

    session.DeleteEntity(person);
    session.DeleteEntity(address);
    await session.SaveChangesAsync();

    Assert.IsNull(await ScalarAsync("Address", "City", address.Id));
    Assert.IsNull(await ScalarAsync("Person", "LastName", person.Id));
  }

  [TestMethod]
  public async Task LongTextAndLongJson_SurviveTheSave() {
    // Past the 32,767 bytes Oracle takes as a string bind and the 65,535 bytes of MySQL's short text and blob:
    // 40,000 characters of two bytes each, 70,000 bytes, and a document of 40,000.
    var text = new string('ж', 40_000);
    var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)(i % 251)).ToArray();
    var theme = new string('x', 40_000);
    var newTheme = new string('y', 40_000);
    await using var session = NewSession();
    var row = session.NewEntity<Dml.IEveryType>();
    row.Id = Guid.NewGuid();
    row.Text = "long values";
    row.Amount = 1m;
    row.Grade = Dml.Grade.Gold;
    row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    row.Day = new DateOnly(2031, 4, 1);
    row.Long = text;
    row.Blob = bytes;
    row.Settings = new Dml.EveryTypeSettings { Theme = theme, PageSize = 25, Tags = [] };
    // A document held as text is a string member with no size: the default string size is not its limit.
    row.RawJson = $$"""{"n": 1, "text": "{{theme}}"}""";
    await session.SaveChangesAsync();

    await using var reader = NewSession();
    var read = await reader.GetEntityAsync<Dml.IEveryType>(row.Id);
    Assert.IsNotNull(read);
    Assert.AreEqual(text, read.Long, "the long text");
    CollectionAssert.AreEqual(bytes, read.Blob, "the long binary");
    Assert.AreEqual(theme, read.Settings.Theme, "the long JSON document");
    Assert.AreEqual(theme, GetRawText(read), "the long JSON document held as text");

    // A long value written at a path of a document, and a long text bound in an update.
    read.Settings.Theme = newTheme;
    EntityHelper.MarkModified(read, r => r.Settings);
    read.RawJson = $$"""{"n": 2, "text": "{{newTheme}}"}""";
    read.Long = text + "!";
    await reader.SaveChangesAsync();
    await using var second = NewSession();
    var changed = await second.GetEntityAsync<Dml.IEveryType>(row.Id);
    Assert.AreEqual(newTheme, changed!.Settings.Theme, "the long value at a path");
    Assert.AreEqual(text + "!", changed.Long, "the long text updated");
    Assert.AreEqual(newTheme, GetRawText(changed), "the document held as text, replaced");

    // ----- local functions
    static string? GetRawText(Dml.IEveryType source) =>
      System.Text.Json.JsonDocument.Parse(source.RawJson!).RootElement.GetProperty("text").GetString();
  }

  // A NULL BINARY IN A SAVE OF ONE ROW. A save of one row binds its values, and a bound null has no type: SQL Server
  // takes such a null for every column type but a binary one. A save of several rows writes literals and never
  // showed it.
  [TestMethod]
  public async Task NullBinary_IsInsertedAndAssignedInASaveOfOneRow() {
    await using var session = NewSession();
    var row = session.NewEntity<Dml.IEveryType>();
    row.Id = Guid.NewGuid();
    row.Text = "null binary";
    row.Grade = Dml.Grade.Gold;
    row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    row.Day = new DateOnly(2031, 4, 1);
    row.Long = "x";
    row.Settings = new Dml.EveryTypeSettings { Theme = "plain", PageSize = 1, Tags = [] };
    await session.SaveChangesAsync();
    Assert.IsNull(await ReadBlobAsync());

    row.Blob = [1, 2, 3];
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await ReadBlobAsync());

    row.Blob = null;
    await session.SaveChangesAsync();
    Assert.IsNull(await ReadBlobAsync());

    // ----- local functions
    async Task<byte[]?> ReadBlobAsync() {
      await using var reader = NewSession();
      return (await reader.GetEntityAsync<Dml.IEveryType>(row.Id))!.Blob;
    }
  }

  // A byte array is one binary value, as a string is one text: compared with a column it is bound whole, whether a
  // variable, a named parameter or a constant brings it. Read as a list of bytes it was written out as a list on
  // every server that binds no array.
  [TestMethod]
  public async Task BinaryColumn_IsComparedWithABinaryValue() {
    var tag = "bin-" + Guid.NewGuid().ToString("N")[..6];
    var long16 = Guid.NewGuid().ToByteArray();
    await using (var writer = NewSession()) {
      foreach (var (name, blob) in new (string, byte[]?)[] { ("a", long16), ("b", [9]), ("c", null) }) {
        var row = writer.NewEntity<Dml.IEveryType>();
        row.Id = Guid.NewGuid();
        row.Text = tag + name;
        row.Blob = blob;
        // The same bytes in the binary that says its size, cut to it.
        row.Token = blob;
        row.Grade = Dml.Grade.Gold;
        row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        row.Day = new DateOnly(2031, 4, 1);
        row.Long = "x";
        row.Settings = new Dml.EveryTypeSettings { Theme = "plain", PageSize = 1, Tags = [] };
      }
      await writer.SaveChangesAsync();
    }

    await using var session = NewSession();
    var mine = session.EntitySet<Dml.IEveryType>().Where(e => e.Text.StartsWith(tag));
    async Task<string> ReadNamesAsync(IQueryable<Dml.IEveryType> query) =>
      string.Join(",", (await query.OrderBy(e => e.Text).ToListAsync()).Select(e => e.Text[tag.Length..]));

    var same = long16.ToArray();
    byte[] one = [9];

    // ----- a binary that says its size is the server's bounded type, which every server compares ------------------
    Assert.AreEqual("a", await ReadNamesAsync(mine.Where(e => e.Token == same)));
    Assert.AreEqual("b", await ReadNamesAsync(mine.Where(e => e.Token == one)));
    Assert.AreEqual("b", await ReadNamesAsync(mine.Where(e => e.Token != same)));
    CollectionAssert.AreEqual(same, (await mine.Where(e => e.Token == same).ToListAsync()).Single().Token);

    // ----- Db2 holds a binary with no size as a BLOB and compares it cut to the length of the value beside it: a
    // row holding a longer one fails the statement (SQL0433N), so the answer hangs on the other rows of the table.
    // The null test is sure -------------------------------------------------------------------------------------------
    if (Fixture.ServerKind == DbServerKind.Db2) {
      Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == null)));
      byte[]? missing = null;
      Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == missing)));
      return;
    }

    // ----- Oracle holds a binary with no size as a BLOB, which it compares with nothing. The value arrives whole
    // all the same, and the refusal is the server's own -----------------------------------------------------------
    if (Fixture.ServerKind == DbServerKind.Oracle) {
      var refused = await Assert.ThrowsExactlyAsync<DataAccessException>(() =>
        mine.Where(e => e.Blob == same).ToListAsync());
      StringAssert.Contains(refused.Message, "ORA-22848");
      Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == null)));
      // A variable that holds null is the null test as well, which a BLOB takes.
      byte[]? absent = null;
      Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == absent)));
      return;
    }

    // ----- a variable: many bytes, one byte, not equal ------------------------------------------------------------
    Assert.AreEqual("a", await ReadNamesAsync(mine.Where(e => e.Blob == same)));
    Assert.AreEqual("b", await ReadNamesAsync(mine.Where(e => e.Blob == one)));
    Assert.AreEqual("b", await ReadNamesAsync(mine.Where(e => e.Blob != same)));

    // ----- a variable that holds null is the question whether the column is null, as a null written out is ---------
    byte[]? none = null;
    Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == none)));
    Assert.AreEqual("a,b", await ReadNamesAsync(mine.Where(e => e.Blob != none)));
    Assert.AreEqual("c", await ReadNamesAsync(mine.Where(e => e.Blob == null)));

    // ----- by name: a parameter and a constant ---------------------------------------------------------------------
    var byParameter = await session.ExecuteQueryAsync(session.From("EveryType")
      .Where(r => r.Member("Blob").Equal(QueryMaker.Parameter("bytes", typeof(byte[])))), same);
    Assert.AreEqual(tag + "a", ((Dml.IEveryType)byParameter.Single().EntityInstance).Text);
    var byConstant = await session.ExecuteQueryAsync(session.From("EveryType")
      .Where(r => r.Member("Blob").Equal(QueryMaker.Constant(same))));
    Assert.AreEqual(tag + "a", ((Dml.IEveryType)byConstant.Single().EntityInstance).Text);
  }

  // A hash beside its text: written with the text by the insert and by the update, and the same value the
  // application computes to find the row by.
  [TestMethod]
  public async Task HashMember_IsStoredWithItsText_AndFindsTheRow() {
    var tag = "hash-" + Guid.NewGuid().ToString("N")[..6];
    var url = "https://example.com/pages/" + tag;
    var id = Guid.NewGuid();
    await using (var writer = NewSession()) {
      var bookmark = writer.NewEntity<Dml.IBookmark>();
      bookmark.Id = id;
      bookmark.Name = tag;
      bookmark.Url = url;
      await writer.SaveChangesAsync();
    }

    await using var session = NewSession();
    var hash = App.ComputeStableHash64(url);
    var found = await session.EntitySet<Dml.IBookmark>().Where(b => b.UrlHash == hash && b.Url == url).ToListAsync();
    Assert.AreEqual(id, found.Single().Id);
    Assert.AreEqual(App.ComputeStableHash(tag), found[0].NameHash);
    Assert.AreNotEqual(0, found[0].NameHash);

    // ----- the text changed: the hash goes with it, in the same update -------------------------------------------
    found[0].Url = url + "/moved";
    await session.SaveChangesAsync();
    Assert.AreEqual(1, session.GetLastCommands().Count);
    Assert.AreEqual(App.ComputeStableHash64(url + "/moved"), await ProbeScalarAsync<long>(
      $"SELECT {Db.Quote("UrlHash")} FROM {Db.GetTableRef("Bookmark")} WHERE {Db.Quote("Id")} = @id", ("id", id)));

    // ----- a hash another algorithm left in the row is recomputed by assigning the member -------------------------
    await Db.ExecuteAsync($"UPDATE {Db.GetTableRef("Bookmark")} SET {Db.Quote("NameHash")} = 7 "
      + $"WHERE {Db.Quote("Name")} = '{tag}'");
    await using var migration = NewSession();
    var nameHash = App.ComputeStableHash(tag);
    Assert.IsNull(await migration.EntitySet<Dml.IBookmark>().Where(b => b.NameHash == nameHash && b.Name == tag)
      .FirstOrDefaultAsync(), "the row is not found by a hash it does not hold");
    foreach (var stale in await migration.EntitySet<Dml.IBookmark>().Where(b => b.Name == tag).ToListAsync())
      stale.NameHash = App.ComputeStableHash(stale.Name);
    await migration.SaveChangesAsync();
    var again = await migration.EntitySet<Dml.IBookmark>().Where(b => b.NameHash == nameHash && b.Name == tag)
      .FirstOrDefaultAsync();
    Assert.AreEqual(id, again!.Id);
  }

  // ----- many rows at once -----------------------------------------------------------------------------------------

  /// <summary>THE ACCEPTANCE TEST FOR LITERALS: every type, written INTO the statement, read back unchanged.</summary>
  /// <remarks>A multi-row insert spells its values into the SQL rather than binding them, and every server spells them
  /// differently — `bytea` is not `X'…'` is not `0x…` is not `HEXTORAW('…')`; a boolean is a keyword on one server and
  /// a number on four; a timestamp is read against the SESSION's format on Oracle unless it says otherwise. Nothing
  /// short of a real server proves any of that, and a formatter that is merely plausible is exactly the failure this
  /// is exposed to.
  ///
  /// SEVERAL ROWS, because one would not collapse and would never reach the literal path at all.
  ///
  /// The values are chosen to break things rather than to be typical: a quote and a BACKSLASH in the text — MySQL
  /// treats backslash as an escape unless told not to, so a naive `''` doubling lets a value escape its own quoting,
  /// which is injection rather than a wrong row — a negative decimal with a fractional part that a comma-decimal
  /// culture would render with a comma, and the extremes of `int` and `long`.
  ///
  /// NO EMPTY STRING, deliberately: Oracle stores `''` as NULL, so a round trip of one tests Oracle's own conflation
  /// rather than anything about a literal, and would need a per-vendor branch to say so.</remarks>
  [TestMethod]
  public async Task EveryType_SurvivesTheStatement() {
    await using var session = NewSession();
    session.LogMessage("every literal type, inserted as several rows so the insert collapses");

    var moment = new DateTime(2031, 4, 1, 13, 45, 12, 345, DateTimeKind.Utc);
    var day = new DateOnly(2029, 12, 31);
    var rows = new List<Dml.IEveryType>();
    // The last is outside the code page of a Latin collation, which a literal has to say on SQL Server.
    const string foreign = "жук naïve 日本";
    var texts = new[] { "it's a \\ backslash", "trailing backslash \\", "ordinary", foreign };

    for (var i = 0; i < texts.Length; i++) {
      var row = session.NewEntity<Dml.IEveryType>();
      row.Id = Guid.NewGuid();
      row.Text = texts[i];
      row.Missing = null;
      row.Flag = i % 2 == 0;
      row.Count = i == 0 ? int.MinValue : int.MaxValue;
      row.Big = i == 0 ? long.MinValue : long.MaxValue;
      row.Amount = -1234.5678m;
      row.Ratio = 0.1 + 0.2;
      row.Grade = Dml.Grade.Gold;
      row.Moment = moment;
      row.Day = day;
      row.Long = new string('x', 500);            // past MaxLiteralLength — must fall back to a parameter
      row.Blob = [1, 2, 3, 4];
      row.Settings = new Dml.EveryTypeSettings { Theme = "dark", PageSize = 25, Tags = ["a", "b\"quoted\""] };
      row.RawJson = i == 0 ? null : """{"n": 1, "text": "it's"}""";
      rows.Add(row);
    }
    await session.SaveChangesAsync();

    // ONE STATEMENT, which is what says the literal path ran at all: a per-row insert would never have reached it.
    Assert.AreEqual(1, session.LastCommands.Count, session.LastCommands.FirstOrDefault()?.Sql);

    // ----- and every value comes back as it went in ------------------------------------------------------------------
    await using var reader = NewSession();
    for (var i = 0; i < rows.Count; i++) {
      var read = await reader.GetEntityAsync<Dml.IEveryType>(rows[i].Id);
      Assert.IsNotNull(read, $"row {i} was not written");

      Assert.AreEqual(texts[i], read.Text, $"row {i}: text");
      Assert.IsNull(read.Missing, $"row {i}: a null stayed null");
      Assert.AreEqual(rows[i].Flag, read.Flag, $"row {i}: boolean");
      Assert.AreEqual(rows[i].Count, read.Count, $"row {i}: int");
      Assert.AreEqual(rows[i].Big, read.Big, $"row {i}: long");
      Assert.AreEqual(-1234.5678m, read.Amount, $"row {i}: decimal");
      Assert.AreEqual(0.1 + 0.2, read.Ratio, 1e-12, $"row {i}: double");
      Assert.AreEqual(Dml.Grade.Gold, read.Grade, $"row {i}: enum");
      // TO THE SECOND, and the sub-second part is deliberately not asserted: MySQL's `datetime` holds whole seconds
      // unless it is declared `datetime(3)`, and MIC registers it without a precision — so `.345` is truncated there
      // and would be by a bound parameter just the same. The fractional part is still WRITTEN, which is what keeps the
      // `.fff` in the literal exercised on the servers that keep it.
      Assert.AreEqual(moment.AddTicks(-(moment.Ticks % TimeSpan.TicksPerSecond)),
        read.Moment.AddTicks(-(read.Moment.Ticks % TimeSpan.TicksPerSecond)), $"row {i}: timestamp");
      Assert.AreEqual(day, read.Day, $"row {i}: date");
      Assert.AreEqual(500, read.Long.Length, $"row {i}: the bound long string");
      CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, read.Blob, $"row {i}: the bound blob");
      Assert.AreEqual("dark", read.Settings.Theme, $"row {i}: the JSON object");
      Assert.AreEqual(25, read.Settings.PageSize);
      CollectionAssert.AreEqual(new[] { "a", "b\"quoted\"" }, read.Settings.Tags, $"row {i}: a list inside the object");
      if (i == 0)
        Assert.IsNull(read.RawJson, "a null JSON column stays null");
      else
        Assert.AreEqual(1, System.Text.Json.JsonDocument.Parse(read.RawJson!).RootElement.GetProperty("n").GetInt32(),
          $"row {i}: raw JSON text comes back as JSON, in whatever spacing the server keeps");
    }

    // ----- a constant of a query is written into its statement as well -----------------------------------------------
    var foreignId = rows[^1].Id;
    var byConstant = await reader.EntitySet<Dml.IEveryType>().Where(e => e.Text == foreign && e.Id == foreignId)
      .ToListAsync();
    Assert.AreEqual(1, byConstant.Count, "a constant outside the code page matches: " + reader.LastCommands[^1].Sql);

    // ----- a JSON object CHANGED IN PLACE is saved once its member is marked; one left alone is not written ---------
    reader.LogMessage("JSON: the object mutated in place, unmarked and then marked");
    var first = await reader.GetEntityAsync<Dml.IEveryType>(rows[0].Id);
    var second = await reader.GetEntityAsync<Dml.IEveryType>(rows[1].Id);
    first.Settings.PageSize = 99;
    first.Settings.Tags.Add("c");
    var commandsBefore = reader.LastCommands;
    await reader.SaveChangesAsync();
    Assert.AreSame(commandsBefore, reader.LastCommands, "a change nobody reported is not looked for");
    EntityHelper.MarkModified(first, f => f.Settings);
    Assert.AreEqual(EntityStatus.Modified, ((EntityBase)first).Record.Status);
    await reader.SaveChangesAsync();
    var update = reader.LastCommands.Single();
    StringAssert.Contains(update.Sql, "UPDATE", "the mutation was saved");
    // TWO PATHS, NOT THE DOCUMENT: the page size and the tags are written into the column by the server's own
    // path-set function, whose name has `json` in it on every one of the five; the whole document is `= @p`, which
    // is what Db2, with no such function, is sent.
    if (Fixture.ServerKind != DbServerKind.Db2)
      StringAssert.Contains(update.Sql.ToLowerInvariant(), "json", "a change confined to paths writes the paths");
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)first).Record.Status);
    await reader.SaveChangesAsync();
    Assert.AreSame(update, reader.LastCommands.Single(), "nothing changed since, so nothing was written");

    reader.LogMessage("JSON: a property that APPEARS is written at its path, and one that is GONE is removed at its path");
    second.RawJson = """{"n":1,"m":2}""";
    await reader.SaveChangesAsync();
    StringAssert.Contains(reader.LastCommands.Single().Sql.ToLowerInvariant(), "json", "a property added is a path");
    second.RawJson = """{"n":1}""";
    await reader.SaveChangesAsync();
    StringAssert.Contains(reader.LastCommands.Single().Sql.ToLowerInvariant(), "json", "a property removed is a path too");
    await using (var checker = NewSession()) {
      var trimmed = await checker.GetEntityAsync<Dml.IEveryType>(rows[1].Id);
      var document = System.Text.Json.JsonDocument.Parse(trimmed.RawJson!).RootElement;
      Assert.AreEqual(1, document.GetProperty("n").GetInt32(), "what stayed is there");
      Assert.IsFalse(document.TryGetProperty("m", out _), "what was removed is gone from the row");
    }

    reader.LogMessage("JSON: assigned a new object, and nulled");
    second.Settings = new Dml.EveryTypeSettings { Theme = "light" };
    second.RawJson = null;
    await reader.SaveChangesAsync();

    await using var verifier = NewSession();
    var mutated = await verifier.GetEntityAsync<Dml.IEveryType>(rows[0].Id);
    Assert.AreEqual(99, mutated.Settings.PageSize, "the in-place change reached the row");
    CollectionAssert.AreEqual(new[] { "a", "b\"quoted\"", "c" }, mutated.Settings.Tags);
    var assigned = await verifier.GetEntityAsync<Dml.IEveryType>(rows[1].Id);
    Assert.AreEqual("light", assigned.Settings.Theme);
    Assert.AreEqual(0, assigned.Settings.PageSize);
    Assert.IsNull(assigned.RawJson);

    // ----- read by a QUERY into a shape: the text comes back as the object, with no member to say it is JSON --------
    var themes = await verifier.ExecuteValuesAsync<Dml.EveryTypeSettings>(verifier.From("EveryType")
      .Where(row => row.Member("Id").Equal(QueryMaker.Parameter("id", rows[0].Id)))
      .Select(row => [row.Member("Settings")]));
    Assert.AreEqual(1, themes.Count);
    Assert.AreEqual(99, themes[0]!.PageSize, "a projected JSON column is the object");
  }

  // A JSON MEMBER HOLDS JSON ON EVERY SERVER. Three refuse other text by the column's type; SQL Server before 2025
  // and SQLite keep the document as text, and the check constraint the ORM puts on the column refuses it there.
  // One such row would fail every query that reads into the documents of the table.
  [TestMethod]
  public async Task JsonText_ThatIsNotJson_IsRefusedByTheSave() {
    await using var session = NewSession();
    var row = session.NewEntity<Dml.IEveryType>();
    row.Id = Guid.NewGuid();
    row.Text = "not json";
    row.Grade = Dml.Grade.Gold;
    row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    row.Day = new DateOnly(2031, 4, 1);
    row.Long = "x";
    row.Blob = [1];
    row.Settings = new Dml.EveryTypeSettings { Theme = "plain", PageSize = 1, Tags = [] };
    row.RawJson = "this is not a document";
    // Db2 has no test for JSON to put into a check, so the column takes any text.
    if (Fixture.ServerKind == DbServerKind.Db2)
      await session.SaveChangesAsync();
    else
      await Assert.ThrowsAsync<DataAccessException>(() => session.SaveChangesAsync());

    // The same row with a document is taken, and so is one with none.
    row.RawJson = """{"n": 1}""";
    await session.SaveChangesAsync();
    row.RawJson = null;
    await session.SaveChangesAsync();
  }

  [TestMethod]
  public async Task JsonDocument_IsTextUntilReadAndWrittenWhenReported() {
    var id = Guid.NewGuid();
    await using (var writer = NewSession()) {
      var row = writer.NewEntity<Dml.IEveryType>();
      row.Id = id;
      row.Text = "a document";
      row.Amount = 1m;
      row.Grade = Dml.Grade.Gold;
      row.Moment = new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc);
      row.Day = new DateOnly(2031, 4, 1);
      row.Long = "long";
      row.Blob = [1, 2];
      row.Settings = new Dml.EveryTypeSettings { Theme = "dark", PageSize = 25, Tags = ["a"] };
      await writer.SaveChangesAsync();
    }

    // ----- a loaded document is text; the text is read without the object, and the first read makes the object ------
    await using var session = NewSession();
    var loaded = await session.GetEntityAsync<Dml.IEveryType>(id);
    var record = EntityHelper.GetRecord(loaded!);
    var settings = record.Entity.FindMember(nameof(Dml.IEveryType.Settings))!;
    Assert.IsInstanceOfType<JsonText>(record.GetRawValue(settings), "no object was made at the load");
    StringAssert.Contains(EntityHelper.GetJson(loaded!, e => e.Settings), "dark");
    StringAssert.Contains(EntityHelper.GetJson(loaded!, "settings"), "dark", "by name, ignoring case");
    Assert.IsInstanceOfType<JsonText>(record.GetRawValue(settings), "reading the text makes no object either");
    Assert.AreEqual(25, loaded!.Settings.PageSize);
    Assert.IsInstanceOfType<Dml.EveryTypeSettings>(record.GetRawValue(settings));
    Assert.AreSame(loaded.Settings, loaded.Settings, "one object, made once");
    Assert.AreEqual(EntityStatus.Loaded, record.Status, "reading changes nothing");

    // ----- a document set as text: written, and read as an object after --------------------------------------------
    EntityHelper.SetJson(loaded, e => e.Settings, """{"Theme":"set","PageSize":7,"Tags":["x","y"]}""");
    Assert.AreEqual(EntityStatus.Modified, record.Status);
    Assert.IsInstanceOfType<JsonText>(record.GetRawValue(settings), "the object is gone until it is read again");
    EntityHelper.SetJson(loaded, nameof(Dml.IEveryType.RawJson), """{"a":1}""");
    await session.SaveChangesAsync();
    Assert.AreEqual("set", loaded.Settings.Theme);
    await using (var checker = NewSession()) {
      var saved = await checker.GetEntityAsync<Dml.IEveryType>(id);
      Assert.AreEqual(7, saved!.Settings.PageSize);
      CollectionAssert.AreEqual(new[] { "x", "y" }, saved.Settings.Tags);
      Assert.AreEqual(1, System.Text.Json.JsonDocument.Parse(EntityHelper.GetJson(saved, e => e.RawJson)!)
        .RootElement.GetProperty("a").GetInt32());
    }

    // ----- any column can be marked: it goes into the update with the value it has ---------------------------------
    EntityHelper.MarkModified(loaded, nameof(Dml.IEveryType.Text));
    await session.SaveChangesAsync();
    StringAssert.Contains(session.LastCommands.Single().Sql, "Text", "the marked column is written");

    // ----- what has no column of its own to write is refused, as is a member that is no JSON ------------------------
    Assert.ThrowsExactly<ArgumentException>(() => EntityHelper.MarkModified(loaded, e => e.Id));
    Assert.ThrowsExactly<ArgumentException>(() => EntityHelper.MarkModified(loaded, e => e.Text.Length));
    Assert.ThrowsExactly<ArgumentException>(() => EntityHelper.GetJson(loaded, e => e.Text));
    Assert.ThrowsExactly<ArgumentException>(() => EntityHelper.SetJson(loaded, nameof(Dml.IEveryType.Text), "{}"));

    // ----- a reference is marked through its key column --------------------------------------------------------------
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "marked";
    await session.SaveChangesAsync();
    EntityHelper.MarkModified(person, p => p.Address);
    await session.SaveChangesAsync();
    StringAssert.Contains(session.LastCommands.Single().Sql, "Address_Id", "the reference's key column is written");
  }

  [TestMethod]
  public async Task JsonColumn_HoldsSeveralTypesByDiscriminator() {
    // Two shapes in the same column: `Kind` — an enum, mapped on the application by NAME — says which type `Shape`
    // holds, and the column leads: a document of another type is a fault, not a correction. `Inline` carries the
    // discriminator inside the document and the serializer does the telling.
    await using var writer = NewSession();
    var round = writer.NewEntity<Dml.IDrawing>();
    round.Id = Guid.NewGuid();
    round.Kind = Dml.ShapeKind.Circle;
    round.Shape = new Dml.Circle { Color = "red", Radius = 2.5 };
    round.Inline = new Dml.Square { Color = "blue", Side = 4 };
    var boxy = writer.NewEntity<Dml.IDrawing>();
    boxy.Id = Guid.NewGuid();
    boxy.Shape = new Dml.Square { Color = "green", Side = 1 };
    var fault = Assert.ThrowsExactly<ClientFaultException>(writer.ValidateChanges).Faults.Single();
    Assert.AreEqual(ClientFaultCodes.JsonTypeMismatch, fault.Code, "Kind is None, which names no type");
    Assert.AreEqual("Shape", fault.MemberName);
    boxy.Kind = Dml.ShapeKind.Circle;
    fault = Assert.ThrowsExactly<ClientFaultException>(writer.ValidateChanges).Faults.Single();
    Assert.AreEqual(ClientFaultCodes.JsonTypeMismatch, fault.Code, "Kind says circle and the document is a square");
    StringAssert.Contains(fault.Message, "Circle");
    boxy.Kind = Dml.ShapeKind.Square;
    await writer.SaveChangesAsync();
    Assert.AreEqual((int)Dml.ShapeKind.Square, await ScalarAsync<int>("Drawing", "Kind", boxy.Id));

    // ----- read back: the type the column says, and the type the document says --------------------------------------
    await using var reader = NewSession();
    var readRound = await reader.GetEntityAsync<Dml.IDrawing>(round.Id);
    var circle = readRound.Shape as Dml.Circle;
    Assert.IsNotNull(circle, $"`Kind` said circle; got {readRound.Shape.GetType().Name}");
    Assert.AreEqual(2.5, circle.Radius);
    Assert.AreEqual("red", circle.Color);
    var square = readRound.Inline as Dml.Square;
    Assert.IsNotNull(square, "the document's own discriminator chose the type");
    Assert.AreEqual(4, square.Side);
    Assert.IsInstanceOfType<Dml.Square>((await reader.GetEntityAsync<Dml.IDrawing>(boxy.Id)).Shape);

    // ----- a document of a DIFFERENT type needs the column changed with it -----------------------------------------
    reader.LogMessage("JSON: the circle becomes a square, and Kind must say so");
    readRound.Shape = new Dml.Square { Color = "red", Side = 9 };
    Assert.AreEqual(ClientFaultCodes.JsonTypeMismatch,
      Assert.ThrowsExactly<ClientFaultException>(reader.ValidateChanges).Faults.Single().Code);
    readRound.Kind = Dml.ShapeKind.Square;
    await reader.SaveChangesAsync();
    await using var verifier = NewSession();
    var changed = await verifier.GetEntityAsync<Dml.IDrawing>(round.Id);
    Assert.AreEqual(9, ((Dml.Square)changed.Shape).Side);
    Assert.AreEqual(Dml.ShapeKind.Square, changed.Kind);

    // ----- projected by a query: the type the discriminator SELECTED BESIDE IT names, in either order --------------
    verifier.LogMessage("JSON: a discriminated document projected with its Kind, typed and by name");
    var roundId = round.Id;
    var pairs = await verifier.EntitySet<Dml.IDrawing>().Where(d => d.Id == roundId)
      .Select(d => new { d.Kind, d.Shape }).ToListAsync();
    Assert.AreEqual(9, ((Dml.Square)pairs.Single().Shape).Side, "a projected document is the type Kind names");
    var named = await verifier.ExecuteRowsAsync(verifier.From("Drawing")
      .Where(row => row.Member("Id").Equal(QueryMaker.Parameter("id", roundId)))
      .Select(row => [row.Member("Shape"), row.Member("Kind")]));
    Assert.IsInstanceOfType<Dml.Square>(named.Single().GetValue("Shape"), "by name, the discriminator after the document");
    var bundled = await verifier.EntitySet<Dml.IDrawing>().Where(d => d.Id == roundId)
      .Select(d => new { Drawing = d, d.Shape }).ToListAsync();
    Assert.IsInstanceOfType<Dml.Square>(bundled.Single().Shape, "beside the whole row, whose columns carry Kind");
    // And WITHOUT the discriminator, nothing could say the type: refused by name.
    var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      verifier.EntitySet<Dml.IDrawing>().Where(d => d.Id == roundId).Select(d => d.Shape).ToListAsync());
    StringAssert.Contains(refused.Message, "'Kind' beside it");
  }

  [TestMethod]
  public async Task ManyNewRows_GoInOneStatement() {
    // The whole argument for the promoted key model. A client-generated key exists before anything is sent, so a set
    // of new rows needs nothing back and nothing carried between statements — which makes it ONE statement.
    //
    // Oracle is the one to watch: it has no multi-row VALUES and spells this `INSERT ALL … SELECT * FROM dual`. If the
    // two templates were the wrong shape, this is where it shows.
    await using var session = NewSession();

    // TAGGED: the database is shared, so this reads back its OWN rows rather than every address there is.
    var tag = "bulk-" + Guid.NewGuid().ToString("N")[..8] + "-";
    var cities = Enumerable.Range(0, 40).Select(i => tag + i).ToList();
    foreach (var city in cities) {
      var address = session.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = city;
    }
    await session.SaveChangesAsync();

    var written = await Db.ColumnAsync(
      $"SELECT {Db.Quote("City")} FROM {Db.GetTableRef("Address")} "
      + $"WHERE {Db.Quote("City")} LIKE '{tag}%' ORDER BY {Db.Quote("City")}");
    CollectionAssert.AreEqual(cities.OrderBy(c => c, StringComparer.Ordinal).ToList(), written);
  }

  [TestMethod]
  public async Task ManyDeletes_GoInOneStatement() {
    await using var session = NewSession();

    // TAGGED for the same reason as the insert above: the shared database has other tests' addresses in it.
    var tag = "del-" + Guid.NewGuid().ToString("N")[..8] + "-";
    var addresses = new List<Dml.IAddress>();
    for (var i = 0; i < 25; i++) {
      var address = session.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = tag + i;
      addresses.Add(address);
    }
    await session.SaveChangesAsync();

    // All but the first, so that a key LIST is what is being tested rather than "delete everything".
    foreach (var address in addresses.Skip(1))
      session.DeleteEntity(address);
    await session.SaveChangesAsync();

    var left = await Db.ColumnAsync(
      $"SELECT {Db.Quote("City")} FROM {Db.GetTableRef("Address")} WHERE {Db.Quote("City")} LIKE '{tag}%'");
    CollectionAssert.AreEqual(new List<string?> { tag + 0 }, left);
  }

  [TestMethod]
  public async Task SelfReferencingType_WritesItsRowsInOrder() {
    // A multi-row VALUES is written in the ORDER GIVEN, which is the order the sorter chose — so a manager may sit in
    // the same statement as the people reporting to them. That is an assumption about five servers rather than a
    // guarantee anybody wrote down, which is why it has a test rather than a comment.
    await using var session = NewSession();

    var chief = session.NewEntity<Dml.IPerson>();
    chief.Id = Guid.NewGuid();
    chief.LastName = "Chief";

    var reports = new List<Dml.IPerson>();
    for (var i = 0; i < 10; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = "Report-" + i;
      person.Manager = i == 0 ? chief : reports[i - 1];     // a chain, so every row depends on the one before it
      reports.Add(person);
    }
    await session.SaveChangesAsync();

    Assert.AreEqual("Chief", await ScalarAsync("Person", "LastName", chief.Id));
    Assert.AreEqual("Report-9", await ScalarAsync("Person", "LastName", reports[9].Id));
  }

  [TestMethod]
  public async Task BulkInsert_ThatFailsSaysWhichRow() {
    // The developer-experience half of collapsing rows into one statement. A unique-key violation names the STATEMENT,
    // and the statement is now forty rows — so without this the caller's only recourse is to bisect their own data,
    // which is a worse answer than the one-statement-per-row it replaced.
    await using var session = NewSession();

    var duplicated = Guid.NewGuid();
    for (var i = 0; i < 40; i++) {
      var address = session.NewEntity<Dml.IAddress>();
      // Rows 8 and 24 share a key, so the twenty-fifth is the one the server refuses.
      address.Id = i is 8 or 24 ? duplicated : Guid.NewGuid();
      address.City = "City-" + i;
    }

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());

    StringAssert.Contains(ex.Message, "covered 40 rows");
    StringAssert.Contains(ex.Message, "row 25 is the one the server refused");
    StringAssert.Contains(ex.Message, duplicated.ToString());

    // And the replay wrote nothing — it runs inside a transaction that is always rolled back.
    Assert.IsNull(await ScalarAsync("Address", "City", duplicated), "the diagnostic replay left a row behind");
  }

  [TestMethod]
  public async Task OneBadStatement_LeavesNothingBehind() {
    // THE TEST THE BATCH EXISTS TO SURVIVE, and the only one that can catch what SQL Server does by default: it treats
    // a constraint failure as a STATEMENT-level abort — the statement is undone, the batch CARRIES ON, and the
    // transaction is still committable. So the earlier statements commit and an error is reported, which is the worst
    // of both. `SET XACT_ABORT ON` at the head of the batch is what makes it stop and roll back.
    //
    // Two ENTITY TYPES, deliberately: two records of one type collapse into a single statement, which is atomic on its
    // own and so cannot half-succeed. It takes two statements in one command to ask the question at all.
    await using var writer = NewSession();
    var taken = writer.NewEntity<Dml.IPerson>();
    taken.Id = Guid.NewGuid();
    taken.LastName = "Larsen";
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "Sunnyvale";

    // The address is written FIRST — a person references one, so the sort puts it there — and the person then fails.
    var clash = session.NewEntity<Dml.IPerson>();
    clash.Id = taken.Id;
    clash.LastName = "Someone Else";

    await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());

    Assert.IsNull(await ScalarAsync("Address", "City", address.Id),
      "the statement before the failing one committed — the batch did not roll back");
    Assert.AreEqual("Larsen", await ScalarAsync("Person", "LastName", taken.Id),
      "the row that was already there should be untouched");
  }

  [TestMethod]
  public async Task LinkRecord_CanBeDeletedAndReInsertedWithTheSameKey() {
    // THE CASE THE FIVE PHASES EXIST FOR. A user removes a link and adds one back — reordering a many-to-many, or
    // toggling a tag off and on — and the same primary key goes out and comes back in ONE save.
    //
    // With writes merely before deletes, the insert lands while the old row is still there and the server refuses it,
    // correctly. It works because the link is ISOLATED — nothing in the model points at it — so its delete can be
    // moved to the very front of the save and its insert to the very back, with everything else in between.
    await using var session = NewSession();

    var owner = session.NewEntity<Dml.IOwner>();
    owner.Id = Guid.NewGuid();
    owner.Name = "Larsen";
    var red = session.NewEntity<Dml.ITag>();
    red.Id = Guid.NewGuid();
    red.Name = "red";
    var blue = session.NewEntity<Dml.ITag>();
    blue.Id = Guid.NewGuid();
    blue.Name = "blue";

    var link = session.NewEntity<Dml.IOwnerTag>();
    var linkId = Guid.NewGuid();
    link.Id = linkId;
    link.Owner = owner;
    link.Tag = red;
    await session.SaveChangesAsync();

    // The same key, out and back, in one save.
    session.DeleteEntity(link);
    var replacement = session.NewEntity<Dml.IOwnerTag>();
    replacement.Id = linkId;
    replacement.Owner = owner;
    replacement.Tag = blue;
    await session.SaveChangesAsync();

    var tagId = await ProbeScalarAsync<Guid>(
      $"SELECT {Db.Quote("Tag_Id")} FROM {Db.GetTableRef("OwnerTag")} WHERE {Db.Quote("Id")} = @id", ("id", linkId));
    Assert.AreEqual(blue.Id, tagId, "the row was not replaced by the re-inserted link");
  }

  // ----- saved and let go of ---------------------------------------------------------------------------------------

  [TestMethod]
  public async Task ForgottenSave_WritesTheRowsAndStopsTracking() {
    // The save nobody reads afterwards — log rows, the ledger entries an invoice posting generates. The session is
    // about to be abandoned with the request that made it, so being ready for a next question is pure cost.
    await using var session = NewSession();

    var addresses = new List<Dml.IAddress>();
    for (var i = 0; i < 20; i++) {
      var address = session.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = "City-" + i;
      addresses.Add(address);
    }
    await session.SaveChangesAsync(SaveOptions.InsertAndForget);

    Assert.AreEqual("City-7", await ScalarAsync("Address", "City", addresses[7].Id), "the rows were written");

    var record = ((EntityBase)addresses[7]).Record;
    Assert.AreEqual(EntityStatus.Fantom, record.Status);
    Assert.AreEqual(EntityStatus.New, record.StatusBeforeSave, "an after-save handler still needs to know");

    // READING STILL WORKS, and that is the useful half — the id of a row just written is exactly what a caller logs.
    Assert.AreEqual("City-7", addresses[7].City);

    // CHANGING DOES NOT. Nothing tracks it, so the write would be saved by nobody — silent loss, turned into an
    // exception.
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => addresses[7].City = "Somewhere Else");
    StringAssert.Contains(ex.Message, "saved and let go of");

    // And it is out of the identity map: reading that key again builds a NEW object rather than finding this one.
    var reread = await session.GetEntityAsync<Dml.IAddress>(addresses[7].Id);
    Assert.IsNotNull(reread);
    Assert.AreNotSame(addresses[7], reread, "a forgotten record must not still answer for its identity");
  }

  [TestMethod]
  public async Task Forgetting_LetsAGeneratedIdTableBulkInsert() {
    // The objection to collapsing identity inserts is that the keys would have to come back matched by POSITION, which
    // no server promises. Ask for none and the objection goes with it — which is what this mode is worth to the legacy
    // key model.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    // TAGGED, and counted by the tag: the database is shared with every other test in this suite, so "how many
    // tickets are there" is not a question this test may ask. "How many of MINE" is.
    var tag = "forget-" + Guid.NewGuid().ToString("N")[..8] + "-";
    for (var i = 0; i < 12; i++)
      session.NewEntity<Dml.ITicket>().Title = tag + i;
    await session.SaveChangesAsync(SaveOptions.InsertAndForget);

    var count = await ProbeScalarAsync<int>(
      $"SELECT COUNT(*) FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Title")} LIKE @prefix", ("prefix", tag + "%"));
    Assert.AreEqual(12, count);
  }

  [TestMethod]
  public async Task Forgetting_StillBindsAChildToItsParentsNewKey() {
    // The shape an import has: parents whose keys the server makes, children pointing at them, nothing read back
    // afterwards. The mode means "read back only the keys the save itself needs" — the parents' are, to bind the
    // children's foreign keys — and never a refusal, which it used to be.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    var tag = "forget-" + Guid.NewGuid().ToString("N")[..8] + "-";
    var tickets = new List<Dml.ITicket>();
    for (var i = 0; i < 5; i++) {
      var ticket = session.NewEntity<Dml.ITicket>();
      ticket.Title = tag + i;
      tickets.Add(ticket);
      for (var j = 0; j < 3; j++) {
        var comment = session.NewEntity<Dml.IComment>();
        comment.Text = $"{tag}{i}.{j}";
        comment.Ticket = ticket;
      }
    }
    await session.SaveChangesAsync(SaveOptions.InsertAndForget);

    // Every comment landed on its own ticket — the comment's text names the ticket's.
    await AssertCommentsOnTheirTicketsAsync(tag, 15);
    Assert.AreEqual(EntityStatus.Fantom, ((EntityBase)tickets[0]).Record.Status, "forgotten, parents included");
  }

  /// <summary>Read the run's tickets and comments back by raw SQL and check each comment's text starts with its
  /// ticket's title — which is how the seed names them — so a comment bound to the wrong parent shows.</summary>
  private async Task AssertCommentsOnTheirTicketsAsync(string tag, int expectedComments) {
    var tickets = Db.GetTableRef("Ticket");
    var comments = Db.GetTableRef("Comment");
    var ticketIds = await Db.ColumnAsync($"SELECT {Db.Quote("Id")} FROM {tickets} WHERE {Db.Quote("Title")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    var titles = await Db.ColumnAsync($"SELECT {Db.Quote("Title")} FROM {tickets} WHERE {Db.Quote("Title")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    var titleById = ticketIds.Zip(titles).ToDictionary(p => p.First!, p => p.Second!);
    var texts = await Db.ColumnAsync($"SELECT {Db.Quote("Text")} FROM {comments} WHERE {Db.Quote("Text")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    var parents = await Db.ColumnAsync($"SELECT {Db.Quote("Ticket_Id")} FROM {comments} WHERE {Db.Quote("Text")} LIKE '{tag}%' ORDER BY {Db.Quote("Id")}");
    Assert.AreEqual(expectedComments, texts.Count, "every comment written");
    foreach (var (text, parent) in texts.Zip(parents))
      Assert.IsTrue(titleById.TryGetValue(parent!, out var title) && text!.StartsWith(title + ".", StringComparison.Ordinal),
        $"comment '{text}' is on ticket {parent} ('{(parent != null && titleById.TryGetValue(parent, out var t) ? t : "?")}')");
  }

  [TestMethod]
  public async Task IdentityKeys_TravelThroughTheBatch() {
    // A hundred tickets with three comments each — four hundred rows whose keys the server makes, the children's
    // foreign keys among them. On a server that carries a key through the batch this is ONE command: a parent's
    // insert leaves its key in a slot, a child writes the slot's name, and the client reads the keys once at the
    // end. Elsewhere every identity insert that wants its key is a round trip of its own.
    RequiresFeature(DbFeatures.IdentityColumns);
    await using var session = NewSession();

    var tag = "batch-" + Guid.NewGuid().ToString("N")[..8] + "-";
    var tickets = new List<Dml.ITicket>();
    var comments = new List<Dml.IComment>();
    for (var i = 0; i < 100; i++) {
      var ticket = session.NewEntity<Dml.ITicket>();
      ticket.Title = tag + i;
      tickets.Add(ticket);
      for (var j = 0; j < 3; j++) {
        var comment = session.NewEntity<Dml.IComment>();
        comment.Text = $"{tag}{i}.{j}";
        comment.Ticket = ticket;
        comments.Add(comment);
      }
    }
    await session.SaveChangesAsync();

    var carried = Db.Provider.Supports(DbFeatures.IdentityCarriedInBatch);
    Assert.AreEqual(carried ? 1 : 400, session.LastCommands.Count, carried ? "one batch" : "one round trip per identity row");

    // EVERY KEY CAME BACK, parents and children alike, and every child's foreign key is its parent's key — on the
    // records, not only in the database: a child's was written as a slot's name and told the value afterwards.
    Assert.IsTrue(tickets.All(t => t.Id > 0) && comments.All(c => c.Id > 0), "every key read back");
    var commentEntity = ((EntityBase)comments[0]).Record.Entity;
    var foreignKey = commentEntity.Members.Single(m => m.OwnerReference?.Name == nameof(Dml.IComment.Ticket));
    Assert.IsTrue(comments.All(c => Equals(((EntityBase)c).Record.GetRawValue(foreignKey), c.Ticket.Id)),
      "the foreign keys on the records");

    await AssertCommentsOnTheirTicketsAsync(tag, 300);
  }

  // ----- the transaction -------------------------------------------------------------------------------------------

  [TestMethod]
  public async Task AFailedSaveLeavesNOTHINGBehind() {
    // A save is all or nothing: half a graph written is worse than none of it, and the ordering that makes foreign
    // keys hold at every point is only a guarantee if the whole sequence commits together.
    await using var session = NewSession();

    var good = session.NewEntity<Dml.IPerson>();
    good.Id = Guid.NewGuid();
    good.LastName = "Larsen";

    // THE SAME KEY, so the second insert violates the primary key AFTER the first has already run. A value too long
    // for its column would have been the obvious way to fail, and SQLite does not enforce a length — its declared
    // types are affinities, and `varchar(50)` takes two hundred characters without complaint. A duplicate key is
    // refused by every server there is.
    var bad = session.NewEntity<Dml.IPerson>();
    bad.Id = good.Id;
    bad.LastName = "Larsen";

    await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());

    Assert.IsNull(await ScalarAsync("Person", "LastName", good.Id),
      "the first insert committed even though the save failed");
  }

  [TestMethod]
  public async Task Row_SaysWhatBlocksItsDeletion() {
    await using var writer = NewSession();
    var parent = writer.NewEntity<Dml.IParent>();
    parent.Id = Guid.NewGuid();
    parent.Code = "del-" + Guid.NewGuid().ToString("N")[..8];
    Assert.IsTrue(writer.CanDeleteEntity(parent, out var none), "a row never written has nothing pointing at it");
    Assert.AreEqual(0, none.Length, "and the answer is empty, not null");

    // A note GOES WITH its parent — `[CascadeDelete]` — so alone it blocks nothing.
    var note = writer.NewEntity<Dml.INote>();
    note.Id = Guid.NewGuid();
    note.Parent = parent;
    note.Text = "goes along";
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var read = await session.GetEntityAsync<Dml.IParent>(parent.Id);
    session.LogMessage("only a cascading child: nothing blocks");
    Assert.AreEqual(0, (await session.GetDeleteEntityBlockingTypesAsync(read)).Length);

    var child = session.NewEntity<Dml.IChild>();
    child.Id = Guid.NewGuid();
    child.Parent = read;
    child.Label = "stays";
    await session.SaveChangesAsync();

    session.LogMessage("a child that does not cascade: it blocks, and is named");
    var cache = App.DataAccess.FindDatabase()!.QueryCache;
    var (hits, misses) = (cache.Hits, cache.Misses);
    Assert.IsFalse(session.CanDeleteEntity(read, out var blocking));
    CollectionAssert.AreEqual(new[] { typeof(Dml.IChild) }, blocking);
    // ONE LOOKUP PER CHECK, under the reference's own fixed key: translated the first time the shape is met, found
    // afterwards — no tree built, nothing hashed.
    Assert.AreEqual(hits + misses + 1, cache.Hits + cache.Misses, "one lookup for one reference that does not cascade");
    (hits, misses) = (cache.Hits, cache.Misses);
    Assert.IsFalse(session.CanDeleteEntity(read, out _));
    Assert.AreEqual((hits + 1, misses), (cache.Hits, cache.Misses), "and found under its key the second time");

    // THE DATABASE AGREES, which is the point of asking: the delete it said was blocked IS refused…
    session.DeleteEntity(read);
    await Assert.ThrowsAsync<Exception>(() => session.SaveChangesAsync());

    // …and with the child gone first, the parent goes, taking its note along.
    await using var last = NewSession();
    last.DeleteEntity(await last.GetEntityAsync<Dml.IChild>(child.Id));
    await last.SaveChangesAsync();
    var free = await last.GetEntityAsync<Dml.IParent>(parent.Id);
    Assert.IsTrue(last.CanDeleteEntity(free, out _));
    last.DeleteEntity(free);
    await last.SaveChangesAsync();
  }

  [TestMethod]
  public async Task UniqueValue_FreedAndTakenInOneSave_IsVacatedFirst() {
    // A GRID SUBMIT. The user deletes the row named X and adds a new X, or renames rows into each other's names. A
    // save inserts before it updates and updates before it deletes, so the old row still holds the value when the
    // new one takes it. The row that frees a value gets a placeholder in a statement at the start of the save, and
    // the save then runs in its usual order. Parent is referenced by Child, so its rows cannot go to either end.
    var tag = "uv" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    Dml.IParent Parent(string code) {
      var parent = session.NewEntity<Dml.IParent>();
      parent.Id = Guid.NewGuid();
      parent.Code = tag + code;
      return parent;
    }
    async Task<List<(Guid Id, string Code)>> RowsAsync() {
      await using var reader = NewSession();
      var rows = await reader.EntitySet<Dml.IParent>().Where(p => p.Code.StartsWith(tag)).OrderBy(p => p.Code)
        .ToListAsync();
      return [.. rows.Select(p => (p.Id, p.Code[tag.Length..]))];
    }
    bool StartsWithVacate() => session.LastCommands[0].Sql.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal);

    var first = Parent("-X");
    var other = Parent("-O");
    await session.SaveChangesAsync();
    Assert.IsFalse(StartsWithVacate(), "nothing freed, nothing vacated");

    // ----- delete X, add a new X -------------------------------------------------------------------------------------
    session.DeleteEntity(first);
    var second = Parent("-X");
    await session.SaveChangesAsync();
    Assert.IsTrue(StartsWithVacate(), "the old row gave up its value first: " + session.LastCommands[0].Sql);
    CollectionAssert.AreEqual(new[] { (other.Id, "-O"), (second.Id, "-X") }, await RowsAsync());

    // ----- rename X to Y, add a new X --------------------------------------------------------------------------------
    second.Code = tag + "-Y";
    var third = Parent("-X");
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { (other.Id, "-O"), (third.Id, "-X"), (second.Id, "-Y") }, await RowsAsync());

    // ----- delete X, rename another row to X -------------------------------------------------------------------------
    session.DeleteEntity(third);
    other.Code = tag + "-X";
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { (other.Id, "-X"), (second.Id, "-Y") }, await RowsAsync());

    // ----- two rows swap their values --------------------------------------------------------------------------------
    other.Code = tag + "-Y";
    second.Code = tag + "-X";
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { (second.Id, "-X"), (other.Id, "-Y") }, await RowsAsync());

    // ----- delete X, add a new X, move the old row's child to the new one --------------------------------------------
    var child = session.NewEntity<Dml.IChild>();
    child.Id = Guid.NewGuid();
    child.Parent = second;
    child.Label = tag;
    await session.SaveChangesAsync();
    Assert.IsFalse(StartsWithVacate());
    session.DeleteEntity(second);
    var fourth = Parent("-X");
    child.Parent = fourth;
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { (fourth.Id, "-X"), (other.Id, "-Y") }, await RowsAsync());
    await using (var reader = NewSession())
      Assert.AreEqual(fourth.Id, (await reader.GetEntityAsync<Dml.IChild>(child.Id)).Parent.Id);

    // ----- a number unique within a parent: two positions swapped ----------------------------------------------------
    Dml.ISlot Slot(int position) {
      var slot = session.NewEntity<Dml.ISlot>();
      slot.Id = Guid.NewGuid();
      slot.Parent = other;
      slot.Position = position;
      return slot;
    }
    var zero = Slot(0);
    var one = Slot(1);
    await session.SaveChangesAsync();
    zero.Position = 1;
    one.Position = 0;
    await session.SaveChangesAsync();
    Assert.IsTrue(StartsWithVacate());
    await using (var reader = NewSession()) {
      Assert.AreEqual(1, (await reader.GetEntityAsync<Dml.ISlot>(zero.Id)).Position);
      Assert.AreEqual(0, (await reader.GetEntityAsync<Dml.ISlot>(one.Id)).Position);
    }
  }

  [TestMethod]
  public async Task DeleteCheck_IsOneStatementForEveryReference() {
    // A person is pointed at by a phone and, through Manager, by another person. Whatever the number of references,
    // the check is one statement: the row by its key, with a column per entity that refers to it.
    await using var session = NewSession();
    Dml.IPerson Person(string name, Dml.IPerson? manager = null) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = name;
      person.Manager = manager;
      return person;
    }
    var tag = "chk-" + Guid.NewGuid().ToString("N")[..6];
    var boss = Person(tag + "-boss");
    var report = Person(tag + "-report", boss);
    var loner = Person(tag + "-loner");
    var phone = session.NewEntity<Dml.IPhone>();
    phone.Id = Guid.NewGuid();
    phone.Number = tag;
    phone.Person = boss;
    await session.SaveChangesAsync();

    var blocking = await session.GetDeleteEntityBlockingTypesAsync(boss);
    Assert.AreEqual(1, session.LastCommands.Count, "one statement: " + session.LastCommands[0].Sql);
    CollectionAssert.AreEquivalent(new[] { typeof(Dml.IPerson), typeof(Dml.IPhone) }, blocking);

    // Nothing points at the report; given a phone, the phone alone blocks it.
    Assert.AreEqual(0, (await session.GetDeleteEntityBlockingTypesAsync(report)).Length);
    var own = session.NewEntity<Dml.IPhone>();
    own.Id = Guid.NewGuid();
    own.Number = tag + "-2";
    own.Person = report;
    await session.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { typeof(Dml.IPhone) }, await session.GetDeleteEntityBlockingTypesAsync(report));

    // A record whose row another session deleted is blocked by nothing.
    Assert.AreEqual(0, (await session.GetDeleteEntityBlockingTypesAsync(loner)).Length);
    await using (var other = NewSession()) {
      other.DeleteEntity(await other.GetEntityAsync<Dml.IPerson>(loner.Id));
      await other.SaveChangesAsync();
    }
    Assert.AreEqual(0, (await session.GetDeleteEntityBlockingTypesAsync(loner)).Length);
  }
}
