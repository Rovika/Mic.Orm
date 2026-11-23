# Vector search

AI applications work with vectors: an embedding model turns a text, an image or a product into an array of
several hundred to a few thousand numbers, and things of similar meaning get arrays that are close. Database
servers support this with a vector data type. MIC ORM gives access to it: an application writes vectors and
queries them for the nearest ones, such as the passages nearest to a user's question or the products most like
the one on the screen.

The vector type is a column of a fixed number of dimensions, a distance between two vectors, and on some
servers an index that finds the nearest ones. PostgreSQL has it with the pgvector extension, SQL Server from
version 2025. MIC ORM provides one attribute and one function over them, so the vectors stay beside the
application's other data and one query filters and searches.

## A vector member

A vector is a `float[]` member marked `[Vector]` with its number of dimensions, the length of the array the
embedding model returns:

```csharp
[Entity]
public interface IPassage {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(50)] string Source { get; set; }
  [Unlimited] string Text { get; set; }
  [Vector(1536), Index] float[] Embedding { get; set; }
}

var passage = session.NewEntity<IPassage>();
passage.Source = "manual";
passage.Text = text;
passage.Embedding = embedding;   // what the model returned for the text
await session.SaveChangesAsync();
```

`[Vector]` is in `Mic.Orm`. The server refuses a value of another length at the save. A `float[]` member with
no `[Vector]` is an array member; see [Array columns on PostgreSQL](postgres-arrays.md). MIC ORM does not make
an embedding: the application calls its model and stores what comes back.

| Server | Column | What it needs |
|---|---|---|
| PostgreSQL | `vector(n)` of pgvector | The extension installed on the server. The upgrade creates it in the database before the first vector column. |
| SQL Server 2025 | `vector(n)` | A provider made for that version: `new SqlServerProvider(SqlServerVersion.Sql2025)`. |

On MySQL, Oracle, SQLite and Db2 a vector member is an error of the model.

A vector of 1536 dimensions takes 6 KB of its row, and a query that returns the entity returns the vector with
it. To keep the vector out of the read, mark the member `[OnDemand]`: a query measures by it all the same, and
the vector is loaded when the code reads it. See [On-demand members](on-demand-members.md).

## The nearest rows

A query measures with `VectorFunctions.Distance(vector, other)`, in `Mic.Linq`. A smaller distance is a nearer
vector. To read the nearest rows, order by the distance and take a page:

```csharp
float[] question = …;   // the embedding of what the user asked

var nearest = await session.EntitySet<IPassage>()
  .Where(p => p.Source == "manual")
  .OrderBy(p => VectorFunctions.Distance(p.Embedding, question))
  .Take(5)
  .Select(p => new { p.Text, Distance = VectorFunctions.Distance(p.Embedding, question) })
  .ToListAsync();
```

`Take` comes before `Select`: the page is taken from the ordered rows and projected after. The distance goes in
a filter too, `Where(p => VectorFunctions.Distance(p.Embedding, question) < 0.3)`. In
[QueryMaker](querymaker.md) the function is `VectorDistance`.

The server measures the distance of a query. A row whose vector is null has no distance; ordered by it, the
row comes last on PostgreSQL and first on SQL Server. In plain code the method returns the cosine distance.

## The metric

The distance is measured by the metric of the vector member in the call. The member states it on its
attribute, `[Vector(1536, Metric = VectorMetric.Euclidean)]`; with nothing stated it is the cosine metric. Use
the one the embedding model's documentation names.

| `VectorMetric` | The distance | PostgreSQL | SQL Server |
|---|---|---|---|
| `Cosine` | One minus the cosine of the angle between the vectors: 0 for the same direction, 1 for a right angle, 2 for the opposite | `<=>` | `VECTOR_DISTANCE('cosine', …)` |
| `Euclidean` | The length of the straight line between the vectors | `<->` | `VECTOR_DISTANCE('euclidean', …)` |
| `DotProduct` | The dot product of the vectors, negated, so that smaller is nearer | `<#>` | `VECTOR_DISTANCE('dot', …)` |

## The index

`[Index]` on a vector member asks for the server's vector index.

On PostgreSQL it is an HNSW index of the member's metric, with pgvector's default options. It finds the nearest
rows approximately: fast, and it may leave out a row an exact search returns. It serves one form of query: the
ordering by the distance, ascending, with `Take`. A filter on the distance alone and `OrderByDescending` are
answered by reading the table. pgvector indexes a vector of up to 2,000 dimensions. When the member's metric
changes, the next upgrade rebuilds the index.

On SQL Server no vector index is created. The server's own is a preview feature in 2025, and a table under it
accepts no writes. `[Index]` on a vector member is a warning of the model there, and a query measures every row
its filter leaves.

### A filter beside the index on PostgreSQL

With a filter beside the ordering, the index hands over its nearest candidates, 40 by default, and the filter
keeps some of them, so a page can come back short. pgvector's iterative scan, from its version 0.8, reads on
until the page is full. It is a setting of the connection, which the application sets with
[direct SQL](direct-sql.md) inside its transaction:

```csharp
await session.BeginTransactionAsync();
await session.RunSqlNonQueryAsync("SET LOCAL hnsw.iterative_scan = strict_order");
var page = await session.EntitySet<IPassage>()
  .Where(p => p.Source == "manual")
  .OrderBy(p => VectorFunctions.Distance(p.Embedding, question))
  .Take(50)
  .ToListAsync();
await session.CommitTransactionAsync();
```

## Entities from an existing database

[`mic-db entities`](tool-entities.md) writes a pgvector column as a `[Vector(n)] float[]` member, with the
metric of the HNSW index over it. A `vector` column with no dimensions is left out with a comment. On SQL
Server the tool leaves a `vector` column out with a comment.
