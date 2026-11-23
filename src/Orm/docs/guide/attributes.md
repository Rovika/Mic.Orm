# C# attributes for modeling

An entity interface gives each member a type and a nullability. The model needs more said about the entity and
its members: which member is the key, how long a string may be, which columns are indexed, which value the
server computes; and it should be said on the interface, not in a mapping kept elsewhere.

MIC ORM says it with attributes on the interface and its members. This page lists them: each row gives the
attribute, where it goes, what it does and an example. [Application data model](data-model.md), [Entity
members and types](entity-members.md) and [Database objects](database-objects.md) show them in context.

## Entity

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[Entity]` | interface | Declares an entity. `Name`, `PluralName`, `TableName` override the derived names. | `[Entity(TableName = "tbl_customer")]` |
| `[View]` | interface | The entity reads through a view. `Name` is the view's name; `Materialized = true` stores the rows. | `[Entity, View("v_Totals", Materialized = true)]` |
| `[OldNames]` | interface, property | Former names, comma-separated. An upgrade renames the table or column instead of dropping it. | `[OldNames("Client,Klient")]` |
| `[Validator]` | interface | A static method `(entity) → void` run after the built-in validation. | `[Validator(typeof(OrderRules), "Validate")]` |
| `[BypassAuthorization]` | interface | Skips authorization checks for the entity. | `[BypassAuthorization]` |

## Keys and indexes

A key attribute on a property covers that member. On the interface, the first argument lists the members:
comma or space separated, `-` before a member for descending (`"LastName -CreatedOn"`). `Name` sets the
database name of the key or index.

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[PrimaryKey]` | property, interface | The primary key. Required on every entity. `Clustered = true` clusters the table on it. | `[PrimaryKey("Order,LineNo")]` |
| `[Index]` | property, interface | An index. `Unique`, `Clustered`, `IncludeMembers`, `Filter` (SQL condition), `IndexType` (`DbIndexType.FullText` and others). Several allowed. | `[Index("Tenant,City", IncludeMembers = "PostalCode")]` |
| `[Unique]` | property, interface | A unique index. | `[Unique("Tenant,Login")]` |
| `[ClusteredIndex]` | property, interface | A clustered index. | `[ClusteredIndex("Customer,PlacedOn")]` |
| `[UniqueClusteredIndex]` | property, interface | A unique clustered index. | `[UniqueClusteredIndex("Code")]` |
| `[Check]` | interface | A check constraint: a name and a SQL condition, `{Member}` standing for a column. A server kind as the first argument limits it to that server. Several allowed. | `[Check("Price", "{Price} >= 0")]` |

## Columns

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[Column]` | property | The column: `ColumnName`, `Size`, `Precision`, `Scale`, `Default`, `DbTypeSpec`. | `[Column("amt", Precision = 12, Scale = 2)]` |
| `[Size]` | property | The length of a string or binary member. `SizeOptions.AutoTrim` trims instead of failing. A size code resolves against the module's size table. | `[Size(100)]`, `[Size(200, SizeOptions.AutoTrim)]` |
| `[Unlimited]` | property | Unbounded text or binary (`text`, `nvarchar(max)`). | `[Unlimited] string? Notes { get; set; }` |
| `[Nullable]` | property | The member is nullable. Needed only in an assembly without nullable reference types, or for a value type without `?`. | `[Nullable] string Email { get; set; }` |
| `[Utc]` | property | A `DateTime` kept in UTC; assigned values are converted. | `[Utc] DateTime PlacedOn { get; set; }` |
| `[DateOnly]` | property | A `DateTime` member stored as a date. A `DateOnly` member needs no attribute. | `[DateOnly] DateTime BirthDate { get; set; }` |
| `[Json]` | property | A JSON column holding an object of the member's type. `Discriminator` names a member holding the type discriminator. | `[Json] Dimensions? Size { get; set; }` |
| `[Vector]` | property | A vector column of the stated dimensions, on a `float[]` member. `Metric` names the metric of a distance over it. See [Vector search](vector-search.md). | `[Vector(1536)] float[] Embedding { get; set; }` |
| `[NoColumn]` | property | No column; the member lives in memory only. | `[NoColumn] string? Scratch { get; set; }` |
| `[AsIs]` | property | An upgrade never alters the column's type or definition. | `[AsIs] string? Legacy { get; set; }` |

## References and lists

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[EntityRef]` | reference property | Names the foreign-key columns (`KeyColumns`), the constraint (`ForeignKeyName`), or turns the database foreign key off (`HasDbForeignKey = false`). | `[EntityRef("CustomerId")] ICustomer Customer { get; set; }` |
| `[CascadeDelete]` | reference property | `ON DELETE CASCADE` on the foreign key. | `[CascadeDelete] IOrder Order { get; set; }` |
| `[OneToMany]` | list property | Names the child's reference that backs the list, when the child has more than one reference to this entity. | `[OneToMany("BillingAddress")] IList<IOrder> BilledOrders { get; }` |
| `[ManyToMany]` | list property | The list goes through a link entity. `ThisEntityRef` and `OtherEntityRef` name the link's members when ambiguous. | `[ManyToMany(typeof(IProductTag))] IList<ITag> Tags { get; }` |
| `[OneToOne]` | property | The member is the other end of a one-to-one relation; the child's primary key is its reference to this entity. | `[OneToOne] ICustomerProfile Profile { get; }` |
| `[OrderBy]` | list property, interface | The order of a list, or of every list of the entity. | `[OrderBy("PlacedOn")] IList<IOrder> Orders { get; }` |
| `[PersistOrderIn]` | list property | The member of the child or link entity that stores the list position, one-based. | `[ManyToMany(typeof(IPlaylistTrack)), PersistOrderIn("Position")] IList<ITrack> Tracks { get; }` |

