using Mic.Orm.Db;
using Mic.Orm.Db.Readers;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Holds an entity's values: one array of current values, a mask of the members written since the last
/// accept and a mask of the members that hold real data.</summary>
/// <remarks>The emitted entity class has no state of its own and reads and writes its record. A slot holds what the
/// member's type says from the moment the record exists, except primary-key and foreign-key members, whose slots
/// stay <see langword="null"/> until a value is set, and a JSON member, whose slot holds a loaded document as text
/// until the member is first read. The typed accessors go through the member's own behaviour,
/// assigned by <see cref="MemberValueAccessorHelper"/>; <see cref="GetRawValue"/> and <see cref="SetRawValue"/>
/// reach the array and the masks directly, for the few places that mean to skip that behaviour.</remarks>
public class EntityRecord {
  readonly object?[] _values;

  // The text the row holds for each JSON member, as loaded or last saved, in `Entity.JsonMembers` order; made on
  // first use, and never for an entity with no JSON member.
  string?[]? _jsonTexts;

  /// <summary>Specifies which members have been written since the record was last accepted.</summary>
  /// <remarks>A field rather than a property, so that a bit set through it lands on the record rather than on a copy
  /// of the struct.</remarks>
  public EntityMemberMask Modified;

  /// <summary>Specifies which members hold real data.</summary>
  /// <remarks>A partially loaded record holds some columns and knows which, so a read of a member that is not there
  /// can be satisfied by a load; a <see langword="null"/> in a slot is simply null.</remarks>
  public EntityMemberMask Loaded;

  public EntityRecord(EntityTypeInfo entity, EntitySession? session = null) {
    ArgumentNullException.ThrowIfNull(entity);
    Entity = entity;
    Session = session;
    var count = entity.DataCount;
    _values = new object?[count];

    // The slots start as a copy of the entity's initial values, so a value-type member reads as its type's default;
    // key members are left null (see EntityMemberInfo.DefaultValue). Guarded because a model that failed to build
    // never reached CompleteSetup, and the tests make records over one on purpose.
    if (entity.InitialValues.Length == count)
      Array.Copy(entity.InitialValues, _values, count);

    Modified = new EntityMemberMask(count);
    Loaded = new EntityMemberMask(count);
  }

  public EntityTypeInfo Entity { get; }

  /// <summary>Gets the session the record belongs to, or <see langword="null"/> for a record that belongs to
  /// none.</summary>
  /// <remarks>A record without a session can hold values, as one the model layer builds or the entity cache serves
  /// does, but cannot resolve a reference, which needs the identity map, and throws when asked to.</remarks>
  public EntitySession? Session { get; private set; }

  /// <summary>Gets or sets the status of the record, which says which statement a save turns it into.</summary>
  public EntityStatus Status { get; set; } = EntityStatus.New;

  /// <summary>Gets or sets where the batch being written leaves this record's generated id, from the build of its insert
  /// until the batch's keys are applied; <see langword="null"/> otherwise.</summary>
  /// <remarks>A child's insert built after it reads the slot off this record and writes its reference in place of the
  /// key it does not have yet.</remarks>
  public GeneratedIdSlot? GeneratedIdSlot { get; internal set; }

  /// <summary>Gets the status the record had when the last save took it, or <see langword="null"/> if no save ever
  /// has.</summary>
  /// <remarks>A save turns every record it wrote to <see cref="EntityStatus.Loaded"/>, so this is what an after-save
  /// handler reads to tell an insert from an update or a delete.</remarks>
  public EntityStatus? StatusBeforeSave { get; internal set; }

  /// <summary>Gets the record's position among the others of its topological group, where its type could not decide
  /// the order.</summary>
  /// <remarks>Zero for every record but those of a type in a reference cycle, and then only when a save contains two
  /// of them that point at each other.</remarks>
  public int SortSubIndex { get; internal set; }

  EntityKey _primaryKey;
  bool _primaryKeyKnown;

  /// <summary>Gets the record's identity: its primary-key members as one value.</summary>
  /// <remarks>The key is rebuilt after a key member is written, as when the database assigns an id at insert, and
  /// cached between writes. The key carries no reference to the record.</remarks>
  /// <exception cref="InvalidOperationException">the entity has no primary key.</exception>
  public EntityKey PrimaryKey {
    get {
      if (_primaryKeyKnown)
        return _primaryKey;
      var keyInfo = Entity.PrimaryKey
        ?? throw new InvalidOperationException(
          $"Entity '{Entity.Name}' has no primary key, so it has no identity to track.");
      _primaryKey = EntityKey.ForRecord(keyInfo, this);
      _primaryKeyKnown = true;
      return _primaryKey;
    }
  }

