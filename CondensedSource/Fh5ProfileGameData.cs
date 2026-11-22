#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

/// <summary>Read-only installed FH5 TIDE tables and English strings, captured per loaded profile.</summary>
public static class Fh5ProfileGameData
{
    public sealed record ClothingItem(ushort Id, string Name, string Slot, string Category, string Rarity);
    public sealed record HornItem(uint Id, string Name, int Category, string CategoryName);
    public sealed record EmoteItem(uint Id, string Name, string Rarity);
    public sealed record PhraseItem(uint Id, string Name, string Category);
    public sealed record ChallengeObjectiveDef(uint Id, string Description, string ThresholdValue, string ThresholdComparison, string SaveType, string Type);
    public sealed record BadgeItem(uint Id, uint BadgeDefId, uint ChallengeDataId, string Name, string Description, uint[] ObjectiveIds, ChallengeObjectiveDef[] Objectives);
    public sealed record AccoladeCollectionDef(uint Id, string Name, string Description, uint SortOrder, uint[] ChallengeIds);
    public sealed record AccoladeItem(uint Id, uint ChallengeDataId, uint CollectionId, string CollectionName, string SubcategoryName, string Category, string Name, string Description, string HowToUnlock, int Points, uint[] ObjectiveIds, ChallengeObjectiveDef[] Objectives);

    internal sealed record Snapshot(
        string Path,
        string Fingerprint,
        uint[] OnlineThresholds,
        int ValidRoadBits,
        uint RoadTargetCount,
        ClothingItem[] Clothing,
        HornItem[] Horns,
        EmoteItem[] Emotes,
        PhraseItem[] Phrases,
        BadgeItem[] Badges,
        AccoladeCollectionDef[] Collections,
        AccoladeItem[] Accolades)
    {
        internal IReadOnlyDictionary<uint, ChallengeObjectiveDef> ObjectivesById { get; init; } = new Dictionary<uint, ChallengeObjectiveDef>();
        internal IReadOnlyDictionary<uint, BadgeItem> BadgesById { get; } = Badges.ToDictionary(b => b.Id);
        internal IReadOnlyDictionary<uint, AccoladeItem> AccoladesById { get; } = Accolades.ToDictionary(a => a.Id);
        internal IReadOnlyDictionary<uint, AccoladeCollectionDef> CollectionsById { get; } = Collections.ToDictionary(c => c.Id);
        internal IReadOnlyDictionary<ushort, ClothingItem> ClothingById { get; } = Clothing.ToDictionary(c => c.Id);
        internal IReadOnlyDictionary<uint, HornItem> HornsById { get; } = Horns.ToDictionary(h => h.Id);
        internal IReadOnlyDictionary<uint, EmoteItem> EmotesById { get; } = Emotes.ToDictionary(e => e.Id);
        internal IReadOnlyDictionary<uint, PhraseItem> PhrasesById { get; } = Phrases.ToDictionary(p => p.Id);
    }

    public sealed record Status(bool Loaded, string Source, string Message);

    // Exact 17-segment RLE table of FH5 HorizonOpenXP.xml (1,000 ranks: Rank 1 = 0 .. Rank 1000 = 10,000,000).
    internal static readonly uint[] OnlineFallbackThresholds = BuildOnlineFallbackThresholds();
    internal const int DefaultValidRoadBits = 24_498;
    internal const uint DefaultRoadTargetCount = 578;

    static readonly string[] WantedTables =
    {
        "PlayerXPLevels.xml",
        "HorizonOpenXP.xml",
        "CharacterCustomisationItemData.xml",
        "CarHorn.xml",
        "CarHornCategory.xml",
        "EmoteData.xml",
        "QuickChatData.xml",
        "QuickChatCategoryData.xml",
        "BadgeDefinition.xml",
        "BadgeChallengeData.xml",
        "CareerChallengeData.xml",
        "CareerChallengeCollectionData.xml",
        "CareerChallengeSubcategory.xml",
        "ChallengeData.xml",
        "ChallengeObjectives.xml",
        "ChallengeStatsBucket.xml",
        "Reward.xml",
        "CurrencyReward.xml",
    };

    static readonly HashSet<string> WardrobeSlots = new(StringComparer.Ordinal)
    {
        "UpperBodyLayer1", "UpperBodyLayer2", "LowerBody", "Feet", "Hat",
        "Outfit", "Socks", "Dress", "Hands", "Face", "LeftWrist", "Eye",
    };

