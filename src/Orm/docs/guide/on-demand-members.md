# On-demand members

An entity often has a member far larger than the rest of its row: the text of an article, a picture, a JSON
document, an embedding. Most of the code that reads the entity does not need it. A page that lists fifty
articles shows a title and a date, and the read brings fifty texts with them. A projection of the title and the
date leaves the text out, but its rows are not entities: there is nothing in them to change and save.

MIC ORM leaves such a member out of the read with one attribute. A member marked `[OnDemand]` stays on the
entity; no read of the entity brings its column, and the member is loaded when the code first reads it. This
page covers the declaration, the load on the first read, the calls that load the member ahead of the read, and
the save.

## An on-demand member

```csharp
[Entity]
public interface IArticle {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Title { get; set; }
  DateTime PublishedOn { get; set; }
  [OnDemand, Unlimited] string Body { get; set; }
  [OnDemand] byte[]? Picture { get; set; }
}
```

`[OnDemand]` is in `Mic.Orm`. It goes on a member that maps to a column: a text, a binary, a JSON document, a
vector. Every read that returns the entity leaves the column out: a query, a read by key, a list member, the
target of a reference.

```csharp
var articles = await session.EntitySet<IArticle>()
  .Where(a => a.PublishedOn >= since)
  .ToListAsync();   // reads Id, Title and PublishedOn
```

A query that names the member reads its column as any other. `Select(a => a.Body)` returns the texts, and a
condition or an ordering over the member runs on the server.

## The load on the first read

```csharp
foreach (var article in articles)
  Console.WriteLine($"{article.Title}: {article.Body.Length} characters");
```

The first read of `Body` loads it. Under [smart loading](smart-loading.md), which is on by default, the
statement loads the text of every article the session tracks that does not have it yet. The query and the loop
run two statements: the articles, then their texts.

The statement reads the key and the one member. Another on-demand member of the entity has a load of its own:
reading `Body` does not bring `Picture`. In a session opened with `EntitySessionOptions.DisableSmartLoad` the
member is loaded for one entity at a time.

A load from a getter is synchronous, as every [lazy load](linq-queries.md#reading-related-entities-lazy-loading)
is: it runs on the calling thread.

## Loading the member ahead of the read

A server application must not hold a thread while the database answers. Two calls load the member
asynchronously, before the code reads it.

`Include` on a query loads the member with the query's rows:

```csharp
var articles = await session.EntitySet<IArticle>()
  .Where(a => a.PublishedOn >= since)
  .Include(a => a.Body)
  .ToListAsync();
```

The texts are read by a second statement, by the keys of the rows the query returned, as every include runs:
see [Using the `Include` query option](linq-queries.md#using-the-include-query-option).
`Include(a => new { a.Body, a.Picture })` names several members, a statement for each. An on-demand member may
end a chain: on a query of comments, `Include(c => c.Article.Body)` loads the articles and then their texts.
`session.AddInclude<IArticle>(a => a.Body)` adds the include to every query over `IArticle` in the session.

`LoadMemberAsync` loads the member of an entity the code holds:

```csharp
var article = await session.GetEntityAsync<IArticle>(id);
await session.LoadMemberAsync(article, a => a.Body);
var text = article.Body;   // loaded already
```

It is the load of the getter, awaited: under smart loading it loads the member for the other articles the
session tracks too. A call for an entity that has the member runs no statement. The method takes an on-demand
member only; any other is an `ArgumentException`.

Awaiting an entity, `await comment.Article`, loads its row without the on-demand members.

## Changing and saving

A new entity has all its members, and the insert writes them all. An update writes the members the code
assigned; an on-demand member is in it only when it was assigned. Assigning the member takes no load:
`article.Body = text` is written at the save, and the old text is never read.

A JSON document changed in place is reported with `EntityHelper.MarkModified`, as for any JSON member: see
[A JSON member](json.md#a-json-member).

A read under a lock refreshes an entity the session tracks from its row, and the row does not carry an
on-demand member. A member loaded before the lock and not assigned is dropped by that read, and its next read
loads it under the lock. See [Locking a single row](locking.md#locking-a-single-row).

## Members that cannot be on demand

`[OnDemand]` is an error of the model, reported at [startup](startup.md#startup-errors), on:

* a member that is not a plain column: a reference, a list, a `[Computed]` member, a `[NoColumn]` member;
* a member of the primary key or of a unique key;
* a member declared for the key column of a reference, `Guid Customer_Id` beside `ICustomer Customer`;
* the `[RowVersion]` member;
* a member of an entity with no primary key;
* a member of a cached entity, whose rows the [data cache](data-cache.md) keeps whole.
