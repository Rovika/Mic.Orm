# Array columns on PostgreSQL

PostgreSQL has array column types: `integer[]`, `text[]`, `double precision[]`. A column of one holds a list of
values in the row itself, the tags of a product or the readings of a sensor, with no second table. MIC ORM maps
a member that is an array or a list of scalars to such a column, reads and writes it as the collection, and
queries its items. It does so on PostgreSQL only.

## An array member

```csharp
[Entity]
public interface ISensor {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(30)] string Name { get; set; }
  int[] Counts { get; set; }
  List<string> Tags { get; set; }
  double[]? Samples { get; set; }
}
```

A member typed `T[]` or `List<T>` is an array column where `T` is one of these:

| Item type | Column |
|---|---|
| `int`, `long`, `short` | `integer[]`, `bigint[]`, `smallint[]` |
| `double`, `float`, `decimal` | `double precision[]`, `real[]`, `numeric[]` |
| `bool` | `boolean[]` |
| `string` | `text[]` |
| `Guid` | `uuid[]` |
| `DateTime`, `DateOnly` | `timestamp[]`, `date[]` |

A nullable member, `double[]?`, is a nullable column. A `byte[]` is a binary value, not an array of bytes, as
on every server. `IList<T>` is not an array member: it declares a list of entities.

## Reading and writing

The member is read and written as a whole:

```csharp
var sensor = session.NewEntity<ISensor>();
sensor.Name = "north";
sensor.Counts = [3, 1, 2];
sensor.Tags = ["roof", "wind"];
await session.SaveChangesAsync();
```

To change the items, assign the member a new array or list. The session sees an assignment; it does not watch
the items of the array it holds, so `sensor.Counts[0] = 100` alone saves nothing.

## Queries

A query reads the items of the array:

```csharp
// Sensors that have an item equal to a value, or an item a condition takes.
var roofed = await session.EntitySet<ISensor>().Where(s => s.Tags.Contains("roof")).ToListAsync();
var high = await session.EntitySet<ISensor>().Where(s => s.Counts.Any(c => c > 5)).ToListAsync();

// The number of items, as a method or a property.
var sizes = await session.EntitySet<ISensor>()
  .Select(s => new { s.Name, Items = s.Counts.Length, Odd = s.Counts.Count(c => c % 2 == 1) })
  .ToListAsync();

// The items spread, one row each, beside the row they belong to.
var items = await session.EntitySet<ISensor>()
  .SelectMany(s => s.Counts, (s, c) => new { s.Name, Item = c })
  .ToListAsync();
```

`Contains`, `Any`, `Count()` and `SelectMany` translate, and so do `Length` of an array and `Count` of a list.
An item by its position, `s.Counts[0]`, does not translate. The statement reads the items as rows, with
`unnest`, so an index over the array column does not serve these queries.

## An existing database

`mic-db entities` writes an array column as an array member, `int[] Counts`, nullable where the column is. See
[Generating entities from a database](tool-entities.md).

## The other servers

On any other server the member has no column type. The connect fails there: the model cannot be mapped, and the
error names the member and `[Json]`. A model that has to run on several servers holds the list as a JSON
document instead:

```csharp
[Json] List<string> Tags { get; set; }
```

That member is a JSON column on every server, `jsonb` on PostgreSQL, and it answers the same queries. Db2
stores and reads it and does not query its items. See [JSON](json.md).
