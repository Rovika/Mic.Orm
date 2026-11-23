# mic-db — the MIC database tool

A console tool with two jobs, each driven by a JSON config file:

* **`upgrade`** — compares a database with an application's entity model and writes the DDL script that brings the
  database up to the model, for a production upgrade done by hand. Nothing runs unless you say so.
* **`entities`** — generates C# entity interfaces from an existing database, with the module and the application
  class that register them: the database-first start of a MIC application.

## Installing

The tool is packed as a dotnet tool named `mic-db`. From the repository:

```bash
dotnet pack src/Tools/Mic.DbTool/Mic.DbTool.csproj -c Release -o ./nupkg
dotnet tool install --global Mic.DbTool --add-source ./nupkg
```

Or run it from its build output without installing:

```bash
dotnet src/Tools/Mic.DbTool/bin/Debug/net10.0/Mic.DbTool.dll upgrade my-config.json
```

`mic-db -h` prints the commands and their options.

## The config file

Both jobs read one JSON file. Comments and trailing commas are allowed, keys are matched ignoring case, and every
path in the file is relative to the file's own folder. The schema `mic-db.schema.json` ships beside the tool; point
`"$schema"` at it and an editor completes and validates the file.

| key | job | meaning |
|---|---|---|
| `provider` | both | The kind of server: `Postgres`, `MySql`, `MsSql`, `Oracle` or `SQLite`. |
| `connectionString` | both | The connection string the database is read with. |
| `schemaConnectionString` | upgrade | The connection schema changes run on when the script is applied; the connection string itself when absent. |
| `providerPath` | both | The folder the provider assembly and its driver are loaded from. Required by `entities`; for `upgrade` it defaults to the application assembly's folder. |
| `appAssembly` | upgrade | The path of the assembly holding the application. |
| `appClass` | both | For `upgrade`, the full name of the application's `EntityApp` class, needed only when the assembly holds more than one. For `entities`, the name of the class written; `DbApp` when absent. |
| `output` | both | The file written: the script, or the source. Defaults to `upgrade.<provider>.sql` and `<appClass>.cs` beside the config. |
| `dropUnknownTables` | upgrade | Whether the script drops tables the model does not know. Default `false`. |
| `ignoreDbObjects` | upgrade | Patterns of the objects the script neither drops nor changes, such as `*.Order.IX_Report_*`. Default none. |
| `options` | upgrade | The `DbOptions` flags the model is mapped with, separated by commas: `UseRefIntegrity`, `AutoIndexForeignKeys`, `PluralizeTableNames`, `AddSchemaToTableNames`. Default `UseRefIntegrity`. |
| `schemas` | entities | The schemas read. Absent or empty: every schema but the system ones. |
| `tables` | entities | The tables and views taken, as names or patterns with `*` and `?`, a schema prefix allowed (`sales.Order*`). Absent: every table of the schemas. |
| `ignoreTables` | entities | The tables and views left out, in the same form. |
| `namespace` | entities | The namespace the source declares. Default `Generated`. |
| `module` | entities | The name of the module class. Default: the schema's name followed by `Module`. With several schemas each module is named after its schema with this name appended. |
| `lists` | entities | Whether a referenced entity gets an `IList<IChild>` member for each entity referencing it, written commented out. Default `true`. |
| `autoGuidKeys` | entities | Whether a single-column Guid primary key is marked `[Auto]`. Default `true`. |
| `addDbObjectsNames` | entities | Whether every table, column, key and foreign key is named in an attribute, so that any identifier in the file can be renamed and the mapping stands. Default `true`; off, a name is written only where it differs from what MIC would derive. |

### Keeping the connection string out of the file

A config file ends up in the repository next to the project it serves, and a connection string with a password in
it is what a security scan rejects. The two connection-string keys therefore take three forms:

