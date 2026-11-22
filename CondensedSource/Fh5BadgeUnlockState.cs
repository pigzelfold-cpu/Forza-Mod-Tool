#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

/// <summary>FH5 BadgeChallengeSaveState unlock and UTC earned-date editing.</summary>
public static class Fh5BadgeUnlockState
{
    const string RecordName = "BadgeChallengeSaveState";
    const string SerializerName = "ChallengeSaveDataBase";

    public sealed record Row(uint Id, string Name, string Description, string Category, bool Unlocked, bool Supported, DateTimeOffset? EarnedUtc = null);

    public static bool TryRead(ProfileContainer? container, out Row[] rows)
    {
        rows = Array.Empty<Row>();
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, SerializerName, out var record, bxmlValueType: RecordName)
            || !Fh5ChallengeSaveCodec.TryParse(record!.Payload, allowBadgeTrailing: true, out var doc))
            return false;

        var list = new List<Row>(doc!.Entries.Count);
        foreach (var e in doc.Entries)
        {
            data.BadgesById.TryGetValue(e.Id, out var def);
            bool supported = def != null && def.ChallengeDataId == e.ChallengeDataId
                && e.Objectives.Count == def.Objectives.Length
                && IsEntrySupported(e, data);
            string name = def?.Name ?? $"Badge #{e.Id}";
            string desc = def?.Description ?? "";
            list.Add(new Row(e.Id, name, desc, "Horizon Badge", e.CompletionCount != 0, supported, Fh5ChallengeSaveCodec.DecodeDate(e.UtcMicroseconds)));
        }
        rows = list.OrderBy(r => r.Id).ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        DateTimeOffset? earnedUtc, out int added, out string error)
    {
        added = 0; error = "";
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, SerializerName, out var record, bxmlValueType: RecordName)
            || !Fh5ChallengeSaveCodec.TryParse(record!.Payload, allowBadgeTrailing: true, out var doc))
        {
            error = "Badge save data or FH5 TIDE tables are not loaded.";
            return false;
        }

        if (!TryValidateDate(earnedUtc, out var stampedUtc, out error)) return false;

        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0) { error = "Select badges to unlock."; return false; }

        var byId = doc!.Entries.ToDictionary(e => e.Id);
        var targets = new List<Fh5ChallengeSaveCodec.EntryRecord>();
        foreach (ulong rawId in selected)
        {
            if (rawId > uint.MaxValue || !byId.TryGetValue((uint)rawId, out var entry)
                || !data.BadgesById.TryGetValue(entry.Id, out var def)
                || def.ChallengeDataId != entry.ChallengeDataId
                || entry.Objectives.Count != def.Objectives.Length
                || !IsEntrySupported(entry, data))
            {
                error = $"Badge {rawId} is missing or has an unsupported challenge definition.";
                return false;
            }
            targets.Add(entry);
        }

        ulong encodedUtc = Fh5ChallengeSaveCodec.EncodeDate(stampedUtc);
        int count = 0;
        foreach (var entry in targets)
        {
            if (entry.CompletionCount != 0) continue;
            foreach (var obj in entry.Objectives)
            {
                data.ObjectivesById.TryGetValue(obj.ObjectiveId, out var odef);
                if (!Fh5ChallengeSaveCodec.TryComputeCompletedValue(obj.ValueType, obj.Value, odef, out string newVal))
                {
                    error = $"Could not compute completion value for objective {obj.ObjectiveId} ({obj.ValueType}).";
                    return false;
                }
                obj.Done = 1;
                obj.Value = newVal;
            }
            entry.UnlockedFlag = 1;
            entry.ActiveObjectiveId = 0;
            entry.CompletionCount = 1;
            entry.UtcMicroseconds = encodedUtc;
            count++;
        }

        if (count == 0) return true;
        byte[] bytes = Fh5ChallengeSaveCodec.Serialize(doc);
        if (!Fh5ChallengeSaveCodec.TryParse(bytes, allowBadgeTrailing: true, out _))
        {
            error = "Edited BadgeChallengeSaveState failed validation.";
            return false;
        }
        container!.Career!.ReplacePayload(record!, bytes);
        added = count;
        return true;
    }

    public static bool TrySetEarnedDate(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        DateTimeOffset earnedUtc, out int changed, out string error)
    {
        changed = 0; error = "";
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, SerializerName, out var record, bxmlValueType: RecordName)
            || !Fh5ChallengeSaveCodec.TryParse(record!.Payload, allowBadgeTrailing: true, out var doc))
        {
            error = "Badge save data or FH5 TIDE tables are not loaded.";
            return false;
        }
        if (!TryValidateDate(earnedUtc, out var stampedUtc, out error)) return false;

        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0) { error = "Select unlocked badges to edit."; return false; }

        var byId = doc!.Entries.ToDictionary(e => e.Id);
        var targets = new List<Fh5ChallengeSaveCodec.EntryRecord>();
        foreach (ulong rawId in selected)
        {
            if (rawId > uint.MaxValue || !byId.TryGetValue((uint)rawId, out var entry) || !IsEntrySupported(entry, data))
            {
                error = $"Badge {rawId} is not supported.";
                return false;
            }
            if (entry.CompletionCount == 0)
            {
                error = "Select unlocked badges to change their earned date. Use Unlock selected badges for locked ones.";
                return false;
            }
            targets.Add(entry);
        }

        ulong encoded = Fh5ChallengeSaveCodec.EncodeDate(stampedUtc);
        int updated = 0;
        foreach (var entry in targets)
        {
            if (entry.UtcMicroseconds == encoded) continue;
            entry.UtcMicroseconds = encoded;
            updated++;
        }
        if (updated == 0) return true;
        byte[] bytes = Fh5ChallengeSaveCodec.Serialize(doc);
        if (!Fh5ChallengeSaveCodec.TryParse(bytes, allowBadgeTrailing: true, out _))
        {
            error = "Edited BadgeChallengeSaveState failed validation.";
            return false;
        }
        container!.Career!.ReplacePayload(record!, bytes);
        changed = updated;
        return true;
    }

    static bool TryValidateDate(DateTimeOffset? requested, out DateTimeOffset validated, out string error)
    {
        error = "";
        var now = DateTimeOffset.UtcNow;
        var date = (requested ?? now).ToUniversalTime();
        if (date < DateTimeOffset.UnixEpoch.AddTicks(10) || date > now)
        {
            validated = default;
            error = "Earned dates must be after 1970-01-01 and cannot be in the future.";
            return false;
        }
        validated = DateTimeOffset.UnixEpoch.AddTicks(((date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) * 10);
        return true;
    }

    static bool IsEntrySupported(Fh5ChallengeSaveCodec.EntryRecord entry, Fh5ProfileGameData.Snapshot data)
        => entry.Objectives.All(o => Fh5ChallengeSaveCodec.IsTypeSupported(o.ValueType) && data.ObjectivesById.ContainsKey(o.ObjectiveId));
}
#endif
