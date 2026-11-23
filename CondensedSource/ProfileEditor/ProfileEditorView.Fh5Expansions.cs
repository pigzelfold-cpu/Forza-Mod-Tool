#nullable disable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    void AddFh5HotWheelsControls()
    {
        OverviewPanel.Children.Add(SectionHeader("FH5 Hot Wheels Academy"));

        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        OverviewPanel.Children.Add(chips);

        var boardStatus = Note("");
        var boardBtn = MakeExpansionButton("Fh5HotWheelsBoardsButton", "Smash all Hot Wheels bonus boards", enabled: false);
        Action refreshGrid = null;

        void RefreshChips()
        {
            chips.Children.Clear();
            Fh5ExpansionsSaveState.TryReadExpansionIntroState(_container, out string ex1, out _);
            chips.Children.Add(StatChip(ex1, "Intro state"));

            if (Fh5ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh5ExpansionsSaveState.SpectrumMedalsKey,
                    Fh5ExpansionsSaveState.SpectrumMedalsName, Fh5ExpansionsSaveState.SpectrumMedalsType,
                    out bool exists, out int medals, out _))
            {
                chips.Children.Add(StatChip(exists ? medals.ToString("N0", CultureInfo.CurrentCulture) : "0 (not created)", "Hot Wheels medals"));
                chips.Children.Add(StatChip(Fh5ExpansionsSaveState.GetHotWheelsRankDisplay(medals), "Hot Wheels rank"));
            }

            if (Fh5ExpansionsSaveState.TryReadBoards(_container, rally: false, out int bCol, out int bTot))
            {
                chips.Children.Add(StatChip(bTot > 0 ? $"{bCol:N0} / {bTot:N0}" : "0 (not visited)", "Hot Wheels boards"));
                boardBtn.IsEnabled = bTot > 0 && bCol < bTot;
                boardBtn.Opacity = boardBtn.IsEnabled ? 1 : 0.45;
                boardStatus.Text = bTot == 0
                    ? "Bonus boards appear in SaveState once you visit Hot Wheels Park in-game."
                    : $"{bCol:N0} / {bTot:N0} Hot Wheels bonus boards smashed.";
            }
            else
            {
                boardBtn.IsEnabled = false;
                boardBtn.Opacity = 0.45;
                boardStatus.Text = "Hot Wheels bonus board layout is not safely recognized.";
            }

#if PROFILE_UNLOCK_TESTING
            if (Fh5RoadDiscoverySaveState.TryReadProfile(_container, out var roads) && roads.Ex1 is { } ex1Roads)
                chips.Children.Add(StatChip($"{ex1Roads.DiscoveredEntries:N0} / {ex1Roads.EntryCount:N0}", "Hot Wheels roads"));
#endif
        }

        Fh5ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh5ExpansionsSaveState.SpectrumMedalsKey,
            Fh5ExpansionsSaveState.SpectrumMedalsName, Fh5ExpansionsSaveState.SpectrumMedalsType,
            out _, out int initialMedals, out _);

        var medalsRow = AddFh5CurrencyEditRow(
            "Fh5HotWheelsMedalsRow",
            "Hot Wheels Medals",
            "Updates or creates CurrencyBucket_SpectrumMedals_career_progress (max 5,000 from missions). Also unlocks ranks by medal threshold.",
            initialMedals,
            Fh5ExpansionsSaveState.SpectrumMedalsKey,
            Fh5ExpansionsSaveState.SpectrumMedalsName,
            Fh5ExpansionsSaveState.SpectrumMedalsType,
            () => { RefreshChips(); refreshGrid?.Invoke(); });
        if (medalsRow != null) OverviewPanel.Children.Add(medalsRow);

        boardBtn.Click += (_, _) =>
        {
            if (!ConfirmExpansionAction("Smash all Hot Wheels Park bonus boards in your working save? Other progress stays unchanged.")) return;
            if (!Fh5ExpansionsSaveState.TrySmashAllBoards(_container, rally: false, out int smashed, out string err))
            {
                Log(err, "err");
                return;
            }
            RefreshChips();
            BuildProperties();
            RefreshSaveStateDisplays();
            Log($"Hot Wheels bonus boards: {smashed:n0} smashed", "ok");
        };
        OverviewPanel.Children.Add(boardBtn);
        OverviewPanel.Children.Add(boardStatus);
        RefreshChips();

