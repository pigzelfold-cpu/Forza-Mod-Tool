using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>FH5 Hot Wheels Academy and Rally Adventure (Sierra Nueva) save-state currency, rank, and bonus-board helpers.</summary>
public static class Fh5ExpansionsSaveState
{
    public const string SpectrumMedalsKey = "CurrencyBucket_SpectrumMedals_career_progress";
    public const string SpectrumMedalsName = "career_progress";
    public const string SpectrumMedalsType = "SpectrumMedals";

    public const string AstraCurrencyType = "AstraReputation";
    public const string AlejandraKey = "CurrencyBucket_AstraReputation_team_alejandra_reputation";
    public const string AlejandraName = "team_alejandra_reputation";
    public const string AlexKey = "CurrencyBucket_AstraReputation_team_alex_reputation";
    public const string AlexName = "team_alex_reputation";
    public const string RamiKey = "CurrencyBucket_AstraReputation_team_rami_reputation";
    public const string RamiName = "team_rami_reputation";

    public const int HotWheelsMaxMedals = 5000;

    static readonly (int Threshold, string Label)[] HotWheelsRankThresholds =
    {
        (0, "Unranked (Qualifier)"),
        (100, "Rookie (B Class)"),
        (750, "Pro (A Class)"),
        (1750, "Expert (S1 Class)"),
        (2800, "Elite (S2 Class)"),
        (4000, "Legend (X Class)"),
    };

    public readonly record struct RallyTeamDef(string BucketKey, string BucketName, string DisplayName, int[] Thresholds);

    public static readonly RallyTeamDef[] RallyTeams =
    {
        new(AlejandraKey, AlejandraName, "Apex Predators (Alejandra)", new[] { 0, 100, 800, 1500, 2200, 3000, 3800, 4800, 5300 }),
        new(AlexKey, AlexName, "Horizon Raptors (Alex)", new[] { 0, 100, 800, 1500, 2200, 2900, 3600, 4600, 5100 }),
        new(RamiKey, RamiName, "Grit Reapers (Rami)", new[] { 0, 100, 700, 1300, 2000, 2700, 3400, 4200, 4700 }),
    };

    public static string GetHotWheelsRankDisplay(int medals)
    {
        int clamped = Math.Max(0, medals);
        int idx = 0;
        for (int i = 0; i < HotWheelsRankThresholds.Length; i++)
        {
            if (clamped >= HotWheelsRankThresholds[i].Threshold) idx = i;
        }
        string label = HotWheelsRankThresholds[idx].Label;
        if (idx + 1 < HotWheelsRankThresholds.Length)
        {
            int next = HotWheelsRankThresholds[idx + 1].Threshold;
            return $"{label} · {next - clamped:n0} medals to {HotWheelsRankThresholds[idx + 1].Label}";
        }
        return $"{label} · Max rank";
    }

    public static string GetRallyTeamRankDisplay(string bucketName, int reputation)
    {
        var team = RallyTeams.FirstOrDefault(t => t.BucketName == bucketName);
        if (team.Thresholds == null || team.Thresholds.Length == 0)
            return $"{reputation:n0} rep";
        int clamped = Math.Max(0, reputation);
        int tier = 0;
        for (int i = 0; i < team.Thresholds.Length; i++)
        {
            if (clamped >= team.Thresholds[i]) tier = i;
        }
        int rank = tier + 1;
        int maxRank = team.Thresholds.Length;
        if (rank < maxRank)
            return $"Rank {rank}/{maxRank} · {team.Thresholds[tier + 1] - clamped:n0} to Rank {rank + 1}";
        return $"Rank {maxRank}/{maxRank} (Max)";
    }

    public static bool TryReadExpansionIntroState(ProfileContainer? container, out string ex1State, out string ex2State)
    {
        ex1State = "Unknown";
        ex2State = "Unknown";
        if (container?.SaveState is not { } bx || !IsGameSaveState(bx)) return false;
        ex1State = ReadMapPropertyValue(bx, "IEStateExpansionOne", "IESaveStateExpansionOne", "IEState") ?? "Absent";
        ex2State = ReadMapPropertyValue(bx, "IEStateExpansionTwo", "IESaveStateExpansionTwo", "IEState") ?? "Absent";
        return true;
    }

    public static bool TryReadCurrencyBucket(ProfileContainer? container, string mapKey, string expectedName, string expectedType,
        out bool exists, out int total, out Bxml.Node? totalNode)
    {
        exists = false;
        total = 0;
        totalNode = null;
        if (container?.SaveState is not { } bx || !IsGameSaveState(bx)) return false;

        var maps = FindMapElements(bx, mapKey);
        if (maps.Length == 0) return true;
        if (maps.Length != 1 || maps[0].Children.Count(c => bx.Strings[c.NameIndex] == "key") != 1) return false;

        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != "CurrencyBucket") return false;

