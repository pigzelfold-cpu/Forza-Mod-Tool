#nullable disable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    uint? _selectedPlaylist;

    bool AddPlaylistPointsControls()
    {
#if PROFILE_UNLOCK_TESTING
        if (!PlaylistPointsState.TryReadPlaylists(_container, out var playlists, out string reason) || playlists.Length == 0)
        {
            OverviewPanel.Children.Add(Note(string.IsNullOrEmpty(reason) ? "No Playlists found." : reason));
            return false;
        }
        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        var picker = new ComboBox
        {
            Name = "PlaylistSeriesPicker", ItemsSource = playlists, DisplayMemberPath = "Display",
            MinWidth = 300, MinHeight = 32, Margin = new Thickness(0, 0, 8, 4),
        };
        picker.SelectedItem = playlists.SingleOrDefault(p => p.Series == _selectedPlaylist) ?? playlists[0];
        var complete = TestButton("CompletePlaylistButton", "Complete playlist", true);
        complete.Margin = new Thickness(0, 0, 0, 4);
        var chooseData = TestButton("PlaylistGameDataButton", "Choose game data…", true);
        chooseData.Margin = new Thickness(8, 0, 0, 4);
        chooseData.ToolTip = "Select media\\ObjectModelGame.zip from your matching FH6 install.";
        chooseData.Click += (_, _) => ChooseProfileGameData();
        var status = Note(""); status.Name = "PlaylistCompletionStatus";
        void Refresh()
        {
            if (picker.SelectedItem is not PlaylistPointsState.Playlist selected) return;
            if (_selectedPlaylist != selected.Series)
            {
                _selectedPlaylist = selected.Series;
                BuildOverview();
                return;
            }
            _selectedPlaylist = selected.Series; complete.IsEnabled = selected.Supported;
            chooseData.Visibility = selected.Supported ? Visibility.Collapsed : (_isFh5 ? Visibility.Collapsed : Visibility.Visible);
            status.Text = selected.Supported
                ? (selected.NeedsInitialization ? "Missing Playlist progress will be added. " : "")
                    + (selected.NeedsScoreRepair ? "Saved point totals will be synchronized. " : "")
                    + (_isFh5 ? "Complete all four seasons. Saved activity records, score totals and track points update automatically; rewards stay unclaimed." : "Complete all four seasons. Playlist History points update automatically; rewards stay unclaimed. Click Export save to write a separate file.")
                : selected.Reason;
        }
        picker.SelectionChanged += (_, _) => Refresh();
        if (picker.SelectedItem is PlaylistPointsState.Playlist initialSelected)
        {
            _selectedPlaylist = initialSelected.Series; complete.IsEnabled = initialSelected.Supported;
            chooseData.Visibility = initialSelected.Supported ? Visibility.Collapsed : (_isFh5 ? Visibility.Collapsed : Visibility.Visible);
            status.Text = initialSelected.Supported
                ? (initialSelected.NeedsInitialization ? "Missing Playlist progress will be added. " : "")
                    + (initialSelected.NeedsScoreRepair ? "Saved point totals will be synchronized. " : "")
                    + (_isFh5 ? "Complete all four seasons. Saved activity records, score totals and track points update automatically; rewards stay unclaimed." : "Complete all four seasons. Playlist History points update automatically; rewards stay unclaimed. Click Export save to write a separate file.")
                : initialSelected.Reason;
        }
        complete.Click += (_, _) =>
        {
            if (picker.SelectedItem is not PlaylistPointsState.Playlist selected || !selected.Supported) return;
            try
            {
                string confirmMsg = _isFh5
                    ? $"Complete Series {selected.Series} — all four seasons ({selected.Maximum:n0} points)? This marks all season activities complete and updates score totals and track points. It does not claim rewards or create online records."
                    : $"Complete Series {selected.Series} — all four seasons ({selected.Maximum:n0} points)? "
                        + (selected.NeedsInitialization ? "Missing season and activity records will be created. " : "")
                        + (selected.NeedsScoreRepair ? "Saved point totals will be synchronized with the loaded game data. " : "")
                        + "This updates activities, score totals and Playlist History points. It does not claim rewards or create online records. Export a separate save and test in-game.";
                if (!_confirmUnlockTest(confirmMsg)) return;
                byte[] before = _container.Serialize();
                if (!PlaylistPointsState.TryCompletePlaylist(_container, selected.Series, out var result, out string error)) throw new InvalidOperationException(error);
                _hasUnlockTestEdits |= !before.AsSpan().SequenceEqual(_container.Serialize());
                RefreshSaveStateDisplays(); BuildCareer(); BuildOverview();
                string logMsg = _isFh5
                    ? $"Playlist Series {selected.Series}: {result.Activities:n0} activity record(s), {result.Steps:n0} steps completed; {result.Points:n0} points. Reward claims unchanged."
                    : $"Playlist Series {selected.Series}: {result.Activities:n0} activity record(s), {result.Steps:n0} steps completed; {result.Points:n0} points. History: +{result.HistoryPointsAdded:n0} → {result.HistoryPoints:n0}. Reward claims unchanged. Click Export save to write a separate file.";
                Log(logMsg, "ok");
                StatusLine.Text = "Playlist completion applied to working copy — click Export save";
            }
            catch (Exception ex) { Log("Playlist completion: " + ex.Message, "error"); }
        };
        controls.Children.Add(picker); controls.Children.Add(complete); controls.Children.Add(chooseData);
        OverviewPanel.Children.Add(controls); OverviewPanel.Children.Add(status);

        if (PlaylistPointsState.TryRead(_container, out var seasons, out _) && seasons.Length > 0)
        {
            uint currentSeries = (picker.SelectedItem as PlaylistPointsState.Playlist)?.Series ?? _selectedPlaylist ?? seasons[0].Series;
            var seriesSeasons = seasons.Where(s => s.Series == currentSeries).OrderBy(s => s.Index).ToList();
            foreach (var s in seriesSeasons)
            {
                OverviewPanel.Children.Add(EditRow($"{s.Name} season points", $"Series {s.Series} {s.Name} points", s.Points.ToString(CultureInfo.InvariantCulture), text =>
                {
                    if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pts) || pts < 0)
                        return (false, "Must be a non-negative number.");
                    if (!PlaylistPointsState.TrySet(_container, s.Series, s.Index, pts, out bool changed, out string err))
                        return (false, err);
                    _hasUnlockTestEdits |= changed;
                    RefreshSaveStateDisplays(); BuildCareer(); BuildOverview();
                    return (true, pts.ToString(CultureInfo.InvariantCulture));
                }));
            }
        }
        return true;
#else
        OverviewPanel.Children.Add(Note("Playlist completion is unavailable in this build. Saved activity and rewards are preserved."));
        return false;
#endif
    }
}