## Generated values

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[Auto]` | property | A value assigned by the ORM: `AutoType.NewGuid` (default, time-ordered), `CreatedOn`, `UpdatedOn`, `CreatedBy`, `UpdatedBy`, `CreatedById`, `UpdatedById`. | `[Auto(AutoType.CreatedOn)] DateTime CreatedOn { get; set; }` |
| `[Identity]` | property | An integer the database generates on insert. | `[PrimaryKey, Identity] int Id { get; set; }` |
| `[Sequence]` | property | The next value of a sequence the module registered. A dotted name includes the schema. | `[Sequence("OrderNumbers")] long Number { get; set; }` |
| `[RowVersion]` | property | A row version the ORM counts up on each update; a stale save is refused. | `[RowVersion] int Version { get; set; }` |
| `[HashFor]` | property | A hash of another member's text, in an `int` or a `long` member, written whenever the text is assigned; for finding a long text through an index. See [Generated values](entity-members.md#generated-values). | `[HashFor("Url"), Index] long UrlHash { get; set; }` |

## Computed members

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[Computed]` | property | Computed by a static method on every read. `MethodClass` defaults to the module. `Stored = true` keeps a column the ORM writes at every save. | `[Computed("GetFullName")] string FullName { get; }` |
| `[DbComputed]` | property | A generated column the server computes from a `[SqlExpression]`. `Stored = true` stores it. | `[DbComputed(Stored = true)] decimal LineTotal { get; }` |
| `[SqlExpression]` | property | The expression of a `[DbComputed]` member for one server. `{Member}` stands for a column. Several allowed. | `[SqlExpression(DbServerKind.Postgres, "{Quantity} * {UnitPrice}")]` |
| `[FromJson]` | property | A generated column over a path in a `[Json]` member. `Stored = true` stores it. | `[FromJson("Spec", "$.Weight")] decimal? Weight { get; }` |

## Behaviour

| Attribute | On | Effect | Example |
|---|---|---|---|
| `[NoUpdate]` | property | Set at creation, never updated. | `[NoUpdate] DateTime CreatedOn { get; set; }` |
| `[ReadOnly]` | property | Never assigned by the application. `System.ComponentModel.ReadOnlyAttribute` has the same name; qualify one of them. | `[ReadOnly] string Code { get; set; }` |
| `[Secret]` | property | Written, never read back in a `SELECT`. Usable in a `WHERE`. | `[Secret] string PasswordHash { get; set; }` |
| `[OnDemand]` | property | Left out of every read of the entity and loaded when first read; for a large column. See [On-demand members](on-demand-members.md). | `[OnDemand, Unlimited] string Body { get; set; }` |

## Not attributes

Caching is declared on the application: `SetCached(CacheType.Server, [typeof(ICountry)])`. Multi-tenancy and soft
delete are marker interfaces on the entity, `ITenantBound` and `ISoftDeleteEntity`, with an extension added to
the application; `[TenantId]` and `[DeletedMark]` mark a member of another name. Sequences, views, temp tables and table
functions are registered in the module.
