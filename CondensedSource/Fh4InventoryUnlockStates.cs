#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

internal static class Fh4CareerRecordHelper
{
    internal static bool TryFindRecord(ProfileContainer? container, string recordName, string serializerName, out BinaryCareer.Record? record, string? bxmlValueType = null)
    {
        record = null;
        string expectedType = bxmlValueType ?? serializerName;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        if (bx.Strings[bx.Root.NameIndex] != "ObjectDictionary") return false;
        var records = career.Records.Where(r => r.Name == recordName).ToArray();
        if (records.Length != 1 || records[0].Serializer != serializerName) return false;

        var objects = bx.Root.Children.Where(n =>
            bx.Strings[n.NameIndex] == "Object" &&
            string.Equals(bx.GetAttribute(n, "Id"), recordName, StringComparison.Ordinal)).ToArray();
        if (objects.Length != 1) return false;
        var obj = objects[0];
        if (bx.GetAttribute(obj, "Type") != expectedType || obj.Children.Count != 0) return false;

        record = records[0];
        return true;
    }

    internal static (bool Recognized, uint[] Items) ReadU32Tail(byte[] bytes, int headerBytes, int maxItems)
    {
        if (bytes.Length < headerBytes + 4) return (false, Array.Empty<uint>());
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(headerBytes, 4));
        int itemsOffset = headerBytes + 4;
        if (count > maxItems || bytes.Length != itemsOffset + count * 4L) return (false, Array.Empty<uint>());
        var (ok, items) = Fh5CareerRecordHelper.ReadDistinctU32Tail(bytes, itemsOffset, (int)count, id => id);
        return (ok, ok ? items : Array.Empty<uint>());
    }

    internal static byte[] SerializeU32Tail(byte[] header, IReadOnlyList<uint> items)
    {
        int headerLen = header.Length;
        byte[] result = new byte[headerLen + 4 + items.Count * 4];
        header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(headerLen, 4), (uint)items.Count);
        int offset = headerLen + 4;
        for (int i = 0; i < items.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + i * 4, 4), items[i]);
        return result;
    }
}

/// <summary>FH4 CharacterCustomisation wardrobe unlocking (u16 IDs, 45B header, u32 count, sorted 4B entries, 1B trailer).</summary>
public static class Fh4ClothingUnlockState
{
    const string RecordName = "CharacterCustomisation";
    const string Serializer = "CharacterCustomisationState";
    const int HeaderBytes = 45;
    const int MaxItems = 10_000;

    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries, int Missing, int StoredLocked);
    readonly record struct Flags(byte Owned, byte IsNew);
    sealed record Inventory(byte[] Header, SortedDictionary<ushort, Flags> Items, byte Trailer);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        var data = Fh4ProfileGameData.For(container);
        if (data == null || !Fh4CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        int unlocked = 0, missing = 0, storedLocked = 0;
        foreach (var item in data.Clothing)
        {
            if (!inv!.Items.TryGetValue(item.Id, out var flags)) missing++;
            else if (flags.Owned != 0) unlocked++;
            else storedLocked++;
        }
        status = new(unlocked, data.Clothing.Length, inv!.Items.Count, missing, storedLocked);
        return true;
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out ushort[] ids)
    {
        ids = Array.Empty<ushort>();
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.Where(p => p.Value.Owned != 0).Select(p => p.Key).ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int changed)
    {
        changed = 0;
        var data = Fh4ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > ushort.MaxValue || !data.ClothingById.ContainsKey((ushort)id)))
            return false;
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<ushort>(selected.Length);
        foreach (ulong id in selected) selectedSet.Add((ushort)id);
        int updated = 0;
        foreach (ushort id in selectedSet)
        {
            if (inv!.Items.TryAdd(id, new Flags(1, 0))) updated++;
            else if (inv.Items[id].Owned == 0) { inv.Items[id] = new Flags(1, 0); updated++; }
        }
        if (updated == 0) return true;
        if (inv!.Items.Count > MaxItems) return false;
        byte[] edited = Serialize(inv);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        changed = updated;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < HeaderBytes + 5) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 1) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes, 4));
        if (count > MaxItems || bytes.Length != HeaderBytes + 5L + count * 4L) return false;
        var items = new SortedDictionary<ushort, Flags>();
        int offset = HeaderBytes + 4;
        for (int i = 0; i < count; i++)
        {
            int pos = offset + i * 4;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2));
            byte owned = bytes[pos + 2];
            byte isNew = bytes[pos + 3];
            if (!items.TryAdd(id, new Flags(owned, isNew))) return false;
        }
        byte trailer = bytes[^1];
        var parsed = new Inventory(bytes[..HeaderBytes], items, trailer);
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        byte[] result = new byte[HeaderBytes + 5 + inventory.Items.Count * 4];
        inventory.Header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(HeaderBytes, 4), (uint)inventory.Items.Count);
        int pos = HeaderBytes + 4;
        foreach (var pair in inventory.Items)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos, 2), pair.Key);
            result[pos + 2] = pair.Value.Owned;
            result[pos + 3] = pair.Value.IsNew;
            pos += 4;
        }
        result[pos] = inventory.Trailer;
        return result;
    }
}

