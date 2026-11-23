using System.Buffers.Binary;
using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>Versioned FestivalPass score maps; never completes events or claims rewards.</summary>
public static partial class PlaylistPointsState
{
    const string Name = "FestivalPassSaveState";
    const int BaseTrack = 820, MaxSeries = 4096, MaxBytes = 2_000_000;
    public sealed record Season(uint Series, uint Index, string Name, int Points, int SeriesPoints)
    {
        public string Display => $"Series {Series} · {Name}";
    }
    sealed record Score(int Value, int Offset);
    sealed record Track(int Id, int Points);
    sealed record TrackSeries(uint Id, List<Track> Tracks);
    sealed record Tail(int Offset, List<TrackSeries> Series, byte[] Metadata, int LifetimeOffset, int Lifetime);
    sealed record State(uint Version, Dictionary<uint, Score> Series, Dictionary<(uint Series, uint Season), Score> Seasons, Tail Tail, int ContentOffset,
        int ScoreMapsOffset, List<uint> SeasonRoots, int BaseTrack, HashSet<uint> RewardSeries);

    public static bool TryRead(ProfileContainer? container, out Season[] seasons, out string reason)
    {
        seasons = Array.Empty<Season>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason))
        { reason = "Playlist score maps are not recognized; saved activity and rewards are preserved."; return false; }
        seasons = state!.Seasons.OrderByDescending(p => p.Key.Series).ThenBy(p => p.Key.Season)
            .Select(p => new Season(p.Key.Series, p.Key.Season, new[] { "Summer", "Autumn", "Winter", "Spring" }[p.Key.Season], p.Value.Value, state.Series[p.Key.Series].Value)).ToArray();
        reason = ""; return true;
    }

    public static bool TrySet(ProfileContainer? container, uint series, uint season, int points, out bool changed, out string reason)
    {
        changed = false; reason = "Playlist score maps are not recognized; nothing was changed.";
        if (points < 0 || !TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason)) return false;
        if (state!.Version == 8) return TrySetV8(container!, record!, state, series, season, points, out changed, out reason);
        if (!state.Seasons.TryGetValue((series, season), out var old)) return false;
        long delta = (long)points - old.Value;
        if (delta == 0) { reason = ""; return true; }
        if (!TrackScoresMatch(state, series))
        { reason = "This Series has different saved point totals. Use Complete playlist with matching game data to rebuild them. Nothing was changed."; return false; }
        int baseTrack = state.BaseTrack;
        long nextSeries = state.Series[series].Value + delta, lifetime = state.Tail.Lifetime + delta;
        var target = state.Tail.Series.SingleOrDefault(s => s.Id == series);
        int index = target?.Tracks.FindIndex(t => t.Id == baseTrack) ?? -1;
        long nextTrack = (index < 0 ? 0 : target!.Tracks[index].Points) + delta;
        if (nextSeries is < 0 or > int.MaxValue || lifetime is < 0 or > int.MaxValue || nextTrack is < 0 or > int.MaxValue)
        { reason = "That score would overflow a total or remove points belonging to another track. Nothing was changed."; return false; }
        byte[] prefix = record!.Payload[..state.Tail.Offset];
        Put(prefix, old.Offset, points); Put(prefix, state.Series[series].Offset, (int)nextSeries);
        var trackSeries = state.Tail.Series.Select(s => new TrackSeries(s.Id, s.Tracks.ToList())).ToList();
        target = trackSeries.SingleOrDefault(s => s.Id == series);
        if (target == null) { target = new(series, new()); trackSeries.Add(target); }
        if (index < 0) target.Tracks.Add(new(baseTrack, (int)nextTrack));
        else target.Tracks[index] = new(baseTrack, (int)nextTrack);
        if (nextTrack == 0)
        {
            // A zero contribution is represented by an absent map entry in
            // naturally uninitialized saves. Keeping a synthetic empty Series
            // can otherwise make the surrounding zero-valued suffix ambiguous.
            target.Tracks.RemoveAll(t => t.Id == baseTrack);
            if (target.Tracks.Count == 0) trackSeries.Remove(target);
        }
        using var output = new MemoryStream(); using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(prefix);
            WriteTrackSeries(writer, trackSeries);
            byte[] metadata = (byte[])state.Tail.Metadata.Clone(); Put(metadata, state.Tail.LifetimeOffset, (int)lifetime);
            writer.Write(metadata);
        }
        byte[] edited = output.ToArray();
        if (!TryParse(edited, out var verified, out reason) || verified!.Seasons[(series, season)].Value != points
            || verified.Series[series].Value != nextSeries || verified.Tail.Lifetime != lifetime) return false;
        if (!CommitEditedPayload(container!, record!, edited)) return false;
        changed = true; reason = ""; return true;
    }

    static bool TrySetV8(ProfileContainer container, BinaryCareer.Record record, State state, uint series, uint season, int points, out bool changed, out string reason)
    {
        changed = false; reason = "Playlist score maps are not recognized; nothing was changed.";
        if (series == 0 || season > 3 || points < 0) return false;
        if (state.Seasons.TryGetValue((series, season), out var old))
        {
            long delta = (long)points - old.Value;
            if (delta == 0) { reason = ""; return true; }
            if (!TrackScoresMatch(state, series))
            { reason = "This Series has different saved point totals. Use Complete playlist with matching game data to rebuild them. Nothing was changed."; return false; }
            long nextSeries = state.Series[series].Value + delta;
            if (nextSeries is < 0 or > int.MaxValue)
            { reason = "That score would overflow a total or remove points belonging to another track. Nothing was changed."; return false; }
            byte[] prefix = record.Payload[..state.Tail.Offset];
            Put(prefix, old.Offset, points); Put(prefix, state.Series[series].Offset, (int)nextSeries);
            var trackSeries = AdjustTracksV8(state, series, delta, nextSeries);
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
            {
                writer.Write(prefix);
                WriteTrackSeries(writer, trackSeries);
            }
            byte[] edited = output.ToArray();
            if (!TryParse(edited, out var verified, out reason) || verified!.Seasons[(series, season)].Value != points
                || verified.Series[series].Value != nextSeries) return false;
            if (!CommitEditedPayload(container, record, edited)) return false;
            changed = true; reason = ""; return true;
        }
        else
        {
            if (points == 0 && !state.Series.ContainsKey(series)) { reason = ""; return true; }
            if (!TryActivityMaps(record.Payload, state.ContentOffset, state.Series, state.Version, out var activityState)) return false;
            int? defMax = GetFh5SeriesMaximum(container, series);
            if (!state.RewardSeries.Contains(series) && !activityState!.Maxima.ContainsKey(series) && defMax == null) return false;
            if (state.Series.ContainsKey(series) && !TrackScoresMatch(state, series))
            { reason = "This Series has different saved point totals. Use Complete playlist with matching game data to rebuild them. Nothing was changed."; return false; }
            long oldSeries = state.Series.GetValueOrDefault(series)?.Value ?? 0;
            long nextSeries = oldSeries + points;
            if (nextSeries is < 0 or > int.MaxValue)
            { reason = "That score would overflow a total or remove points belonging to another track. Nothing was changed."; return false; }
            var targetSeasons = Enumerable.Range(0, 4).Select(i =>
            {
                uint idx = (uint)i;
                int val = idx == season ? points : (state.Seasons.TryGetValue((series, idx), out var ex) ? ex.Value : 0);
                return (idx, val);
            }).ToArray();
            var trackSeries = AdjustTracksV8(state, series, points, nextSeries);
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
            {
                WriteScoreMaps(writer, record.Payload, state, series, (int)nextSeries, targetSeasons);
                if (activityState!.Maxima.ContainsKey(series))
                {
                    writer.Write(record.Payload.AsSpan(state.ContentOffset, activityState.Start - state.ContentOffset));
                }
                else
                {
                    int maxPts = defMax ?? Math.Max((int)nextSeries, 260);
                    ulong token = activityState.MaximaEntries.LastOrDefault()?.Token ?? 0UL;
                    var nextMaxima = activityState.MaximaEntries.ToList();
                    nextMaxima.Add(new MaximumEntry(series, token, maxPts, new() { new(state.BaseTrack, maxPts) }, new() { [state.BaseTrack] = maxPts }));
                    WriteMaxima(writer, nextMaxima);
                }
                writer.Write(record.Payload.AsSpan(activityState.Start, state.Tail.Offset - activityState.Start));
                WriteTrackSeries(writer, trackSeries);
            }
            byte[] edited = output.ToArray();
            if (!TryParse(edited, out var verified, out reason) || verified!.Seasons[(series, season)].Value != points
                || verified.Series[series].Value != nextSeries) return false;
            if (!CommitEditedPayload(container, record, edited)) return false;
            changed = true; reason = ""; return true;
        }
    }

    static List<TrackSeries> AdjustTracksV8(State state, uint series, long delta, long nextSeries)
    {
        var trackSeries = state.Tail.Series.Select(s => new TrackSeries(s.Id, s.Tracks.ToList())).ToList();
        var target = trackSeries.SingleOrDefault(s => s.Id == series);
        if (nextSeries == 0)
        {
            if (target != null) trackSeries.Remove(target);
            return trackSeries;
        }
        if (target == null)
        {
            trackSeries.Add(new(series, new() { new(state.BaseTrack, (int)nextSeries) }));
            return trackSeries;
        }
        if (delta > 0)
        {
            int idx = target.Tracks.FindIndex(t => t.Id == state.BaseTrack);
            if (idx < 0) target.Tracks.Add(new(state.BaseTrack, (int)delta));
            else target.Tracks[idx] = new(state.BaseTrack, checked((int)(target.Tracks[idx].Points + delta)));
        }
        else if (delta < 0)
        {
            long rem = -delta;
            var order = new List<int>();
            int baseIdx = target.Tracks.FindIndex(t => t.Id == state.BaseTrack);
            if (baseIdx >= 0) order.Add(baseIdx);
            int negIdx = target.Tracks.FindIndex(t => t.Id == -1);
            if (negIdx >= 0 && !order.Contains(negIdx)) order.Add(negIdx);
            for (int i = 0; i < target.Tracks.Count; i++) if (!order.Contains(i)) order.Add(i);
            foreach (int idx in order)
            {
                if (rem <= 0) break;
                int avail = target.Tracks[idx].Points;
                int take = (int)Math.Min(rem, avail);
                target.Tracks[idx] = new(target.Tracks[idx].Id, avail - take);
                rem -= take;
            }
            target.Tracks.RemoveAll(t => t.Points == 0);
            if (target.Tracks.Count == 0) trackSeries.Remove(target);
        }
        return trackSeries;
    }

    static int? GetFh5SeriesMaximum(ProfileContainer container, uint series)
    {
#if PROFILE_UNLOCK_TESTING
        return Fh5ProfileGameData.For(container)?.PlaylistsById.GetValueOrDefault(series)?.Maximum;
#else
        return null;
#endif
    }

    static bool CommitEditedPayload(ProfileContainer container, BinaryCareer.Record record, byte[] edited)
    {
        var staged = ProfileContainer.Parse(container.Serialize());
        staged.Career!.ReplacePayload(staged.Career.Records.Single(r => r.Name == Name), edited);
        byte[] roundtrip = staged.Serialize();
        if (!ProfileContainer.Parse(roundtrip).Serialize().AsSpan().SequenceEqual(roundtrip)) return false;
        container.Career!.ReplacePayload(record, edited);
        return true;
    }

    static void WriteTrackSeries(BinaryWriter w, List<TrackSeries> trackSeries)
    {
        w.Write((uint)trackSeries.Count);
        foreach (var item in trackSeries)
        {
            w.Write(item.Id);
            w.Write((uint)item.Tracks.Count);
            foreach (var track in item.Tracks) { w.Write(track.Id); w.Write(track.Points); }
        }
    }

    static void WriteScoreMaps(BinaryWriter w, byte[] original, State state, uint targetSeries, int targetSeriesScore, IReadOnlyList<(uint Index, int Points)> targetSeasons)
    {
        // Keep the version and both reward-claim maps exactly as loaded.
        w.Write(original.AsSpan(0, state.ScoreMapsOffset));
        var scores = state.Series.ToDictionary(p => p.Key, p => p.Value.Value);
        scores[targetSeries] = targetSeriesScore;
        w.Write((uint)scores.Count);
        foreach (var item in scores) { w.Write(item.Key); w.Write(item.Value); }
        var roots = state.SeasonRoots.ToList();
        if (!roots.Contains(targetSeries)) roots.Add(targetSeries);
        w.Write((uint)roots.Count);
        foreach (uint id in roots)
        {
            w.Write(id);
            if (id == targetSeries)
            {
                w.Write((uint)targetSeasons.Count);
                foreach (var season in targetSeasons) { w.Write(season.Index); w.Write(season.Points); }
            }
            else
            {
                var seasons = state.Seasons.Where(p => p.Key.Series == id).ToArray();
                w.Write((uint)seasons.Length);
                foreach (var season in seasons) { w.Write(season.Key.Season); w.Write(season.Value.Value); }
            }
        }
    }

    static bool TryRecord(ProfileContainer? container, out BinaryCareer.Record? record)
    {
        record = null;
        var records = container?.Career?.Records.Where(r => r.Name == Name && r.Serializer == Name).ToArray();
        if (records is not { Length: 1 } || container!.Career!.Records.Count(r => r.Name == Name) != 1) return false;
        record = records[0]; return true;
    }

    static bool TryParse(byte[] bytes, out State? state, out string reason)
    {
        state = null; reason = "Unsupported Festival Playlist score layout.";
        if (bytes.Length is < 40 or > MaxBytes) return false;
        try
        {
            var r = new Reader(bytes); uint version = r.U32();
            if (version is not (2 or 4 or 5 or 8)) return false;
            int baseTrack = version == 8 ? 800 : 820;
            // Season reward maps, then Series reward maps. Each reward has a
            // saved ID and three acquisition flags; never rewrite any flag.
            uint count = r.Count(MaxSeries); var roots = new HashSet<uint>(); var rewardSeries = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint sid = r.U32(); if (!roots.Add(sid)) return false;
                rewardSeries.Add(sid);
                uint seasons = r.Count(4); var keys = new HashSet<uint>();
                for (int j = 0; j < seasons; j++) { uint key = r.U32(); if (key > 3 || !keys.Add(key)) return false; r.Rewards(version); }
            }
            count = r.Count(MaxSeries); roots.Clear();
            for (int i = 0; i < count; i++) { uint sid = r.U32(); if (!roots.Add(sid)) return false; rewardSeries.Add(sid); r.Rewards(version); }
            int scoreMapsOffset = r.Offset;
            count = r.Count(MaxSeries); var series = new Dictionary<uint, Score>();
            for (int i = 0; i < count; i++) { uint id = r.U32(); var score = r.Score(); if (!series.TryAdd(id, score)) return false; }
            count = r.Count(MaxSeries); roots.Clear(); var seasonScores = new Dictionary<(uint, uint), Score>(); var seasonRoots = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32(); if (!series.ContainsKey(id) || !roots.Add(id)) return false;
                seasonRoots.Add(id);
                uint seasons = r.Count(4); long sum = 0;
                for (int j = 0; j < seasons; j++) { uint key = r.U32(); var score = r.Score(); if (key > 3 || !seasonScores.TryAdd((id, key), score)) return false; sum += score.Value; }
                if (sum > series[id].Value) return false; // Series-only activities may add extra points.
            }
            if ((seasonScores.Count == 0 && !(version == 8 && rewardSeries.Count > 0)) || series.Values.Sum(s => (long)s.Value) > int.MaxValue) return false;
            // Parse through the activity and two season-timestamp maps to reach
            // the suffix at its exact boundary. Saved per-track caches can differ
            // from the primary scores, so their sums cannot locate that boundary.
            int contentOffset = r.Offset;
            if (!TryActivityMaps(bytes, contentOffset, series, version, out var activities)) return false;
            r.Offset = activities!.End;
            for (int map = 0; map < 2; map++)
            {
                count = r.Count(MaxSeries); roots.Clear();
                for (int i = 0; i < count; i++)
                {
                    uint id = r.U32(); if (!series.ContainsKey(id) || !roots.Add(id)) return false;
                    uint seasons = r.Count(4); var keys = new HashSet<uint>();
                    for (int j = 0; j < seasons; j++)
                    {
                        uint key = r.U32(); if (key > 3 || !keys.Add(key)) return false;
                        _ = r.U32(); // Preserve the native clock value without interpreting it.
                    }
                }
            }
            if (!TryTail(bytes, r.Offset, series, version, out var tail)) return false;
            state = new(version, series, seasonScores, tail!, contentOffset, scoreMapsOffset, seasonRoots, baseTrack, rewardSeries); reason = ""; return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    static bool TryTail(byte[] bytes, int at, Dictionary<uint, Score> scores, uint version, out Tail? tail)
    {
        tail = null;
        try
        {
            var r = new Reader(bytes) { Offset = at }; uint count = r.Count(scores.Count);
            var series = new List<TrackSeries>(); var seen = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32(); if (!scores.TryGetValue(id, out var score) || !seen.Add(id)) return false;
                uint tracks = r.Count(64); var trackKeys = new HashSet<int>(); var values = new List<Track>(); long sum = 0;
                for (int j = 0; j < tracks; j++) { int key = unchecked((int)r.U32()); int points = r.Score().Value; if (key < -1 || key > 1_000_000 || !trackKeys.Add(key)) return false; values.Add(new(key, points)); sum += points; }
                if (sum > int.MaxValue) return false;
                series.Add(new(id, values));
            }
            if (version != 8 && scores.Any(s => s.Value.Value > 0 && !seen.Contains(s.Key))) return false;
            if (version == 8)
            {
                if (r.Offset != bytes.Length) return false;
                tail = new(at, series, Array.Empty<byte>(), -1, (int)scores.Values.Sum(s => (long)s.Value));
                return true;
            }
            int metadataStart = r.Offset; uint seenCount = r.Count(scores.Count); seen.Clear();
            if (seenCount == 0) return false;
            for (int i = 0; i < seenCount; i++) { uint id = r.U32(); if (!scores.ContainsKey(id) || !seen.Add(id)) return false; }
            int lifetimeOffset = r.Offset - metadataStart; int lifetime = r.Score().Value;
            if (lifetime != scores.Values.Sum(s => (long)s.Value)) return false;
            r.Rewards(version); if (r.Offset != bytes.Length) return false;
            tail = new(at, series, bytes[metadataStart..], lifetimeOffset, lifetime); return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    static bool TrackScoresMatch(State state, uint series)
    {
        var tracks = state.Tail.Series.SingleOrDefault(s => s.Id == series);
        if (state.Version == 8 && tracks == null) return true;
        return (tracks?.Tracks.Sum(t => (long)t.Points) ?? 0) == (state.Series.GetValueOrDefault(series)?.Value ?? 0);
    }

    sealed class Reader(byte[] bytes)
    {
        internal int Offset;
        internal uint U32() { if (Offset > bytes.Length - 4) throw new InvalidDataException(); uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Offset)); Offset += 4; return value; }
        internal uint Count(int max) { uint value = U32(); if (value > max) throw new InvalidDataException(); return value; }
        internal Score Score() { int at = Offset; uint value = U32(); if (value > int.MaxValue) throw new InvalidDataException(); return new((int)value, at); }
        internal void Rewards(uint version)
        {
            uint count = Count(10000); var ids = new HashSet<ulong>();
            int idSize = version == 8 ? 4 : 8;
            if (Offset + count * (idSize + 3L) > bytes.Length) throw new InvalidDataException();
            for (int i = 0; i < count; i++)
            {
                ulong id = version == 8 ? U32() : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Offset));
                if (version != 8) Offset += 8;
                if (id == 0 || !ids.Add(id)) throw new InvalidDataException();
                for (int flag = 0; flag < 3; flag++) if (bytes[Offset++] > 1) throw new InvalidDataException();
            }
        }
    }
    static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
}
