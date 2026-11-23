#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FH6LocalCryptoTool;

/// <summary>Read-only installed FH4 TIDE tables and English strings, captured per loaded profile.</summary>
public static class Fh4ProfileGameData
{
    public sealed record ClothingItem(ushort Id, string Name, string Slot, string Category, string Rarity);
    public sealed record HornItem(uint Id, string Name, int Category, string CategoryName);
    public sealed record EmoteItem(uint Id, string Name, string Rarity);
    public sealed record PhraseItem(uint Id, string Name);
    public sealed record FlairItem(uint Id, string Name, string Description, String Category);
    public sealed record BrickObjectiveDef(uint Id, long TargetThreshold, string StorageType);
    public sealed record BrickChallengeItem(uint Id, int UnlockTier, int SortIndex, string Category, string Difficulty, string Name, string Description, int Bricks, uint[] ObjectiveIds, BrickObjectiveDef[] Objectives);

    internal sealed record Snapshot(
        string Path,
        string Fingerprint,
        int ValidRoadBits,
        int Ex1ValidRoadBits,
        int Ex2ValidRoadBits,
        uint RoadTargetCount,
        ClothingItem[] Clothing,
        HornItem[] Horns,
        EmoteItem[] Emotes,
        PhraseItem[] Phrases,
        FlairItem[] Flairs,
        BrickChallengeItem[] BrickChallenges)
    {
        internal IReadOnlyDictionary<ushort, ClothingItem> ClothingById { get; } = Clothing.ToDictionary(c => c.Id);
        internal IReadOnlyDictionary<uint, HornItem> HornsById { get; } = Horns.ToDictionary(h => h.Id);
        internal IReadOnlyDictionary<uint, EmoteItem> EmotesById { get; } = Emotes.ToDictionary(e => e.Id);
        internal IReadOnlyDictionary<uint, PhraseItem> PhrasesById { get; } = Phrases.ToDictionary(p => p.Id);
        internal IReadOnlyDictionary<uint, FlairItem> FlairsById { get; } = Flairs.ToDictionary(f => f.Id);
        internal IReadOnlyDictionary<uint, BrickChallengeItem> BrickChallengesById { get; } = BrickChallenges.ToDictionary(b => b.Id);
        internal IReadOnlyDictionary<uint, BrickObjectiveDef> BrickObjectivesById { get; } = BrickChallenges.SelectMany(b => b.Objectives).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public sealed record Status(bool Loaded, string Source, string Message);

    internal const int DefaultValidRoadBits = 18_715;
    internal const uint DefaultRoadTargetCount = 531;
    internal const int DefaultEx1ValidRoadBits = 2_686;
    internal const int DefaultEx2ValidRoadBits = 2_907;

    static readonly string[] WantedTables =
    {
        "CharacterCustomisationItemData.xml",
        "CarHorn.xml",
        "CarHornCategory.xml",
        "EmoteData.xml",
        "QuickChatData.xml",
        "FlairReward.xml",
        "BrickChallenges.xml",
        "ChallengeObjectives.xml",
        "ChallengeStatsBucket.xml",
        "Reward.xml",
        "CurrencyReward.xml",
    };

    static readonly HashSet<string> WardrobeSlots = new(StringComparer.Ordinal)
    {
        "UpperBodyLayer1", "UpperBodyLayer2", "LowerBody", "Feet", "Hat",
        "Outfit", "Hands", "Eye", "Nose", "LeftWrist",
    };

    static readonly ConditionalWeakTable<ProfileContainer, Snapshot> Bindings = new();
    static readonly object Gate = new();
    static Snapshot? cached;
    static (string Path, long Stamp) cacheKey;

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForzaModTool", "Fh4ProfileGameDataPath.txt");

    internal static Snapshot? For(ProfileContainer? container) => container != null && Bindings.TryGetValue(container, out var data) ? data : null;

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
            $"Installed FH4 game data: {data.Clothing.Length} clothes · {data.Horns.Length} horns · {data.Emotes.Length} emotes · {data.Phrases.Length} quick chats · {data.Flairs.Length} flairs · {data.BrickChallenges.Length} LEGO challenges.")
        : new(false, "Built-in fallback",
            "Select FH4 TIDETables.zip (or mediapc\\stripped\\TIDETables) for unlock names/editing. Road discovery & everyday values remain active.");

