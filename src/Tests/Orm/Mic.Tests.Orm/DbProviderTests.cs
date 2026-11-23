using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sqlite;
using Mic.Orm.Db.SqlServer;
using Mic.Orm.Logging;
using Microsoft.Data.Sqlite;

namespace Mic.Tests.Orm;

// A provider DECLARES; the base gates its own calls on what was declared. These tests are about the shape of that
// bargain — that a vendor difference is data rather than an override with logic in it.
[TestClass]
public class DbProviderTests {
  private static readonly PostgresProvider Postgres = new();

  // ----- what one provider DECLARES, and how it is asked ----------------------------------------------------------------
  //
  // Seven methods over one object with no state to disturb.
  [TestMethod]
  public void Provider_IsForOneServerAndDeclaresItsFeatures() {
    Assert.AreEqual(DbServerKind.Postgres, Postgres.ServerKind);

    // `Supports` answers for one feature or for several at once.
    Assert.IsTrue(Postgres.Supports(DbFeatures.Schemas));
    Assert.IsTrue(Postgres.Supports(DbFeatures.Schemas | DbFeatures.Sequences));
    Assert.IsFalse(Postgres.Supports(DbFeatures.Schemas | DbFeatures.ClusteredStorage));

    Assert.IsTrue(Postgres.Supports(DbFeatures.ReferentialConstraints));
    Assert.IsTrue(Postgres.Supports(DbFeatures.FilterInIndexes));       // partial indexes
    Assert.IsTrue(Postgres.Supports(DbFeatures.DeferredConstraintCheck));

    // ----- and its ABSENCES are facts, not omissions ------------------------------------------------------------------
    //
    // It does not index foreign keys for you — which is why the flag exists at all, since InnoDB does and generation
    // would otherwise emit a redundant index that introspection then reports as a difference forever. And it
    // REGENERATES a view definition and strips comments, so a definition hash cannot ride inside the SQL.
    Assert.IsFalse(Postgres.Supports(DbFeatures.ForeignKeysAutoIndexed));
    Assert.IsFalse(Postgres.Supports(DbFeatures.ServerPreservesComments));

    // ----- CLUSTERED STORAGE and a CLUSTERING MARKER are different capabilities -----------------------------------------
    //
    // PostgreSQL has no index-organized tables — an index is never the table, so every table is a heap. But it CAN
    // designate a clustering index and reorder to match once (CLUSTER … USING …, remembered in indisclustered).
    // Splitting them is what lets introspection read that marker and put it somewhere, instead of the round trip losing
    // it. SQL Server is the other way round: the storage IS clustered, and there is no separate marker to designate.
    Assert.IsFalse(Postgres.Supports(DbFeatures.ClusteredStorage));
    Assert.IsTrue(Postgres.Supports(DbFeatures.ClusteringMarker));
    Assert.IsTrue(Postgres.Supports(DbFeatures.HeapTables));

    var sqlServer = new ToyProvider();
    Assert.IsTrue(sqlServer.Supports(DbFeatures.ClusteredStorage));
    Assert.IsFalse(sqlServer.Supports(DbFeatures.ClusteringMarker));

    // ----- while what is not BOOLEAN is a setting rather than a flag ------------------------------------------------------
    //
    // Identifier folding is three-valued; forcing it into a flag is how a descriptor starts lying.
    Assert.AreEqual(DbIdentifierCase.Lower, Postgres.IdentifierCase);
    Assert.AreEqual("public", Postgres.DefaultSchema);
    Assert.AreEqual(63, Postgres.MaxNameLength);        // and it truncates SILENTLY, which is why the number matters

    // ----- and the type registry is built ONCE, on demand -------------------------------------------------------------------
    //
    // On first use rather than in the constructor, so a derived provider is fully constructed before it is asked to
    // register anything.
    Assert.AreSame(Postgres.TypeRegistry, Postgres.TypeRegistry);
    Assert.IsTrue(Postgres.TypeRegistry.Types.Count > 10);
  }

  // ----- what writing a provider actually costs -------------------------------------------------------------------

  /// <summary>A second "vendor" that differs from PostgreSQL in every declared way — the whole of it is data.</summary>
  private sealed class ToyProvider : ModelOnlyProvider {
    public ToyProvider() : base(DbServerKind.MsSql, BuildTypeRegistry()) { }

    public override DbFeatures Features =>
      DbFeatures.Schemas | DbFeatures.ReferentialConstraints | DbFeatures.ClusteredStorage
      | DbFeatures.ServerPreservesComments | DbFeatures.ForeignKeyMustTargetPrimaryKey;