| form | meaning |
|---|---|
| `"Host=…;Password=…"` | The value itself. Fine for a local test server whose credentials are no secret. |
| `"env:PG_CONN_DEV"` | The environment variable of that name, read when the tool runs. The form every CI system and secret store hands a secret to a process in; nothing lands on disk. |
| `"file:../secrets/pg-dev.txt"` | The file's content, trimmed, the path relative to the config. A git-ignored file beside the config, for a developer's own machine. |

A variable that is not set, or a file that is not there, stops the job with a message naming it. The option
`--connection <value>` on either command replaces the config's `connectionString` and takes the same three forms,
for a pipeline that wants to touch no file at all:

```bash
mic-db upgrade deploy/prod.json --connection env:PROD_CONN
```

### Where the provider comes from

The tool itself carries only the ORM core, `Mic.Orm`. The provider for your server
(`Mic.Orm.Db.Postgres`, `Mic.Orm.Db.MySql`, `Mic.Orm.Db.SqlServer`, `Mic.Orm.Db.Oracle`, `Mic.Orm.Db.Sqlite`) and the
driver under it are loaded from a folder at run time: a built application already has them in its output folder,
which is why `upgrade` needs no `providerPath`. A class library has them there only when its project sets
`CopyLocalLockFileAssemblies`; otherwise point `providerPath` at the output folder of a host that references the
provider. The application must be built against the same major and minor version of `Mic.Orm` the tool carries,
and with the tool's patch or an earlier one. The tool checks and names both versions when they do not match.

## Job: upgrade

Compares a database with an application's entity model and writes the DDL script that brings the database up to
the model, for a production upgrade done by hand. Nothing runs unless you say so.

```bash
mic-db upgrade <config.json> [--output <path>] [--apply] [--connection <value>]
```

What happens:

1. The application assembly is loaded, its `EntityApp` class found and constructed through its parameterless
   constructor, and `Init()` run. A model that fails to build stops here with the build report.
2. The provider is loaded, the database read, and its schema compared with the model as a connect would compare
   it. Nothing is changed.
3. The script is written to `output`, with a header that says when it was made, from which application and
   version, for which server, what the database declares itself to be and at which version, then the warnings,
   then the statements stage by stage. A script that would lose data says so at the top and counts the destructive
   steps. When nothing differs, the file says the database matches the model.
4. The report's messages are printed. Exit code 0 means done; 1 means the job failed (a missing file, an assembly
   built against another `Mic.Orm`, a database that cannot be reached); 2 means the report holds errors, or an apply
   did not happen.

With `--apply`, and only when the script is not empty, the tool then connects the application to the database the
ordinary way with the upgrade mode forced to *always*. The connect reads the database again under the upgrade lock
and runs what that finds, which is the planned script unless another instance changed the database in between. The
tool then writes the file again from the run, so the file holds the script that ran, with a header line saying it
was applied. A run that fails part-way leaves the script it attempted in the file, with the error.

A config for the Beauty Shop sample against the local test PostgreSQL:

```json
{
  "$schema": "../mic-db.schema.json",
  "provider": "Postgres",
  "connectionString": "Host=localhost;Port=5433;Database=mic_test;Username=mic;Password=mic",
  "appAssembly": "../../../Samples/BeautyShop/bin/Debug/net10.0/BeautyShop.dll",
  "output": "beauty.upgrade.postgres.sql"
}
```

Typical use: run it against a copy of production, read the script, hand it to whoever runs scripts on production.
Run it again afterwards; the file should then say the database matches the model.

## Job: entities

Generates C# entity interfaces from an existing database, with the module and the application class that
register them: the database-first start of a MIC application. Take all of a database or a selection of its
schemas and tables, rename what reads badly, and the mapping to the database stands.

```bash
mic-db entities <config.json> [--output <path>] [--connection <value>]
```

What happens:

1. The provider is loaded from `providerPath` and the schemas named by `schemas` are read: tables, columns, keys,
   indexes, foreign keys, check constraints and views. Nothing is written to the database. A check constraint is
   written as a comment above its interface, with a `[Check]` declaration to uncomment.