  EntityBase? _instance;

  /// <summary>Gets the emitted entity in front of the record, creating it on first use.</summary>
  /// <remarks>A record is useful without one: queries, the identity map, a save and a metadata-driven application
  /// all work in records.</remarks>
  /// <exception cref="InvalidOperationException">the record has no session, and so no model to ask for the
  /// factory.</exception>
  public EntityBase EntityInstance {
    get {
      if (_instance != null)
        return _instance;
      var factory = Session?.Model.EntityClassFactory
        ?? throw new InvalidOperationException(
          $"A '{Entity.Name}' record with no session cannot make an entity instance — there is no model to ask for "
          + "the entity classes. Open it through an EntitySession, or work with the record.");
      return _instance = factory.CreateInstance(this);
    }
  }

  // There is no "is modified" property: whether a record needs saving, and as what, is Status; which members were
  // written is the Modified mask. The two disagree in every direction.

  // ----- the typed accessors: what an emitted property calls ---------------------------------------------------------
  //
  // The emitted code passes a member index, a position in Entity.Members, not a storage slot; it is resolved to the
  // member and then to member.DataIndex, so an emitted property carries no storage decision. No bounds check of our
  // own: the array does one, the indexes come from metadata, and this is the hottest path in the ORM.

  public T GetValue<T>(int memberIndex) => GetValue(memberIndex) is T value ? value : default!;

  public void SetValue<T>(int memberIndex, T value) => SetValue(memberIndex, (object?)value);

  public object? GetValue(int memberIndex) => GetValue(Entity.Members[memberIndex]);

  public void SetValue(int memberIndex, object? value) => SetValue(Entity.Members[memberIndex], value);

  public object? GetValue(EntityMemberInfo member) => member.GetValueFunc(this, member);

  public void SetValue(EntityMemberInfo member, object? value) {
    // The save writes through the raw layer, so what is refused here is a caller's change made while it runs.
    Session?.ThrowIfSaving();
    member.SetValueAction(this, member, value);
  }

  // ----- the raw layer: the array and the masks, and nothing else ------------------------------------------------------
  //
  // Called by the per-kind accessors, never around them: a caller reaching for these means to skip what its member's
  // kind does, as copying a foreign key out of a target's record does.

  public object? GetRawValue(EntityMemberInfo member) => _values[member.DataIndex];

  /// <summary>Writes a member's slot and marks the masks, when the value differs from what the slot holds.</summary>
  /// <param name="member">The member to write.</param>
  /// <param name="value">The value to write.</param>
  /// <remarks>A write that changes nothing marks the member loaded only, so that the changed mask, which decides the
  /// columns of an update and keys its cached statement, stays exact. A write to a column member the record does not
  /// hold is always a change.</remarks>
  /// <exception cref="InvalidOperationException">the member is part of the primary key and the record is not new.
  /// -or- The member is the row version. -or- The record's status is <see cref="EntityStatus.Fantom"/>.</exception>
  public void SetRawValue(EntityMemberInfo member, object? value) {
    var index = member.DataIndex;
    // A column the record does not hold has no value to compare with: its slot is empty, not the row's.
    if (Equals(_values[index], value) && (Loaded.IsSet(index) || member.Kind != MemberKind.Column)) {
      Loaded.Set(index);
      return;
    }
    // An existing row's key is what everything refers to it by: an update matching on a new key would find no row.
    // Refused at the write, so the stack trace names the assignment; a new record has no row yet.
    if (member.IsPrimaryKey && Status != EntityStatus.New)
      throw new InvalidOperationException(
        $"'{Entity.Name}.{member.Name}' is part of the primary key and this record is {Status} — an entity's identity "
        + "cannot change once the database has a row for it. Delete this entity and create another with the key you "
        + "want.");
    // The row version is counted by the save and matched on by every update; a value the application set would be a
    // version the database never had.
    if (member.AutoValue == AutoType.RowVersion)
      throw new InvalidOperationException(
        $"'{Entity.Name}.{member.Name}' is the row version, which the ORM counts: it is read, never assigned.");

    // Nothing tracks a fantom any more, so a write would be saved by nobody; reading stays legal.
    if (Status == EntityStatus.Fantom)
      throw new InvalidOperationException(
        $"'{Entity.Name}.{member.Name}' cannot be changed: this entity was saved and let go of — either deleted, or "
        + $"written under {nameof(SaveOptions.InsertAndForget)} — so the session no longer tracks it and nothing "
        + "further would be written for it.");

    // The value a unique-key column has in its row is kept on the column's first change.
    if (member.MemberFlags.HasFlag(MemberFlags.InUniqueKey) && Status != EntityStatus.New && Loaded.IsSet(index)
        && !Modified.IsSet(index))
      Session?.KeepOriginal(this, member, _values[index]);

    _values[index] = value;
    if (member.IsPrimaryKey)
      _primaryKeyKnown = false;
    MarkModified(member);
  }

