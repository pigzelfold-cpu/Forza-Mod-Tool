using System.IO;

namespace FH6LocalCryptoTool;

public static partial class PlaylistPointsState
{
    sealed record ActivityProgress(ulong Id, float[] Values);
    sealed record ActivityGroup(uint Type, List<ActivityProgress> Entries);
    sealed record SeasonProgress(uint Index, List<ActivityGroup> Groups);
    sealed record SeriesProgress(uint Series, List<SeasonProgress> Seasons);
    sealed record MaximumEntry(uint Series, ulong Token, int Maximum, List<KeyValuePair<int, int>> TrackList, Dictionary<int, int> Tracks);
    sealed record ActivityState(int Start, int End, List<SeriesProgress> Series, List<MaximumEntry> MaximaEntries, Dictionary<uint, MaximumEntry> Maxima);

    static bool TryActivityMaps(byte[] bytes, int contentOffset, Dictionary<uint, Score> scores, uint version, out ActivityState? result)
    {
        result = null;
        try
        {
            var r = new Reader(bytes) { Offset = contentOffset };
            ulong U64() { ulong value = r.U32(); return value | (ulong)r.U32() << 32; }
            uint count = r.Count(MaxSeries); var maximaEntries = new List<MaximumEntry>(); var maxima = new Dictionary<uint, MaximumEntry>();
            for (int i = 0; i < count; i++)
            {
                uint series = r.U32(); ulong token = U64(); int maximum = r.Score().Value;
                uint n = r.Count(64); var trackList = new List<KeyValuePair<int, int>>(); var tracks = new Dictionary<int, int>();
                for (int j = 0; j < n; j++)
                {
                    int track = unchecked((int)r.U32());
                    int value = r.Score().Value;
                    if (!tracks.TryAdd(track, value)) return false;
                    trackList.Add(new(track, value));
                }
                // Maxima may already include a Series the player has never
                // opened, before any score/activity maps exist for it.
                // Historical v8 saves also carry Pre-Season (Series 0) with no tracks.
                bool validTracks = tracks.Values.Sum(v => (long)v) == maximum || (version == 8 && series == 0 && tracks.Count == 0);
                var entry = new MaximumEntry(series, token, maximum, trackList, tracks);
                if (!validTracks || !maxima.TryAdd(series, entry)) return false;
                maximaEntries.Add(entry);
            }
            // Verified v2/v4/v5 saves have an empty reserved Series-wide map.
            // Do not assume a new populated layout has the same season grammar.
            if (r.U32() != 0) return false;
            int start = r.Offset; count = r.Count(MaxSeries); var seriesProgress = new List<SeriesProgress>(); var seriesIds = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint series = r.U32();
                bool validActivitySeries = scores.ContainsKey(series) || (version == 8 && maxima.ContainsKey(series));
                if (!validActivitySeries || !seriesIds.Add(series)) return false;
                uint seasons = r.Count(4); var values = new List<SeasonProgress>(); var seasonIds = new HashSet<uint>();
                for (int j = 0; j < seasons; j++)
                {
                    uint season = r.U32(); if (season > 3 || !seasonIds.Add(season)) return false;
                    uint groups = r.Count(128); var list = new List<ActivityGroup>(); var types = new HashSet<uint>();
                    for (int k = 0; k < groups; k++)
                    {
                        uint type = r.U32(), events = r.Count(4096); if (type > 127 || !types.Add(type)) return false;
                        var entries = new List<ActivityProgress>(); var ids = new HashSet<ulong>();
                        for (int l = 0; l < events; l++)
                        {
                            ulong id = version == 8 ? r.U32() : U64(); uint steps = r.Count(64); if ((id == 0 && version != 8) || steps == 0 || !ids.Add(id)) return false;
                            var progress = new float[steps];
                            for (int n = 0; n < steps; n++) { float value = BitConverter.UInt32BitsToSingle(r.U32()); if (!float.IsFinite(value) || value < 0 || value > 1) return false; progress[n] = value; }
                            entries.Add(new(id, progress));
                        }
                        list.Add(new(type, entries));
                    }
                    values.Add(new(season, list));
                }
                seriesProgress.Add(new(series, values));
            }
            result = new(start, r.Offset, seriesProgress, maximaEntries, maxima); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    static void WriteMaxima(BinaryWriter w, List<MaximumEntry> entries)
    {
        w.Write((uint)entries.Count);
        foreach (var entry in entries)
        {
            w.Write(entry.Series);
            w.Write(entry.Token);
            w.Write(entry.Maximum);
            w.Write((uint)entry.TrackList.Count);
            foreach (var track in entry.TrackList)
            {
                w.Write(track.Key);
                w.Write(track.Value);
            }
        }
        w.Write(0u);
    }
}