        if (FindValueProperty(bx, values[0], "Name", "string") is not { } nameProp || bx.GetAttribute(nameProp, "value") != expectedName
            || FindValueProperty(bx, values[0], "Type", "CurrencyTypes") is not { } typeProp || bx.GetAttribute(typeProp, "value") != expectedType
            || FindValueProperty(bx, values[0], "Total", "int") is not { } scoreProp
            || !int.TryParse(bx.GetAttribute(scoreProp, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < 0)
        {
            return false;
        }

        exists = true;
        total = parsed;
        totalNode = scoreProp;
        return true;
    }

    public static bool TrySetOrCreateCurrencyBucket(ProfileContainer? container, string mapKey, string bucketName, string currencyType,
        int total, out bool changed, out string reason)
    {
        changed = false;
        reason = "The expansion currency bucket layout is not safely recognized; nothing was changed.";
        if (total < 0)
        {
            reason = "Value cannot be negative.";
            return false;
        }
        if (!TryReadCurrencyBucket(container, mapKey, bucketName, currencyType, out bool exists, out int oldTotal, out _))
            return false;
        if (exists && oldTotal == total)
        {
            reason = "";
            return true;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        if (!TryReadCurrencyBucket(staged, mapKey, bucketName, currencyType, out _, out _, out var stagedTotalNode))
            return false;

        ApplyCurrencyBucketMutation(staged.SaveState!, stagedTotalNode, mapKey, bucketName, currencyType, total);

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadCurrencyBucket(verified, mapKey, bucketName, currencyType, out bool vExists, out int vTotal, out _)
            || !vExists || vTotal != total)
        {
            reason = "Staged expansion currency edit failed round-trip verification; nothing was changed.";
            return false;
        }

        TryReadCurrencyBucket(container, mapKey, bucketName, currencyType, out _, out _, out var liveTotalNode);
        ApplyCurrencyBucketMutation(container.SaveState!, liveTotalNode, mapKey, bucketName, currencyType, total);
        changed = true;
        reason = "";
        return true;
    }

    public static bool TryReadBoards(ProfileContainer? container, bool rally, out int collected, out int total)
    {
        collected = 0;
        total = 0;
        if (container?.SaveState is not { } bx || !IsGameSaveState(bx)) return false;

        foreach (var map in bx.Root.Children)
        {
            if (bx.Strings[map.NameIndex] != "map_element") continue;
            var keys = map.Children.Where(c => bx.Strings[c.NameIndex] == "key").ToArray();
            if (keys.Length != 1) continue;
            string? key = bx.GetAttribute(keys[0], "value");
            if (string.IsNullOrEmpty(key) || !IsMatchingBoardKey(key, rally)) continue;

            var values = map.Children.Where(c => bx.Strings[c.NameIndex] == "value" && bx.GetAttribute(c, "type") == "DiscountBoardSaveState").ToArray();
            if (values.Length != 1) return false;
            var prop = FindValueProperty(bx, values[0], "State", "DiscountBoardState");
            if (prop is null) return false;
            total++;
            if (bx.GetAttribute(prop, "value") == "Collected") collected++;
        }
        return true;
    }

    public static bool TrySmashAllBoards(ProfileContainer? container, bool rally, out int smashed, out string reason)
    {
        smashed = 0;
        reason = "Expansion bonus boards are not safely recognized; nothing was changed.";
        if (!TryReadBoards(container, rally, out _, out int total)) return false;
        if (total == 0)
        {
            reason = rally
                ? "No Rally Adventure bonus board entries exist in this save yet (visit Sierra Nueva in-game first)."
                : "No Hot Wheels bonus board entries exist in this save yet (visit Hot Wheels Park in-game first).";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        int stagedSmashed = ApplySmashBoards(staged, rally);
        if (stagedSmashed < 0) return false;
        if (stagedSmashed == 0)
        {
            reason = "";
            return true;
        }

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadBoards(verified, rally, out int vCollected, out int vTotal)
            || vTotal != total || vCollected != vTotal)
        {
            reason = "Staged bonus board update failed round-trip verification; nothing was changed.";
            return false;
        }

        smashed = ApplySmashBoards(container, rally);
        reason = "";
        return true;
    }

    internal static void ApplyCurrencyBucketMutation(Bxml bx, Bxml.Node? totalNode, string mapKey, string bucketName, string currencyType, int total)
    {
        string valueText = total.ToString(CultureInfo.InvariantCulture);
        if (totalNode != null)
        {
            int idx = bx.AddFreshString(valueText);
            for (int i = 0; i < totalNode.Attributes.Count; i++)
            {
                if (bx.Strings[totalNode.Attributes[i].KeyIndex] == "value")
                {
                    var attr = totalNode.Attributes[i];
                    attr.ValueIndex = idx;
                    totalNode.Attributes[i] = attr;
                    bx.MarkDirty();
                    return;
                }
            }
            bx.SetAttribute(totalNode, "value", valueText);
            return;
        }

        Bxml.Node MakeNode(byte flags, string tag, params (string k, string v, bool fresh)[] attrs)
        {
            var node = new Bxml.Node { Flags = flags, NameIndex = bx.EnsureString(tag) };
            foreach (var (k, v, fresh) in attrs)
            {
                node.Attributes.Add(new Bxml.Attribute
                {
                    KeyIndex = bx.EnsureString(k),
                    ValueIndex = fresh ? bx.AddFreshString(v) : bx.EnsureString(v),
                });
            }
            return node;
        }

        var mapEl = MakeNode(0x05, "map_element");
        mapEl.Children.Add(MakeNode(0x03, "key", ("type", "string", false), ("value", mapKey, false)));
        var valEl = MakeNode(0x07, "value", ("type", "CurrencyBucket", false));
        valEl.Children.Add(MakeNode(0x03, "property", ("id", "Name", false), ("type", "string", false), ("value", bucketName, false)));
        valEl.Children.Add(MakeNode(0x03, "property", ("id", "Type", false), ("type", "CurrencyTypes", false), ("value", currencyType, false)));
        valEl.Children.Add(MakeNode(0x03, "property", ("id", "Total", false), ("type", "int", false), ("value", valueText, true)));
        mapEl.Children.Add(valEl);
        bx.Root.Children.Add(mapEl);
        bx.MarkDirty();
    }

    internal static void CopyCatalogSnapshots(ProfileContainer src, ProfileContainer dst)
    {
#if PROFILE_UNLOCK_TESTING
        ProfileGameData.Copy(src, dst);
        Fh5ProfileGameData.Copy(src, dst);
        Fh4ProfileGameData.Copy(src, dst);
        Fh3ProfileGameData.Copy(src, dst);
#endif
    }

    static bool IsGameSaveState(Bxml bx)
        => bx.Root.NameIndex >= 0 && bx.Root.NameIndex < bx.Strings.Count && bx.Strings[bx.Root.NameIndex] == "GameSaveState";

    static Bxml.Node[] FindMapElements(Bxml bx, string mapKey)
        => bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element"
            && n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == mapKey)).ToArray();

    static Bxml.Node? FindValueProperty(Bxml bx, Bxml.Node valueNode, string id, string fieldType)
    {
        var props = valueNode.Children.Where(n => bx.Strings[n.NameIndex] == "property"
            && bx.GetAttribute(n, "id") == id && bx.GetAttribute(n, "type") == fieldType).ToArray();
        return props.Length == 1 && props[0].Children.Count == 0 ? props[0] : null;
    }

    static string? ReadMapPropertyValue(Bxml bx, string mapKey, string expectedValueType, string propId)
    {
        var maps = FindMapElements(bx, mapKey);
        if (maps.Length != 1) return null;
        var valNode = maps[0].Children.FirstOrDefault(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == expectedValueType);
        if (valNode == null) return null;
        var prop = valNode.Children.FirstOrDefault(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == propId);
        return prop == null ? null : bx.GetAttribute(prop, "value");
    }

    static bool IsMatchingBoardKey(string key, bool rally) => rally
        ? key.StartsWith("astra_bonus_board_", StringComparison.Ordinal) && key.EndsWith("_save_state", StringComparison.Ordinal)
        : key.StartsWith("discount_board_xp_", StringComparison.Ordinal) && key.Contains("_rainbow_", StringComparison.Ordinal) && key.EndsWith("_save_state", StringComparison.Ordinal);

    static int ApplySmashBoards(ProfileContainer container, bool rally)
    {
        if (container.SaveState is not { } bx) return -1;
        int changed = 0;
        int allCollectedBoards = 0;
        foreach (var map in bx.Root.Children)
        {
            if (bx.Strings[map.NameIndex] != "map_element") continue;
            var keyNode = map.Children.FirstOrDefault(c => bx.Strings[c.NameIndex] == "key");
            string? key = keyNode == null ? null : bx.GetAttribute(keyNode, "value");
            if (string.IsNullOrEmpty(key)) continue;

            var valNode = map.Children.FirstOrDefault(c => bx.Strings[c.NameIndex] == "value" && bx.GetAttribute(c, "type") == "DiscountBoardSaveState");
            if (valNode == null) continue;
            var prop = FindValueProperty(bx, valNode, "State", "DiscountBoardState");
            if (prop == null) continue;

            if (IsMatchingBoardKey(key, rally))
            {
                if (bx.GetAttribute(prop, "value") != "Collected")
                {
                    if (!bx.SetAttribute(prop, "value", "Collected")) return -1;
                    changed++;
                }
            }
            if (bx.GetAttribute(prop, "value") == "Collected") allCollectedBoards++;
        }

        if (changed > 0 && container.Properties != null)
        {
            var statNode = container.Properties.Walk().FirstOrDefault(p => p.Path == "/CompareStats/DiscountBoardsSmashed").Node;
            if (statNode != null && statNode.IsEditable && !statNode.IsBag
                && uint.TryParse(statNode.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint oldCount)
                && allCollectedBoards > oldCount)
            {
                statNode.TrySetFromText(allCollectedBoards.ToString(CultureInfo.InvariantCulture));
            }
        }
        return changed;
    }
}

