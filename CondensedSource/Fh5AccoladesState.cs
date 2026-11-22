#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Atomic FH5 Accolades editing across CareerChallengeSaveState, CareerChallengeCollectionSaveState0..21 and CurrencyBucket_CareerProgress_career_progress.</summary>
public static class Fh5AccoladesState
{
    const string CareerRecordName = "CareerChallengeSaveState";
    const string CareerSerializer = "ChallengeSaveDataBase";
    const string CollectionPrefix = "CareerChallengeCollectionSaveState";
    const string CollectionSerializer = "CareerChallengeCollectionSaveState";
    const string PointsBucketKey = "CurrencyBucket_CareerProgress_career_progress";
    const int MaxCollectionEntries = 20_000;

    public sealed record Row(
        uint Id,
        uint CollectionId,
        string CollectionName,
        string Subcategory,
        string Name,
        string Description,
        string HowToUnlock,
        int Points,
        bool Completed,
        bool Supported,
        DateTimeOffset? EarnedUtc)
    {
        public string PointsDisplay => Points > 0 ? Points.ToString("n0", CultureInfo.InvariantCulture) : "—";
    }

    public sealed record Display(
        bool CanEdit,
        int Completed,
        int Total,
        int SavedPoints,
        int ComputedPoints,
        bool TotalsAgree,
        string Warning,
        Row[] Rows);

    public sealed record ChangeSummary(
        bool Changed,
        int AccoladesUpdated,
        int CollectionsUpdated,
        int PreviousPoints,
        int NewPoints);

    sealed record CollectionPayload(BinaryCareer.Record Record, uint CollectionId, List<uint> Ids);

    public static Display ReadForDisplay(ProfileContainer? container)
    {
        var data = Fh5ProfileGameData.For(container);
        if (data == null)
        {
            return new(false, 0, 0, 0, 0, true,
                "Choose FH5 game data (TIDETables.zip) above to inspect and edit Accolades.",
                Array.Empty<Row>());
        }

        if (!TryLoadState(container, data, out var doc, out _, out var collections, out _, out int savedPoints, out string failReason))
        {
            var fallbackRows = data.Accolades
                .Select(a => new Row(a.Id, a.CollectionId, a.CollectionName, a.SubcategoryName, a.Name, a.Description, a.HowToUnlock, a.Points, false, false, null))
                .ToArray();
            return new(false, 0, fallbackRows.Length, 0, 0, false,
                $"Read-only: {failReason}", fallbackRows);
        }

        var entryById = doc!.Entries.ToDictionary(e => e.Id);
        int completedCount = 0;
        int computedPoints = 0;
        var rows = new List<Row>(data.Accolades.Length);

        foreach (var def in data.Accolades)
        {
            bool hasEntry = entryById.TryGetValue(def.Id, out var entry);
            bool completed = hasEntry && entry!.CompletionCount != 0;
            bool supported = hasEntry && IsEntrySupported(entry!, def, data);
            DateTimeOffset? earned = hasEntry ? Fh5ChallengeSaveCodec.DecodeDate(entry!.UtcMicroseconds) : null;
            if (completed)
            {
                completedCount++;
                computedPoints += def.Points;
            }
            rows.Add(new(def.Id, def.CollectionId, def.CollectionName, def.SubcategoryName, def.Name, def.Description, def.HowToUnlock, def.Points, completed, supported, earned));
        }

        bool collectionsAgree = CollectionsMatchCompletedSet(doc, collections!, data);
        bool totalsAgree = savedPoints == computedPoints && collectionsAgree;
        string warning = totalsAgree
            ? ""
            : savedPoints != computedPoints
                ? $"Saved Accolade points ({savedPoints:n0}) differ from completed total ({computedPoints:n0}); use Sync or Complete to align."
                : "Collection progress records differ from completed accolades; click Sync points & collections to repair.";

        return new(true, completedCount, rows.Count, savedPoints, computedPoints, totalsAgree, warning, rows.ToArray());
    }

    public static bool TryPreviewComplete(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out ChangeSummary summary, out string error)
        => TryExecute(container, selectedIds, mode: Mode.Complete, commit: false, out summary, out error);

    public static bool TryComplete(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out ChangeSummary summary, out string error)
        => TryExecute(container, selectedIds, mode: Mode.Complete, commit: true, out summary, out error);

    public static bool TryPreviewReset(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out ChangeSummary summary, out string error)
        => TryExecute(container, selectedIds, mode: Mode.Reset, commit: false, out summary, out error);

    public static bool TryReset(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out ChangeSummary summary, out string error)
        => TryExecute(container, selectedIds, mode: Mode.Reset, commit: true, out summary, out error);

