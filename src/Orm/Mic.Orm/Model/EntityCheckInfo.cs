using Mic.Orm.Db;

namespace Mic.Orm.Model;

/// <summary>Represents a check constraint an entity declares, as its <c>[Check]</c> attribute wrote it.</summary>
/// <param name="Name">The name of the check within its entity.</param>
/// <param name="Expression">The condition in the server's SQL; a member of the entity written as <c>{Name}</c>
/// stands for its column.</param>
/// <param name="ServerKind">The server kind the declaration is written for, or <see langword="null"/> for every
/// server.</param>
public sealed record EntityCheckInfo(string Name, string Expression, DbServerKind? ServerKind);