#if PROFILE_UNLOCK_TESTING
internal static class Fh5ChallengeExpansionHelper
{
    internal static bool TryLoadChallengeDoc(ProfileContainer? container, string recordName,
        out Fh5ProfileGameData.Snapshot? data, out BinaryCareer.Record? record,
        out List<Fh5ChallengeSaveCodec.EntryRecord>? entries, out Func<byte[]>? serializeDoc, out string reason)
    {
        data = Fh5ProfileGameData.For(container);
        record = null;
        entries = null;
        serializeDoc = null;
        if (data == null)
        {
            reason = "FH5 game tables are not loaded for this profile.";
            return false;
        }
        if (!Fh5CareerRecordHelper.TryFindRecord(container, recordName, "ChallengeSaveDataBase", out record, bxmlValueType: recordName))
        {
            reason = $"'{recordName}' record is not safely recognized in this save.";
            return false;
        }
        if (!Fh5ChallengeSaveCodec.TryParse(record!.Payload, allowBadgeTrailing: false, out var doc))
        {
            reason = $"Could not parse '{recordName}' binary data.";
            return false;
        }
        entries = doc!.Entries;
        serializeDoc = () => Fh5ChallengeSaveCodec.Serialize(doc!);
        reason = "";
        return true;
    }

    internal static bool IsEntrySupported(Fh5ChallengeSaveCodec.EntryRecord entry, uint expectedChallengeDataId,
        int expectedObjectiveCount, Fh5ProfileGameData.Snapshot data)
        => entry.ChallengeDataId == expectedChallengeDataId
            && entry.Objectives.Count == expectedObjectiveCount
            && entry.Objectives.All(o => Fh5ChallengeSaveCodec.IsTypeSupported(o.ValueType) && data.ObjectivesById.ContainsKey(o.ObjectiveId));

