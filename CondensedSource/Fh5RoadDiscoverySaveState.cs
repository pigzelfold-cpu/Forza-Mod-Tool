#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Numerics;

namespace FH6LocalCryptoTool;

/// <summary>
/// FH5 version-2 road-discovery bitmap editing ([u32 version=2][u32 navSig=0xF14FAE9E][4096-byte bitmap]).
/// Sets only the verified Hendrix_00.nav bit range (24,498 bits) and updates /CompareStats/RoadsDiscovered (578).
/// </summary>
public static class Fh5RoadDiscoverySaveState
{
    public const string RecordName = "RoadDiscovery";
    public const string SerializerName = "RoadDiscoveryState";
    public const string StatisticPath = "/CompareStats/RoadsDiscovered";
    public const uint NavSignature = 0xF14FAE9Eu;
    const int HeaderBytes = 8;
    const int KnownPayloadBytes = 4104;

    public readonly record struct Info(int EntryCount, int DiscoveredEntries);

    public static bool TryRead(byte[]? payload, out Info info)
        => TryRead(payload, Fh5ProfileGameData.DefaultValidRoadBits, out info);

    public static bool TryRead(byte[]? payload, int validBits, out Info info)
    {
        info = default;
        if (payload is null || payload.Length != KnownPayloadBytes || validBits is < 1000 or > (KnownPayloadBytes - HeaderBytes) * 8)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)) != NavSignature)
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

    public static bool TryDiscoverAll(byte[]? payload, int validBits, out byte[] edited, out Info before)
    {
        edited = payload ?? Array.Empty<byte>();
        if (!TryRead(payload, validBits, out before)) return false;
        edited = (byte[])payload!.Clone();
        int completeBytes = validBits / 8;
        int remainder = validBits % 8;
        edited.AsSpan(HeaderBytes, completeBytes).Fill(0xFF);
        if (remainder > 0)
            edited[HeaderBytes + completeBytes] |= (byte)((1 << remainder) - 1);
        return TryRead(edited, validBits, out Info after) &&
               after.EntryCount == before.EntryCount &&
               after.DiscoveredEntries == after.EntryCount;
    }

    public static bool TryReadProfile(ProfileContainer? container, out Info info, out uint roads)
        => TryGetTargets(container, out _, out _, out info, out roads);

    public static bool TryDiscoverAll(ProfileContainer? container, out Info before, out uint previousRoads)
    {
        if (!TryGetTargets(container, out var record, out var statistic, out before, out previousRoads))
            return false;
        var snap = Fh5ProfileGameData.For(container);
        int validBits = snap?.ValidRoadBits ?? Fh5ProfileGameData.DefaultValidRoadBits;
        uint targetRoads = snap?.RoadTargetCount ?? Fh5ProfileGameData.DefaultRoadTargetCount;
        if (!TryDiscoverAll(record!.Payload, validBits, out byte[] edited, out _)) return false;

        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, targetRoads);
        container!.Career!.ReplacePayload(record, edited);
        statistic!.RawValue = counter;
        statistic.Edited = true;
        return true;
    }

    static bool TryGetTargets(ProfileContainer? container, out BinaryCareer.Record? record,
        out PropertyTree.PropertyNode? statistic, out Info info, out uint roads)
    {
        record = null; statistic = null; info = default; roads = 0;
        if (container?.Career is not { } career || container.Properties is not { } properties) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray();
        var counters = properties.Walk().Where(p => string.Equals(p.Path, StatisticPath, StringComparison.Ordinal)).ToArray();
        if (records.Length != 1 || counters.Length != 1) return false;
        var candidate = counters[0].Node;
        int validBits = Fh5ProfileGameData.For(container)?.ValidRoadBits ?? Fh5ProfileGameData.DefaultValidRoadBits;
        if (records[0].Serializer != SerializerName || !TryRead(records[0].Payload, validBits, out info) ||
            candidate.TypeId is not (3 or 17) || candidate.RawValue.Length != 4 || !candidate.IsEditable)
            return false;
        roads = BinaryPrimitives.ReadUInt32LittleEndian(candidate.RawValue);
        record = records[0];
        statistic = candidate;
        return true;
    }
}
#endif
