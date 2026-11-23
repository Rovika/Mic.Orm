# Reading data

There are several ways to read data in MIC ORM:

* A single entity, loaded by its primary key.
* A related entity or an entity list, read as a member of an entity already loaded. This is lazy loading.
* A LINQ query.

This page covers the three, then the N+1 problem and the `Include` option that answers it. The objects behind the
reads, the context and the session, are on [Core runtime objects](core-objects.md).

## Loading by primary key

```csharp
// One query; null when no row has the key.
var customer = await session.GetEntityAsync<ICustomer>(customerId);

// Returns the tracked entity if it was already loaded in the session; otherwise, returns a stub.
var stub = session.GetEntity<ICustomer>(customerId, LoadFlags.Stub);

// The row locked until the transaction ends.
var locked = await session.GetEntityAsync<ICustomer>(customerId, LoadFlags.WriteLock);

// A composite key.
var leg = await session.GetEntityAsync<ITripLeg>(session.CreatePrimaryKey<ITripLeg>(tripId, 2));
```

A stub is an entity with only its primary key set. Its row is loaded when any member other than the key is
touched.

The `LoadFlags` values:

| `LoadFlags` | Meaning |
|---|---|
| `Default` | The tracked entity, or one query. `null` when no row has the key. |
| `Stub` | The tracked entity, or an entity holding only its key. The row is read when another member is read. No query now. |
| `SkipEntityCache` | Read the server even when the entity is cached. |
| `ReadLock`, `WriteLock` | A consistent-snapshot read, or a row held for update until the transaction ends. See [Locking records](locking.md). |

## Reading related entities: lazy loading

A related entity is a member of an entity already loaded, and it is loaded when the member is touched:

```csharp
var order = await session.GetEntityAsync<IOrder>(orderId);
var name = order.Customer.Name;
```

`order.Customer` does not load the customer from the database. If the customer with that `Id` is tracked in the
session already, it is returned; otherwise a stub is created and returned. Reading `Name` on the stub loads the
customer in full. This is the lazy loading pattern: a load on demand, on the first touch. A load from a getter
is synchronous: it runs on the calling thread with the driver's synchronous calls.

An assignment of a reference therefore runs no load and no query either:

```csharp
order2.Customer = order1.Customer;
```

Nothing is loaded; the foreign key is copied.

A list member loads the same way, a one-to-many list and a many-to-many list alike:

```csharp
// Nothing is read yet.
var orders = customer.Orders;

// The orders are loaded here.
var count = orders.Count;

// A many-to-many list, loaded on the first touch too.
var tags = product.Tags;
```

The list object is returned at once, and its rows are read on the first touch: a count, an element, an
enumeration.

A server application must not hold a thread while the database answers. It awaits the entity or the list, and the
same load runs asynchronously:

```csharp
using Mic.Orm.Awaiting;

var customer = await order.Customer;   // loads the stub and returns the customer
var orders = await customer.Orders;    // loads the list and returns it
```

An entity or a list already loaded completes at once, and a reference that is null awaits to null. The `using`
goes in each file that awaits an entity: the methods are on every reference type, so in such a file an object
that is no entity also compiles under `await`, and is refused when it runs.