    internal static bool ApplyChallengeEdit(List<Fh5ChallengeSaveCodec.EntryRecord> entries,
        bool complete, ulong encodedUtc, Fh5ProfileGameData.Snapshot data, out int mutated, out string reason)
    {
        mutated = 0;
        reason = "";
        foreach (var entry in entries)
        {
            bool wasCompleted = entry.CompletionCount != 0;
            if (complete)
            {
                if (wasCompleted) continue;
                foreach (var obj in entry.Objectives)
                {
                    data.ObjectivesById.TryGetValue(obj.ObjectiveId, out var odef);
                    if (!Fh5ChallengeSaveCodec.TryComputeCompletedValue(obj.ValueType, obj.Value, odef, out string newVal))
                    {
                        reason = $"Could not compute completion value for objective {obj.ObjectiveId} ({obj.ValueType}).";
                        return false;
                    }
                    obj.Done = 1;
                    obj.Value = newVal;
                }
                entry.UnlockedFlag = 1;
                entry.ActiveObjectiveId = 0;
                entry.CompletionCount = 1;
                entry.UtcMicroseconds = encodedUtc;
                mutated++;
            }
            else
            {
                if (!wasCompleted) continue;
                foreach (var obj in entry.Objectives)
                {
                    data.ObjectivesById.TryGetValue(obj.ObjectiveId, out var odef);
                    if (!Fh5ChallengeSaveCodec.TryComputeResetValue(obj.ValueType, odef, out string resetVal))
                    {
                        reason = $"Could not reset objective {obj.ObjectiveId} ({obj.ValueType}).";
                        return false;
                    }
                    obj.Done = 0;
                    obj.Value = resetVal;
                }
                entry.ActiveObjectiveId = entry.Objectives.Count > 0 ? entry.Objectives[0].ObjectiveId : 0;
                entry.CompletionCount = 0;
                entry.UtcMicroseconds = 0;
                mutated++;
            }
        }
        return true;
    }
}

/// <summary>FH5 Hot Wheels Academy missions (SpectrumMissionSaveState + SpectrumMedals).</summary>
public static class Fh5HotWheelsState
{
    const string RecordName = "SpectrumMissionSaveState";

    public sealed record Row(uint Id, string Rank, string RankDisplay, string Importance, uint SortOrder, string Name, string Description,
        int Medals, bool Completed, bool Supported, string Compatibility, DateTimeOffset? CompletedUtc, uint[] ObjectiveIds)
    {
        public string CompletedDisplay => Completed ? "Yes" : "No";
    }

    public sealed record View(Row[] Rows, int SavedCount, int CatalogCount, int CompletedCount,
        int CompletedMedals, int SavedMedals, bool BucketExists, bool BucketAgrees, string RankDisplay, string StatusNote);

    public sealed record Change(int CompletedDelta, int ResetDelta, int PreviousMedals, int NewMedals, bool BucketCreated, bool Changed);

