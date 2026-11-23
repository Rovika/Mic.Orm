using Mic.Linq.Translation;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Provides the ORM's part of loading a list member's rows: the keys by which the rows of a many-owners load
/// are dealt out to their owners, whether a record has a key to ask by, and the link and target pairs a
/// many-to-many's rows come as.</summary>
/// <remarks>The query that loads the rows is the engine's, <c>MemberQueryHelper.BuildListLoadQuery</c>, which writes
/// a list's rows of one owner or of many in the list's default order.</remarks>
internal static class EntityListHelper {
  /// <summary>Gets the owner's key, which the many form's rows are dealt out to.</summary>
  /// <param name="owner">The owner record.</param>
  /// <param name="member">The list member.</param>
  /// <returns>The key, or <see langword="null"/> where any part of it is missing.</returns>
  public static EntityKey? GetOwnerKey(EntityRecord owner, EntityMemberInfo member) {
    var reference = member.GetOwnerReference();
    return ReadKey(owner, reference.ToKey, reference.ToKey);
  }

  /// <summary>Gets the key of the owner a row of the many form belongs to, read off the row's foreign key as the
  /// same key <see cref="GetOwnerKey"/> returns, so that the two meet in one lookup.</summary>
  /// <param name="row">The row record.</param>
  /// <param name="member">The list member.</param>
  /// <returns>The key, or <see langword="null"/> where the row points at nothing.</returns>
  public static EntityKey? GetOwnerKeyOfRow(EntityRecord row, EntityMemberInfo member) {
    var reference = member.GetOwnerReference();
    return ReadKey(row, reference.FromKey, reference.ToKey);
  }

  /// <summary>Determines whether a record has every value of the key a reference points at.</summary>
  /// <param name="record">The record to check.</param>
  /// <param name="reference">The reference whose target key is wanted.</param>
  /// <returns><see langword="true"/> if every key value is present; otherwise, <see langword="false"/>. A
  /// half-written key points at nothing.</returns>
  public static bool HasKey(EntityRecord record, ReferenceInfo reference) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(reference);
    foreach (var part in reference.ToKey.MembersExpanded)
      if (record.GetRawValue(part.Member) == null)
        return false;
    return true;
  }

  /// <summary>Gets the link and target record pairs of a many-to-many list load, leaving out any pair whose link or
  /// target the session has marked for deletion.</summary>
  /// <param name="linkRows">The rows the load produced, each with a link slot and a target slot.</param>
  /// <returns>The pairs.</returns>
  public static List<LinkRecordPair> GetLinkRecordPairs(IReadOnlyList<ProjectionData> linkRows) {
    ArgumentNullException.ThrowIfNull(linkRows);
    var pairs = new List<LinkRecordPair>(linkRows.Count);
    foreach (var linkRow in linkRows) {
      var linkRecord = GetSlotRecord(linkRow.GetValue(MemberQueryHelper.LinkSlot));
      var targetRecord = GetSlotRecord(linkRow.GetValue(MemberQueryHelper.TargetSlot));
      if (linkRecord.Status != EntityStatus.Deleted && targetRecord.Status != EntityStatus.Deleted)
        pairs.Add(new LinkRecordPair(linkRecord, targetRecord));
    }
    return pairs;

    // A bundle slot holds the record the reader materialised, or the entity the typed path made of it.
    static EntityRecord GetSlotRecord(object? slot) =>
      slot as EntityRecord ?? EntityHelper.GetRecord(slot
        ?? throw new InvalidOperationException("A many-to-many row came back with an empty slot."));
  }

  // The values of `readFrom`'s members off the record, as a value of `keyOf` — null where any part is null.
  private static EntityKey? ReadKey(EntityRecord record, EntityKeyInfo readFrom, EntityKeyInfo keyOf) {
    var members = readFrom.MembersExpanded;
    if (members.Count == 1)
      return record.GetRawValue(members[0].Member) is { } value ? new EntityKey(keyOf, value) : null;
    var values = new object?[members.Count];
    for (var i = 0; i < values.Length; i++)
      if ((values[i] = record.GetRawValue(members[i].Member)) == null)
        return null;
    return new EntityKey(keyOf, values);
  }
}
