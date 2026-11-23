# Running tests

How to run the ORM's test suites from a clone of the repository: what to install, which servers to start, how to
pick the database vendors a run covers, and what a run leaves behind.

## Prerequisites

* The .NET 10 SDK.
* Docker: Docker Desktop on Windows and macOS, Docker Engine with the compose plugin on Linux. It runs the database
  servers.

Nothing else. SQLite needs no server; its driver comes with the `Mic.Orm.Db.Sqlite` package. The other drivers come
with their provider packages the same way. A server installed on the machine can stand in for a container; see
[Using an installed server](#using-an-installed-server).

## The suites

All under `src/Tests/Orm`. Each is an MSTest project.

| project | covers | needs a server |
|---|---|---|
| `Mic.Tests.Orm` | what the model alone can answer: the model builder and its diagnostics, provider capabilities, save planning, LINQ translation to SQL text against the Beauty Shop model | no |
| `Mic.Tests.Orm.Ddl` | schema: DDL generation, introspection, the upgrade, the upgrade lock, the `mic-db` tool | yes |
| `Mic.Tests.Orm.Dml` | data: CRUD, save ordering, events, validation, sessions, sequences, row versions, locking, temp tables, direct SQL | yes |
| `Mic.Tests.Orm.Linq` | queries: what the engine produces, run against the server | yes |

`Mic.Tests.Orm.DbHarness` is a library the three server suites share, not a suite.

Each server suite holds its tests in one abstract class with one thin `[TestClass]` per vendor, so every test runs
against every vendor in the run. The classes run in parallel and the tests within a class in sequence, so a run
costs about what its slowest vendor costs.

## Start the servers

The servers are containers declared in `docker-compose.yml` at the root of the repository. The ports are not the
servers' defaults, so a server already installed on the machine keeps its port.

| vendor | container | port | started by |
|---|---|---|---|
| PostgreSQL | built from `docker/postgres/Dockerfile` | 5433 | `docker compose up -d` |
| MySQL | `mysql:8.4` | 3307 | `docker compose up -d` |
| SQL Server | built from `docker/sqlserver/Dockerfile` | 1434 | `--profile heavy` |
| Oracle | `gvenzl/oracle-free:23` | 1522 | `--profile heavy` |
| Db2 | `icr.io/db2_community/db2:12.1.5.0` | 50000 | `--profile db2` |
| SQLite | none, a file under the temp folder | | |

The default set, PostgreSQL and MySQL, starts in a few seconds:

```bash
docker compose up -d
```

SQL Server and Oracle are opt-in, behind the `heavy` profile. SQL Server is built on first start from the stock
image plus the full-text search package, about 1.5 GB, and wants 2 GB of memory. Oracle is several gigabytes and
takes a minute or two to report healthy on first start. `--wait` returns when both are ready:

```bash
docker compose --profile heavy up -d --wait
```

Stop them when the run is over; the two heavy containers idle at a noticeable share of CPU and memory:

```bash
docker compose --profile heavy down
```

Db2 is behind a profile of its own and is not in `all`: its container beside SQL Server's and Oracle's wants more
memory than a developer's Docker usually has. Starting it accepts IBM's licence for Db2 Community Edition, which
the compose file states with `LICENSE: accept`. The first start creates the instance and two databases and takes
eight to nine minutes; a later start takes under a minute, so the container is stopped and kept, not removed:

```bash
docker compose --profile db2 up -d --wait db2
MIC_TEST_VENDORS=db2 dotnet test src/MicOrm.slnx
docker compose stop db2
```

MySQL, SQL Server and Oracle keep nothing between runs: every `up` is a clean server. PostgreSQL keeps its data in
the named volume `mic-test-pgdata`, because the same container also hosts the `BeautyShop` sample database, which
is a thing a person opens. `docker compose down -v` discards it too; the sample is rebuilt by
`dotnet run --project src/Samples/BeautyShop -- --drop`.

## Choose the vendors

The environment variable `MIC_TEST_VENDORS` says which vendors a run covers:

| value | vendors |
|---|---|
| unset | SQLite, PostgreSQL, MySQL: the ones that cost nothing to reach |
| `all` | the five: SQLite, PostgreSQL, MySQL, SQL Server and Oracle |
| `db2` | Db2, which runs only when named |
| `postgres,sqlite` | a chosen few, comma-separated |

The names are the values of `DbServerKind`: `Postgres`, `MySql`, `SQLite`, `MsSql`, `Oracle`, `Db2`, matched
without regard to case. SQL Server is `MsSql`. A name that matches nothing enables nothing, and the run still reports
Passed, so check the skipped count after setting the variable.

A vendor not in the run is skipped: its tests end `Inconclusive`, not failed. A vendor in the run that cannot be
reached fails, because it was asked for.

## Run

Everything, on the default vendors:

```bash
dotnet test src/MicOrm.slnx
```

One suite, on PostgreSQL alone:

```bash
MIC_TEST_VENDORS=postgres dotnet test src/Tests/Orm/Mic.Tests.Orm.Dml
```

```powershell
$env:MIC_TEST_VENDORS = "postgres"; dotnet test src/Tests/Orm/Mic.Tests.Orm.Dml
```

One test class or one test, by MSTest's filter:

```bash
dotnet test src/Tests/Orm/Mic.Tests.Orm.Linq --filter "FullyQualifiedName~Postgres_LinqTests"
dotnet test src/Tests/Orm/Mic.Tests.Orm.Dml --filter "Name=RowVersion_RefusesAStaleSave"
```

The five vendors, in two runs. Oracle runs its container at full CPU for the length of a suite and starves the
other four into connection-open timeouts when it runs beside them, so it runs alone:

```bash
docker compose --profile heavy up -d --wait
MIC_TEST_VENDORS=postgres,mysql,sqlite,mssql dotnet test src/MicOrm.slnx
MIC_TEST_VENDORS=oracle dotnet test src/MicOrm.slnx
docker compose --profile heavy down
```

```powershell
docker compose --profile heavy up -d --wait
$env:MIC_TEST_VENDORS = "postgres,mysql,sqlite,mssql"; dotnet test src/MicOrm.slnx
$env:MIC_TEST_VENDORS = "oracle"; dotnet test src/MicOrm.slnx
docker compose --profile heavy down
```

Rough times, after the build:

| run | time |
|---|---|
| `Mic.Tests.Orm` | seconds |
| `Dml` or `Linq` on the default vendors | under a minute |
| `Ddl` on the default vendors | about a minute |
| `Ddl` on Oracle | 2 to 3 minutes; a few DDL tests build every shape MIC can express and read the catalog back |
| the five vendors, every suite, the two runs | 5 to 10 minutes |

## Visual Studio Test Explorer

The suites run from Test Explorer as they do from `dotnet test`: start the containers first, then run. Grouped by
class, a server suite shows one node per vendor, `Postgres_DmlTests`, `Sqlite_DmlTests` and so on; a vendor not in
the run shows as skipped. The parallelism is the suite's own, class-level setting; Test Explorer's "Run tests in
parallel" button does not change it.

The tests read `MIC_TEST_VENDORS` from the process Test Explorer starts, which inherits Visual Studio's
environment, not a shell's. The repository sets it for Test Explorer in `src/.runsettings`, beside the solution:

```xml
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <MIC_TEST_VENDORS>postgres,sqlite</MIC_TEST_VENDORS>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

Visual Studio finds the file when the solution opens and lists it checked under Test > Configure Run Settings; the
option behind that is "Auto detect runsettings files" under Tools > Options > Test, on by default. Edit the value
for another set, or add `MIC_TEST_LOG` and the `MIC_TEST_*` connection strings beside it. A user environment
variable, set in Windows' environment settings, works too; Visual Studio reads it at start, so restart it after a
change.

`dotnet test` ignores the file unless `--settings src/.runsettings` names it, and then the file's value wins over
the shell's. The command lines above run without it.

## Using an installed server

Each vendor's fixture takes its connection string from an environment variable and falls back to the container's:

| variable | default |
|---|---|
| `MIC_TEST_POSTGRES` | `Host=localhost;Port=5433;Database=mic_test;Username=mic;Password=mic` |
| `MIC_TEST_MYSQL` | `Server=localhost;Port=3307;User ID=root;Password=mic;AllowUserVariables=True` |
| `MIC_TEST_SQLSERVER` | `Server=localhost,1434;User ID=sa;Password=Mic_test_1;TrustServerCertificate=True;Database=mic_test` |
| `MIC_TEST_ORACLE` | `User Id=system;Password=mic;Data Source=localhost:1522/FREEPDB1;Self Tuning=false;Statement Cache Size=0` |

The login needs to create and drop the vendor's unit of isolation, listed below: a schema on PostgreSQL and SQL
Server, a database on MySQL, a user on Oracle. Oracle also needs the two grants in `docker/oracle-init/grants.sql`
for the full-text tests.

## Files a run leaves behind

**The SQL log.** Every statement a test runs, with its parameters, goes to a file per vendor and test class under
the suite's output folder: `bin/Debug/net10.0/logs/{Vendor}/{Class}.log`, for example `logs/Postgres/DmlTests.log`.
A banner marks each test. The file is truncated at the start of the run. `MIC_TEST_LOG=0` turns the log off, which
makes a run somewhat faster.

**Databases.** A test asks its fixture for a database of its own, and each vendor answers differently:

| vendor | a database of one's own |
|---|---|
| PostgreSQL | a schema in `mic_test` |
| MySQL | a database, since a MySQL schema is a database |
| SQL Server | a schema in `mic_test` |
| Oracle | a user with no password |
| Db2 | a schema in `MICTEST` |
| SQLite | a file under `{temp}/mic_tests` |

The DDL suite is about schema churn, so each of its tests takes a database of its own and drops it at the end. The
DML and LINQ suites share one per vendor, created when the first test needs it and dropped when the run ends. A run
killed half-way leaves one behind; the next run makes its own, and the leftover is harmless until a cleanup such as
`docker compose down` takes the server with it. On SQLite the leftover is a file in the temp folder.
