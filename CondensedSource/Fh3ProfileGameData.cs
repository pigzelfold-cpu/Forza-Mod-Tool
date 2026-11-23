#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Installed FH3 GameDB/EN string-table metadata with embedded fallback for Car Horns and Skill Perks.</summary>
public static class Fh3ProfileGameData
{
    public sealed record HornItem(uint Id, string Name, int Category, string CategoryName, int UnlockByPerk);
    public sealed record PerkItem(uint Id, string Name, string DisplayName, string Description, int SlotPage, string Category, int Cost, int TotalUses);
    public sealed record Status(bool Loaded, string Source, string Message);

    internal sealed record Snapshot(
        string Path,
        bool FromInstall,
        int ValidRoadBits,
        int MountainValidRoadBits,
        int OpusIslandValidRoadBits,
        uint RoadTargetCount,
        HornItem[] Horns,
        PerkItem[] Perks)
    {
        internal IReadOnlyDictionary<uint, HornItem> HornsById { get; } = Horns.ToDictionary(h => h.Id);
        internal IReadOnlyDictionary<uint, PerkItem> PerksById { get; } = Perks.ToDictionary(p => p.Id);
    }

    internal const int DefaultValidRoadBits = 17_422;
    internal const uint DefaultRoadTargetCount = 488;
    internal const int DefaultMountainValidRoadBits = 2_061;
    internal const int DefaultOpusIslandValidRoadBits = 5_408;

    const string EmbeddedDbResource = "FH6LocalCryptoTool.gamedbRC.stock.fh3-1231512575.sqlite.gz";

    static readonly ConditionalWeakTable<ProfileContainer, Snapshot> Bindings = new();
    static readonly object Gate = new();
    static Snapshot? cached;
    static (string Path, long Stamp) cacheKey;
    static Snapshot? embeddedSnapshot;

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForzaModTool", "Fh3ProfileGameDataPath.txt");

    internal static Snapshot? For(ProfileContainer? container) => container != null && Bindings.TryGetValue(container, out var data) ? data : null;

    internal static Snapshot ForOrEmbedded(ProfileContainer? container)
        => For(container) ?? (embeddedSnapshot ??= ReadEmbeddedSnapshot());

    internal static void Copy(ProfileContainer original, ProfileContainer next)
    {
        if (For(original) is { } snapshot)
        {
            Bindings.Remove(next);
            Bindings.Add(next, snapshot);
        }
    }

    public static Status GetStatus(ProfileContainer? container) => For(container) is { } data
        ? new(true, data.Path,
            $"Installed FH3 game data: {data.Horns.Length} horns · {data.Perks.Length} skill perks.")
        : new(false, "Built-in fallback",
            "Built-in FH3 perk names and road defaults are active. Select your FH3 install folder (or gamedbrc.slt / en.zip) for localized Car Horn names and verified map bit counts.");

    public static Status AttachAutomatically(ProfileContainer container)
    {
        try
        {
            var paths = Candidates().Where(ValidCandidate).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 1) return Attach(container, paths[0]);
            if (paths.Length > 1)
                return new(false, "Built-in fallback", "Multiple FH3 installs found. Choose your FH3 folder or gamedbrc.slt manually.");
            return new(false, "Built-in fallback", GetStatus(container).Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new(false, "Built-in fallback", "FH3 install could not be detected. Choose your FH3 folder or gamedbrc.slt manually.");
        }
    }