    static readonly ConditionalWeakTable<ProfileContainer, Snapshot> Bindings = new();
    static readonly AsyncLocal<Snapshot?> Ambient = new();
    static readonly object Gate = new();
    static Snapshot? cached;
    static (string Path, long Stamp) cacheKey;

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForzaModTool", "Fh5ProfileGameDataPath.txt");

    internal static Snapshot? Current => Ambient.Value;
    internal static Snapshot? For(ProfileContainer? container) => container != null && Bindings.TryGetValue(container, out var data) ? data : null;
    internal static void Copy(ProfileContainer original, ProfileContainer next)
    {
        if (For(original) is { } snapshot)
        {
            Bindings.Remove(next);
            Bindings.Add(next, snapshot);
        }
    }

    internal static IDisposable Use(ProfileContainer? container)
    {
        var previous = Ambient.Value;
        Ambient.Value = For(container) ?? previous;
        return new Scope(() => Ambient.Value = previous);
    }

    sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }

    public static Status GetStatus(ProfileContainer? container) => For(container) is { } data
        ? new(true, data.Path,
            $"Installed FH5 game data: {data.Clothing.Length} clothes · {data.Horns.Length} horns · {data.Emotes.Length} emotes · {data.Phrases.Length} phrases · {data.Badges.Length} badges · {data.Accolades.Length:n0} Accolades.")
        : new(false, "Built-in fallback",
            "Select FH5 TIDETables.zip (or mediapc\\stripped\\TIDETables) for unlock & Accolade names/editing. Built-in XP tables remain active.");

    public static Status AttachAutomatically(ProfileContainer container)
    {
        try
        {
            var paths = Candidates().Where(ValidCandidate).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 1) return Attach(container, paths[0]);
            if (paths.Length > 1)
                return new(false, "Built-in fallback", "Multiple FH5 installs found. Choose TIDETables.zip manually.");
            return new(false, "Built-in fallback", GetStatus(container).Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new(false, "Built-in fallback", "FH5 install could not be detected. Choose TIDETables.zip manually.");
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
            return new(false, "Preserved current definitions", "FH5 game data not loaded: " + ex.Message);
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

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            libraries.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames", "Forza Horizon 5", "Content"));
        }
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            if ((Registry.GetValue(key, "SteamPath", null) ?? Registry.GetValue(key, "InstallPath", null)) is string steam)
            {
                libraries.Add(steam);
                string config = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(config) && new FileInfo(config).Length < 1_000_000)
                {
                    foreach (Match match in Regex.Matches(File.ReadAllText(config), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
        }
        foreach (string library in libraries)
            roots.Add(Path.Combine(library, "steamapps", "common", "ForzaHorizon5"));

        foreach (string root in roots)
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
            if (!Path.GetFileName(path).Equals("TIDETables.zip", StringComparison.OrdinalIgnoreCase) || !Fh5ZipArchive.CanHandle(path))
                throw new InvalidDataException("Select TIDETables.zip or the TIDETables folder from your FH5 install.");
            return path;
        }
        if (!Directory.Exists(path)) throw new InvalidDataException("Selected FH5 game data path does not exist.");
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
            if (File.Exists(probe) && Fh5ZipArchive.CanHandle(probe)) return probe;
            if (Directory.Exists(probe) && Directory.EnumerateFiles(probe, "*.xml", SearchOption.TopDirectoryOnly).Any()) return probe;
        }
        throw new InvalidDataException("Could not find TIDETables.zip or TIDE XML files in that folder.");
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
            if (Directory.Exists(Path.Combine(dir.FullName, "media", "Tracks")) ||
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
            if (!Fh5ZipArchive.CanHandle(tideSource))
                throw new InvalidDataException("TIDETables.zip failed FH5 authentication.");
            using (var fs = File.OpenRead(tideSource)) fingerprint = Convert.ToHexString(SHA256.HashData(fs));
            rawTables = Fh5ZipArchive.ReadEntries(tideSource, wantedSet);
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
                throw new InvalidDataException("Missing required FH5 TIDE table: " + fileName);
            return XDocument.Parse(Bxml.Parse(bytes).ToXml()).Root ?? throw new InvalidDataException("Empty BXML root: " + fileName);
        }

        string? gameRoot = FindGameRoot(tideSource);
        var strings = LoadEnglishStrings(tideSource, gameRoot);
        int validRoadBits = ReadValidRoadBits(gameRoot);

        uint[] onlineThresholds = LoadXml("HorizonOpenXP.xml").Elements("row")
            .OrderBy(r => IntAttr(r, "id"))
            .Select(r => UIntAttr(r, "XPRequired"))
            .ToArray();
        if (onlineThresholds.Length < 2) onlineThresholds = OnlineFallbackThresholds;

        // Clothing (player wardrobe slots only, excluding IsRemoveItem)
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
            string name = ResolveString(strings, "CharacterCustomisationItemData.IDS_DisplayName_" + id.ToString(CultureInfo.InvariantCulture), Humanize(itemId));
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
            string fallback = Humanize(StrAttr(row, "AudioHornName"));
            string name = ResolveString(strings, StrAttr(row, "DisplayName"), fallback);
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

        // QuickChat / Forza Link phrases
        var chatCategories = LoadXml("QuickChatCategoryData.xml").Elements("row").ToDictionary(
            r => (int)UIntAttr(r, "id"),
            r => ResolveString(strings, StrAttr(r, "CategoryTitle"), "Category " + StrAttr(r, "id")));
        var phrases = new List<PhraseItem>();
        foreach (var row in LoadXml("QuickChatData.xml").Elements("row"))
        {
            if (!StrAttr(row, "RequiresUnlock").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
            if (StrAttr(row, "Hidden").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(StrAttr(row, "SpecialId"))) continue;
            uint id = UIntAttr(row, "id");
            int catId = IntAttr(row, "CategoryId");
            string catName = chatCategories.GetValueOrDefault(catId, "General");
            string name = ResolveString(strings, StrAttr(row, "ChatMessage"), "Phrase #" + id.ToString(CultureInfo.InvariantCulture));
            phrases.Add(new(id, name, catName));
        }

        // ChallengeStatsBucket + ChallengeObjectives (both support <alias rowId="..." aliasId="..."/>)
        var bucketMap = IndexByIdWithAliases(LoadXml("ChallengeStatsBucket.xml"));
        var objectiveMap = IndexByIdWithAliases(LoadXml("ChallengeObjectives.xml"));
        var challengeMap = IndexByIdWithAliases(LoadXml("ChallengeData.xml"));
        var rewardMap = IndexByIdWithAliases(LoadXml("Reward.xml"));
        var currencyRewardMap = IndexByIdWithAliases(LoadXml("CurrencyReward.xml"));
        var subcategoryMap = IndexByIdWithAliases(LoadXml("CareerChallengeSubcategory.xml"));

        var objectiveDefs = new Dictionary<uint, ChallengeObjectiveDef>();
        foreach (var pair in objectiveMap)
        {
            uint objId = pair.Key;
            var objRow = pair.Value;
            string thresholdVal = "";
            string comparison = "GreaterThanOrEqual";
            if (uint.TryParse(StrAttr(objRow, "StatsBucket"), NumberStyles.None, CultureInfo.InvariantCulture, out uint bucketId) && bucketMap.TryGetValue(bucketId, out var bucket))
            {
                thresholdVal = StrAttr(bucket, "ThresholdValue");
                string cmp = StrAttr(bucket, "ThresholdComparison");
                if (!string.IsNullOrEmpty(cmp)) comparison = cmp;
            }
            string desc = ResolveString(strings, StrAttr(objRow, "Description"), "");
            objectiveDefs[objId] = new(objId, desc, thresholdVal, comparison, StrAttr(objRow, "SaveType"), StrAttr(objRow, "Type"));
        }

        ChallengeObjectiveDef[] ResolveObjectives(XElement chRow, out uint[] objIds)
        {
            objIds = SplitUInts(StrAttr(chRow, "Objectives"));
            var list = new ChallengeObjectiveDef[objIds.Length];
            for (int i = 0; i < objIds.Length; i++)
                list[i] = objectiveDefs.GetValueOrDefault(objIds[i], new ChallengeObjectiveDef(objIds[i], "", "", "GreaterThanOrEqual", "", ""));
            return list;
        }

        // Badges (BadgeChallengeData -> ChallengeData)
        var badges = new List<BadgeItem>();
        foreach (var row in LoadXml("BadgeChallengeData.xml").Elements("row").OrderBy(r => UIntAttr(r, "id")))
        {
            uint id = UIntAttr(row, "id");
            uint badgeDefId = UIntAttr(row, "Badge");
            uint chId = UIntAttr(row, "ChallengeData");
            if (!challengeMap.TryGetValue(chId, out var ch)) continue;
            string name = ResolveString(strings, StrAttr(ch, "Name"), "Badge #" + id.ToString(CultureInfo.InvariantCulture));
            string desc = ResolveString(strings, StrAttr(ch, "Description"), "");
            var objs = ResolveObjectives(ch, out uint[] objIds);
            badges.Add(new(id, badgeDefId, chId, name, desc, objIds, objs));
        }

        // Accolade Collections + Accolades
        var collections = new List<AccoladeCollectionDef>();
        var collectionByChallengeId = new Dictionary<uint, AccoladeCollectionDef>();
        foreach (var row in LoadXml("CareerChallengeCollectionData.xml").Elements("row").OrderBy(r => TryUIntAttr(r, "SortOrder")))
        {
            uint colId = UIntAttr(row, "id");
            string name = ResolveString(strings, StrAttr(row, "Name"), "Collection " + colId.ToString(CultureInfo.InvariantCulture));
            string desc = ResolveString(strings, StrAttr(row, "Description"), "");
            uint sort = TryUIntAttr(row, "SortOrder");
            uint[] chIds = SplitUInts(StrAttr(row, "Challenges"));
            var def = new AccoladeCollectionDef(colId, name, desc, sort, chIds);
            collections.Add(def);
            foreach (uint cid in chIds)
                collectionByChallengeId.TryAdd(cid, def);
        }

        int SumCareerProgressPoints(string instantRewardsAttr)
        {
            int pts = 0;
            foreach (uint rid in SplitUInts(instantRewardsAttr))
            {
                if (!rewardMap.TryGetValue(rid, out var r)) continue;
                if (!StrAttr(r, "RewardType").Equals("Currency", StringComparison.Ordinal)) continue;
                if (!uint.TryParse(StrAttr(r, "RewardID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint curId)) continue;
                if (!currencyRewardMap.TryGetValue(curId, out var cr)) continue;
                if (StrAttr(cr, "BucketId").Equals("career_progress", StringComparison.Ordinal) &&
                    StrAttr(cr, "CurrencyType").Equals("CareerProgress", StringComparison.Ordinal))
                    pts += IntAttr(cr, "Amount");
            }
            return pts;
        }

        var accolades = new List<AccoladeItem>();
        foreach (var row in LoadXml("CareerChallengeData.xml").Elements("row").OrderBy(r => UIntAttr(r, "id")))
        {
            uint id = UIntAttr(row, "id");
            if (!collectionByChallengeId.TryGetValue(id, out var col)) continue;
            uint chId = UIntAttr(row, "ChallengeData");
            if (!challengeMap.TryGetValue(chId, out var ch)) continue;
            string cat = StrAttr(row, "Category");
            string subName = uint.TryParse(StrAttr(row, "SubCategory"), NumberStyles.None, CultureInfo.InvariantCulture, out uint subId) && subcategoryMap.TryGetValue(subId, out var subRow)
                ? ResolveString(strings, StrAttr(subRow, "DisplayName"), cat)
                : (string.IsNullOrEmpty(cat) ? col.Name : cat);
            string name = ResolveString(strings, StrAttr(ch, "Name"), "Accolade #" + id.ToString(CultureInfo.InvariantCulture));
            string desc = ResolveString(strings, StrAttr(ch, "Description"), "");
            string howTo = ResolveString(strings, StrAttr(row, "HowToUnlock"), "");
            int points = SumCareerProgressPoints(StrAttr(ch, "InstantRewards"));
            var objs = ResolveObjectives(ch, out uint[] objIds);
            accolades.Add(new(id, chId, col.Id, col.Name, subName, cat, name, desc, howTo, points, objIds, objs));
        }

        return new(
            tideSource,
            fingerprint,
            onlineThresholds,
            validRoadBits,
            DefaultRoadTargetCount,
            clothing.OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToArray(),
            horns.OrderBy(h => h.Id).ToArray(),
            emotes.OrderBy(e => e.Id).ToArray(),
            phrases.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray(),
            badges.OrderBy(b => b.Id).ToArray(),
            collections.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToArray(),
            accolades.OrderBy(a => a.CollectionId).ThenBy(a => a.Id).ToArray())
        {
            ObjectivesById = objectiveDefs,
        };
    }

    static Dictionary<string, string> LoadEnglishStrings(string tideSource, string? gameRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? tideDir = File.Exists(tideSource) ? Path.GetDirectoryName(tideSource) : Path.GetDirectoryName(tideSource);
        var zipCandidates = new List<string>();
        var dirCandidates = new List<string>();
        if (tideDir != null)
        {
            zipCandidates.Add(Path.Combine(tideDir, "StringTables", "EN.zip"));
            dirCandidates.Add(Path.Combine(tideDir, "StringTables", "EN"));
        }
        if (gameRoot != null)
        {
            zipCandidates.Add(Path.Combine(gameRoot, "media", "stripped", "StringTables", "EN.zip"));
            zipCandidates.Add(Path.Combine(gameRoot, "StringTables", "EN.zip"));
            dirCandidates.Add(Path.Combine(gameRoot, "media", "stripped", "StringTables", "EN"));
            dirCandidates.Add(Path.Combine(gameRoot, "StringTables", "EN"));
        }

        string? enZip = zipCandidates.FirstOrDefault(File.Exists);
        if (enZip != null && Fh5ZipArchive.CanHandle(enZip))
        {
            foreach (var pair in Fh5ZipArchive.ReadEntriesByExtension(enZip, ".str"))
                ReadStrTableInto(pair.Value, map);
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

    static int ReadValidRoadBits(string? gameRoot)
    {
        if (gameRoot == null) return DefaultValidRoadBits;
        string nav = Path.Combine(gameRoot, "media", "Tracks", "Hendrix", "Ribbon_00", "Hendrix_00.nav");
        if (!File.Exists(nav)) return DefaultValidRoadBits;
        try
        {
            using var fs = File.OpenRead(nav);
            if (fs.Length < 0x60) return DefaultValidRoadBits;
            Span<byte> hdr = stackalloc byte[0x60];
            fs.ReadExactly(hdr);
            // "WVNA" = 0x4E415657 LE
            if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x4E415657u) return DefaultValidRoadBits;
            int bits = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(0x58, 4));
            return bits is >= 1000 and <= 32768 ? bits : DefaultValidRoadBits;
        }
        catch (IOException) { return DefaultValidRoadBits; }
    }

    static Dictionary<uint, XElement> IndexByIdWithAliases(XElement root)
    {
        var rows = root.Elements("row").ToList();
        var map = new Dictionary<uint, XElement>(rows.Count);
        foreach (var row in rows)
        {
            if (uint.TryParse(StrAttr(row, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                map[id] = row;
        }
        foreach (var alias in root.Elements("alias"))
        {
            if (uint.TryParse(StrAttr(alias, "rowId"), NumberStyles.None, CultureInfo.InvariantCulture, out uint rowId) &&
                int.TryParse(StrAttr(alias, "aliasId"), NumberStyles.None, CultureInfo.InvariantCulture, out int aliasIdx) &&
                (uint)aliasIdx < (uint)rows.Count)
            {
                map.TryAdd(rowId, rows[aliasIdx]);
            }
        }
        return map;
    }

    static uint[] BuildOnlineFallbackThresholds()
    {
        (uint Step, int Count)[] rle =
        {
            (2000, 2), (3000, 6), (4000, 90), (5000, 100), (6000, 100), (7000, 100),
            (8000, 100), (9000, 100), (10000, 100), (11000, 100), (12000, 100),
            (20000, 20), (24000, 20), (28000, 20), (30000, 20), (36000, 10), (38000, 11),
        };
        var list = new List<uint>(1000) { 0u };
        uint cur = 0;
        foreach (var (step, count) in rle)
        {
            for (int i = 0; i < count; i++)
            {
                cur += step;
                list.Add(cur);
            }
        }
        return list.ToArray();
    }

    internal static string ClothingCategory(string slot) => slot switch
    {
        "UpperBodyLayer1" => "Tops",
        "UpperBodyLayer2" => "Jackets",
        "LowerBody" => "Legs",
        "Feet" => "Feet",
        "Hat" => "Hats",
        "Outfit" => "Outfits",
        "Socks" => "Socks",
        "Dress" => "Dresses",
        "Hands" => "Gloves",
        "Face" => "Face",
        "LeftWrist" => "Watches",
        "Eye" => "Glasses",
        _ => slot,
    };

    static string ResolveString(Dictionary<string, string> strings, string key, string fallback)
        => !string.IsNullOrEmpty(key) && strings.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val) ? val : fallback;

    static string Humanize(string text)
        => string.IsNullOrWhiteSpace(text) ? "" : Regex.Replace(
            text.Replace("Horn_SFX_", "").Replace("Horn_Musical_", "").Replace("Horn_Standard_", "").Replace('_', ' '),
            "(?<=[a-z])(?=[A-Z])", " ").Trim();

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
    static uint TryUIntAttr(XElement el, string name) => uint.TryParse(StrAttr(el, name), NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? v : 0;
    static int IntAttr(XElement el, string name) => int.TryParse(StrAttr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
#endif
