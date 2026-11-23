# Locking records

In a multi-user application, two operations might reach the same record at the same moment: two users editing an
order, two workers taking the same job from a queue, two requests adjusting one stock count. Left unguarded, the
second overwrites what the first wrote, a count goes below zero, a job runs twice, a reader sees a document
half-written with its integrity broken; two operations that take each other's rows in opposite orders deadlock.
The application needs the second operation to wait for the first, or to read a consistent state while the first
writes, or to learn that its copy is stale before it overwrites. Database servers provide row locks and
isolation levels for this, each in its own syntax, and a lock lives only inside a transaction.

In MIC ORM, the application asks for the lock where it reads. A read by key with `LoadFlags.WriteLock`, or a
query with `QueryOptions.LockForUpdate`, holds the rows it returns until the transaction ends; a read by key
with `LoadFlags.ReadLock` runs in a consistent snapshot and never waits on a writer. The session begins the
transaction where the caller opened none, and the save commits it. Where a lock is too costly, a `[RowVersion]`
member refuses a stale save instead.

Three cases follow: a single row, locked and changed; a table of scheduled jobs used as a queue, where each
worker takes the next free record; and a compound document, a root record with the records that belong to it,
locked through its root.

## Locking a single row

The simplest case is a single record that several operations update: a stock count, a balance, a counter. Each
operation reads the record, checks it and writes it back; two of them at once read the same value, and one
update is lost or the count goes below zero. The classic answer is a pessimistic lock: lock the row at the read,
so that the second operation's read waits until the first commits and then sees the committed value. In MIC
ORM, the read asks for the lock with `LoadFlags.WriteLock`:

```csharp
var item = await session.GetEntityAsync<IStockItem>(itemId, LoadFlags.WriteLock);   // begins a transaction
if (item.OnHand < quantity)
  throw new InvalidOperationException("Not enough stock.");
item.OnHand -= quantity;
await session.SaveChangesAsync();                                                    // commits it
```

The locked read always goes to the server, past the session's tracked record and the cache, and refreshes the
tracked record with what the server holds under the lock. A member the application assigned before the read
keeps the assigned value, and a list member already loaded is not read again. An
[on-demand member](on-demand-members.md) loaded before the read is dropped, and loads again when it is next
read. The lock needs a transaction:
with none open, the session begins one, and the next save commits it, written rows or not; inside a
transaction the caller began, the lock holds until the caller commits or rolls back.

To lock the rows a query finds, pass `QueryOptions.LockForUpdate`, inside a transaction the caller begins:
`session.EntitySet<IStockItem>(QueryOptions.LockForUpdate).Where(…)`. The query must be a plain select over a
table: not a set operation, a condition or a mutation. It refreshes the tracked records of its rows as the
read by key does.

The statement is the server's own: `FOR UPDATE` on PostgreSQL, MySQL and Oracle, `WITH (UPDLOCK, ROWLOCK)` on SQL
Server. SQLite has no row locks: the options are accepted, the rows are read plainly and no transaction is begun.
Its write transaction locks the whole file.

## A table as a queue

A common case in practice: an application keeps its scheduled jobs as records in a `ScheduledJob` table, and
several worker processes pick the jobs from the table. Each worker must take a record no other worker has, run
the job it describes and mark the record done; a worker that waits on a record another holds idles, and one that
reads it runs the job twice. The worker needs to find the next free record and lock it in one read.
`QueryOptions.SkipLocked` does that: the query locks the records it finds and skips the records another
transaction holds, so each worker gets the next free one:

```csharp
await session.BeginTransactionAsync();
var job = (await session.EntitySet<IScheduledJob>(QueryOptions.SkipLocked)
  .Where(j => j.Status == JobStatus.Pending).OrderBy(j => j.CreatedOn).Take(1).ToListAsync()).SingleOrDefault();
if (job != null) {
  // run the job
  job.Status = JobStatus.Done;
  await session.SaveChangesAsync();
}
await session.CommitTransactionAsync();   // releases the row
```

The option adds `SKIP LOCKED` to the `FOR UPDATE`, and `READPAST` to the hint on SQL Server; SQLite ignores it
with the lock. The ordering column needs an index: MySQL and SQL Server hold the lock on every record a sort
scanned, not only on the one returned, and the next worker finds nothing free. Oracle takes no row limit on a
locked select (`ORA-02014`); the query is refused there at translation, naming `DbFeatures.NoRowLimitWithLock`, so
the queue read in this form does not run on Oracle.

## Locking a compound document

The information in a database is usually organized logically into documents: a set of related records, an
order with its line items, an invoice with its items. The business system needs to preserve the integrity of
the whole document, in reads and in updates, so the lock belongs to the document as a whole, not to its detail
records. The technique is to lock the header, the root record of the document: a writer locks the root row and
then changes the rest, and a reader at the snapshot level sees the document whole. Locking every line instead
is costly, and two writers locking lines in different orders deadlock.

In MIC ORM, the writer reads the root with the write lock, `GetEntityAsync<IOrder>(id, LoadFlags.WriteLock)`,
then changes the lines and the total, then saves. Writers serialize on the one row, with no lock on the lines,
and a reader with `LoadFlags.ReadLock` sees the whole document, the total equal to its lines.

## A consistent read

Reports and exports read many records across tables, and the business needs the result to be consistent: every
record as of one moment, no document half-written, no total changed after the lines it was read with. Database
servers provide this as a snapshot isolation level: a transaction at that level sees the database as of its
start and waits on no writer. In MIC ORM, the reader asks for it with `LoadFlags.ReadLock` on its first read:
the session begins a transaction at the server's snapshot level, `REPEATABLE READ` on PostgreSQL and MySQL,
`SNAPSHOT` on SQL Server, `SERIALIZABLE` on Oracle, and every read until the transaction ends sees the same
state of the database.

## Optimistic concurrency: a row version

There is another known pattern in the industry to handle concurrent edits, optimistic concurrency: do not lock
for the edit; edit, then try to save, and at the save compare the row's version in the database with the
original version of the record; if it changed, reject the save. It suits an edit that takes minutes, a form in
front of a user, where a lock held that long would block everyone else. The pattern is implemented with a row
version column, and MIC ORM supports it directly:

```csharp
[RowVersion] int Version { get; set; }
```

The member is an `int` or a `long` the ORM counts. An update sets it one up and matches on the value read; a
save over a row another session changed since the read is refused with a `DataAccessException` whose `SubType`
is `ConcurrentUpdate`, and the first save's values stand. A stale delete is refused the same way. The
application then reloads the row and shows the conflict.
