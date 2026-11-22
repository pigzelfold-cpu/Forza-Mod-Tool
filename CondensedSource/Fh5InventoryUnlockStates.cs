#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

internal static class Fh5CareerRecordHelper
{
    internal static bool TryFindRecord(ProfileContainer? container, string recordName, string serializerName, out BinaryCareer.Record? record, string? bxmlValueType = null)
    {
        record = null;
        string expectedBxmlType = bxmlValueType ?? serializerName;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == recordName).ToArray();
        if (records.Length != 1 || records[0].Serializer != serializerName) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == recordName) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != expectedBxmlType ||
            values[0].Children.Count != 0 || values[0].Attributes.Count != 1) return false;
        record = records[0];
        return true;
    }

    internal static (bool Recognized, T[] Parsed) ReadDistinctU32Tail<T>(byte[] bytes, int itemsOffset, int count, Func<uint, T> wrap)
    {
        var seen = new HashSet<uint>();
        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(itemsOffset + i * 4, 4));
            if (!seen.Add(id)) return (false, Array.Empty<T>());
            list.Add(wrap(id));
        }
        return (true, list.ToArray());
    }
}

/// <summary>FH5 CharacterCustomisation wardrobe unlocking (u16 IDs, 65B header, u32 count, sorted 4B entries, 1B trailer).</summary>
public static class Fh5ClothingUnlockState
{
    const string RecordName = "CharacterCustomisation";
    const string Serializer = "CharacterCustomisationState";
    const int HeaderBytes = 65;
    const int MaxItems = 10_000;

    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries, int Missing, int StoredLocked);
    readonly record struct Flags(byte Owned, byte IsNew);
    sealed record Inventory(byte[] Header, SortedDictionary<ushort, Flags> Items, byte Trailer);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
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
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.Where(p => p.Value.Owned != 0).Select(p => p.Key).ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int changed)
    {
        changed = 0;
        var data = Fh5ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > ushort.MaxValue || !data.ClothingById.ContainsKey((ushort)id)))
            return false;
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, Serializer, out var record) || !TryParse(record!.Payload, out var inv))
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

/// <summary>FH5 CarHornSaveState unlocking (u32 ver=1, u8 hasEquipped=1, u32 equippedId, u32 count, count x u32 IDs).</summary>
public static class Fh5CarHornUnlockState
{
    const string RecordName = "CarHornSaveState";
    const int HeaderBytes = 9;
    const int ItemsOffset = 13;
    const int MaxHorns = 10_000;

    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        var owned = inv!.Items.ToHashSet();
        status = new(data.Horns.Count(h => owned.Contains(h.Id)), data.Horns.Length, owned.Count);
        return true;
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh5ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.HornsById.ContainsKey((uint)id)))
            return false;
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var horn in data.Horns)
            if (selectedSet.Contains(horn.Id) && owned.Add(horn.Id)) { inv.Items.Add(horn.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxHorns) return false;
        byte[] edited = Serialize(inv);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < ItemsOffset || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 1 || bytes[4] != 1)
            return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes, 4));
        if (count > MaxHorns || bytes.Length != ItemsOffset + count * 4L) return false;
        var (ok, items) = Fh5CareerRecordHelper.ReadDistinctU32Tail(bytes, ItemsOffset, (int)count, id => id);
        if (!ok) return false;
        uint equipped = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        if (!items.Contains(equipped)) return false;
        var parsed = new Inventory(bytes[..HeaderBytes], new List<uint>(items));
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        byte[] result = new byte[ItemsOffset + inventory.Items.Count * 4];
        inventory.Header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(HeaderBytes, 4), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ItemsOffset + i * 4, 4), inventory.Items[i]);
        return result;
    }
}

/// <summary>FH5 EmoteSaveState unlocking (u32 ver=0, u32 eq0, u32 eq1, u32 count, count x u32 IDs).</summary>
public static class Fh5EmoteUnlockState
{
    const string RecordName = "EmoteSaveState";
    const int HeaderBytes = 12;
    const int ItemsOffset = 16;
    const int MaxEmotes = 10_000;

    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        var owned = inv!.Items.ToHashSet();
        status = new(data.Emotes.Count(e => owned.Contains(e.Id)), data.Emotes.Length, owned.Count);
        return true;
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh5ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.EmotesById.ContainsKey((uint)id)))
            return false;
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var emote in data.Emotes)
            if (selectedSet.Contains(emote.Id) && owned.Add(emote.Id)) { inv.Items.Add(emote.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxEmotes) return false;
        byte[] edited = Serialize(inv);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < ItemsOffset || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0)
            return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes, 4));
        if (count > MaxEmotes || bytes.Length != ItemsOffset + count * 4L) return false;
        var (ok, items) = Fh5CareerRecordHelper.ReadDistinctU32Tail(bytes, ItemsOffset, (int)count, id => id);
        if (!ok) return false;
        var set = items.ToHashSet();
        foreach (int eqOffset in new[] { 4, 8 })
        {
            uint eq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eqOffset, 4));
            if (eq != uint.MaxValue && !set.Contains(eq)) return false;
        }
        var parsed = new Inventory(bytes[..HeaderBytes], new List<uint>(items));
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        byte[] result = new byte[ItemsOffset + inventory.Items.Count * 4];
        inventory.Header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(HeaderBytes, 4), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ItemsOffset + i * 4, 4), inventory.Items[i]);
        return result;
    }
}

/// <summary>FH5 QuickChatSaveState unlocking (preserving all context-slot bindings verbatim + trailing u32 ID list).</summary>
public static class Fh5ForzaLinkUnlockState
{
    const string RecordName = "QuickChatSaveState";
    const int MaxContexts = 16;
    const int MaxSlots = 8;
    const int MaxPhrases = 10_000;

    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<uint> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        var data = Fh5ProfileGameData.For(container);
        if (data == null || !Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        var owned = inv!.Items.ToHashSet();
        status = new(data.Phrases.Count(p => owned.Contains(p.Id)), data.Phrases.Length, owned.Count);
        return true;
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;
        ids = inv!.Items.ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var data = Fh5ProfileGameData.For(container);
        if (data == null) return false;
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.PhrasesById.ContainsKey((uint)id)))
            return false;
        if (!Fh5CareerRecordHelper.TryFindRecord(container, RecordName, RecordName, out var record) || !TryParse(record!.Payload, out var inv))
            return false;

        var selectedSet = new HashSet<uint>(selected.Select(id => (uint)id));
        var owned = inv!.Items.ToHashSet();
        int changed = 0;
        foreach (var phrase in data.Phrases)
            if (selectedSet.Contains(phrase.Id) && owned.Add(phrase.Id)) { inv.Items.Add(phrase.Id); changed++; }
        if (changed == 0) return true;
        if (inv.Items.Count > MaxPhrases) return false;
        byte[] edited = Serialize(inv);
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
        uint ownedCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        int itemsOffset = offset + 4;
        if (ownedCount > MaxPhrases || bytes.Length != itemsOffset + ownedCount * 4L) return false;
        var (ok, items) = Fh5CareerRecordHelper.ReadDistinctU32Tail(bytes, itemsOffset, (int)ownedCount, id => id);
        if (!ok) return false;
        var parsed = new Inventory(bytes[..offset], new List<uint>(items));
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        int headerLen = inventory.Header.Length;
        byte[] result = new byte[headerLen + 4 + inventory.Items.Count * 4];
        inventory.Header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(headerLen, 4), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(headerLen + 4 + i * 4, 4), inventory.Items[i]);
        return result;
    }
}
#endif