    public static bool TryRead(ProfileContainer? container, out View? view, out string reason)
    {
        view = null;
        if (!Fh5ChallengeExpansionHelper.TryLoadChallengeDoc(container, RecordName, out var data, out _, out var docEntries, out _, out reason))
            return false;
        if (!Fh5ExpansionsSaveState.TryReadCurrencyBucket(container, Fh5ExpansionsSaveState.SpectrumMedalsKey,
                Fh5ExpansionsSaveState.SpectrumMedalsName, Fh5ExpansionsSaveState.SpectrumMedalsType,
                out bool bucketExists, out int savedMedals, out _))
        {
            reason = "Hot Wheels SpectrumMedals bucket layout is not safely recognized.";
            return false;
        }

        var byId = docEntries!.ToDictionary(e => e.Id);
        var rows = new List<Row>(data!.HotWheelsMissions.Length);
        int completedCount = 0, completedMedals = 0;

        foreach (var item in data.HotWheelsMissions)
        {
            if (byId.TryGetValue(item.Id, out var entry))
            {
                bool supported = Fh5ChallengeExpansionHelper.IsEntrySupported(entry, item.ChallengeDataId, item.ObjectiveIds.Length, data);
                bool completed = entry.CompletionCount != 0;
                if (completed)
                {
                    completedCount++;
                    completedMedals += item.Medals;
                }
                rows.Add(new(item.Id, item.Rank, item.RankDisplay, item.Importance, item.SortOrder, item.Name, item.Description,
                    item.Medals, completed, supported,
                    supported ? "Supported" : "Read-only: mission definition differs",
                    Fh5ChallengeSaveCodec.DecodeDate(entry.UtcMicroseconds), item.ObjectiveIds));
            }
            else
            {
                rows.Add(new(item.Id, item.Rank, item.RankDisplay, item.Importance, item.SortOrder, item.Name, item.Description,
                    item.Medals, false, false, "Not present in save yet", null, item.ObjectiveIds));
            }
        }

        int savedCount = docEntries!.Count;
        string note = savedCount == 0
            ? "SpectrumMissionSaveState has 0 entries in this save. Visit Hot Wheels Park in-game to initialize missions, or edit Hot Wheels Medals directly."
            : !bucketExists
                ? $"{savedCount:n0} missions initialized in save (Hot Wheels Medals bucket will be created automatically on first edit)."
                : "";
        view = new(rows.ToArray(), savedCount, data.HotWheelsMissions.Length, completedCount, completedMedals,
            savedMedals, bucketExists, savedMedals == completedMedals, Fh5ExpansionsSaveState.GetHotWheelsRankDisplay(savedMedals), note);
        reason = "";
        return true;
    }

    public static bool TryPreviewComplete(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, mode: EditMode.Complete, apply: false, out change, out reason);

    public static bool TryComplete(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, mode: EditMode.Complete, apply: true, out change, out reason);

    public static bool TryPreviewReset(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, mode: EditMode.Reset, apply: false, out change, out reason);

    public static bool TryReset(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, mode: EditMode.Reset, apply: true, out change, out reason);

