# MIC ORM on an existing Db2 database

Many business systems keep their records in Db2, in a schema that was designed years ago and belongs to the
system that made it. A new application, a service or a web front end, has to read and update those records, and
the database must stay as it is. MIC ORM runs on Db2 for this case: `mic-db entities` writes the entity model
from the database, and the application connects without touching the schema. This page goes through the steps
on `SAMPLE`, the sample database that comes with Db2.

The provider is for Db2 on Linux, UNIX and Windows, and is tested on Db2 12.1 on Linux. Db2 for z/OS and Db2 for
i are other products with their own catalogs and are not supported.

## Packages

The provider is `Mic.Orm.Db.Db2`. It works over IBM's ADO.NET driver, which IBM ships as one package per
operating system, each with the native client inside. The provider names none of them as a dependency; the
application references the one of its system, beside the provider:

```bash
dotnet add package Mic.Orm.Db.Db2 --prerelease
```

```xml
<PackageReference Include="Net.IBM.Data.Db2" Version="10.0.0.400"
                  Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
<PackageReference Include="Net.IBM.Data.Db2-lnx" Version="10.0.0.400"
                  Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="Net.IBM.Data.Db2-osx" Version="10.0.0.400"
                  Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
```

The driver's licence is IBM's, and the application's owner accepts it. The driver's version above is the one the
provider is built and tested with, on Windows.

## Generating the model

[Generating entities from a database](tool-entities.md) describes the tool. The config for `SAMPLE`:

```json
{
  "provider": "Db2",
  "connectionString": "env:SAMPLE_CONN",
  "providerPath": "../Sample/bin/Debug/net10.0",
  "schemas": [ "DB2INST1" ],
  "namespace": "Sample.Model",
  "appClass": "SampleApp",
  "output": "SampleApp.cs"
}
```

`providerPath` is the output folder of a built project that references the two packages; the driver's native
client is loaded from there. The connection string has the driver's form:
`Server=localhost:50000;Database=SAMPLE;UID=…;PWD=…`. A schema's name is written as the catalog has it, in upper
case unless it was created in quotes.

```bash
mic-db entities Sample/mic-db/entities.json
```

For `SAMPLE` the tool writes 14 tables and 20 views. One of the entities, shortened:

```csharp
[Entity(TableName = "EMPLOYEE")]
public interface IEMPLOYEE {
  [PrimaryKey(Name = "PK_EMPLOYEE"), Size(6), Column("EMPNO", DbTypeSpec = "char(6)")]
  string EMPNO { get; set; }

  [Size(15), Column("LASTNAME")]
  string LASTNAME { get; set; }

  [EntityRef(KeyColumns = "WORKDEPT", ForeignKeyName = "RED"), Index(Name = "XEMP2")]
  IDEPARTMENT? DEPARTMENT { get; set; }

  [Size(8), Column("JOB", DbTypeSpec = "char(8)")]
  string? JOB { get; set; }

  [Column("HIREDATE")]
  DateOnly? HIREDATE { get; set; }

  [Column("SALARY", Precision = 9, Scale = 2)]
  decimal? SALARY { get; set; }
}
```

What an older Db2 schema brings into the generated file:

* **Upper-case names.** Db2 stores an unquoted name in upper case, so the interfaces and members are named
  `IEMPLOYEE` and `LASTNAME`. Every database name is in an attribute, so the identifiers can be renamed to
  `IEmployee` and `LastName` and the mapping stands.
* **`CHAR(n)` columns.** A fixed-length column carries `DbTypeSpec = "char(n)"`. Its value is read padded with
  blanks to the column's length: `JOB` of `SAMPLE`'s first employee is `"PRES    "`.
* **Tables with no primary key.** An entity needs a key. A table without one is written under a unique key
  whose columns are all required, where it has such a key; otherwise it is left out with a warning. `SAMPLE`
  has 11 such tables of its 25, and none has a unique key. They are read with
  [direct SQL](direct-sql.md).
