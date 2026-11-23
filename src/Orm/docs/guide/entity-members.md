# Entity members and types

> [!NOTE]
> *Entity and member are the general ORM terms. In MIC ORM an entity is a C# interface, and its members are the
> interface's properties.*

A table's columns have the server's types; an entity's members have C# types, and the two must agree. The
application chooses the C# type that fits the value, and the column type follows from it on every server, with
attributes for what the type does not say: a string's length, a decimal's precision, a date's time zone. Some
members are more than a plain column: a value the ORM generates at the save, a value computed from other
members, a value kept in memory only. Beside the scalar members, an entity has its primary key, its references
to other entities and its lists of them. This page lists the member types with the column type each gives, the
attributes that shape a member, and the key, reference and list members.

## Member types and column types

A member of a scalar type maps to a table column. The type decides the column type, with these attributes
refining it:

| Member type | Column type |
|---|---|
| `string` | a character column of `[Size(n)]` characters, or of `EntityAppSettings.DefaultStringSize` (50) without it; `[Unlimited]` for unbounded text |
| `int`, `long`, `short`, `byte`, `bool`, `float`, `double` | the server's type of that width |
| `decimal` | a decimal of `[Column(Precision = p, Scale = s)]`, or of `EntityAppSettings.DefaultDecimalPrecision` and `DefaultDecimalScale` (18 and 4) |
| `Guid` | the server's uuid where it has one, otherwise a 16-byte binary |
| `DateTime` | a date and time; `[Utc]` keeps it in UTC on both sides, `[DateOnly]` makes it a date |
| `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset` | the server's date, time, interval and offset types |
| an enum | the enum's underlying integer |
| `byte[]` | unbounded binary; with `[Size(n)]`, the server's bounded binary type where it has one, see [Binary members on Oracle](servers.md#binary-members-on-oracle) |
| `GeoPoint` | the server's geographic point type; see [Geographic points](geo-points.md) |
| any serialisable type with `[Json]` | a JSON column; see [JSON](json.md) |
| `T[]` or `List<T>` of a scalar, on PostgreSQL | an array column; see [Array columns on PostgreSQL](postgres-arrays.md) |
| `float[]` with `[Vector(n)]`, on PostgreSQL and SQL Server 2025 | a vector column; see [Vector search](vector-search.md) |