    public static bool TryPreviewSyncMedals(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), mode: EditMode.SyncMedals, apply: false, out change, out reason);

    public static bool TrySyncMedals(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), mode: EditMode.SyncMedals, apply: true, out change, out reason);

    enum EditMode { Complete, Reset, SyncMedals }

    static bool TryExecute(ProfileContainer? container, IEnumerable<uint> ids, EditMode mode, bool apply, out Change? change, out string reason)
    {
        change = null;
        if (!TryRead(container, out var beforeView, out reason)) return false;

        var selectedIds = ids?.Distinct().ToArray() ?? Array.Empty<uint>();
        if (mode != EditMode.SyncMedals && selectedIds.Length == 0)
        {
            reason = "Select at least one Hot Wheels mission.";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        Fh5ExpansionsSaveState.CopyCatalogSnapshots(container, staged);
        if (!Fh5ChallengeExpansionHelper.TryLoadChallengeDoc(staged, RecordName, out var data, out var stagedRecord, out var stagedEntries, out var serializeStaged, out reason))
            return false;

        var stagedById = stagedEntries!.ToDictionary(e => e.Id);
        int mutated = 0;
        int medalDelta = 0;

        if (mode != EditMode.SyncMedals)
        {
            var targets = new List<Fh5ChallengeSaveCodec.EntryRecord>(selectedIds.Length);
            foreach (uint id in selectedIds)
            {
                if (!data!.HotWheelsMissionsById.TryGetValue(id, out var item) || !stagedById.TryGetValue(id, out var entry)
                    || !Fh5ChallengeExpansionHelper.IsEntrySupported(entry, item.ChallengeDataId, item.ObjectiveIds.Length, data))
                {
                    reason = $"Hot Wheels mission #{id} is missing from this save or unsupported.";
                    return false;
                }
                bool wasDone = entry.CompletionCount != 0;
                if (mode == EditMode.Complete && !wasDone) medalDelta += item.Medals;
                else if (mode == EditMode.Reset && wasDone) medalDelta -= item.Medals;
                targets.Add(entry);
            }

            ulong utc = Fh5ChallengeSaveCodec.EncodeDate(DateTimeOffset.UtcNow);
            if (!Fh5ChallengeExpansionHelper.ApplyChallengeEdit(targets, mode == EditMode.Complete, utc, data!, out mutated, out reason))
                return false;
        }

        int completedSum = 0;
        foreach (var item in data!.HotWheelsMissions)
        {
            if (stagedById.TryGetValue(item.Id, out var e) && e.CompletionCount != 0)
                completedSum += item.Medals;
        }

        int newMedals = mode == EditMode.SyncMedals
            ? completedSum
            : Math.Max(0, beforeView!.SavedMedals + medalDelta);

        bool needRecordWrite = mutated > 0;
        bool needBucketWrite = !beforeView!.BucketExists || beforeView.SavedMedals != newMedals;
        if (!needRecordWrite && !needBucketWrite)
        {
            change = new(0, 0, beforeView.SavedMedals, newMedals, false, false);
            reason = "";
            return true;
        }

        byte[]? editedPayload = null;
        if (needRecordWrite)
        {
            editedPayload = serializeStaged!();
            if (!Fh5ChallengeSaveCodec.TryParse(editedPayload, allowBadgeTrailing: false, out _))
            {
                reason = "Edited SpectrumMissionSaveState failed validation; nothing was changed.";
                return false;
            }
            staged.Career!.ReplacePayload(stagedRecord!, editedPayload);
        }

        if (needBucketWrite)
        {
            Fh5ExpansionsSaveState.TryReadCurrencyBucket(staged, Fh5ExpansionsSaveState.SpectrumMedalsKey,
                Fh5ExpansionsSaveState.SpectrumMedalsName, Fh5ExpansionsSaveState.SpectrumMedalsType,
                out _, out _, out var stagedTotalNode);
            Fh5ExpansionsSaveState.ApplyCurrencyBucketMutation(staged.SaveState!, stagedTotalNode,
                Fh5ExpansionsSaveState.SpectrumMedalsKey, Fh5ExpansionsSaveState.SpectrumMedalsName,
                Fh5ExpansionsSaveState.SpectrumMedalsType, newMedals);
        }

        byte[] roundTripBytes = staged.Serialize();
        var verified = ProfileContainer.Parse(roundTripBytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(roundTripBytes))
        {
            reason = "Staged Hot Wheels edit failed container round-trip verification; nothing was changed.";
            return false;
        }
        Fh5ExpansionsSaveState.CopyCatalogSnapshots(container, verified);
        if (!TryRead(verified, out var afterView, out reason) || afterView!.SavedMedals != newMedals)
        {
            reason = "Verified Hot Wheels state did not match expected medal total; nothing was changed.";
            return false;
        }

        change = new(
            mode == EditMode.Complete ? mutated : 0,
            mode == EditMode.Reset ? mutated : 0,
            beforeView.SavedMedals,
            newMedals,
            !beforeView.BucketExists && needBucketWrite,
            true);

        if (!apply) return true;

        if (needRecordWrite)
        {
            Fh5CareerRecordHelper.TryFindRecord(container, RecordName, "ChallengeSaveDataBase", out var liveRecord, bxmlValueType: RecordName);
            container.Career!.ReplacePayload(liveRecord!, editedPayload!);
        }
        if (needBucketWrite)
        {
            Fh5ExpansionsSaveState.TryReadCurrencyBucket(container, Fh5ExpansionsSaveState.SpectrumMedalsKey,
                Fh5ExpansionsSaveState.SpectrumMedalsName, Fh5ExpansionsSaveState.SpectrumMedalsType,
                out _, out _, out var liveTotalNode);
            Fh5ExpansionsSaveState.ApplyCurrencyBucketMutation(container.SaveState!, liveTotalNode,
                Fh5ExpansionsSaveState.SpectrumMedalsKey, Fh5ExpansionsSaveState.SpectrumMedalsName,
                Fh5ExpansionsSaveState.SpectrumMedalsType, newMedals);
        }
        return true;
    }
}

/// <summary>FH5 Rally Adventure team challenges (AstraChallengeSaveState + AstraReputation buckets).</summary>
public static class Fh5RallyAdventureState
{
    const string RecordName = "AstraChallengeSaveState";

    public sealed record Row(uint Id, string TeamBucket, string TeamName, int StageIndex, string Name, string Description,
        int Reputation, bool Completed, bool Supported, string Compatibility, DateTimeOffset? CompletedUtc, uint[] ObjectiveIds)
    {
        public string StageDisplay => StageIndex >= 0 ? $"Tier {StageIndex + 1}" : "—";
        public string CompletedDisplay => Completed ? "Yes" : "No";
    }

    public sealed record TeamProgress(string BucketKey, string BucketName, string TeamName, int SavedReputation,
        int CompletedReputation, bool BucketExists, bool BucketAgrees, string RankDisplay);

    public sealed record View(Row[] Rows, TeamProgress[] Teams, int SavedCount, int CatalogCount, int CompletedCount,
        bool HasInitializedEntries, string StatusNote);

    public sealed record Change(int CompletedDelta, int ResetDelta, (string TeamName, int Before, int After)[] TeamChanges, bool Changed);

