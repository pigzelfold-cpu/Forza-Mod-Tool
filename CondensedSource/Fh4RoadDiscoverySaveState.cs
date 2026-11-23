#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Numerics;

namespace FH6LocalCryptoTool;

/// <summary>
/// FH4 version-1 road-discovery bitmap editing ([u32 version=1][u32 navSig][4096-byte bitmap]).
/// Sets verified road bits on Great Britain (UK_00.nav = 18,715), Fortune Island (Savage_00.nav = 2,686),
/// and LEGO Speed Champions (Red_00.nav = 2,907) when present, and updates /CompareStats/RoadsDiscovered (531).
/// </summary>
public static class Fh4RoadDiscoverySaveState
{
    public const string RecordName = "RoadDiscovery";
    public const string Ex1RecordName = "RoadDiscoveryEx1";
    public const string Ex2RecordName = "RoadDiscoveryEx2";
    public const string SerializerName = "RoadDiscoveryState";
    public const string StatisticPath = "/CompareStats/RoadsDiscovered";
    public const uint ExpectedVersion = 1u;
    public const uint UkNavSignature = Fh4ProfileHelper.UkNavSignature;
    public const uint Ex1NavSignature = 0x87F9C752u;
    public const uint Ex2NavSignature = 0xE8665F08u;
    const int HeaderBytes = 8;
    const int KnownPayloadBytes = 4104;

    public readonly record struct Info(int EntryCount, int DiscoveredEntries);
    public readonly record struct Summary(Info Main, uint Roads, Info? Ex1, Info? Ex2);

    public static bool TryRead(byte[]? payload, uint expectedSig, int validBits, out Info info)
    {
        info = default;
        if (payload is null || payload.Length != KnownPayloadBytes || validBits is < 500 or > (KnownPayloadBytes - HeaderBytes) * 8)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) != ExpectedVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)) != expectedSig)
            return false;

        int completeBytes = validBits / 8;
        int remainder = validBits % 8;
        int visited = 0;
        for (int i = 0; i < completeBytes; i++)
            visited += BitOperations.PopCount((uint)payload[HeaderBytes + i]);
        int tailStart = HeaderBytes + completeBytes;
        if (remainder > 0)
        {
            byte mask = (byte)((1 << remainder) - 1);
            if ((payload[tailStart] & ~mask) != 0) return false;
            visited += BitOperations.PopCount((uint)payload[tailStart]);
            tailStart++;
        }
        for (int i = tailStart; i < payload.Length; i++)
            if (payload[i] != 0) return false;
        info = new Info(validBits, visited);
        return true;
    }

    public static bool TryDiscoverAll(byte[]? payload, uint expectedSig, int validBits, out byte[] edited, out Info before)
    {
        edited = payload ?? Array.Empty<byte>();
        if (!TryRead(payload, expectedSig, validBits, out before)) return false;
        edited = (byte[])payload!.Clone();
        int completeBytes = validBits / 8;
        int remainder = validBits % 8;
        edited.AsSpan(HeaderBytes, completeBytes).Fill(0xFF);
        if (remainder > 0)
            edited[HeaderBytes + completeBytes] |= (byte)((1 << remainder) - 1);
        return TryRead(edited, expectedSig, validBits, out Info after) &&
               after.EntryCount == before.EntryCount &&
               after.DiscoveredEntries == after.EntryCount;
    }

    public static bool TryReadProfile(ProfileContainer? container, out Summary summary)
    {
        summary = default;
        if (!TryGetTargets(container, out _, out _, out _, out summary)) return false;
        return true;
    }

    public static bool TryDiscoverAll(ProfileContainer? container, out Summary before, out int expansionsUpdated)
    {
        expansionsUpdated = 0;
        if (!TryGetTargets(container, out var targets, out var statistic, out uint targetRoads, out before))
            return false;

        var editedRecords = new List<(BinaryCareer.Record Record, byte[] Edited, bool IsExpansion)>();
        foreach (var (record, sig, bits, isExp) in targets)
        {
            if (!TryDiscoverAll(record.Payload, sig, bits, out byte[] edited, out _)) return false;
            editedRecords.Add((record, edited, isExp));
        }

        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, targetRoads);
        foreach (var (record, edited, isExp) in editedRecords)
        {
            if (isExp && !record.Payload.AsSpan().SequenceEqual(edited)) expansionsUpdated++;
            container!.Career!.ReplacePayload(record, edited);
        }
        statistic!.RawValue = counter;
        statistic.Edited = true;
        return true;
    }

    static bool TryGetTargets(
        ProfileContainer? container,
        out List<(BinaryCareer.Record Record, uint Sig, int Bits, bool IsExpansion)> targets,
        out PropertyTree.PropertyNode? statistic,
        out uint targetRoads,
        out Summary summary)
    {
        targets = new();
        statistic = null;
        summary = default;
        var snap = Fh4ProfileGameData.For(container);
        targetRoads = snap?.RoadTargetCount ?? Fh4ProfileGameData.DefaultRoadTargetCount;
        if (container?.Career is not { } career || container.Properties is not { } properties) return false;

        var counters = properties.Walk().Where(p => string.Equals(p.Path, StatisticPath, StringComparison.Ordinal)).ToArray();
        if (counters.Length != 1) return false;
        var candidate = counters[0].Node;
        if (candidate.TypeId is not (3 or 17) || candidate.RawValue.Length != 4 || !candidate.IsEditable) return false;
        uint roads = BinaryPrimitives.ReadUInt32LittleEndian(candidate.RawValue);

        int mainBits = snap?.ValidRoadBits ?? Fh4ProfileGameData.DefaultValidRoadBits;
        var mainRecords = career.Records.Where(r => r.Name == RecordName).ToArray();
        if (mainRecords.Length != 1 || mainRecords[0].Serializer != SerializerName ||
            !TryRead(mainRecords[0].Payload, UkNavSignature, mainBits, out Info mainInfo))
            return false;
        var foundTargets = new List<(BinaryCareer.Record Record, uint Sig, int Bits, bool IsExpansion)>
        {
            (mainRecords[0], UkNavSignature, mainBits, false)
        };

        Info? ReadExpansion(string recordName, uint sig, int bits)
        {
            var recs = career.Records.Where(r => r.Name == recordName).ToArray();
            if (recs.Length != 1 || recs[0].Serializer != SerializerName) return null;
            if (!TryRead(recs[0].Payload, sig, bits, out Info info)) return null;
            foundTargets.Add((recs[0], sig, bits, true));
            return info;
        }

        Info? ex1 = ReadExpansion(Ex1RecordName, Ex1NavSignature, snap?.Ex1ValidRoadBits ?? Fh4ProfileGameData.DefaultEx1ValidRoadBits);
        Info? ex2 = ReadExpansion(Ex2RecordName, Ex2NavSignature, snap?.Ex2ValidRoadBits ?? Fh4ProfileGameData.DefaultEx2ValidRoadBits);

        targets = foundTargets;
        statistic = candidate;
        summary = new Summary(mainInfo, roads, ex1, ex2);
        return true;
    }
}
#endif
