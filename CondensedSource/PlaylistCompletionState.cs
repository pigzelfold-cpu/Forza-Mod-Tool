#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.IO;
using System.Globalization;

namespace FH6LocalCryptoTool;

public static partial class PlaylistPointsState
{
    public sealed record Playlist(uint Series, string Name, int Points, int Maximum, bool Supported, string Reason)
    {
        public string Display => $"Series {Series} · {Name} ({Points:n0}/{Maximum:n0})";
        public bool NeedsInitialization { get; init; }
        public bool NeedsScoreRepair { get; init; }
    }
    public sealed record Completion(uint Series, int Activities, int Steps, int Points, int HistoryPointsAdded = 0, int HistoryPoints = 0);

    static PlaylistActivityCatalog.Series SynthesizeSeries(uint seriesId, int totalMaximum, ActivityState activities, State state, Fh5ProfileGameData.PlaylistSeriesDef? fh5Def = null)
    {
        var saved = activities.Series.SingleOrDefault(s => s.Series == seriesId);
        bool useDefSeasons = fh5Def != null && fh5Def.SeasonMaxima.Length == 4 && fh5Def.Maximum == totalMaximum;
        int perSeason = totalMaximum / 4;
        int remainder = totalMaximum % 4;
        var seasons = new PlaylistActivityCatalog.Season[4];
        for (uint i = 0; i < 4; i++)
        {
            int seasonMax = useDefSeasons ? fh5Def!.SeasonMaxima[i] : perSeason + (i < remainder ? 1 : 0);
            var seasonProgress = saved?.Seasons.SingleOrDefault(s => s.Index == i);
            var activityList = new List<PlaylistActivityCatalog.Activity>();
            if (seasonProgress != null)
            {
                foreach (var group in seasonProgress.Groups)
                {
                    foreach (var entry in group.Entries)
                    {
                        activityList.Add(new(group.Type, entry.Id, entry.Values.Length, 0));
                    }
                }
            }
            seasons[i] = new PlaylistActivityCatalog.Season(i, activityList.ToArray())
            {
                StoredMaximum = seasonMax
            };
        }
        string name = fh5Def != null && !string.IsNullOrWhiteSpace(fh5Def.Name) ? fh5Def.Name : "Playlist";
        return new PlaylistActivityCatalog.Series(seriesId, name, seasons)
        {
            StoredMaximum = totalMaximum
        };
    }