#if PROFILE_UNLOCK_TESTING
        refreshGrid = AddFh5HotWheelsGrid(RefreshChips, medals => SetEditRowText(medalsRow, medals));
#endif
    }

    void AddFh5RallyAdventureControls()
    {
        OverviewPanel.Children.Add(SectionHeader("FH5 Rally Adventure (Sierra Nueva)"));

        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        OverviewPanel.Children.Add(chips);

        var boardStatus = Note("");
        var boardBtn = MakeExpansionButton("Fh5RallyBoardsButton", "Smash all Rally Adventure bonus boards", enabled: false);
        Action refreshGrid = null;
        var teamRows = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);

        void RefreshChips()
        {
            chips.Children.Clear();
            Fh5ExpansionsSaveState.TryReadExpansionIntroState(_container, out _, out string ex2);
            chips.Children.Add(StatChip(ex2, "Intro state"));

            foreach (var team in Fh5ExpansionsSaveState.RallyTeams)
            {
                if (Fh5ExpansionsSaveState.TryReadCurrencyBucket(_container, team.BucketKey, team.BucketName,
                        Fh5ExpansionsSaveState.AstraCurrencyType, out bool exists, out int rep, out _))
                {
                    string shortName = team.DisplayName.Split(" (")[0];
                    string val = exists ? $"{rep:N0} · {Fh5ExpansionsSaveState.GetRallyTeamRankDisplay(team.BucketName, rep)}" : "Absent";
                    chips.Children.Add(StatChip(val, shortName));
                }
            }

            if (Fh5ExpansionsSaveState.TryReadBoards(_container, rally: true, out int bCol, out int bTot))
            {
                chips.Children.Add(StatChip(bTot > 0 ? $"{bCol:N0} / {bTot:N0}" : "0 (not visited)", "Rally boards"));
                boardBtn.IsEnabled = bTot > 0 && bCol < bTot;
                boardBtn.Opacity = boardBtn.IsEnabled ? 1 : 0.45;
                boardStatus.Text = bTot == 0
                    ? "Bonus boards appear in SaveState once you visit Sierra Nueva in-game."
                    : $"{bCol:N0} / {bTot:N0} Rally Adventure bonus boards smashed.";
            }
            else
            {
                boardBtn.IsEnabled = false;
                boardBtn.Opacity = 0.45;
                boardStatus.Text = "Rally Adventure bonus board layout is not safely recognized.";
            }

#if PROFILE_UNLOCK_TESTING
            if (Fh5RoadDiscoverySaveState.TryReadProfile(_container, out var roads) && roads.Ex2 is { } ex2Roads)
                chips.Children.Add(StatChip($"{ex2Roads.DiscoveredEntries:N0} / {ex2Roads.EntryCount:N0}", "Sierra Nueva roads"));
#endif
        }

        foreach (var team in Fh5ExpansionsSaveState.RallyTeams)
        {
            Fh5ExpansionsSaveState.TryReadCurrencyBucket(_container, team.BucketKey, team.BucketName,
                Fh5ExpansionsSaveState.AstraCurrencyType, out _, out int rep, out _);

            var row = AddFh5CurrencyEditRow(
                "Fh5Rally_" + team.BucketName,
                $"{team.DisplayName} Reputation",
                $"Updates or creates {team.BucketKey} (9 ranks total; max threshold {team.Thresholds[^1]:N0}).",
                rep,
                team.BucketKey,
                team.BucketName,
                Fh5ExpansionsSaveState.AstraCurrencyType,
                () => { RefreshChips(); refreshGrid?.Invoke(); });
            if (row != null)
            {
                teamRows[team.DisplayName] = row;
                OverviewPanel.Children.Add(row);
            }
        }

        boardBtn.Click += (_, _) =>
        {
            if (!ConfirmExpansionAction("Smash all Rally Adventure (Sierra Nueva) bonus boards in your working save? Other progress stays unchanged.")) return;
            if (!Fh5ExpansionsSaveState.TrySmashAllBoards(_container, rally: true, out int smashed, out string err))
            {
                Log(err, "err");
                return;
            }
            RefreshChips();
            BuildProperties();
            RefreshSaveStateDisplays();
            Log($"Rally Adventure bonus boards: {smashed:n0} smashed", "ok");
        };
        OverviewPanel.Children.Add(boardBtn);
        OverviewPanel.Children.Add(boardStatus);
        RefreshChips();

