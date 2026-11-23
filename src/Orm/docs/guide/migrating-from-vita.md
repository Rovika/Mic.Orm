# Migrating from VITA

MIC ORM is the successor of [VITA](https://github.com/rivantsov/vita). VITA established the main design choices:
an entity is an interface and the class is generated; the database follows the model; the session is the unit of
work; LINQ is the query language, translated for five servers; related rows are loaded in batches.

MIC ORM is a complete rewrite. No VITA code is carried over. Each area was built again and extended: the LINQ
engine covers more of the language and composes over projections; includes and the entity cache are part of the
query translation; the providers are declarations over a common base; the upgrade handles views, sequences,
computed columns and table functions; the `mic-db` tool generates entities from a database and the upgrade script
for one.

Compatibility with VITA applications is deliberate. An application written on VITA keeps its entity declarations
and its session code. The application and module setup changes.

## Changes

| VITA | MIC ORM |
|---|---|
| Targets .NET Standard 2.0 | Targets .NET 10. A project on .NET Standard, .NET Framework or an earlier .NET moves to .NET 10 first. |
| Packages `Vita`, `Vita.Data.Postgres`, `Vita.Data.MsSql`, `Vita.Data.MySql`, `Vita.Data.Oracle`, `Vita.Data.SQLite` | `Mic.Orm`, `Mic.Orm.Db.Postgres`, `Mic.Orm.Db.SqlServer`, `Mic.Orm.Db.MySql`, `Mic.Orm.Db.Oracle`, `Mic.Orm.Db.Sqlite` |
| Drivers `PgDbDriver`, `MsSqlDbDriver`, `MySqlDbDriver`, `OracleDbDriver`, `SQLiteDbDriver` | Providers `PostgresProvider`, `SqlServerProvider`, `MySqlProvider`, `OracleProvider`, `SqliteProvider` |
| `EntityArea`: `app.AddArea("dbo")` and `new MyModule(area)` | No areas. `AddModule(module, "schema")` or `AssignSchema` puts a module in a schema; `EntityAppSettings.DefaultSchema` is the default. |
| `EntityModule(EntityArea area, string name, string description, Version version)` | `EntityModule(string name, Version version)` |
| `EntityApp(string appName, string version, string activationLogPath)` | `EntityApp(string name, Version version, EntityAppSettings? settings)` |
| `DbSettings(driver, DbOptions options, connectionString, schemaManagementConnectionString, upgradeMode, upgradeOptions, namingPolicy, dataSourceName)` | `DbSettings(provider, connectionString, schemaConnectionString, databaseAlias)` with `Options`, `UpgradeMode` and the other policies as properties. `DbOptions` keeps `UseRefIntegrity`, `AutoIndexForeignKeys`, `PluralizeTableNames`, `AddSchemaToTableNames`; `IgnoreTableNamesCase`, `ForceArraysAsLiterals`, `UseBatchMode` and `ShareDbModel` are gone. `NamingPolicy` and `DefaultStringSize` are on `EntityAppSettings`. |
| `app.Init()` throws on errors | The same; it returns the build report, and the model stays on `app.Model` for inspection |
| `app.ConnectTo(dbSettings)` | `await app.ConnectToAsync(dbSettings)` returns the upgrade report; `app.ConnectTo(dbSettings)` is kept |
| `app.Shutdown()` | `await app.ShutdownAsync()`, or `await using` on the application; `Shutdown()` is kept; `app.Events.Shutdown` and `app.ShutdownToken` as before. No module `Shutdown` hook. |
| `OperationContext(app, user, webContext, log, connectionMode)` | `OperationContext(app, user, connectionMode)`; `DbConnectionMode` is `ConnectionReuseMode`, `DataSourceName` is `DatabaseAlias`, `Log` is `LogMode` with `Log(message)` |
| `WebCallContext`, `RequestInfo`, `ResponseInfo` | Removed. `OperationContext.HttpContext` carries the host's own request object; `CorrelationId` and `WebRequestId` are set by the host. |
| `context.OpenSecureSession()` | Removed; returns with authorization. |
| `session.CancelChanges()` | Removed. Dispose the context and open another. |
| `EntitySessionOptions`: `DisableLog`, `DisableCache`, `DisableBatchMode`, `DisableSmartLoad` | `DisableSmartLoad`, `DisableBatchMode`. The log is the context's `LogMode`; the cache is skipped per request with `QueryOptions.SkipEntityCache` or `LoadFlags.SkipCache`. |
| `SaveChanges()` writes and keeps tracking | `SaveChanges(SaveOptions)`; `SaveOptions.InsertAndForget` stops tracking |
| VITA `[Validate(typeof(T), "Method")]` | `[Validator(typeof(T), "Method")]` |
| `[DependsOn]`, `[Display]`, `[GrantAccess]`, `[Groups]`, `[PropertyGroup]`, `[PropagateUpdatedOn]`, `[TransactionId]`, `[DiscardOnAbort]` | Not carried over. Authorization and transaction logging are outside the ORM. |
| `module.RegisterMigrations(DbMigrationSet)` | No per-module migrations. A data migration is a statement in the upgrade script. |
| `module.RegisterFunctions(…)` for SQL functions used in LINQ | Removed. |
| `module.Init()`, `AppInitComplete()`, `Shutdown()` | `OnModelConstructed(EntityAppModel model)` on the module and the application. |
| Transaction log, login, authorization and other VITA modules | Not part of MIC ORM. |
| `IAsyncLoad` on an entity, for `await entity` and `await entity.List` | `await` works on every entity and list member in a file with `using Mic.Orm.Awaiting;`. `IAsyncLoad` is gone; remove it from the entity interfaces. |
| `AsyncQueryExtensions` in `Vita.Entities` | `Mic.Linq`, with `SingleAsync`, `AnyAsync`, `AllAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, `AverageAsync` added |
| `session.ExecuteUpdate<T>(query)`, `ExecuteDelete<T>`, `ExecuteInsert<T>`, their `…Async` forms, `ScheduleUpdate<T>(query, CommandSchedule)`, … | The same. `CommandSchedule.TransactionStart` and `TransactionEnd` as in Vita. The same methods exist on the query: `query.ExecuteUpdateAsync<T>()`, `ScheduleUpdate<T>()`, … |
| `session.Select(() => expr)`, `GetSequenceNextValue(SequenceDefinition)` | `Select` is removed; `GetSequenceNextValue(definition)` and `GetSequenceNextValue(name)` stay |
| `session.GetNextTransactionId()`, a `long` | The same, a `Guid`; new after a save has written. |
| `[Auto]` Guid keys from `Guid.NewGuid()`, random | Ordered by time by default; `GuidScheme.V4` keeps them random. See [Guid generation](guid-generation.md). |
| `[HashFor]` on an `int` member, the hash CRC32 | `[HashFor]` on an `int` or a `long` member, the hash FNV-1a. `app.ComputeStableHash(text)` as in VITA, with another result: the stored hashes are recomputed, step 8. |
| `WithElevatedRead`, the transaction tags | Not carried over. |
| The direct database access API | `RunSqlQueryAsync`, `RunSqlScalarAsync`, `RunSqlNonQueryAsync` on the session |

Synchronous `SaveChanges`, `GetEntity` and `ConnectTo` remain; everything that reaches the server also has an
async form, and the async form is the primary one. A session's synchronous member runs the driver's synchronous
calls, as under Vita's `RunMode`; it does not wait on a task.

## New features

* `[Json]`, `[FromJson]`, `MapJsonTypes`: JSON columns with typed documents.
* Views defined by a typed query, by QueryMaker or by SQL, created and maintained by the upgrade; materialized
  views; existing views.
* Table functions, temp tables, sequences registered in the module.
* `Include` on queries; `IQueryable<T>` as an entity member.
* `QueryMaker`: queries built from names.
* Full-text search with `FullText.Matches` and `FullText.Rank`.
* `AppExtension`: `MultiTenantExtension`, `SoftDeleteExtension`, and application-defined ones;
  `session.DisableExtension<TExtension>()` to work without one for the session.
* `QueryOptions.BypassQueryCache` for a query that will not repeat.
* `ReplaceEntity<TPlaceholder, TReplacement>` to extend a module's entity from the application.
* The `mic-db` tool: the upgrade script and entity generation from a database.
* `ConnectionStringHelper.Resolve` with `env:` and `file:` forms.
* A session registers with its context and ends with it; `OperationContext.ConnectionReuseMode` per operation.

## Steps

1. **Replace the packages.** Set the projects' target to `net10.0`. Remove `Vita` and `Vita.Data.*`; add
   `Mic.Orm` to the model project and the provider package to the host.
2. **Build.** Entity interfaces compile as they are. Fix the attribute renames: `[Validate]` to `[Validator]`;
   remove the attributes that were not carried over.
3. **Rewrite the modules.** Drop the `EntityArea` parameter: `base("Name", new Version(1, 0))`. Move
   `RegisterFunctions` and `RegisterMigrations` calls out; migrations become script statements.
4. **Rewrite the application.** `base("Name", new Version(1, 0), new EntityAppSettings { DefaultSchema = "dbo" })`,
   `AddModule(new MyModule(), "schema")` per module. The naming policy goes to `EntityAppSettings`.
5. **Rewrite the startup.** `new DbSettings(new PostgresProvider(), connectionString) { Options = …, UpgradeMode = … }`;
   `app.Init()`; `await app.ConnectToAsync(settings)`. The session code compiles as it is: `app.OpenSession()`,
   `context.OpenSession()`. Replace `CancelChanges` with a new context, and `WebCallContext` with
   `OperationContext.HttpContext`.
6. **Run against a copy of the database.** Run `mic-db upgrade` against the copy and read the script it writes;
   it applies nothing. It lists every difference between the VITA-made schema and what MIC derives. Index
   and key names derived by MIC may differ from VITA's; pin a name with `Name = "…"` on the key attribute, or let
   the upgrade rename.
7. **Upgrade.** Set `NonProductionOnly` and connect, or run `mic-db upgrade` and apply the script. VITA's
   `DbInfo` table is matched by name and altered in place; its `InstanceType` value survives. VITA's
   `DbModuleInfo` table is left as an unknown table.
8. **Recompute the hashes.** A member declared with `[HashFor]` holds VITA's CRC32 in the migrated database,
   and MIC ORM computes FNV-1a. A query by hash finds no row until the stored values are rewritten. Assign each
   one from its text, once:

   ```csharp
   foreach (var user in await session.EntitySet<IUser>().ToListAsync())
     user.UserNameHash = app.ComputeStableHash(user.UserName);
   await session.SaveChangesAsync();
   ```
9. **Run the tests.** Queries that used VITA-only extensions (`RegisterFunctions`, search objects, `Select`) are
   rewritten with LINQ, QueryMaker or direct SQL.