  /// <summary>Counts a member as changed without writing it, and puts the record in the save set on its first
  /// change.</summary>
  /// <param name="member">The member that changed.</param>
  /// <remarks>A JSON object changed in place needs this, since no assignment reported the change.</remarks>
  internal void MarkModified(EntityMemberInfo member) {
    // A row the server derives or a session filled is never changed; a new record of such an entity is the fill's own.
    if (Entity.IsReadOnly && Status != EntityStatus.New)
      throw new InvalidOperationException($"'{Entity.Name}' is {Entity.ReadOnlyKindName}; its rows are read-only.");
    var index = member.DataIndex;
    Modified.Set(index);
    Loaded.Set(index);

    // The first change is what joins the save set; a record already New or Deleted is there and stays what it is.
    if (Status is EntityStatus.Loaded or EntityStatus.Stub) {
      Status = EntityStatus.Modified;
      Session?.RegisterModified(this);

      // Raised on the transition, once per record, after the record is Modified and in the save set, so a handler may
      // mark its aggregate root dirty in turn.
      Entity.Events.OnModified(this);
    }
  }

  /// <summary>Gets the JSON text a JSON member was loaded or last saved with, which a save reads the document's
  /// changed paths against.</summary>
  internal string? GetLoadedJsonText(EntityMemberInfo member) =>
    _jsonTexts?[GetJsonIndex(member)];

  internal void SetLoadedJsonText(EntityMemberInfo member, string? text) =>
    (_jsonTexts ??= new string?[Entity.JsonMembers.Count])[GetJsonIndex(member)] = text;

  private int GetJsonIndex(EntityMemberInfo member) {
    var jsonMembers = Entity.JsonMembers;
    for (var i = 0; i < jsonMembers.Count; i++)
      if (ReferenceEquals(jsonMembers[i], member))
        return i;
    throw new ArgumentException($"'{Entity.Name}.{member.Name}' is not a JSON member.", nameof(member));
  }

  /// <summary>Writes a value that came from the database, or stands in for one: loaded, and not modified.</summary>
  /// <param name="member">The member to write.</param>
  /// <param name="value">The value to write.</param>
  /// <remarks>A row arriving from a query and a reference resolved into a stub both fill the record without making
  /// it dirty.</remarks>
  public void SetLoadedValue(EntityMemberInfo member, object? value) {
    var index = member.DataIndex;
    _values[index] = value;
    Loaded.Set(index);
    if (member.IsPrimaryKey)
      _primaryKeyKnown = false;
  }

  /// <summary>Marks every member as holding real data, as a new record does, having nothing to fetch.</summary>
  public void MarkFullyLoaded() => Loaded.SetAll(_values.Length);

  // ----- the whole record ---------------------------------------------------------------------------------------------

  // ----- validation ---------------------------------------------------------------------------------------------------

  /// <summary>Gets what was found wrong with the record the last time it was checked, or <see langword="null"/> if
  /// nothing was.</summary>
  public List<ClientFault>? ValidationFaults { get; private set; }

  public bool IsValid => ValidationFaults == null;

  /// <summary>Reports something wrong with the record, from the ORM's own rules or an application's.</summary>
  /// <param name="fault">The fault to report.</param>
  /// <remarks>An application's rules run in a <c>ValidatingChanges</c> handler. Every fault added during one
  /// validation pass is thrown together, so a caller is told everything at once.</remarks>
  public void AddValidationFault(ClientFault fault) {
    ArgumentNullException.ThrowIfNull(fault);
    (ValidationFaults ??= []).Add(fault);
  }

