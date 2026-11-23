# Feature highlights

The features in short. The guide pages linked from each have the details.

### Entities are interfaces

An entity, the C# image of a table, is an interface. That is an unusual choice for an ORM, with strong reasons behind
it: the interface exposes the typed members of a data row and hides the mechanics behind them; the implementing class
is emitted at startup. See [Application data model](data-model.md).

### The data model is split into functional modules

The entity model has two grouping layers: entities are grouped into modules, together with the functionality that
belongs with them, and modules are joined into an application. The structure keeps a very large model manageable, the
thousands of tables of a real-world database. See [Application data
model](data-model.md#modules-and-the-application).

### Entity tracking

The standard facility of a full-featured ORM. MIC ORM tracks every loaded entity and every change made to it, and
does so efficiently. See [Core runtime objects](core-objects.md#entity-tracking).

### Transactions and saves

A save writes all applied changes in one transaction, orders the updates by the references automatically, and sends
them in batch mode, one round trip to the server. See [Updating data](saving-changes.md).

### Locking records

To change a record that other operations may change at the same time, lock it on the read: a read by key or a
query holds its rows until the transaction ends, and the session begins and commits the transaction where the
caller opens none. A consistent-snapshot read never waits on a writer. One model on every server, from `FOR
UPDATE` to `WITH (UPDLOCK)`. For the optimistic alternative, a row version member refuses a stale save. See
[Locking records](locking.md).

### A full LINQ engine for querying data

And not only querying: update, insert and delete statements over a group of rows are written as LINQ expressions too.
Includes and row locking are supported. See [Reading data](linq-queries.md) and [Set-based
updates in LINQ](saving-changes.md#set-based-updates-in-linq).

> [!NOTE]
> *The engine is a library of its own, `Mic.Linq`, with no dependency on the ORM core: it translates expressions
> over an abstract data model and knows no database server, so it is provider-independent by construction.*

### Multiple database servers

A single data model expressed in C# runs on different database servers, from SQLite to Oracle. The server is chosen
at startup. See [Application startup](startup.md).

### Data cache, built into the LINQ engine

Caching improves application performance and greatly reduces the load on the database server. To keep a small,
slowly changing table in memory, mark the entity as cached; every LINQ query over it then runs against the in-memory
data automatically, with no change to the query. See [Data cache](data-cache.md).

### Query cache

LINQ translation is an expensive operation, so MIC ORM caches translated queries and reuses a previous translation
through an efficient scan-and-lookup mechanism. See [Reading data](linq-queries.md#linq-queries).

### Full-text search

Direct support for the full-text search capabilities of the database servers: one model for building full-text
queries, translated automatically into the dialect of the target server, and the dialects vary substantially. See
[Full-text search](full-text.md).

### JSON

Direct support for the JSON capabilities of the database servers: a member stored as a JSON document, typed
documents, a value of the document as a computed column. See [JSON](json.md).

### Geographic points

Direct support for the geographic types of the database servers: a `GeoPoint` member stored in the server's
type, its spatial index, and a distance function translated to each server's own, for "within a radius" and
"nearest first" queries. See [Geographic points](geo-points.md).

### Vector search

Direct support for the vector types of PostgreSQL with pgvector and of SQL Server 2025: an embedding stored in a
`[Vector]` member, a distance function translated to each server's own for "nearest first" queries, and
pgvector's HNSW index. See [Vector search](vector-search.md).

### Code first, automatic schema maintenance

MIC ORM, like VITA before it, has a unique method of maintaining the database schema of an application under active
development: the schema follows the C# code. There are no separate migration files to apply. At startup the engine
compares the database schema with the entity model and changes the schema to match. The mechanism is optional, and a
great benefit to team development: developers change the C# code and merge it, and the application updates the target
database on connection, be it local, a shared staging or a development environment. See [Maintaining the database
schema](database-model.md).

### Secondary database objects

Schemas, indexes, sequences, views and more. MIC ORM gives direct access to the server's facilities, and lets them be
defined in C# code, so the code-first upgrade creates them. Attributes express indexes, constraints and the other
artifacts, so every aspect of the database is maintained through C# code alone. See [Database
objects](database-objects.md) and [C# attributes for modeling](attributes.md).

### Temp tables

To collect the ids several queries find, or to query over a large list of values, declare a temp table as an
entity: the session fills it from queries or from objects and joins it in LINQ like any entity set. See
[Temp tables](temp-tables.md).

### Database first

An easy start with a large existing database: the `mic-db` tool generates the entity definitions for every table in
the database, and the database can be queried in minutes. See [Generating entities from a
database](tool-entities.md).

### QueryMaker

A query built as an abstract tree, with no reference to entity types: entities and members are named by strings.
It serves generic facilities such as a search or filter builder in the UI, where the client sends a names-only
sketch of the query and the server rebuilds it from the sketch and runs it. See [QueryMaker](querymaker.md).

### Smart loading, the N+1 problem solved at the ORM level

A feature of MIC ORM alone. To load a large data tree spanning several related tables, query the root entities and
read the rest: MIC ORM detects when related data should be loaded and how much of it, and loads it in batches, so
the data access is automatic and efficient. `Include`, the answer in other ORMs, is much weaker and narrower: the
code must know at query time what it will touch and say so on every query, so an output shape set by the request,
a GraphQL endpoint, is out of its reach. Smart loading needs no instruction and covers both; `Include` remains
available. See [Smart loading](smart-loading.md).

### Application extensions

A facility for adding cross-cutting behavior across the application. For example, multi-tenancy: the current tenant's
filter is applied automatically to every queried table. Two extensions ship out of the box, multi-tenancy and soft
delete. You can write your own. See [Application extensions](app-extensions.md).
