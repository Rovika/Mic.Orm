using System.Diagnostics;
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// ROW LOCKS ON A DOCUMENT'S ROOT: a writer holds the header for update and every other writer waits on that row;
// a reader runs at the snapshot level and never waits. The article's test in miniature — writers adding to a
// document's lines and recomputing its total while readers check the total equals the sum — and the two facts the
// mechanism rests on, asserted directly: a second writer's locked read waits until the first saves, and the
// transaction begun for the caller is committed by the save.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task RowLocks_SerializeWritersOnTheRoot() {
    var document = await NewDocumentAsync("lock-" + Guid.NewGuid().ToString("N")[..6], 3);

    // A SERVER WITHOUT ROW LOCKS accepts the flags and reads plainly — SQLite, whose write transaction locks the
    // whole file; the rest of this test is about what a lock does, and runs where there is one.
    if (!Db.Provider.Supports(DbFeatures.RowLocks)) {
      await using var plain = NewSession();
      var read = await plain.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
      Assert.IsNotNull(read);
      Assert.IsFalse(plain.HasOpenTransaction, "no lock, no transaction begun for it");
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no row locks; the flags are accepted and ignored.");
    }

    // ----- a locked read begins a transaction the save commits ---------------------------------------------------
    await using (var writer = NewSession()) {
      Assert.IsFalse(writer.HasOpenTransaction);
      var header = await writer.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
      Assert.IsTrue(writer.HasOpenTransaction, "a locked read begins the transaction the lock lives in");
      header.Name = header.Name;      // nothing changed, and nothing to write: the save still ends the transaction
      await writer.SaveChangesAsync();
      Assert.IsFalse(writer.HasOpenTransaction, "the save committed it");
    }

    // ----- a second writer waits on the first ------------------------------------------------------------------
    await using (var first = NewSession())
    await using (var second = NewSession()) {
      var held = await first.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
      var clock = Stopwatch.StartNew();
      // On a thread of its own: a driver whose asynchronous calls block, as IBM's do, would hold this one.
      var waiting = Task.Run(() => second.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock));
      var finishedEarly = await Task.WhenAny(waiting, Task.Delay(400)) == waiting;
      var outcome = waiting.IsFaulted ? "it failed: " + waiting.Exception!.GetBaseException() : "it completed";
      Assert.IsFalse(finishedEarly,
        $"the second writer's locked read must wait while the first holds the root; after {clock.ElapsedMilliseconds} ms {outcome}");

      // The lines and the total kept consistent, since the readers below check that: 42 in one line, 42 in the total.
      held.Lines[0].Value = 42;
      held.Total = 42;
      await first.SaveChangesAsync();
      var released = await waiting;
      Assert.AreEqual(42, released.Total, "the second writer sees the first's save, not the row it waited on");
      await second.RollbackTransactionAsync();
    }

    // ----- a reader at the snapshot level does not wait, and sees a consistent document ----------------------------
    await using (var writer = NewSession())
    await using (var reader = NewSession()) {
      var held = await writer.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
      held.Lines[1].Value = 57;
      held.Total = 99;
      await writer.SaveChangesAsync();   // committed: 99 is what any reader sees from now on

      var seen = await reader.GetEntityAsync<Dml.IDocument>(document, LoadFlags.ReadLock);
      Assert.IsTrue(reader.HasOpenTransaction, "a read lock is a transaction at the snapshot level");
      Assert.AreEqual(99, seen.Total);
      Assert.AreEqual(99, seen.Lines.Sum(l => l.Value), "the lines of the same moment");
      await reader.CommitTransactionAsync();
      Assert.IsFalse(reader.HasOpenTransaction);
    }

    // ----- the article's workload: writers and readers over one document, the total always the sum -----------------
    const int writers = 4, readers = 2, rounds = 6;
    var failures = new List<string>();
    var work = new List<Task>();
    for (var w = 0; w < writers; w++)
      work.Add(Task.Run(async () => {
        for (var round = 0; round < rounds; round++) {
          await using var session = NewSession();
          var header = await session.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
          var line = header.Lines[Random.Shared.Next(header.Lines.Count)];
          line.Value += 1;
          header.Total = header.Lines.Sum(l => l.Value);
          await session.SaveChangesAsync();
        }
      }));
    for (var r = 0; r < readers; r++)
      work.Add(Task.Run(async () => {
        for (var round = 0; round < rounds * 2; round++) {
          await using var session = NewSession();
          var header = await session.GetEntityAsync<Dml.IDocument>(document, LoadFlags.ReadLock);
          var sum = header.Lines.Sum(l => l.Value);
          if (sum != header.Total)
            lock (failures)
              failures.Add($"total {header.Total}, lines {sum}");
          await session.CommitTransactionAsync();
        }
      }));
    await Task.WhenAll(work);
    Assert.AreEqual(0, failures.Count, "an inconsistent read: " + string.Join("; ", failures));

    await using var check = NewSession();
    var final = await check.GetEntityAsync<Dml.IDocument>(document);
    Assert.AreEqual(99 + writers * rounds, final.Lines.Sum(l => l.Value), "every increment landed — no lost update");
    Assert.AreEqual(final.Lines.Sum(l => l.Value), final.Total);
  }

  [TestMethod]
  public async Task LockedRead_RefreshesTheTrackedRecord() {
    var document = await NewDocumentAsync("fresh-" + Guid.NewGuid().ToString("N")[..6], 1);
    if (!Db.Provider.Supports(DbFeatures.RowLocks))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no row locks; a locked read answers the tracked record.");

    // ----- a read by key under a lock --------------------------------------------------------------------------
    await using var session = NewSession();
    var header = await session.GetEntityAsync<Dml.IDocument>(document);
    header.Name = "mine";
    await ChangeDocumentAsync(document, "theirs", 7);
    Assert.AreEqual(0, header.Total, "the session's copy, read before the other session saved");

    var locked = await session.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);

    Assert.AreSame(header, locked, "one identity, one entity");
    Assert.AreEqual(7, header.Total, "a member not assigned is what the server holds under the lock");
    Assert.AreEqual("mine", header.Name, "a member assigned before the lock is kept");
    // The row version came with the refresh, so the save matches the row and is not refused as stale.
    await session.SaveChangesAsync();
    await using (var check = NewSession()) {
      var saved = await check.GetEntityAsync<Dml.IDocument>(document);
      Assert.AreEqual("mine", saved.Name);
      Assert.AreEqual(7, saved.Total, "the other session's total stands: the update named the name alone");
    }

    // ----- a plain query keeps the tracked record; a locked one refreshes it -----------------------------------------
    await ChangeDocumentAsync(document, "theirs", 9);
    await session.EntitySet<Dml.IDocument>().Where(d => d.Id == document).ToListAsync();
    Assert.AreEqual(7, header.Total, "a plain query answers the tracked record as it is");

    await session.BeginTransactionAsync();
    var rows = await session.EntitySet<Dml.IDocument>(QueryOptions.LockForUpdate).Where(d => d.Id == document)
      .ToListAsync();
    Assert.AreSame(header, rows.Single());
    Assert.AreEqual(9, header.Total, "a locked query refreshes the rows it holds");
    Assert.AreEqual("theirs", header.Name);
    await session.RollbackTransactionAsync();
  }

  [TestMethod]
  public async Task LockedRead_LeavesAnOnDemandMemberToBeLoadedAgain() {
    if (!Db.Provider.Supports(DbFeatures.RowLocks))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no row locks; a locked read answers the tracked record.");
    var entity = App.Model!.FindEntity("Article")!;
    var (body, summary) = (entity.GetMember("Body"), entity.GetMember("Summary"));
    Guid id;
    await using (var writer = NewSession()) {
      var written = writer.NewEntity<Dml.IArticle>();
      (written.Title, written.Body, written.Summary) = ("locked", "first text", "first summary");
      await writer.SaveChangesAsync();
      id = written.Id;
    }

    await using var session = NewSession();
    var article = await session.GetEntityAsync<Dml.IArticle>(id);
    var record = ((EntityBase)article).Record;
    Assert.AreEqual("first text", article.Body);
    article.Summary = "mine";
    await using (var other = NewSession()) {
      var theirs = await other.GetEntityAsync<Dml.IArticle>(id);
      (theirs.Body, theirs.Summary) = ("second text", "theirs");
      await other.SaveChangesAsync();
    }

    // ----- a plain read leaves the text the record holds ---------------------------------------------------------
    await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id).ToListAsync();
    Assert.IsTrue(record.Loaded.IsSet(body.DataIndex));
    Assert.AreEqual("first text", article.Body);

    // ----- a read under a lock: its row does not carry the text, so the record lets go of it --------------------
    await session.GetEntityAsync<Dml.IArticle>(id, LoadFlags.WriteLock);

    Assert.IsFalse(record.Loaded.IsSet(body.DataIndex), "the text read before the lock is not held any more");
    Assert.AreEqual("second text", article.Body, "the next read loads it, under the lock");
    Assert.AreEqual("mine", article.Summary, "a member assigned before the lock is kept");
    Assert.IsTrue(record.Modified.IsSet(summary.DataIndex));
    await session.SaveChangesAsync();
    Assert.AreEqual("second text", await ScalarAsync<string>("Article", "Body", id));
    Assert.AreEqual("mine", await ScalarAsync<string>("Article", "Summary", id));
  }

  [TestMethod]
  public async Task WriteLock_IsSpelledOnTheStatement() {
    // The by-key read under a write lock is its own statement — the lock is in the text — and the plain read's
    // shape is untouched by it. SQLite has no spelling, by declaration, and reads the plain statement inside its
    // write transaction.
    var document = await NewDocumentAsync("spell-" + Guid.NewGuid().ToString("N")[..6], 1);
    await using var session = NewSession();
    await session.GetEntityAsync<Dml.IDocument>(document, LoadFlags.WriteLock);
    var text = session.LastCommands[^1].Sql;
    var dialect = Db.Provider.SqlDialect;
    var spelled = dialect.SelectForUpdate.Length > 0 ? dialect.SelectForUpdate.Trim() : dialect.TableHintForUpdate.Trim();
    if (spelled.Length > 0)
      StringAssert.Contains(text, spelled);
    else
      Assert.IsFalse(text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase));
    await session.RollbackTransactionAsync();

    await session.GetEntityAsync<Dml.IDocument>(Guid.NewGuid());   // a plain read of the same shape: no lock
    Assert.IsFalse(session.LastCommands[^1].Sql.Contains("UPDLOCK") || session.LastCommands[^1].Sql.Contains("FOR UPDATE"));
  }

  [TestMethod]
  public async Task SkipLocked_TakesTheNextFreeRow() {
    // A TABLE AS A QUEUE: each worker takes the next free row; a row another worker holds is skipped, not waited for.
    var tag = "queue-" + Guid.NewGuid().ToString("N")[..6];
    for (var i = 1; i <= 3; i++)
      await NewDocumentAsync($"{tag}-{i}", 0);
    static Task<IReadOnlyList<Dml.IDocument>> TakeNextAsync(IEntitySession session, string tag) =>
      session.EntitySet<Dml.IDocument>(QueryOptions.SkipLocked)
        .Where(d => d.Name.StartsWith(tag)).OrderBy(d => d.Name).Take(1).ToListAsync();

    // A server that takes no row limit under a lock refuses the query at translation, naming the feature.
    if (Db.Provider.Supports(DbFeatures.NoRowLimitWithLock)) {
      await using var refusing = NewSession();
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => TakeNextAsync(refusing, tag));
      StringAssert.Contains(refused.Message, "NoRowLimitWithLock");
      return;
    }

    await using var first = NewSession();
    await first.BeginTransactionAsync();
    var taken = (await TakeNextAsync(first, tag)).Single();
    Assert.AreEqual($"{tag}-1", taken.Name);
    if (!Db.Provider.Supports(DbFeatures.RowLocks)) {
      await first.RollbackTransactionAsync();
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no row locks; the option is accepted and ignored.");
    }
    var dialect = Db.Provider.SqlDialect;
    var spelled = dialect.SelectForUpdateSkipLocked.Length > 0
      ? dialect.SelectForUpdateSkipLocked.Trim() : dialect.TableHintForUpdateSkipLocked.Trim();
    StringAssert.Contains(first.LastCommands[^1].Sql, spelled);

    await using var second = NewSession();
    await second.BeginTransactionAsync();
    var next = (await TakeNextAsync(second, tag)).Single();
    Assert.AreEqual($"{tag}-2", next.Name, "the row the first holds is skipped");

    await first.RollbackTransactionAsync();
    await using var third = NewSession();
    await third.BeginTransactionAsync();
    var again = (await TakeNextAsync(third, tag)).Single();
    Assert.AreEqual($"{tag}-1", again.Name, "released, the first row is free again");
    await second.RollbackTransactionAsync();
    await third.RollbackTransactionAsync();
  }

  /// <summary>A document with its lines, saved and let go of — only its key comes back.</summary>
  /// <summary>Changes a document's name and total in a session of its own, as another user would.</summary>
  private async Task ChangeDocumentAsync(Guid document, string name, int total) {
    await using var other = NewSession();
    var theirs = await other.GetEntityAsync<Dml.IDocument>(document);
    theirs.Name = name;
    theirs.Total = total;
    await other.SaveChangesAsync();
  }

  private async Task<Guid> NewDocumentAsync(string name, int lines) {
    await using var session = NewSession();
    var document = session.NewEntity<Dml.IDocument>();
    document.Name = name;
    for (var i = 0; i < lines; i++) {
      var line = session.NewEntity<Dml.IDocLine>();
      line.Document = document;
      line.Name = "L" + i;
    }
    await session.SaveChangesAsync();
    return document.Id;
  }
}
