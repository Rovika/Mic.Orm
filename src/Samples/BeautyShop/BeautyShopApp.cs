using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Extensions.MultiTenancy;

namespace BeautyShop;

/// <summary>The module that declares the Beauty Shop entities.</summary>
/// <remarks>ONE MODULE, not several. A module is a unit of declaration and versioning, and splitting this model into
/// scheduling / inventory / payroll would suggest they version independently, which they do not — they are one
/// application released together. When modules take a larger role in MIC, this is a natural place to revisit.</remarks>
public class BeautyShopModule : EntityModule {
  public const string ModuleName = "BeautyShop";

  public BeautyShopModule() : base(ModuleName, new Version(1, 0)) {
    RegisterEntities(
      typeof(ITenant), typeof(ICompany), typeof(IUser),
      typeof(IAddress), typeof(IPhone), typeof(IPerson),
      typeof(IShop), typeof(IShopHours), typeof(IEmployee), typeof(IShopEmployee), typeof(ICustomer),
      typeof(IAvailability),
      typeof(IServiceCategory), typeof(IService), typeof(IServiceStation),
      typeof(IAppointment), typeof(IAppointmentService),
      typeof(IVendor), typeof(IProductCategory), typeof(IProduct), typeof(IStockLevel),
      typeof(IShipment), typeof(IShipmentLine),
      typeof(IPaymentSource), typeof(IVendorInvoice), typeof(IVendorPayment),
      typeof(ISale), typeof(ISaleLine), typeof(IPayment),
      typeof(ITimeEntry), typeof(IPayrollRun), typeof(IPayrollLine));
    RegisterViews();
    RegisterShopDaySchedule();
  }

  /// <summary>Declares the three views, each a LINQ query over the entity sets, each projecting the tenant's id for
  /// the extension to filter on.</summary>
  private void RegisterViews() {
    RegisterView<IUpcomingAppointment>(
      from a in ViewHelper.EntitySet<IAppointment>()
      where a.Status == AppointmentStatus.Requested || a.Status == AppointmentStatus.Confirmed
         || a.Status == AppointmentStatus.CheckedIn
      select new {
        AppointmentId = a.Id,
        TenantId = a.Tenant.Id,
        CompanyName = a.Shop.Company.Name,
        ShopName = a.Shop.Name,
        a.StartsOn,
        a.EndsOn,
        a.Status,
        CustomerName = a.Customer.Person.FirstName + " " + a.Customer.Person.LastName,
        StylistName = a.Employee.Person.FirstName + " " + a.Employee.Person.LastName,
        BookedValue = a.Services.Sum(s => (decimal?)s.Price) ?? 0m,
      });

    // An entity set a sub-query reads is taken outside the query and used inside it, as the engine asks.
    var appointments = ViewHelper.EntitySet<IAppointment>();
    var lines = ViewHelper.EntitySet<ISaleLine>();
    RegisterView<IStylistRevenue>(
      from e in ViewHelper.EntitySet<IEmployee>()
      select new {
        EmployeeId = e.Id,
        TenantId = e.Tenant.Id,
        StylistName = e.Person.FirstName + " " + e.Person.LastName,
        HomeShopName = e.HomeShop!.Name,
        AppointmentCount = appointments.Count(a => a.Employee.Id == e.Id && a.Status == AppointmentStatus.Completed),
        RevenueAmount = lines.Where(l => l.PerformedBy!.Id == e.Id).Sum(l => (decimal?)(l.UnitPrice * l.Quantity)) ?? 0m,
        CommissionAmount = (lines.Where(l => l.PerformedBy!.Id == e.Id).Sum(l => (decimal?)(l.UnitPrice * l.Quantity)) ?? 0m)
          * e.CommissionPercent / 100,
      });

    RegisterView<ILowStock>(
      from s in ViewHelper.EntitySet<IStockLevel>()
      where s.QuantityOnHand <= s.ReorderLevel
      select new {
        s.Id,
        TenantId = s.Tenant.Id,
        ShopName = s.Shop.Name,
        s.Product.Sku,
        ProductName = s.Product.Name,
        s.QuantityOnHand,
        s.ReorderLevel,
      });
  }

  /// <summary>Declares the table function <c>fn_ShopDaySchedule(shop, day)</c>: a shop's appointments on one day,
  /// in order. Its body is PostgreSQL's; the sample targets one server.</summary>
  private void RegisterShopDaySchedule() {
    const string body = """
      SELECT a."StartsOn", a."EndsOn",
             (ep."FirstName" || ' ' || ep."LastName")::varchar(121) AS "StylistName",
             (cp."FirstName" || ' ' || cp."LastName")::varchar(121) AS "CustomerName"
      FROM beauty."Appointment" a
           JOIN beauty."Employee" e  ON e."Id"  = a."Employee_Id"
           JOIN beauty."Person"   ep ON ep."Id" = e."Person_Id"
           JOIN beauty."Customer" c  ON c."Id"  = a."Customer_Id"
           JOIN beauty."Person"   cp ON cp."Id" = c."Person_Id"
      WHERE a."Shop_Id" = p_shop AND a."StartsOn"::date = p_day
      ORDER BY a."StartsOn"
      """;
    RegisterTableFunction<IShopDaySlot>("fn_ShopDaySchedule",
      new Dictionary<DbServerKind, string> { [DbServerKind.Postgres] = body },
      ("p_shop", typeof(Guid)), ("p_day", typeof(DateOnly)));
  }
}

/// <summary>The Beauty Shop sample application.</summary>
/// <remarks>The one `EntityApp` MIC has that is meant to be USED rather than tested — everything else lives inside
/// the test suite, and `Mic.TestApp.PeopleModel` there is not one at all: it is DTOs and enums with no Mic reference,
/// since the People sample is document-authored.
///
/// ITS OWN DATABASE, and its own schema inside it. The database keeps the sample clear of the test suites, which share
/// one and create their own schemas in it; the named schema keeps the sample honest, since a real application rarely
/// lives in `public` and a browser pointed at one that does learns nothing.
///
/// `beauty` IS ALSO THE DEFAULT SCHEMA, not only the module's, and that is what puts `DbInfo` there with everything
/// else. `EntityApp` adds its bookkeeping module itself, with no schema, so it takes the application's default —
/// which, left unset, is the server's own and lands MIC's own table in `public` while the application it describes is
/// somewhere else entirely.</remarks>
public class BeautyShopApp : EntityApp {
  public const string SchemaName = "beauty";

  /// <summary>Where the sample lives when nobody says otherwise — the local test PostgreSQL, its own database.
  /// </summary>
  /// <remarks>A constant so that a page, a seeder and a person at a psql prompt all mean the same database. It is a
  /// development server with development credentials and is not a secret; a real deployment supplies its own.</remarks>
  public const string DefaultConnectionString =
    "Host=localhost;Port=5433;Database=BeautyShop;Username=mic;Password=mic";

  /// <summary>Initializes a new instance of the <see cref="BeautyShopApp"/> class: the module in its schema, and the
  /// multi-tenancy extension.</summary>
  public BeautyShopApp() : base("BeautyShop", new Version(1, 0),
      new EntityAppSettings { DefaultSchema = SchemaName }) {
    AddModule(new BeautyShopModule(), SchemaName);
    AddExtension(new MultiTenantExtension());
  }
}