    public override DbIdentifierCase IdentifierCase => DbIdentifierCase.Preserve;
    public override string DefaultSchema => "dbo";

    private static DbTypeRegistry BuildTypeRegistry() {
      var registry = new DbTypeRegistry();
      registry.Add("nvarchar", typeof(string), DbTypeFlags.TakesSize, specialType: DbSpecialType.String);
      registry.Add("uniqueidentifier", typeof(Guid));
      registry.Add("varbinary", typeof(byte[]), DbTypeFlags.TakesSize, specialType: DbSpecialType.Binary);

      // Superseded by their own vendor. Registered so they can be NAMED and so a real schema containing them can be
      // read — never so that anything lands on them by default.
      registry.Add("text", typeof(string), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);
      registry.Add("ntext", typeof(string), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);
      registry.Add("image", typeof(byte[]), DbTypeFlags.Unlimited | DbTypeFlags.Legacy);
      return registry;
    }
  }

  [TestMethod]
  public void ALegacyTypeIsReachableButNeverChosen() {
    // SQL Server's text/ntext/image are deprecated by Microsoft. They have to be resolvable — a DbTypeSpec may name
    // one, and db-first introspection will meet them in real schemas — without anything mapping onto them by default.
    var toy = new ToyProvider();

    Assert.IsTrue(toy.TypeRegistry.FindByName("text")!.IsLegacy);
    Assert.AreEqual("text", toy.TypeRegistry.ParseSpec("text", out var error)!.TypeSpec);
    Assert.IsNull(error);

    // ... but the modern types are what the semantic categories point at, so nothing lands on a legacy type.
    Assert.AreEqual("nvarchar", toy.TypeRegistry.FindSpecial(DbSpecialType.String)!.Name);
    Assert.AreEqual("varbinary", toy.TypeRegistry.FindSpecial(DbSpecialType.Binary)!.Name);

    // ----- and LEGACY is a per-VENDOR judgement ------------------------------------------------------------------------
    //
    // The same spelling, opposite verdicts: PostgreSQL's `text` is its RECOMMENDED unlimited string type, while SQL
    // Server's is deprecated. Which is why the flag lives on the registration rather than on the type name.
    Assert.IsFalse(Postgres.TypeRegistry.FindByName("text")!.IsLegacy);

    // ----- so marking one legacy AND semantic at once is refused where the mistake is made -------------------------------
    //
    // A legacy type must never be what the base reaches for.
    Assert.ThrowsExactly<ArgumentException>(() =>
      new DbTypeRegistry().Add("text", typeof(string),
        DbTypeFlags.Unlimited | DbTypeFlags.Legacy, specialType: DbSpecialType.StringUnlimited));
  }

  [TestMethod]
  public void AProviderIsDeclarationsAndATypeTable() {
    var toy = new ToyProvider();

    Assert.IsTrue(toy.Supports(DbFeatures.ClusteredStorage));
    Assert.IsFalse(toy.Supports(DbFeatures.FilterInIndexes));
    Assert.AreEqual("dbo", toy.DefaultSchema);
    Assert.AreEqual(DbIdentifierCase.Preserve, toy.IdentifierCase);

    // The same CLR types, different server types — and nothing was overridden to say so.
    Assert.AreEqual("nvarchar(50)", toy.TypeRegistry.Apply(toy.TypeRegistry.FindByName("nvarchar")!, 50).TypeSpec);
    Assert.AreEqual("uniqueidentifier", toy.TypeRegistry.Apply(toy.TypeRegistry.FindByName("uniqueidentifier")!).TypeSpec);

    // And the semantic layer answers for it without the base knowing either name.
    Assert.AreEqual("nvarchar", toy.TypeRegistry.FindSpecial(DbSpecialType.String)!.Name);
    Assert.AreEqual("varchar", Postgres.TypeRegistry.FindSpecial(DbSpecialType.String)!.Name);
  }

  // ----- SQLite's foreign keys are on, whatever the connection string says --------------------------------------
  //
  // SQLite enforces foreign keys per connection and off by default. The provider says Foreign Keys=True where the
  // string does not, and leaves an explicit False alone.
  [TestMethod]
  public void SqliteConnection_EnforcesForeignKeysUnlessToldNot() {
    var provider = new SqliteProvider();
    var plain = (SqliteConnection)provider.CreateConnection("Data Source=:memory:");
    Assert.AreEqual(true, new SqliteConnectionStringBuilder(plain.ConnectionString).ForeignKeys);

    var off = (SqliteConnection)provider.CreateConnection("Data Source=:memory:;Foreign Keys=False");
    Assert.AreEqual(false, new SqliteConnectionStringBuilder(off.ConnectionString).ForeignKeys);
  }

