# Generating the upgrade script

`mic-db upgrade` compares a database with an application's model and writes the DDL script that brings the
database up to the model. The script is for a production upgrade run by a person or a pipeline.

## When to use it

* The database is marked `Production` in its `DbInfo` row, so the application does not upgrade it.
* The schema change must be reviewed before it runs.
* The upgrade runs under a DBA's login or in a deployment pipeline, not by the application.

A development or staging database needs no script; the application upgrades it on connect. See
[Maintaining the database schema](database-model.md).

## Install

```bash
dotnet tool install --global Mic.DbTool --prerelease
```

The package comes from the ORM's own feed, added as [Getting started](getting-started.md) shows.

The tool carries its own `Mic.Orm` and runs the application on it. The application must be built against the
same major and minor version of `Mic.Orm` as the tool, and with the tool's patch or an earlier one: a later
patch may hold what the tool's `Mic.Orm` lacks. The tool refuses any other application and names both versions.

A repository keeps the tool at the version of its `Mic.Orm` with a local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install Mic.DbTool --prerelease
```

`dotnet tool restore` installs the recorded version on another machine and in a pipeline. The command is then
`dotnet mic-db`.

## The config file

The template writes `Shop/mic-db/upgrade.json`:

```json
{
  "$schema": "../mic-db.schema.json",
  "provider": "Postgres",
  "connectionString": "env:SHOP_CONN",
  "schemaConnectionString": "env:SHOP_SCHEMA_CONN",
  "appAssembly": "../bin/Debug/net10.0/Shop.Model.dll",
  "appClass": "Shop.Model.ShopApp",
  "dropUnknownTables": false,
  "ignoreDbObjects": [ "*.Order.IX_Report_*" ],
  "output": "upgrade.postgres.sql"
}
```

| Key | Meaning | Default |
|---|---|---|
| `provider` | `Postgres`, `MySql`, `MsSql`, `Oracle`, `SQLite` or `Db2`. | required |
| `connectionString` | The connection string the database is read with: the value, `env:NAME` or `file:path`. | required |
| `schemaConnectionString` | The connection the script runs on with `--apply`. | the connection string |
| `appAssembly` | The assembly holding the application class. | required |
| `appClass` | The full name of the `EntityApp` class. | the one class in the assembly |
| `providerPath` | The folder the provider is loaded from. | the application assembly's folder |
| `dropUnknownTables` | Whether the script drops tables the model does not declare. | `false` |
| `ignoreDbObjects` | Patterns of the objects the script neither drops nor changes; the same values the host's `DbSettings.IgnoreDbObjects` has. | none |
| `options` | The `DbOptions` flags the model is mapped with, separated by commas; the same values the host's `DbSettings.Options` has. | `UseRefIntegrity` |
| `output` | The script file. | `upgrade.<provider>.sql` beside the config |

## Run

```bash
mic-db upgrade Shop/mic-db/upgrade.json
mic-db upgrade Shop/mic-db/upgrade.json --connection env:PROD_CONN --output deploy/prod-2026-11-01.sql
```

1. The assembly is loaded, the application constructed through its parameterless constructor, and `Init()`
   run. A model that fails to build stops the job with the build report.
2. The provider is loaded and the database read. Its schema is compared with the model as a connect compares
   it. Nothing is changed.
3. The script is written. Its header says when it was made, from which application and version, for which
   server, what the database declares itself to be and at which version. The warnings follow, then the
   statements stage by stage. A script that loses data says so at the top and counts the destructive steps.
   When nothing differs, the file says so.
4. The report's messages are printed.

| Exit code | Meaning |
|---|---|
| 0 | Done. |
| 1 | The job failed: a missing file, an assembly built against another `Mic.Orm`, a database that cannot be reached. |
| 2 | The report holds errors, or `--apply` did not apply. |

## Applying

Run the script with the server's own tool (`psql`, `sqlcmd`, `mysql`, `sqlplus`, `sqlite3`), under a login with
DDL rights. The script's last statement records the upgrade in the `DbInfo` row: the application's name and
version, the module versions and the server's time. Afterwards run `mic-db upgrade` again; the file should say
the database matches the model. The application's next connect finds no differences. A database the script
created has no row for the statement to update; that connect writes it.

`--apply` makes the tool run the upgrade itself:

```bash
mic-db upgrade Shop/mic-db/upgrade.json --apply
```

With `--apply`, and only when the script is not empty, the tool connects the application to the database with
the upgrade mode forced to `Always`. The connect reads the database again under the upgrade lock and runs what
that comparison finds. The tool then writes the file again from the run: the file holds the script that ran, with
a header line saying it was applied. It has no statement for the `DbInfo` row; the connect wrote the row. A run
that fails part-way leaves the script it attempted, with the error.

## The script

The statements run outside a transaction, in the stage order the upgrade uses: drops of dependent objects,
renames, additions, alterations, keys and constraints, views and functions, destructive drops. A failure part-way
leaves the earlier statements applied; the next comparison produces the remaining work. A data migration, such
as filling a new column from old ones, is added to the script by hand between the stages it belongs to. An index
on a large table is built online by changing its statement by hand; see
[How do I add an index to a large table?](how-to.md#how-do-i-add-an-index-to-a-large-table).
