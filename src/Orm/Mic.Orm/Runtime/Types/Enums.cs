
namespace Mic.Orm.Runtime;
/// <summary>Specifies what a request's caller asked for beyond the request itself, and what the executor's preparation
/// found about it.</summary>
[Flags]
public enum DataRequestFlags {
  None = 0,

  /// <summary>The row read by key is locked for writing until the transaction ends. A locked read goes to the server
  /// whatever is cached.</summary>
  LockForUpdate = 1,

  /// <summary>The row read by key is read from the server whatever the entity cache holds; set from
  /// <see cref="LoadFlags.SkipEntityCache"/>.</summary>
  SkipEntityCache = 1 << 3,

  /// <summary>The save writes the records, reads back only what it needs, and stops tracking them; set from
  /// <see cref="SaveOptions.InsertAndForget"/>.</summary>
  InsertAndForget = 1 << 1,

  /// <summary>The query is answered from the entity cache: a read by key of a cached entity, or a shape with a
  /// <see cref="DataRequest.CacheQuery"/>. Set by the executor's preparation.</summary>
  CanRunInCache = 1 << 2,

  /// <summary>A row read for a record the session tracks replaces the record's values in the members that were not
  /// assigned. Set for a read under a lock.</summary>
  RefreshTracked = 1 << 4,
}

