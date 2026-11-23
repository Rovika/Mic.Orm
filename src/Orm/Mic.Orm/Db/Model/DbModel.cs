using Mic.Linq.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
namespace Mic.Orm.Db.Model;

/// <summary>Represents everything one database contains: its schemas, tables, keys, constraints and sequences, and
/// the other objects a server holds where they were read.</summary>
/// <remarks>One entity model can have several db models, one per vendor. Every link back to the entity model is
/// optional; a model read from a database has none. As the <see cref="ILinqDataModel"/> the query engine runs over,
/// the model answers entities from its entity model and tables and columns from itself.</remarks>
public class DbModel : DbModelObject, ILinqDataModel {
  /// <summary>Gets the provider of the server this model is for.</summary>
  public DbProvider Provider { get; }

  /// <summary>Gets or sets the entity model this model was derived from, or <see langword="null"/> when the model
  /// was read from a database.</summary>
  public EntityAppModel? EntityModel { get; set; }


  /// <summary>Gets the report of mapping the entity model to this database, empty for an introspected model.</summary>
  public DbModelBuildReport BuildReport { get; } = new();

  /// <summary>Gets or sets the options the model was built under, <see cref="DbOptions.Default"/> for an
  /// introspected model.</summary>
  public DbOptions Options { get; set; } = DbOptions.Default;

  public IReadOnlyList<DbTableInfo> Tables => _tables;
  public IReadOnlyList<DbSchemaInfo> Schemas => _schemas;
  public IReadOnlyList<DbSequenceInfo> Sequences => _sequences;

  // The objects beyond tables are empty unless asked for, and none of them is compared by an upgrade.
  public IReadOnlyList<DbViewInfo> Views => _views;
  public IReadOnlyList<DbRoutineInfo> Routines => _routines;
  public IReadOnlyList<DbPackageInfo> Packages => _packages;
  public IReadOnlyList<DbCustomTypeInfo> CustomTypes => _customTypes;
  public IReadOnlyList<DbSynonymInfo> Synonyms => _synonyms;

  /// <summary>Gets or sets the parts that were read into this model, which tells an absent object from one that was
  /// not asked for.</summary>
  public DbModelParts LoadedParts { get; set; } = DbModelParts.Default;

  readonly List<DbTableInfo> _tables = new();
  readonly List<DbSchemaInfo> _schemas = new();
  readonly List<DbSequenceInfo> _sequences = new();
  readonly List<DbViewInfo> _views = new();
  readonly List<DbRoutineInfo> _routines = new();
  readonly List<DbPackageInfo> _packages = new();
  readonly List<DbCustomTypeInfo> _customTypes = new();
  readonly List<DbSynonymInfo> _synonyms = new();
  Dictionary<EntityTypeInfo, DbTableInfo>? _tablesByEntity;

  public DbModel(string name, DbProvider provider) : base(name) {
    ArgumentNullException.ThrowIfNull(provider);
    Provider = provider;
  }

  /// <summary>Gets the table an entity is stored in, in this db model.</summary>
  /// <param name="entity">The entity.</param>
  /// <returns>The table, or <see langword="null"/> if the entity has none.</returns>
  public DbTableInfo? GetTable(EntityTypeInfo entity) {
    _tablesByEntity ??= _tables.Where(t => t.Entity != null).ToDictionary(t => t.Entity!, t => t);
    return _tablesByEntity.TryGetValue(entity, out var table) ? table : null;
  }

  public DbSchemaInfo? FindSchema(string name) =>
    _schemas.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

  public DbSchemaInfo GetOrAddSchema(string name) {
    var existing = FindSchema(name);
    if (existing != null)
      return existing;
    var schema = new DbSchemaInfo(name);
    _schemas.Add(schema);
    return schema;
  }

  /// <summary>Finds a table by schema and name, compared ignoring case.</summary>
  /// <param name="schema">The name of the schema.</param>
  /// <param name="name">The name of the table.</param>
  /// <returns>The table, or <see langword="null"/> if the model has no such table.</returns>
  public DbTableInfo? FindTable(string schema, string name) =>
    _tables.FirstOrDefault(t =>
      string.Equals(t.Schema.Name, schema, StringComparison.OrdinalIgnoreCase)
      && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

  public void AddTable(DbTableInfo table) {
    ArgumentNullException.ThrowIfNull(table);
    if (FindTable(table.Schema.Name, table.Name) != null)
      throw new InvalidOperationException($"Table '{table.Schema.Name}.{table.Name}' is already in the model.");
    _tables.Add(table);
  }

  public void AddSequence(DbSequenceInfo sequence) => _sequences.Add(sequence);

  public void AddView(DbViewInfo view) => _views.Add(view ?? throw new ArgumentNullException(nameof(view)));

  /// <summary>Adds a standalone routine.</summary>
  /// <remarks>A routine inside a package is added to the package instead.</remarks>
  public void AddRoutine(DbRoutineInfo routine) =>
    _routines.Add(routine ?? throw new ArgumentNullException(nameof(routine)));

  public void AddPackage(DbPackageInfo package) =>
    _packages.Add(package ?? throw new ArgumentNullException(nameof(package)));

  public void AddCustomType(DbCustomTypeInfo customType) =>
    _customTypes.Add(customType ?? throw new ArgumentNullException(nameof(customType)));

  public void AddSynonym(DbSynonymInfo synonym) =>
    _synonyms.Add(synonym ?? throw new ArgumentNullException(nameof(synonym)));

  // ----- ILinqDataModel ------------------------------------------------------------------------------------------------

  IReadOnlyList<ILinqEntity> ILinqDataModel.Entities => EntityModel?.Entities ?? [];

  ILinqEntity? ILinqDataModel.FindEntity(string name) => EntityModel?.FindEntity(name);

  ILinqEntity? ILinqDataModel.FindEntity(Type entityType) => EntityModel?.FindEntity(entityType);

  ILinqTable? ILinqDataModel.FindTable(ILinqEntity entity) => entity is EntityTypeInfo typed ? GetTable(typed) : null;

  ILinqColumn? ILinqDataModel.FindColumn(ILinqEntity entity, ILinqEntityMember member) =>
    entity is EntityTypeInfo typed && member is EntityMemberInfo memberInfo ? GetTable(typed)?.GetColumn(memberInfo) : null;

  ILinqEntity ILinqDataModel.CreateTransientEntity(string name, IReadOnlyList<LinqMemberSpec> members) =>
    new TransientEntityInfo(name, members);
}
