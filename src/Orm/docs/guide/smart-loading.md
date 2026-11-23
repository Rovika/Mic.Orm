# Smart loading

[Reading data](linq-queries.md#lazy-loading-and-the-n1-problem) outlines the N+1 problem: a query for a list of
entities, then a loop that touches a related entity on each, runs one statement per entity. One solution is
`Include` on the query, which loads the related entities with the root query. It is possible when the extent of
the data tree the code will navigate is known in advance. A handler of a REST `GET` endpoint is the example: it
runs the query, gets the root entities and passes them to the entity-to-DTO mapper, which converts the entity
tree into the output shape. The shape of the DTO tree is known when the handler is written, so every related
entity it needs is preloaded at query time.

For an endpoint whose output shape is set by the request, a GraphQL endpoint for one, that approach does not
work. The shape of the returned data is not known in advance; the selection tree of the query defines it. The
related entities are then loaded lazily, as the code touches them, and smart loading is what makes the lazy
loads efficient: it is a **smart lazy load**. It is on by default, needs no instruction in the code, and the loop is
written as it is.

## The mechanism

Smart loading is three hooks inside the [lazy loading](linq-queries.md#reading-related-entities-lazy-loading),
one per kind of member. Each adds one step to the ordinary load: before the session loads the one member that
was touched, it looks for every other entity it tracks that is in the same state, and loads the member for all
of them in one statement.

**A reference.** A query brings 100 orders; each order's `Customer` is a stub, an entity with its key only. The
loop touches `Name` on the first customer. Before loading that one row, the session collects every other
customer stub it tracks and loads them all in one statement by their keys. The 99 touches that follow find their
customer loaded.

```csharp
var orders = await session.EntitySet<IOrder>().Where(o => o.Total > 100).ToListAsync();
foreach (var order in orders)
  Console.WriteLine($"Customer: {order.Customer.Name}, total: {order.Total}");
```

Two statements run: the orders, then the customers of all the orders.

**A list member.** The loop touches `Orders` on the first customer. Before loading that one customer's orders,
the session collects every tracked customer whose list is not loaded and loads all the lists in one statement,
each list filled with its own rows. A one-to-many list and a many-to-many list work the same way.

```csharp
var customers = await session.EntitySet<ICustomer>().Where(c => c.Status == CustomerStatus.Active).ToListAsync();
foreach (var customer in customers)
  Console.WriteLine($"{customer.Name}: {customer.Orders.Count} orders");
```

**A query over an `IQueryable<T>` member.** A list member loads every related row on the first touch; on a
relation of thousands of rows that is the wrong instrument. An `IQueryable<T>` member in its place gives a
query the code filters and pages, with no list stored in the entity: see [`IQueryable<T>`
members](entity-members.md#iqueryablet-members). The loop runs a query over the member of the first customer.
The session rewrites it as one statement over all the tracked customers, with the answer per customer, and runs
that statement once. It keeps each customer's answer in an internal cache and returns the first customer's. The
customers that follow find their answer in the cache; no statement runs for them. The cache is per owner and
holds its last eight answers for as long as the owner lives.

```csharp
foreach (var customer in customers) {
  var bigOrders = await customer.OrderQuery.Where(o => o.Total > 1000).CountAsync();
  Console.WriteLine($"{customer.Name}: {bigOrders} big orders");
}
```

One statement counts the big orders of every customer, grouped by customer.

An [on-demand member](on-demand-members.md) loads the same way: the first read of the member loads it for every
tracked entity that does not have it.

## The bounds

One statement takes up to 500 owners; past the cap the rest load as they are touched, 500 at a time. An entity
held in the [data cache](data-cache.md) is read from the cache and not from the server. A reference whose
target the session tracks already is not read at all. A new or a deleted entity is left out of the batch.

A query's `Include` loads its related entities through the same statements, one for all the stubs and one for
all the owners' lists. A query with includes and a loop without them cost the same number of statements; they
differ in when the rows are read, with the query or on the first touch. See [Using the `Include` query
option](linq-queries.md#using-the-include-query-option).

## Switching it off

A session opened with `EntitySessionOptions.DisableSmartLoad` loads a member for one entity at a time, as the
code touches it. The option is set when the session is opened, or on an open session for the requests that
follow:

```csharp
var session = context.OpenSession(EntitySessionOptions.DisableSmartLoad);
session.SetOptions(EntitySessionOptions.None);
```

The option changes how many statements run, never what the code sees.
