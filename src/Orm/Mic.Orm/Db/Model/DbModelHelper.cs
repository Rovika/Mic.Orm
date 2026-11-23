namespace Mic.Orm.Db.Model;

/// <summary>Provides helper methods over the objects of a database model.</summary>
public static class DbModelHelper {
  /// <summary>Gets the qualified path of a db-model object, such as <c>schema.Table.Column</c>.</summary>
  /// <param name="obj">The object, or <see langword="null"/>.</param>
  /// <returns>The path; a schema's and the model's own name; <c>?</c> for <see langword="null"/>.</returns>
  public static string GetPath(DbModelObject? obj) => obj switch {
    null => "?",
    DbColumnInfo column => column.Table == null
      ? column.Name
      : $"{column.Table.Schema.Name}.{column.Table.Name}.{column.Name}",
    DbKeyInfo key => $"{key.Table.Schema.Name}.{key.Table.Name}.{key.Name}",
    DbRefConstraintInfo constraint =>
      $"{constraint.FromTable.Schema.Name}.{constraint.FromTable.Name}.{constraint.Name}",
    DbCheckConstraintInfo check => $"{check.Table.Schema.Name}.{check.Table.Name}.{check.Name}",
    DbTableInfo table => $"{table.Schema.Name}.{table.Name}",
    DbViewInfo view => $"{view.Schema.Name}.{view.Name}",
    DbRoutineInfo routine => $"{routine.Schema.Name}.{routine.Name}",
    DbSequenceInfo sequence => $"{sequence.Schema.Name}.{sequence.Name}",
    _ => obj.Name,      // a schema, and the model itself
  };
}