  /// <summary>Forgets what the last validation pass found.</summary>
  internal void ClearValidationFaults() => ValidationFaults = null;

  /// <summary>Gets the members written since the last accept, which a save turns into an update's column
  /// list.</summary>
  public IEnumerable<EntityMemberInfo> ModifiedMembers {
    get {
      // By slot: the mask's bits are storage slots, which are not member positions once a member owns no slot.
      var members = Entity.MembersBySlot;
      foreach (var index in Modified.GetSetIndexes())
        yield return members[index];
    }
  }

  /// <summary>Marks the record as what the database now holds, with nothing outstanding.</summary>
  public void AcceptChanges() {
    // Before the mask is cleared: it says which documents the save wrote.
    if (Entity.JsonMembers.Count > 0)
      JsonMemberHelper.AcceptTexts(this);
    Modified.Clear();
  }

  /// <summary>Takes every loaded member of another record of the same entity, which is how a stub stops being
  /// one.</summary>
  /// <param name="other">The record to take the values from.</param>
  /// <remarks>Only the members the other record holds are taken, so a partially loaded row does not overwrite a
  /// value with a null it never carried. The record keeps its identity, so every reference already handed out stays
  /// valid.</remarks>
  public void FillFrom(EntityRecord other) {
    ArgumentNullException.ThrowIfNull(other);
    if (!ReferenceEquals(other.Entity, Entity))
      throw new ArgumentException($"Cannot fill a '{Entity.Name}' from a '{other.Entity.Name}'.", nameof(other));

    // Slot to slot: the mask's bits are storage slots, and both records are of one entity.
    foreach (var index in other.Loaded.GetSetIndexes()) {
      _values[index] = other._values[index];
      Loaded.Set(index);
    }
    if (other._jsonTexts != null)
      _jsonTexts = (string?[])other._jsonTexts.Clone();
    Status = other.Status;
    Modified.Clear();
  }

  /// <summary>Takes from another record of the same entity the members this record does not hold; on a refresh,
  /// also the members it holds and was not assigned.</summary>
  /// <param name="other">The record to take the values from, a row read after this record was.</param>
  /// <param name="refresh"><see langword="true"/> to replace the loaded members that were not assigned; otherwise,
  /// <see langword="false"/>.</param>
  /// <remarks>An assigned member is never replaced, and the status does not change. A foreign key member that takes
  /// another value clears its reference, so the next read of the reference resolves the target again. A refresh
  /// drops an on-demand member the record holds, was not assigned and the row does not carry, so the next read of
  /// the member loads it.</remarks>
  internal void MergeFrom(EntityRecord other, bool refresh) {
    ArgumentNullException.ThrowIfNull(other);
    if (!ReferenceEquals(other.Entity, Entity))
      throw new ArgumentException($"Cannot merge a '{other.Entity.Name}' into a '{Entity.Name}'.", nameof(other));
    // The common case: a row read again by a query, for a record that holds every member the row has.
    if (!refresh && Loaded.Contains(other.Loaded))
      return;

    var members = Entity.MembersBySlot;
    foreach (var index in other.Loaded.GetSetIndexes()) {
      if (Modified.IsSet(index) || Loaded.IsSet(index) && !refresh)
        continue;
      var member = members[index];
      if (member.OwnerReference is { } reference && !Equals(_values[index], other._values[index]))
        _values[reference.DataIndex] = null;
      if (member.IsJson)
        SetLoadedJsonText(member, other.GetLoadedJsonText(member));
      _values[index] = other._values[index];
      Loaded.Set(index);
    }
    if (!refresh)
      return;
    foreach (var member in Entity.Members) {
      var index = member.DataIndex;
      if (!member.IsOnDemand || !Loaded.IsSet(index) || Modified.IsSet(index) || other.Loaded.IsSet(index))
        continue;
      if (member.IsJson)
        SetLoadedJsonText(member, null);
      _values[index] = null;
      Loaded.Clear(index);
    }
  }

  /// <summary>Makes the record nobody's: the values, the loaded mask and the status stay, the session goes.</summary>
  /// <remarks>For the entity cache's fill only, which reads an entity's rows through a session of its own and keeps
  /// the records detached as the block's snapshots; a record is otherwise its session's for its life.</remarks>
  internal void Detach() => Session = null;

  public override string ToString() => $"{Entity.Name} ({Status})";
}
