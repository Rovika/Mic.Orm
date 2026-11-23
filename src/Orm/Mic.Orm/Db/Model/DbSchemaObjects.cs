
namespace Mic.Orm.Db.Model;

/// <summary>Represents a view, materialized or not: one read from a server, or the view part of a model's
/// <see cref="DbTableInfo"/> whose entity reads through a view.</summary>
/// <remarks>Routines, packages, types, triggers and synonyms are read from a server and never compared with a model,
/// so an upgrade never drops them. A view is compared where the model declares one of its name, through the hash of
/// its definition that MIC stores beside it; a view MIC did not make is never dropped. A body is read only when
/// <c>DbModelParts.SourceText</c> is asked for.</remarks>
public class DbViewInfo : DbModelObject {
  readonly List<DbColumnInfo> _columns = new();

  public DbViewInfo(string name, DbSchemaInfo schema) : base(name) {
    ArgumentNullException.ThrowIfNull(schema);
    Schema = schema;
  }

  public DbSchemaInfo Schema { get; }

  /// <summary>Gets or sets a value that indicates whether the server stores the view's result rather than its query.
  /// </summary>
  public bool IsMaterialized { get; set; }

  /// <summary>Gets the columns the view presents, as read from a server; empty on a model's view, whose columns are
  /// its table's.</summary>
  public IReadOnlyList<DbColumnInfo> Columns => _columns;

  /// <summary>Gets or sets the defining select: as read from a server, or as the model built it for a view MIC
  /// manages; <see langword="null"/> when it was not read, and on a view the model declares as existing.</summary>
  public string? Source { get; set; }

  /// <summary>Gets or sets a value that indicates whether MIC creates, changes and drops the view, as against a view
  /// the model declares as existing in the database.</summary>
  public bool IsManaged { get; set; }

  /// <summary>Gets or sets the column names the CREATE VIEW lists, in the defining select's order, for a view whose
  /// select names its values otherwise; or <see langword="null"/> to take the select's own names.</summary>
  public IReadOnlyList<string>? ColumnNames { get; set; }

  /// <summary>Gets or sets the tables of this model the view's select reads, for a view MIC manages: the tables of a
  /// query's translation, and the table entities its definition names as dependencies.</summary>
  /// <remarks>A view over one of them is recreated when a column of the table changes type or is dropped, since a
  /// server may refuse the change under a view.</remarks>
  public IReadOnlyList<DbTableInfo> ReadsTables { get; set; } = [];

  /// <summary>Gets or sets the hash MIC stored beside the view when it created it, as read back by introspection; or
  /// <see langword="null"/> on a view MIC did not make, or on a model's view, whose hash is computed from
  /// <see cref="Source"/>.</summary>
  public string? StoredDefinitionHash { get; set; }

  public string? Comment { get; set; }

  public void AddColumn(DbColumnInfo column) {
    ArgumentNullException.ThrowIfNull(column);
    _columns.Add(column);
  }

  public override string ToString() => $"{Schema.Name}.{Name}" + (IsMaterialized ? " (materialized)" : "");
}

/// <summary>Represents the function part of a model's <see cref="DbTableInfo"/> whose entity reads the rows a
/// function of the server returns: its parameters as this server types them, and the body MIC defines it with.
/// </summary>
/// <remarks>The table's name is the function's, its columns the rows'. A function MIC manages is compared with the
/// database's routine of its name through the hash of its body, stored in a comment inside the body; a function
/// declared as existing is checked for and never changed.</remarks>
public class DbTableFunctionInfo {
  readonly List<DbParameterInfo> _parameters = new();

  /// <summary>Gets the parameters, by name and type spec, in order.</summary>
  public IReadOnlyList<DbParameterInfo> Parameters => _parameters;

  /// <summary>Gets or sets the body that defines the function on this server, the select over the parameters; or
  /// <see langword="null"/> on a function the model declares as existing.</summary>
  public string? Body { get; set; }

  /// <summary>Gets or sets a value that indicates whether MIC creates, changes and drops the function.</summary>
  public bool IsManaged { get; set; }

  public void AddParameter(DbParameterInfo parameter) {
    ArgumentNullException.ThrowIfNull(parameter);
    _parameters.Add(parameter);
  }
}

/// <summary>Represents a procedure or a function.</summary>
public class DbRoutineInfo : DbModelObject {
  readonly List<DbParameterInfo> _parameters = new();

  public DbRoutineInfo(string name, DbSchemaInfo schema, DbRoutineKind kind = DbRoutineKind.Procedure) : base(name) {
    ArgumentNullException.ThrowIfNull(schema);
    Schema = schema;
    RoutineKind = kind;
  }

  public DbSchemaInfo Schema { get; }
  public DbRoutineKind RoutineKind { get; set; }

  /// <summary>Gets the package this routine belongs to, or <see langword="null"/> for a standalone routine.</summary>
  /// <remarks>The value is set when the package takes the routine.</remarks>
  public DbPackageInfo? Package { get; internal set; }