    public static bool TrySyncPointsAndCollections(ProfileContainer? container, out ChangeSummary summary, out string error)
        => TryExecute(container, Array.Empty<ulong>(), mode: Mode.SyncOnly, commit: true, out summary, out error);

    public static bool TryPreviewSyncPointsAndCollections(ProfileContainer? container, out ChangeSummary summary, out string error)
        => TryExecute(container, Array.Empty<ulong>(), mode: Mode.SyncOnly, commit: false, out summary, out error);

    enum Mode { Complete, Reset, SyncOnly }

    static bool TryExecute(
        ProfileContainer? container,
        IEnumerable<ulong>? selectedIds,
        Mode mode,
        bool commit,
        out ChangeSummary summary,
        out string error)
    {
        summary = new(false, 0, 0, 0, 0);
        error = "";
        var data = Fh5ProfileGameData.For(container);
        if (data == null)
        {
            error = "FH5 game data (TIDETables.zip) must be loaded first.";
            return false;
        }

        ulong[] selectedArray = (selectedIds ?? Array.Empty<ulong>()).ToArray();
        if (mode != Mode.SyncOnly && selectedArray.Length == 0)
        {
            error = mode == Mode.Complete ? "Select accolades to complete." : "Select accolades to reset.";
            return false;
        }

        // Work on a full cloned ProfileContainer to guarantee atomic validation before touching the caller's container.
        if (container == null) { error = "No save profile loaded."; return false; }
        ProfileContainer staged;
        try
        {
            staged = ProfileContainer.Parse(container.Serialize());
            Fh5ProfileGameData.Copy(container, staged);
        }
        catch (Exception ex)
        {
            error = "Failed to stage profile container: " + ex.Message;
            return false;
        }

        if (!TryLoadState(staged, data, out var doc, out var careerRecord, out var collections, out var pointsNode, out int previousPoints, out error))
            return false;

        var entryById = doc!.Entries.ToDictionary(e => e.Id);
        int accoladesUpdated = 0;

        if (mode != Mode.SyncOnly)
        {
            var uniqueIds = new HashSet<uint>();
            foreach (ulong raw in selectedArray)
            {
                if (raw > uint.MaxValue || !data.AccoladesById.TryGetValue((uint)raw, out var def)
                    || !entryById.TryGetValue((uint)raw, out var entry)
                    || !IsEntrySupported(entry, def, data))
                {
                    error = $"Accolade {raw} is missing or has an unsupported objective layout. Nothing was changed.";
                    return false;
                }
                uniqueIds.Add((uint)raw);
            }

            ulong stamp = Fh5ChallengeSaveCodec.EncodeDate(DateTimeOffset.UtcNow);
            foreach (uint id in uniqueIds)
            {
                var entry = entryById[id];
                var def = data.AccoladesById[id];

                if (mode == Mode.Complete)
                {
                    if (entry.CompletionCount != 0) continue;
                    foreach (var obj in entry.Objectives)
                    {
                        data.ObjectivesById.TryGetValue(obj.ObjectiveId, out var odef);
                        if (!Fh5ChallengeSaveCodec.TryComputeCompletedValue(obj.ValueType, obj.Value, odef, out string newVal))
                        {
                            error = $"Could not compute completion value for objective {obj.ObjectiveId} ({obj.ValueType}) on Accolade {id}.";
                            return false;
                        }
                        obj.Done = 1;
                        obj.Value = newVal;
                    }
                    entry.UnlockedFlag = 1;
                    entry.ActiveObjectiveId = 0;
                    entry.CompletionCount = 1;
                    if (entry.UtcMicroseconds == 0) entry.UtcMicroseconds = stamp;
                    accoladesUpdated++;
                }
                else // Mode.Reset
                {
                    if (entry.CompletionCount == 0 && entry.Objectives.All(o => o.Done == 0)) continue;
                    foreach (var obj in entry.Objectives)
                    {
                        data.ObjectivesById.TryGetValue(obj.ObjectiveId, out var odef);
                        if (!Fh5ChallengeSaveCodec.TryComputeResetValue(obj.ValueType, odef, out string resetVal))
                        {
                            error = $"Could not compute reset value for objective {obj.ObjectiveId} ({obj.ValueType}) on Accolade {id}.";
                            return false;
                        }
                        obj.Done = 0;
                        obj.Value = resetVal;
                    }
                    entry.ActiveObjectiveId = entry.Objectives.Count > 0 ? entry.Objectives[0].ObjectiveId : 0;
                    entry.CompletionCount = 0;
                    entry.UtcMicroseconds = 0;
                    accoladesUpdated++;
                }
            }
        }

        // Apply CareerChallengeSaveState payload change on staged
        byte[] updatedCareerPayload = Fh5ChallengeSaveCodec.Serialize(doc);
        if (!Fh5ChallengeSaveCodec.TryParse(updatedCareerPayload, allowBadgeTrailing: false, out _))
        {
            error = "Updated CareerChallengeSaveState failed validation.";
            return false;
        }
        bool careerChanged = !updatedCareerPayload.AsSpan().SequenceEqual(careerRecord!.Payload);
        if (careerChanged) staged.Career!.ReplacePayload(careerRecord, updatedCareerPayload);

        // Reconcile all existing CareerChallengeCollectionSaveState{colId} records
        var completedIds = new HashSet<uint>(doc.Entries.Where(e => e.CompletionCount != 0).Select(e => e.Id));
        var collectionEdits = new List<(string RecordName, byte[] Payload)>();
        foreach (var col in collections!)
        {
            if (!data.CollectionsById.TryGetValue(col.CollectionId, out var colDef)) continue;
            var colExpectedSet = new HashSet<uint>(colDef.ChallengeIds.Where(completedIds.Contains));
            var nextList = new List<uint>(colExpectedSet.Count);
            var addedSet = new HashSet<uint>();

            foreach (uint existingId in col.Ids)
            {
                if (colExpectedSet.Contains(existingId) && addedSet.Add(existingId))
                    nextList.Add(existingId);
            }
            foreach (uint defId in colDef.ChallengeIds)
            {
                if (colExpectedSet.Contains(defId) && addedSet.Add(defId))
                    nextList.Add(defId);
            }

            if (!nextList.SequenceEqual(col.Ids))
            {
                byte[] colBytes = SerializeCollection(col.CollectionId, nextList);
                if (!TryParseCollection(colBytes, col.CollectionId, out _))
                {
                    error = $"Updated collection {col.Record.Name} failed validation.";
                    return false;
                }
                staged.Career!.ReplacePayload(col.Record, colBytes);
                collectionEdits.Add((col.Record.Name, colBytes));
            }
        }

        // Recompute CurrencyBucket_CareerProgress_career_progress Total from completed accolades
        long newPointsLong = 0;
        foreach (uint cid in completedIds)
        {
            if (data.AccoladesById.TryGetValue(cid, out var def))
                newPointsLong += def.Points;
        }
        if (newPointsLong < 0 || newPointsLong > int.MaxValue)
        {
            error = "Computed accolade point total overflows Int32.";
            return false;
        }
        int newPoints = (int)newPointsLong;
        bool pointsChanged = newPoints != previousPoints;
        if (pointsChanged)
        {
            if (!staged.SaveState!.SetAttribute(pointsNode!, "value", newPoints.ToString(CultureInfo.InvariantCulture)))
            {
                error = "Could not update Accolade points bucket in SaveState.";
                return false;
            }
        }

        // Full staged container serialization & verification
        byte[] stagedBytes = staged.Serialize();
        var verified = ProfileContainer.Parse(stagedBytes);
        Fh5ProfileGameData.Copy(staged, verified);
        if (!TryLoadState(verified, data, out var verifiedDoc, out _, out var verifiedCollections, out _, out int verifiedPoints, out error)
            || verifiedPoints != newPoints
            || !CollectionsMatchCompletedSet(verifiedDoc!, verifiedCollections!, data))
        {
            error = string.IsNullOrEmpty(error) ? "Verified save state did not match expected Accolades totals." : error;
            return false;
        }

        bool anyChange = careerChanged || collectionEdits.Count > 0 || pointsChanged;
        summary = new(anyChange, accoladesUpdated, collectionEdits.Count, previousPoints, newPoints);

        if (commit && anyChange)
        {
            if (careerChanged)
            {
                var targetCareerRecord = container.Career!.Records.Single(r => r.Name == CareerRecordName);
                container.Career.ReplacePayload(targetCareerRecord, updatedCareerPayload);
            }
            foreach (var (recName, payload) in collectionEdits)
            {
                var targetCol = container.Career!.Records.Single(r => r.Name == recName);
                container.Career.ReplacePayload(targetCol, payload);
            }
            if (pointsChanged && TryFindPointsBucket(container, out var livePointsNode, out _))
            {
                container.SaveState!.SetAttribute(livePointsNode!, "value", newPoints.ToString(CultureInfo.InvariantCulture));
            }
        }
        return true;
    }

