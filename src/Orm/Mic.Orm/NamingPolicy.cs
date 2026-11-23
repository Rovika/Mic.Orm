using Mic.Orm.Db;
using Mic.Orm.Model;

namespace Mic.Orm;

/// <summary>Derives entity and database object names from declarations, with a default convention that needs no
/// configuration.</summary>
/// <remarks>An application that has to match names already in a database replaces the policy through
/// <see cref="EntityAppSettings.NamingPolicy"/> rather than naming every entity and member explicitly.</remarks>
public class NamingPolicy {
  /// <summary>Gets the entity name of a declared interface.</summary>
  /// <param name="entityInterface">The entity interface.</param>
  /// <returns>The interface name without its leading <c>I</c> when the next character is a capital, as in
  /// <c>IPerson</c> to <c>Person</c>; otherwise, the interface name.</returns>
  public virtual string GetEntityName(Type entityInterface) {
    var name = entityInterface.Name;
    if (name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]))
      return name[1..];
    return name;
  }

  // ----- database names ---------------------------------------------------------------------------------------------
  //
  // Each is consulted as that name is produced, not afterwards: an index name is derived from its columns' final names.

  /// <summary>Gets the table name of an entity.</summary>
  /// <param name="entity">The entity.</param>
  /// <returns>The name given by <c>[Entity(TableName = …)]</c>, or the entity name when none is given.</returns>
  public virtual string GetTableName(EntityTypeInfo entity) => entity.TableName ?? entity.Name;

  /// <summary>Gets the column name of a member, including a foreign-key member the ORM generated.</summary>
  /// <param name="member">The member.</param>
  /// <returns>The declared column name, or the member name when none is declared.</returns>
  public virtual string GetColumnName(EntityMemberInfo member) => member.ColumnName ?? member.Name;

  /// <summary>Gets the database schema name of a model schema.</summary>
  /// <param name="schemaName">The schema name in the model.</param>
  /// <returns>The schema name in the database.</returns>
  public virtual string GetSchemaName(string schemaName) => schemaName;

  /// <summary>Gets the name of the member a reference generates to hold one column of its foreign key.</summary>
  /// <param name="referenceMemberName">The name of the reference member.</param>
  /// <param name="targetKeyMemberName">The name of the member of the target key the column holds.</param>
  /// <returns>The two names joined by an underscore, as in <c>Address_Id</c>.</returns>
  /// <remarks>Composing the reference's name with the target key member's name is what makes a composite target
  /// work: one reference generates one column per target key member.</remarks>
  public virtual string GetForeignKeyMemberName(string referenceMemberName, string targetKeyMemberName) =>
    referenceMemberName + "_" + targetKeyMemberName;

  /// <summary>Gets the name of a table on a server without schemas, where the schema is prefixed to the name.</summary>
  /// <param name="schemaName">The schema name.</param>
  /// <param name="tableName">The table name.</param>
  /// <returns>The two names joined by an underscore.</returns>
  public virtual string GetSchemaPrefixedTableName(string schemaName, string tableName) =>
    schemaName + "_" + tableName;

  /// <summary>Gets the name of a key or index, which for a key is the name of the database constraint.</summary>
  /// <param name="entityName">The name of the entity the key belongs to.</param>
  /// <param name="flags">A bitwise combination of the enumeration values that specifies the kind of key.</param>
  /// <param name="memberNames">The names of the key's members.</param>
  /// <returns>A prefix (<c>PK</c>, <c>FK</c>, <c>UQ</c> or <c>IX</c>), the entity name and the member names, joined by
  /// underscores.</returns>
  public virtual string GetKeyName(string entityName, KeyFlags flags, IEnumerable<string> memberNames) {
    var prefix = flags.HasFlag(KeyFlags.PrimaryKey) ? "PK"
      : flags.HasFlag(KeyFlags.ForeignKey) ? "FK"
      : flags.HasFlag(KeyFlags.Unique) ? "UQ"
      : "IX";
    var members = string.Join("_", memberNames);
    return members.Length == 0 ? $"{prefix}_{entityName}" : $"{prefix}_{entityName}_{members}";
  }

  /// <summary>Gets the plural form of an entity name, used where a name for a set is wanted.</summary>
  /// <param name="entityName">The entity name.</param>
  /// <returns>The plural form, by a simple English rule.</returns>
  /// <remarks>An entity that needs a better plural declares it with <c>[Entity(PluralName = …)]</c>.</remarks>
  public virtual string GetPluralName(string entityName) =>
    entityName.EndsWith('s') || entityName.EndsWith("sh") || entityName.EndsWith("ch")
      ? entityName + "es"
      : entityName.EndsWith('y') && entityName.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(entityName[^2]))
        ? entityName[..^1] + "ies"
        : entityName + "s";
}