/// <summary>FH4 CarHornSaveState unlocking (u32 ver=1, u8 hasEquipped=1, u32 equippedId, u32 count, count x u32 IDs).</summary>
public static class Fh4CarHornUnlockState
{
    const string RecordName = "CarHornSaveState";
    const int HeaderBytes = 9;
    const int MaxItems = 10_000;
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh4ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.HornsById.ContainsKey((uint)id)))
            return false;
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var horn in data.Horns)
            if (selectedSet.Contains(horn.Id) && owned.Add(horn.Id)) { inv.Items.Add(horn.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxItems) return false;
        byte[] edited = Fh4CareerRecordHelper.SerializeU32Tail(inv.Header, inv.Items);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < HeaderBytes + 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 1 || bytes[4] != 1)
            return false;
        var (ok, items) = Fh4CareerRecordHelper.ReadU32Tail(bytes, HeaderBytes, MaxItems);
        if (!ok) return false;
        uint equipped = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        if (!items.Contains(equipped)) return false;
        var parsed = new Inventory(bytes[..HeaderBytes], new List<uint>(items));
        if (!Fh4CareerRecordHelper.SerializeU32Tail(parsed.Header, parsed.Items).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }
}

/// <summary>FH4 EmoteSaveState unlocking (u32 ver=0, u32 eq0, u32 eq1, u32 count, count x u32 IDs).</summary>
public static class Fh4EmoteUnlockState
{
    const string RecordName = "EmoteSaveState";
    const int HeaderBytes = 12;
    const int MaxItems = 10_000;
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh4ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.EmotesById.ContainsKey((uint)id)))
            return false;
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var emote in data.Emotes)
            if (selectedSet.Contains(emote.Id) && owned.Add(emote.Id)) { inv.Items.Add(emote.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxItems) return false;
        byte[] edited = Fh4CareerRecordHelper.SerializeU32Tail(inv.Header, inv.Items);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < HeaderBytes + 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0)
            return false;
        var (ok, items) = Fh4CareerRecordHelper.ReadU32Tail(bytes, HeaderBytes, MaxItems);
        if (!ok) return false;
        var set = items.ToHashSet();
        foreach (int eqOffset in new[] { 4, 8 })
        {
            uint eq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eqOffset, 4));
            if (eq != uint.MaxValue && !set.Contains(eq)) return false;
        }
        var parsed = new Inventory(bytes[..HeaderBytes], new List<uint>(items));
        if (!Fh4CareerRecordHelper.SerializeU32Tail(parsed.Header, parsed.Items).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }
}

/// <summary>FH4 QuickChatSaveState unlocking (preserving context-slot bindings verbatim + trailing u32 ID list).</summary>
public static class Fh4QuickChatUnlockState
{
    const string RecordName = "QuickChatSaveState";
    const int MaxContexts = 16;
    const int MaxSlots = 8;
    const int MaxItems = 10_000;
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh4ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.PhrasesById.ContainsKey((uint)id)))
            return false;
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var phrase in data.Phrases)
            if (selectedSet.Contains(phrase.Id) && owned.Add(phrase.Id)) { inv.Items.Add(phrase.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxItems) return false;
        byte[] edited = Fh4CareerRecordHelper.SerializeU32Tail(inv.Header, inv.Items);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0)
            return false;
        uint contextCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (contextCount is 0 or > MaxContexts) return false;
        int offset = 8;
        var contexts = new HashSet<uint>();
        for (int c = 0; c < contextCount; c++)
        {
            if (offset + 8 > bytes.Length) return false;
            uint ctxId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint slotCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (!contexts.Add(ctxId) || slotCount > MaxSlots || offset + 8L + slotCount * 8L > bytes.Length - 4)
                return false;
            offset += 8 + (int)slotCount * 8;
        }
        var (ok, items) = Fh4CareerRecordHelper.ReadU32Tail(bytes, offset, MaxItems);
        if (!ok) return false;
        var parsed = new Inventory(bytes[..offset], new List<uint>(items));
        if (!Fh4CareerRecordHelper.SerializeU32Tail(parsed.Header, parsed.Items).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }
}

/// <summary>FH4 FlairRewardsSaveState unlocking (u32 ver=3, u32 equippedOrNone, u32 count, count x u32 IDs).</summary>
public static class Fh4FlairUnlockState
{
    const string RecordName = "FlairRewardsSaveState";
    const int HeaderBytes = 8;
    const int MaxItems = 1_000;
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh4ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.FlairsById.ContainsKey((uint)id)))
            return false;
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var flair in data.Flairs)
            if (selectedSet.Contains(flair.Id) && owned.Add(flair.Id)) { inv.Items.Add(flair.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxItems) return false;
        byte[] edited = Fh4CareerRecordHelper.SerializeU32Tail(inv.Header, inv.Items);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < HeaderBytes + 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 3)
            return false;
        var (ok, items) = Fh4CareerRecordHelper.ReadU32Tail(bytes, HeaderBytes, MaxItems);
        if (!ok) return false;
        uint equipped = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (equipped != uint.MaxValue && !items.Contains(equipped)) return false;
        var parsed = new Inventory(bytes[..HeaderBytes], new List<uint>(items));
        if (!Fh4CareerRecordHelper.SerializeU32Tail(parsed.Header, parsed.Items).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }
}
#endif
