#nullable disable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    void AddFh4ExpansionsControls()
    {
        AddFh4FortuneIslandControls();
        AddFh4LegoValleyControls();
    }

    void AddFh4FortuneIslandControls()
    {
        if (!Fh4ExpansionsSaveState.TryReadExpansionStates(_container, out string ex1Intro, out _, out _))
            return;

        if (!Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.FortuneIslandXpObjectId,
                Fh4ExpansionsSaveState.FortuneIslandXpName, Fh4ExpansionsSaveState.FortuneIslandXpType,
                out bool exists, out int fiXp, out _))
            return;

        if (!exists && ex1Intro is "Absent" or "Initial")
            return;

        OverviewPanel.Children.Add(SectionHeader("FH4 Fortune Island"));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        void RefreshFiChips()
        {
            chips.Children.Clear();
            Fh4ExpansionsSaveState.TryReadExpansionStates(_container, out string ex1, out _, out _);
            chips.Children.Add(StatChip(ex1, "Intro state"));
            if (Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.FortuneIslandXpObjectId,
                    Fh4ExpansionsSaveState.FortuneIslandXpName, Fh4ExpansionsSaveState.FortuneIslandXpType,
                    out bool ex, out int xpVal, out _))
            {
                chips.Children.Add(StatChip(ex ? xpVal.ToString("N0", CultureInfo.CurrentCulture) : "0 (not created)", "Fortune Island Influence"));
            }
        }
        OverviewPanel.Children.Add(chips);
        RefreshFiChips();

        var fiRow = EditRow(
            "Fortune Island Influence (XP)",
            "Updates or creates CurrencyBucket_ExpansionOneCareerXP_TrackSpecificXP.Total.",
            fiXp.ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                if (!Fh4ExpansionsSaveState.TrySetOrCreateCurrencyBucket(_container, Fh4ExpansionsSaveState.FortuneIslandXpObjectId,
                        Fh4ExpansionsSaveState.FortuneIslandXpName, Fh4ExpansionsSaveState.FortuneIslandXpType, value,
                        out bool changed, out string reason))
                    return (false, string.IsNullOrEmpty(reason) ? "could not update Fortune Island Influence" : reason);
                if (changed)
                {
                    RefreshSaveStateDisplays();
                    RefreshFiChips();
                }
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        fiRow.Name = "Fh4FortuneIslandXpRow";
        OverviewPanel.Children.Add(fiRow);
    }

    void AddFh4LegoValleyControls()
    {
        OverviewPanel.Children.Add(SectionHeader("FH4 LEGO Speed Champions (LEGO Valley)"));

        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        OverviewPanel.Children.Add(chips);

        var boardBtn = MakeExpansionButton("Fh4LegoBoardsButton", "Smash all LEGO bonus boards", enabled: false);
        var landmarkBtn = MakeExpansionButton("Fh4LegoLandmarksButton", "Discover all LEGO landmarks", enabled: false);
        var worldBtns = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        worldBtns.Children.Add(boardBtn);
        worldBtns.Children.Add(landmarkBtn);
        var worldStatus = Note("");

        Action refreshGrid = null;
        FrameworkElement tiersRow = null;
        FrameworkElement houseRow = null;

        void RefreshChips()
        {
            chips.Children.Clear();
            Fh4ExpansionsSaveState.TryReadExpansionStates(_container, out _, out string ex2Intro, out string barnState);
            chips.Children.Add(StatChip(ex2Intro, "Intro state"));

            if (Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.BricksObjectId,
                    Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType,
                    out bool bEx, out int bricks, out _))
            {
                chips.Children.Add(StatChip(bEx ? $"{bricks:N0} / {Fh4ExpansionsSaveState.MaxLegoBricks:N0}" : "0 (not created)", "LEGO Bricks"));
            }
            if (Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.TiersObjectId,
                    Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType,
                    out bool tEx, out int tiers, out _))
            {
                chips.Children.Add(StatChip(tEx ? $"{tiers:N0} / 14" : "0 (not created)", "Brick Tiers"));
            }
            if (Fh4ExpansionsSaveState.TryReadHouseState(_container, out bool hEx, out bool hBuilt, out _, out int hLevel))
            {
                chips.Children.Add(StatChip(hEx ? Fh4ExpansionsSaveState.GetHouseStageDisplay(hLevel, hBuilt) : "Not built yet", "Master Builder's House"));
            }
            if (!string.IsNullOrEmpty(barnState) && barnState != "Absent")
                chips.Children.Add(StatChip(barnState, "LEGO barn find"));

            var statusParts = new List<string>();
            if (Fh4ExpansionsSaveState.TryReadLegoBoards(_container, out int bCol, out int bTot))
            {
                chips.Children.Add(StatChip(bTot > 0 ? $"{bCol:N0} / {bTot:N0}" : "0 (not visited)", "LEGO boards"));
                boardBtn.IsEnabled = bTot > 0 && bCol < bTot;
                statusParts.Add(bTot == 0 ? "Boards: visit LEGO Valley in-game to initialize" : $"Boards: {bCol:N0}/{bTot:N0} collected");
            }
            else
            {
                boardBtn.IsEnabled = false;
                statusParts.Add("Boards: unrecognized layout");
            }

            if (Fh4ExpansionsSaveState.TryReadLegoLandmarks(_container, out int lAct, out int lTot))
            {
                chips.Children.Add(StatChip(lTot > 0 ? $"{lAct:N0} / {lTot:N0}" : "0 (not visited)", "LEGO landmarks"));
                landmarkBtn.IsEnabled = lTot > 0 && lAct < lTot;
                statusParts.Add(lTot == 0 ? "Landmarks: visit LEGO Valley in-game to initialize" : $"Landmarks: {lAct:N0}/{lTot:N0} discovered");
            }
            else
            {
                landmarkBtn.IsEnabled = false;
                statusParts.Add("Landmarks: unrecognized layout");
            }

            boardBtn.Opacity = boardBtn.IsEnabled ? 1 : 0.45;
            landmarkBtn.Opacity = landmarkBtn.IsEnabled ? 1 : 0.45;