    static bool TryLoadState(
        ProfileContainer? container,
        Fh5ProfileGameData.Snapshot data,
        out Fh5ChallengeSaveCodec.SaveDocument? doc,
        out BinaryCareer.Record? careerRecord,
        out List<CollectionPayload>? collections,
        out Bxml.Node? pointsNode,
        out int savedPoints,
        out string error)
    {
        doc = null; careerRecord = null; collections = null; pointsNode = null; savedPoints = 0; error = "";
        if (!Fh5CareerRecordHelper.TryFindRecord(container, CareerRecordName, CareerSerializer, out careerRecord, bxmlValueType: CareerRecordName))
        {
            error = "CareerChallengeSaveState record or BXML map entry not found.";
            return false;
        }
        if (!Fh5ChallengeSaveCodec.TryParse(careerRecord!.Payload, allowBadgeTrailing: false, out doc))
        {
            error = "CareerChallengeSaveState layout could not be validated.";
            return false;
        }

        var loadedCols = new List<CollectionPayload>();
        foreach (var rec in container!.Career!.Records)
        {
            if (!rec.Name.StartsWith(CollectionPrefix, StringComparison.Ordinal)) continue;
            string suffix = rec.Name[CollectionPrefix.Length..];
            if (!uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out uint colId)) continue;
            if (!Fh5CareerRecordHelper.TryFindRecord(container, rec.Name, CollectionSerializer, out _))
            {
                error = $"Collection record {rec.Name} has unexpected BXML/serializer metadata.";
                return false;
            }
            if (!TryParseCollection(rec.Payload, colId, out var ids))
            {
                error = $"Collection record {rec.Name} failed payload validation.";
                return false;
            }
            loadedCols.Add(new(rec, colId, ids!));
        }

