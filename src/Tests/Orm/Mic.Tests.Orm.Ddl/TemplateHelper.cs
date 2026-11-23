using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Mic.Tests.Orm.Db;

/// <summary>Packs the ORM and the project template into a feed of their own, installs the template into a private
/// hive, and runs the dotnet commands a generated application is made, built and run with.</summary>
/// <remarks>Everything lives under <c>mic_template_tests</c> in the temp folder: the feed and the hive are made
/// afresh by every run, the packages folder is kept so the drivers and the test SDK are downloaded once. The
/// packing happens once per process, whichever vendor asks first; the others wait on it.</remarks>
public static class TemplateHelper {
  public static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "mic_template_tests");
  public static readonly string Feed = Path.Combine(WorkRoot, "feed");
  public static readonly string Hive = Path.Combine(WorkRoot, "hive");
  public static readonly string PackagesFolder = Path.Combine(WorkRoot, "packages");

#if DEBUG
  const string Configuration = "Debug";
#else
  const string Configuration = "Release";
#endif

  // The shared assemblies, Mic.Common, Mic.Emit and Mic.Linq, are inside the Mic.Orm package.
  static readonly string[] PackedProjects = [
    "Orm/Mic.Orm", "Orm/Mic.Orm.Db.Postgres", "Orm/Mic.Orm.Db.MySql", "Orm/Mic.Orm.Db.SqlServer",
    "Orm/Mic.Orm.Db.Oracle", "Orm/Mic.Orm.Db.Sqlite", "Orm/Mic.Orm.Db.Db2",
    "Templates/Mic.Templates",
  ];

  static readonly Lazy<Task<string>> _packed = new(PackAsync);

  /// <summary>Packs the projects and installs the template, once per process.</summary>
  /// <returns>The version the packages carry.</returns>
  public static Task<string> EnsurePackedAsync() => _packed.Value;

  /// <summary>Runs a dotnet command on a generated application: its packages restored into the private folder,
  /// the output captured.</summary>
  /// <returns>The command's output.</returns>
  public static Task<string> RunAppCommandAsync(string arguments, string folder,
      IReadOnlyDictionary<string, string>? environment = null) {
    var variables = new Dictionary<string, string>(environment ?? new Dictionary<string, string>()) {
      ["NUGET_PACKAGES"] = PackagesFolder,
    };
    return RunAsync("dotnet", arguments, folder, variables);
  }

  /// <summary>Runs the database tool as a process of its own, from its build output, so that it loads an
  /// application as an installed tool does: from the application's folder, holding nothing of it beforehand.
  /// </summary>
  /// <returns>The tool's output.</returns>
  public static Task<string> RunToolAsync(string arguments, string folder,
      IReadOnlyDictionary<string, string>? environment = null) {
    var tool = Path.Combine(FindRepoRoot(), "src", "Tools", "Mic.DbTool", "bin", Configuration, "net10.0",
      "Mic.DbTool.dll");
    return RunAsync("dotnet", $"\"{tool}\" {arguments}", folder, environment);
  }

  /// <summary>Writes a nuget.config beside an application that takes the MIC packages from the feed and the rest
  /// from nuget.org, whatever the machine's own sources and source mappings are.</summary>
  public static void WriteNugetConfig(string folder) {
    File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <packageSources>
          <clear />
          <add key="mic-local" value="{Feed}" />
          <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
        </packageSources>
        <packageSourceMapping>
          <clear />
          <packageSource key="mic-local">
            <package pattern="Mic.*" />
          </packageSource>
          <packageSource key="nuget.org">
            <package pattern="*" />
          </packageSource>
        </packageSourceMapping>
      </configuration>
      """);
  }

  /// <summary>Deletes a folder if it is there and creates it empty.</summary>
  /// <returns>The folder's path.</returns>
  public static string RecreateFolder(string path) {
    if (Directory.Exists(path))
      Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
    return path;
  }

  static async Task<string> PackAsync() {
    var repo = FindRepoRoot();
    var version = ReadVersion(repo);
    RecreateFolder(Feed);
    RecreateFolder(Hive);
    Directory.CreateDirectory(PackagesFolder);
    // The version does not change between runs, so a package restored by the last run would be taken for this one.
    foreach (var cached in Directory.GetDirectories(PackagesFolder, "mic.*"))
      Directory.Delete(cached, recursive: true);

    foreach (var project in PackedProjects) {
      var projectFile = Path.Combine(repo, "src", project, Path.GetFileName(project) + ".csproj");
      await RunAsync("dotnet", $"pack \"{projectFile}\" --no-build -c {Configuration} -o \"{Feed}\"", repo);
    }
    var templatePackage = Path.Combine(Feed, $"Mic.Templates.{version}.nupkg");
    await RunAsync("dotnet", $"new install \"{templatePackage}\" --debug:custom-hive \"{Hive}\"", repo);
    return version;
  }

  static string FindRepoRoot() {
    var folder = new DirectoryInfo(AppContext.BaseDirectory);
    while (folder != null && !File.Exists(Path.Combine(folder.FullName, "src", "Directory.Build.props")))
      folder = folder.Parent;
    return folder?.FullName
      ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
  }

  static string ReadVersion(string repo) {
    var props = File.ReadAllText(Path.Combine(repo, "src", "Directory.Build.props"));
    var match = Regex.Match(props, "<Version>(.+?)</Version>");
    return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("No <Version> in the props.");
  }

  static async Task<string> RunAsync(string program, string arguments, string folder,
      IReadOnlyDictionary<string, string>? environment = null) {
    var startInfo = new ProcessStartInfo(program, arguments) {
      WorkingDirectory = folder,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    // The variables MSBuild sets for its own nodes would steer a nested build into the host's engine.
    foreach (var name in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
      startInfo.Environment.Remove(name);
    startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    startInfo.Environment["DOTNET_NOLOGO"] = "1";
    startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
    startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
    foreach (var (name, value) in environment ?? new Dictionary<string, string>())
      startInfo.Environment[name] = value;

    using var process = Process.Start(startInfo)
      ?? throw new InvalidOperationException($"Could not start {program}.");
    var output = new StringBuilder();
    var readOutput = ReadAsync(process.StandardOutput, output);
    var readError = ReadAsync(process.StandardError, output);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    try {
      await process.WaitForExitAsync(timeout.Token);
    } catch (OperationCanceledException) {
      process.Kill(entireProcessTree: true);
      throw new TimeoutException($"'{program} {arguments}' did not finish in ten minutes.");
    }
    await Task.WhenAll(readOutput, readError);
    if (process.ExitCode != 0)
      throw new InvalidOperationException(
        $"'{program} {arguments}' exited with {process.ExitCode}:{Environment.NewLine}{GetTail(output.ToString())}");
    return output.ToString();
  }

  static async Task ReadAsync(StreamReader reader, StringBuilder output) {
    while (await reader.ReadLineAsync() is { } line)
      lock (output)
        output.AppendLine(line);
  }

  static string GetTail(string text) {
    const int limit = 6000;
    return text.Length <= limit ? text : "…" + text[^limit..];
  }
}
