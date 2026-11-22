#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>Shared parser, validator and writer for FH5 ChallengeSaveDataBase binary payloads (Badges and Accolades).</summary>
internal static class Fh5ChallengeSaveCodec
{
    const int MaxEntries = 20_000;
    const int MaxObjectivesPerEntry = 64;
    const int MaxStringBytes = 256;

    internal sealed class ObjectiveRecord
    {
        public uint ObjectiveId { get; init; }
        public byte Done { get; set; }
        public string ValueType { get; init; } = "";
        public string Value { get; set; } = "";
    }

    internal sealed class EntryRecord
    {
        public uint Id { get; init; }
        public uint ChallengeDataId { get; init; }
        public uint DataVersion { get; init; }
        public List<ObjectiveRecord> Objectives { get; init; } = new();
        public byte UnlockedFlag { get; set; }
        public uint ActiveObjectiveId { get; set; }
        public ulong CompletionCount { get; set; }
        public ulong UtcMicroseconds { get; set; }
        public uint PreservedFooter { get; init; }
    }

    internal sealed class SaveDocument
    {
        public uint Version { get; init; }
        public List<EntryRecord> Entries { get; init; } = new();
        public byte[] Trailing { get; init; } = Array.Empty<byte>();
    }

    internal static bool TryParse(byte[]? bytes, bool allowBadgeTrailing, out SaveDocument? doc)
    {
        doc = null;
        if (bytes is null || bytes.Length < 8) return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (version != 0) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (count > MaxEntries) return false;

        int pos = 8;
        var entries = new List<EntryRecord>((int)count);
        var seen = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            if (pos + 20 > bytes.Length) return false;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            uint id2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            uint chDataId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 8, 4));
            uint dataVer = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 12, 4));
            uint objCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 16, 4));
            pos += 20;

            if (id != id2 || !seen.Add(id) || objCount > MaxObjectivesPerEntry) return false;
            var objs = new List<ObjectiveRecord>((int)objCount);
            for (int j = 0; j < objCount; j++)
            {
                if (pos + 9 > bytes.Length) return false;
                uint objId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
                byte done = bytes[pos + 4];
                int tLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 5, 4));
                pos += 9;
                if (done > 1 || tLen < 0 || tLen > MaxStringBytes || pos + tLen + 4 > bytes.Length) return false;
                string valueType = Encoding.ASCII.GetString(bytes, pos, tLen);
                pos += tLen;
                int vLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos, 4));
                pos += 4;
                if (vLen < 0 || vLen > MaxStringBytes || pos + vLen > bytes.Length) return false;
                string value = Encoding.ASCII.GetString(bytes, pos, vLen);
                pos += vLen;
                objs.Add(new ObjectiveRecord { ObjectiveId = objId, Done = done, ValueType = valueType, Value = value });
            }

            if (pos + 25 > bytes.Length) return false;
            byte unlocked = bytes[pos];
            uint activeObj = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 1, 4));
            ulong compCount = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos + 5, 8));
            ulong utcUs = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos + 13, 8));
            uint footer = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 21, 4));
            pos += 25;

            if (unlocked > 1) return false;
            entries.Add(new EntryRecord
            {
                Id = id,
                ChallengeDataId = chDataId,
                DataVersion = dataVer,
                Objectives = objs,
                UnlockedFlag = unlocked,
                ActiveObjectiveId = activeObj,
                CompletionCount = compCount,
                UtcMicroseconds = utcUs,
                PreservedFooter = footer,
            });
        }

        byte[] trailing;
        if (pos == bytes.Length)
        {
            trailing = Array.Empty<byte>();
        }
        else if (allowBadgeTrailing && bytes.Length - pos >= 4)
        {
            uint tailCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            if (tailCount > MaxEntries || bytes.Length != pos + 4L + tailCount * 4L) return false;
            trailing = bytes[pos..];
        }
        else
        {
            return false;
        }

        var candidate = new SaveDocument { Version = version, Entries = entries, Trailing = trailing };
        if (!Serialize(candidate).AsSpan().SequenceEqual(bytes)) return false;
        doc = candidate;
        return true;
    }

    internal static byte[] Serialize(SaveDocument doc)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(doc.Version);
        bw.Write((uint)doc.Entries.Count);
        foreach (var e in doc.Entries)
        {
            bw.Write(e.Id);
            bw.Write(e.Id);
            bw.Write(e.ChallengeDataId);
            bw.Write(e.DataVersion);
            bw.Write((uint)e.Objectives.Count);
            foreach (var obj in e.Objectives)
            {
                bw.Write(obj.ObjectiveId);
                bw.Write(obj.Done);
                byte[] tb = Encoding.ASCII.GetBytes(obj.ValueType);
                bw.Write(tb.Length);
                bw.Write(tb);
                byte[] vb = Encoding.ASCII.GetBytes(obj.Value);
                bw.Write(vb.Length);
                bw.Write(vb);
            }
            bw.Write(e.UnlockedFlag);
            bw.Write(e.ActiveObjectiveId);
            bw.Write(e.CompletionCount);
            bw.Write(e.UtcMicroseconds);
            bw.Write(e.PreservedFooter);
        }
        if (doc.Trailing.Length > 0) bw.Write(doc.Trailing);
        bw.Flush();
        return ms.ToArray();
    }

    internal static ulong EncodeDate(DateTimeOffset utc)
        => checked((ulong)((utc.ToUniversalTime().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10));

    internal static DateTimeOffset? DecodeDate(ulong raw)
    {
        if (raw == 0 || raw > long.MaxValue / 10) return null;
        try { return DateTimeOffset.UnixEpoch.AddTicks(checked((long)(raw * 10))); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static bool IsTypeSupported(string valueType)
        => valueType is "int" or "uint" or "u32" or "float" or "ulong";

    internal static bool TryComputeCompletedValue(
        string valueType,
        string currentValue,
        Fh5ProfileGameData.ChallengeObjectiveDef? def,
        out string result)
    {
        result = currentValue;
        if (!IsTypeSupported(valueType)) return false;

        string cmp = def?.ThresholdComparison ?? "GreaterThanOrEqual";
        if (string.IsNullOrEmpty(cmp)) cmp = "GreaterThanOrEqual";
        string thresholdStr = def?.ThresholdValue ?? "";

        if (!double.TryParse(currentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double cur))
            return false;

        double target;
        if (string.IsNullOrWhiteSpace(thresholdStr))
        {
            target = Math.Max(cur, 1.0);
        }
        else if (!double.TryParse(thresholdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        {
            return false;
        }

        bool alreadySatisfied = cmp switch
        {
            "GreaterThanOrEqual" => cur >= target,
            "Equal" => Math.Abs(cur - target) < 1e-6,
            "LessThanOrEqual" => cur <= target,
            "NotEqual" => Math.Abs(cur - target) >= 1e-6,
            _ => false,
        };
        if (alreadySatisfied)
        {
            result = currentValue;
            return true;
        }

        double chosen = cmp switch
        {
            "NotEqual" => target == 0 ? 1.0 : 0.0,
            _ => target,
        };

        return TryFormatForType(valueType, chosen, cmp, thresholdStr, out result);
    }

    internal static bool TryComputeResetValue(
        string valueType,
        Fh5ProfileGameData.ChallengeObjectiveDef? def,
        out string result)
    {
        result = "0";
        if (!IsTypeSupported(valueType)) return false;
        string cmp = def?.ThresholdComparison ?? "GreaterThanOrEqual";
        string thresholdStr = def?.ThresholdValue ?? "";
        if (cmp == "LessThanOrEqual" && double.TryParse(thresholdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
            return TryFormatForType(valueType, t + 1.0, "GreaterThanOrEqual", null, out result);
        return true;
    }

    static bool TryFormatForType(string valueType, double val, string cmp, string? rawThreshold, out string formatted)
    {
        formatted = "";
        switch (valueType)
        {
            case "int":
            {
                double rounded = cmp == "GreaterThanOrEqual" ? Math.Ceiling(val) : cmp == "LessThanOrEqual" ? Math.Floor(val) : Math.Round(val);
                if (rounded < int.MinValue || rounded > int.MaxValue) return false;
                formatted = ((int)rounded).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case "uint":
            case "u32":
            {
                double rounded = cmp == "GreaterThanOrEqual" ? Math.Ceiling(val) : cmp == "LessThanOrEqual" ? Math.Floor(val) : Math.Round(val);
                if (rounded < 0 || rounded > uint.MaxValue) return false;
                formatted = ((uint)rounded).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case "ulong":
            {
                double rounded = cmp == "GreaterThanOrEqual" ? Math.Ceiling(val) : cmp == "LessThanOrEqual" ? Math.Floor(val) : Math.Round(val);
                if (rounded < 0 || rounded > ulong.MaxValue) return false;
                formatted = ((ulong)rounded).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case "float":
            {
                if (!string.IsNullOrEmpty(rawThreshold) && cmp is "GreaterThanOrEqual" or "Equal" or "LessThanOrEqual")
                {
                    if (double.TryParse(rawThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && Math.Abs(parsed - val) < 1e-6)
                    {
                        formatted = rawThreshold.Trim();
                        return true;
                    }
                }
                formatted = ((float)val).ToString("G9", CultureInfo.InvariantCulture);
                return true;
            }
            default:
                return false;
        }
    }
}
#endif
