using System.Text;
using Mic.Orm;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE LOG, as far as it goes with no database: what an entry captures, what a mode does with it, and what a batch
// reads like once a listener renders it. The statements a real save produces are the data suite's to check.
[TestClass]
public class LoggingTests {
  public interface IPerson {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  static EntityApp NewApp(LogMode mode = LogMode.Details) {
    var app = new EntityApp("Logs", new Version(1, 0), new EntityAppSettings {
      DefaultSchema = "app", LogMode = mode,
    });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPerson));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }

  /// <summary>A listener that keeps what it was given, so a test can read the batches rather than a file.</summary>
  sealed class Collector : ILogListener {
    public List<IReadOnlyList<LogEntry>> Batches { get; } = [];
    public IEnumerable<LogEntry> Entries => Batches.SelectMany(b => b);
    public void WriteBatch(IReadOnlyList<LogEntry> batch) => Batches.Add(batch);
  }

  // ----- the modes, which is the whole point of the facility -----------------------------------------------------

  [TestMethod]
  public async Task Mode_DecidesWhetherRecordsArePosted() {
    // ----- None records nothing at all -------------------------------------------------------------------------
    //
    // The cheap path: the ORM's own layers call `Log` unconditionally on the save path, so the OFF case has to cost a
    // field read and a return.
    var silent = NewApp(LogMode.None);
    var silentCollector = new Collector();
    silent.LogService.AddListener(silentCollector);

    var quiet = new OperationContext(silent);
    quiet.Log("nothing doing");
    quiet.Dispose();
    await silent.LogService.FlushAsync();

    Assert.AreEqual(0, silentCollector.Batches.Count);

    // ----- Always posts whether or not anything went wrong -------------------------------------------------------
    var app = NewApp();
    var collector = new Collector();
    app.LogService.AddListener(collector);

    var context = new OperationContext(app);
    context.Log("first");
    context.Log("second");
    Assert.AreEqual(0, collector.Batches.Count, "nothing is posted until the operation ends or the buffer fills");

    context.Dispose();                       // the operation ending is what posts
    await app.LogService.FlushAsync();

    CollectionAssert.AreEqual(new[] { "first", "second" },
      collector.Entries.OfType<MessageLogEntry>().Select(e => e.Message).ToArray());

    // ----- A BATCH IS AN OPERATION, and nothing re-chunks it ---------------------------------------------------------
    //
    // The service holds what it was handed rather than tearing it into entries and reassembling a list of very nearly
    // the same ones. So two operations are two batches, whatever their sizes — which is also what keeps a file's
    // entries from mixing two requests into one block.
    Assert.AreEqual(1, collector.Batches.Count, "one operation, one batch");

    var second = new OperationContext(app);
    second.Log("a different operation");
    second.Dispose();
    await app.LogService.FlushAsync();

    Assert.AreEqual(2, collector.Batches.Count);
    Assert.AreEqual(1, collector.Batches[1].Count, "sized by what the operation produced, not by BatchSize");

    // ----- OnError DISCARDS a clean operation ---------------------------------------------------------------------
    //
    // The whole point of the mode: the detail was captured in case it was needed, and it was not.
    var onError = NewApp(LogMode.OnError);
    var errorCollector = new Collector();
    onError.LogService.AddListener(errorCollector);

    var clean = new OperationContext(onError);
    clean.Log("captured, and about to be thrown away");
    clean.Dispose();
    await onError.LogService.FlushAsync();

    Assert.AreEqual(0, errorCollector.Batches.Count);
    Assert.IsFalse(clean.HasErrors);

    // ----- …and posts the WHOLE operation when something does go wrong ----------------------------------------------
    //
    // Including what was recorded BEFORE the failure, which is the reason to keep it: the statements leading up to an
    // error are what explain it.
    var failed = new OperationContext(onError);
    failed.Log("what happened before");
    failed.LogError("the thing that went wrong", new InvalidOperationException("boom"));
    Assert.IsTrue(failed.HasErrors);

    // ----- AT ONCE, without waiting for the operation to end ---------------------------------------------------------
    //
    // The operation that fails is the one least likely to reach its own end tidily — a crash, a torn-down process, an
    // abandoned request — and it is the one whose log is worth having. So the error does not wait for `Dispose`.
    await onError.LogService.FlushAsync();

    var posted = errorCollector.Entries.ToList();
    Assert.AreEqual(2, posted.Count, "the lead-up is posted with the failure, not just the failure");
    Assert.IsFalse(posted[0].IsError);
    Assert.IsTrue(posted[1].IsError);

    // …and disposing afterwards adds nothing, because the buffer was taken.
    failed.Dispose();
    await onError.LogService.FlushAsync();
    Assert.AreEqual(2, errorCollector.Entries.Count());
  }

  [TestMethod]
  public void Cap_DoesOppositeThingsInTheTwoModes() {
    // ----- OnError DROPS THE OLDEST, because there is nowhere to write to yet ---------------------------------------
    //
    // A ring: what is worth keeping is the tail nearest a failure that has not happened.
    var app = NewApp(LogMode.OnError);
    app.Settings.LogBufferSize = 3;
    var collector = new Collector();
    app.LogService.AddListener(collector);

    var context = new OperationContext(app);
    for (var i = 0; i < 6; i++)
      context.Log($"entry {i}");
    context.LogError("and then it failed");
    context.Dispose();
    app.LogService.FlushAsync().GetAwaiter().GetResult();

    var kept = collector.Entries.ToList();

    // THE RING BOUNDS THE LEAD-UP, and the error arrives on top of it: six entries at a cap of three leaves the last
    // three, and the failure makes four. Which is the shape wanted — the buffer cannot grow forever, and what survives
    // is the tail nearest the failure rather than the beginning of a loop nobody is reading.
    CollectionAssert.AreEqual(new[] { "entry 3", "entry 4", "entry 5" },
      kept.OfType<MessageLogEntry>().Select(e => e.Message).ToArray());
    Assert.AreEqual(4, kept.Count);
    Assert.IsTrue(kept[^1].IsError);

    // ----- Always FLUSHES at the cap instead, losing nothing ---------------------------------------------------------
    var always = NewApp();
    always.Settings.LogBufferSize = 3;
    var everything = new Collector();
    always.LogService.AddListener(everything);

    var busy = new OperationContext(always);
    for (var i = 0; i < 10; i++)
      busy.Log($"entry {i}");
    busy.Dispose();
    always.LogService.FlushAsync().GetAwaiter().GetResult();

    Assert.AreEqual(10, everything.Entries.Count(), "a log that is going out anyway must not drop anything");
  }

  // ----- what an entry carries, and when it is rendered -----------------------------------------------------------

  [TestMethod]
  public void Entry_IsStampedWithSourceAndClock() {
    var app = NewApp();
    var context = new OperationContext(app) { WebRequestId = "req-1", CorrelationId = "corr-1" };

    Assert.AreEqual("corr-1", context.CorrelationId);

    // ----- an entry posted to the CONTEXT carries no session --------------------------------------------------------
    var bare = context.LogSource;
    Assert.IsNull(bare.SessionId);
    Assert.AreEqual("corr-1", bare.CorrelationId);
    Assert.AreEqual("req-1", bare.WebRequestId);

    // ----- one posted through a SESSION carries that session's id ---------------------------------------------------
    var session = new EntitySession(context);
    Assert.AreNotEqual(Guid.Empty, session.SessionId);
    Assert.AreEqual(session.SessionId, session.LogSource.SessionId);
    Assert.AreEqual("corr-1", session.LogSource.CorrelationId, "and the context's ids, unchanged");

    // Two sessions of one context are told apart, which is what makes a combined log readable.
    Assert.AreNotEqual(session.SessionId, new EntitySession(context).SessionId);

    // ----- the time comes from the application's clock, not the machine's -------------------------------------------
    //
    // The same rule that governs entity keys. Asserted by SHIFTING the clock: an entry reading `DateTime.UtcNow` would
    // be stamped now.
    app.RegisterService<ITimeService>(new Mic.Orm.Services.AdjustableTimeService(app) {
      TimeShift = TimeSpan.FromDays(365),
    });
    var shifted = new MessageLogEntry(context.LogSource, context.Now, "later");
    Assert.IsTrue(shifted.Time > DateTime.UtcNow.AddDays(300), "the entry was stamped from the machine's clock");
  }

  // ----- severity, a place, and the flat form that travels ------------------------------------------------------------
  //
  // What replaced `ModelDiagnostic` and `DbUpgradeMessage`: one entry type carrying a severity and a location, and one
  // flattened form of it that can be stored or sent. `LogEntry` itself is abstract and renders on demand, which is
  // what makes posting cheap and is also why it cannot travel.
  [TestMethod]
  public void Entry_CarriesSeverityAndPlace() {
    var app = NewApp();
    var context = new OperationContext(app);

    // ----- a WARNING is expressible, which an IsError bool could not do -----------------------------------------------
    var warning = new MessageLogEntry(context.LogSource, context.Now, "size 50 → 80",
      Severity.Warning, "Person.Name");
    Assert.AreEqual(Severity.Warning, warning.Severity);
    Assert.IsFalse(warning.IsError, "a warning is not what an error-only log flushes for");
    StringAssert.Contains(warning.ToString(), "WARNING: Person.Name: size 50 → 80");

    // ----- and an error still is, so LogMode.OnError is untouched -----------------------------------------------------
    Assert.IsTrue(new MessageLogEntry(context.LogSource, context.Now, "bad", Severity.Error).IsError);
    Assert.IsTrue(new ErrorLogEntry(context.LogSource, context.Now, "threw", new InvalidOperationException()).IsError);
  }

  [TestMethod]
  public void Batch_IsRenderedWithOneHeaderPerSource() {
    // Grouping is what keeps a combined log readable: two sessions' statements read as two blocks rather than
    // interleaved, and the ids are printed once per block instead of on every line.
    var app = NewApp();
    var context = new OperationContext(app) { WebRequestId = "req-1" };
    var first = new EntitySession(context);
    var second = new EntitySession(context);

    var path = Path.Combine(Path.GetTempPath(), $"mic-log-{Guid.NewGuid():N}.log");
    var listener = new FileLogListener(path);
    try {
      listener.WriteBatch([
        new MessageLogEntry(first.LogSource, context.Now, "one"),
        new MessageLogEntry(first.LogSource, context.Now, "two"),
        new MessageLogEntry(second.LogSource, context.Now, "three"),
      ]);

      var text = File.ReadAllText(path);
      Assert.AreEqual(2, text.Split("-- session").Length - 1, "one header per source, not one per entry");
      StringAssert.Contains(text, first.SessionId.ToString("D"));
      StringAssert.Contains(text, second.SessionId.ToString("D"));
      StringAssert.Contains(text, "request req-1");
      StringAssert.Contains(text, "one");
      StringAssert.Contains(text, "three");
    } finally {
      File.Delete(path);
    }
  }

  [TestMethod]
  public async Task SetLogFilePath_TurnsLoggingOn() {
    // Off by default, so an application that says nothing pays nothing.
    var app = NewApp(LogMode.None);
    Assert.AreEqual(LogMode.None, app.Settings.LogMode);

    var path = Path.Combine(Path.GetTempPath(), $"mic-log-{Guid.NewGuid():N}.log");
    File.WriteAllText(path, "left over from a previous run");
    try {
      app.SetLogFilePath(path);

      // ----- naming a file is not an ambiguous signal about whether you want logging ---------------------------------
      //
      // A path with the mode left at `None` would create the file and leave it empty, which is worse than either doing
      // nothing or working.
      Assert.AreEqual(LogMode.Details, app.Settings.LogMode);

      // ----- and a previous run's file is GONE ------------------------------------------------------------------------
      //
      // It is a debugging facility: a log showing yesterday's failure is worse than no log, because the failure it
      // shows is not the one being looked at.
      Assert.IsFalse(File.Exists(path) && File.ReadAllText(path).Contains("previous run"));

      await using (var session = new EntitySession(new OperationContext(app)))
        session.LogMessage("through the file listener");
      // The context is what posts; the service is what waits.
      var context = new OperationContext(app);
      context.Log("and a message with no session");
      context.Dispose();
      await app.LogService.FlushAsync();

      StringAssert.Contains(File.ReadAllText(path), "and a message with no session");

      // ----- and detail-on-error is a NARROWING, said afterwards -------------------------------------------------------
      //
      // `SetLogFilePath` sets the mode outright rather than negotiating with whatever was there — so an application
      // that wants the log only on failure says so after it, which reads as the narrowing it is.
      app.Settings.LogMode = LogMode.OnError;
      Assert.AreEqual(LogMode.OnError, app.Settings.LogMode);
    } finally {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void LogService_CanBeReplaced() {
    // Like the clock and the data-access registry: resolved per read, so an application that installs its own — handing
    // entries to a host pipeline rather than batching them again — has THAT one used.
    var app = NewApp();
    Assert.IsInstanceOfType<LogService>(app.LogService);

    var mine = new CollectingService();
    app.RegisterService<ILogService>(mine);
    Assert.AreSame(mine, app.LogService, "the replacement must be what the ORM posts to");

    var context = new OperationContext(app);
    context.Log("straight into the replacement");
    context.Dispose();

    Assert.AreEqual(1, mine.Posted.Count);
  }

  sealed class CollectingService : ILogService {
    public List<LogEntry> Posted { get; } = [];
    public void Post(IReadOnlyList<LogEntry> entries) => Posted.AddRange(entries);
    public Task FlushAsync() => Task.CompletedTask;
    public void AddListener(ILogListener listener) { }
  }

  [TestMethod]
  public async Task Orm_WritesItsOwnFailuresToTheLog() {
    // WITHOUT THIS, `LogMode.OnError` FIRES ON ALMOST NOTHING. An error entry was only ever created by application
    // code calling `LogError`, so the mode could not be triggered by the failures the ORM is in the best position to
    // report — which are the ones somebody turned it on for.
    var app = NewApp(LogMode.OnError);
    var collector = new Collector();
    app.LogService.AddListener(collector);
    AppInitHelper.CreateDatabase(app, new Mic.Orm.Db.DbSettings(new Mic.Orm.Db.Postgres.PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));

    var session = new EntitySession(new OperationContext(app));
    session.LogMessage("about to save into thin air");
    session.NewEntity<IPerson>().LastName = "Larsen";

    await Assert.ThrowsExactlyAsync<Mic.Orm.Db.DataAccessException>(() => session.SaveChangesAsync());
    await app.LogService.FlushAsync();

    // ----- posted WITHOUT the operation ever ending ------------------------------------------------------------------
    //
    // The context was never disposed. An error posts at once, which is the whole point: a failing operation is the one
    // least likely to reach its own end tidily.
    var posted = collector.Entries.ToList();
    Assert.IsTrue(posted.Any(e => e.IsError), "the ORM's own failure never reached its own log");

    // ----- and the LEAD-UP came with it --------------------------------------------------------------------------------
    Assert.IsTrue(posted.OfType<MessageLogEntry>().Any(e => e.Message == "about to save into thin air"),
      "the error posted alone, without what led to it");

    var error = (ErrorLogEntry)posted.First(e => e.IsError);
    StringAssert.Contains(error.Message, "failed");
    Assert.IsNotNull(error.Error, "the exception itself is kept, not just its message");
  }

  [TestMethod]
  public void Listener_ThatThrowsDoesNotTakeTheApplicationDown() {
    // An unreachable log server is not a reason to fail the request that was being logged — and it must not stop the
    // OTHER listeners either.
    var app = NewApp();
    var good = new Collector();
    app.LogService.AddListener(new ThrowingListener());
    app.LogService.AddListener(good);

    var context = new OperationContext(app);
    context.Log("still recorded");
    context.Dispose();
    app.LogService.FlushAsync().GetAwaiter().GetResult();

    Assert.AreEqual(1, good.Entries.Count());
  }

  sealed class ThrowingListener : ILogListener {
    public void WriteBatch(IReadOnlyList<LogEntry> batch) => throw new InvalidOperationException("no log server");
  }

  [TestMethod]
  public void ADbCommandEntryCapsWhatItCapturesRatherThanWhatItPrints() {
    // THE CAP IS ON THE CAPTURE. A cap only in the formatter would still copy every parameter reference on the thread
    // the application is waiting on, in order to print ten of them — which is the precise cost the command snapshot
    // refuses to pay at all.
    var provider = new Mic.Orm.Db.Postgres.PostgresProvider();
    var dbCommand = provider.CreateDbCommand();
    dbCommand.CommandText = "INSERT INTO \"app\".\"Person\" VALUES (…)";
    for (var i = 0; i < 25; i++)
      provider.AddParameter(dbCommand, $"p{i}", i);

    var entry = new DbCommandLogEntry(default, DateTime.UtcNow, dbCommand,
      Mic.Orm.Db.DbExecutionType.NonQuery, rowCount: 25, timeMs: 1.5);

    Assert.AreEqual(25, entry.ParameterCount, "it still says how many there really were");

    var text = new StringBuilder();
    entry.WriteText(text);
    var rendered = text.ToString();

    StringAssert.Contains(rendered, "p0=0");
    StringAssert.Contains(rendered, "… (10 of 25)");
    Assert.IsFalse(rendered.Contains("p10="), "the eleventh was never captured, so it cannot be printed");
    StringAssert.Contains(rendered, "25 row(s)");
    StringAssert.Contains(rendered, "1.5 ms");

    // ----- the SQL is on a line of its OWN, ready to select and run -----------------------------------------------------
    //
    // Preceded by a blank line — a save writes several statements and without the break they run together into a wall —
    // and starting at column zero with no timestamp in front, because what somebody does with a statement in a log is
    // copy it. Everything that is not the statement is below it, the time included.
    StringAssert.StartsWith(rendered, Environment.NewLine);
    var lines = rendered.Split(Environment.NewLine);
    Assert.AreEqual("INSERT INTO \"app\".\"Person\" VALUES (…)", lines[1], "nothing in front of the SQL");
    StringAssert.StartsWith(lines[2].TrimStart(), "-- Parameters:");
    StringAssert.Contains(lines[3], "25 row(s), ", "the time joins the duration and the row count");

    // ----- BINARY is let go at capture, but a SHORT one keeps its value ------------------------------------------------
    //
    // A byte[] kept as a reference pins the whole array for the life of the buffer — in error-only mode, the length of
    // the request. But sixteen bytes is a GUID: MySQL stores one as BINARY(16), and rendering `byte[16]` for a primary
    // key makes the log useless exactly where it matters most. So a short one is hex and a long one is its length.
    var binary = provider.CreateDbCommand();
    binary.CommandText = "INSERT";
    provider.AddParameter(binary, "key", new byte[] {
      0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF });
    provider.AddParameter(binary, "blob", new byte[4096]);
    var binaryEntry = new DbCommandLogEntry(default, DateTime.UtcNow, binary,
      Mic.Orm.Db.DbExecutionType.NonQuery, 1, 0.1);

    var binaryText = new StringBuilder();
    binaryEntry.WriteText(binaryText);
    var shown = binaryText.ToString();

    StringAssert.Contains(shown, "key=0x0123456789ABCDEF0123456789ABCDEF");
    StringAssert.Contains(shown, "blob=byte[4096]");
    // Verbatim, not quoted and labelled `(String)` — the type annotation is there to name the VALUE's type, and a
    // rendering that claimed these were strings would be naming its own.
    Assert.IsFalse(shown.Contains("'0x0123"), "the hex was rendered as though it were a string value");
  }
}