#if PROFILE_UNLOCK_TESTING
            if (Fh4RoadDiscoverySaveState.TryReadProfile(_container, out var roads) && roads.Ex2 is { } ex2Roads)
                chips.Children.Add(StatChip($"{ex2Roads.DiscoveredEntries:N0} / {ex2Roads.EntryCount:N0}", "LEGO Valley roads"));
#endif
            worldStatus.Text = string.Join(" · ", statusParts);
        }

        var syncCheckbox = new CheckBox
        {
            Content = "Auto-sync Challenge Tiers & Master Builder's House level when editing LEGO Bricks",
            IsChecked = true,
            Style = (Style)FindResource("PinkCheckBox"),
            Margin = new Thickness(0, 2, 0, 6),
        };
        OverviewPanel.Children.Add(syncCheckbox);

        Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.BricksObjectId,
            Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, out _, out int initBricks, out _);
        Fh4ExpansionsSaveState.TryReadCurrencyBucket(_container, Fh4ExpansionsSaveState.TiersObjectId,
            Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, out _, out int initTiers, out _);
        Fh4ExpansionsSaveState.TryReadHouseState(_container, out _, out _, out _, out int initHouse);

        var bricksRow = EditRow(
            "LEGO Bricks",
            $"Updates or creates CurrencyBucket_Bricks_bricks (272 challenges total {Fh4ExpansionsSaveState.MaxLegoBricks:N0} bricks).",
            initBricks.ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                bool sync = syncCheckbox.IsChecked == true;
                if (!Fh4ExpansionsSaveState.TrySetOrCreateCurrencyBucket(_container, Fh4ExpansionsSaveState.BricksObjectId,
                        Fh4ExpansionsSaveState.BricksName, Fh4ExpansionsSaveState.BricksType, value,
                        out bool changed, out string reason, syncTierAndHouseWithBricks: sync))
                    return (false, string.IsNullOrEmpty(reason) ? "could not update LEGO Bricks" : reason);
                if (changed)
                {
                    RefreshSaveStateDisplays();
                    RefreshChips();
                    if (sync)
                    {
                        var (t, h) = Fh4ExpansionsSaveState.ComputeTierAndHouseLevel(value);
                        SetEditRowText(tiersRow, t);
                        SetEditRowText(houseRow, h);
                    }
                    refreshGrid?.Invoke();
                }
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        bricksRow.Name = "Fh4LegoBricksRow";
        OverviewPanel.Children.Add(bricksRow);

        tiersRow = EditRow(
            "LEGO Brick Challenge Tiers",
            "Updates or creates CurrencyBucket_BrickChallengeTiers_tiers (0–14).",
            initTiers.ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 0 or > 14)
                    return (false, "enter a tier from 0 to 14");
                if (!Fh4ExpansionsSaveState.TrySetOrCreateCurrencyBucket(_container, Fh4ExpansionsSaveState.TiersObjectId,
                        Fh4ExpansionsSaveState.TiersName, Fh4ExpansionsSaveState.TiersType, value,
                        out bool changed, out string reason))
                    return (false, string.IsNullOrEmpty(reason) ? "could not update Brick Challenge Tiers" : reason);
                if (changed)
                {
                    RefreshSaveStateDisplays();
                    RefreshChips();
                    refreshGrid?.Invoke();
                }
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        tiersRow.Name = "Fh4LegoTiersRow";
        OverviewPanel.Children.Add(tiersRow);

        houseRow = EditRow(
            "Master Builder's House Level",
            "Updates or creates player_house_red_festival_save_state (Level 1–16; marks Built=true, Activated=true).",
            (initHouse <= 0 ? 1 : initHouse).ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 16)
                    return (false, "enter a level from 1 to 16");
                if (!Fh4ExpansionsSaveState.TrySetOrCreateHouseState(_container, value, out bool changed, out string reason))
                    return (false, string.IsNullOrEmpty(reason) ? "could not update Master Builder's House" : reason);
                if (changed)
                {
                    RefreshSaveStateDisplays();
                    RefreshChips();
                    refreshGrid?.Invoke();
                }
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        houseRow.Name = "Fh4LegoHouseRow";
        OverviewPanel.Children.Add(houseRow);

        boardBtn.Click += (_, _) =>
        {
            if (!ConfirmExpansionAction("Smash all 25 LEGO Valley bonus boards in your working save? Other progress stays unchanged.")) return;
            if (!Fh4ExpansionsSaveState.TrySmashAllLegoBoards(_container, out int smashed, out string err))
            {
                Log(err, "err");
                return;
            }
            RefreshChips();
            BuildProperties();
            RefreshSaveStateDisplays();
            Log($"LEGO Valley bonus boards: {smashed:n0} smashed", "ok");
        };

        landmarkBtn.Click += (_, _) =>
        {
            if (!ConfirmExpansionAction("Discover all LEGO Valley landmarks in your working save? Other progress stays unchanged.")) return;
            if (!Fh4ExpansionsSaveState.TryDiscoverAllLegoLandmarks(_container, out int discovered, out string err))
            {
                Log(err, "err");
                return;
            }
            RefreshChips();
            RefreshSaveStateDisplays();
            Log($"LEGO Valley landmarks: {discovered:n0} discovered", "ok");
        };

        OverviewPanel.Children.Add(worldBtns);
        OverviewPanel.Children.Add(worldStatus);
        RefreshChips();

#if PROFILE_UNLOCK_TESTING
        refreshGrid = AddFh4LegoBrickChallengesGrid(RefreshChips, (b, t, h) =>
        {
            SetEditRowText(bricksRow, b);
            SetEditRowText(tiersRow, t);
            SetEditRowText(houseRow, h);
        });
#endif
    }

    static void SetEditRowText(FrameworkElement row, int value)
    {
        if (row == null) return;
        foreach (var tb in FindVisualChildren<TextBox>(row))
        {
            tb.Text = value.ToString(CultureInfo.InvariantCulture);
            break;
        }
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

#if PROFILE_UNLOCK_TESTING
    Action AddFh4LegoBrickChallengesGrid(Action refreshParentChips, Action<int, int, int> updateBoxes)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        var summary = Note("");
        var dim = (Brush)FindResource("Dim");
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var catBox = MakeFilterCombo(150);
        var tierBox = MakeFilterCombo(120);
        var stateBox = MakeFilterCombo(145);
        stateBox.ItemsSource = new[] { "All challenges", "Incomplete only", "Completed only", "Supported only" };
        stateBox.SelectedIndex = 0;

        var search = new TextBox
        {
            Width = 200,
            Style = (Style)FindResource("Input"),
            Margin = new Thickness(0, 0, 10, 4),
            ToolTip = "Search LEGO Brick Challenges by ID, category, tier, name, or description",
        };
        var matches = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };

        filters.Children.Add(MakeFilterLabel("Category:", dim));
        filters.Children.Add(catBox);
        filters.Children.Add(MakeFilterLabel("Tier:", dim));
        filters.Children.Add(tierBox);
        filters.Children.Add(MakeFilterLabel("State:", dim));
        filters.Children.Add(stateBox);
        filters.Children.Add(MakeFilterLabel("Search:", dim));
        filters.Children.Add(search);
        filters.Children.Add(matches);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var completeSelBtn = TestButton("Fh4BrickCompleteSelectedBtn", "Complete selected", false);
        var completeAllBtn = TestButton("Fh4BrickCompleteAllBtn", "Complete all visible", false);
        var resetSelBtn = MakeExpansionButton("Fh4BrickResetSelectedBtn", "Reset selected", false, pink: false);
        var syncBtn = MakeExpansionButton("Fh4BrickSyncBtn", "Sync Bricks, Tiers & House", false, pink: false);
        actions.Children.Add(completeSelBtn);
        actions.Children.Add(completeAllBtn);
        actions.Children.Add(resetSelBtn);
        actions.Children.Add(syncBtn);

        var grid = CreateDarkDataGrid();
        grid.Columns.Add(DarkTextColumn("ID", "Id", new DataGridLength(56)));
        grid.Columns.Add(DarkTextColumn("Tier", "TierDisplay", new DataGridLength(68)));
        grid.Columns.Add(DarkTextColumn("Category", "Category", new DataGridLength(105)));
        grid.Columns.Add(DarkTextColumn("Challenge", "Name", new DataGridLength(250)));
        grid.Columns.Add(DarkTextColumn("Bricks", "Bricks", new DataGridLength(60)));
        grid.Columns.Add(DarkTextColumn("Done", "CompletedDisplay", new DataGridLength(56)));
        grid.Columns.Add(DarkTextColumn("Status", "Compatibility", new DataGridLength(1, DataGridLengthUnitType.Star)));

        panel.Children.Add(summary);
        panel.Children.Add(filters);
        panel.Children.Add(actions);
        panel.Children.Add(grid);
        OverviewPanel.Children.Add(panel);

        Fh4BrickChallengesState.Row[] allRows = Array.Empty<Fh4BrickChallengesState.Row>();
        Fh4BrickChallengesState.Row[] visibleRows = Array.Empty<Fh4BrickChallengesState.Row>();

        void ApplyFilter()
        {
            string catFilter = catBox.SelectedItem as string ?? "All categories";
            string tierFilter = tierBox.SelectedItem as string ?? "All tiers";
            int stateMode = stateBox.SelectedIndex;
            string q = search.Text?.Trim() ?? "";

            visibleRows = allRows.Where(r =>
            {
                if (catFilter != "All categories" && r.Category != catFilter) return false;
                if (tierFilter != "All tiers" && r.TierDisplay != tierFilter) return false;
                if (stateMode == 1 && r.Completed) return false;
                if (stateMode == 2 && !r.Completed) return false;
                if (stateMode == 3 && !r.Supported) return false;
                if (q.Length > 0 &&
                    !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Category.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !r.Id.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal))
                    return false;
                return true;
            }).ToArray();

            grid.ItemsSource = visibleRows;
            matches.Text = $"{visibleRows.Length:N0} / {allRows.Length:N0} shown · Ctrl+A selects visible";
            completeSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Any(r => r.Supported && r.Completed);
            completeAllBtn.IsEnabled = visibleRows.Any(r => r.Supported && !r.Completed);
            syncBtn.IsEnabled = allRows.Length > 0;
            foreach (var b in new[] { completeSelBtn, completeAllBtn, resetSelBtn, syncBtn }) b.Opacity = b.IsEnabled ? 1 : 0.45;
        }

        void Refresh()
        {
            if (!Fh4BrickChallengesState.TryRead(_container, out var view, out string err))
            {
                allRows = Array.Empty<Fh4BrickChallengesState.Row>();
                summary.Text = err;
                catBox.ItemsSource = new[] { "All categories" };
                catBox.SelectedIndex = 0;
                tierBox.ItemsSource = new[] { "All tiers" };
                tierBox.SelectedIndex = 0;
                ApplyFilter();
                return;
            }

            allRows = view.Rows;
            string prevCat = catBox.SelectedItem as string;
            var cats = new[] { "All categories" }.Concat(allRows.Select(r => r.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal)).ToArray();
            catBox.ItemsSource = cats;
            catBox.SelectedItem = prevCat != null && cats.Contains(prevCat) ? prevCat : "All categories";

            string prevTier = tierBox.SelectedItem as string;
            var tiers = new[] { "All tiers" }.Concat(allRows.Select(r => r.UnlockTier).Distinct().OrderBy(t => t).Select(t => $"Tier {t}")).ToArray();
            tierBox.ItemsSource = tiers;
            tierBox.SelectedItem = prevTier != null && tiers.Contains(prevTier) ? prevTier : "All tiers";

            summary.Text = $"{view.CompletedCount:N0} / {view.CatalogCount:N0} LEGO Brick Challenges completed ({view.CompletedBricks:N0} bricks earned; saved: {view.SavedBricks:N0})."
                + (string.IsNullOrEmpty(view.StatusNote) ? "" : " " + view.StatusNote);
            ApplyFilter();
        }

        catBox.SelectionChanged += (_, _) => ApplyFilter();
        tierBox.SelectionChanged += (_, _) => ApplyFilter();
        stateBox.SelectionChanged += (_, _) => ApplyFilter();
        search.TextChanged += (_, _) => ApplyFilter();
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        grid.SelectionChanged += (_, _) =>
        {
            completeSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Any(r => r.Supported && !r.Completed);
            resetSelBtn.IsEnabled = grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Any(r => r.Supported && r.Completed);
            completeSelBtn.Opacity = completeSelBtn.IsEnabled ? 1 : 0.45;
            resetSelBtn.Opacity = resetSelBtn.IsEnabled ? 1 : 0.45;
        };

        void RunEdit(Func<uint[]> pickIds, bool complete)
        {
            try
            {
                uint[] ids = pickIds();
                if (ids.Length == 0) { Log("No eligible LEGO Brick Challenges match the action.", "info"); return; }
                bool ok = complete
                    ? Fh4BrickChallengesState.TryPreviewComplete(_container, ids, out var ch, out string reason)
                    : Fh4BrickChallengesState.TryPreviewReset(_container, ids, out ch, out reason);
                if (!ok) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("No LEGO Brick Challenge changes needed.", "info"); return; }
                string verb = complete ? "Complete" : "Reset";
                if (!ConfirmExpansionAction($"{verb} {ids.Length:N0} LEGO Brick Challenge(s)? Bricks: {ch.PreviousBricks:N0} → {ch.NewBricks:N0} · House Level: {ch.PreviousHouseLevel} → {ch.NewHouseLevel}.")) return;
                bool applied = complete
                    ? Fh4BrickChallengesState.TryComplete(_container, ids, out ch, out reason)
                    : Fh4BrickChallengesState.TryReset(_container, ids, out ch, out reason);
                if (!applied) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateBoxes(ch.NewBricks, ch.NewTiers, ch.NewHouseLevel);
                RefreshSaveStateDisplays();
                RefreshUnlockTestDisplays($"LEGO Valley: {verb.ToLowerInvariant()}d {(complete ? ch.CompletedDelta : ch.ResetDelta):N0} challenge(s) · {ch.NewBricks:N0} bricks");
            }
            catch (Exception ex) { Fail("update LEGO Brick Challenges", ex); }
        }

        completeSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        completeAllBtn.Click += (_, _) => RunEdit(() => visibleRows.Where(r => r.Supported && !r.Completed).Select(r => r.Id).ToArray(), true);
        resetSelBtn.Click += (_, _) => RunEdit(() => grid.SelectedItems.OfType<Fh4BrickChallengesState.Row>().Where(r => r.Supported && r.Completed).Select(r => r.Id).ToArray(), false);
        syncBtn.Click += (_, _) =>
        {
            try
            {
                if (!Fh4BrickChallengesState.TryPreviewSyncBricks(_container, out var ch, out string reason)) { Log(reason, "err"); return; }
                if (!ch.Changed) { Log("LEGO Bricks, Tiers, and Master Builder's House already match completed challenges.", "info"); return; }
                if (!ConfirmExpansionAction($"Sync Bricks ({ch.PreviousBricks:N0}→{ch.NewBricks:N0}), Tiers ({ch.PreviousTiers}→{ch.NewTiers}), and House Level ({ch.PreviousHouseLevel}→{ch.NewHouseLevel})?")) return;
                if (!Fh4BrickChallengesState.TrySyncBricks(_container, out ch, out reason)) { Log(reason, "err"); return; }
                _hasUnlockTestEdits = true;
                Refresh();
                refreshParentChips();
                updateBoxes(ch.NewBricks, ch.NewTiers, ch.NewHouseLevel);
                RefreshSaveStateDisplays();
                RefreshUnlockTestDisplays($"LEGO Valley synced: {ch.NewBricks:N0} bricks · Tier {ch.NewTiers} · House Lv {ch.NewHouseLevel}");
            }
            catch (Exception ex) { Fail("sync LEGO Valley state", ex); }
        };

        Refresh();
        return Refresh;
    }
#endif
}
