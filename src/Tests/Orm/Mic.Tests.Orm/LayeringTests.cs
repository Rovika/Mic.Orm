using System.Reflection;
using Mic.Linq.Model;

namespace Mic.Tests.Orm;

// The ORM's layering rule, as a test rather than as a convention nobody rereads: Mic.Orm may reference Mic.Common,
// Mic.Emit and Mic.Linq, and NOTHING else of MIC. That is what makes the ORM usable as just an ORM, with none of MIC's
// server, client, modeling or web infrastructure present — a constraint that is cheap to hold and expensive to
// recover, since a single convenient reference is how it would be lost.
[TestClass]
public class LayeringTests {
  // What the ORM must never pull in. Prefixes, so Mic.Server.Services and Mic.Server.Rest are both caught.
  private static readonly string[] ForbiddenPrefixes =
    ["Mic.Server.", "Mic.Client.", "Microsoft.AspNetCore", "Microsoft.Extensions.Hosting"];

  [TestMethod]
  public void Orm_DoesNotReferenceServerOrClient() => AssertNoForbiddenReferences("Mic.Orm");

  [TestMethod]
  public void Provider_DoesNotReferenceServerOrClient() => AssertNoForbiddenReferences("Mic.Orm.Db.Postgres");

  [TestMethod]
  public void Orm_ReferencesOnlyItsThreeSharedAssemblies() {
    // A whitelist, as the query engine's below: Mic.Modeling, Mic.Data or anything else of MIC's would slip through
    // the forbidden list above and is just as wrong. The providers are held to the same, with the ORM itself added.
    AssertReferencesOnly("Mic.Orm", "Mic.Common", "Mic.Emit", "Mic.Linq");
    AssertReferencesOnly("Mic.Orm.Db.Postgres", "Mic.Orm", "Mic.Common", "Mic.Emit", "Mic.Linq");
  }

  [TestMethod]
  public void Emitter_DoesNotReferenceTheOrm() {
    // THE OTHER DIRECTION, and the one this rule exists for. Mic.Emit builds classes over interfaces and must know
    // nothing about entities, sessions or databases; the ORM's knowledge lives in the controller it supplies. That is
    // easy to say and easy to lose — one convenient reference, added to solve one awkward member, and the emitter is
    // an ORM component wearing a general name.
    //
    // It is checked here rather than argued in a comment because this is the kind of claim that decays silently: an
    // emitter with an ORM inside it still works.
    var referenced = Assembly.Load("Mic.Emit").GetReferencedAssemblies()
      .Select(a => a.Name ?? string.Empty)
      .Where(name => name.StartsWith("Mic.", StringComparison.Ordinal))
      .ToList();
    Assert.AreEqual(0, referenced.Count,
      $"Mic.Emit must reference nothing of MIC's and now references {string.Join(", ", referenced)} — "
      + "see ORM_VISION.md, 'The emitter is its own thing'.");
  }

  [TestMethod]
  public void LinqEngine_DoesNotReferenceTheOrm() {
    // THE SAME SHAPE AS THE EMITTER'S RULE, and for a sharper reason. Mic.Linq is shared with the Blazor client, where
    // there is no database, no session and no entity — so it must know none of them. What it needs from a host it
    // DECLARES (`ILinqDataModel`, `ILinqCallContext`) and the host answers — the model, the
    // entities made for a query, the rows of them — which is why the reference runs ORM → Linq and never back.
    //
    // Asserted as a whitelist rather than a forbidden list: this one is allowed exactly ONE of MIC's assemblies, and
    // naming it is stricter than listing what it must avoid — a reference to Mic.Modeling or Mic.Data would slip
    // through a forbidden list and is just as wrong.
    var allowed = new[] { "Mic.Common" };
    var referenced = Assembly.Load("Mic.Linq").GetReferencedAssemblies()
      .Select(a => a.Name ?? string.Empty)
      .Where(name => name.StartsWith("Mic.", StringComparison.Ordinal) && !allowed.Contains(name))
      .ToList();
    Assert.AreEqual(0, referenced.Count,
      $"Mic.Linq may reference only {string.Join(" and ", allowed)}, and now references "
      + $"{string.Join(", ", referenced)} — see LINQ_VISION.md, 'On the client'.");
  }

  private static void AssertReferencesOnly(string assemblyName, params string[] allowed) {
    var referenced = Assembly.Load(assemblyName).GetReferencedAssemblies()
      .Select(a => a.Name ?? string.Empty)
      .Where(name => name.StartsWith("Mic.", StringComparison.Ordinal) && !allowed.Contains(name))
      .ToList();
    Assert.AreEqual(0, referenced.Count,
      $"{assemblyName} may reference only {string.Join(", ", allowed)} of MIC's, and now references "
      + $"{string.Join(", ", referenced)}.");
  }

  private static void AssertNoForbiddenReferences(string assemblyName) {
    var assembly = Assembly.Load(assemblyName);
    var violations = assembly.GetReferencedAssemblies()
      .Select(a => a.Name ?? string.Empty)
      .Where(name => ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
      .ToList();
    Assert.AreEqual(0, violations.Count,
      $"{assemblyName} must not reference {string.Join(", ", violations)} — see ORM_VISION.md, 'Standalone use'.");
  }
}
