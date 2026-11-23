using Mic.Orm;
using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

/// <summary>Which vendors this run is meant to exercise.</summary>
/// <remarks>THE INNER LOOP HAS TO STAY FAST. A developer changing model code wants the suite in seconds, not after
/// four containers have started — so nothing set means the vendors that cost nothing to reach, and the rest report
/// SKIPPED rather than failed.
///
/// The distinction that matters: a vendor that was ASKED FOR must work. Absent means untested, and untested must look
/// different from passing — but a vendor named in <c>MIC_TEST_VENDORS</c> and then unreachable is a failure, because
/// somebody asked.
///
///   MIC_TEST_VENDORS=all              every vendor but those of <c>OnlyWhenNamed</c>
///   MIC_TEST_VENDORS=db2              Db2, which runs only when named
///   MIC_TEST_VENDORS=sqlite,postgres  a chosen few
///   (unset)                           the default set below
///
/// THE NAMES ARE `DbServerKind`'S, and one of them surprises: SQL Server is **MsSql**. A name that matches nothing is
/// not an error — it simply enables nothing, and the run reports SKIPPED rather than failing, which is easy to read
/// past when looking for a pass or a failure. Matching is case-insensitive; the spelling is not negotiable.</remarks>
public static class TestVendors {
  public const string Variable = "MIC_TEST_VENDORS";

  /// <summary>What runs when nothing says otherwise. PostgreSQL is here because its container is one command and is
  /// already in the repository; the heavier servers are not.</summary>
  static readonly DbServerKind[] Default = [DbServerKind.SQLite, DbServerKind.Postgres, DbServerKind.MySql];

  /// <summary>The vendors <c>all</c> leaves out, which run only when named. Db2 is one: its container beside those
  /// of SQL Server and Oracle wants more memory than a developer's Docker has.</summary>
  static readonly DbServerKind[] OnlyWhenNamed = [DbServerKind.Db2];

  public static bool IsEnabled(DbServerKind kind) {
    var setting = Environment.GetEnvironmentVariable(Variable);
    if (string.IsNullOrWhiteSpace(setting))
      return Default.Contains(kind);
    if (setting.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
      return !OnlyWhenNamed.Contains(kind);
    return setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
      .Any(name => name.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase));
  }

  public static string WhyNot(DbServerKind kind) =>
    OnlyWhenNamed.Contains(kind)
      ? $"{kind} is not in this run. Set {Variable}={kind} to include it; {Variable}=all leaves it out."
      : $"{kind} is not in this run. Set {Variable}=all, or {Variable}={kind}, to include it.";
}