#if PROFILE_UNLOCK_TESTING
        refreshGrid = AddFh5RallyGrid(RefreshChips, updates =>
        {
            foreach (var (tName, _, after) in updates)
                if (teamRows.TryGetValue(tName, out var tr)) SetEditRowText(tr, after);
        });
#endif
    }

    FrameworkElement AddFh5CurrencyEditRow(string rowName, string label, string hint, int initialValue,
        string mapKey, string bucketName, string currencyType, Action afterApply)
    {
        var row = EditRow(
            label,
            hint,
            initialValue.ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");

                if (!Fh5ExpansionsSaveState.TrySetOrCreateCurrencyBucket(_container, mapKey, bucketName, currencyType, value, out bool changed, out string reason))
                    return (false, string.IsNullOrEmpty(reason) ? "currency bucket could not be updated" : reason);

                if (changed)
                {
                    RefreshSaveStateDisplays();
                    afterApply?.Invoke();
                }
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        row.Name = rowName;
        return row;
    }

    Button MakeExpansionButton(string name, string text, bool enabled, bool pink = true) => new()
    {
        Name = name,
        Content = text,
        Style = (Style)FindResource(pink ? "PinkButton" : "GhostButton"),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 6, 8, 0),
        IsEnabled = enabled,
        Opacity = enabled ? 1 : 0.45,
    };

    bool ConfirmExpansionAction(string message)
    {
#if PROFILE_UNLOCK_TESTING
        if (_confirmUnlockTest != null) return _confirmUnlockTest(message);
#endif
        return MessageBox.Show(message, "Confirm expansion edit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    DataGrid CreateDarkDataGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Extended,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HorizontalGridLinesBrush = (Brush)FindResource("Line"),
        Background = (Brush)FindResource("Card2"),
        Foreground = (Brush)FindResource("Ink"),
        BorderBrush = (Brush)FindResource("Line"),
        BorderThickness = new Thickness(1),
        RowBackground = (Brush)FindResource("Card2"),
        AlternatingRowBackground = (Brush)FindResource("Card"),
        MaxHeight = 320,
        Margin = new Thickness(0, 6, 0, 8),
    };

    static DataGridTextColumn DarkTextColumn(string header, string bindingPath, DataGridLength? width = null, string stringFormat = null)
    {
        var binding = new Binding(bindingPath);
        if (!string.IsNullOrEmpty(stringFormat)) binding.StringFormat = stringFormat;
        var col = new DataGridTextColumn { Header = header, Binding = binding };
        if (width.HasValue) col.Width = width.Value;
        return col;
    }

    static TextBlock MakeFilterLabel(string text, Brush brush) => new()
    {
        Text = text,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 4),
    };

    static ComboBox MakeFilterCombo(int width = 180) => new()
    {
        Width = width,
        Margin = new Thickness(0, 0, 10, 4),
    };

