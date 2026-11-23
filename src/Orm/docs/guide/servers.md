# Database server specifics

MIC ORM runs on PostgreSQL, SQL Server, MySQL, Oracle, SQLite and Db2, through one provider each. The model, the
queries and the schema upgrade are the same on all of them. What differs is on this page; Db2 has a page of its
own, [MIC ORM on an existing Db2 database](existing-db2-database.md), with its limits.

## Guid keys

The ORM generates Guid keys in the application, ordered by time. SQL Server orders a Guid by its last six bytes
first and the other servers from the first byte, so the time sits in another place on SQL Server:

| Server | Default scheme |
|---|---|
| PostgreSQL, MySQL, Oracle, SQLite, Db2 | `V7`, the millisecond in the first six bytes |
| SQL Server | `V8SqlServer`, the millisecond in the last six bytes |

[Guid generation](guid-generation.md) has the layouts, the settings and how the time is read out of a key.

## Unlimited text and binary on MySQL

On MySQL an `[Unlimited]` member is a `longtext` or a `longblob` column, which hold 4 GB. MySQL's shorter types,
`text`, `mediumtext`, `blob` and `mediumblob`, read back as the same type, so the upgrade leaves a column made as
one of them as it is; `text` and `blob` stop at 65,535 bytes. Such a column is widened by hand, and the server
rebuilds the table:

```sql
ALTER TABLE `Note` MODIFY `Body` longtext NOT NULL;
```

## Binary members on Oracle

A `byte[]` member with a `[Size]` is the server's bounded binary type: `RAW(n)` on Oracle, `varbinary(n)` on SQL
Server and MySQL. PostgreSQL and SQLite have one binary type, `bytea` and `blob`, for every size. With no size,
or with `[Unlimited]`, the member is the unlimited type: `BLOB`, `varbinary(max)`, `longblob`.

On Oracle the size decides what a query can do. Oracle compares no `BLOB`: a query with `d.Hash == hash` fails
there with ORA-22848 where `Hash` has no size, and only a test for null, `d.Hash == null`, runs. With
`[Size(32)]` the column is a `RAW`, which is compared and indexed as on the other servers. A hash, a token or
any binary that is looked up by its value gets a size.

A `RAW(16)` on Oracle and a `binary(16)` on MySQL is also what a `Guid` is there. `mic-db entities` reads a
binary column of that size as a `Guid`, and of any other size as a `byte[]`.

## An `int` key on SQLite

SQLite marks no identity column. A primary key of one `INTEGER` column is the table's row id, and SQLite gives it
a value where the insert has none. An `[Identity]` member is such a column; so is a plain `[PrimaryKey] int Id`,
whose value the application gives. The two tables are the same in the database, and an upgrade reports no
difference between them.

## The SQLite driver

`SqliteProvider` is built and tested on `Microsoft.Data.Sqlite`. An application that has to use another ADO.NET
driver for SQLite derives from the provider and hands out that driver's connection and command:

```csharp
public class OtherDriverSqliteProvider : SqliteProvider {
  public override DbConnection CreateConnection(string connectionString) =>
    new System.Data.SQLite.SQLiteConnection(connectionString);
  public override DbCommand CreateDbCommand() => new System.Data.SQLite.SQLiteCommand();
}
```

This is not a supported configuration. Over `System.Data.SQLite` 1.0.119 most of the ORM works, and these parts
do not:

* A date bound as a parameter. A `DateOnly` is written in the current culture's format and does not read back;
  how a date and a time are stored is the driver's choice. A save of several rows writes its values into the
  statement and is not affected.
* The classification of a failure. A `DataAccessException` carries no `SubType`, so a unique violation is not
  told from another failure.
* The distance between two geographic points. Its function is registered on the Microsoft driver's connection.
* Full-text search. That package's SQLite has no FTS5 module; the derived provider also takes
  `DbFeatures.FullText` out of `Features` and returns no `IndexTypes`.
* Foreign keys. The provider adds `Foreign Keys=True` to the Microsoft driver's connection string; with another
  driver the connection string says it.