* **A foreign key from a key to the same key** is left out with a warning; its columns are plain members.
* **XML columns** are `string` members that hold the document's text.
* **A materialized query table** is written as a view and is read like one.
* **Columns of a type with no .NET counterpart**, `VECTOR` among them, are left out with a comment.

## Connecting

```csharp
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Db2;
using Mic.Orm.Db.Model.Upgrades;
using Sample.Model;

var app = new SampleApp();
app.Init();
var settings = new DbSettings(new Db2Provider(), connectionString) { UpgradeMode = DbUpgradeMode.Never };
await app.ConnectToAsync(settings);
```

`DbUpgradeMode.Never` is what keeps the database as it is. Under it the connect does not read the catalog,
compares nothing and creates nothing; MIC's bookkeeping table `DbInfo` is not made either.

Keep that mode for a database another system owns. A generated model does not describe everything in the
database, and an upgrade would bring the database to the model. `mic-db upgrade` writes the script without
running it, which shows what an upgrade would do. For `SAMPLE` the script has 45 steps: it recreates the 20
views to store a hash in each, creates `DbInfo`, and drops a sequence and the self-referencing foreign key,
neither of which the model declares. When the schema changes, the DBA changes it, and the model is edited or
generated again to follow.

## Reading and updating

Sessions, LINQ and saves work as on the other servers. See [Reading data](linq-queries.md) and
[Updating data](saving-changes.md).

```csharp
var session = app.OpenSession();
var staff = await session.EntitySet<IEMPLOYEE>()
  .Where(e => e.DEPARTMENT!.DEPTNAME.StartsWith("SPIFFY") && e.SALARY > 50000)
  .OrderBy(e => e.LASTNAME)
  .Take(10)
  .ToListAsync();

var employee = await session.GetEntityAsync<IEMPLOYEE>("000010");
employee!.BONUS = 1200m;
await session.SaveChangesAsync();
```

A table left out of the model is read through the same session:

```csharp
InfoTable rows = await session.RunSqlQueryAsync(
  "SELECT ID, NAME, DEPT FROM DB2INST1.STAFF WHERE DEPT = @dept", ("dept", 20));
```

## Limits on Db2

| Area | On Db2 |
|---|---|
| Asynchronous calls | IBM's driver runs them synchronously: an `await` on a query or a save holds the calling thread until the server answers. |
| A text parameter that stands alone | In a select list, a conditional or a coalesce the server wants a parameter's type stated, and a string is sent as `VARCHAR(4000)`. |
| [Temp tables](temp-tables.md) | Need a user temporary table space in the database, which a DBA creates. So does a list of values whose text is longer than `DbSettings.RowsTempTableThreshold`. |
| [JSON](json.md) | A `[Json]` member is a `CLOB` and its paths are read with `JSON_VALUE`. The items of a JSON array are not queried as rows: a `SelectMany` over an array is refused. |
| [Geographic points](geo-points.md) | A `GeoPoint` member is stored as 21 bytes and reads back as it was written. `GeoFunctions.Distance` is refused. |
| [Full-text search](full-text.md) | Not supported. |
| Ordering by a sub-query | `OrderBy(d => d.Employees.Any())` fails on the server (`SQL0206N`). Select the value and order in memory, or order by a column. |
| Unlimited members in a set operation | `Union`, `Intersect` and `Except` over entities with an `[Unlimited]` text member fail (`SQL0134N`). `Concat` works, and so does a projection without that member. |
| Comparing an unlimited binary member | Only a test for null runs. A binary that is looked up by its value gets a `[Size]`. |

## A new schema on Db2

MIC ORM also creates and upgrades a schema of its own on Db2, as on the other servers; `dotnet new mic-app
--provider Db2` makes such an application. What differs there:

* A `Guid` is a `binary(16)` column and a `bool` a `boolean`.
* A filtered index is refused when the model is built. A view declared materialized is an ordinary view.
* After a column is dropped or its type changes, the upgrade reorganizes the table (`REORG`), which Db2 requires
  before the table is used again.
* Db2 has no lock that outlives a transaction, so two instances of the application that start at once are not
  kept from upgrading at the same time. Upgrade from one instance, or with `mic-db upgrade`.