    public static bool TryRead(ProfileContainer? container, out View? view, out string reason)
    {
        view = null;
        if (!Fh5ChallengeExpansionHelper.TryLoadChallengeDoc(container, RecordName, out var data, out _, out var docEntries, out _, out reason))
            return false;

        var savedRep = new Dictionary<string, (bool Exists, int Total)>(StringComparer.Ordinal);
        foreach (var team in Fh5ExpansionsSaveState.RallyTeams)
        {
            if (!Fh5ExpansionsSaveState.TryReadCurrencyBucket(container, team.BucketKey, team.BucketName,
                    Fh5ExpansionsSaveState.AstraCurrencyType, out bool exists, out int total, out _))
            {
                reason = $"Rally team reputation bucket '{team.BucketKey}' is not safely recognized.";
                return false;
            }
            savedRep[team.BucketName] = (exists, total);
        }

        var byId = docEntries!.ToDictionary(e => e.Id);
        var rows = new List<Row>(data!.RallyChallenges.Length);
        var completedRepByTeam = Fh5ExpansionsSaveState.RallyTeams.ToDictionary(t => t.BucketName, _ => 0, StringComparer.Ordinal);
        int completedCount = 0;

        foreach (var item in data.RallyChallenges)
        {
            if (byId.TryGetValue(item.Id, out var entry))
            {
                bool supported = Fh5ChallengeExpansionHelper.IsEntrySupported(entry, item.ChallengeDataId, item.ObjectiveIds.Length, data);
                bool completed = entry.CompletionCount != 0;
                if (completed)
                {
                    completedCount++;
                    if (completedRepByTeam.ContainsKey(item.TeamBucket))
                        completedRepByTeam[item.TeamBucket] += item.Reputation;
                }
                rows.Add(new(item.Id, item.TeamBucket, item.TeamName, item.StageIndex, item.Name, item.Description,
                    item.Reputation, completed, supported,
                    supported ? "Supported" : "Read-only: challenge definition differs",
                    Fh5ChallengeSaveCodec.DecodeDate(entry.UtcMicroseconds), item.ObjectiveIds));
            }
            else
            {
                rows.Add(new(item.Id, item.TeamBucket, item.TeamName, item.StageIndex, item.Name, item.Description,
                    item.Reputation, false, false, "Not initialized in save yet — visit Sierra Nueva in-game", null, item.ObjectiveIds));
            }
        }

        var teams = Fh5ExpansionsSaveState.RallyTeams.Select(t =>
        {
            var (exists, total) = savedRep[t.BucketName];
            int comp = completedRepByTeam[t.BucketName];
            return new TeamProgress(t.BucketKey, t.BucketName, t.DisplayName, total, comp, exists,
                total >= comp, Fh5ExpansionsSaveState.GetRallyTeamRankDisplay(t.BucketName, total));
        }).ToArray();

        int savedCount = docEntries!.Count;
        string note = savedCount == 0
            ? "AstraChallengeSaveState has 0 entries in this save (Sierra Nueva not visited yet). You can still edit Team Reputations directly above, or visit Sierra Nueva in-game to populate individual challenges."
            : "";
        view = new(rows.ToArray(), teams, savedCount, data.RallyChallenges.Length, completedCount, savedCount > 0, note);
        reason = "";
        return true;
    }

    public static bool TryPreviewComplete(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, EditMode.Complete, apply: false, out change, out reason);

    public static bool TryComplete(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, EditMode.Complete, apply: true, out change, out reason);

    public static bool TryPreviewReset(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, EditMode.Reset, apply: false, out change, out reason);

    public static bool TryReset(ProfileContainer? container, IEnumerable<uint> ids, out Change? change, out string reason)
        => TryExecute(container, ids, EditMode.Reset, apply: true, out change, out reason);