2. The tables and views are filtered by `tables` and `ignoreTables`. MIC's own `DbInfo` table is never taken.
3. One C# file is written: an interface per table and view, sorted by name with the views after the tables, a
   module per schema that registers them, the application class, which adds each module with its schema and takes
   the first schema as the default, and at the end a class holding the views' defining selects. The file starts
   with `#nullable enable` and an `<auto-generated>` header.
4. Whatever the file could not express is printed as a warning and is also in the file as a comment where it
   applies.

A config that reads the Beauty Shop database back:

```json
{
  "$schema": "../mic-db.schema.json",
  "provider": "Postgres",
  "connectionString": "Host=localhost;Port=5433;Database=BeautyShop;Username=mic;Password=mic",
  "providerPath": "../../../Samples/BeautyShop/bin/Debug/net10.0",
  "schemas": [ "beauty" ],
  "namespace": "BeautyShop.Generated",
  "appClass": "BeautyShopApp",
  "module": "BeautyShopModule",
  "output": "BeautyShopApp.generated.cs"
}
```

### What the generated code looks like

The output of that config, `Samples/BeautyShopApp.generated.cs`, is kept beside it to look at; running the config
again rewrites it.

Names become PascalCase: underscores and spaces are removed and the next letter capitalized, so `order_line` is
`IOrderLine` and `created_on` is `CreatedOn`. By default every table, column, key and foreign key is named in an
attribute, whatever the derived name would have been:

```csharp
[Entity(TableName = "User"), Unique("Tenant,Login", Name = "UQ_User_Tenant_Login"), Unique("Tenant,Email", Name = "UQ_User_Tenant_Email")]
public interface IUser {
  [PrimaryKey(Name = "PK_User_Id"), Auto, Column("Id")]
  Guid Id { get; set; }

  [Size(100), Column("Login")]
  string Login { get; set; }

  [Size(200), Column("Email")]
  string Email { get; set; }

  [Size(100), Column("PasswordHash")]
  string PasswordHash { get; set; }

  [Column("UserType")]
  int UserType { get; set; }

  [Column("Status")]
  int Status { get; set; }

  [Column("LastSignedInOn")]
  DateTime? LastSignedInOn { get; set; }

  [EntityRef(KeyColumns = "Company_Id", ForeignKeyName = "FK_User_Company_Id")]
  ICompany? Company { get; set; }

  [EntityRef(KeyColumns = "Tenant_Id", ForeignKeyName = "FK_User_Tenant_Id"), Index(Name = "IX_User_Tenant")]
  ITenant Tenant { get; set; }

  // Lists: uncomment, delete, or replace with IQueryable<T> for a large, ever-growing set of dependent entities.
  // IList<ICustomer> Customers { get; }
  // IList<IEmployee> Employees { get; }
}
```

That is the point of a database-first start: an old database has abbreviated, cryptic or inconsistent names, and
the next job is renaming the entities and properties into something that reads, without breaking the match to the
database. With the names pinned, `IUser` can become `IAccount` and `LastSignedInOn` can become `LastLogin`, and
the table, the column and the index they map to stay what they are. The list members and the reference members'
own names are free already; they map to nothing by name.

With `addDbObjectsNames` off, a name is written only where it differs from what MIC would derive from the
identifier, and a database MIC made comes back with no name attributes at all:

```csharp
[Entity, Unique("Tenant,Login"), Unique("Tenant,Email")]
public interface IUser {
  [PrimaryKey, Auto]
  Guid Id { get; set; }

  [Size(100)]
  string Login { get; set; }

  [Size(200)]
  string Email { get; set; }

  [Size(100)]
  string PasswordHash { get; set; }

  int UserType { get; set; }

  int Status { get; set; }

  DateTime? LastSignedInOn { get; set; }

  ICompany? Company { get; set; }

  [Index]
  ITenant Tenant { get; set; }

  // Lists: uncomment, delete, or replace with IQueryable<T> for a large, ever-growing set of dependent entities.
  // IList<ICustomer> Customers { get; }
  // IList<IEmployee> Employees { get; }
}
```