  /// <summary>Gets or sets the type a function returns, as the server declares it, or <see langword="null"/> for a
  /// procedure.</summary>
  public string? ReturnTypeSpec { get; set; }

  public IReadOnlyList<DbParameterInfo> Parameters => _parameters;

  /// <summary>Gets or sets the shape of the row set the routine returns.</summary>
  /// <value>The columns; an empty list if the routine returns no rows; <see langword="null"/> if the server cannot
  /// say.</value>
  /// <remarks>PostgreSQL declares the shape; SQL Server infers the first result set and gives up on dynamic SQL; Oracle
  /// returns a cursor whose shape is not in the catalog; MySQL records nothing.</remarks>
  public IReadOnlyList<DbColumnInfo>? ResultColumns { get; set; }

  /// <summary>Gets or sets the body, or <see langword="null"/> when it was not read.</summary>
  public string? Source { get; set; }

  public string? Comment { get; set; }

  /// <summary>Gets or sets the hash MIC stored in the body when it created the function, as read back by
  /// introspection; or <see langword="null"/> on a routine MIC did not make.</summary>
  public string? StoredDefinitionHash { get; set; }

  public void AddParameter(DbParameterInfo parameter) {
    ArgumentNullException.ThrowIfNull(parameter);
    _parameters.Add(parameter);
  }

  /// <summary>Removes the parameters that meet a condition.</summary>
  /// <param name="match">The condition a parameter to remove meets.</param>
  public void RemoveParameters(Predicate<DbParameterInfo> match) {
    ArgumentNullException.ThrowIfNull(match);
    _parameters.RemoveAll(match);
  }

  public override string ToString() => $"{RoutineKind} {Schema.Name}.{Name}({_parameters.Count})";
}

/// <summary>Represents one parameter of a routine.</summary>
public class DbParameterInfo : DbModelObject {
  public DbParameterInfo(string name, string typeSpec) : base(name) {
    TypeSpec = typeSpec;
  }

  /// <summary>Gets or sets the type exactly as the server declares it.</summary>
  public string TypeSpec { get; set; }

  public DbParameterDirection Direction { get; set; }

  /// <summary>Gets or sets the one-based position of the parameter in the signature.</summary>
  public int Position { get; set; }

  public bool HasDefault { get; set; }
  public string? DefaultText { get; set; }

  public override string ToString() => $"{Direction} {Name} {TypeSpec}";
}

/// <summary>Represents an Oracle package, a container of routines.</summary>
/// <remarks>The specification and the body are separate objects on the server, separately compiled and separately
/// valid, so both sources are kept.</remarks>
public class DbPackageInfo : DbModelObject {
  readonly List<DbRoutineInfo> _routines = new();

  public DbPackageInfo(string name, DbSchemaInfo schema) : base(name) {
    ArgumentNullException.ThrowIfNull(schema);
    Schema = schema;
  }

  public DbSchemaInfo Schema { get; }

  public IReadOnlyList<DbRoutineInfo> Routines => _routines;

  /// <summary>Gets or sets the specification's source, or <see langword="null"/> when it was not read.</summary>
  public string? Source { get; set; }

  /// <summary>Gets or sets the body's source, or <see langword="null"/> when it was not read or there is no body.
  /// </summary>
  public string? BodySource { get; set; }

  public string? Comment { get; set; }

  public void AddRoutine(DbRoutineInfo routine) {
    ArgumentNullException.ThrowIfNull(routine);
    routine.Package = this;
    _routines.Add(routine);
  }

  public override string ToString() => $"{Schema.Name}.{Name} ({_routines.Count} routines)";
}

/// <summary>Represents a trigger, owned by the table it fires for.</summary>
public class DbTriggerInfo : DbModelObject {
  public DbTriggerInfo(string name, DbTableInfo table) : base(name) {
    ArgumentNullException.ThrowIfNull(table);
    Table = table;
  }

  public DbTableInfo Table { get; }

  public DbTriggerTiming Timing { get; set; }
  public DbTriggerEvents Events { get; set; }

  /// <summary>Gets or sets a value that indicates whether the trigger fires once per row rather than once per
  /// statement.</summary>
  public bool IsRowLevel { get; set; }

  /// <summary>Gets or sets a value that indicates whether the trigger is enabled.</summary>
  /// <value><see langword="true"/> if the trigger is enabled; otherwise, <see langword="false"/>. The default is
  /// <see langword="true"/>.</value>
  public bool IsEnabled { get; set; } = true;

  /// <summary>Gets or sets the body, or <see langword="null"/> when it was not read.</summary>
  public string? Source { get; set; }

  public override string ToString() => $"{Table.Name}.{Name}: {Timing} {Events}";
}

/// <summary>Represents a check constraint, owned by its table.</summary>
public class DbCheckConstraintInfo : DbModelObject {
  const string ManagedPrefix = "CK_";
  const int HashLength = 8;