    public static bool TryPreviewSyncReputation(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), EditMode.SyncReputation, apply: false, out change, out reason);

    public static bool TrySyncReputation(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), EditMode.SyncReputation, apply: true, out change, out reason);

    enum EditMode { Complete, Reset, SyncReputation }

    static bool TryExecute(ProfileContainer? container, IEnumerable<uint> ids, EditMode mode, bool apply, out Change? change, out string reason)
    {
        change = null;
        if (!TryRead(container, out var beforeView, out reason)) return false;

        var selectedIds = ids?.Distinct().ToArray() ?? Array.Empty<uint>();
        if (mode != EditMode.SyncReputation && selectedIds.Length == 0)
        {
            reason = "Select at least one Rally Adventure challenge.";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        Fh5ExpansionsSaveState.CopyCatalogSnapshots(container, staged);
        if (!Fh5ChallengeExpansionHelper.TryLoadChallengeDoc(staged, RecordName, out var data, out var stagedRecord, out var stagedEntries, out var serializeStaged, out reason))
            return false;

        var stagedById = stagedEntries!.ToDictionary(e => e.Id);
        int mutated = 0;
        var deltaByTeam = Fh5ExpansionsSaveState.RallyTeams.ToDictionary(t => t.BucketName, _ => 0, StringComparer.Ordinal);

        if (mode != EditMode.SyncReputation)
        {
            var targets = new List<Fh5ChallengeSaveCodec.EntryRecord>(selectedIds.Length);
            foreach (uint id in selectedIds)
            {
                if (!data!.RallyChallengesById.TryGetValue(id, out var item) || !stagedById.TryGetValue(id, out var entry)
                    || !Fh5ChallengeExpansionHelper.IsEntrySupported(entry, item.ChallengeDataId, item.ObjectiveIds.Length, data))
                {
                    reason = $"Rally challenge #{id} is missing from this save or unsupported.";
                    return false;
                }
                bool wasDone = entry.CompletionCount != 0;
                if (mode == EditMode.Complete && !wasDone) deltaByTeam[item.TeamBucket] += item.Reputation;
                else if (mode == EditMode.Reset && wasDone) deltaByTeam[item.TeamBucket] -= item.Reputation;
                targets.Add(entry);
            }

            ulong utc = Fh5ChallengeSaveCodec.EncodeDate(DateTimeOffset.UtcNow);
            if (!Fh5ChallengeExpansionHelper.ApplyChallengeEdit(targets, mode == EditMode.Complete, utc, data!, out mutated, out reason))
                return false;
        }

        var completedRepByTeam = Fh5ExpansionsSaveState.RallyTeams.ToDictionary(t => t.BucketName, _ => 0, StringComparer.Ordinal);
        foreach (var item in data!.RallyChallenges)
        {
            if (stagedById.TryGetValue(item.Id, out var e) && e.CompletionCount != 0 && completedRepByTeam.ContainsKey(item.TeamBucket))
                completedRepByTeam[item.TeamBucket] += item.Reputation;
        }

        var teamTargets = new List<(Fh5ExpansionsSaveState.RallyTeamDef Team, int Before, int After, bool ExistsBefore)>();
        foreach (var team in Fh5ExpansionsSaveState.RallyTeams)
        {
            var beforeTeam = beforeView!.Teams.Single(t => t.BucketName == team.BucketName);
            int targetRep = mode == EditMode.SyncReputation
                ? Math.Max(beforeTeam.SavedReputation, completedRepByTeam[team.BucketName])
                : Math.Max(0, beforeTeam.SavedReputation + deltaByTeam[team.BucketName]);
            if (targetRep != beforeTeam.SavedReputation || !beforeTeam.BucketExists)
                teamTargets.Add((team, beforeTeam.SavedReputation, targetRep, beforeTeam.BucketExists));
        }

        bool needRecordWrite = mutated > 0;
        if (!needRecordWrite && teamTargets.Count == 0)
        {
            change = new(0, 0, Array.Empty<(string, int, int)>(), false);
            reason = "";
            return true;
        }

        byte[]? editedPayload = null;
        if (needRecordWrite)
        {
            editedPayload = serializeStaged!();
            if (!Fh5ChallengeSaveCodec.TryParse(editedPayload, allowBadgeTrailing: false, out _))
            {
                reason = "Edited AstraChallengeSaveState failed validation; nothing was changed.";
                return false;
            }
            staged.Career!.ReplacePayload(stagedRecord!, editedPayload);
        }

        foreach (var t in teamTargets)
        {
            Fh5ExpansionsSaveState.TryReadCurrencyBucket(staged, t.Team.BucketKey, t.Team.BucketName,
                Fh5ExpansionsSaveState.AstraCurrencyType, out _, out _, out var stagedTotalNode);
            Fh5ExpansionsSaveState.ApplyCurrencyBucketMutation(staged.SaveState!, stagedTotalNode,
                t.Team.BucketKey, t.Team.BucketName, Fh5ExpansionsSaveState.AstraCurrencyType, t.After);
        }

        byte[] roundTripBytes = staged.Serialize();
        var verified = ProfileContainer.Parse(roundTripBytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(roundTripBytes))
        {
            reason = "Staged Rally Adventure edit failed container round-trip verification; nothing was changed.";
            return false;
        }
        Fh5ExpansionsSaveState.CopyCatalogSnapshots(container, verified);
        if (!TryRead(verified, out var afterView, out reason)) return false;
        foreach (var t in teamTargets)
        {
            if (afterView!.Teams.Single(x => x.BucketName == t.Team.BucketName).SavedReputation != t.After)
            {
                reason = $"Verified reputation for {t.Team.DisplayName} did not match expected value; nothing was changed.";
                return false;
            }
        }

        change = new(
            mode == EditMode.Complete ? mutated : 0,
            mode == EditMode.Reset ? mutated : 0,
            teamTargets.Select(t => (t.Team.DisplayName, t.Before, t.After)).ToArray(),
            true);

        if (!apply) return true;

        if (needRecordWrite)
        {
            Fh5CareerRecordHelper.TryFindRecord(container, RecordName, "ChallengeSaveDataBase", out var liveRecord, bxmlValueType: RecordName);
            container.Career!.ReplacePayload(liveRecord!, editedPayload!);
        }
        foreach (var t in teamTargets)
        {
            Fh5ExpansionsSaveState.TryReadCurrencyBucket(container, t.Team.BucketKey, t.Team.BucketName,
                Fh5ExpansionsSaveState.AstraCurrencyType, out _, out _, out var liveTotalNode);
            Fh5ExpansionsSaveState.ApplyCurrencyBucketMutation(container.SaveState!, liveTotalNode,
                t.Team.BucketKey, t.Team.BucketName, Fh5ExpansionsSaveState.AstraCurrencyType, t.After);
        }
        return true;
    }
}
#endif
