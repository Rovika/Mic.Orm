# Generating entities from a database

`mic-db entities` reads an existing database and writes C# entity interfaces for its tables and views, with the
module and the application class that register them. This is the database-first start of a MIC application.

## When to use it

* An application is moved to MIC ORM and its database exists already.
* A model is needed for a database that another system owns.
* A legacy schema is to be read as entities and then renamed into readable names without changing the
  database. [MIC ORM on an existing Db2 database](existing-db2-database.md) goes through such a case.

## Install

```bash
dotnet tool install --global Mic.DbTool --prerelease
```

The package comes from the ORM's own feed, added as [Getting started](getting-started.md) shows. The tool
carries `Mic.Orm`. The provider package for the server and its driver are loaded from a folder at run
time (`providerPath`): the output folder of any project that references the provider.

## The config file

```json
{
  "$schema": "../mic-db.schema.json",
  "provider": "Postgres",
  "connectionString": "env:SHOP_CONN",
  "providerPath": "../Shop/bin/Debug/net10.0",
  "schemas": [ "shop" ],
  "tables": [ "Customer", "Order*" ],
  "ignoreTables": [ "audit_*" ],
  "namespace": "Shop.Model",
  "appClass": "ShopApp",
  "module": "ShopModule",
  "lists": true,
  "autoGuidKeys": true,
  "addDbObjectsNames": true,
  "output": "ShopApp.generated.cs"
}
```

| Key | Meaning | Default |
|---|---|---|
| `provider` | `Postgres`, `MySql`, `MsSql`, `Oracle`, `SQLite` or `Db2`. | required |
| `connectionString` | The connection string, or `env:NAME`, or `file:path`. | required |
| `providerPath` | The folder the provider assembly and driver are loaded from. | required |
| `schemas` | The schemas read. | every schema except the system ones |
| `tables`, `ignoreTables` | Tables and views to take or skip: names or patterns with `*` and `?`, optionally schema-qualified (`sales.Order*`). | all |
| `namespace` | The namespace of the generated file. | `Generated` |
| `appClass` | The name of the application class. | `DbApp` |
| `module` | The name of the module class. With several schemas, each module is named after its schema plus this name. | `<Schema>Module` |
| `lists` | Write an `IList<IChild>` member on each referenced entity, commented out. | `true` |
| `autoGuidKeys` | Mark a single-column Guid primary key `[Auto]`. | `true` |
| `addDbObjectsNames` | Name every table, column, key and foreign key in an attribute. | `true` |
| `output` | The file written. | `<appClass>.cs` beside the config |

Paths in the file are relative to the file. `--output <path>` and `--connection <value>` on the command line
override the file's values.

## Run

```bash
mic-db entities Shop/mic-db/entities.json
```

The tool reads the schemas' tables, columns, keys, indexes, foreign keys, check constraints and views. It writes
nothing to the database. It writes one C# file: an interface per table and view, a module per schema, the
application class, and a class holding the views' defining selects. Warnings about what the file could not
express are printed and written as comments at the place they apply. MIC's own `DbInfo` table is skipped.

## The generated code

```csharp
[Entity(TableName = "User"), Unique("Tenant,Login", Name = "UQ_User_Tenant_Login"), Unique("Tenant,Email", Name = "UQ_User_Tenant_Email")]
public interface IUser {
  [PrimaryKey(Name = "PK_User_Id"), Auto, Column("Id")]
  Guid Id { get; set; }

  [Size(100), Column("Login")]
  string Login { get; set; }

  // ... Email, PasswordHash, UserType, Status

  [Column("LastSignedInOn")]
  DateTime? LastSignedInOn { get; set; }

  [EntityRef(KeyColumns = "Company_Id", ForeignKeyName = "FK_User_Company_Id")]
  ICompany? Company { get; set; }

  [EntityRef(KeyColumns = "Tenant_Id", ForeignKeyName = "FK_User_Tenant_Id"), Index(Name = "IX_User_Tenant")]
  ITenant Tenant { get; set; }

  // Lists: uncomment, delete, or replace with IQueryable<T> for a large, ever-growing set of dependent entities.
  // IList<ICustomer> Customers { get; }
  // IList<IEmployee> Employees { get; }
}
```

