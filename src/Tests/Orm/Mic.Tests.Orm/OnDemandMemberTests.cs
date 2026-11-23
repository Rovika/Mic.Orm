using Mic.Linq.Model;
using Mic.Orm;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// AN ON-DEMAND MEMBER, without a server: what [OnDemand] marks, and the members it is refused on.
[TestClass]
public class OnDemandMemberTests {
  public interface IArticle {
    [PrimaryKey] Guid Id { get; set; }
    [Size(60)] string Title { get; set; }
    [OnDemand, Unlimited] string Body { get; set; }
  }

  public interface IBadArticle {
    [PrimaryKey, OnDemand] Guid Id { get; set; }
    [OnDemand] IBadArticle? Parent { get; set; }
    [OnDemand] IList<IBadArticle> Children { get; }
    [OnDemand, NoColumn] string? Scratch { get; set; }
    [OnDemand, RowVersion] int Version { get; set; }
    [OnDemand, Unique, Size(20)] string Code { get; set; }
  }

  public interface ICachedArticle {
    [PrimaryKey] Guid Id { get; set; }
    [OnDemand, Unlimited] string? Body { get; set; }
  }

  [TestMethod]
  public void OnDemandMember_IsMarkedOnTheModel() {
    var app = NewApp(typeof(IArticle));
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IArticle))!;

    Assert.IsTrue(entity.GetMember("Body").IsOnDemand);
    Assert.IsTrue(((ILinqEntityMember)entity.GetMember("Body")).IsOnDemand, "as the query engine reads the member");
    Assert.IsFalse(entity.GetMember("Title").IsOnDemand);
    Assert.IsTrue(entity.GetMember("Body").MemberFlags.HasFlag(MemberFlags.Unlimited), "the other attributes stand");
  }

  [TestMethod]
  public void OnDemandMember_RefusesWhatItCannotBe() {
    var refused = NewApp(typeof(IBadArticle));
    Assert.IsFalse(refused.Initialized);
    var report = refused.Report();
    StringAssert.Contains(report, "has [OnDemand] and is part of the primary key");
    StringAssert.Contains(report, "has [OnDemand] and is not a column member");
    StringAssert.Contains(report, "BadArticle.Parent");
    StringAssert.Contains(report, "BadArticle.Children");
    StringAssert.Contains(report, "BadArticle.Scratch");
    StringAssert.Contains(report, "has [OnDemand] and is the row version");
    StringAssert.Contains(report, "has [OnDemand] and is part of a unique key");

    // ----- a cached entity's rows are held whole -------------------------------------------------------------------
    var cached = NewApp(typeof(ICachedArticle), app => app.SetCached(CacheType.Server, [typeof(ICachedArticle)]));
    Assert.IsFalse(cached.Initialized);
    StringAssert.Contains(cached.Report(), "is cached and has an [OnDemand] member, 'Body'");
  }

  static EntityApp NewApp(Type entity, Action<EntityApp>? configure = null) {
    var app = new EntityApp("OnDemand", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entity);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    configure?.Invoke(app);
    app.TryInit();
    return app;
  }
}
