# Mic.Templates — project templates for `dotnet new`

One template, `mic-app`: a MIC application in three projects, for the database server you name.

## Installing

From the package feed, added as a source once:

```bash
dotnet nuget add source https://rovika.github.io/Mic.Orm/feed/index.json --name mic-orm
dotnet new install Mic.Templates
```

From the repository, the ORM packages and the template packed into a local feed first:

```bash
dotnet pack src/Orm/Mic.Orm/Mic.Orm.csproj -o ./nupkg
dotnet pack src/Orm/Mic.Orm.Db.Postgres/Mic.Orm.Db.Postgres.csproj -o ./nupkg
dotnet pack src/Templates/Mic.Templates/Mic.Templates.csproj -o ./nupkg
dotnet new install ./nupkg/Mic.Templates.0.10.0-preview.1.nupkg
```

A generated application restores the MIC packages from the feeds the machine knows; with a local feed, point a
`nuget.config` next to the application at it.

## `mic-app`

```bash
dotnet new mic-app -n Shop --provider Postgres
```

| option | meaning |
|---|---|
| `-n`, `--name` | The application's name: the solution, the namespace, the application class, the schema (lower case) and the environment variable (upper case, `SHOP_CONN`). A valid C# identifier. |
| `--provider` | The server: `Postgres` (the default), `MySql`, `MsSql`, `Oracle` or `SQLite`. Chooses the provider package the host references and the provider it constructs. |
| `--micVersion` | The version of the MIC packages referenced; the version this template was packed with by default. Required when the template is installed from its source folder, where no version is set. |

What it makes, for `-n Shop`:

| | |
|---|---|
| `Shop.slnx` | The solution. |
| `Shop.Model/` | The entity model: `Entities.cs` (a customer and its orders, a starter), `ShopApp.cs` (the module and the application class, with the schema). References `Mic.Orm` alone. |
| `Shop/` | The console host: `DatabaseHelper` creates the provider and connects; `AppConfig` reads `appsettings.json`, where the connection string is `env:SHOP_CONN` (a file on SQLite); `Program` connects, adds a first customer and lists them. References the provider package. |
| `Shop/mic-db/` | `upgrade.json` and `entities.json` for the database tool, pointed at the host's output folder. |
| `Shop.Tests/` | One MSTest test: connects, saves a customer with an order, reads them back. |
| `README.md` | How to run it, and what to change first. |

## How it is tested

`Mic.Tests.Orm.Ddl` packs the ORM packages and this template into a feed of its own, installs the template into a
private hive, and per vendor makes an application from it, builds it against the feed and, on PostgreSQL and SQLite,
runs its test and its host. The packages and the hive live under the temp folder, `mic_template_tests`.
