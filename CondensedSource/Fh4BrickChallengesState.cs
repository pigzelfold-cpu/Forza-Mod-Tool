using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>FH4 LEGO Speed Champions (LEGO Valley) and Fortune Island save-state currency, house, board, and landmark helpers.</summary>
public static class Fh4ExpansionsSaveState
{
    public const string BricksObjectId = "CurrencyBucket_Bricks_bricks";
    public const string BricksName = "bricks";
    public const string BricksType = "Bricks";

    public const string TiersObjectId = "CurrencyBucket_BrickChallengeTiers_tiers";
    public const string TiersName = "tiers";
    public const string TiersType = "BrickChallengeTiers";

    public const string FortuneIslandXpObjectId = "CurrencyBucket_ExpansionOneCareerXP_TrackSpecificXP";
    public const string FortuneIslandXpName = "TrackSpecificXP";
    public const string FortuneIslandXpType = "ExpansionOneCareerXP";

    public const string HouseObjectId = "player_house_red_festival_save_state";
    public const string HouseType = "FestivalSiteState";
    public const int MaxLegoBricks = 12_500;

    static readonly int[] BrickTierThresholds =
    {
        0, 350, 750, 1200, 1750, 2400, 3100, 3900, 4800, 5800, 6900, 8100, 9400, 10700, 12000,
    };

    public static (int Tiers, int HouseLevel) ComputeTierAndHouseLevel(int bricks)
    {
        int clamped = Math.Max(0, bricks);
        int tier = 0;
        for (int i = 0; i < BrickTierThresholds.Length; i++)
        {
            if (clamped >= BrickTierThresholds[i]) tier = i;
        }
        int houseLevel = tier == 0 ? 1 : (tier >= 14 ? 16 : tier + 1);
        return (tier, houseLevel);
    }

    public static string GetHouseStageDisplay(int level, bool built = true) => level switch
    {
        <= 0 => built ? "Level 0" : "Not built yet",
        1 => "Level 1 / 16 · Horizon Base Camp",
        >= 16 => "Level 16 / 16 · Master Builder's House Complete",
        _ => $"Level {level} / 16 · Master Builder Upgrade",
    };

    public static bool TryReadExpansionStates(ProfileContainer? container, out string ex1Intro, out string ex2Intro, out string barnFindState)
    {
        ex1Intro = "Unknown";
        ex2Intro = "Unknown";
        barnFindState = "Unknown";
        if (container?.SaveState is not { } bx || !IsObjectDictionary(bx)) return false;

        ex1Intro = ReadObjectProperty(bx, "IEStateExpansionOne", "IESaveStateExpansionOne", "IEState") ?? "Absent";
        ex2Intro = ReadObjectProperty(bx, "IEStateExpansionTwo", "IESaveStateExpansionTwo", "IEState") ?? "Absent";
        barnFindState = ReadObjectProperty(bx, "BARN_FIND_RED_1_save_state", "BarnFindState", "State") ?? "Absent";
        return true;
    }

