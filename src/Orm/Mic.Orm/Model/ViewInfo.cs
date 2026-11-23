using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm.Db;

namespace Mic.Orm.Model;

/// <summary>Represents the definition of a view an entity reads through: how the view is defined, and the query or
/// the SQL that defines it.</summary>
/// <remarks>The entity gives the view its shape; the definition gives it its rows. A view's rows are read-only.
/// </remarks>
public class ViewInfo {
  /// <summary>Gets the entity that reads through the view.</summary>
  public EntityTypeInfo Entity { get; }

  /// <summary>Gets how the view is defined.</summary>
  public ViewDefinitionKind Kind { get; }

  /// <summary>Gets or sets a value that indicates whether the view is materialized where the server can.</summary>
  public bool IsMaterialized { get; set; }

  /// <summary>Gets or sets the typed query that defines the view, written over <see cref="ViewHelper.EntitySet{T}"/>;
  /// or <see langword="null"/> for a view not defined by a typed query.</summary>
  public IQueryable? TypedQuery { get; set; }

  /// <summary>Gets or sets the function that builds the query tree defining the view over a database's model, with
  /// <see cref="Mic.Linq.QueryMaker"/>; or <see langword="null"/> for a view not defined by a tree.</summary>
  public Func<ILinqDataModel, QueryNode>? QueryBuilder { get; set; }

  /// <summary>Gets or sets the SQL that defines the view, by server kind; or <see langword="null"/> for a view not
  /// defined by SQL.</summary>
  public IReadOnlyDictionary<DbServerKind, string>? SqlByServer { get; set; }

  /// <summary>Gets or sets the entities of the views and tables this view's definition reads, as declared; a view
  /// among them is created before this one and this one is recreated with it, and a table's column change recreates
  /// this view too.</summary>
  /// <remarks>A query-defined view's tables are known from its translation; naming them is for a view defined by
  /// SQL, whose text the model does not read.</remarks>
  public IReadOnlyList<EntityTypeInfo> DependsOn { get; set; } = [];

  /// <summary>Initializes a new instance of the <see cref="ViewInfo"/> class.</summary>
  /// <param name="entity">The entity that reads through the view.</param>
  /// <param name="kind">One of the enumeration values that specifies how the view is defined.</param>
  public ViewInfo(EntityTypeInfo entity, ViewDefinitionKind kind) {
    ArgumentNullException.ThrowIfNull(entity);
    Entity = entity;
    Kind = kind;
  }

  /// <summary>Finds the SQL written for a server kind.</summary>
  /// <param name="server">The server kind.</param>
  /// <returns>The SQL, or <see langword="null"/> if none was written for the server.</returns>
  public string? FindSql(DbServerKind server) =>
    SqlByServer != null && SqlByServer.TryGetValue(server, out var sql) ? sql : null;

  public override string ToString() => $"{Entity.Name} ({Kind} view)";
}