        if (!TryFindPointsBucket(container, out pointsNode, out savedPoints))
        {
            error = "CurrencyBucket_CareerProgress_career_progress was not found or is not an integer bucket.";
            return false;
        }

        collections = loadedCols;
        return true;
    }

    static bool CollectionsMatchCompletedSet(
        Fh5ChallengeSaveCodec.SaveDocument doc,
        List<CollectionPayload> collections,
        Fh5ProfileGameData.Snapshot data)
    {
        var completed = new HashSet<uint>(doc.Entries.Where(e => e.CompletionCount != 0).Select(e => e.Id));
        foreach (var col in collections)
        {
            if (!data.CollectionsById.TryGetValue(col.CollectionId, out var colDef)) continue;
            var expected = new HashSet<uint>(colDef.ChallengeIds.Where(completed.Contains));
            var actual = new HashSet<uint>(col.Ids);
            if (!expected.SetEquals(actual)) return false;
        }
        return true;
    }

    static bool TryFindPointsBucket(ProfileContainer? container, out Bxml.Node? totalNode, out int points)
    {
        totalNode = null;
        points = 0;
        if (container?.SaveState is not { } bx) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element"
            && n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == PointsBucketKey) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != "CurrencyBucket") return false;

        Bxml.Node? Field(string id, string type)
        {
            var matches = values[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            return matches.Length == 1 && bx.GetAttribute(matches[0], "type") == type && matches[0].Children.Count == 0 ? matches[0] : null;
        }

        if (Field("Name", "string") is not { } name || bx.GetAttribute(name, "value") != "career_progress"
            || Field("Type", "CurrencyTypes") is not { } type || bx.GetAttribute(type, "value") != "CareerProgress"
            || Field("Total", "int") is not { } score
            || !int.TryParse(bx.GetAttribute(score, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out points)
            || points < 0)
            return false;

        totalNode = score;
        return true;
    }

    static bool TryParseCollection(byte[] bytes, uint expectedColId, out List<uint>? ids)
    {
        ids = null;
        if (bytes.Length < 12) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != expectedColId) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (count > MaxCollectionEntries || bytes.Length != 12L + count * 4L) return false;
        var (ok, arr) = Fh5CareerRecordHelper.ReadDistinctU32Tail(bytes, 12, (int)count, id => id);
        if (!ok) return false;
        var list = new List<uint>(arr);
        if (!SerializeCollection(expectedColId, list).AsSpan().SequenceEqual(bytes)) return false;
        ids = list;
        return true;
    }

    static byte[] SerializeCollection(uint colId, List<uint> ids)
    {
        byte[] result = new byte[12 + ids.Count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), colId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), (uint)ids.Count);
        for (int i = 0; i < ids.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12 + i * 4, 4), ids[i]);
        return result;
    }

    static bool IsEntrySupported(
        Fh5ChallengeSaveCodec.EntryRecord entry,
        Fh5ProfileGameData.AccoladeItem def,
        Fh5ProfileGameData.Snapshot data)
    {
        return def.ChallengeDataId == entry.ChallengeDataId
            && entry.Objectives.Count == def.Objectives.Length
            && entry.Objectives.All(o => Fh5ChallengeSaveCodec.IsTypeSupported(o.ValueType) && data.ObjectivesById.ContainsKey(o.ObjectiveId));
    }
}
#endif
