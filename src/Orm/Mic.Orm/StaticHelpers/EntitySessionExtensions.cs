using System.Data;
using System.Linq.Expressions;
using Mic.Linq;
using Mic.Orm.Db;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides what an application does with a session beyond <see cref="IEntitySession"/>: the transaction,
/// the options, the log, what the session holds and what it last ran, and the conveniences of Vita.</summary>
/// <remarks>Every method works on the <see cref="IEntitySession"/> an application holds; the session class is
/// reached inside.</remarks>
public static partial class EntitySessionExtensions {
  // ----- the transaction ----------------------------------------------------------------------------------------

  /// <summary>Asynchronously begins a transaction on the session's connection; every request until it ends runs
  /// inside it.</summary>
  /// <param name="session">The session.</param>
  /// <param name="isolationLevel">The isolation level; the server's default where unspecified.</param>
  /// <param name="commitOnSave"><see langword="true"/> to have the next save commit the transaction; otherwise,
  /// <see langword="false"/>.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  public static Task BeginTransactionAsync(this IEntitySession session,
      IsolationLevel isolationLevel = IsolationLevel.Unspecified, bool commitOnSave = false) =>
    GetSession(session).BeginTransactionAsync(isolationLevel, commitOnSave);

  /// <summary>Asynchronously commits the open transaction, if there is one.</summary>
  public static Task CommitTransactionAsync(this IEntitySession session) => GetSession(session).CommitTransactionAsync();

  /// <summary>Asynchronously rolls back the open transaction, if there is one.</summary>
  public static Task RollbackTransactionAsync(this IEntitySession session) =>
    GetSession(session).RollbackTransactionAsync();

  /// <summary>Determines whether a transaction is open on the session's connection.</summary>
  public static bool HasOpenTransaction(this IEntitySession session) => GetSession(session).HasOpenTransaction;

  // ----- the options --------------------------------------------------------------------------------------------

  public static EntitySessionOptions GetOptions(this IEntitySession session) => GetSession(session).Options;

  /// <summary>Sets the options of the session; they apply from the next request.</summary>
  /// <remarks><see cref="EntitySessionOptions.AddThreadSafety"/> is fixed when the session is opened and stays as
  /// it was.</remarks>
  public static void SetOptions(this IEntitySession session, EntitySessionOptions options) {
    const EntitySessionOptions FixedAtOpen = EntitySessionOptions.AddThreadSafety;
    var target = GetSession(session);
    target.Options = (options & ~FixedAtOpen) | (target.Options & FixedAtOpen);
  }

  public static bool IsSet(this EntitySessionOptions options, EntitySessionOptions option) => (options & option) != 0;

  // ----- the extensions disabled on the session -----------------------------------------------------------------

  /// <summary>Disables an extension for the rest of the session: its filters are left out of every read, and its
  /// write hooks skip the session's saves.</summary>
  /// <typeparam name="TExtension">The extension; a derived extension counts as disabled with its base.</typeparam>
  /// <param name="session">The session.</param>
  /// <remarks>For a job that works outside what the extension enforces: a purge that removes soft-deleted rows, a
  /// migration that moves rows between tenants. The session's queries bypass the query cache from then on, and
  /// read nothing from the entity cache.</remarks>
  public static void DisableExtension<TExtension>(this IEntitySession session) where TExtension : AppExtension =>
    GetSession(session).DisableExtension(typeof(TExtension));

  /// <summary>Disables every extension for the rest of the session.</summary>
  /// <param name="session">The session.</param>
  /// <remarks>See <see cref="DisableExtension{TExtension}"/>.</remarks>
  public static void DisableExtensions(this IEntitySession session) => GetSession(session).DisableAllExtensions();

  // ----- what the session holds, and what it ran ----------------------------------------------------------------

  /// <summary>Gets the records the next save will write: new, modified and marked for deletion.</summary>
  public static IReadOnlyList<EntityRecord> GetChangedRecords(this IEntitySession session) =>
    GetSession(session).ChangedRecords;

  /// <summary>Gets the number of records the next save will write.</summary>
  public static int GetChangeCount(this IEntitySession session) => GetSession(session).ChangedRecords.Count;

  /// <summary>Gets the set-based mutations held for the next save, in the order scheduled.</summary>
  public static IReadOnlyList<ScheduledMutation> GetScheduledMutations(this IEntitySession session) =>
    GetSession(session).ScheduledMutations;

  /// <summary>Gets the commands the last request ran, with their row counts and timings.</summary>
  public static IReadOnlyList<DataCommandSnapshot> GetLastCommands(this IEntitySession session) =>
    GetSession(session).LastCommands;

  /// <summary>Gets the last command the last request ran.</summary>
  /// <returns>The command, or <see langword="null"/> when the session has run nothing.</returns>
  public static DataCommandSnapshot? GetLastCommand(this IEntitySession session) =>
    GetSession(session).LastCommands is { Count: > 0 } commands ? commands[^1] : null;

  // ----- the log ------------------------------------------------------------------------------------------------

  /// <summary>Records a message in the operation's log.</summary>
  /// <param name="session">The session.</param>
  /// <param name="message">The message, a composite format string where <paramref name="args"/> are given.</param>
  /// <param name="args">The values the message formats.</param>
  public static void LogMessage(this IEntitySession session, string message, params object?[] args) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(message);
    session.Context.Log(args.Length == 0 ? message : string.Format(message, args));
  }

  /// <summary>Switches the operation's log on, with every detail, or off.</summary>
  /// <param name="session">The session.</param>
  /// <param name="enable"><see langword="true"/> for <see cref="LogMode.Details"/>; <see langword="false"/> for
  /// <see cref="LogMode.None"/>.</param>
  /// <remarks>The log is the operation's: the switch applies to every session of the context.</remarks>
  public static void EnableLog(this IEntitySession session, bool enable = true) {
    ArgumentNullException.ThrowIfNull(session);
    session.Context.LogMode = enable ? LogMode.Details : LogMode.None;
  }

  // ----- the conveniences of Vita -------------------------------------------------------------------------------

  /// <summary>Gets a page of an entity's rows.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="orderBy">The member to order by, or <see langword="null"/> for the server's order.</param>
  /// <param name="descending"><see langword="true"/> for descending order; otherwise, <see langword="false"/>.</param>
  /// <param name="skip">The number of rows to skip, or <see langword="null"/> for none.</param>
  /// <param name="take">The number of rows to take, or <see langword="null"/> for all.</param>
  /// <returns>The entities.</returns>
  /// <remarks>A page, <paramref name="skip"/> or <paramref name="take"/>, needs <paramref name="orderBy"/>: the query
  /// engine refuses a page of an unordered set.</remarks>
  public static IList<TEntity> GetEntities<TEntity>(this IEntitySession session,
      Expression<Func<TEntity, object?>>? orderBy = null, bool descending = false, int? skip = null, int? take = null)
      where TEntity : class =>
    GetSession(session).RunSync(() => session.GetEntitiesAsync(orderBy, descending, skip, take));

  /// <summary>Asynchronously gets a page of an entity's rows.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session.</param>
  /// <param name="orderBy">The member to order by, or <see langword="null"/> for the server's order.</param>
  /// <param name="descending"><see langword="true"/> for descending order; otherwise, <see langword="false"/>.</param>
  /// <param name="skip">The number of rows to skip, or <see langword="null"/> for none.</param>
  /// <param name="take">The number of rows to take, or <see langword="null"/> for all.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the entities.</returns>
  public static async Task<IList<TEntity>> GetEntitiesAsync<TEntity>(this IEntitySession session,
      Expression<Func<TEntity, object?>>? orderBy = null, bool descending = false, int? skip = null, int? take = null)
      where TEntity : class {
    ArgumentNullException.ThrowIfNull(session);
    var query = session.EntitySet<TEntity>();
    if (orderBy != null)
      query = descending ? query.OrderByDescending(orderBy) : query.OrderBy(orderBy);
    if (skip != null)
      query = query.Skip(skip.Value);
    if (take != null)
      query = query.Take(take.Value);
    return [.. await query.ToListAsync()];
  }

  /// <summary>Creates a new entity with the column values of another.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session the copy is created in.</param>
  /// <param name="entity">The entity to copy.</param>
  /// <param name="copyPrimaryKey"><see langword="true"/> to copy the primary key too; otherwise,
  /// <see langword="false"/>, and the copy gets its own.</param>
  /// <returns>The copy, tracked by the session and written by the next save.</returns>
  /// <remarks>References, lists, computed members and values the database generates are not copied.</remarks>
  public static TEntity NewCopyOf<TEntity>(this IEntitySession session, TEntity entity, bool copyPrimaryKey = false)
      where TEntity : class {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(entity);
    var copy = session.NewEntity<TEntity>();
    foreach (var member in EntityHelper.GetEntityInfo(entity).Members) {
      if (member.Kind != MemberKind.Column || member.Computed != null || member.DbComputed != null || member.IsDbGenerated
          || member.OwnerReference != null || (member.IsPrimaryKey && !copyPrimaryKey))
        continue;
      EntityHelper.SetProperty(copy, member.Name, EntityHelper.GetProperty(entity, member.Name));
    }
    return copy;
  }

  /// <summary>Gets the id of the session's next save, the same until a save has written.</summary>
  /// <param name="session">The session.</param>
  /// <returns>The id.</returns>
  /// <remarks>For an application's own audit rows: the save's statements are logged with it.</remarks>
  public static Guid GetNextTransactionId(this IEntitySession session) => GetSession(session).NextTransactionId;

  /// <summary>Draws the next value of a sequence.</summary>
  /// <param name="session">The session to draw through.</param>
  /// <param name="sequence">The sequence, as the module registered it.</param>
  /// <returns>The value.</returns>
  public static long GetSequenceNextValue(this IEntitySession session, SequenceDefinition sequence) =>
    GetSession(session).RunSync(() => session.GetSequenceNextValueAsync(sequence));

  /// <summary>Draws the next value of a sequence.</summary>
  /// <param name="session">The session to draw through.</param>
  /// <param name="sequenceName">The sequence's name, with its schema in front where two schemas have one of that
  /// name.</param>
  /// <returns>The value.</returns>
  public static long GetSequenceNextValue(this IEntitySession session, string sequenceName) =>
    GetSession(session).RunSync(() => session.GetNextSequenceValueAsync(sequenceName));

  /// <summary>Asynchronously draws the next value of a sequence.</summary>
  /// <param name="session">The session to draw through.</param>
  /// <param name="sequence">The sequence, as the module registered it.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the value.</returns>
  public static Task<long> GetSequenceNextValueAsync(this IEntitySession session, SequenceDefinition sequence) {
    ArgumentNullException.ThrowIfNull(sequence);
    return session.GetNextSequenceValueAsync(sequence.Name);
  }

  /// <summary>Asynchronously loads an on-demand member of an entity: the load the member's getter would run on the
  /// calling thread.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="session">The session the entity belongs to.</param>
  /// <param name="entity">The entity.</param>
  /// <param name="member">An expression that names the member, such as <c>a => a.Body</c>.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>An entity that holds the member completes at once and runs nothing. Where the smart load is on, the
  /// member is loaded for every entity of the type the session tracks that lacks it, in the same statement.
  /// </remarks>
  /// <exception cref="ArgumentException">The member is not marked <c>[OnDemand]</c>. -or- The entity belongs to
  /// another session.</exception>
  public static Task LoadMemberAsync<TEntity>(this IEntitySession session, TEntity entity,
      Expression<Func<TEntity, object?>> member) where TEntity : class {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(member);
    var entSession = GetSession(session);
    var record = EntityHelper.GetRecord(entity);
    if (!ReferenceEquals(record.Session, entSession))
      throw new ArgumentException($"The '{record.Entity.Name}' belongs to another session, or to none.", nameof(entity));
    var name = EntityHelper.GetMemberName(member);
    var info = record.Entity.FindMember(name);
    if (info is not { IsOnDemand: true })
      throw new ArgumentException(
        $"'{record.Entity.Name}.{name}' is not an on-demand member; only a member marked [OnDemand] is loaded alone.",
        nameof(member));
    return record.Loaded.IsSet(info.DataIndex) ? Task.CompletedTask : entSession.LoadMemberAsync(record, info);
  }

  private static EntitySession GetSession(IEntitySession session) {
    ArgumentNullException.ThrowIfNull(session);
    return session as EntitySession
      ?? throw new ArgumentException($"The session is a {session.GetType().Name}, not the ORM's.", nameof(session));
  }
}
