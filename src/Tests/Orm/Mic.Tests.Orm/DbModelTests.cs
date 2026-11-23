using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// The db model: what a DATABASE contains. Built by hand here, because nothing maps yet — EntityAppModel → DbModel is
// plan step 11 and waits on the provider abstraction, since what a CLR type becomes in a database is a provider's
// answer rather than a general one.
[TestClass]
public class DbModelTests {
  private static DbModel NewModel(out DbTableInfo person, out DbTableInfo address) {
    var model = new DbModel("TestDb", new PostgresProvider());
    var schema = model.GetOrAddSchema("people");

    address = new DbTableInfo("Address", schema);
    address.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    address.AddColumn(new DbColumnInfo("City", new DbTypeRef("varchar(50)", typeof(string))) { Size = 50 });
    model.AddTable(address);

    person = new DbTableInfo("Person", schema);
    person.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    person.AddColumn(new DbColumnInfo("Name", new DbTypeRef("varchar(80)", typeof(string))) { Size = 80 });
    person.AddColumn(new DbColumnInfo("AddressId", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(person);
    return model;
  }

  // ----- ONE hand-built model, everything it contains and every way it is asked ------------------------------------------
  //
  // Nine methods over the same two tables. The one case that MUTATES — a second `Person` in another schema — comes
  // after the counts it would change.
  [TestMethod]
  public void DbModel_HoldsSchemasTablesColumnsForOneServer() {
    var model = NewModel(out var person, out var address);
    Assert.AreEqual(2, model.Tables.Count);
    Assert.AreEqual(1, model.Schemas.Count);
    Assert.AreSame(person, model.FindTable("people", "Person"));
    Assert.AreSame(model.FindSchema("people"), person.Schema);

    // One entity model becomes several db models — one per vendor — so a db model without its server is meaningless.
    Assert.AreEqual(DbServerKind.Postgres, model.Provider.ServerKind);

    // ----- columns are reachable both ways, and looked up case-insensitively ------------------------------------------
    //
    // Database identifiers are not case-sensitive in most servers, and the ones that fold do so differently.
    Assert.AreEqual(3, person.Columns.Count);
    Assert.AreSame(person, person.GetColumn("Name").Table);
    Assert.IsNotNull(person.FindColumn("NAME"));
    Assert.IsNull(person.FindColumn("Nope"));

    // ----- and each carries the TYPE the database declares -------------------------------------------------------------
    //
    // The field the round trip rests on: whatever cannot be inferred back from a CLR type survives verbatim.
    var name = person.GetColumn("Name");
    Assert.AreEqual("varchar(80)", name.TypeSpec);
    Assert.AreEqual(typeof(string), ((DbTypeRef)name.TypeRef).ClrType);

    // …including one no mapping claims, which is a FACT worth keeping rather than an error — exactly what an exotic
    // column looks like when a database is read.
    var exotic = new DbTableInfo("Place", model.GetOrAddSchema("public"));
    exotic.AddColumn(new DbColumnInfo("Shape", new DbTypeRef("geography")));
    model.AddTable(exotic);
    Assert.AreEqual("geography", exotic.GetColumn("Shape").TypeSpec);
    Assert.IsNull(((DbTypeRef)exotic.GetColumn("Shape").TypeRef).ClrType);

    // ----- a column belongs to ONE table, and a table name is unique WITHIN a schema ------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => address.AddColumn(person.GetColumn("Name")));
    Assert.ThrowsExactly<InvalidOperationException>(() => model.AddTable(new DbTableInfo("Person", person.Schema)));

    // …so the same name in ANOTHER schema is an ordinary thing, which is what schemas are for.
    var archived = new DbTableInfo("Person", model.GetOrAddSchema("archive"));
    archived.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(archived);

    Assert.AreSame(person, model.FindTable("people", "Person"));
    Assert.AreSame(archived, model.FindTable("archive", "Person"));
  }

  // ----- keys and constraints ---------------------------------------------------------------------------------------

  private static DbKeyInfo PrimaryKey(DbTableInfo table, params string[] columns) {
    var key = new DbKeyInfo($"PK_{table.Name}", table, KeyFlags.PrimaryKey);
    foreach (var column in columns)
      key.AddColumn(table.GetColumn(column));
    table.AddKey(key);
    return key;
  }

  [TestMethod]
  public void Key_CarriesColumnsAndAConstraintPairsTwo() {
    var model = NewModel(out var person, out var address);

    // ----- an index carries its direction, its includes and its filter ------------------------------------------------
    var index = new DbKeyInfo("IX_Person_Name", person, KeyFlags.Index) { Filter = "\"Name\" IS NOT NULL" };
    index.AddColumn(person.GetColumn("Name"), descending: true);
    index.AddIncludedColumn(person.GetColumn("Id"));
    person.AddKey(index);

    Assert.IsTrue(index.Columns[0].Descending);
    Assert.AreEqual("Id", index.IncludedColumns.Single().Name);
    Assert.AreEqual("\"Name\" IS NOT NULL", index.Filter);

    // ----- a primary key is found, and unique without saying so ---------------------------------------------------------
    var pk = PrimaryKey(person, "Id");
    Assert.AreSame(pk, person.PrimaryKey);
    Assert.IsTrue(pk.IsUnique);
    Assert.IsFalse(pk.IsComposite);

    // ----- and a key over ANOTHER table's columns is refused --------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(
      () => new DbKeyInfo("IX_Bad", person, KeyFlags.Index).AddColumn(address.GetColumn("City")));

    // ----- a FOREIGN KEY CONSTRAINT is its own object, pairing two keys ------------------------------------------------
    //
    // On the entity side a reference is a member, so ReferenceInfo has somewhere to live. A table has no member to hang
    // it on — and a database really does hold a named constraint, which an upgrade adds, drops and reports on.
    var addressPk = PrimaryKey(address, "Id");

    var fromKey = new DbKeyInfo("FK_Person_AddressId", person, KeyFlags.ForeignKey);
    fromKey.AddColumn(person.GetColumn("AddressId"));
    person.AddKey(fromKey);

    var constraint = new DbRefConstraintInfo("FK_Person_AddressId", fromKey, addressPk) { CascadeDelete = true };
    person.AddRefConstraint(constraint);

    Assert.AreSame(person, constraint.FromTable);
    Assert.AreSame(address, constraint.ToTable);
    Assert.IsTrue(fromKey.IsForeignKey);
    Assert.IsFalse(fromKey.Flags.HasFlag(KeyFlags.Index));   // a foreign key does not imply an index on itself
    Assert.IsTrue(constraint.CascadeDelete);

    // ----- which must start from its OWN table -------------------------------------------------------------------------
    var stray = new DbKeyInfo("FK", address, KeyFlags.ForeignKey);
    Assert.ThrowsExactly<InvalidOperationException>(
      () => person.AddRefConstraint(new DbRefConstraintInfo("FK", stray, addressPk)));
  }

  // ----- the links back, and their absence ---------------------------------------------------------------------------

  [TestMethod]
  public void IntrospectedModel_HasNoLinks() {
    // The DbFirst and migration path: a database is read and there is no application to point back at. A first-class
    // case, so everything reading these references copes with their absence.
    var model = NewModel(out var person, out _);
    Assert.IsNull(model.EntityModel);
    Assert.IsNull(person.Entity);
    Assert.IsNull(person.GetColumn("Name").Member);
    Assert.IsNull(person.Schema.EntitySchema);
  }

  [TestMethod]
  public void ADerivedModelPointsBackAtEveryLevel() {
    var entityModel = BuildEntityModel().Model!;
    var entity = entityModel.FindEntity("Widget")!;

    var model = new DbModel("Db", new PostgresProvider()) { EntityModel = entityModel };
    var schema = model.GetOrAddSchema("s");
    schema.EntitySchema = entityModel.FindSchema("s");
    var table = new DbTableInfo("Widget", schema) { Entity = entity };
    table.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))) { Member = entity.GetMember("Id") });
    model.AddTable(table);

    Assert.AreSame(entityModel, model.EntityModel);
    Assert.AreSame(entity, table.Entity);
    Assert.AreSame(entity.GetMember("Id"), table.GetColumn("Id").Member);

    // ----- and a DbTypeSpec travels through the ENTITY model ---------------------------------------------------------
    //
    // The carrier that makes the round trip survive a type the mapping would never choose — recorded on the MEMBER,
    // because the entity model sits in the middle of database → db model → entity model → db model → database.
    Assert.AreEqual("citext", entity.GetMember("Code").DbTypeSpec);
    Assert.IsNull(entity.GetMember("Id").DbTypeSpec);

    // A foreign key column takes the type its target key names, or the two columns of one constraint would differ.
    Assert.AreEqual("char(2)", entityModel.FindEntity("WidgetPart")!.GetMember("Kind_Code").DbTypeSpec);
  }

  // A change of an upgrade is named by a path: every table has an `Id`, so a bare name says which column by accident.
  [TestMethod]
  public void ObjectPath_NamesTheSchemaAndTheTable() {
    var model = NewModel(out var person, out _);

    Assert.AreEqual("people.Person", DbModelHelper.GetPath(person));
    Assert.AreEqual("people.Person.Name", DbModelHelper.GetPath(person.GetColumn("Name")));
    var index = new DbKeyInfo("IX_Person_Name", person, KeyFlags.Index);
    Assert.AreEqual("people.Person.IX_Person_Name", DbModelHelper.GetPath(index));
    Assert.AreEqual("people", DbModelHelper.GetPath(person.Schema));
    Assert.AreEqual("TestDb", DbModelHelper.GetPath(model));
    Assert.AreEqual("?", DbModelHelper.GetPath(null));
  }

  private interface IWidget {
    [PrimaryKey] Guid Id { get; set; }
    [Column(DbTypeSpec = "citext")] string Code { get; set; }
  }

  private interface IWidgetKind {
    [PrimaryKey, Column(DbTypeSpec = "char(2)")] string Code { get; set; }
  }

  private interface IWidgetPart {
    [PrimaryKey] Guid Id { get; set; }
    IWidgetKind Kind { get; set; }
  }

  private static EntityApp BuildEntityModel() {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IWidget), typeof(IWidgetKind), typeof(IWidgetPart));
    app.AddModule(module, "s");
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }
}