* The primary key's members come first; the database's column order decides the rest.
* A nullable column is a nullable member: `int?`, `string?`.
* Text and binary columns carry `[Size(n)]` or `[Unlimited]`; decimals carry their precision and scale; a column
  whose type is not the one MIC would choose for its CLR type carries `DbTypeSpec`; a default carries `Default`.
* An identity column is `[Identity]`; a row-version column `[RowVersion]`; a single-column Guid primary key
  `[Auto]` unless `autoGuidKeys` is off.
* A foreign key becomes a reference member typed as the target entity, named after its column with the target
  key's name cut off the end (`Company_Id` → `Company`), nullable if the column is, `[CascadeDelete]` where the constraint
  cascades. The foreign-key column itself is not a member; MIC generates it from the reference.
* A key or index over one member sits on that member: `[PrimaryKey]`, `[Unique]`, `[Index]`. One over several, or
  with a descending column, sits on the interface with the member list: `[Index("Tenant,-StartsOn")]`. A filter,
  included columns and an access method other than the server's default are written as `Filter`,
  `IncludeMembers` and `IndexType`. A clustered index is `[ClusteredIndex]`; a primary key's placement is left to
  the server.
* With `lists` on, every reference puts an `IList<IChild>` on the entity it points at, named by the child's plural;
  `[OneToMany("Ref")]` names the reference where the child points at the same entity twice. The lists are written
  commented out, under a comment that says what to do with them: a tenant entity would otherwise get one list
  per table in the database. Uncomment the ones that read as a part of their owner, delete
  the rest, and for a large, ever-growing set of dependents (a company's sales, a customer's events) replace `IList<T>`
  with `IQueryable<T>`, a queryable property that loads nothing until a query over it asks.
* A view is an interface too, marked `[Entity, View("v_Name")]`, `Materialized = true` where it is one. Its
  defining select, as the server reports it, is a constant of the `ViewDefinitions` class at the end of the file,
  and the module registers the view with it for the server's kind, `RegisterView<IView>(DbServerKind.Postgres,
  ViewDefinitions.ViewSql)`, so the generated application owns the view: an upgrade creates it on an empty
  database, and a second server's select is the dictionary overload of the same call, written by hand. The views are
  written in dependency order, a view after the views it reads. The first upgrade over the generated model
  recreates each view once with the same definition, to store the hash it is compared by from then on. Where the
  server reports no definition, the view is registered as an entity and read as existing. Since
  every entity needs a primary key and a view defines none, the member named `…Id`, or the first, is declared as
  the key and made non-nullable, with a comment saying so.
* A column whose type maps to no CLR type, or that the server computes, is left out with a comment above the
  interface naming it.

### Generating a subset

A large database is rarely one module. Name the schemas and tables that are yours:

```json
{
  "schemas": [ "sales" ],
  "tables": [ "Order*", "Customer", "Invoice" ],
  "ignoreTables": [ "OrderAudit" ],
  "module": "SalesModule"
}
```

A table in the selection that references one outside it cannot have a reference member, since the target
interface does not exist. Its foreign-key column stays a plain column, with a comment naming the table it points
at, and the console counts them. Bear in mind that an `upgrade` run over such a partial model sees a foreign key the
model does not declare and plans to drop it; a partial model is partial by design, and the file's header says so.

### After generating

The file is a start, not an end: rename what the database named badly, replace `int` with your enums, add the
`[Utc]`, `[Secret]` and `[Json]` the database cannot tell you about, go through the commented-out lists, and split
the module where it is really several. Then run `upgrade` over the built assembly against the same database: it should find nothing to do, apart
from rebuilding any filtered index once (the database reports a filter rewritten, so its stored hash differs).

## Exit codes

| code | meaning |
|---|---|
| 0 | done |
| 1 | the job failed before it could finish: a missing file, an assembly built against another `Mic.Orm`, a database that cannot be reached |
| 2 | `upgrade` finished but the report holds errors, or `--apply` did not apply |
