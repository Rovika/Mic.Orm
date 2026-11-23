using System.Text.Json;
using System.Text.Json.Serialization;
using Mic.Orm.Db;

namespace Mic.DbTool;

/// <summary>Represents a job's config file: the database, the application and where the output goes.</summary>
/// <remarks>The file is JSON, read without regard to case and with comments allowed; its schema is
/// <c>mic-db.schema.json</c> beside the tool. A path in the file is relative to the file's own folder.</remarks>
public sealed class ToolConfig {
  /// <summary>Gets or sets the kind of server, one of <see cref="DbServerKind"/>.</summary>
  public DbServerKind Provider { get; set; }

  /// <summary>Gets or sets the connection string the database is read with: the value itself, or
  /// <c>env:NAME</c> for an environment variable, or <c>file:path</c> for a file holding it, until
  /// <see cref="ResolveConnectionStrings"/> replaces it with what it names.</summary>
  public string ConnectionString { get; set; } = "";

  /// <summary>Gets or sets the connection string schema changes run on when the script is applied, in the forms
  /// <see cref="ConnectionString"/> takes, or <see langword="null"/> for <see cref="ConnectionString"/>.</summary>
  public string? SchemaConnectionString { get; set; }

  /// <summary>Gets or sets the path of the assembly holding the application, for the upgrade job.</summary>
  public string? AppAssembly { get; set; }

  /// <summary>Gets or sets the application's <c>EntityApp</c> class: for the upgrade job its full name, or
  /// <see langword="null"/> for the one such class the assembly holds; for the entities job the name of the class
  /// written, <c>DbApp</c> when absent.</summary>
  public string? AppClass { get; set; }

  /// <summary>Gets or sets the folder the provider and its driver are loaded from, or <see langword="null"/> for
  /// the application assembly's own folder.</summary>
  public string? ProviderPath { get; set; }

  /// <summary>Gets or sets the path of the file the job writes, or <see langword="null"/> for the job's default
  /// name in the config's folder.</summary>
  public string? Output { get; set; }

  /// <summary>Gets or sets a value that indicates whether the upgrade script drops tables the model does not know.
  /// </summary>
  public bool DropUnknownTables { get; set; }

  /// <summary>Gets or sets the patterns of the database objects the upgrade script neither drops nor changes, as
  /// <see cref="DbSettings.IgnoreDbObjects"/> takes them.</summary>
  public string[] IgnoreDbObjects { get; set; } = [];

  /// <summary>Gets or sets how the model is mapped to the database for the upgrade job, as the names of
  /// <see cref="DbOptions"/> flags separated by commas.</summary>
  public DbOptions Options { get; set; } = DbOptions.Default;

  /// <summary>Gets or sets the schemas the entities job reads; empty for every schema but the system ones.</summary>
  public string[] Schemas { get; set; } = [];

  /// <summary>Gets or sets the tables and views the entities job takes, as names or patterns with <c>*</c> and
  /// <c>?</c>, a schema prefix allowed; empty for every table of the schemas.</summary>
  public string[] Tables { get; set; } = [];

  /// <summary>Gets or sets the tables and views the entities job leaves out, in the form <see cref="Tables"/> takes.
  /// </summary>
  public string[] IgnoreTables { get; set; } = [];

  /// <summary>Gets or sets the namespace the entities job writes.</summary>
  public string Namespace { get; set; } = "Generated";

  /// <summary>Gets or sets the name of the module the entities job writes, or <see langword="null"/> for the schema's
  /// name followed by <c>Module</c>.</summary>
  public string? Module { get; set; }

  /// <summary>Gets or sets a value that indicates whether the entities job writes a list member on a referenced
  /// entity for each entity referencing it.</summary>
  public bool Lists { get; set; } = true;

  /// <summary>Gets or sets a value that indicates whether the entities job marks a single-column Guid primary key
  /// <c>[Auto]</c>.</summary>
  public bool AutoGuidKeys { get; set; } = true;

  /// <summary>Gets or sets a value that indicates whether the entities job names every table, column, key and
  /// foreign key in an attribute, so that the identifiers can be renamed and the mapping stands.</summary>
  public bool AddDbObjectsNames { get; set; } = true;

  /// <summary>Gets the folder the config was read from, which its relative paths are resolved against.</summary>
  [JsonIgnore]
  public string BaseFolder { get; private set; } = "";

  static readonly JsonSerializerOptions _options = new() {
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = { new JsonStringEnumConverter() },
  };

  /// <summary>Reads a config file.</summary>
  /// <param name="path">The path of the file.</param>
  /// <returns>The config, its folder remembered.</returns>
  /// <exception cref="FileNotFoundException">The file does not exist.</exception>
  /// <exception cref="JsonException">The file is not a config.</exception>
  public static ToolConfig Load(string path) {
    var fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath))
      throw new FileNotFoundException($"Config file '{fullPath}' not found.", fullPath);
    var config = JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(fullPath), _options)
      ?? throw new JsonException($"Config file '{fullPath}' holds no config.");
    config.BaseFolder = Path.GetDirectoryName(fullPath) ?? "";
    return config;
  }

  /// <summary>Resolves a path of the config against the config's folder.</summary>
  /// <param name="path">The path as written in the config.</param>
  /// <returns>The full path.</returns>
  public string ResolvePath(string path) => Path.GetFullPath(Path.Combine(BaseFolder, path));

  /// <summary>Replaces the connection strings with what they name, where they name an environment variable or a
  /// file, so a config kept in a repository holds no secret.</summary>
  /// <exception cref="InvalidOperationException">A connection string is empty, or names a variable that is not set
  /// or a file that does not exist.</exception>
  public void ResolveConnectionStrings() {
    ConnectionString = ResolveValue(ConnectionString, "connectionString");
    if (SchemaConnectionString != null)
      SchemaConnectionString = ResolveValue(SchemaConnectionString, "schemaConnectionString");
  }

  /// <summary>Resolves a value that may name where it is kept: <c>env:NAME</c> reads the environment variable,
  /// <c>file:path</c> reads the file, trimmed, the path relative to the config's folder; anything else is the value
  /// itself.</summary>
  /// <param name="value">The value as written.</param>
  /// <param name="key">The config key, for the error.</param>
  /// <returns>The value resolved.</returns>
  /// <exception cref="InvalidOperationException">The value is empty, or what it names is not there.</exception>
  public string ResolveValue(string value, string key) {
    try {
      return ConnectionStringHelper.Resolve(value, BaseFolder);
    } catch (InvalidOperationException ex) {
      throw new InvalidOperationException($"{key}: {ex.Message}", ex);
    }
  }
}