    public static Status AttachAutomatically(ProfileContainer container)
    {
        try
        {
            var paths = Candidates().Where(ValidCandidate).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 1) return Attach(container, paths[0]);
            if (paths.Length > 1)
                return new(false, "Built-in fallback", "Multiple FH4 installs found. Choose TIDETables.zip manually.");
            return new(false, "Built-in fallback", GetStatus(container).Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new(false, "Built-in fallback", "FH4 install could not be detected. Choose TIDETables.zip manually.");
        }
    }

    public static Status Attach(ProfileContainer container, string path, bool remember = false)
    {
        try
        {
            string resolved = ResolveTideSource(Path.GetFullPath(path));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Xml.XmlException or NotSupportedException or System.Security.SecurityException)
        {
            return new(false, "Preserved current definitions", "FH4 game data not loaded: " + ex.Message);
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

        foreach (string root in FH6CarEditor.ThumbnailInstallDiscovery.Horizon4GameRoots())
        {
            string zip = Path.Combine(root, "media", "stripped", "TIDETables.zip");
            string dir = Path.Combine(root, "mediapc", "stripped", "TIDETables");
            if (File.Exists(zip)) yield return zip;
            else if (Directory.Exists(dir)) yield return dir;
        }
    }

    static string ResolveTideSource(string path)
    {
        if (File.Exists(path))
        {
            if (!Path.GetFileName(path).Equals("TIDETables.zip", StringComparison.OrdinalIgnoreCase) || !LegacyZipArchive.CanHandle(path))
                throw new InvalidDataException("Select FH4 TIDETables.zip or the TIDETables folder from your FH4 install.");
            return path;
        }
        if (!Directory.Exists(path)) throw new InvalidDataException("Selected FH4 game data path does not exist.");
        if (Directory.EnumerateFiles(path, "*.xml", SearchOption.TopDirectoryOnly).Any()) return path;

        foreach (string probe in new[]
        {
            Path.Combine(path, "TIDETables.zip"),
            Path.Combine(path, "TIDETables"),
            Path.Combine(path, "stripped", "TIDETables.zip"),
            Path.Combine(path, "media", "stripped", "TIDETables.zip"),
            Path.Combine(path, "mediapc", "stripped", "TIDETables"),
        })
        {
            if (File.Exists(probe) && LegacyZipArchive.CanHandle(probe)) return probe;
            if (Directory.Exists(probe) && Directory.EnumerateFiles(probe, "*.xml", SearchOption.TopDirectoryOnly).Any()) return probe;
        }
        throw new InvalidDataException("Could not find FH4 TIDETables.zip or TIDE XML files in that folder.");
    }

    static long SourceStamp(string resolved)
    {
        if (File.Exists(resolved))
        {
            var info = new FileInfo(resolved);
            return info.Length ^ info.LastWriteTimeUtc.Ticks;
        }
        return Directory.EnumerateFiles(resolved, "*.xml").Select(f => new FileInfo(f)).Aggregate(0L, (h, fi) => h ^ fi.Length ^ fi.LastWriteTimeUtc.Ticks);
    }

    static string? FindGameRoot(string tideSource)
    {
        DirectoryInfo? dir = File.Exists(tideSource) ? new FileInfo(tideSource).Directory : new DirectoryInfo(tideSource);
        for (int depth = 0; dir != null && depth < 6; depth++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "media", "tracks")) ||
                Directory.Exists(Path.Combine(dir.FullName, "media", "stripped")))
                return dir.FullName;
        }
        return null;
    }

    static Snapshot ReadSnapshot(string tideSource)
    {
        var wantedSet = new HashSet<string>(WantedTables, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, byte[]> rawTables;
        string fingerprint;
        if (File.Exists(tideSource))
        {
            if (!LegacyZipArchive.CanHandle(tideSource))
                throw new InvalidDataException("TIDETables.zip failed FH4 authentication.");
            using (var fs = File.OpenRead(tideSource)) fingerprint = Convert.ToHexString(SHA256.HashData(fs));
            rawTables = LegacyZipArchive.ReadEntries(tideSource, wantedSet);
        }
        else
        {
            rawTables = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string name in WantedTables)
            {
                string file = Path.Combine(tideSource, name);
                if (!File.Exists(file)) continue;
                byte[] bytes = File.ReadAllBytes(file);
                rawTables[name] = bytes;
                inc.AppendData(bytes);
            }
            fingerprint = Convert.ToHexString(inc.GetHashAndReset());
        }

        XElement LoadXml(string fileName)
        {
            if (!rawTables.TryGetValue(fileName, out byte[]? bytes))
                throw new InvalidDataException("Missing required FH4 TIDE table: " + fileName);
            return XDocument.Parse(Bxml.Parse(bytes).ToXml()).Root ?? throw new InvalidDataException("Empty BXML root: " + fileName);
        }

        string? gameRoot = FindGameRoot(tideSource);
        var strings = LoadEnglishStrings(tideSource, gameRoot);
        int validRoadBits = ReadNavBitCount(gameRoot, Path.Combine("media", "tracks", "uk", "ribbon_00", "UK_00.nav"), DefaultValidRoadBits);
        int ex1RoadBits = ReadNavBitCount(gameRoot, Path.Combine("DLC", "MicrosoftExpansion1", "Media", "Tracks", "Savage", "Ribbon_00", "Savage_00.nav"), DefaultEx1ValidRoadBits);
        int ex2RoadBits = ReadNavBitCount(gameRoot, Path.Combine("DLC", "MicrosoftExpansion2", "Media", "Tracks", "Red", "Ribbon_00", "Red_00.nav"), DefaultEx2ValidRoadBits);

        // Clothing (player wardrobe slots, excluding Wristband and IsRemoveItem)
        var clothing = new List<ClothingItem>();
        foreach (var row in LoadXml("CharacterCustomisationItemData.xml").Elements("row"))
        {
            if (StrAttr(row, "IsRemoveItem").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
            string slot = StrAttr(row, "SlotID");
            if (!WardrobeSlots.Contains(slot)) continue;
            uint rawId = UIntAttr(row, "id");
            if (rawId > ushort.MaxValue) continue;
            ushort id = (ushort)rawId;
            string itemId = StrAttr(row, "ItemID");
            string dispKey = StrAttr(row, "DisplayName");
            if (string.IsNullOrEmpty(dispKey))
                dispKey = "CharacterCustomisationItemData.IDS_DisplayName_" + id.ToString(CultureInfo.InvariantCulture);
            string name = ResolveString(strings, dispKey, Humanize(itemId));
            clothing.Add(new(id, name, slot, ClothingCategory(slot), StrAttr(row, "Rarity")));
        }

        // Car Horns
        var hornCategories = LoadXml("CarHornCategory.xml").Elements("row").ToDictionary(
            r => (int)UIntAttr(r, "id"),
            r => ResolveString(strings, StrAttr(r, "DisplayName"), "Category " + StrAttr(r, "id")));
        var horns = new List<HornItem>();
        foreach (var row in LoadXml("CarHorn.xml").Elements("row"))
        {
            uint id = UIntAttr(row, "id");
            int cat = IntAttr(row, "Category");
            string catName = hornCategories.GetValueOrDefault(cat, cat switch { 0 => "Standard", 1 => "Musical", _ => "Sound effects" });
            string name = ResolveString(strings, StrAttr(row, "DisplayName"), "Horn #" + id.ToString(CultureInfo.InvariantCulture));
            horns.Add(new(id, name, cat, catName));
        }

        // Emotes
        var emotes = new List<EmoteItem>();
        foreach (var row in LoadXml("EmoteData.xml").Elements("row"))
        {
            uint id = UIntAttr(row, "id");
            string anim = Path.GetFileNameWithoutExtension(StrAttr(row, "Animation"));
            string name = ResolveString(strings, StrAttr(row, "Name"), Humanize(anim));
            emotes.Add(new(id, name, StrAttr(row, "Rarity")));
        }

        // QuickChat phrases
        var phrases = new List<PhraseItem>();
        foreach (var row in LoadXml("QuickChatData.xml").Elements("row"))
        {
            if (!StrAttr(row, "RequiresUnlock").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
            uint id = UIntAttr(row, "id");
            string name = ResolveString(strings, StrAttr(row, "ChatMessage"), "Phrase #" + id.ToString(CultureInfo.InvariantCulture));
            phrases.Add(new(id, name));
        }

        // Star Card Flairs
        var flairs = new List<FlairItem>();
        foreach (var row in LoadXml("FlairReward.xml").Elements("row"))
        {
            uint id = UIntAttr(row, "id");
            string cat = StrAttr(row, "StarCardFlairType");
            string name = ResolveString(strings, StrAttr(row, "Name"), string.IsNullOrEmpty(cat) ? "Flair #" + id.ToString(CultureInfo.InvariantCulture) : Humanize(cat));
            string desc = ResolveString(strings, StrAttr(row, "Description"), "");
            flairs.Add(new(id, name, desc, Humanize(cat)));
        }

        // LEGO Speed Champions Brick Challenges
        var brickObjectiveMap = LoadXml("ChallengeObjectives.xml").Elements("row").ToDictionary(r => UIntAttr(r, "id"));
        var statsBucketMap = LoadXml("ChallengeStatsBucket.xml").Elements("row").ToDictionary(r => UIntAttr(r, "id"));
        var rewardMap = LoadXml("Reward.xml").Elements("row").ToDictionary(r => UIntAttr(r, "id"));
        var currencyRewardMap = LoadXml("CurrencyReward.xml").Elements("row").ToDictionary(r => UIntAttr(r, "id"));
        var brickChallenges = new List<BrickChallengeItem>();
        foreach (var row in LoadXml("BrickChallenges.xml").Elements("row"))
        {
            uint id = UIntAttr(row, "id");
            int tier = IntAttr(row, "UnlockTier");
            int sortIdx = IntAttr(row, "SortIndex");
            string category = Humanize(StrAttr(row, "Type"));
            string difficulty = StrAttr(row, "Difficulty");
            string name = CleanHighlight(ResolveString(strings, StrAttr(row, "Name"), "Brick Challenge #" + id.ToString(CultureInfo.InvariantCulture)));
            string desc = CleanHighlight(ResolveString(strings, StrAttr(row, "Description"), ""));

            int bricks = 0;
            foreach (uint rid in SplitUInts(StrAttr(row, "Rewards")))
            {
                if (!rewardMap.TryGetValue(rid, out var r)) continue;
                if (!StrAttr(r, "RewardType").Equals("Currency", StringComparison.Ordinal)) continue;
                if (!uint.TryParse(StrAttr(r, "RewardID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint curId)) continue;
                if (currencyRewardMap.TryGetValue(curId, out var cr) && StrAttr(cr, "CurrencyType").Equals("Bricks", StringComparison.Ordinal))
                    bricks += IntAttr(cr, "Amount");
            }

            uint[] objIds = SplitUInts(StrAttr(row, "Objectives"));
            var objDefs = new List<BrickObjectiveDef>(objIds.Length);
            foreach (uint oid in objIds)
            {
                long threshold = 1;
                string storage = "Int";
                if (brickObjectiveMap.TryGetValue(oid, out var o))
                {
                    if (long.TryParse(StrAttr(o, "ManualTrackingValue"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long manualVal) && manualVal > threshold)
                        threshold = manualVal;
                    foreach (uint bid in SplitUInts(StrAttr(o, "StatsBucket")))
                    {
                        if (!statsBucketMap.TryGetValue(bid, out var b)) continue;
                        if (long.TryParse(StrAttr(b, "ThresholdValue"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bv) && bv > threshold)
                            threshold = bv;
                        string st = StrAttr(b, "StorageType");
                        if (!string.IsNullOrEmpty(st)) storage = st;
                    }
                }
                objDefs.Add(new(oid, threshold, storage));
            }
            brickChallenges.Add(new(id, tier, sortIdx, category, difficulty, name, desc, bricks, objIds, objDefs.ToArray()));
        }

        return new(
            tideSource,
            fingerprint,
            validRoadBits,
            ex1RoadBits,
            ex2RoadBits,
            DefaultRoadTargetCount,
            clothing.OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToArray(),
            horns.OrderBy(h => h.Id).ToArray(),
            emotes.OrderBy(e => e.Id).ToArray(),
            phrases.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray(),
            flairs.OrderBy(f => f.Id).ToArray(),
            brickChallenges.OrderBy(b => b.UnlockTier).ThenBy(b => b.SortIndex).ThenBy(b => b.Id).ToArray());
    }

    static Dictionary<string, string> LoadEnglishStrings(string tideSource, string? gameRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? tideDir = Path.GetDirectoryName(tideSource);
        var zipCandidates = new List<string>();
        var dirCandidates = new List<string>();
        if (tideDir != null)
        {
            zipCandidates.Add(Path.Combine(tideDir, "stringtables", "EN.zip"));
            dirCandidates.Add(Path.Combine(tideDir, "stringtables", "EN"));
        }
        if (gameRoot != null)
        {
            zipCandidates.Add(Path.Combine(gameRoot, "media", "stripped", "stringtables", "EN.zip"));
            zipCandidates.Add(Path.Combine(gameRoot, "stringtables", "EN.zip"));
            dirCandidates.Add(Path.Combine(gameRoot, "media", "stripped", "stringtables", "EN"));
            dirCandidates.Add(Path.Combine(gameRoot, "stringtables", "EN"));
        }

        string? enZip = zipCandidates.FirstOrDefault(File.Exists);
        if (enZip != null)
        {
            using var archive = ZipFile.OpenRead(enZip);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length is < 140 or > 20_000_000 || !entry.Name.EndsWith(".str", StringComparison.OrdinalIgnoreCase))
                    continue;
                byte[] bytes = new byte[checked((int)entry.Length)];
                using var stream = entry.Open();
                stream.ReadExactly(bytes);
                ReadStrTableInto(bytes, map);
            }
            return map;
        }

        string? enDir = dirCandidates.FirstOrDefault(Directory.Exists);
        if (enDir != null)
        {
            foreach (string strPath in Directory.EnumerateFiles(enDir, "*.str", SearchOption.AllDirectories))
                ReadStrTableInto(File.ReadAllBytes(strPath), map);
        }
        return map;
    }

    static void ReadStrTableInto(byte[] bytes, Dictionary<string, string> target)
    {
        if (bytes.Length is < 140 or > 20_000_000) return;
        int end = Array.IndexOf(bytes, (byte)0, 2, 126);
        if (end < 3) return;
        string tableName = Encoding.ASCII.GetString(bytes, 2, end - 2);
        var st = new UpgradeStringTable(bytes, tableName);
        foreach (var pair in st.Entries)
            target[tableName + "." + pair.Key] = pair.Value;
    }

    static int ReadNavBitCount(string? gameRoot, string relativePath, int fallback)
    {
        if (gameRoot == null) return fallback;
        string nav = Path.Combine(gameRoot, relativePath);
        if (!File.Exists(nav)) return fallback;
        try
        {
            using var fs = File.OpenRead(nav);
            if (fs.Length < 0x60) return fallback;
            Span<byte> hdr = stackalloc byte[0x60];
            fs.ReadExactly(hdr);
            // "WVNA" = 0x4E415657 LE
            if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x4E415657u) return fallback;
            int bits = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(0x58, 4));
            return bits is >= 500 and <= 32768 ? bits : fallback;
        }
        catch (IOException) { return fallback; }
    }

    internal static string ClothingCategory(string slot) => slot switch
    {
        "UpperBodyLayer1" => "Tops",
        "UpperBodyLayer2" => "Jackets",
        "LowerBody" => "Legs",
        "Feet" => "Feet",
        "Hat" => "Hats",
        "Outfit" => "Outfits",
        "Hands" => "Gloves",
        "Eye" => "Glasses",
        "Nose" => "Face / Nose",
        "LeftWrist" => "Watches",
        _ => slot,
    };

    static string ResolveString(Dictionary<string, string> strings, string key, string fallback)
        => !string.IsNullOrEmpty(key) && strings.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val) ? val : fallback;

    static string Humanize(string text)
        => string.IsNullOrWhiteSpace(text) ? "" : Regex.Replace(
            text.Replace("Horn_SFX_", "").Replace("Horn_Musical_", "").Replace("Horn_Standard_", "").Replace('_', ' '),
            "(?<=[a-z])(?=[A-Z])", " ").Trim();

    static string CleanHighlight(string text)
        => string.IsNullOrWhiteSpace(text) ? "" : Regex.Replace(text, @"\[HIGHLIGHT:([^\]]*)\]", "$1").Trim();

    static uint[] SplitUInts(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<uint>();
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(s => uint.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();
    }

    static string StrAttr(XElement el, string name) => (string?)el.Attribute(name) ?? "";
    static uint UIntAttr(XElement el, string name) => uint.Parse(StrAttr(el, name), CultureInfo.InvariantCulture);
    static int IntAttr(XElement el, string name) => int.TryParse(StrAttr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
#endif