#if PROFILE_UNLOCK_TESTING
    Action AddFh5HotWheelsGrid(Action refreshParentChips, Action<int> updateMedalsBox)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        var summary = Note("");
        var dim = (Brush)FindResource("Dim");
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var rankBox = MakeFilterCombo(175);
        var stateBox = MakeFilterCombo(145);
        stateBox.ItemsSource = new[] { "All missions", "Incomplete only", "Completed only", "Supported only" };
        stateBox.SelectedIndex = 0;

        var search = new TextBox
        {
            Width = 220,
            Style = (Style)FindResource("Input"),
            Margin = new Thickness(0, 0, 10, 4),
            ToolTip = "Search Hot Wheels missions by ID, rank, name, or description",
        };
        var matches = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };

        filters.Children.Add(MakeFilterLabel("Rank:", dim));
        filters.Children.Add(rankBox);
        filters.Children.Add(MakeFilterLabel("State:", dim));
        filters.Children.Add(stateBox);
        filters.Children.Add(MakeFilterLabel("Search:", dim));
        filters.Children.Add(search);
        filters.Children.Add(matches);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var completeSelBtn = TestButton("Fh5HwCompleteSelectedBtn", "Complete selected", false);
        var completeAllBtn = TestButton("Fh5HwCompleteAllBtn", "Complete all visible", false);
        var resetSelBtn = MakeExpansionButton("Fh5HwResetSelectedBtn", "Reset selected", false, pink: false);
        var syncBtn = MakeExpansionButton("Fh5HwSyncMedalsBtn", "Sync medals to completed", false, pink: false);
        actions.Children.Add(completeSelBtn);
        actions.Children.Add(completeAllBtn);
        actions.Children.Add(resetSelBtn);
        actions.Children.Add(syncBtn);

        var grid = CreateDarkDataGrid();
        grid.Columns.Add(DarkTextColumn("ID", "Id", new DataGridLength(68)));
        grid.Columns.Add(DarkTextColumn("Rank", "RankDisplay", new DataGridLength(155)));
        grid.Columns.Add(DarkTextColumn("Type", "Importance", new DataGridLength(72)));
        grid.Columns.Add(DarkTextColumn("Mission", "Name", new DataGridLength(260)));
        grid.Columns.Add(DarkTextColumn("Medals", "Medals", new DataGridLength(64)));
        grid.Columns.Add(DarkTextColumn("Done", "CompletedDisplay", new DataGridLength(56)));
        grid.Columns.Add(DarkTextColumn("Completed UTC", "CompletedUtc", new DataGridLength(130), "yyyy-MM-dd HH:mm"));
        grid.Columns.Add(DarkTextColumn("Status", "Compatibility", new DataGridLength(1, DataGridLengthUnitType.Star)));

        panel.Children.Add(summary);
        panel.Children.Add(filters);
        panel.Children.Add(actions);
        panel.Children.Add(grid);
        OverviewPanel.Children.Add(panel);

        Fh5HotWheelsState.Row[] allRows = Array.Empty<Fh5HotWheelsState.Row>();
        Fh5HotWheelsState.Row[] visibleRows = Array.Empty<Fh5HotWheelsState.Row>();

        void ApplyFilter()
        {
            string rankFilter = rankBox.SelectedItem as string ?? "All ranks";
            int stateMode = stateBox.SelectedIndex;
            string q = search.Text?.Trim() ?? "";

            visibleRows = allRows.Where(r =>
            {
                if (rankFilter != "All ranks" && r.RankDisplay != rankFilter) return false;
                if (stateMode == 1 && r.Completed) return false;
                if (stateMode == 2 && !r.Completed) return false;
                if (stateMode == 3 && !r.Supported) return false;
                if (q.Length > 0 &&
                    !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.RankDisplay.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Id.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal))
                    return false;
                return true;
            }).ToArray();

            grid.ItemsSource = visibleRows;
            matches.Text = $"{visibleRows.Length:N0} / {allRows.Length:N0} shown · Ctrl+A selects visible";
            bool anySupported = allRows.Any(r => r.Supported);
            completeSelBtn.IsEnabled = completeSelBtn.Opacity > 0 && grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Any(r => r.Supported && r.Completed);
            completeAllBtn.IsEnabled = visibleRows.Any(r => r.Supported && !r.Completed);
            syncBtn.IsEnabled = anySupported;
            foreach (var b in new[] { completeSelBtn, completeAllBtn, resetSelBtn, syncBtn }) b.Opacity = b.IsEnabled ? 1 : 0.45;
        }

        void Refresh()
        {
            if (!Fh5HotWheelsState.TryRead(_container, out var view, out string err))
            {
                allRows = Array.Empty<Fh5HotWheelsState.Row>();
                summary.Text = err;
                rankBox.ItemsSource = new[] { "All ranks" };
                rankBox.SelectedIndex = 0;
                ApplyFilter();
                return;
            }

            allRows = view.Rows;
            string prevRank = rankBox.SelectedItem as string;
            var ranks = new[] { "All ranks" }.Concat(allRows.Select(r => r.RankDisplay).Distinct()).ToArray();
            rankBox.ItemsSource = ranks;
            rankBox.SelectedItem = prevRank != null && ranks.Contains(prevRank) ? prevRank : "All ranks";

            summary.Text = $"{view.CompletedCount:N0} / {view.CatalogCount:N0} Hot Wheels missions completed ({view.CompletedMedals:N0} medals earned; saved bucket: {view.SavedMedals:N0})."
                + (string.IsNullOrEmpty(view.StatusNote) ? "" : " " + view.StatusNote);
            ApplyFilter();
        }

        rankBox.SelectionChanged += (_, _) => ApplyFilter();
        stateBox.SelectionChanged += (_, _) => ApplyFilter();
        search.TextChanged += (_, _) => ApplyFilter();
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        grid.SelectionChanged += (_, _) =>
        {
            completeSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Any(r => r.Supported && r.Completed);
            completeSelBtn.Opacity = completeSelBtn.IsEnabled ? 1 : 0.45;
            resetSelBtn.Opacity = resetSelBtn.IsEnabled ? 1 : 0.45;
        };

        void RunEdit(Func<uint[]> pickIds, bool complete)
        {
            try
            {
                uint[] ids = pickIds();
                if (ids.Length == 0) { Log("No eligible Hot Wheels missions match the action.", "info"); return; }
                bool ok = complete
                    ? Fh5HotWheelsState.TryPreviewComplete(_container, ids, out var ch, out string reason)
                    : Fh5HotWheelsState.TryPreviewReset(_container, ids, out ch, out reason);
                if (!ok) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("No Hot Wheels mission changes needed.", "info"); return; }
                string verb = complete ? "Complete" : "Reset";
                if (!ConfirmExpansionAction($"{verb} {ids.Length:N0} Hot Wheels mission(s)? Medals: {ch.PreviousMedals:N0} → {ch.NewMedals:N0}.")) return;
                bool applied = complete
                    ? Fh5HotWheelsState.TryComplete(_container, ids, out ch, out reason)
                    : Fh5HotWheelsState.TryReset(_container, ids, out ch, out reason);
                if (!applied) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateMedalsBox(ch.NewMedals);
                RefreshSaveStateDisplays();
                BuildProperties();
                RefreshUnlockTestDisplays($"Hot Wheels: {verb.ToLowerInvariant()}d {(complete ? ch.CompletedDelta : ch.ResetDelta):N0} mission(s) · medals {ch.NewMedals:N0}");
            }
            catch (Exception ex) { Fail("update Hot Wheels missions", ex); }
        }

        completeSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        completeAllBtn.Click += (_, _) => RunEdit(() => visibleRows.Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        resetSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh5HotWheelsState.Row>().Where(r => r.Supported && r.Completed).Select(r => r.Id).ToArray(), false);
        syncBtn.Click += (_, _) =>
        {
            try
            {
                if (!Fh5HotWheelsState.TryPreviewSyncMedals(_container, out var ch, out string reason)) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("Hot Wheels medals already match completed missions.", "info"); return; }
                if (!ConfirmExpansionAction($"Sync Hot Wheels medals bucket from {ch.PreviousMedals:N0} to {ch.NewMedals:N0}?")) return;
                if (!Fh5HotWheelsState.TrySyncMedals(_container, out ch, out reason)) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateMedalsBox(ch.NewMedals);
                RefreshSaveStateDisplays();
                RefreshUnlockTestDisplays($"Hot Wheels medals synced to {ch.NewMedals:N0}");
            }
            catch (Exception ex) { Fail("sync Hot Wheels medals", ex); }
        };

        Refresh();
        return Refresh;
    }

    Action AddFh5RallyGrid(Action refreshParentChips, Action<(string TeamName, int Before, int After)[]> updateTeamBoxes)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        var summary = Note("");
        var dim = (Brush)FindResource("Dim");
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var teamBox = MakeFilterCombo(210);
        var stateBox = MakeFilterCombo(145);
        stateBox.ItemsSource = new[] { "All challenges", "Incomplete only", "Completed only", "Supported only" };
        stateBox.SelectedIndex = 0;

        var search = new TextBox
        {
            Width = 220,
            Style = (Style)FindResource("Input"),
            Margin = new Thickness(0, 0, 10, 4),
            ToolTip = "Search Rally Adventure challenges by ID, team, name, or description",
        };
        var matches = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };

        filters.Children.Add(MakeFilterLabel("Team:", dim));
        filters.Children.Add(teamBox);
        filters.Children.Add(MakeFilterLabel("State:", dim));
        filters.Children.Add(stateBox);
        filters.Children.Add(MakeFilterLabel("Search:", dim));
        filters.Children.Add(search);
        filters.Children.Add(matches);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var completeSelBtn = TestButton("Fh5RallyCompleteSelectedBtn", "Complete selected", false);
        var completeAllBtn = TestButton("Fh5RallyCompleteAllBtn", "Complete all visible", false);
        var resetSelBtn = MakeExpansionButton("Fh5RallyResetSelectedBtn", "Reset selected", false, pink: false);
        var syncBtn = MakeExpansionButton("Fh5RallySyncRepBtn", "Sync team reps (floor)", false, pink: false);
        actions.Children.Add(completeSelBtn);
        actions.Children.Add(completeAllBtn);
        actions.Children.Add(resetSelBtn);
        actions.Children.Add(syncBtn);

        var grid = CreateDarkDataGrid();
        grid.Columns.Add(DarkTextColumn("ID", "Id", new DataGridLength(68)));
        grid.Columns.Add(DarkTextColumn("Team", "TeamName", new DataGridLength(180)));
        grid.Columns.Add(DarkTextColumn("Stage", "StageDisplay", new DataGridLength(68)));
        grid.Columns.Add(DarkTextColumn("Challenge", "Name", new DataGridLength(250)));
        grid.Columns.Add(DarkTextColumn("Rep", "Reputation", new DataGridLength(56)));
        grid.Columns.Add(DarkTextColumn("Done", "CompletedDisplay", new DataGridLength(56)));
        grid.Columns.Add(DarkTextColumn("Completed UTC", "CompletedUtc", new DataGridLength(130), "yyyy-MM-dd HH:mm"));
        grid.Columns.Add(DarkTextColumn("Status", "Compatibility", new DataGridLength(1, DataGridLengthUnitType.Star)));

        panel.Children.Add(summary);
        panel.Children.Add(filters);
        panel.Children.Add(actions);
        panel.Children.Add(grid);
        OverviewPanel.Children.Add(panel);

        Fh5RallyAdventureState.Row[] allRows = Array.Empty<Fh5RallyAdventureState.Row>();
        Fh5RallyAdventureState.Row[] visibleRows = Array.Empty<Fh5RallyAdventureState.Row>();

        void ApplyFilter()
        {
            string teamFilter = teamBox.SelectedItem as string ?? "All teams";
            int stateMode = stateBox.SelectedIndex;
            string q = search.Text?.Trim() ?? "";

            visibleRows = allRows.Where(r =>
            {
                if (teamFilter != "All teams" && r.TeamName != teamFilter) return false;
                if (stateMode == 1 && r.Completed) return false;
                if (stateMode == 2 && !r.Completed) return false;
                if (stateMode == 3 && !r.Supported) return false;
                if (q.Length > 0 &&
                    !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.TeamName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Id.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal))
                    return false;
                return true;
            }).ToArray();

            grid.ItemsSource = visibleRows;
            matches.Text = $"{visibleRows.Length:N0} / {allRows.Length:N0} shown · Ctrl+A selects visible";
            bool anySupported = allRows.Any(r => r.Supported);
            completeSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Any(r => r.Supported && r.Completed);
            completeAllBtn.IsEnabled = visibleRows.Any(r => r.Supported && !r.Completed);
            syncBtn.IsEnabled = allRows.Length > 0;
            foreach (var b in new[] { completeSelBtn, completeAllBtn, resetSelBtn, syncBtn }) b.Opacity = b.IsEnabled ? 1 : 0.45;
        }

        void Refresh()
        {
            if (!Fh5RallyAdventureState.TryRead(_container, out var view, out string err))
            {
                allRows = Array.Empty<Fh5RallyAdventureState.Row>();
                summary.Text = err;
                teamBox.ItemsSource = new[] { "All teams" };
                teamBox.SelectedIndex = 0;
                ApplyFilter();
                return;
            }

            allRows = view.Rows;
            string prevTeam = teamBox.SelectedItem as string;
            var teams = new[] { "All teams" }.Concat(allRows.Select(r => r.TeamName).Distinct()).ToArray();
            teamBox.ItemsSource = teams;
            teamBox.SelectedItem = prevTeam != null && teams.Contains(prevTeam) ? prevTeam : "All teams";

            summary.Text = $"{view.CompletedCount:N0} / {view.CatalogCount:N0} Rally Adventure team challenges completed ({view.SavedCount:N0} saved entries)."
                + (string.IsNullOrEmpty(view.StatusNote) ? "" : " " + view.StatusNote);
            ApplyFilter();
        }

        teamBox.SelectionChanged += (_, _) => ApplyFilter();
        stateBox.SelectionChanged += (_, _) => ApplyFilter();
        search.TextChanged += (_, _) => ApplyFilter();
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        grid.SelectionChanged += (_, _) =>
        {
            completeSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Any(r => r.Supported && r.Completed);
            completeSelBtn.Opacity = completeSelBtn.IsEnabled ? 1 : 0.45;
            resetSelBtn.Opacity = resetSelBtn.IsEnabled ? 1 : 0.45;
        };

        void RunEdit(Func<uint[]> pickIds, bool complete)
        {
            try
            {
                uint[] ids = pickIds();
                if (ids.Length == 0) { Log("No eligible Rally Adventure challenges match the action.", "info"); return; }
                bool ok = complete
                    ? Fh5RallyAdventureState.TryPreviewComplete(_container, ids, out var ch, out string reason)
                    : Fh5RallyAdventureState.TryPreviewReset(_container, ids, out ch, out reason);
                if (!ok) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("No Rally Adventure challenge changes needed.", "info"); return; }
                string verb = complete ? "Complete" : "Reset";
                string repSummary = string.Join(", ", ch.TeamChanges.Select(t => $"{t.TeamName.Split(" (")[0]}: {t.Before:N0}→{t.After:N0}"));
                if (!ConfirmExpansionAction($"{verb} {ids.Length:N0} Rally Adventure challenge(s)? {(repSummary.Length == 0 ? "" : "Reputation updates: " + repSummary)}")) return;
                bool applied = complete
                    ? Fh5RallyAdventureState.TryComplete(_container, ids, out ch, out reason)
                    : Fh5RallyAdventureState.TryReset(_container, ids, out ch, out reason);
                if (!applied) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateTeamBoxes(ch.TeamChanges);
                RefreshSaveStateDisplays();
                BuildProperties();
                RefreshUnlockTestDisplays($"Rally Adventure: {verb.ToLowerInvariant()}d {(complete ? ch.CompletedDelta : ch.ResetDelta):N0} challenge(s)");
            }
            catch (Exception ex) { Fail("update Rally Adventure challenges", ex); }
        }

        completeSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        completeAllBtn.Click += (_, _) => RunEdit(() => visibleRows.Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        resetSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh5RallyAdventureState.Row>().Where(r => r.Supported && r.Completed).Select(r => r.Id).ToArray(), false);
        syncBtn.Click += (_, _) =>
        {
            try
            {
                if (!Fh5RallyAdventureState.TryPreviewSyncReputation(_container, out var ch, out string reason)) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("Rally team reputation buckets already meet or exceed completed challenges.", "info"); return; }
                string repSummary = string.Join(", ", ch.TeamChanges.Select(t => $"{t.TeamName.Split(" (")[0]}: {t.Before:N0}→{t.After:N0}"));
                if (!ConfirmExpansionAction($"Raise Rally team reputation floor(s)? {repSummary}")) return;
                if (!Fh5RallyAdventureState.TrySyncReputation(_container, out ch, out reason)) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateTeamBoxes(ch.TeamChanges);
                RefreshSaveStateDisplays();
                RefreshUnlockTestDisplays($"Rally team reputations synced: {repSummary}");
            }
            catch (Exception ex) { Fail("sync Rally team reputations", ex); }
        };

        Refresh();
        return Refresh;
    }
#endif
}