A member whose type is another entity interface is a reference, a foreign-key column; a getter-only `IList<T>`
is a list with no column. Both are below: [Entity reference members](#entity-reference-members), [Entity list
members](#entity-list-members).

## Nullability and size

Nullability comes from the C# type: `string?` and `int?` are nullable columns, `string` and `int` are not. An
assembly compiled without nullable reference types says nothing, and then `[Nullable]` marks the strings and
references that may be null.

A string longer than its `[Size]` is an error at the save, not a silent truncation; `[Size(100,
SizeOptions.AutoTrim)]` asks for the trim instead.

## Setting the column type explicitly

The column type the ORM derives from a member's type fits most columns. Some need another: a two-letter code
kept as `char(2)`, a money amount with a precision the database already has, a case-insensitive text type the
server offers, a column whose name the database fixed long ago. The `[Column]` attribute sets these on the
member:

```csharp
[Entity]
public interface ICountry {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Column(DbTypeSpec = "char(2)")] string Code { get; set; }
  [Column(DbTypeSpec = "citext")] string Name { get; set; }
  [Column("gdp_usd", Precision = 20, Scale = 2)] decimal Gdp { get; set; }
  [Column(Default = "0")] int Population { get; set; }
}
```

* `Precision` and `Scale` set a decimal's; `Size` a string's or a binary's length, the same as `[Size(n)]`.
* `DbTypeSpec` names the column type outright, as the server spells it, with its arguments: `char(2)`,
  `numeric(18,4)`, `citext`. The ORM derives nothing for that member; the provider checks the spec at the
  connect, and a malformed spec or a type the server does not have is an error there.
* The member keeps its C# type, and the value is read and written as that type, so the spec is one the type
  converts to: a character type for a `string`, a numeric one for a `decimal`.
* A spec is the server's own. A model that carries one is written for that server; an application that runs
  on several servers leaves the type to the ORM.
* `[Column("gdp_usd")]` names the column; `Default` gives the column's default, an SQL expression.
* A string column is the server's Unicode type. A non-Unicode one is asked for by its spec, `varchar(50)` on
  SQL Server.

The `mic-db entities` tool writes `DbTypeSpec` for every column whose type the ORM would not have chosen, so a
model generated from an existing database keeps the database's types.

## The primary key

Every entity declares its primary key; nothing is inferred from a member named `Id`. On a member, the
attribute makes that member the key; on the interface, it names the members.

```csharp
[Entity]
public interface IOrderLine {
  [PrimaryKey] IOrder Order { get; set; }
  [PrimaryKey] int LineNo { get; set; }
  IProduct Product { get; set; }
  int Quantity { get; set; }
}

[Entity, PrimaryKey("Order,LineNo")]
public interface IOrderLine {
  IOrder Order { get; set; }
  int LineNo { get; set; }
  IProduct Product { get; set; }
  int Quantity { get; set; }
}
```

The two declarations are the same key. A member list is comma or space separated, and a `-` before a member
makes it descending: `"LastName -DateCreated"`.

`[Auto]` on a key has the ORM generate the value: a time-ordered Guid by default, so that a graph of new
entities can be wired up in memory before anything is saved; the layout follows the server, see
[Guid generation](guid-generation.md). `[Identity]` is a database-generated integer;
`[Sequence("shop.OrderNo")]` takes the next value of a sequence.

In the ORM the primary key is more than a database constraint. The session tracks its entities by the key: one
instance per entity and key value, and a query that brings a row with a tracked key returns the tracked
instance. See [Entity tracking](core-objects.md#entity-tracking).

## Entity reference members

A member whose type is another entity interface is a reference: a foreign-key column in this table, and
the referenced entity when the member is read.

```csharp
[Entity]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ICustomer Customer { get; set; }
  IEmployee? TakenBy { get; set; }
}
```

* The column is named after the member and the target's key member, joined by an underscore: `Customer_Id`.
  A target with a composite key gives one column per key member. `[EntityRef("CustomerId")]` names the
  column outright, for a database that exists already.
* `ICustomer` is a required reference, a `NOT NULL` column; `IEmployee?` is optional.
* The database enforces the reference with a foreign key, unless `[EntityRef(HasDbForeignKey = false)]`.
  `[CascadeDelete]` on the member makes it `ON DELETE CASCADE`.
* Reading `order.Customer` on a loaded order loads the customer, once, on the first read. For every order the
  same query brought, in one statement: see [Smart loading](smart-loading.md).
* Assigning a reference assigns the key: `order.Customer = customer` sets `Customer_Id` at the save, and works
  with a customer created in the same session and not yet saved.

An entity may reference itself (an employee's manager), and two members may reference the same entity (an
order's billing and shipping addresses).

## Entity list members

A getter-only `IList<T>` of an entity type is the other end of a reference: the one-to-many back from the
child's reference member.

```csharp
[Entity]
public interface ICustomer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  IList<IOrder> Orders { get; }
}
```

* The list has no column. `customer.Orders` is the orders whose `Customer` is this customer, loaded on the
  first read of the count, an item or an enumeration.
* Which reference backs the list is found from the types. When the child has two references to the same
  entity, `[OneToMany("Customer")]` names the one.
* `[OrderBy("PlacedOn")]` on the list, or on the child entity for every list of it, orders the rows.
* Adding a new child to the list points it at the owner; removing one clears its reference where the model
  allows a null there.

A many-to-many list goes through a link entity, named in the attribute:

```csharp
[Entity]
public interface IProduct {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  [ManyToMany(typeof(IProductTag))] IList<ITag> Tags { get; }
}

[Entity]
public interface ITag {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(40), Unique] string Name { get; set; }
}

[Entity, PrimaryKey("Product,Tag")]
public interface IProductTag {
  IProduct Product { get; set; }
  ITag Tag { get; set; }
}
```

`product.Tags` is the tags reached through `IProductTag`. Adding a tag to the list makes a link row at the
save; removing one deletes the link; adding and removing before the save writes nothing. A link entity with a
position member keeps the list's order with `[PersistOrderIn("Position")]` on the list.

## `IQueryable<T>` members

A getter-only `IQueryable<T>` of an entity type is a list member declared as a query. It is backed by a reference
the same way, one-to-many or many-to-many, and takes the same attributes, `[OneToMany]` and `[ManyToMany]`:

```csharp
[OneToMany("Customer")] IQueryable<IOrder> OrderQuery { get; }
[ManyToMany(typeof(IProductTag))] IQueryable<ITag> TagQuery { get; }
```

The difference is that the entity stores no list. The member returns a query over the related rows, and the
code filters, orders and pages it before anything is read: `customer.OrderQuery.Where(o => o.Total > 100).Count()`.
Nothing is loaded into the entity, so the member suits a relation with many rows. `[OrderBy]` and
`[PersistOrderIn]` do not apply: a query is ordered where it is written.

An `IQueryable<T>` member may look like a code shortcut for a filtered query. It plays a real part in smart
loading: it expresses the link from the related set to its owner entity, for a large set where a list member is
not practical, and a query written over it runs for every owner the session holds at once. See [Smart
loading](smart-loading.md).

## Members that are not plain columns

* `[NoColumn]`: the member lives in memory only, with no column.
* `[NoUpdate]`: the application sets the member at creation and never again; an update does not write it.
* `[ReadOnly]`: the application never sets it.
* `[Secret]`: the member is written but never read back in a `SELECT`, such as a password hash.
* `[OnDemand]`: no read of the entity brings the member's column; the member is loaded when it is first read.
  For a large column, a text or a picture: see [On-demand members](on-demand-members.md).

## Generated values

* `[Auto(AutoType.CreatedOn)]` and `[Auto(AutoType.UpdatedOn)]` on a `DateTime` member stamp it at the insert
  and at every update; `CreatedBy`, `UpdatedBy`, `CreatedById` and `UpdatedById` take the user from the
  session's context.
* `[Auto]` on a key generates the key value; see [The primary key](#the-primary-key).
* `[RowVersion]` is an `int` or `long` row version the ORM counts up on every update, for optimistic
  concurrency; see [Locking records](locking.md).
* `[HashFor("Url")]` keeps a hash of a text member in an `int` or a `long` member, so a long text is found
  through an index on its hash.

A hash member is written whenever its text member is assigned. The application computes the same value to
query by, with `app.ComputeStableHash(text)` for an `int` member and `app.ComputeStableHash64(text)` for a
`long`. Two texts can share a hash, so the query compares the text as well:

```csharp
public interface IBookmark {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(400)] string Url { get; set; }
  [HashFor(nameof(Url)), Index] long UrlHash { get; set; }
}

var hash = app.ComputeStableHash64(url);
var bookmark = await session.EntitySet<IBookmark>()
  .Where(b => b.UrlHash == hash && b.Url == url)
  .FirstOrDefaultAsync();
```

The hash is FNV-1a over the trimmed text: 64 bits in a `long`, folded to 32 bits in an `int`. A text that is
null or blank hashes to 0. The value does not depend on the .NET version or the machine.

## Computed members

* `[Computed("GetFullName")]` is computed by a static method over the entity on every read, with no column
  unless `Stored = true`.
* `[DbComputed]` with a `[SqlExpression]` per server is computed by the database.
* `[FromJson("Spec", "$.weight")]` is computed by the database from a JSON member.

The three, who computes what and when, and the stored form are on [Database
objects](database-objects.md#computed-columns).