  public DbCheckConstraintInfo(string name, DbTableInfo table, string expression) : base(name) {
    ArgumentNullException.ThrowIfNull(table);
    Table = table;
    Expression = expression;
  }

  public DbTableInfo Table { get; }

  /// <summary>Gets or sets the condition as SQL text, commonly rewritten by the server from what was written.</summary>
  public string Expression { get; set; }

  /// <summary>Gets the name a declared check takes in the database: <c>CK_</c>, the table, the check's own name and
  /// eight hex digits of a hash of its condition.</summary>
  /// <param name="tableName">The name of the table.</param>
  /// <param name="checkName">The name the entity gives the check.</param>
  /// <param name="expression">The condition, as written to the server.</param>
  /// <returns>The name.</returns>
  /// <remarks>The server rewrites a condition, so its text cannot be compared; the name says whether the condition
  /// is the one declared.</remarks>
  public static string GetManagedName(string tableName, string checkName, string expression) =>
    $"{ManagedPrefix}{tableName}_{checkName}_{Mic.Common.Fnv1aHash.ComputeHex(expression)[..HashLength]}";

  /// <summary>Determines whether a constraint's name is one <see cref="GetManagedName"/> makes for a table.</summary>
  /// <param name="name">The constraint's name.</param>
  /// <param name="tableName">The name of the table.</param>
  /// <returns><see langword="true"/> if the name starts with <c>CK_</c> and the table and ends with an underscore and
  /// eight hex digits; otherwise, <see langword="false"/>.</returns>
  public static bool IsManagedName(string name, string tableName) {
    ArgumentNullException.ThrowIfNull(name);
    var prefix = $"{ManagedPrefix}{tableName}_";
    return name.Length > prefix.Length + HashLength && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      && name[^(HashLength + 1)] == '_' && name[^HashLength..].All(char.IsAsciiHexDigit);
  }

  /// <summary>Gets the name an entity gave a check, out of the name <see cref="GetManagedName"/> made from it.
  /// </summary>
  /// <param name="name">The constraint's name.</param>
  /// <param name="tableName">The name of the table.</param>
  /// <returns>The check's own name, or <see langword="null"/> if <paramref name="name"/> is not one
  /// <see cref="GetManagedName"/> makes for the table.</returns>
  public static string? GetCheckName(string name, string tableName) =>
    IsManagedName(name, tableName) ? name[(ManagedPrefix.Length + tableName.Length + 1)..^(HashLength + 1)] : null;

  public override string ToString() => $"{Name}: {Expression}";
}

/// <summary>Represents a user-defined type; <see cref="TypeKind"/> says which members carry meaning.</summary>
public class DbCustomTypeInfo : DbModelObject {
  readonly List<DbColumnInfo> _attributes = new();
  readonly List<string> _enumValues = new();

  public DbCustomTypeInfo(string name, DbSchemaInfo schema, DbCustomTypeKind kind) : base(name) {
    ArgumentNullException.ThrowIfNull(schema);
    Schema = schema;
    TypeKind = kind;
  }

  public DbSchemaInfo Schema { get; }
  public DbCustomTypeKind TypeKind { get; set; }

  /// <summary>Gets or sets the type a domain or alias type is built on.</summary>
  public string? BaseTypeSpec { get; set; }

  public bool IsNullable { get; set; } = true;

  /// <summary>Gets or sets a domain's constraint, as SQL text.</summary>
  public string? CheckExpression { get; set; }

  /// <summary>Gets the attributes of a composite or object type, or the columns of a table type.</summary>
  public IReadOnlyList<DbColumnInfo> Attributes => _attributes;

  /// <summary>Gets an enum type's values in the server's own order, which is the order they sort in.</summary>
  public IReadOnlyList<string> EnumValues => _enumValues;

  public string? Comment { get; set; }

  public void AddAttribute(DbColumnInfo attribute) {
    ArgumentNullException.ThrowIfNull(attribute);
    _attributes.Add(attribute);
  }

  public void AddEnumValue(string value) => _enumValues.Add(value);

  public override string ToString() => $"{TypeKind} {Schema.Name}.{Name}";
}

/// <summary>Represents a synonym, a second name for an object that may live in another database.</summary>
/// <remarks>The target is held by name, since it is commonly not in this model.</remarks>
public class DbSynonymInfo : DbModelObject {
  public DbSynonymInfo(string name, DbSchemaInfo schema, string targetName) : base(name) {
    ArgumentNullException.ThrowIfNull(schema);
    Schema = schema;
    TargetName = targetName;
  }

  public DbSchemaInfo Schema { get; }

  public string? TargetSchema { get; set; }
  public string TargetName { get; set; }

  /// <summary>Gets or sets the database or link the target lives in, where the server records one and it is not this
  /// database.</summary>
  public string? TargetDatabase { get; set; }

  public string? Comment { get; set; }

  public override string ToString() =>
    $"{Schema.Name}.{Name} → " + (TargetSchema == null ? TargetName : $"{TargetSchema}.{TargetName}");
}
