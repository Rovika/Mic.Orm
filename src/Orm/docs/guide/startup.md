# Application startup

An application's data layer is assembled before the first query: the modules combined into a model and
checked, the model matched to a database, the connection made, the schema brought up to date. A host needs this
to be short, in one place, and to report each failure where it happens.

In MIC ORM the startup is three calls in a fixed order: construct the application, `Init` to build the model,
`ConnectToAsync` to connect and upgrade. The host keeps the connected application for the life of the process
and shuts it down at the end. This page walks the sequence, the connect and its settings, what each step
reports or throws, and the shutdown.

## Startup sequence

```csharp
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;

var app = new ShopApp();
app.Init();
var connectionString = ConnectionStringHelper.Resolve("env:SHOP_CONN");
var report = await app.ConnectToAsync(new DbSettings(new PostgresProvider(), connectionString));
```

1. **Construct the application.** The constructor adds the modules, extensions, cache declarations and
   services. Nothing is validated yet. The application class is described on [Application data
   model](data-model.md#modules-and-the-application).
2. **`Init()`** builds the entity model from the modules and applies the extensions. The application's settings
   are read here and not afterwards. It returns the build report, with the warnings. A model with errors throws
   `InvalidOperationException` with the report in the message; the model stays on `app.Model` for inspection and
   `app.Initialized` is false.
3. **`ConnectToAsync(DbSettings)`** connects to the database and brings its schema up to date with the model.
   It returns the upgrade report. See [Connecting to the database](#connecting-to-the-database).
4. **Save the connected application.** The connected application is one per process. Save it as a global
   singleton, in a static field or in the DI container, as you prefer.

From then on the application's operations run on it through contexts and sessions: see [Core runtime
objects](core-objects.md).

## Connecting to the database

The connect takes the model `Init` built and a `DbSettings` that names the database: the provider, the
connection string, and how the schema is treated. It maps the model to the provider's types, opens a connection,
reads the database, compares it with the model and upgrades it as `UpgradeMode` allows. See [Maintaining the
database schema](database-model.md). `ConnectToAsync(settings)` is the call.

`ConnectTo(settings)` is the synchronous form, for a console host with a synchronous `Main`. It blocks the
calling thread until the connect ends, and does not deadlock on a UI thread. A host with an asynchronous entry
point calls `ConnectToAsync`.

The call returns the upgrade report: what the database was, what the connect changed, what it left as it was.
It throws `DataAccessException` when the model cannot be mapped to the provider, when the database cannot be
reached, or when the upgrade ran and failed. A schema that differs under an `UpgradeMode` that forbids the
change is reported, not thrown: `report.IsApplied` is false and `report.Changes` lists the differences.

The members of `DbSettings`:

| Member | Default | Meaning |
|---|---|---|
| `Provider` | | The provider: `PostgresProvider`, `SqlServerProvider`, `MySqlProvider`, `OracleProvider`, `SqliteProvider`, `Db2Provider`. |
| `ConnectionString` | | The connection string the application's work runs on. |
| `SchemaConnectionString` | the connection string | A second connection string for schema changes, for a login with DDL rights. |
| `DatabaseAlias` | `"(default)"` | The name the application knows this database by. |
| `Options` | `DbOptions.Default` | How the model is mapped to this database; the flags below. |
| `UpgradeMode` | `NonProductionOnly` | `Never`, `NonProductionOnly`, `Always`. |
| `DropUnknownTables` | `false` | Whether an upgrade drops tables the model does not declare. |
| `IgnoreDbObjects` | empty | Patterns of the objects an upgrade neither drops nor changes. See [Maintaining the database schema](database-model.md). |
| `UpgradeLockWait` | 60 seconds | How long an upgrade waits for another instance's upgrade before failing. |
| `UpgradeReportPath` | `null` | A file path the upgrade report is written to. |
| `DbInfoTableName` | `"DbInfo"` | The name of MIC's bookkeeping table. |

`DbOptions` is a flags enum. A value replaces the whole set, so a database that adds a flag writes
`Options = DbOptions.Default | DbOptions.AddSchemaToTableNames`.

| Flag | In `Default` | Meaning |
|---|---|---|
| `UseRefIntegrity` | yes | The model's references are foreign-key constraints. Off, MIC creates none and leaves the database's own alone: for a legacy database whose integrity is kept by the applications. |
| `AutoIndexForeignKeys` | no | Create an index on every foreign key. Ignored on servers that index foreign keys themselves. |
| `PluralizeTableNames` | no | Name a table by the plural of its entity name: `Employees` for `IEmployee`. `[Entity(PluralName = …)]` gives the plural where the rule is wrong; `[Entity(TableName = …)]` is taken as it is. |
| `AddSchemaToTableNames` | no | On a server without schemas (SQLite), prefix the schema to the table name: `shop_Customer`. |

What a session does with its connection between operations is the operation's, not the database's:
`OperationContext.ConnectionReuseMode`, `NoReuse` by default. See [Core runtime objects](core-objects.md).

## The connection string

The connection string in an application's configuration takes one of three forms:

| Value | Meaning |
|---|---|
| `Host=…;Password=…` | The connection string itself. |
| `env:SHOP_CONN` | The environment variable of that name. |
| `file:../secrets/shop.txt` | The content of the file, trimmed. A relative path is resolved against the configuration file's folder. |

The template's `AppConfig` and the `mic-db` tool's config files read all three. An application with a
configuration of its own resolves the value with `ConnectionStringHelper.Resolve(value, baseFolder)` in
`Mic.Orm.Db`; a missing variable or file throws `InvalidOperationException` naming it.

## Several databases

An application can connect to more than one database. Each `DbSettings` has its own `DatabaseAlias`, and
`ConnectToAsync` is called once per database. A session works with one database: `OperationContext.DatabaseAlias`
selects it, and `null` means the default alias.

```csharp
await app.ConnectToAsync(new DbSettings(new PostgresProvider(), mainConnection));
await app.ConnectToAsync(new DbSettings(new PostgresProvider(), archiveConnection, databaseAlias: "archive"));

var context = new OperationContext(app) { DatabaseAlias = "archive" };
var session = context.OpenSession();
```

A database added while the application runs is connected the same way, at any time after startup; a database
first learned of when a request names it is connected from a handler of `DatabaseEvents.DatabaseMissing`. See
[A database added at run time](multi-tenancy.md#a-database-added-at-run-time). The databases of one provider
share one mapped model; what is per database is the connection, the query cache and the entity cache.

## Startup errors

| Condition | Result |
|---|---|
| An interface has no primary key, an attribute is misused, a referenced type is not registered | `Init()` throws `InvalidOperationException` with the report; `app.Model.BuildReport` has the detail. |
| The model uses a feature the provider lacks | `ConnectToAsync` throws `DataAccessException` with the mapping report. |
| The database cannot be reached | `ConnectToAsync` throws `DataAccessException`. |
| The database is at a newer version than the application and is marked staging or production | `ConnectToAsync` throws `DataAccessException`. |
| The schema differs and `UpgradeMode` forbids the change | `ConnectToAsync` returns; the report's `IsApplied` is false and `Changes` lists the withheld changes. The developer takes the responsibility. |
| A DDL statement of the upgrade fails | `ConnectToAsync` throws; `DbInfo` records the failure; the next connect re-diffs and retries the remaining work. |

## Application shutdown

A process that ends has work to finish first: the application's background services stop, and the last batch
of the log is written. Shut the application down when the host stops: `await app.ShutdownAsync()`. `await using`
on the application does the same at the end of its scope, for a console program that holds the application in one
method. `app.Shutdown()` is the synchronous form, for an exit handler that cannot await; it blocks the calling
thread until the shutdown ends.

The shutdown raises `app.Events.Shutdown` for the application's own services, cancels `app.ShutdownToken` for
background work that watches it, and flushes the log. It runs once; a second call does nothing.