With `addDbObjectsNames` on, every identifier in the file can be renamed and the mapping to the database stands:
`IUser` can become `IAccount` and `Login` can become `UserName` without a change to the database. With it off,
a name is written only where it differs from what MIC would derive.

Rules of the generated code:

* Names become PascalCase: `order_line` is `IOrderLine`, `created_on` is `CreatedOn`.
* The primary key's members come first; then the database's column order.
* A table with no primary key is written with a unique key standing in for it, where the table has one whose
  columns are all required: the key of fewest columns, written as `[PrimaryKey]` under its own name. A table
  with no such key is not written. A warning says which of the two happened.
* A foreign key that points from a key to that same key is not written; its columns are plain members.
* A nullable column is a nullable member.
* Text and binary columns carry `[Size(n)]` or `[Unlimited]`; decimals carry precision and scale; a column of
  a type MIC would not choose carries `DbTypeSpec`; a default carries `Default`.
* An identity column is `[Identity]`; a row-version column `[RowVersion]`.
* A PostgreSQL array column is an array member, `int[]` for `integer[]`. See
  [Array columns on PostgreSQL](postgres-arrays.md).
* A pgvector column is a `float[]` member with `[Vector(n)]`, and the HNSW index over it `[Index]`, with the
  index's metric on `[Vector]`. See [Vector search](vector-search.md).
* A column of the server's JSON type is a `string` member with `[Json]`, which holds the document as text. So
  is an unlimited text column under the check MIC makes for a JSON member where the server has no JSON type,
  `CK_Table_MemberIsJson_` and eight hex digits; that check is not written as a comment. Replace `string` with
  the type of the document to have it serialized.
* A foreign key is a reference member typed as the target entity, named after the column with the target
  key's name removed (`Company_Id` becomes `Company`), nullable if the column is, `[CascadeDelete]` where the constraint
  cascades. The foreign-key column itself is not a member.
* A single-member key or index sits on the member; a multi-member one sits on the interface with the member
  list. `Filter`, `IncludeMembers` and `IndexType` are written where they apply.
* A view is an interface with `[Entity, View("v_Name")]`, `Materialized = true` where it is one. Its defining
  select is registered in the module with `RegisterView<IView>(DbServerKind.Postgres, ViewDefinitions.ViewSql)`.
  Where the server reports no definition, the view is registered as existing. A view is written without a
  primary key, since the database defines none. Add `[PrimaryKey]` to the member that identifies a row, and make
  the member non-nullable, to have the session track the view's rows.
* A column whose type maps to no CLR type, or that the server computes, is left out with a comment.
* A check constraint is a comment above its interface: its name, its condition as the server reports it, and a
  `[Check]` declaration to uncomment. It is not declared, because MIC creates a declared check under a name of
  its own, beside the one the database has. The database keeps its constraint, and an upgrade leaves it alone.
  To have MIC own the rule, uncomment the declaration and drop the old constraint. Two cases differ. A constraint
  that already carries MIC's name, `CK_Table_Name_` and eight hex digits, is dropped by an upgrade over a model
  that does not declare it; the comment and a warning say so. A condition with a braced word outside quoted text
  gets no declaration, since `[Check]` reads such a word as a member's name.

## After generating

1. Add the file to the model project and build.
2. Uncomment the list members that are wanted; replace the large ones with `IQueryable<T>`.
3. Rename interfaces and members into readable names.
4. Run `mic-db upgrade` over the generated application first and read the script. A script with nothing to
   run means the model matches the database. Views registered with a definition are recreated once
   on the first upgrade, to store the hash they are compared by afterwards.
