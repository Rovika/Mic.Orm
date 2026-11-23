using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// ON-DEMAND MEMBERS: a column marked [OnDemand] is left out of every read that answers the entity, and the record
// does not hold it. A query that names the member reads it; a save writes what was assigned and nothing else. A read
// of the member loads it, for every tracked record of the entity that lacks it. An include and LoadMemberAsync ask
// for it ahead of the read.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task OnDemandMember_IsLeftOutOfAReadOfTheEntity() {
    var entity = App.Model!.FindEntity("Article")!;
    var body = entity.GetMember("Body");
    var summary = entity.GetMember("Summary");
    var (bodyColumn, summaryColumn) = (Db.Quote("Body"), Db.Quote("Summary"));

    // ----- a new entity holds every member, and the insert writes them all ------------------------------------------
    Guid id;
    await using (var writer = NewSession()) {
      var written = writer.NewEntity<Dml.IArticle>();
      written.Title = "On demand";
      written.Body = "the long text";
      written.Summary = "short";
      await writer.SaveChangesAsync();
      id = written.Id;
      Assert.IsTrue(((EntityBase)written).Record.Loaded.IsSet(body.DataIndex), "what was written is held");
    }
    Assert.AreEqual("the long text", await ScalarAsync<string>("Article", "Body", id));

    // ----- a query of the entity, and a read by key ------------------------------------------------------------------
    await using (var session = NewSession()) {
      var article = (await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id).ToListAsync()).Single();
      AssertLeftOut(session, article);

      // ----- a query that names the member reads it ------------------------------------------------------------------
      var texts = await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id).Select(a => a.Body).ToListAsync();
      Assert.AreEqual("the long text", texts.Single());
      var titled = await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id && a.Summary == "short")
        .Select(a => new { a.Title, a.Summary }).ToListAsync();
      Assert.AreEqual("short", titled.Single().Summary);

      // ----- named over a page, it is read through the derived table, and the rows still come without it -----------
      var paged = await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id).OrderBy(a => a.Title).Take(5)
        .Where(a => a.Summary == "short").ToListAsync();
      Assert.AreSame(article, paged.Single());
      Assert.IsFalse(((EntityBase)article).Record.Loaded.IsSet(summary.DataIndex), "a condition is not a read");

      // ----- an update names what was assigned; a required member that is not held is not judged -------------------
      article.Title = "Renamed";
      await session.SaveChangesAsync();
      var update = session.LastCommands[^1].Sql;
      Assert.IsFalse(update.Contains(bodyColumn) || update.Contains(summaryColumn), update);
    }
    Assert.AreEqual("the long text", await ScalarAsync<string>("Article", "Body", id), "the text stands");

    await using (var session = NewSession()) {
      var article = await session.GetEntityAsync<Dml.IArticle>(id);
      AssertLeftOut(session, article);
      Assert.AreEqual("Renamed", article.Title);

      // ----- a member assigned without a load is written, a null as any other value --------------------------------
      article.Summary = null;
      article.Body = "another text";
      await session.SaveChangesAsync();
      Assert.IsTrue(((EntityBase)article).Record.Loaded.IsSet(body.DataIndex), "what was written is held");
    }
    Assert.AreEqual("another text", await ScalarAsync<string>("Article", "Body", id));
    Assert.IsNull(await ScalarAsync<string>("Article", "Summary", id));

    // ----- an entity in a slot of a projection, and a stub filled from its row -------------------------------------
    await using (var session = NewSession()) {
      var rows = await session.EntitySet<Dml.IArticle>().Where(a => a.Id == id)
        .Select(a => new { Article = a, a.Rank }).ToListAsync();
      AssertLeftOut(session, rows.Single().Article);
    }
    await using (var session = NewSession()) {
      var stub = session.GetEntity<Dml.IArticle>(id, LoadFlags.Stub);
      Assert.AreEqual("Renamed", stub.Title);
      AssertLeftOut(session, stub);
    }

    // ----- local functions
    void AssertLeftOut(IEntitySession session, Dml.IArticle article) {
      var select = ((EntitySession)session).LastCommands[^1].Sql;
      Assert.IsFalse(select.Contains(bodyColumn) || select.Contains(summaryColumn), select);
      var record = ((EntityBase)article).Record;
      Assert.AreEqual(EntityStatus.Loaded, record.Status);
      Assert.IsFalse(record.Loaded.IsSet(body.DataIndex), "the record does not hold the text");
      Assert.IsFalse(record.Loaded.IsSet(summary.DataIndex), "nor the summary");
    }
  }

  [TestMethod]
  public async Task OnDemandMember_LoadsOnARead() {
    var entity = App.Model!.FindEntity("Article")!;
    var (body, summary) = (entity.GetMember("Body"), entity.GetMember("Summary"));
    var tag = "load-" + Guid.NewGuid().ToString("N")[..6];
    var ids = new List<Guid>();
    await using (var writer = NewSession()) {
      for (var i = 0; i < 3; i++) {
        var written = writer.NewEntity<Dml.IArticle>();
        (written.Title, written.Rank, written.Body, written.Summary) = (tag, i, "text " + i, "summary " + i);
        if (i == 0)
          (written.Notes, written.Picture) = (new Dml.ArticleNotes { Editor = "Ann", Revision = 1 }, [1, 2, 3, 250]);
        ids.Add(written.Id);
      }
      await writer.SaveChangesAsync();
    }

    // ----- one read loads the member for every record that lacks it, in one statement ----------------------------
    await using (var session = NewSession()) {
      var articles = await ReadAsync(session);

      Assert.AreEqual("text 0", articles[0].Body);

      // The session keeps the commands of its last request: one command, and the same one after the other reads.
      var load = session.LastCommands.Single();
      StringAssert.Contains(load.Sql, Db.Quote("Body"));
      Assert.IsFalse(load.Sql.Contains(Db.Quote("Summary")) || load.Sql.Contains(Db.Quote("Title")), load.Sql);
      Assert.AreEqual("text 1", articles[1].Body);
      Assert.AreEqual("text 2", articles[2].Body);
      Assert.AreSame(load, session.LastCommands.Single(), "the others came with the first");
      foreach (var article in articles) {
        var record = ((EntityBase)article).Record;
        Assert.AreEqual(EntityStatus.Loaded, record.Status);
        Assert.IsTrue(record.Modified.IsEmpty, "being loaded is not a change to save");
        Assert.IsFalse(record.Loaded.IsSet(summary.DataIndex), "a member nobody read stays out");
      }
      Assert.AreEqual("summary 2", articles[2].Summary);
      Assert.AreNotSame(load, session.LastCommands.Single(), "the other member has a load of its own");

      // ----- a binary member, the same way ---------------------------------------------------------------------------
      CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 250 }, articles[0].Picture);
      Assert.IsNull(articles[1].Picture);

      // ----- a JSON document: read, changed in place, and saved against the text the load brought ----------------
      var notes = articles[0].Notes!;
      Assert.AreEqual("Ann", notes.Editor);
      Assert.IsNull(articles[1].Notes);
      notes.Revision = 2;
      EntityHelper.MarkModified(articles[0], a => a.Notes);
      await session.SaveChangesAsync();
    }
    await using (var session = NewSession()) {
      var stored = (await session.EntitySet<Dml.IArticle>().Where(a => a.Id == ids[0]).Select(a => a.Notes)
        .ToListAsync()).Single()!;
      Assert.AreEqual(("Ann", 2), (stored.Editor, stored.Revision));
    }

    // ----- a member assigned before the load keeps what was assigned -------------------------------------------------
    await using (var session = NewSession()) {
      var articles = await ReadAsync(session);
      articles[1].Body = "mine";

      Assert.AreEqual("text 0", articles[0].Body);

      Assert.AreEqual("mine", articles[1].Body);
      Assert.IsTrue(((EntityBase)articles[1]).Record.Modified.IsSet(body.DataIndex));
      Assert.IsTrue(((EntityBase)articles[2]).Record.Loaded.IsSet(body.DataIndex));
    }

    // ----- with the smart load off, the record asked alone ---------------------------------------------------------
    await using (var session = NewSession()) {
      session.Options = EntitySessionOptions.DisableSmartLoad;
      var articles = await ReadAsync(session);

      Assert.AreEqual("text 0", articles[0].Body);

      Assert.IsFalse(((EntityBase)articles[1]).Record.Loaded.IsSet(body.DataIndex));
    }

    // ----- a stub: the member alone, then the row, which leaves the member as it is ------------------------------
    await using (var session = NewSession()) {
      var stub = session.GetEntity<Dml.IArticle>(ids[2], LoadFlags.Stub);
      Assert.AreEqual("text 2", stub.Body);
      Assert.AreEqual(EntityStatus.Stub, ((EntityBase)stub).Record.Status, "the row was not read for it");
      Assert.AreEqual(2, stub.Rank);
      Assert.AreEqual("text 2", stub.Body);

      // ----- no row: the member reads as nothing, and is asked for once ------------------------------------------
      var missing = session.GetEntity<Dml.IArticle>(Guid.NewGuid(), LoadFlags.Stub);
      Assert.IsNull(missing.Summary);
      var asked = session.LastCommands.Single();
      Assert.IsNull(missing.Summary);
      Assert.AreSame(asked, session.LastCommands.Single());
    }

    // ----- local functions
    async Task<IReadOnlyList<Dml.IArticle>> ReadAsync(EntitySession session) =>
      await session.EntitySet<Dml.IArticle>().Where(a => a.Title == tag).OrderBy(a => a.Rank).ToListAsync();
  }

  [TestMethod]
  public async Task OnDemandMember_IsAskedForAheadOfTheRead() {
    var entity = App.Model!.FindEntity("Article")!;
    var (body, summary) = (entity.GetMember("Body"), entity.GetMember("Summary"));
    var tag = "ask-" + Guid.NewGuid().ToString("N")[..6];
    await using (var writer = NewSession()) {
      Dml.IArticle? next = null;
      for (var i = 2; i >= 0; i--) {
        var written = writer.NewEntity<Dml.IArticle>();
        (written.Title, written.Rank, written.Body, written.Summary) = (tag, i, "text " + i, "summary " + i);
        written.Sequel = next;
        next = written;
      }
      await writer.SaveChangesAsync();
    }

    // ----- an include: a second statement by the keys of the query's rows -------------------------------------------
    await using (var session = NewSession()) {
      var articles = await Ordered(session).Include(a => a.Body).ToListAsync();
      var load = session.LastCommands.Single();
      StringAssert.Contains(load.Sql, Db.Quote("Body"));
      Assert.IsTrue(articles.All(a => Holds(a, body) && !Holds(a, summary)), "the text came, the summary did not");
      Assert.AreEqual("text 1", articles[1].Body);
      Assert.AreSame(load, session.LastCommands.Single(), "the read runs nothing");
    }

    // ----- several members, and the same by name -----------------------------------------------------------------
    await using (var session = NewSession()) {
      var articles = await Ordered(session).Include(a => new { a.Body, a.Summary }).ToListAsync();
      Assert.IsTrue(articles.All(a => Holds(a, body) && Holds(a, summary)));
    }
    await using (var session = NewSession()) {
      var byName = session.From("Article").Where(row => row.Member("Title").Equal(QueryMaker.Constant(tag)))
        .Include("Summary");
      var records = await session.ExecuteQueryAsync(byName);
      Assert.AreEqual(3, records.Count);
      Assert.IsTrue(records.All(r => r.Loaded.IsSet(summary.DataIndex) && !r.Loaded.IsSet(body.DataIndex)));
    }

    // ----- at the end of a chain: the sequel's text, and not the article's own ------------------------------------
    await using (var session = NewSession()) {
      var first = (await Tagged(session).Where(a => a.Rank == 0).Include(a => a.Sequel!.Body).ToListAsync()).Single();
      Assert.IsFalse(Holds(first, body));
      var sequel = first.Sequel!;
      Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)sequel).Record.Status, "the reference was loaded on the way");
      Assert.IsTrue(Holds(sequel, body));
      Assert.AreEqual("text 1", sequel.Body);
    }

    // ----- an include the session adds to every query of the entity ------------------------------------------------
    await using (var session = NewSession()) {
      session.AddInclude<Dml.IArticle>(a => a.Summary);
      var articles = await Tagged(session).ToListAsync();
      Assert.IsTrue(articles.All(a => Holds(a, summary) && !Holds(a, body)));
    }

    // ----- an entity in hand -----------------------------------------------------------------------------------------
    await using (var session = NewSession()) {
      var articles = await Ordered(session).ToListAsync();

      await session.LoadMemberAsync(articles[0], a => a.Summary);

      var load = session.LastCommands.Single();
      StringAssert.Contains(load.Sql, Db.Quote("Summary"));
      Assert.IsTrue(articles.All(a => Holds(a, summary)), "the others the session tracks came with it");
      await session.LoadMemberAsync(articles[1], a => a.Summary);
      Assert.AreSame(load, session.LastCommands.Single(), "an entity that holds the member runs nothing");
      Assert.AreEqual("summary 2", articles[2].Summary);

      // ----- what is refused: a member that is not on demand, by the method and by an include ---------------------
      var notOnDemand = Assert.ThrowsExactly<ArgumentException>(
        () => { _ = session.LoadMemberAsync(articles[0], a => a.Title); });
      StringAssert.Contains(notOnDemand.Message, "'Article.Title' is not an on-demand member");
      var notIncluded = await Assert.ThrowsExactlyAsync<NotSupportedException>(
        () => Tagged(session).Include(a => a.Title).ToListAsync());
      StringAssert.Contains(notIncluded.Message, "'Article.Title' is not a reference, a list or an on-demand member");
    }

    // ----- local functions
    IQueryable<Dml.IArticle> Tagged(EntitySession session) =>
      session.EntitySet<Dml.IArticle>().Where(a => a.Title == tag);

    IQueryable<Dml.IArticle> Ordered(EntitySession session) => Tagged(session).OrderBy(a => a.Rank);

    static bool Holds(Dml.IArticle article, Mic.Orm.Model.EntityMemberInfo member) =>
      ((EntityBase)article).Record.Loaded.IsSet(member.DataIndex);
  }
}
