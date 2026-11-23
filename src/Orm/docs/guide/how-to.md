# How to…

Short answers to common questions. Each heading is the question.

## How do I define an index?

On one member, put `[Index]` on the member. On several, put `[Index("A,B")]` on the interface. `-` before a
member makes it descending.

```csharp
[Entity, Index("Customer,-PlacedOn")]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ICustomer Customer { get; set; }
  [Utc] DateTime PlacedOn { get; set; }
  [Index] OrderStatus Status { get; set; }
}
```

`[Unique]` is a unique index. `[Index(Filter = "\"Status\" = 0")]` is a filtered index.
`[Index(IncludeMembers = "Total")]` adds covering columns.

## How do I make a member nullable or required?

Use the C# type. `string?` and `int?` are nullable columns. `string` and `int` are `NOT NULL`. A reference
`ICustomer?` is an optional foreign key; `ICustomer` is required. In an assembly compiled without nullable
reference types, mark nullable strings and references with `[Nullable]`.

## How do I define a composite primary key?

```csharp
[Entity, PrimaryKey("Order,LineNo")]
public interface IOrderLine {
  IOrder Order { get; set; }
  int LineNo { get; set; }
}
```

Read it with `session.GetEntityAsync<IOrderLine>(session.CreatePrimaryKey<IOrderLine>(orderId, 3))`.

## How do I use an identity column instead of a Guid?

```csharp
[Entity]
public interface ICustomer {
  [PrimaryKey, Identity] int Id { get; set; }
}
```

The value is assigned by the database at the save and read back into the entity.

## How do I rename an entity or a member without losing data?

Add `[OldNames]` with the former name. The upgrade renames the table or column.

```csharp
[Entity, OldNames("Client")]
public interface ICustomer {
  [Size(100), OldNames("FullName")] string Name { get; set; }
}
```

The attribute can stay in place after the rename.

## How do I store an enum?

Declare the member with the enum type. The column is the enum's underlying integer. A `[Flags]` enum is stored
the same way.

## How do I store a decimal with a given precision?

```csharp
[Column(Precision = 12, Scale = 2)] decimal Total { get; set; }
```

Without the attribute the column is `decimal(18, 4)`.

## How do I set a database default for a column?

```csharp
[Column(Default = "0")] int Priority { get; set; }
```

`Default` is the SQL text of the default.

## How do I store an object as JSON?

```csharp
public record Dimensions(decimal Width, decimal Height, decimal Depth);

[Json] Dimensions? Size { get; set; }
```

To filter on a value inside the document, add a `[FromJson("Size", "$.Width")] decimal? Width { get; }`
member and query it. See [JSON](json.md).

## How do I load a reference or a list with the query?

```csharp
var orders = await session.EntitySet<IOrder>()
  .Include(o => o.Customer)
  .Include(o => o.Lines)
  .ToListAsync();
```

Without `Include`, the member loads on first read, for all entities the query returned at once.

## How do I await the load of a reference or a list?

Await the member. Import `Mic.Orm.Awaiting` in the file.

```csharp
using Mic.Orm.Awaiting;

var customer = await order.Customer;   // loads the customer if it is not loaded
var lines = await order.Lines;         // loads the list if it is not loaded
```

