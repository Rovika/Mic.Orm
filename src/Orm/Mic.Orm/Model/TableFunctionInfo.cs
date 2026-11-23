using Mic.Orm.Db;

namespace Mic.Orm.Model;

/// <summary>Represents the function of the server an entity's rows come from: its name, its parameters, and the
/// body written per server where MIC defines it.</summary>
/// <remarks>The entity gives the rows their shape; the function gives them their values, for the arguments of a
/// call. The rows are read-only, and the entity's primary key is optional: without one, the rows are not tracked.
/// </remarks>
public class TableFunctionInfo {
  /// <summary>Gets the entity whose rows the function returns.</summary>
  public EntityTypeInfo Entity { get; }

  /// <summary>Gets the function's name in the database, as the module declared it.</summary>
  public string Name { get; }

  /// <summary>Gets the function's parameters, by name and type, in order.</summary>
  public IReadOnlyList<TableFunctionParameter> Parameters { get; }

  /// <summary>Gets the body that defines the function, by server kind; or <see langword="null"/> for a function
  /// that exists in the database and MIC never creates, changes or drops.</summary>
  public IReadOnlyDictionary<DbServerKind, string>? BodyByServer { get; }

  /// <summary>Gets a value that indicates whether MIC creates, changes and drops the function.</summary>
  public bool IsManaged => BodyByServer != null;

  public TableFunctionInfo(EntityTypeInfo entity, string name, IReadOnlyList<TableFunctionParameter> parameters,
      IReadOnlyDictionary<DbServerKind, string>? bodyByServer) {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(parameters);
    Entity = entity;
    Name = name;
    Parameters = parameters;
    BodyByServer = bodyByServer;
  }

  /// <summary>Finds the body written for a server kind.</summary>
  /// <param name="server">The server kind.</param>
  /// <returns>The body, or <see langword="null"/> if none was written for the server, or the function is not MIC's.
  /// </returns>
  public string? FindBody(DbServerKind server) =>
    BodyByServer != null && BodyByServer.TryGetValue(server, out var body) ? body : null;

  public override string ToString() => $"{Name}({Parameters.Count}) → {Entity.Name}";
}

/// <summary>Represents one parameter of a table function: its name, as the body refers to it, and the CLR type of
/// the value a call passes.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The CLR type of the value.</param>
public sealed record TableFunctionParameter(string Name, Type Type);