  // ----- a deadlocked schema read is retried ----------------------------------------------------------------------
  //
  // Reading a catalog and changing a schema collide on a server whose readers take locks, and two instances starting
  // together is enough to arrange it: one holds the upgrade lock and applies DDL, the other reads. The upgrade reads
  // the schema BEFORE it takes that lock — deliberately, so a run that only reports needs nothing from anybody — so
  // nothing sequences the two.
  //
  // What follows is the answer in two halves: the reader volunteers to be the victim, and the read is tried again.

  [TestMethod]
  public void SchemaRead_ThatDeadlocksIsTriedAgain() {
    // ----- the reader volunteers to lose ----------------------------------------------------------------------------
    //
    // ONLY SQL SERVER SAYS ANYTHING, because it is the only one of the five whose readers take locks by default. The
    // others version their reads, so a catalog read does not contend with DDL the same way and there is no victim to
    // choose. Snapshot isolation would not help even on SQL Server: it versions DATA, and a schema change has no
    // older version to keep.
    Assert.AreEqual("SET DEADLOCK_PRIORITY LOW", new SqlServerProvider().SqlDialect.SchemaConnectionSetup);
    Assert.IsNull(Postgres.SqlDialect.SchemaConnectionSetup);
    // ----- and the read is tried again ------------------------------------------------------------------------------
    var settings = new DbSettings(new ToyProvider(), "not-a-real-connection");

    // Deadlocking twice, then succeeding: the caller sees a model, not a failure.
    var recovers = new FlakyLoader(settings, failuresBeforeSucceeding: 2);
    var model = recovers.LoadModelAsync().GetAwaiter().GetResult();
    Assert.AreEqual(3, recovers.Attempts, "it should have taken three attempts");

    // AND SAYS SO. A retry that left no trace would make an intermittent deadlock invisible exactly when somebody is
    // trying to work out why startup is slow.
    Assert.IsTrue(model.BuildReport.Messages.Any(m => m.Severity == Severity.Warning && m.Message.Contains("deadlock")),
      "the retry was not reported: " + model.BuildReport.Format());

    // ----- but not forever, and not for anything else ---------------------------------------------------------------
    var hopeless = new FlakyLoader(settings, failuresBeforeSucceeding: 99);
    var gaveUp = Assert.ThrowsExactly<DataAccessException>(() =>
      hopeless.LoadModelAsync().GetAwaiter().GetResult());
    Assert.AreEqual(DataAccessException.SubTypeDeadlock, gaveUp.SubType);
    Assert.AreEqual(DbModelLoader.DeadlockRetries, hopeless.Attempts, "it should stop at the bound");

    // A failure that is NOT a deadlock is not retried at all — retrying it would turn one honest error into three.
    var broken = new FlakyLoader(settings, failuresBeforeSucceeding: 1,
      subType: DataAccessException.SubTypeConnectionFailed);
    Assert.ThrowsExactly<DataAccessException>(() => broken.LoadModelAsync().GetAwaiter().GetResult());
    Assert.AreEqual(1, broken.Attempts, "a non-deadlock failure should be handed straight to the caller");
  }

  /// <summary>A loader that fails a chosen number of times, in a chosen way, before it works.</summary>
  /// <remarks>It overrides the ONE ATTEMPT rather than any query, which is the seam the retry was built around: the
  /// real load builds its model in pieces and starts a fresh one each time, so what is retried is the whole thing.
  /// Overriding here tests exactly that boundary and needs no database.</remarks>
  private sealed class FlakyLoader(DbSettings settings, int failuresBeforeSucceeding,
        string subType = DataAccessException.SubTypeDeadlock)
      : DbModelLoader(settings) {
    public int Attempts { get; private set; }

    protected override Task<DbModel> LoadModelOnceAsync(IReadOnlyList<string>? schemas, DbModelParts parts,
        CancellationToken cancellation) {
      Attempts++;
      if (Attempts <= failuresBeforeSucceeding)
        throw new DataAccessException("deadlocked, as arranged") { SubType = subType };
      return Task.FromResult(new DbModel(settings.DatabaseAlias, settings.Provider));
    }
  }
}