    public static bool TryReadCurrencyBucket(ProfileContainer? container, string objectId, string expectedName, string expectedType,
        out bool exists, out int total, out Bxml.Node? totalNode)
    {
        exists = false;
        total = 0;
        totalNode = null;
        if (container?.SaveState is not { } bx || !IsObjectDictionary(bx)) return false;

        var matches = FindObjects(bx, objectId);
        if (matches.Length == 0) return true;
        if (matches.Length != 1 || bx.GetAttribute(matches[0], "Type") != "CurrencyBucket") return false;

        if (FindProperty(bx, matches[0], "Name", "string") is not { } nameProp || bx.GetAttribute(nameProp, "Value") != expectedName
            || FindProperty(bx, matches[0], "Type", "CurrencyTypes") is not { } typeProp || bx.GetAttribute(typeProp, "Value") != expectedType
            || FindProperty(bx, matches[0], "Total", "int") is not { } scoreProp
            || !int.TryParse(bx.GetAttribute(scoreProp, "Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < 0)
        {
            return false;
        }

        exists = true;
        total = parsed;
        totalNode = scoreProp;
        return true;
    }

    public static bool TrySetOrCreateCurrencyBucket(ProfileContainer? container, string objectId, string bucketName, string currencyType,
        int total, out bool changed, out string reason, bool syncTierAndHouseWithBricks = false)
    {
        changed = false;
        reason = "The FH4 expansion currency bucket layout is not safely recognized; nothing was changed.";
        if (total < 0)
        {
            reason = "Value cannot be negative.";
            return false;
        }
        if (!TryReadCurrencyBucket(container, objectId, bucketName, currencyType, out bool exists, out int oldTotal, out _))
            return false;

        bool doSync = syncTierAndHouseWithBricks && objectId == BricksObjectId;
        if (exists && oldTotal == total && !doSync)
        {
            reason = "";
            return true;
        }

        var (targetTiers, targetHouseLevel) = ComputeTierAndHouseLevel(total);
        if (doSync)
        {
            if (!TryReadCurrencyBucket(container, TiersObjectId, TiersName, TiersType, out bool tExists, out int oldTiers, out _)
                || !TryReadHouseState(container, out bool hExists, out bool hBuilt, out bool hAct, out int oldHouse))
            {
                reason = "LEGO tier or Master Builder's House state is not safely recognized; nothing was changed.";
                return false;
            }
            if (exists && oldTotal == total && tExists && oldTiers == targetTiers && hExists && hBuilt && hAct && oldHouse == targetHouseLevel)
            {
                reason = "";
                return true;
            }
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        if (!TryReadCurrencyBucket(staged, objectId, bucketName, currencyType, out _, out _, out var stagedTotalNode))
            return false;

        ApplyCurrencyBucketMutation(staged.SaveState!, stagedTotalNode, objectId, bucketName, currencyType, total);
        if (doSync)
        {
            TryReadCurrencyBucket(staged, TiersObjectId, TiersName, TiersType, out _, out _, out var stagedTierNode);
            ApplyCurrencyBucketMutation(staged.SaveState!, stagedTierNode, TiersObjectId, TiersName, TiersType, targetTiers);
            ApplyHouseMutation(staged.SaveState!, targetHouseLevel);
        }

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadCurrencyBucket(verified, objectId, bucketName, currencyType, out bool vExists, out int vTotal, out _)
            || !vExists || vTotal != total)
        {
            reason = "Staged FH4 currency edit failed round-trip verification; nothing was changed.";
            return false;
        }
        if (doSync && (!TryReadCurrencyBucket(verified, TiersObjectId, TiersName, TiersType, out bool vtEx, out int vtVal, out _)
            || !vtEx || vtVal != targetTiers
            || !TryReadHouseState(verified, out bool vhEx, out bool vhBuilt, out bool vhAct, out int vhLvl)
            || !vhEx || !vhBuilt || !vhAct || vhLvl != targetHouseLevel))
        {
            reason = "Staged LEGO tier/house sync failed verification; nothing was changed.";
            return false;
        }

        TryReadCurrencyBucket(container, objectId, bucketName, currencyType, out _, out _, out var liveTotalNode);
        ApplyCurrencyBucketMutation(container.SaveState!, liveTotalNode, objectId, bucketName, currencyType, total);
        if (doSync)
        {
            TryReadCurrencyBucket(container, TiersObjectId, TiersName, TiersType, out _, out _, out var liveTierNode);
            ApplyCurrencyBucketMutation(container.SaveState!, liveTierNode, TiersObjectId, TiersName, TiersType, targetTiers);
            ApplyHouseMutation(container.SaveState!, targetHouseLevel);
        }
        changed = true;
        reason = "";
        return true;
    }

    public static bool TryReadHouseState(ProfileContainer? container, out bool exists, out bool built, out bool activated, out int level)
    {
        exists = false;
        built = false;
        activated = false;
        level = 0;
        if (container?.SaveState is not { } bx || !IsObjectDictionary(bx)) return false;

        var matches = FindObjects(bx, HouseObjectId);
        if (matches.Length == 0) return true;
        if (matches.Length != 1 || bx.GetAttribute(matches[0], "Type") != HouseType) return false;

        var actProp = FindProperty(bx, matches[0], "Activated", "bool");
        var builtProp = FindProperty(bx, matches[0], "Built", "bool");
        var lvlProp = FindProperty(bx, matches[0], "Level", "int");
        if (actProp is null || builtProp is null || lvlProp is null
            || bx.GetAttribute(actProp, "Value") is not ("true" or "false")
            || bx.GetAttribute(builtProp, "Value") is not ("true" or "false")
            || !int.TryParse(bx.GetAttribute(lvlProp, "Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lvl)
            || lvl < 0)
        {
            return false;
        }

        exists = true;
        activated = bx.GetAttribute(actProp, "Value") == "true";
        built = bx.GetAttribute(builtProp, "Value") == "true";
        level = lvl;
        return true;
    }

    public static bool TrySetOrCreateHouseState(ProfileContainer? container, int level, out bool changed, out string reason)
    {
        changed = false;
        reason = "Master Builder's House layout is not safely recognized; nothing was changed.";
        if (level is < 1 or > 16)
        {
            reason = "Master Builder's House level must be between 1 and 16.";
            return false;
        }
        if (!TryReadHouseState(container, out bool exists, out bool built, out bool activated, out int oldLevel))
            return false;
        if (exists && built && activated && oldLevel == level)
        {
            reason = "";
            return true;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        ApplyHouseMutation(staged.SaveState!, level);

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadHouseState(verified, out bool vEx, out bool vBuilt, out bool vAct, out int vLvl)
            || !vEx || !vBuilt || !vAct || vLvl != level)
        {
            reason = "Staged Master Builder's House edit failed round-trip verification; nothing was changed.";
            return false;
        }

        ApplyHouseMutation(container.SaveState!, level);
        changed = true;
        reason = "";
        return true;
    }

    public static bool TryReadLegoBoards(ProfileContainer? container, out int collected, out int total)
    {
        collected = 0;
        total = 0;
        if (container?.SaveState is not { } bx || !IsObjectDictionary(bx)) return false;

        foreach (var obj in bx.Root.Children)
        {
            if (bx.Strings[obj.NameIndex] != "Object") continue;
            string? id = bx.GetAttribute(obj, "Id");
            if (string.IsNullOrEmpty(id) || !IsLegoBoardId(id)) continue;
            if (bx.GetAttribute(obj, "Type") != "DiscountBoardSaveState") return false;
            var prop = FindProperty(bx, obj, "State", "DiscountBoardState");
            if (prop is null) return false;
            total++;
            if (bx.GetAttribute(prop, "Value") == "Collected") collected++;
        }
        return true;
    }

    public static bool TrySmashAllLegoBoards(ProfileContainer? container, out int smashed, out string reason)
    {
        smashed = 0;
        reason = "LEGO Valley bonus boards are not safely recognized; nothing was changed.";
        if (!TryReadLegoBoards(container, out _, out int total)) return false;
        if (total == 0)
        {
            reason = "No LEGO Valley bonus board entries exist in this save yet (visit LEGO Valley in-game first).";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        int stagedSmashed = ApplySmashLegoBoards(staged);
        if (stagedSmashed < 0) return false;
        if (stagedSmashed == 0)
        {
            reason = "";
            return true;
        }

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadLegoBoards(verified, out int vCol, out int vTot)
            || vTot != total || vCol != vTot)
        {
            reason = "Staged LEGO bonus board edit failed round-trip verification; nothing was changed.";
            return false;
        }

        smashed = ApplySmashLegoBoards(container);
        reason = "";
        return true;
    }

    public static bool TryReadLegoLandmarks(ProfileContainer? container, out int activated, out int total)
    {
        activated = 0;
        total = 0;
        if (container?.SaveState is not { } bx || !IsObjectDictionary(bx)) return false;

        foreach (var obj in bx.Root.Children)
        {
            if (bx.Strings[obj.NameIndex] != "Object") continue;
            string? id = bx.GetAttribute(obj, "Id");
            if (string.IsNullOrEmpty(id) || !IsLegoLandmarkId(id, bx.GetAttribute(obj, "Type"))) continue;
            var prop = FindProperty(bx, obj, "Activated", "bool");
            if (prop is null || bx.GetAttribute(prop, "Value") is not ("true" or "false")) return false;
            total++;
            if (bx.GetAttribute(prop, "Value") == "true") activated++;
        }
        return true;
    }

    public static bool TryDiscoverAllLegoLandmarks(ProfileContainer? container, out int discovered, out string reason)
    {
        discovered = 0;
        reason = "LEGO Valley landmarks are not safely recognized; nothing was changed.";
        if (!TryReadLegoLandmarks(container, out _, out int total)) return false;
        if (total == 0)
        {
            reason = "No LEGO Valley landmark entries exist in this save yet (visit LEGO Valley in-game first).";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        CopyCatalogSnapshots(container, staged);
        int stagedDone = ApplyDiscoverLegoLandmarks(staged.SaveState!);
        if (stagedDone < 0) return false;
        if (stagedDone == 0)
        {
            reason = "";
            return true;
        }

        byte[] bytes = staged.Serialize();
        var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes)
            || !TryReadLegoLandmarks(verified, out int vAct, out int vTot)
            || vTot != total || vAct != vTot)
        {
            reason = "Staged LEGO landmark edit failed round-trip verification; nothing was changed.";
            return false;
        }

        discovered = ApplyDiscoverLegoLandmarks(container.SaveState!);
        reason = "";
        return true;
    }

    internal static void ApplyCurrencyBucketMutation(Bxml bx, Bxml.Node? totalNode, string objectId, string bucketName, string currencyType, int total)
    {
        string valueText = total.ToString(CultureInfo.InvariantCulture);
        if (totalNode != null)
        {
            SetFreshAttrValue(bx, totalNode, "Value", valueText);
            return;
        }

        var obj = MakeObjectNode(bx, objectId, "CurrencyBucket",
            ("Name", "string", bucketName, false),
            ("Type", "CurrencyTypes", currencyType, false),
            ("Total", "int", valueText, true));
        bx.Root.Children.Add(obj);
        bx.MarkDirty();
    }

    internal static void ApplyHouseMutation(Bxml bx, int level)
    {
        string levelText = level.ToString(CultureInfo.InvariantCulture);
        var existing = FindObjects(bx, HouseObjectId).FirstOrDefault();
        if (existing != null)
        {
            var actProp = FindProperty(bx, existing, "Activated", "bool");
            var builtProp = FindProperty(bx, existing, "Built", "bool");
            var lvlProp = FindProperty(bx, existing, "Level", "int");
            if (actProp != null) SetFreshAttrValue(bx, actProp, "Value", "true");
            if (builtProp != null) SetFreshAttrValue(bx, builtProp, "Value", "true");
            if (lvlProp != null) SetFreshAttrValue(bx, lvlProp, "Value", levelText);
            return;
        }

        var obj = MakeObjectNode(bx, HouseObjectId, HouseType,
            ("Activated", "bool", "true", false),
            ("Built", "bool", "true", false),
            ("Level", "int", levelText, true));
        bx.Root.Children.Add(obj);
        bx.MarkDirty();
    }

    internal static void CopyCatalogSnapshots(ProfileContainer src, ProfileContainer dst)
    {
#if PROFILE_UNLOCK_TESTING
        Fh4ProfileGameData.Copy(src, dst);
        ProfileGameData.Copy(src, dst);
        Fh5ProfileGameData.Copy(src, dst);
        Fh3ProfileGameData.Copy(src, dst);
#endif
    }

    static Bxml.Node MakeObjectNode(Bxml bx, string objectId, string objectType,
        params (string Name, string Type, string Value, bool FreshValue)[] props)
    {
        var obj = new Bxml.Node { Flags = 0x07, NameIndex = bx.EnsureString("Object") };
        obj.Attributes.Add(new Bxml.Attribute { KeyIndex = bx.EnsureString("Id"), ValueIndex = bx.EnsureString(objectId) });
        obj.Attributes.Add(new Bxml.Attribute { KeyIndex = bx.EnsureString("Type"), ValueIndex = bx.EnsureString(objectType) });
        foreach (var (name, type, value, fresh) in props)
        {
            var p = new Bxml.Node { Flags = 0x03, NameIndex = bx.EnsureString("Property") };
            p.Attributes.Add(new Bxml.Attribute { KeyIndex = bx.EnsureString("Name"), ValueIndex = bx.EnsureString(name) });
            p.Attributes.Add(new Bxml.Attribute { KeyIndex = bx.EnsureString("Type"), ValueIndex = bx.EnsureString(type) });
            p.Attributes.Add(new Bxml.Attribute { KeyIndex = bx.EnsureString("Value"), ValueIndex = fresh ? bx.AddFreshString(value) : bx.EnsureString(value) });
            obj.Children.Add(p);
        }
        return obj;
    }

    static void SetFreshAttrValue(Bxml bx, Bxml.Node node, string key, string value)
    {
        int idx = bx.AddFreshString(value);
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            if (bx.Strings[node.Attributes[i].KeyIndex] == key)
            {
                var attr = node.Attributes[i];
                attr.ValueIndex = idx;
                node.Attributes[i] = attr;
                bx.MarkDirty();
                return;
            }
        }
        bx.SetAttribute(node, key, value);
    }

    static bool IsObjectDictionary(Bxml bx)
        => bx.Root.NameIndex >= 0 && bx.Root.NameIndex < bx.Strings.Count && bx.Strings[bx.Root.NameIndex] == "ObjectDictionary";

    static Bxml.Node[] FindObjects(Bxml bx, string objectId)
        => bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "Object"
            && string.Equals(bx.GetAttribute(n, "Id"), objectId, StringComparison.Ordinal)).ToArray();

    static Bxml.Node? FindProperty(Bxml bx, Bxml.Node parent, string propName, string propType)
    {
        var props = parent.Children.Where(n => bx.Strings[n.NameIndex] == "Property"
            && bx.GetAttribute(n, "Name") == propName && bx.GetAttribute(n, "Type") == propType).ToArray();
        return props.Length == 1 && props[0].Children.Count == 0 ? props[0] : null;
    }

    static string? ReadObjectProperty(Bxml bx, string objectId, string objectType, string propName)
    {
        var matches = FindObjects(bx, objectId);
        if (matches.Length != 1 || bx.GetAttribute(matches[0], "Type") != objectType) return null;
        var prop = matches[0].Children.FirstOrDefault(n => bx.Strings[n.NameIndex] == "Property" && bx.GetAttribute(n, "Name") == propName);
        return prop == null ? null : bx.GetAttribute(prop, "Value");
    }

    static bool IsLegoBoardId(string id)
        => id.StartsWith("discount_board_xp_", StringComparison.Ordinal)
        && id.Contains("_red_", StringComparison.Ordinal)
        && id.EndsWith("_save_state", StringComparison.Ordinal);

    static bool IsLegoLandmarkId(string id, string? type)
        => id.StartsWith("Red_", StringComparison.Ordinal)
        && id.EndsWith("_save_state", StringComparison.Ordinal)
        && type == "LandmarkSaveState";

    static int ApplySmashLegoBoards(ProfileContainer container)
    {
        if (container.SaveState is not { } bx) return -1;
        int changed = 0;
        int allCollected = 0;
        foreach (var obj in bx.Root.Children)
        {
            if (bx.Strings[obj.NameIndex] != "Object") continue;
            string? id = bx.GetAttribute(obj, "Id");
            if (string.IsNullOrEmpty(id) || bx.GetAttribute(obj, "Type") != "DiscountBoardSaveState") continue;
            var prop = FindProperty(bx, obj, "State", "DiscountBoardState");
            if (prop == null) continue;

            if (IsLegoBoardId(id) && bx.GetAttribute(prop, "Value") != "Collected")
            {
                if (!bx.SetAttribute(prop, "Value", "Collected")) return -1;
                changed++;
            }
            if (bx.GetAttribute(prop, "Value") == "Collected") allCollected++;
        }

        if (changed > 0 && container.Properties != null)
        {
            var statNode = container.Properties.Walk().FirstOrDefault(p => p.Path == "/CompareStats/DiscountBoardsSmashed").Node;
            if (statNode != null && statNode.IsEditable && !statNode.IsBag
                && uint.TryParse(statNode.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint oldCount)
                && allCollected > oldCount)
            {
                statNode.TrySetFromText(allCollected.ToString(CultureInfo.InvariantCulture));
            }
        }
        return changed;
    }

    static int ApplyDiscoverLegoLandmarks(Bxml bx)
    {
        int changed = 0;
        foreach (var obj in bx.Root.Children)
        {
            if (bx.Strings[obj.NameIndex] != "Object") continue;
            string? id = bx.GetAttribute(obj, "Id");
            if (string.IsNullOrEmpty(id) || !IsLegoLandmarkId(id, bx.GetAttribute(obj, "Type"))) continue;
            var prop = FindProperty(bx, obj, "Activated", "bool");
            if (prop == null) continue;
            if (bx.GetAttribute(prop, "Value") != "true")
            {
                if (!bx.SetAttribute(prop, "Value", "true")) return -1;
                changed++;
            }
        }
        return changed;
    }
}

#if PROFILE_UNLOCK_TESTING
/// <summary>FH4 LEGO Speed Champions Brick Challenges (BrickChallengeSaveState + Bricks + Tiers + House).</summary>
public static class Fh4BrickChallengesState
{
    const string RecordName = "BrickChallengeSaveState";
    const string SerializerName = "BrickChallengeSaveState";

    public sealed record StatRecord(string Key, string ValueType, string Value)
    {
        public string Key { get; } = Key;
        public string ValueType { get; } = ValueType;
        public string Value { get; set; } = Value;
    }

    public sealed record ObjectiveRecord(uint ObjectiveId, byte Done, List<StatRecord> Stats)
    {
        public uint ObjectiveId { get; } = ObjectiveId;
        public byte Done { get; set; } = Done;
        public List<StatRecord> Stats { get; } = Stats;
    }

    public sealed record EntryRecord(uint Id, uint DataVersion, List<ObjectiveRecord> Objectives, uint ActiveObjectiveId)
    {
        public uint Id { get; } = Id;
        public uint DataVersion { get; } = DataVersion;
        public List<ObjectiveRecord> Objectives { get; } = Objectives;
        public uint ActiveObjectiveId { get; set; } = ActiveObjectiveId;
    }

    public sealed record Document(uint Version, List<EntryRecord> Entries);

    public sealed record Row(uint Id, int UnlockTier, string Category, string Difficulty, string Name, string Description,
        int Bricks, bool Completed, bool Supported, string Compatibility)
    {
        public string TierDisplay => $"Tier {UnlockTier}";
        public string CompletedDisplay => Completed ? "Yes" : "No";
    }

    public sealed record View(Row[] Rows, int SavedCount, int CatalogCount, int CompletedCount,
        int CompletedBricks, int SavedBricks, int SavedTiers, int HouseLevel,
        bool BricksExist, bool HouseExists, bool HasInitializedEntries, string StatusNote);

    public sealed record Change(int CompletedDelta, int ResetDelta, int PreviousBricks, int NewBricks,
        int PreviousTiers, int NewTiers, int PreviousHouseLevel, int NewHouseLevel, bool Changed);

    public static bool TryParse(byte[] payload, out Document? doc)
    {
        doc = null;
        if (payload == null || payload.Length < 8) return false;
        try
        {
            int off = 0;
            uint U32()
            {
                if (off > payload.Length - 4) throw new InvalidDataException();
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(off, 4));
                off += 4;
                return v;
            }
            int I32()
            {
                if (off > payload.Length - 4) throw new InvalidDataException();
                int v = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(off, 4));
                off += 4;
                return v;
            }
            byte U8()
            {
                if (off >= payload.Length) throw new InvalidDataException();
                return payload[off++];
            }
            string Str()
            {
                int len = I32();
                if (len < 0 || len > 4096 || off > payload.Length - len) throw new InvalidDataException();
                string s = Encoding.UTF8.GetString(payload, off, len);
                off += len;
                return s;
            }

            uint version = U32();
            uint count = U32();
            if (count > 2000) return false;

            var entries = new List<EntryRecord>((int)count);
            for (int i = 0; i < count; i++)
            {
                uint id = U32();
                uint dataVer = U32();
                uint objCount = U32();
                if (objCount > 64) return false;
                var objs = new List<ObjectiveRecord>((int)objCount);
                for (int j = 0; j < objCount; j++)
                {
                    uint oid = U32();
                    byte done = U8();
                    uint statCount = U32();
                    if (statCount > 256) return false;
                    var stats = new List<StatRecord>((int)statCount);
                    for (int k = 0; k < statCount; k++)
                        stats.Add(new StatRecord(Str(), Str(), Str()));
                    objs.Add(new ObjectiveRecord(oid, done, stats));
                }
                uint activeObj = U32();
                entries.Add(new EntryRecord(id, dataVer, objs, activeObj));
            }
            if (off != payload.Length) return false;
            doc = new Document(version, entries);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static byte[] Serialize(Document doc)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        void WriteStr(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }

        w.Write(doc.Version);
        w.Write((uint)doc.Entries.Count);
        foreach (var e in doc.Entries)
        {
            w.Write(e.Id);
            w.Write(e.DataVersion);
            w.Write((uint)e.Objectives.Count);
            foreach (var o in e.Objectives)
            {
                w.Write(o.ObjectiveId);
                w.Write(o.Done);
                w.Write((uint)o.Stats.Count);
                foreach (var st in o.Stats)
                {
                    WriteStr(st.Key);
                    WriteStr(st.ValueType);
                    WriteStr(st.Value);
                }
            }
            w.Write(e.ActiveObjectiveId);
        }
        return ms.ToArray();
    }

    public static bool TryRead(ProfileContainer? container, out View? view, out string reason)
    {
        view = null;
        if (!TryLoadDoc(container, out var data, out _, out var doc, out reason)) return false;

        if (!Fh4ExpansionsSaveState.TryReadCurrencyBucket(container, Fh4ExpansionsSaveState.BricksObjectId,
                Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType,
                out bool bricksExist, out int savedBricks, out _)
            || !Fh4ExpansionsSaveState.TryReadCurrencyBucket(container, Fh4ExpansionsSaveState.TiersObjectId,
                Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType,
                out _, out int savedTiers, out _)
            || !Fh4ExpansionsSaveState.TryReadHouseState(container, out bool houseExists, out _, out _, out int houseLevel))
        {
            reason = "FH4 LEGO Bricks, Tiers, or Master Builder's House state is not safely recognized.";
            return false;
        }

        var byId = doc!.Entries.ToDictionary(e => e.Id);
        var rows = new List<Row>(data!.BrickChallenges.Length);
        int completedCount = 0, completedBricks = 0;

        foreach (var item in data.BrickChallenges)
        {
            if (byId.TryGetValue(item.Id, out var entry))
            {
                bool supported = IsEntrySupported(entry, item);
                bool completed = IsEntryCompleted(entry);
                if (completed)
                {
                    completedCount++;
                    completedBricks += item.Bricks;
                }
                rows.Add(new(item.Id, item.UnlockTier, item.Category, item.Difficulty, item.Name, item.Description,
                    item.Bricks, completed, supported,
                    supported ? "Supported" : "Read-only: objective definition differs"));
            }
            else
            {
                rows.Add(new(item.Id, item.UnlockTier, item.Category, item.Difficulty, item.Name, item.Description,
                    item.Bricks, false, false, "Not initialized in save yet — visit LEGO Valley in-game"));
            }
        }

        int savedCount = doc.Entries.Count;
        string note = savedCount == 0
            ? "BrickChallengeSaveState has 0 entries in this save (LEGO Valley not visited yet). Direct Bricks, Tier, and Master Builder's House edits above still work, or visit LEGO Valley in-game to initialize the 272 Brick Challenges."
            : "";
        view = new(rows.ToArray(), savedCount, data.BrickChallenges.Length, completedCount, completedBricks,
            savedBricks, savedTiers, houseLevel, bricksExist, houseExists, savedCount > 0, note);
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

    public static bool TryPreviewSyncBricks(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), EditMode.SyncBricks, apply: false, out change, out reason);

    public static bool TrySyncBricks(ProfileContainer? container, out Change? change, out string reason)
        => TryExecute(container, Array.Empty<uint>(), EditMode.SyncBricks, apply: true, out change, out reason);

    enum EditMode { Complete, Reset, SyncBricks }

    static bool TryExecute(ProfileContainer? container, IEnumerable<uint> ids, EditMode mode, bool apply, out Change? change, out string reason)
    {
        change = null;
        if (!TryRead(container, out var beforeView, out reason)) return false;

        var selectedIds = ids?.Distinct().ToArray() ?? Array.Empty<uint>();
        if (mode != EditMode.SyncBricks && selectedIds.Length == 0)
        {
            reason = "Select at least one LEGO Brick Challenge.";
            return false;
        }

        var staged = ProfileContainer.Parse(container!.Serialize());
        Fh4ExpansionsSaveState.CopyCatalogSnapshots(container, staged);
        if (!TryLoadDoc(staged, out var data, out var stagedRecord, out var stagedDoc, out reason)) return false;

        var stagedById = stagedDoc!.Entries.ToDictionary(e => e.Id);
        int mutated = 0;
        int brickDelta = 0;

        if (mode != EditMode.SyncBricks)
        {
            foreach (uint id in selectedIds)
            {
                if (!data!.BrickChallengesById.TryGetValue(id, out var item) || !stagedById.TryGetValue(id, out var entry)
                    || !IsEntrySupported(entry, item))
                {
                    reason = $"LEGO Brick Challenge #{id} is missing from this save or unsupported.";
                    return false;
                }

                bool wasCompleted = IsEntryCompleted(entry);
                if (mode == EditMode.Complete)
                {
                    if (wasCompleted) continue;
                    for (int i = 0; i < entry.Objectives.Count; i++)
                    {
                        var obj = entry.Objectives[i];
                        var objDef = item.Objectives[i];
                        obj.Done = 1;
                        foreach (var st in obj.Stats)
                        {
                            long existing = long.TryParse(st.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ex) ? ex : 0;
                            long target = Math.Max(existing, objDef.TargetThreshold);
                            if (string.Equals(st.ValueType, "uint", StringComparison.OrdinalIgnoreCase) && target < 0) target = 0;
                            st.Value = target.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    entry.ActiveObjectiveId = 0;
                    brickDelta += item.Bricks;
                    mutated++;
                }
                else
                {
                    if (!wasCompleted) continue;
                    foreach (var obj in entry.Objectives)
                    {
                        obj.Done = 0;
                        foreach (var st in obj.Stats) st.Value = "0";
                    }
                    entry.ActiveObjectiveId = entry.Objectives.Count > 0 ? entry.Objectives[0].ObjectiveId : 0;
                    brickDelta -= item.Bricks;
                    mutated++;
                }
            }
        }

        int completedSum = 0;
        foreach (var item in data!.BrickChallenges)
        {
            if (stagedById.TryGetValue(item.Id, out var e) && IsEntryCompleted(e))
                completedSum += item.Bricks;
        }

        int newBricks = mode == EditMode.SyncBricks
            ? completedSum
            : Math.Max(0, beforeView!.SavedBricks + brickDelta);
        var (newTiers, newHouseLevel) = Fh4ExpansionsSaveState.ComputeTierAndHouseLevel(newBricks);

        bool needRecordWrite = mutated > 0;
        bool needBricksWrite = !beforeView!.BricksExist || beforeView.SavedBricks != newBricks;
        bool needTiersWrite = !beforeView.BricksExist || beforeView.SavedTiers != newTiers;
        bool needHouseWrite = !beforeView.HouseExists || beforeView.HouseLevel != newHouseLevel;

        if (!needRecordWrite && !needBricksWrite && !needTiersWrite && !needHouseWrite)
        {
            change = new(0, 0, beforeView.SavedBricks, newBricks, beforeView.SavedTiers, newTiers, beforeView.HouseLevel, newHouseLevel, false);
            reason = "";
            return true;
        }

        byte[]? editedPayload = null;
        if (needRecordWrite)
        {
            editedPayload = Serialize(stagedDoc);
            if (!TryParse(editedPayload, out _))
            {
                reason = "Edited BrickChallengeSaveState failed validation; nothing was changed.";
                return false;
            }
            staged.Career!.ReplacePayload(stagedRecord!, editedPayload);
        }

        if (needBricksWrite)
        {
            Fh4ExpansionsSaveState.TryReadCurrencyBucket(staged, Fh4ExpansionsSaveState.BricksObjectId,
                Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, out _, out _, out var node);
            Fh4ExpansionsSaveState.ApplyCurrencyBucketMutation(staged.SaveState!, node,
                Fh4ExpansionsSaveState.BricksObjectId, Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, newBricks);
        }
        if (needTiersWrite)
        {
            Fh4ExpansionsSaveState.TryReadCurrencyBucket(staged, Fh4ExpansionsSaveState.TiersObjectId,
                Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, out _, out _, out var node);
            Fh4ExpansionsSaveState.ApplyCurrencyBucketMutation(staged.SaveState!, node,
                Fh4ExpansionsSaveState.TiersObjectId, Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, newTiers);
        }
        if (needHouseWrite)
        {
            Fh4ExpansionsSaveState.ApplyHouseMutation(staged.SaveState!, newHouseLevel);
        }

        byte[] roundTripBytes = staged.Serialize();
        var verified = ProfileContainer.Parse(roundTripBytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(roundTripBytes))
        {
            reason = "Staged LEGO Brick Challenge edit failed container round-trip verification; nothing was changed.";
            return false;
        }
        Fh4ExpansionsSaveState.CopyCatalogSnapshots(container, verified);
        if (!TryRead(verified, out var afterView, out reason)
            || afterView!.SavedBricks != newBricks || afterView.SavedTiers != newTiers || afterView.HouseLevel != newHouseLevel)
        {
            reason = "Verified LEGO Valley state did not match expected Bricks/Tier/House values; nothing was changed.";
            return false;
        }

        change = new(
            mode == EditMode.Complete ? mutated : 0,
            mode == EditMode.Reset ? mutated : 0,
            beforeView.SavedBricks,
            newBricks,
            beforeView.SavedTiers,
            newTiers,
            beforeView.HouseLevel,
            newHouseLevel,
            true);

        if (!apply) return true;

        if (needRecordWrite)
        {
            Fh4CareerRecordHelper.TryFindRecord(container, RecordName, SerializerName, out var liveRecord);
            container.Career!.ReplacePayload(liveRecord!, editedPayload!);
        }
        if (needBricksWrite)
        {
            Fh4ExpansionsSaveState.TryReadCurrencyBucket(container, Fh4ExpansionsSaveState.BricksObjectId,
                Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, out _, out _, out var node);
            Fh4ExpansionsSaveState.ApplyCurrencyBucketMutation(container.SaveState!, node,
                Fh4ExpansionsSaveState.BricksObjectId, Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, newBricks);
        }
        if (needTiersWrite)
        {
            Fh4ExpansionsSaveState.TryReadCurrencyBucket(container, Fh4ExpansionsSaveState.TiersObjectId,
                Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, out _, out _, out var node);
            Fh4ExpansionsSaveState.ApplyCurrencyBucketMutation(container.SaveState!, node,
                Fh4ExpansionsSaveState.TiersObjectId, Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, newTiers);
        }
        if (needHouseWrite)
        {
            Fh4ExpansionsSaveState.ApplyHouseMutation(container.SaveState!, newHouseLevel);
        }
        return true;
    }

    static bool TryLoadDoc(ProfileContainer? container, out Fh4ProfileGameData.Snapshot? data,
        out BinaryCareer.Record? record, out Document? doc, out string reason)
    {
        data = Fh4ProfileGameData.For(container);
        record = null;
        doc = null;
        if (data == null)
        {
            reason = "FH4 game tables (TIDETables.zip) are not loaded for this profile.";
            return false;
        }
        if (!Fh4CareerRecordHelper.TryFindRecord(container, RecordName, SerializerName, out record))
        {
            reason = $"'{RecordName}' record is not safely recognized in this save.";
            return false;
        }
        if (!TryParse(record!.Payload, out doc))
        {
            reason = $"Could not parse '{RecordName}' binary data.";
            return false;
        }
        reason = "";
        return true;
    }

    static bool IsEntryCompleted(EntryRecord entry)
        => (entry.Objectives.Count > 0 && entry.Objectives.All(o => o.Done != 0)) || entry.ActiveObjectiveId == 0;

    static bool IsEntrySupported(EntryRecord entry, Fh4ProfileGameData.BrickChallengeItem def)
    {
        if (entry.Objectives.Count != def.Objectives.Length) return false;
        for (int i = 0; i < entry.Objectives.Count; i++)
        {
            var o = entry.Objectives[i];
            if (o.ObjectiveId != def.Objectives[i].Id) return false;
            foreach (var st in o.Stats)
            {
                if (!string.Equals(st.ValueType, "uint", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(st.ValueType, "int", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }
}
#endif
