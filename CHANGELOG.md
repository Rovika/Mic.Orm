# Changelog

## 0.10.0-preview.1, 2026-11-22

- A provider for Db2 on Linux, UNIX and Windows: the package `Mic.Orm.Db.Db2`, `--provider Db2` in the `mic-app`
  template, `Db2` as a provider of `mic-db`. For an older schema, `mic-db entities` writes a table with no
  primary key under a unique key. See
  [MIC ORM on an existing Db2 database](https://rovika.github.io/Mic.Orm/guide/existing-db2-database.html).
- Array columns on PostgreSQL: a member typed `T[]` or `List<T>` of a scalar is an array column, queried with
  `Contains`, `Any`, `Count()` and `SelectMany`; `mic-db entities` writes such a column as an array member. See
  [Array columns on PostgreSQL](https://rovika.github.io/Mic.Orm/guide/postgres-arrays.html).
- `Length` of an array and `Count` of a list in a query, over an array column and over a JSON array: the number
  of items.
- Vector search on PostgreSQL with pgvector and on SQL Server 2025: a `float[]` member marked `[Vector(n)]` is
  a `vector(n)` column, for an embedding. `VectorFunctions.Distance` in a query is the distance between two
  vectors, to order by for the nearest rows, to filter by and to select; its metric is the member's,
  `[Vector(n, Metric = …)]`: cosine, Euclidean or dot product. On PostgreSQL `[Index]` on the member is an HNSW
  index of its metric, and `mic-db entities` writes a vector column as the member. On SQL Server the provider
  is made for 2025, `new SqlServerProvider(SqlServerVersion.Sql2025)`, and no vector index is created. See
  [Vector search](https://rovika.github.io/Mic.Orm/guide/vector-search.html).
- On-demand members: a column member marked `[OnDemand]` is left out of every read of its entity, a query, a
  read by key, a list member, a reference's target, and is loaded when it is first read, in one statement for
  every entity of the type the session tracks that lacks it. A query that names the member reads its column as
  any other. `Include(a => a.Body)` on a query loads the member with the query's rows, and
  `session.LoadMemberAsync(article, a => a.Body)` loads it for an entity in hand, both without a blocking read.
  A read under a lock drops a loaded member that was not assigned, and its next read loads it under the lock.
  For a column that is large and seldom read: a text, a picture, a JSON document, an embedding. See
  [On-demand members](https://rovika.github.io/Mic.Orm/guide/on-demand-members.html).
- Change: the average of an integer member is a `double` with its fraction, on every server, and `AverageAsync`
  over an `int` or a `long` selector returns a `double`. It was an integer, or 0 from `AverageAsync`.
- Change: `ExecuteRowsAsync` returns its rows as `ProjectionData`, where it returned `IEntityData`. A value is
  read by name as before, `row.GetValue("Name")`; the names of a row are `row.Entity.Members`.
- Change: `DbServerKind`, `DbIndexType` and the other types that were in the namespace `Mic.Modeling.Db` are in
  `Mic.Orm.Db`, and `Severity` is in `Mic.Orm.Logging`. Replace `using Mic.Modeling.Db;` with
  `using Mic.Orm.Db;`; a file that `mic-db entities` generated earlier has that line. The `Mic.Orm` package no
  longer carries `Mic.Modeling.dll`.
- Change: `DbModelDefBuilder`, `DbModel.EntityModelMapping` and `LogEntry.ToLogItem()` are removed. They made
  documents for MIC's design surface, which will read a model another way.
- Fix: a foreign key column takes the database type its target key declares with `DbTypeSpec`. A reference to a
  `char(6)` key made a `varchar(6)` column.
- Fix: a driver's failure wrapped in an `AggregateException` is reported as a `DataAccessException`.
- Fix: a read under a lock refreshes an entity the session already tracks, in the members the application did
  not assign: a read by key with `LoadFlags.WriteLock` or `LoadFlags.ReadLock`, a query with
  `QueryOptions.LockForUpdate` or `QueryOptions.SkipLocked`. It answered the tracked entity as it was, so a save
  after it could be refused as stale.
- Fix: an entity read as a stub and changed without a load is saved. The save was refused for the required
  members the stub does not hold, and an assigned value equal to null or to the default of the member's type
  was not written.

## 0.9.0-preview.1, 2026-11-15

The first public preview: `Mic.Orm`, the providers for PostgreSQL, SQL Server, MySQL, Oracle and SQLite,
`Mic.DbTool` and `Mic.Templates`.