    public static bool TryReadPlaylists(ProfileContainer? container, out Playlist[] playlists, out string reason)
    {
        playlists = Array.Empty<Playlist>(); reason = "Playlist activity records are not recognized.";
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason)) return false;
        if (!TryActivities(record.Payload, state!, out var activities))
        { reason = "Playlist activity layout is not recognized; saved progress stays unchanged."; return false; }
        var definitions = ProfileGameData.For(container)?.Playlists ?? Array.Empty<PlaylistActivityCatalog.Series>();
        var fh5Defs = state!.Version == 8 ? Fh5ProfileGameData.For(container)?.PlaylistsById : null;
        IEnumerable<uint> seriesIds = state.Version == 8
            ? state.RewardSeries.Concat(activities!.Maxima.Keys).Concat(state.Series.Keys)
            : definitions.Select(d => d.Id).Concat(activities!.Maxima.Keys).Concat(state.Series.Keys);
        playlists = seriesIds.Where(id => id != 0).Distinct()
            .OrderDescending().Select(id =>
            {
                PlaylistActivityCatalog.Series? definition = null;
                Fh5ProfileGameData.PlaylistSeriesDef? fh5Def = null;
                if (state.Version == 8)
                {
                    fh5Def = fh5Defs?.GetValueOrDefault(id);
                    if (activities!.Maxima.TryGetValue(id, out var v8Max))
                        definition = SynthesizeSeries(id, v8Max.Maximum, activities, state, fh5Def);
                    else if (fh5Def != null && fh5Def.Maximum > 0)
                        definition = SynthesizeSeries(id, fh5Def.Maximum, activities, state, fh5Def);
                }
                else
                {
                    definition = definitions.SingleOrDefault(d => d.Id == id);
                }
                bool supported = CanComplete(state, activities!, definition, id, out string error);
                int maximum = activities!.Maxima.TryGetValue(id, out var max) ? max.Maximum : definition?.Maximum ?? 0;
                return new Playlist(id, definition?.Name ?? fh5Def?.Name ?? "Playlist", state.Series.GetValueOrDefault(id)?.Value ?? 0, maximum, supported, error)
                {
                    NeedsInitialization = !state.Series.ContainsKey(id) || state.Seasons.Keys.Count(k => k.Series == id) != 4 ||
                        activities.Series.SingleOrDefault(s => s.Series == id)?.Seasons.Count != 4,
                    NeedsScoreRepair = !TrackScoresMatch(state, id)
                };
            }).ToArray();
        reason = ""; return true;
    }

    /// <summary>Completes the selected Series, adding missing progress from matching game definitions.
    /// Reward-claim flags, dates, account data and online result submissions are never changed.</summary>
    public static bool TryCompletePlaylist(ProfileContainer? container, uint series, out Completion? completion, out string reason)
    {
        completion = null; reason = "Playlist activity records are not recognized; nothing was changed.";
        if (!TryRecord(container, out var original) || !TryParse(original!.Payload, out var state, out reason)) return false;
        if (!TryActivities(original.Payload, state!, out var activityState))
        { reason = "Playlist activity layout is not recognized; nothing was changed."; return false; }
        PlaylistActivityCatalog.Series? definition;
        if (state!.Version == 8)
        {
            var fh5Def = Fh5ProfileGameData.For(container)?.PlaylistsById.GetValueOrDefault(series);
            if (activityState!.Maxima.TryGetValue(series, out var v8Max))
                definition = SynthesizeSeries(series, v8Max.Maximum, activityState, state, fh5Def);
            else if (fh5Def != null && fh5Def.Maximum > 0)
                definition = SynthesizeSeries(series, fh5Def.Maximum, activityState, state, fh5Def);
            else
                definition = null;
        }
        else
        {
            definition = ProfileGameData.For(container)?.Playlists.SingleOrDefault(d => d.Id == series);
        }
        if (!CanComplete(state, activityState!, definition, series, out reason)) return false;
        int oldHistory = 0;
        Bxml.Node? historyNode = null;
        if (state.Version != 8)
        {
            if (!TryHistoryTotal(container!, out historyNode, out oldHistory))
            { reason = "Playlist History points are not safely recognized; nothing was changed."; return false; }
        }
        int activityCount = 0, stepCount = 0;
        var target = activityState!.Series.SingleOrDefault(s => s.Series == series);
        if (target == null) { target = new(series, new()); activityState.Series.Add(target); }
        foreach (var season in definition!.Seasons)
        {
            var saved = target.Seasons.SingleOrDefault(s => s.Index == season.Index);
            if (saved == null) { saved = new(season.Index, new()); target.Seasons.Add(saved); }
            foreach (var activity in season.Activities)
            {
                var group = saved.Groups.SingleOrDefault(g => g.Type == activity.Type);
                if (group == null) { group = new(activity.Type, new()); saved.Groups.Add(group); }
                var old = group.Entries.SingleOrDefault(e => e.Id == activity.Id);
                int changedSteps = old == null ? activity.Stages : old.Values.Count(v => v < 1);
                if (changedSteps > 0)
                {
                    var next = new ActivityProgress(activity.Id, Enumerable.Repeat(1f, activity.Stages).ToArray());
                    if (old == null) group.Entries.Add(next); else group.Entries[group.Entries.IndexOf(old)] = next;
                    activityCount++; stepCount += changedSteps;
                }
            }
        }
        long delta = (long)definition.Maximum - (state!.Series.GetValueOrDefault(series)?.Value ?? 0);
        long lifetime = state.Tail.Lifetime + delta;
        if (lifetime is < 0 or > int.MaxValue) { reason = "Playlist total would overflow; nothing was changed."; return false; }
        // Keep independent historical awards, add only the new Series points,
        // and repair an old history total below the verified saved lifetime.
        // Never decrement history or touch reward-claim/checkpoint records.
        long history = state.Version == 8 ? lifetime : Math.Max((long)oldHistory + Math.Max(0, delta), lifetime);
        if (history > int.MaxValue) { reason = "Playlist History points would overflow; nothing was changed."; return false; }
        var tracks = state.Tail.Series.Select(s => new TrackSeries(s.Id, s.Tracks.ToList())).ToList();
        var oldTracks = tracks.SingleOrDefault(s => s.Id == series);
        // Completing every verified scoring activity replaces legacy unattributed
        // (-1) cache points; retaining them would count those same activities twice.
        TrackSeries nextTracks;
        if (state.Version == 8 && activityState.Maxima.TryGetValue(series, out var v8Tracks))
        {
            var nonZero = v8Tracks.TrackList.Where(t => t.Value > 0).Select(t => new Track(t.Key, t.Value)).ToList();
            nextTracks = new TrackSeries(series, nonZero.Count > 0 ? nonZero : new() { new(state.BaseTrack, definition.Maximum) });
        }
        else
        {
            nextTracks = new TrackSeries(series, new() { new(state.BaseTrack, definition.Maximum) });
        }
        if (oldTracks == null) tracks.Add(nextTracks); else tracks[tracks.IndexOf(oldTracks)] = nextTracks;
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WriteScoreMaps(w, original.Payload, state, definition.Id, definition.Maximum,
                definition.Seasons.Select(s => (s.Index, s.Maximum)).ToArray());
            if (state.Version != 8 || activityState.Maxima.ContainsKey(series))
            {
                // Cached maxima and their native version tokens remain byte-exact.
                w.Write(original.Payload.AsSpan(state.ContentOffset, activityState.Start - state.ContentOffset));
            }
            else
            {
                ulong token = activityState.MaximaEntries.LastOrDefault()?.Token ?? 0UL;
                var nextMaxima = activityState.MaximaEntries.ToList();
                nextMaxima.Add(new MaximumEntry(series, token, definition.Maximum,
                    new() { new(state.BaseTrack, definition.Maximum) },
                    new() { [state.BaseTrack] = definition.Maximum }));
                WriteMaxima(w, nextMaxima);
            }
            WriteActivities(w, activityState.Series, state.Version);
            w.Write(original.Payload.AsSpan(activityState.End, state.Tail.Offset - activityState.End));
            WriteTrackSeries(w, tracks);
            if (state.Version != 8)
            {
                WriteCompletionMetadata(w, state.Tail, series, (int)lifetime);
            }
        }
        byte[] edited = stream.ToArray();
        if (!TryParse(edited, out var check, out reason) || !TryActivities(edited, check!, out var nextState) ||
            !CanComplete(check!, nextState!, definition, series, out reason) ||
            check!.Series[series].Value != definition.Maximum || definition.Seasons.Any(s => check.Seasons[(series, s.Index)].Value != s.Maximum))
        { reason = "Playlist completion readback failed; nothing was changed."; return false; }
        foreach (var season in definition.Seasons)
            foreach (var activity in season.Activities)
            {
                var saved = nextState!.Series.Single(s => s.Series == series).Seasons.Single(s => s.Index == season.Index)
                    .Groups.Single(g => g.Type == activity.Type).Entries.Single(e => e.Id == activity.Id);
                if (saved.Values.Any(v => v != 1)) { reason = "Playlist activity validation failed; nothing was changed."; return false; }
            }
        var staged = ProfileContainer.Parse(container!.Serialize());
        ProfileGameData.Copy(container, staged);
        Fh5ProfileGameData.Copy(container, staged);
        staged.Career!.ReplacePayload(staged.Career.Records.Single(r => r.Name == Name), edited);
        if (state.Version != 8 && history != oldHistory)
        {
            if (!TryHistoryTotal(staged, out var nextHistoryNode, out _) ||
                !staged.SaveState!.SetAttribute(nextHistoryNode!, "value", history.ToString(CultureInfo.InvariantCulture))) return false;
        }
        byte[] serialized = staged.Serialize();
        var verifiedProfile = ProfileContainer.Parse(serialized);
        if (!verifiedProfile.Serialize().AsSpan().SequenceEqual(serialized) ||
            (state.Version != 8 && (!TryHistoryTotal(verifiedProfile, out _, out int verifiedHistory) || verifiedHistory != history)))
        { reason = "Edited profile did not round-trip; nothing was changed."; return false; }
        // Commit only after every check. No live file or other profile record is touched.
        if (!original.Payload.AsSpan().SequenceEqual(edited)) container.Career!.ReplacePayload(original, edited);
        if (state.Version != 8 && history != oldHistory) container.SaveState!.SetAttribute(historyNode!, "value", history.ToString(CultureInfo.InvariantCulture));
        completion = new(series, activityCount, stepCount, definition.Maximum, state.Version == 8 ? 0 : checked((int)history - oldHistory), (int)history); reason = ""; return true;
    }

    static bool TryHistoryTotal(ProfileContainer container, out Bxml.Node? total, out int points)
    {
        total = null; points = 0;
        if (container.SaveState is not { } bx) return false;
        const string key = "CurrencyBucket_CollectionCategoryProgress_cc_festival_playlist";
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == "CurrencyBucket").ToArray();
        if (values.Length != 1 || values[0].Attributes.Count != 1 || values[0].Children.Count != 3) return false;
        Bxml.Node? Field(string id, string fieldType)
        {
            var nodes = values[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            return nodes.Length == 1 && bx.GetAttribute(nodes[0], "type") == fieldType &&
                nodes[0].Attributes.Count == 3 && nodes[0].Children.Count == 0 ? nodes[0] : null;
        }
        var name = Field("Name", "string");
        var type = Field("Type", "CurrencyTypes");
        total = Field("Total", "int");
        return name != null && type != null && total != null && bx.GetAttribute(name, "value") == "cc_festival_playlist" &&
            bx.GetAttribute(type, "value") == "CollectionCategoryProgress" &&
            int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out points) && points >= 0;
    }

    static bool CanComplete(State state, ActivityState activities, PlaylistActivityCatalog.Series? definition, uint series, out string reason)
    {
        reason = state.Version == 8
            ? "Select FH5 TIDETables.zip to load historical Playlist totals, or edit individual season points below."
            : "Select the matching ObjectModelGame.zip to load Playlist challenges.";
        if (definition == null) return false;
        reason = "Playlist definitions are incomplete; nothing was changed.";
        if (series == 0 || definition.Id != series || definition.Seasons.Length != 4 || definition.Maximum <= 0 ||
            !definition.Seasons.Select(s => s.Index).Order().SequenceEqual(new uint[] { 0, 1, 2, 3 })) return false;
        if (state.Version == 8)
        {
            if (activities.Maxima.TryGetValue(series, out var v8Max))
            {
                reason = "Playlist definitions and saved maximum points do not match. Nothing was changed.";
                if (v8Max.Maximum != definition.Maximum || v8Max.Tracks.Values.Sum() != definition.Maximum) return false;
            }
            else if (!state.RewardSeries.Contains(series))
            {
                reason = "Playlist definitions are not recognized for this save.";
                return false;
            }
        }
        else
        {
            reason = "Open Festival Playlist in the updated game, let it save, then reload this save to initialize the new Series.";
            if (!activities.Maxima.TryGetValue(series, out var maximum)) return false;
            reason = "Playlist definitions and saved maximum points do not match. Nothing was changed.";
            if (maximum.Maximum != definition.Maximum) return false;
            if (maximum.Tracks.GetValueOrDefault(state.BaseTrack) != definition.Maximum ||
                maximum.Tracks.Any(t => t.Key != state.BaseTrack && t.Value != 0)) return false;
        }
        if ((state.Series.GetValueOrDefault(series)?.Value ?? 0) > definition.Maximum)
        { reason = "Saved Playlist points exceed the matching maximum; nothing was changed."; return false; }
        var saved = activities.Series.SingleOrDefault(s => s.Series == series);
        foreach (var season in saved?.Seasons ?? Enumerable.Empty<SeasonProgress>())
        {
            var expected = definition.Seasons.SingleOrDefault(s => s.Index == season.Index);
            if (expected == null) return false;
            foreach (var group in season.Groups)
            {
                // Type 11 is not represented by a scoring definition. All
                // scoring awards are independently matched to the exact saved
                // maximum. Preserve these unmapped entries instead of guessing.
                if (group.Type == 11) continue;
                foreach (var entry in group.Entries)
                {
                    var match = expected.Activities.SingleOrDefault(a => a.Type == group.Type && a.Id == entry.Id);
                    if (match == null || match.Stages != entry.Values.Length)
                    { reason = "A saved Playlist activity has changed or is not mapped yet. Nothing was changed."; return false; }
                }
            }
        }
        reason = ""; return true;
    }

    static void WriteCompletionMetadata(BinaryWriter w, Tail tail, uint series, int lifetime)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tail.Metadata));
        var seen = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(tail.Metadata.AsSpan(4 + i * 4))).ToList();
        if (!seen.Contains(series)) seen.Add(series);
        w.Write((uint)seen.Count);
        foreach (uint id in seen) w.Write(id);
        w.Write(lifetime);
        // Lifetime reward claims are independent of which Series has been opened.
        w.Write(tail.Metadata.AsSpan(tail.LifetimeOffset + 4));
    }

    static bool TryActivities(byte[] bytes, State state, out ActivityState? result) =>
        TryActivityMaps(bytes, state.ContentOffset, state.Series, state.Version, out result);

    static void WriteActivities(BinaryWriter w, List<SeriesProgress> series, uint version)
    {
        w.Write((uint)series.Count);
        foreach (var item in series)
        {
            w.Write(item.Series); w.Write((uint)item.Seasons.Count);
            foreach (var season in item.Seasons)
            {
                w.Write(season.Index); w.Write((uint)season.Groups.Count);
                foreach (var group in season.Groups)
                {
                    w.Write(group.Type); w.Write((uint)group.Entries.Count);
                    foreach (var activity in group.Entries)
                    {
                        if (version == 8) w.Write((uint)activity.Id); else w.Write(activity.Id);
                        w.Write((uint)activity.Values.Length);
                        foreach (float value in activity.Values) w.Write(value);
                    }
                }
            }
        }
    }
}
#endif
