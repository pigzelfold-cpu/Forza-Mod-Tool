#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>FH3 CarHornSaveState unlocking (u32 ver=1, u8 hasEquipped=1, u32 equippedId, u32 count, count x u32 IDs).</summary>
public static class Fh3CarHornUnlockState
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
        var data = Fh3ProfileGameData.ForOrEmbedded(container);
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

/// <summary>FH3 SQLite PerkUnlock + PerkState unlocker and /CompareStats/PerksUnlocked sync.</summary>
public static class Fh3PerkUnlockState
{
    public static bool TryReadUnlocked(ProfileContainer? container, out uint[] ids)
        => TryReadUnlocked(container, null, out ids);

    public static bool TryReadUnlocked(ProfileContainer? container, SqliteConnection? liveConn, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (container is null) return false;
        var data = Fh3ProfileGameData.ForOrEmbedded(container);
        try
        {
            if (liveConn != null)
                return ReadFromConn(liveConn, data, out ids);

            uint[] loadedIds = Array.Empty<uint>();
            bool ok = WithTempDb(container, writeBack: false, conn => ReadFromConn(conn, data, out loadedIds));
            ids = loadedIds;
            return ok;
        }
        catch (Exception ex) when (ex is IOException or SqliteException or InvalidDataException)
        {
            return false;
        }
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int changed)
        => TryUnlockSelected(container, null, selectedIds, out changed);

    public static bool TryUnlockSelected(ProfileContainer? container, SqliteConnection? liveConn, IEnumerable<ulong>? selectedIds, out int changed)
    {
        changed = 0;
        if (container is null) return false;
        var data = Fh3ProfileGameData.ForOrEmbedded(container);
        var selected = selectedIds?.ToArray();
        if (selected is null || selected.Length == 0 || selected.Any(id => id > uint.MaxValue || !data.PerksById.ContainsKey((uint)id)))
            return false;

        int updatedCount = 0;
        bool ok;
        try
        {
            if (liveConn != null)
            {
                ok = ApplyToConn(liveConn, container, data, selected, out updatedCount);
            }
            else
            {
                ok = WithTempDb(container, writeBack: true, conn => ApplyToConn(conn, container, data, selected, out updatedCount));
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException or InvalidDataException)
        {
            return false;
        }

        if (!ok) return false;
        changed = updatedCount;
        return true;
    }

    static bool ReadFromConn(SqliteConnection conn, Fh3ProfileGameData.Snapshot data, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!HasTables(conn)) return false;

        var unlocked = ReadUnlockTable(conn);
        var states = ReadStateTable(conn);
        var result = new List<uint>();

        foreach (var perk in data.Perks)
        {
            if (perk.TotalUses == 0)
            {
                if (unlocked.Contains(perk.Id)) result.Add(perk.Id);
            }
            else if (unlocked.Contains(perk.Id) && states.TryGetValue(perk.Id, out var st) && st.Has != 0 && st.Rem > 0)
            {
                result.Add(perk.Id);
            }
        }
        ids = result.ToArray();
        return true;
    }

    static bool ApplyToConn(
        SqliteConnection conn,
        ProfileContainer container,
        Fh3ProfileGameData.Snapshot data,
        ulong[] selected,
        out int updated)
    {
        updated = 0;
        if (!HasTables(conn)) return false;

        var existingUnlocks = ReadUnlockTable(conn);
        var existingStates = ReadStateTable(conn);

        using var tx = conn.BeginTransaction();
        int delta = 0;
        foreach (ulong rawId in selected.Distinct())
        {
            var perk = data.PerksById[(uint)rawId];
            bool inUnlock = existingUnlocks.Contains(perk.Id);
            if (!inUnlock)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO PerkUnlock (UnlockId) VALUES ($id)";
                cmd.Parameters.AddWithValue("$id", (long)perk.Id);
                cmd.ExecuteNonQuery();
                existingUnlocks.Add(perk.Id);
            }

            if (perk.TotalUses > 0)
            {
                bool needsState = !inUnlock || !existingStates.TryGetValue(perk.Id, out var st) || st.Has == 0 || st.Rem < perk.TotalUses;
                if (needsState)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO PerkState (Id, RemainingUses, HasBeenUnlocked) VALUES ($id, $uses, 1) " +
                        "ON CONFLICT(Id) DO UPDATE SET RemainingUses = MAX(RemainingUses, $uses), HasBeenUnlocked = 1";
                    cmd.Parameters.AddWithValue("$id", (long)perk.Id);
                    cmd.Parameters.AddWithValue("$uses", perk.TotalUses);
                    cmd.ExecuteNonQuery();
                    delta++;
                }
            }
            else if (!inUnlock)
            {
                delta++;
            }
        }
        tx.Commit();

        if (delta > 0)
        {
            var postUnlocks = ReadUnlockTable(conn);
            var postStates = ReadStateTable(conn);
            var distinctUnlocked = new HashSet<uint>(postUnlocks);
            foreach (var kv in postStates)
                if (kv.Value.Has != 0) distinctUnlocked.Add(kv.Key);

            var statNode = container.Properties?.Walk().FirstOrDefault(p => p.Path == "/CompareStats/PerksUnlocked").Node;
            if (statNode is { IsEditable: true } &&
                uint.TryParse(statNode.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint oldCount) &&
                distinctUnlocked.Count > oldCount)
            {
                statNode.TrySetFromText(((uint)distinctUnlocked.Count).ToString(CultureInfo.InvariantCulture));
            }
        }

        updated = delta;
        return true;
    }

    static bool HasTables(SqliteConnection conn)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('PerkUnlock','PerkState')";
        return Convert.ToInt64(c.ExecuteScalar()) == 2;
    }

    static HashSet<uint> ReadUnlockTable(SqliteConnection conn)
    {
        var set = new HashSet<uint>();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT UnlockId FROM PerkUnlock";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long val = r.GetInt64(0);
            if (val is >= 0 and <= uint.MaxValue) set.Add((uint)val);
        }
        return set;
    }

    static Dictionary<uint, (int Rem, int Has)> ReadStateTable(SqliteConnection conn)
    {
        var map = new Dictionary<uint, (int, int)>();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT Id, RemainingUses, HasBeenUnlocked FROM PerkState";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            if (id is >= 0 and <= uint.MaxValue)
                map[(uint)id] = (r.GetInt32(1), r.GetInt32(2));
        }
        return map;
    }

    static bool WithTempDb(ProfileContainer container, bool writeBack, Func<SqliteConnection, bool> action)
    {
        if (container.Database.Length < 16) return false;
        string tmp = Path.Combine(TempWorkspace.Root, $"fh3perks_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
        try
        {
            File.WriteAllBytes(tmp, container.Database);
            bool result;
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false, Mode = writeBack ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly }.ToString()))
            {
                conn.Open();
                result = action(conn);
                if (writeBack && result)
                {
                    try { using var wal = conn.CreateCommand(); wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; wal.ExecuteNonQuery(); }
                    catch { }
                }
            }
            SqliteConnection.ClearAllPools();
            if (writeBack && result)
                container.Database = File.ReadAllBytes(tmp);
            return result;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
#endif
