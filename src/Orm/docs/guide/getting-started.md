# Getting started

An application in one command, from the `mic-app` project template: the entity model, a console host that
connects to the database, and a test.

## Install the template and the tool

MIC ORM needs .NET 10 or later: its packages target `net10.0` and do not install into a project on .NET
Standard, .NET Framework or an earlier .NET.

MIC ORM is a preview. Its packages are published to a feed of their own, not to nuget.org. Add the feed once:

```bash
dotnet nuget add source https://rovika.github.io/Mic.Orm/feed/index.json --name mic-orm
```

On a machine that uses package source mapping, also map `Mic.*` to this source. Then install:

```bash
dotnet new install Mic.Templates
dotnet tool install --global Mic.DbTool --prerelease
```

`Mic.DbTool` is the `mic-db` command, used later for the production upgrade script and for generating a model
from an existing database.

## Create the solution skeleton

```bash
dotnet new mic-app -n Shop --provider Postgres
```

`--provider` is `Postgres` (the default), `MySql`, `MsSql`, `Oracle`, `SQLite` or `Db2`. The name becomes the
solution, the namespace, the application class, the database schema in lower case (`shop`) and the environment
variable in upper case (`SHOP_CONN`). It should be a valid C# identifier.

What is made, for `-n Shop`, in a new folder `Shop`:

| | |
|---|---|
| `Shop.slnx` | The solution. |
| `Shop.Model/` | The entity model: `Entities.cs` with a starter `ICustomer` and `IOrder`, and `ShopApp.cs` with the module and the application class. References `Mic.Orm` alone and knows no server. |
| `Shop/` | The console host: `DatabaseHelper` constructs the provider and connects, `AppConfig` reads `appsettings.json`, `Program` connects, adds a first customer and lists the customers. References the provider package. |
| `Shop/mic-db/` | `upgrade.json` and `entities.json`, the tool's configs, pointed at the host's output folder. |
| `Shop.Tests/` | One MSTest test: connects, saves a customer with an order, reads them back. |

## Point it at a database

The host's `appsettings.json` holds the connection string, or says where it is kept:

| value | meaning |
|---|---|
| `Host=…;Password=…` | The connection string itself. For a local server whose credentials are no secret. |
| `env:SHOP_CONN` | The environment variable of that name, read at run time. What the template writes. |
| `file:../secrets/shop.txt` | A file holding the connection string, the path relative to the settings file. |

Set the variable and run, from the solution's folder:

```bash
cd Shop
export SHOP_CONN="Host=localhost;Database=shop;Username=shop;Password=shop"
dotnet run --project Shop
dotnet test
```

On SQLite the template writes `Data Source=shop.db` instead, a file created in the working
directory on the first run, and nothing needs setting.

The first run creates the schema `shop` and the tables `Customer`, `Order` and `DbInfo`, adds a customer with an
order and lists it:

```
Shop 1.0 is connected.
Added the first customer and an order.
1 customer(s):
  First customer, Active, 1 order(s)
```

## Edit the data model

Add a property to `ICustomer` in `Shop.Model/Entities.cs`:

```csharp
[Size(40)] string? Phone { get; set; }
```

Run again. The connect finds the column missing and adds it; nothing else is touched. Add an entity, register
it in `ShopModule`, and the next run adds its table. The host upgrades a database that does not declare itself
production; for production, `mic-db upgrade Shop/mic-db/upgrade.json` writes the DDL out for review.

## Where next

* [Simple application](simple-application.md): the model, sessions, queries and the upgrade, in one program.
* [Application data model](data-model.md), then [Core runtime objects](core-objects.md).
* [C# attributes for modeling](attributes.md): the attributes used most, with examples.
