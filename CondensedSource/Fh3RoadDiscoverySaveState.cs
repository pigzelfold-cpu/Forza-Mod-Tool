#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>
/// FH3 version-1 road-discovery bitmap editing ([u32 version=1][u32 navSig][4096-byte bitmap]).
/// Sets verified road bits on Australia (australia_00.nav = 17,422), Blizzard Mountain (mountain_00.nav = 2,061),
/// and Hot Wheels (island_00.nav = 5,408) when present, and updates /CompareStats/RoadsDiscovered (488).
/// </summary>
public static class Fh3RoadDiscoverySaveState
{
    public const string RecordName = "RoadDiscovery";
    public const string MountainRecordName = "RoadDiscoveryMountain";
    public const string OpusIslandRecordName = "RoadDiscoveryOpusIsland";
    public const string SerializerName = "RoadDiscoveryState";
    public const string StatisticPath = "/CompareStats/RoadsDiscovered";

    public readonly record struct Summary(
        Fh4RoadDiscoverySaveState.Info Main,
        uint Roads,
        Fh4RoadDiscoverySaveState.Info? Mountain,
        Fh4RoadDiscoverySaveState.Info? OpusIsland);

    public static bool TryReadProfile(ProfileContainer? container, out Summary summary)
    {
        summary = default;
        return TryGetTargets(container, out _, out _, out _, out summary);
    }

    public static bool TryDiscoverAll(ProfileContainer? container, out Summary before, out int expansionsUpdated)
    {
        expansionsUpdated = 0;
        if (!TryGetTargets(container, out var targets, out var statistic, out uint targetRoads, out before))
            return false;

        var editedRecords = new List<(BinaryCareer.Record Record, byte[] Edited, bool IsExpansion)>();
        foreach (var (record, sig, bits, isExp) in targets)
        {
            if (!Fh4RoadDiscoverySaveState.TryDiscoverAll(record.Payload, sig, bits, out byte[] edited, out _))
                return false;
            editedRecords.Add((record, edited, isExp));
        }

        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, targetRoads);
        foreach (var (record, edited, isExp) in editedRecords)
        {
            if (isExp && !record.Payload.AsSpan().SequenceEqual(edited))
                expansionsUpdated++;
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
        var snap = Fh3ProfileGameData.ForOrEmbedded(container);
        targetRoads = snap.RoadTargetCount;
        if (container?.Career is not { } career || container.Properties is not { } properties)
            return false;

        var counters = properties.Walk().Where(p => string.Equals(p.Path, StatisticPath, StringComparison.Ordinal)).ToArray();
        if (counters.Length != 1) return false;
        var candidate = counters[0].Node;
        if (candidate.TypeId is not (3 or 17) || candidate.RawValue.Length != 4 || !candidate.IsEditable) return false;
        uint roads = BinaryPrimitives.ReadUInt32LittleEndian(candidate.RawValue);

        int mainBits = snap.ValidRoadBits;
        var mainRecords = career.Records.Where(r => r.Name == RecordName).ToArray();
        if (mainRecords.Length != 1 || mainRecords[0].Serializer != SerializerName ||
            !Fh4RoadDiscoverySaveState.TryRead(mainRecords[0].Payload, Fh3ProfileHelper.AustraliaNavSignature, mainBits, out var mainInfo))
            return false;

        var foundTargets = new List<(BinaryCareer.Record Record, uint Sig, int Bits, bool IsExpansion)>
        {
            (mainRecords[0], Fh3ProfileHelper.AustraliaNavSignature, mainBits, false)
        };

        Fh4RoadDiscoverySaveState.Info? ReadExpansion(string recordName, uint sig, int bits)
        {
            var recs = career.Records.Where(r => r.Name == recordName).ToArray();
            if (recs.Length != 1 || recs[0].Serializer != SerializerName) return null;
            if (!Fh4RoadDiscoverySaveState.TryRead(recs[0].Payload, sig, bits, out var info)) return null;
            foundTargets.Add((recs[0], sig, bits, true));
            return info;
        }

        var mountain = ReadExpansion(MountainRecordName, Fh3ProfileHelper.MountainNavSignature, snap.MountainValidRoadBits);
        var opusIsland = ReadExpansion(OpusIslandRecordName, Fh3ProfileHelper.OpusIslandNavSignature, snap.OpusIslandValidRoadBits);

        targets = foundTargets;
        statistic = candidate;
        summary = new Summary(mainInfo, roads, mountain, opusIsland);
        return true;
    }
}
#endif
