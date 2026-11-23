using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
#if (Postgres)
using Mic.Orm.Db.Postgres;
#endif
#if (MySql)
using Mic.Orm.Db.MySql;
#endif
#if (MsSql)
using Mic.Orm.Db.SqlServer;
#endif
#if (Oracle)
using Mic.Orm.Db.Oracle;
#endif
#if (SQLite)
using Mic.Orm.Db.Sqlite;
#endif
#if (Db2)
using Mic.Orm.Db.Db2;
#endif
using Shop.Model;

namespace Shop;

/// <summary>Connects the application to its database.</summary>
/// <remarks>This is the one place that knows which server the application runs on. The model does not: it is the
/// same for every server MIC supports.</remarks>
public static class DatabaseHelper {
  /// <summary>Creates the provider for the server the application was made for.</summary>
  /// <returns>The provider.</returns>
#if (Postgres)
  public static DbProvider CreateProvider() => new PostgresProvider();
#endif
#if (MySql)
  public static DbProvider CreateProvider() => new MySqlProvider();
#endif
#if (MsSql)
  public static DbProvider CreateProvider() => new SqlServerProvider();
#endif
#if (Oracle)
  public static DbProvider CreateProvider() => new OracleProvider();
#endif
#if (SQLite)
  public static DbProvider CreateProvider() => new SqliteProvider();
#endif
#if (Db2)
  public static DbProvider CreateProvider() => new Db2Provider();
#endif

  /// <summary>Builds the application and connects it, bringing the database up to the model.</summary>
  /// <param name="config">The host's settings.</param>
  /// <returns>The application, connected.</returns>
  /// <remarks>The upgrade runs on every connect unless the database declares itself production, which
  /// <see cref="DbSettings.UpgradeMode"/> governs; a production database is upgraded by the script
  /// <c>mic-db upgrade</c> writes.</remarks>
  public static async Task<ShopApp> ConnectAsync(AppConfig config) {
    var app = new ShopApp();
    app.Init();
    await app.ConnectToAsync(new DbSettings(CreateProvider(), config.ConnectionString));
    return app;
  }
}