Reading `order.Customer.Name` without the await loads the customer too, on the calling thread. See
[Reading related entities](linq-queries.md#reading-related-entities-lazy-loading).

## How do I keep a large column out of a read?

Mark the member `[OnDemand]`. A read of the entity leaves the column out; the member loads when it is first
read, or with the query that includes it.

```csharp
[OnDemand, Unlimited] string Body { get; set; }

var articles = await session.EntitySet<IArticle>().Include(a => a.Body).ToListAsync();
```

See [On-demand members](on-demand-members.md).

## How do I find the places near a point?

Declare the place as a `GeoPoint` member with `[Index]`, and filter or order on
`GeoFunctions.Distance(s.Location, here)`, in metres. See [Geographic points](geo-points.md).

## How do I find the rows nearest to an embedding?

Declare the embedding as a `float[]` member with `[Vector(n)]` and `[Index]`, order the query by
`VectorFunctions.Distance(p.Embedding, question)` and take a page. See [Vector search](vector-search.md).

## How do I run a query over a list of ids?

```csharp
var orders = await session.EntitySet<IOrder>().Where(o => ids.Contains(o.Id)).ToListAsync();
```

`ids` travels as one parameter whatever its length: an array on PostgreSQL; on the other servers the rows of one
JSON document past `SqlDialect.MaxLiteralListItems` values, 100, and a temp table the ORM creates and drops past
`DbSettings.RowsTempTableThreshold` characters of JSON. A shorter list is written into the statement as literals.

## How do I update many rows without loading them?

```csharp
var old = session.EntitySet<IOrder>().Where(o => o.PlacedOn < cutoff);
var count = await session.ExecuteUpdateAsync<IOrder>(old.Select(o => new { Status = OrderStatus.Archived }));
```

`session.ExecuteDeleteAsync<IOrder>(old)` deletes the rows a query finds. Both have a synchronous form,
`ExecuteUpdate<T>` and `ExecuteDelete<T>`.

## How do I open a session?

`app.OpenSession()` for an anonymous user. For a user and the values of a request, make an `OperationContext`
first: `var context = new OperationContext(app, user); context.SetValue("TenantId", tenantId); var session =
context.OpenSession();`. Dispose the context at the end of the operation; it ends the sessions it opened.

## How do I keep one session for a form or a Blazor Server component?

Open it with `EntitySessionOptions.AddThreadSafety`: `context.OpenSession(EntitySessionOptions.AddThreadSafety)`.
An operation started while another runs then waits its turn. See [One operation at a
time](core-objects.md#one-operation-at-a-time).

## How do I report an error the user can fix?

Raise a `ClientFault` on the context and throw them together:
`context.AddClientFault(new ClientFault(ClientFaultCodes.InvalidValue, "Not enough stock.", "Quantity"))`, then
`context.ThrowValidation()`. The ORM's own validation faults, a missing value or an overlong string, come out of
`SaveChangesAsync` as the same `ClientFaultException`.

## How do I shut the application down?

`await app.ShutdownAsync()`, or `await using var app = new ShopApp();` at the start. It raises
`app.Events.Shutdown`, cancels `app.ShutdownToken` and flushes the log. See [Application
shutdown](startup.md#application-shutdown).

## How do I run a transaction across several saves?

```csharp
await session.BeginTransactionAsync();
try {
  // reads, changes, SaveChangesAsync calls
  await session.CommitTransactionAsync();
} catch {
  await session.RollbackTransactionAsync();
  throw;
}
```

## How do I lock a row for update?

`await session.GetEntityAsync<IStockItem>(id, LoadFlags.WriteLock)` inside a transaction. For a query,
`session.EntitySet<IStockItem>(QueryOptions.LockForUpdate)`. See [Locking records](locking.md).

## How do I take the next job from a queue table?

Inside a transaction, `session.EntitySet<IScheduledJob>(QueryOptions.SkipLocked).Where(…).OrderBy(…).Take(1)`:
the records another worker holds are skipped, not waited for. See [Locking records](locking.md#a-table-as-a-queue).

## How do I run raw SQL?

```csharp
var rows = await session.RunSqlQueryAsync("SELECT \"Id\", \"Name\" FROM shop.\"Customer\" WHERE \"Name\" LIKE @prefix", ("prefix", "A%"));
var count = await session.RunSqlScalarAsync<long>("SELECT COUNT(*) FROM shop.\"Customer\"");
var changed = await session.RunSqlNonQueryAsync("DELETE FROM shop.\"Log\" WHERE \"CreatedOn\" < @cutoff", ("cutoff", cutoff));
```

## How do I see the SQL the ORM runs?

`session.GetLastCommands()` after a request holds the commands with their SQL, parameters, row counts and timings.
For a log, `app.SetLogFilePath("shop.log")` writes every command during development; see [Logging](logging.md)
for the modes and for logging in production.

## How do I keep the connection string out of the config file?

Write `env:SHOP_CONN` or `file:../secrets/shop.txt` where the connection string would go and resolve it with
`ConnectionStringHelper.Resolve(value)`. The template's host and the `mic-db` tool do this already.

## How do I put the model in a schema?

`AddModule(new ShopModule(), "shop")` puts the module's tables in schema `shop`.
`EntityAppSettings.DefaultSchema` sets the schema of modules that name none, including the ORM's `DbInfo`
module.

## How do I run tests on SQLite?

Reference `Mic.Orm.Db.Sqlite` and connect with `new DbSettings(new SqliteProvider(), "Data Source=test.db")`. The
same model runs; the provider turns foreign-key enforcement on. SQLite has no schemas; set
`Options = DbOptions.Default | DbOptions.AddSchemaToTableNames` on its `DbSettings` when two
modules have tables of the same name.

## How do I cache a reference table in memory?

In the application's constructor: `SetCached(CacheType.Server, [typeof(ICountry)], TimeSpan.FromMinutes(10))`.
Reads by key and queries over the cached entity alone are answered from memory. A write through a session
drops the cache; the next read refills it. See [Data cache](data-cache.md).

## How do I add a tenant column to every entity?

Register `ITenant`, inherit `ITenantBound` on the entities and add `new MultiTenantExtension()` to the
application; for an existing database, `new MultiTenantExtension("CompanyCode")` instead.
Set the tenant on the context: `context.SetValue("TenantId", tenantId)`. Every query filters by it and every save
stamps it. A job that spans tenants calls `session.DisableExtension<MultiTenantExtension>()`.
See [Multi-tenancy extension](multi-tenancy.md).

## How do I mark rows deleted instead of deleting them?

Declare the entities with `ISoftDeleteEntity` and add `new SoftDeleteExtension()` to the application; for an
existing database with an `IsDeleted` bit, `new SoftDeleteExtension("IsDeleted")` instead.
`DeleteEntity` then sets the flag at the save, and queries skip flagged rows. A session that called
`DisableExtension<SoftDeleteExtension>()` reads them too, and its deletes are deletes; setting `DeletedOn` to null
and saving restores a row. See [Soft delete extension](soft-delete.md).

## How do I stop the application from changing a production database?

Set `InstanceType` to `Production` in the database's `DbInfo` row. The default `DbUpgradeMode.NonProductionOnly`
then reports differences and changes nothing. Upgrade production with the script from `mic-db upgrade`.

## How do I keep an index the model does not declare?

Name it in `DbSettings.IgnoreDbObjects`, by its path or a pattern: `"*.Order.IX_Report_*"`. The upgrade then
leaves it alone, and its report says nothing of it. The same list keeps a column, a sequence or a constraint. See
[Maintaining the database schema](database-model.md).

## How do I add an index to a large table?

An index on a large table takes long to build, and a plain `CREATE INDEX` holds off writes to the table until it
ends. Upgrade such a database with the script from `mic-db upgrade`, and before running it change the index's
statement to the server's online form:

| Server | Online form |
|---|---|
| PostgreSQL | `CREATE INDEX CONCURRENTLY …` |
| SQL Server | `… WITH (ONLINE = ON)`, on Enterprise edition and Azure SQL |
| MySQL | none needed: InnoDB builds an index without holding off writes; `… ALGORITHM=INPLACE LOCK=NONE` makes the server refuse where it cannot |
| Oracle | `… ONLINE`, on Enterprise Edition |
| SQLite | none |

The running version of the application keeps writing to the table during the build. The upgrade compares an
index by its name, columns and options, so the new version's connect finds the index and changes nothing.

On PostgreSQL a concurrent build that fails leaves an invalid index under the name, and the upgrade reads it as
present. Drop it and run the statement again.

## Which version of `mic-db` does my application need?

The one with the same major and minor version as the application's `Mic.Orm`, at the application's patch or a
later one. A local tool manifest keeps the two together in a repository. See
[Generating the upgrade script](tool-upgrade.md).

## How do I generate entities from an existing database?

`mic-db entities config.json`. See [Generating entities from a database](tool-entities.md).
