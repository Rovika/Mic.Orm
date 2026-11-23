namespace Mic.Orm.Db;

/// <summary>Specifies the kind of a database instance, as recorded in the database by whoever set it up.</summary>
/// <remarks>The value comes from the database, not from configuration, and the application never updates it;
/// changing it is an administrator's act, done in the database.</remarks>
public enum DbInstanceType {
  Development,
  /// <summary>A shared pre-production database, deployed to rather than developed against.</summary>
  Staging,
  Production,
}

/// <summary>Represents everything else MIC remembers about a database, carried as JSON in one column.</summary>
/// <remarks>The object is read tolerantly: missing and unknown fields are accepted, and nothing in it is required.
/// An absent module version means the version is unknown.</remarks>
public sealed class DbInfoDetails {
  /// <summary>Gets or sets the version of each module, by module name, which is what makes a module's own migrations
  /// orderable.</summary>
  public Dictionary<string, string> ModuleVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Gets or sets the name of the Guid scheme the keys were minted under at the last upgrade that applied
  /// DDL, such as <c>V7</c>; <see langword="null"/> on a row written before the scheme was recorded.</summary>
  public string? GuidScheme { get; set; }
}

/// <summary>Represents the metadata row MIC records about a database it manages, as read. This class cannot be
/// inherited.</summary>
/// <remarks>The row is a plain record rather than an entity, so that it can be read and written before the entity
/// runtime is up.</remarks>
public sealed class DbInfoRecord {
  public Guid Id { get; init; }

  /// <summary>Gets the name of the application that last wrote the record.</summary>
  /// <remarks>There is one record per database, so the value names the last writer rather than identifying the row.
  /// </remarks>
  public string AppName { get; init; } = "";

  public DbInstanceType InstanceType { get; init; }

  /// <summary>Gets the model version last applied.</summary>
  /// <value>The version, or <see langword="null"/> when MIC has never managed the database.</value>
  public Version? Version { get; init; }

  /// <summary>Gets a value that indicates whether the last upgrade failed part-way.</summary>
  /// <remarks>An upgrade is not transactional, so a failure leaves the schema part-changed; the next run re-diffs and
  /// produces what is left.</remarks>
  public bool LastUpgradeFailed { get; init; }

  /// <summary>Gets the error of the last upgrade.</summary>
  /// <value>The error text, or <see langword="null"/> when the last upgrade did not fail.</value>
  public string? LastUpgradeError { get; init; }

  /// <summary>Gets the time, in UTC, when MIC last applied an upgrade to the database.</summary>
  /// <value>The time, or <see langword="null"/> when MIC has never upgraded the database.</value>
  /// <remarks>Only a successful run is stamped; a failed attempt is described by <see cref="LastUpgradeFailed"/> and
  /// <see cref="LastUpgradeError"/>.</remarks>
  public DateTime? LastUpgradedOn { get; init; }

  public DbInfoDetails Details { get; init; } = new();

  public override string ToString() =>
    $"{InstanceType}, version {Version?.ToString() ?? "unknown"}" + (LastUpgradeFailed ? ", LAST UPGRADE FAILED" : "");
}
