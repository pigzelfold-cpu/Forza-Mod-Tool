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
    public sealed record PlaylistSeriesDef(uint Id, string Name, int[] SeasonMaxima)
    {
        public int Maximum => SeasonMaxima.Sum();
    }
    public sealed record HotWheelsRankDef(uint Id, string Rank, string DisplayName, string MaxCarClass, int MedalTarget, uint CompleteQualifierMission);
    public sealed record HotWheelsMissionItem(uint Id, uint ChallengeDataId, string Rank, string RankDisplay, string Importance, uint SortOrder, string Name, string Description, int Medals, uint[] ObjectiveIds, ChallengeObjectiveDef[] Objectives);
    public sealed record RallyChallengeItem(uint Id, uint ChallengeDataId, string TeamBucket, string TeamName, int StageIndex, string Name, string Description, int Reputation, uint[] ObjectiveIds, ChallengeObjectiveDef[] Objectives);

    internal sealed record Snapshot(
        string Path,
        string Fingerprint,
        uint[] OnlineThresholds,
        int ValidRoadBits,
        int Ex1ValidRoadBits,
        int Ex2ValidRoadBits,
        uint RoadTargetCount,
        ClothingItem[] Clothing,
        HornItem[] Horns,
        EmoteItem[] Emotes,
        PhraseItem[] Phrases,
        BadgeItem[] Badges,
        AccoladeCollectionDef[] Collections,
        AccoladeItem[] Accolades,
        PlaylistSeriesDef[] Playlists,
        HotWheelsRankDef[] HotWheelsRanks,
        HotWheelsMissionItem[] HotWheelsMissions,
        RallyChallengeItem[] RallyChallenges)
    {
        internal IReadOnlyDictionary<uint, ChallengeObjectiveDef> ObjectivesById { get; init; } = new Dictionary<uint, ChallengeObjectiveDef>();
        internal IReadOnlyDictionary<uint, BadgeItem> BadgesById { get; } = Badges.ToDictionary(b => b.Id);
        internal IReadOnlyDictionary<uint, AccoladeItem> AccoladesById { get; } = Accolades.ToDictionary(a => a.Id);
        internal IReadOnlyDictionary<uint, AccoladeCollectionDef> CollectionsById { get; } = Collections.ToDictionary(c => c.Id);
        internal IReadOnlyDictionary<ushort, ClothingItem> ClothingById { get; } = Clothing.ToDictionary(c => c.Id);
        internal IReadOnlyDictionary<uint, HornItem> HornsById { get; } = Horns.ToDictionary(h => h.Id);
        internal IReadOnlyDictionary<uint, EmoteItem> EmotesById { get; } = Emotes.ToDictionary(e => e.Id);
        internal IReadOnlyDictionary<uint, PhraseItem> PhrasesById { get; } = Phrases.ToDictionary(p => p.Id);
        internal IReadOnlyDictionary<uint, PlaylistSeriesDef> PlaylistsById { get; } = Playlists.ToDictionary(p => p.Id);
        internal IReadOnlyDictionary<uint, HotWheelsMissionItem> HotWheelsMissionsById { get; } = HotWheelsMissions.ToDictionary(m => m.Id);
        internal IReadOnlyDictionary<uint, RallyChallengeItem> RallyChallengesById { get; } = RallyChallenges.ToDictionary(c => c.Id);
    }

    public sealed record Status(bool Loaded, string Source, string Message);

    // Exact 17-segment RLE table of FH5 HorizonOpenXP.xml (1,000 ranks: Rank 1 = 0 .. Rank 1000 = 10,000,000).
    internal static readonly uint[] OnlineFallbackThresholds = BuildOnlineFallbackThresholds();
    internal const int DefaultValidRoadBits = 24_498;
    internal const int DefaultEx1ValidRoadBits = 10_877;
    internal const int DefaultEx2ValidRoadBits = 3_171;
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
        "SeasonScheduleOrder.xml",
        "FestivalPassSchedule.xml",
        "FestivalPassEventInfo.xml",
        "SpectrumMissionChallengeData.xml",
        "SpectrumRankData.xml",
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
            $"Installed FH5 game data: {data.Clothing.Length} clothes · {data.Horns.Length} horns · {data.Emotes.Length} emotes · {data.Phrases.Length} phrases · {data.Badges.Length} badges · {data.Accolades.Length:n0} Accolades · {data.HotWheelsMissions.Length} Hot Wheels · {data.RallyChallenges.Length} Rally.")
        : new(false, "Built-in fallback",
            "Select FH5 TIDETables.zip (or mediapc\\stripped\\TIDETables) for unlock, Accolade & expansion editing. Built-in XP tables remain active.");

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
        int validRoadBits = ReadNavBitCount(gameRoot, Path.Combine("media", "Tracks", "Hendrix", "Ribbon_00", "Hendrix_00.nav"), DefaultValidRoadBits);
        int ex1RoadBits = ReadNavBitCount(gameRoot, Path.Combine("DLC", "MicrosoftExpansion1FH5", "media", "Tracks", "Rainbow", "Ribbon_00", "rainbow_00.nav"), DefaultEx1ValidRoadBits);
        int ex2RoadBits = ReadNavBitCount(gameRoot, Path.Combine("DLC", "MicrosoftExpansion2FH5", "media", "Tracks", "Falcon", "Ribbon_00", "falcon_00.nav"), DefaultEx2ValidRoadBits);

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

        int SumCurrencyAmount(string instantRewardsAttr, string bucketId, string currencyType)
        {
            int amt = 0;
            foreach (uint rid in SplitUInts(instantRewardsAttr))
            {
                if (!rewardMap.TryGetValue(rid, out var r)) continue;
                if (!StrAttr(r, "RewardType").Equals("Currency", StringComparison.Ordinal)) continue;
                if (!uint.TryParse(StrAttr(r, "RewardID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint curId)) continue;
                if (!currencyRewardMap.TryGetValue(curId, out var cr)) continue;
                if (StrAttr(cr, "BucketId").Equals(bucketId, StringComparison.Ordinal) &&
                    StrAttr(cr, "CurrencyType").Equals(currencyType, StringComparison.Ordinal))
                    amt += IntAttr(cr, "Amount");
            }
            return amt;
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
            int points = SumCurrencyAmount(StrAttr(ch, "InstantRewards"), "career_progress", "CareerProgress");
            var objs = ResolveObjectives(ch, out uint[] objIds);
            accolades.Add(new(id, chId, col.Id, col.Name, subName, cat, name, desc, howTo, points, objIds, objs));
        }

        // Hot Wheels Academy Ranks & Missions
        var hotWheelsRanks = rawTables.ContainsKey("SpectrumRankData.xml")
            ? LoadXml("SpectrumRankData.xml").Elements("row")
                .OrderBy(r => UIntAttr(r, "id"))
                .Select(r =>
                {
                    string rk = StrAttr(r, "Rank");
                    return new HotWheelsRankDef(
                        UIntAttr(r, "id"),
                        rk,
                        HotWheelsRankDisplay(rk),
                        StrAttr(r, "MaxCarClass"),
                        IntAttr(r, "MedalTarget"),
                        TryUIntAttr(r, "CompleteQualifierMission"));
                })
                .ToArray()
            : Array.Empty<HotWheelsRankDef>();

        var hotWheelsMissions = new List<HotWheelsMissionItem>();
        if (rawTables.ContainsKey("SpectrumMissionChallengeData.xml"))
        {
            foreach (var row in LoadXml("SpectrumMissionChallengeData.xml").Elements("row"))
            {
                uint id = UIntAttr(row, "id");
                uint chId = UIntAttr(row, "ChallengeData");
                if (!challengeMap.TryGetValue(chId, out var ch)) continue;
                string rank = StrAttr(row, "AssociatedRank");
                string imp = StrAttr(row, "ImportanceType");
                uint sort = TryUIntAttr(row, "SortOrder");
                string name = CleanPlaylistName(ResolveString(strings, StrAttr(ch, "Name"), "Mission #" + id.ToString(CultureInfo.InvariantCulture)));
                string desc = CleanPlaylistName(ResolveString(strings, StrAttr(ch, "Description"), ""));
                int medals = SumCurrencyAmount(StrAttr(ch, "InstantRewards"), "career_progress", "SpectrumMedals");
                var objs = ResolveObjectives(ch, out uint[] objIds);
                hotWheelsMissions.Add(new(id, chId, rank, HotWheelsRankDisplay(rank), imp, sort, name, desc, medals, objIds, objs));
            }
        }

        // Rally Adventure (Sierra Nueva) Team Challenges
        var rallyStageMap = ReadRallyStageMap(gameRoot);
        var rallyChallenges = new List<RallyChallengeItem>();
        foreach (var ch in LoadXml("ChallengeData.xml").Elements("row"))
        {
            uint id = UIntAttr(ch, "id");
            if (rallyStageMap.Count > 0 && !rallyStageMap.ContainsKey(id)) continue;
            int rep = 0;
            string teamBucket = "";
            foreach (uint rid in SplitUInts(StrAttr(ch, "InstantRewards")))
            {
                if (!rewardMap.TryGetValue(rid, out var r)) continue;
                if (!StrAttr(r, "RewardType").Equals("Currency", StringComparison.Ordinal)) continue;
                if (!uint.TryParse(StrAttr(r, "RewardID"), NumberStyles.None, CultureInfo.InvariantCulture, out uint curId)) continue;
                if (!currencyRewardMap.TryGetValue(curId, out var cr)) continue;
                if (StrAttr(cr, "CurrencyType").Equals("AstraReputation", StringComparison.Ordinal))
                {
                    teamBucket = StrAttr(cr, "BucketId");
                    rep += IntAttr(cr, "Amount");
                }
            }
            if (rep <= 0 || string.IsNullOrEmpty(teamBucket)) continue;
            string name = CleanPlaylistName(ResolveString(strings, StrAttr(ch, "Name"), "Rally #" + id.ToString(CultureInfo.InvariantCulture)));
            string desc = CleanPlaylistName(ResolveString(strings, StrAttr(ch, "Description"), ""));
            int stageIdx = rallyStageMap.TryGetValue(id, out int st) ? st : -1;
            var objs = ResolveObjectives(ch, out uint[] objIds);
            rallyChallenges.Add(new(id, id, teamBucket, RallyTeamDisplayName(teamBucket), stageIdx, name, desc, rep, objIds, objs));
        }

        var playlists = ReadPlaylists(rawTables, strings);

        return new(
            tideSource,
            fingerprint,
            onlineThresholds,
            validRoadBits,
            ex1RoadBits,
            ex2RoadBits,
            DefaultRoadTargetCount,
            clothing.OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToArray(),
            horns.OrderBy(h => h.Id).ToArray(),
            emotes.OrderBy(e => e.Id).ToArray(),
            phrases.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray(),
            badges.OrderBy(b => b.Id).ToArray(),
            collections.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToArray(),
            accolades.OrderBy(a => a.CollectionId).ThenBy(a => a.Id).ToArray(),
            playlists,
            hotWheelsRanks,
            hotWheelsMissions.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToArray(),
            rallyChallenges.OrderBy(r => r.TeamName, StringComparer.Ordinal).ThenBy(r => r.StageIndex < 0 ? int.MaxValue : r.StageIndex).ThenBy(r => r.Id).ToArray())
        {
            ObjectivesById = objectiveDefs,
        };
    }

    static PlaylistSeriesDef[] ReadPlaylists(Dictionary<string, byte[]> rawTables, Dictionary<string, string> strings)
    {
        if (!rawTables.TryGetValue("SeasonScheduleOrder.xml", out byte[]? orderBytes) ||
            !rawTables.TryGetValue("FestivalPassSchedule.xml", out byte[]? scheduleBytes) ||
            !rawTables.TryGetValue("FestivalPassEventInfo.xml", out byte[]? eventInfoBytes))
            return Array.Empty<PlaylistSeriesDef>();

        static XElement? ParseRoot(byte[] data) => XDocument.Parse(Bxml.Parse(data).ToXml()).Root;
        var orderRoot = ParseRoot(orderBytes);
        var scheduleRoot = ParseRoot(scheduleBytes);
        var eventInfoRoot = ParseRoot(eventInfoBytes);
        if (orderRoot == null || scheduleRoot == null || eventInfoRoot == null)
            return Array.Empty<PlaylistSeriesDef>();

        var schedules = scheduleRoot.Elements("row")
            .Where(r => uint.TryParse(StrAttr(r, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .ToDictionary(r => UIntAttr(r, "id"));

        var eventTypesByInstance = new Dictionary<uint, List<string>>();
        foreach (var row in eventInfoRoot.Elements("row"))
        {
            if (!uint.TryParse(StrAttr(row, "LocalSeasonInstance"), NumberStyles.None, CultureInfo.InvariantCulture, out uint inst))
                continue;
            string eventType = StrAttr(row, "EventType");
            if (string.IsNullOrEmpty(eventType)) continue;
            if (!eventTypesByInstance.TryGetValue(inst, out var list))
            {
                list = new List<string>();
                eventTypesByInstance[inst] = list;
            }
            list.Add(eventType);
        }

        var result = new List<PlaylistSeriesDef>();
        foreach (var group in orderRoot.Elements("row")
            .Select(r => (Row: r, HasSeries: uint.TryParse(StrAttr(r, "Series"), NumberStyles.None, CultureInfo.InvariantCulture, out uint s), Series: TryUIntAttr(r, "Series")))
            .Where(x => x.HasSeries && x.Series > 0)
            .GroupBy(x => x.Series))
        {
            uint seriesId = group.Key;
            var rows = group.Select(x => x.Row).OrderBy(r => TryUIntAttr(r, "id")).ToArray();
            if (rows.Length != 4) continue;

            string rawName = "Series " + seriesId.ToString(CultureInfo.InvariantCulture);
            if (uint.TryParse(StrAttr(rows[0], "FestivalPassScheduleRowId"), NumberStyles.None, CultureInfo.InvariantCulture, out uint fpRowId) &&
                schedules.TryGetValue(fpRowId, out var schedRow))
            {
                string histKey = StrAttr(schedRow, "SeriesHistoryName");
                string seriesKey = StrAttr(schedRow, "SeriesName");
                string resolved = ResolveString(strings, seriesKey, ResolveString(strings, histKey, rawName));
                rawName = CleanPlaylistName(resolved);
                if (string.IsNullOrWhiteSpace(rawName))
                    rawName = "Series " + seriesId.ToString(CultureInfo.InvariantCulture);
            }

            var seasonMaxima = new int[4];
            for (int i = 0; i < 4; i++)
            {
                uint inst = TryUIntAttr(rows[i], "SeasonInstanceRef");
                if (eventTypesByInstance.TryGetValue(inst, out var types))
                {
                    int sum = 0;
                    foreach (string et in types)
                        sum += PlaylistEventPoints(seriesId, et);
                    seasonMaxima[i] = sum;
                }
            }
            result.Add(new(seriesId, rawName, seasonMaxima));
        }

        return result.OrderBy(p => p.Id).ToArray();
    }

    static string CleanPlaylistName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string stripped = Regex.Replace(text, @"\[HIGHLIGHT:([^\]]*)\]", "$1");
        stripped = stripped.Replace('\r', ' ').Replace('\n', ' ');
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    static int PlaylistEventPoints(uint series, string eventType) => eventType switch
    {
        "SeasonalEvent" => 5,
        "Trial" => 10,
        "PRStunt" => 2,
        "PGGamesSeasonal" => 3,
        "ForzathonDaily" => 7,
        "ForzathonWeekly" => 5,
        "MonthlyRival" => series >= 7 ? 1 : 4,
        "HorizonStory" => 3,
        "ShowcaseRemix" => 5,
        "PhotoChallenge" => 2,
        "TreasureChest" => 3,
        "HorizonTour" => series >= 25 ? 8 : 3,
        "HorizonOpen" => 2,
        "Collectibles" => 3,
        "Eliminator" => 2,
        "StuntParty" => 3,
        "EventLabEvent" => 3,
        "HideSeek" => series >= 38 ? 2 : 0,
        _ => 0,
    };

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

    static string HotWheelsRankDisplay(string rank) => rank switch
    {
        "Unranked" => "Unranked (Qualifier)",
        "Rookie" => "Rookie (B Class)",
        "Pro" => "Pro (A Class)",
        "Expert" => "Expert (S1 Class)",
        "Elite" => "Elite (S2 Class)",
        "SkydriverLegend" => "Legend (X Class)",
        _ => rank,
    };

    static string RallyTeamDisplayName(string bucketId) => bucketId switch
    {
        "team_alejandra_reputation" => "Apex Predators (Alejandra)",
        "team_alex_reputation" => "Horizon Raptors (Alex)",
        "team_rami_reputation" => "Grit Reapers (Rami)",
        _ => Humanize(bucketId),
    };

    static Dictionary<uint, int> ReadRallyStageMap(string? gameRoot)
    {
        var map = new Dictionary<uint, int>();
        if (gameRoot == null) return map;
        string cmsZip = Path.Combine(gameRoot, "media", "CMS.zip");
        if (!File.Exists(cmsZip) || !Fh5ZipArchive.CanHandle(cmsZip)) return map;
        try
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "7ff5909c-0683-4443-a026-bf4826153901-en-GB.om.xml",
                "7ff5909c-0683-4443-a026-bf4826153901-DEV.om.xml",
            };
            var files = Fh5ZipArchive.ReadEntries(cmsZip, wanted);
            byte[]? raw = files.GetValueOrDefault("7ff5909c-0683-4443-a026-bf4826153901-en-GB.om.xml")
                ?? files.GetValueOrDefault("7ff5909c-0683-4443-a026-bf4826153901-DEV.om.xml");
            if (raw == null) return map;
            var doc = XDocument.Parse(Encoding.UTF8.GetString(raw));
            foreach (var stageEl in doc.Descendants("property").Where(p => StrAttr(p, "id") == "Stages").SelectMany(p => p.Elements("element")))
            {
                var idxProp = stageEl.Elements("property").FirstOrDefault(p => StrAttr(p, "id") == "Index");
                if (idxProp == null || !int.TryParse(StrAttr(idxProp, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stageIdx))
                    continue;
                foreach (var chProp in stageEl.Descendants("property").Where(p => StrAttr(p, "id") == "ChallengeDataRow"))
                {
                    if (uint.TryParse(StrAttr(chProp, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out uint chId))
                        map[chId] = stageIdx;
                }
            }
        }
        catch { }
        return map;
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