Navigating the data tree through the members that reference other entities is a convenient way to reach the data
the code needs. On a list of entities, however, it leads to the known N+1 problem: see [Lazy loading and the N+1
problem](#lazy-loading-and-the-n1-problem).

## LINQ queries

`EntitySet<T>()` is an `IQueryable<T>` over the entity's rows. Write the query with the standard LINQ operators;
it is translated to SQL and runs when a terminal operator is called. The async forms of the terminal operators,
`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync` and the rest, are in `Mic.Linq`.

```csharp
var recent = await session.EntitySet<IOrder>()
  .Where(o => o.Customer.Name.StartsWith("A") && o.Status == OrderStatus.Open)
  .OrderByDescending(o => o.PlacedOn)
  .Skip(20).Take(10)
  .ToListAsync();
```

The query returns entities, and the session tracks them: see [Entity
tracking](core-objects.md#entity-tracking). A reference in a query, `o.Customer.Name`, becomes a join in the
SQL.

To read some values and not whole entities, project them with `Select`; to total them, group the rows:

```csharp
var summary = await session.EntitySet<IOrder>()
  .Select(o => new { o.Id, Customer = o.Customer.Name, o.Total })
  .ToListAsync();

var perCustomer = await session.EntitySet<IOrder>()
  .GroupBy(o => o.Customer.Id)
  .Select(g => new { CustomerId = g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) })
  .ToListAsync();
```

The rows of a projection are not entities and are not tracked. An optional reference selected whole,
`Select(o => o.Approver)` or a slot of a `new { … }`, is null where the row points at nothing.

A key of several values is written as a `new { … }` and read by member. The groups are filtered and ordered, by
the key or by an aggregate, before the projection:

```csharp
var perCustomerAndStatus = await session.EntitySet<IOrder>()
  .GroupBy(o => new { Customer = o.Customer.Name, o.Status })
  .Where(g => g.Count() > 1)
  .OrderBy(g => g.Key.Customer).ThenBy(g => g.Key.Status)
  .Select(g => new { g.Key.Customer, g.Key.Status, Count = g.Count() })
  .ToListAsync();
```

Such a key is read by member only: `g.Key` selected whole is refused.

A grouping with no projection over it returns the groups:

```csharp
IReadOnlyList<IGrouping<OrderStatus, IOrder>> byStatus = await session.EntitySet<IOrder>()
  .GroupBy(o => o.Status)
  .ToListAsync();
```

Each group has its key as written and the entities as its rows, tracked by the session. This grouping is done
in memory: the statement selects every row, and the rows are grouped after it. To have the server group and
count, project the groups as above.

A condition can use a list member of the entity, or a list of values the application holds:

```csharp
var busy = await session.EntitySet<ICustomer>()
  .Where(c => c.Orders.Count() > 10)
  .ToListAsync();

var some = await session.EntitySet<ICustomer>()
  .Where(c => ids.Contains(c.Id))
  .ToListAsync();
```

A list member, `c.Orders`, becomes a correlated subquery. A value from outside the query, such as `ids` or a
local variable, becomes a parameter; a list of any length is one parameter, as [How do I run a query over a list
of ids?](how-to.md#how-do-i-run-a-query-over-a-list-of-ids) says. A `byte[]` is one binary value and not a list
of bytes, as a string is one text: `Where(d => d.Hash == hash)` binds it whole. The translation of a query shape
is cached; the next call with other values reuses the SQL.

A join can be written explicitly, in the query syntax:

```csharp
var lines = await (
  from line in session.EntitySet<IOrderLine>()
  join product in session.EntitySet<IProduct>() on line.Product.Id equals product.Id
  where product.Name == "Widget"
  select new { line.Order.Id, line.Quantity })
  .ToListAsync();
```

An entity in a condition is compared by its key. That holds for a reference against an entity the application
holds, `o.Customer == customer`; for two rows of the query, `a != b`; and for the row of an outer join against
null, `where product == null`. The key of a join is read the same way: `on line.Product equals product` is the
join above, on the foreign-key column, and an entity may be one part of a `new { … }` key. A row with no primary
key, which a view's may be, has nothing to be compared, joined or grouped by: such a query is refused, naming
the entity, and is written over the row's members.

A variable that holds null is compared as `null` written out. With `DateOnly? when = null`,
`Where(o => o.ShippedOn == when)` finds the orders that have no date, and `!=` the orders that have one; the
same query with a date in `when` compares the dates. The same goes for an entity variable that holds null,
`o.Approver == approver`. Against a value, `!=` is the server's: an order whose `ShippedOn` is null is left out
of `Where(o => o.ShippedOn != when)`.

A conditional operator goes into the statement as a `CASE`. It stands wherever a value does: selected, in a
condition, as an ordering term, as a value a set-based update assigns, and as the term of an aggregate:

```csharp
var bulkTotal = await session.EntitySet<IOrderLine>()
  .Where(l => l.Order == order)
  .SumAsync(l => l.Quantity > 10 ? l.Quantity * l.UnitPrice : 0m);
```

Each of its two results is one value. A conditional that chooses between two entities or two `new { … }` is
refused; write one conditional for each member.

### Query options

To change how a query runs, pass a `QueryOptions` value to `EntitySet<T>()`:

```csharp
var found = await session.EntitySet<IProduct>(QueryOptions.BypassQueryCache)
  .Where(p => p.Name.StartsWith(text))
  .ToListAsync();
```

| `QueryOptions` | Meaning |
|---|---|
| `SkipEntityCache` | The query goes to the server even when its entities are cached. See [Data cache](data-cache.md). |
| `LockForUpdate` | The rows are read under an update lock (`FOR UPDATE`, `WITH (UPDLOCK)`), inside a transaction. See [Locking records](locking.md). |
| `SkipLocked` | The rows are locked as with `LockForUpdate`, and the rows another transaction holds are skipped. See [A table as a queue](locking.md#a-table-as-a-queue). |
| `BypassQueryCache` | The query is translated each time and leaves no entry in the query cache. For a query that will not repeat, such as a user's search with its own combination of conditions. |

## Lazy loading and the N+1 problem

Navigating the data tree through the members that reference other entities is a convenient way to reach the data
the code needs. On a list of entities, however, it leads to the known N+1 problem:

```csharp
var orders = await session.EntitySet<IOrder>().Where(o => o.Total > 100).ToListAsync();
foreach (var order in orders)
  Console.WriteLine($"Customer: {order.Customer.Name}, total: {order.Total}");
```

Written this way, the loop runs N+1 queries: one for the list of orders, then one for the customer of each
order, as `Name` is touched on each customer's stub. A hundred orders cost a hundred and one queries.

MIC ORM answers the problem in two ways:

* `Include` on the query loads the customers together with the orders. It is the answer when the expanse of the
  tree the code will explore is known when the query is written. The next section describes it.
* Smart loading, a different mechanism, more flexible, for the case when the expanse of the tree is not known in
  advance. See [Smart loading](smart-loading.md).

## Using the `Include` query option

`Include` loads related rows together with the query itself, before any member is read:

```csharp
var orders = await session.EntitySet<IOrder>()
  .Where(o => o.PlacedOn >= since)
  .Include(o => o.Customer)                 // a reference
  .Include(o => o.Lines)                    // a list
  .Include((IOrderLine l) => l.Product)     // a reference of the included lines
  .ToListAsync();
```

`Include(o => new { o.Customer, o.Lines })` names several members at once. `session.AddInclude<IOrder>(o =>
o.Customer)` adds an include to every query over `IOrder` in the session; `context.AddInclude<IOrder>(…)` to every
query of every session of the context. An include takes an [on-demand member](on-demand-members.md) too:
`Include(a => a.Body)`.

The engine builds no join for an include. Each included set is loaded by a separate query: the orders, then
their customers by key, their lines by the orders' keys, then the products of the lines. Entity Framework Core
joins the included sets into the query's own statement, and the result widens and multiplies with every include;
here the query's rows are read once, and every related set in one statement of its own.

An application extension adds a filter to every read of the entities it binds, with no instruction in the
query: `SoftDeleteExtension` hides the deleted rows, `MultiTenantExtension` keeps a read to the context's
tenant. A session that must see every row disables the extension: see [Disabling an extension on a
session](app-extensions.md#disabling-an-extension-on-a-session).

For a query composed at run time from names, see [QueryMaker](querymaker.md); for SQL text run through the
session, see [Direct SQL](direct-sql.md).