    public static Status Attach(ProfileContainer container, string path, bool remember = false)
    {
        try
        {
            string resolved = ResolveSource(Path.GetFullPath(path));
            Snapshot snapshot;
            lock (Gate)
            {
                long stamp = SourceStamp(resolved);
                if (cached != null && cacheKey == (resolved, stamp)) snapshot = cached;
                else
                {
                    snapshot = ReadSnapshot(resolved);
                    cached = snapshot;
                    cacheKey = (resolved, stamp);
                }
            }
            Bindings.Remove(container);
            Bindings.Add(container, snapshot);
            if (remember)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, resolved);
            }
            return GetStatus(container);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException or System.Security.SecurityException or SqliteException)
        {
            return new(false, "Built-in fallback", "FH3 game data not loaded: " + ex.Message);
        }
    }

    static bool ValidCandidate(string path) => File.Exists(path) || Directory.Exists(path);

    static IEnumerable<string> Candidates()
    {
        if (File.Exists(SettingsPath))
        {
            string saved = "";
            try { if (new FileInfo(SettingsPath).Length <= 4096) saved = File.ReadAllText(SettingsPath).Trim(); }
            catch (IOException) { }
            if (!string.IsNullOrEmpty(saved) && ValidCandidate(saved))
            {
                yield return saved;
                yield break;
            }
        }

        foreach (string root in FH6CarEditor.ThumbnailInstallDiscovery.Horizon3GameRoots())
        {
            if (!Directory.Exists(root)) continue;
            string sltMediaPc = Fh3NameCodec.ResolveExistingPath(root, Path.Combine("mediapc", "stripped", "gamedbrc.slt"));
            if (File.Exists(sltMediaPc)) { yield return sltMediaPc; continue; }
            string sltMedia = Fh3NameCodec.ResolveExistingPath(root, Path.Combine("media", "stripped", "gamedbrc.slt"));
            if (File.Exists(sltMedia)) yield return sltMedia;
        }
    }

    static string ResolveSource(string path)
    {
        if (File.Exists(path))
        {
            string decName = Fh3NameCodec.Decode(Path.GetFileName(path));
            if (decName.Equals("gamedbrc.slt", StringComparison.OrdinalIgnoreCase) ||
                decName.Equals("en.zip", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return path;
            throw new InvalidDataException("Select your FH3 game folder, gamedbrc.slt ({lgq6`4^.~b1), or stringtables\\en.zip (q8.,$u).");
        }
        if (!Directory.Exists(path)) throw new InvalidDataException("Selected FH3 game data path does not exist.");

        foreach (string rel in new[]
        {
            Path.Combine("mediapc", "stripped", "gamedbrc.slt"),
            Path.Combine("media", "stripped", "gamedbrc.slt"),
            Path.Combine("stripped", "gamedbrc.slt"),
            "gamedbrc.slt",
            Path.Combine("media", "stripped", "stringtables", "en.zip"),
            Path.Combine("stripped", "stringtables", "en.zip"),
            Path.Combine("stringtables", "en.zip"),
            "en.zip",
        })
        {
            string probe = Fh3NameCodec.ResolveExistingPath(path, rel);
            if (File.Exists(probe)) return probe;
        }

        throw new InvalidDataException("Could not find FH3 gamedbrc.slt or stringtables\\en.zip in that folder.");
    }

    static long SourceStamp(string resolved)
    {
        var fi = new FileInfo(resolved);
        return fi.Length ^ fi.LastWriteTimeUtc.Ticks;
    }

    static string? FindGameRoot(string resolved)
    {
        string? cur = File.Exists(resolved) ? Path.GetDirectoryName(resolved) : resolved;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
        {
            string nav = Fh3NameCodec.ResolveExistingPath(cur, Path.Combine("media", "tracks", "australia", "ribbon_00", "australia_00.nav"));
            string slt = Fh3NameCodec.ResolveExistingPath(cur, Path.Combine("media", "stripped", "gamedbrc.slt"));
            string dlc = Path.Combine(cur, "MicrosoftStore_DLC");
            if (File.Exists(nav) || File.Exists(slt) || Directory.Exists(dlc)) return cur;
            cur = Directory.GetParent(cur)?.FullName;
        }
        return null;
    }

    static Snapshot ReadSnapshot(string source)
    {
        string? gameRoot = FindGameRoot(source);
        string decLeaf = Fh3NameCodec.Decode(Path.GetFileName(source));
        string? dbPath = !decLeaf.Equals("en.zip", StringComparison.OrdinalIgnoreCase) && File.Exists(source)
            ? source
            : FindDbPath(source, gameRoot);

        Dictionary<uint, string> hornStrings = new();
        Dictionary<uint, string> hornCategoryStrings = new();
        Dictionary<uint, string> perkStrings = new();
        LoadEnglishStrings(source, gameRoot, hornStrings, hornCategoryStrings, perkStrings);

        byte[] dbBytes = dbPath != null ? ReadPlainSqliteDb(dbPath) : ExtractEmbeddedDbBytes();
        var (horns, perks) = ReadFromSqliteBytes(dbBytes, hornStrings, hornCategoryStrings, perkStrings);

        int mainBits = ReadNavBitCount(gameRoot, Path.Combine("media", "tracks", "australia", "ribbon_00", "australia_00.nav"), DefaultValidRoadBits);
        int mtnBits = ReadNavBitCount(gameRoot, Path.Combine("MicrosoftStore_DLC", "Blizzard Mountain", "Media", "Tracks", "mountain", "Ribbon_00", "mountain_00.nav"), DefaultMountainValidRoadBits);
        int islandBits = ReadNavBitCount(gameRoot, Path.Combine("MicrosoftStore_DLC", "Hot Wheels", "Media", "Tracks", "island", "Ribbon_00", "island_00.nav"), DefaultOpusIslandValidRoadBits);

        return new Snapshot(source, true, mainBits, mtnBits, islandBits, DefaultRoadTargetCount, horns, perks);
    }

    static Snapshot ReadEmbeddedSnapshot()
    {
        byte[] dbBytes = ExtractEmbeddedDbBytes();
        var empty = new Dictionary<uint, string>();
        var (horns, perks) = ReadFromSqliteBytes(dbBytes, empty, empty, empty);
        return new Snapshot("Built-in FH3 catalog", false, DefaultValidRoadBits, DefaultMountainValidRoadBits, DefaultOpusIslandValidRoadBits, DefaultRoadTargetCount, horns, perks);
    }

    static byte[] ExtractEmbeddedDbBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedDbResource)
            ?? throw new InvalidDataException("Missing embedded FH3 catalog resource.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    static string? FindDbPath(string source, string? gameRoot)
    {
        if (gameRoot != null)
        {
            foreach (string rel in new[]
            {
                Path.Combine("mediapc", "stripped", "gamedbrc.slt"),
                Path.Combine("media", "stripped", "gamedbrc.slt"),
            })
            {
                string probe = Fh3NameCodec.ResolveExistingPath(gameRoot, rel);
                if (File.Exists(probe)) return probe;
            }
        }
        return null;
    }

    static byte[] ReadPlainSqliteDb(string dbPath)
    {
        byte[] raw = File.ReadAllBytes(dbPath);
        if (raw.Length >= 16 && raw.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            return raw;
        return LegacyGameDb.Decrypt(raw, ForzaGame.Horizon3);
    }

    static (HornItem[] Horns, PerkItem[] Perks) ReadFromSqliteBytes(
        byte[] dbBytes,
        Dictionary<uint, string> hornStrings,
        Dictionary<uint, string> hornCategoryStrings,
        Dictionary<uint, string> perkStrings)
    {
        string tmp = Path.Combine(TempWorkspace.Root, $"fh3gd_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
        try
        {
            File.WriteAllBytes(tmp, dbBytes);
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();

            var categories = new Dictionary<int, string>();
            using (var cc = conn.CreateCommand())
            {
                cc.CommandText = "SELECT Id, DisplayName FROM CarHornCategories";
                using var r = cc.ExecuteReader();
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    string rawDisp = r.IsDBNull(1) ? "" : r.GetString(1);
                    string resolved = ResolveRef(hornCategoryStrings, "CarHornCategories", rawDisp, FallbackHornCategory(id));
                    categories[id] = resolved;
                }
            }

            var horns = new List<HornItem>();
            using (var ch = conn.CreateCommand())
            {
                ch.CommandText = "SELECT Id, CategoryId, DisplayName, UnlockByPerk FROM CarHorns ORDER BY Id";
                using var r = ch.ExecuteReader();
                while (r.Read())
                {
                    uint id = checked((uint)r.GetInt32(0));
                    int cat = r.GetInt32(1);
                    string rawDisp = r.IsDBNull(2) ? "" : r.GetString(2);
                    int unlockByPerk = r.GetInt32(3);
                    string catName = categories.GetValueOrDefault(cat, FallbackHornCategory(cat));
                    string name = ResolveRef(hornStrings, "CarHorns", rawDisp, $"Horn #{id}");
                    horns.Add(new HornItem(id, name, cat, catName, unlockByPerk));
                }
            }

            var perks = new List<PerkItem>();
            using (var cp = conn.CreateCommand())
            {
                cp.CommandText = "SELECT Id, Name, DisplayName, Description, SlotPage, Cost, TotalUses FROM Perks ORDER BY SlotPage, SlotIndex, Id";
                using var r = cp.ExecuteReader();
                while (r.Read())
                {
                    uint id = checked((uint)r.GetInt32(0));
                    string codeName = r.IsDBNull(1) ? $"Perk #{id}" : r.GetString(1);
                    string rawDisp = r.IsDBNull(2) ? "" : r.GetString(2);
                    string rawDesc = r.IsDBNull(3) ? "" : r.GetString(3);
                    int slotPage = r.GetInt32(4);
                    int cost = r.GetInt32(5);
                    int totalUses = r.GetInt32(6);
                    string display = ResolveRef(perkStrings, "Perks", rawDisp, codeName);
                    string desc = ResolveRef(perkStrings, "Perks", rawDesc, "");
                    perks.Add(new PerkItem(id, codeName, display, desc, slotPage, PerkCategory(slotPage), cost, totalUses));
                }
            }

            return (horns.OrderBy(h => h.Id).ToArray(), perks.ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static string ResolveRef(Dictionary<uint, string> values, string tableName, string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!raw.StartsWith("_&", StringComparison.Ordinal)) return raw.Trim();
        if (!ulong.TryParse(raw.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out ulong key))
            return fallback;
        uint tableHash = (uint)(key >> 32);
        if (tableHash != UpgradeStringTable.Hash(tableName) &&
            tableHash != UpgradeStringTable.Hash(tableName.ToLowerInvariant()))
            return fallback;
        return values.TryGetValue((uint)key, out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;
    }

    static void LoadEnglishStrings(
        string source,
        string? gameRoot,
        Dictionary<uint, string> hornStrings,
        Dictionary<uint, string> hornCategoryStrings,
        Dictionary<uint, string> perkStrings)
    {
        var zipCandidates = new List<string>();
        if (File.Exists(source) && Fh3NameCodec.Decode(Path.GetFileName(source)).Equals("en.zip", StringComparison.OrdinalIgnoreCase))
            zipCandidates.Add(source);

        if (File.Exists(source))
        {
            string? dir = Path.GetDirectoryName(source);
            if (dir != null)
            {
                zipCandidates.Add(Fh3NameCodec.ResolveExistingPath(dir, Path.Combine("stringtables", "en.zip")));
                zipCandidates.Add(Fh3NameCodec.ResolveExistingPath(dir, "en.zip"));
            }
        }
        if (gameRoot != null)
        {
            zipCandidates.Add(Fh3NameCodec.ResolveExistingPath(gameRoot, Path.Combine("mediapc", "stripped", "stringtables", "en.zip")));
            zipCandidates.Add(Fh3NameCodec.ResolveExistingPath(gameRoot, Path.Combine("media", "stripped", "stringtables", "en.zip")));
            zipCandidates.Add(Fh3NameCodec.ResolveExistingPath(gameRoot, Path.Combine("stringtables", "en.zip")));
        }

        string? enZip = zipCandidates.FirstOrDefault(File.Exists);
        if (enZip == null) return;

        using var archive = ZipFile.OpenRead(enZip);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length is < 16 or > 20_000_000) continue;
            string leaf = entry.FullName.Replace('\\', '/').Split('/').Last();
            string dec = Fh3NameCodec.Decode(leaf);
            Dictionary<uint, string>? target = null;
            string? tableName = null;
            if (dec.Equals("carhorns.str", StringComparison.OrdinalIgnoreCase)) { target = hornStrings; tableName = "CarHorns"; }
            else if (dec.Equals("carhorncategories.str", StringComparison.OrdinalIgnoreCase)) { target = hornCategoryStrings; tableName = "CarHornCategories"; }
            else if (dec.Equals("perks.str", StringComparison.OrdinalIgnoreCase)) { target = perkStrings; tableName = "Perks"; }
            if (target == null || tableName == null) continue;

            byte[] bytes = new byte[checked((int)entry.Length)];
            using var s = entry.Open();
            s.ReadExactly(bytes);
            var st = new UpgradeStringTable(bytes, tableName);
            foreach (var pair in st.Values)
                target[pair.Key] = pair.Value;
        }
    }

    static int ReadNavBitCount(string? gameRoot, string relativePath, int fallback)
    {
        if (gameRoot == null) return fallback;
        string nav = Fh3NameCodec.ResolveExistingPath(gameRoot, relativePath);
        if (!File.Exists(nav)) return fallback;
        try
        {
            using var fs = File.OpenRead(nav);
            if (fs.Length < 0x60) return fallback;
            Span<byte> hdr = stackalloc byte[0x60];
            fs.ReadExactly(hdr);
            if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x4E415657u) return fallback;
            int bits = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(0x58, 4));
            return bits is >= 500 and <= 32768 ? bits : fallback;
        }
        catch (IOException) { return fallback; }
    }

    static string FallbackHornCategory(int cat) => cat switch
    {
        0 => "Standard Horns",
        1 => "Musical Horns",
        2 => "Sound Effects",
        _ => $"Category {cat}",
    };

    static string PerkCategory(int slotPage) => slotPage switch
    {
        0 => "Festival Boss",
        1 => "Skills",
        2 => "Instant Rewards",
        _ => $"Page {slotPage + 1}",
    };
}
#endif
