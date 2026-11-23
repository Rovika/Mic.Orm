using System.Reflection;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;

namespace Mic.DbTool;

/// <summary>Loads the provider of a server kind from a folder: the assembly MIC names it by, and the one
/// <c>DbProvider</c> class in it.</summary>
public static class ProviderLoader {
  /// <summary>Gets the name of the assembly that holds a server kind's provider.</summary>
  /// <param name="kind">The server kind.</param>
  /// <returns>The assembly name, without an extension.</returns>
  /// <exception cref="NotSupportedException">MIC has no provider for the kind.</exception>
  public static string GetAssemblyName(DbServerKind kind) => kind switch {
    DbServerKind.Postgres => "Mic.Orm.Db.Postgres",
    DbServerKind.MySql => "Mic.Orm.Db.MySql",
    DbServerKind.MsSql => "Mic.Orm.Db.SqlServer",
    DbServerKind.Oracle => "Mic.Orm.Db.Oracle",
    DbServerKind.SQLite => "Mic.Orm.Db.Sqlite",
    DbServerKind.Db2 => "Mic.Orm.Db.Db2",
    _ => throw new NotSupportedException($"MIC has no provider for {kind}."),
  };

  /// <summary>Loads the provider of a server kind from a folder.</summary>
  /// <param name="kind">The server kind.</param>
  /// <param name="folder">The folder holding the provider's assembly and its driver.</param>
  /// <param name="context">The context to load it in, such as the application's, or <see langword="null"/> for
  /// one of the provider's own.</param>
  /// <returns>The provider, constructed with the defaults of its constructor's parameters.</returns>
  /// <exception cref="InvalidOperationException">The folder has no provider for the kind.</exception>
  public static DbProvider Load(DbServerKind kind, string folder, PluginLoadContext? context = null) {
    var path = Path.Combine(Path.GetFullPath(folder), GetAssemblyName(kind) + ".dll");
    if (!File.Exists(path))
      throw new InvalidOperationException(
        $"No provider for {kind} in '{folder}': '{Path.GetFileName(path)}' is not there. Point providerPath at a "
        + "folder that holds it with its driver, such as a built application's output folder.");
    var assembly = (context ?? new PluginLoadContext(path)).LoadFromAssemblyPath(path);
    var providerType = assembly.GetTypes().SingleOrDefault(t => !t.IsAbstract && typeof(DbProvider).IsAssignableFrom(t))
      ?? throw new InvalidOperationException($"'{assembly.GetName().Name}' holds no provider class.");

    // A provider takes its settings as optional parameters, which a call without arguments does not match.
    var constructor = providerType.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional))
      ?? throw new InvalidOperationException($"'{providerType.Name}' has no constructor callable without arguments.");
    var defaults = constructor.GetParameters().Select(_ => Type.Missing).ToArray();
    return (DbProvider)constructor.Invoke(BindingFlags.OptionalParamBinding, null, defaults, null);
  }
}
