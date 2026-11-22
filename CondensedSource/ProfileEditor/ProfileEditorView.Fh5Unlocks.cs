#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;
using Microsoft.Win32;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    void AddFh5GameDataControls()
    {
        var data = Fh5ProfileGameData.GetStatus(_container);
        var button = new Button
        {
            Name = "ProfileGameDataButton",
            Content = "Choose FH5 game data…",
            Style = (Style)FindResource("PinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var status = Note(data.Message);
        status.Name = "ProfileGameDataStatus";
        status.ToolTip = data.Source + "\n" + data.Message;
        OverviewPanel.Children.Add(status);
        OverviewPanel.Children.Add(button);
        button.Click += (_, _) => ChooseFh5ProfileGameData();
    }

    void ChooseFh5ProfileGameData()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select media\\stripped\\TIDETables.zip from your FH5 install",
            Filter = "FH5 TIDE tables (TIDETables.zip)|TIDETables.zip|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        var result = Fh5ProfileGameData.Attach(_container, dialog.FileName, remember: true);
        Log(result.Message, result.Loaded ? "ok" : "err");
        BuildOverview();
    }

    int AddFh5OnlineXpControls()
    {
        _refreshOnlineXpPreview = null;
        if (!Fh5OnlineXpState.TryRead(_container, out var level, out var reason))
        {
            OverviewPanel.Children.Add(Note("Online XP: " + reason));
            return 0;
        }
        var summary = Note($"Online rank: {level.Number:n0} (Horizon Open)");
        summary.Name = "OnlineLevelSummary";
        var preview = Note("");
        preview.Name = "OnlineXpPreview";
        var row = EditRow("Online XP", "Horizon Open XP (Ranks 1–1,000); also syncs driver XP and earned-XP totals by the same amount",
            level.Xp.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (!uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value > int.MaxValue)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                if (!Fh5OnlineXpState.TrySet(_container, value, out bool changed, out string error))
                    return (false, error);
                _hasUnlockTestEdits |= changed;
                BuildProperties();
                RefreshSaveStateDisplays();
                BuildOverview();
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        row.Name = "OnlineXpRow";
        var input = row.Children.OfType<TextBox>().Single();
        input.Name = "OnlineXpInput";
        row.Children.OfType<Button>().Single().Name = "OnlineXpApply";

        void Preview()
        {
            if (!uint.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            {
                preview.Text = "Online XP preview: enter a whole number from 0 to 2,147,483,647.";
                return;
            }
            if (!Fh5OnlineXpState.TryPreview(_container, value, out var result, out string error))
            {
                preview.Text = "Online XP preview unavailable: " + error;
                return;
            }
            preview.Text = $"Preview: online rank {result.Online.Number:n0}"
                + (result.Online.ToNextLevel is { } next ? $" · {next:n0} XP to next" : " · Maximum mapped rank")
                + $" | Driver: {result.Driver.Display}. Rewards are not claimed.";
        }

        _refreshOnlineXpPreview = Preview;
        input.TextChanged += (_, _) => Preview();
        Preview();
        OverviewPanel.Children.Add(summary);
        OverviewPanel.Children.Add(row);
        OverviewPanel.Children.Add(preview);
        return 2;
    }

    void AddFh5RoadDiscoveryControls()
    {
        bool supported = Fh5RoadDiscoverySaveState.TryReadProfile(_container, out _, out uint roads);
        uint targetRoads = Fh5ProfileGameData.For(_container)?.RoadTargetCount ?? Fh5ProfileGameData.DefaultRoadTargetCount;

        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var button = new Button
        {
            Name = "DiscoverAllRoadsButton",
            Content = "Discover all roads",
            Style = (Style)FindResource("PinkButton"),
            IsEnabled = supported,
            ToolTip = $"Discovers all {targetRoads:n0} roads on the recognized FH5 Mexico map and updates the saved road statistic. Export save writes a separate file.",
        };
        var status = new TextBlock
        {
            Name = "RoadDiscoveryStatus",
            Foreground = BrDim,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Text = supported
                ? $"{roads:n0} / {targetRoads:n0} roads (saved statistic)"
                : "This save's road map and statistic are not safely recognized.",
        };
        Grid.SetColumn(status, 1);
        row.Children.Add(button);
        row.Children.Add(status);
        OverviewPanel.Children.Add(row);
        OverviewPanel.Children.Add(Note($"Sets RoadsDiscovered to {targetRoads:n0} and marks all 24,498 Hendrix road segments discovered. "
            + "Other Discovery progress, rewards and career records stay unchanged; keep a backup of the original save."));
        button.Click += (_, _) =>
        {
            try
            {
                DiscoverAllFh5Roads();
                if (Fh5RoadDiscoverySaveState.TryReadProfile(_container, out _, out uint updatedRoads))
                    status.Text = $"{updatedRoads:n0} / {targetRoads:n0} roads — map bitmap filled; click Export save";
            }
            catch (Exception ex) { Fail("discover all FH5 roads", ex); }
        };
    }

    void DiscoverAllFh5Roads()
    {
        uint targetRoads = Fh5ProfileGameData.For(_container)?.RoadTargetCount ?? Fh5ProfileGameData.DefaultRoadTargetCount;
        if (!Fh5RoadDiscoverySaveState.TryDiscoverAll(_container, out var before, out uint previousRoads))
            throw new InvalidDataException("This save's road map and statistic are not safely recognized. Nothing was changed.");
        _hasUnlockTestEdits = true;
        RenderProperties();
        BuildCareer();
        Log($"FH5 road discovery: {before.DiscoveredEntries:n0} → {before.EntryCount:n0} bitmap entries; "
            + $"RoadsDiscovered {previousRoads:n0} → {targetRoads:n0}; other progression/rewards unchanged — click Export save", "ok");
        StatusLine.Text = "Road map bitmap and statistic edited in the working copy — click Export save";
    }

    void AddFh5UnlockControls()
    {
        OverviewPanel.Children.Add(SectionHeader("Unlocks & progression"));
        var data = Fh5ProfileGameData.For(_container);
        if (data == null)
        {
            OverviewPanel.Children.Add(Note("FH5 game data (TIDETables.zip) is not loaded yet. Click 'Choose FH5 game data…' above to enable clothing, horns, emotes, Forza Link phrases, badges, and Accolades."));
        }
        else
        {
            OverviewPanel.Children.Add(Note("Search and choose items below each button. Ctrl+A in a list selects its visible items. Export save writes a separate file."));
        }

        AddUnlockPicker("Clothing", "UnlockAllClothesButton", "clothes",
            () => data != null && Fh5ClothingUnlockState.TryReadUnlocked(_container, out var owned)
                ? data.Clothing.Select(c => new UnlockItem(c.Id, c.Name, c.Category, owned.Contains(c.Id))).ToArray()
                : null,
            selected => { bool ok = Fh5ClothingUnlockState.TryUnlockSelected(_container, selected, out int changed); return (ok, changed); },
            "Non-wardrobe items, body presets, equipped outfit and existing NEW flags stay unchanged.");

        AddUnlockPicker("Horn", "UnlockAllHornsButton", "horns",
            () => data != null && Fh5CarHornUnlockState.TryReadUnlocked(_container, out var owned)
                ? data.Horns.Select(h => new UnlockItem(h.Id, h.Name, h.CategoryName, owned.Contains(h.Id))).ToArray()
                : null,
            selected => { bool ok = Fh5CarHornUnlockState.TryUnlockSelected(_container, selected, out int changed); return (ok, changed); },
            "Your equipped horn and existing order stay unchanged.");

        AddUnlockPicker("Emote", "UnlockAllEmotesButton", "emotes",
            () => data != null && Fh5EmoteUnlockState.TryReadUnlocked(_container, out var owned)
                ? data.Emotes.Select(e => new UnlockItem(e.Id, e.Name, e.Rarity, owned.Contains(e.Id))).ToArray()
                : null,
            selected => { bool ok = Fh5EmoteUnlockState.TryUnlockSelected(_container, selected, out int changed); return (ok, changed); },
            "Both equipped emote slots stay unchanged.");

        AddUnlockPicker("ForzaLink", "UnlockForzaLinkPhrasesButton", "Forza Link phrases",
            () => data != null && Fh5ForzaLinkUnlockState.TryReadUnlocked(_container, out var owned)
                ? data.Phrases.Select(p => new UnlockItem(p.Id, p.Name, p.Category, owned.Contains(p.Id))).ToArray()
                : null,
            selected => { bool ok = Fh5ForzaLinkUnlockState.TryUnlockSelected(_container, selected, out int changed); return (ok, changed); },
            "All equipped chat context slots stay unchanged.");

        AddFh5BadgeUnlockControls();
        AddFh5AccoladesControls();
    }

    void AddFh5BadgeUnlockControls()
    {
        var dateInput = new TextBox
        {
            Name = "BadgeEarnedDateInput",
            Style = (Style)FindResource("Input"),
            MinWidth = 255,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 8, 0),
            ToolTip = "UTC: yyyy-MM-dd HH:mm:ss (optional .ffffff). Blank on unlock uses current UTC time.",
        };
        DateTimeOffset? RequestedDate()
        {
            if (string.IsNullOrWhiteSpace(dateInput.Text)) return null;
            if (!TryBadgeDate(dateInput.Text, out var utc))
                throw new InvalidDataException("Enter a UTC date as yyyy-MM-dd HH:mm:ss, for example 2024-06-20 18:00:00.");
            return utc;
        }

        AddUnlockPicker("Badge", "UnlockSelectedBadgesButton", "badges",
            () => Fh5BadgeUnlockState.TryRead(_container, out var rows)
                ? rows.Select(b => new UnlockItem(b.Id, b.Name, b.Category, b.Unlocked, b.Supported, b.Description,
                    b.Unlocked ? b.EarnedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "date not set" : "")).ToArray()
                : null,
            selected =>
            {
                if (!Fh5BadgeUnlockState.TryUnlockSelected(_container, selected, RequestedDate(), out int changed, out string error))
                    throw new InvalidDataException(error);
                return (true, changed);
            },
            "Only badges with BadgeChallengeSaveState records are listed (early saves without BadgeChallengeSaveState remain disabled). Existing dates stay unchanged.",
            (panel, list, refresh) =>
            {
                var label = Note("Earned date (UTC)");
                label.Margin = new Thickness(4, 8, 0, 0);
                panel.Children.Add(label);
                var controls = new WrapPanel();
                controls.Children.Add(dateInput);
                var setDate = TestButton("BadgeApplyEarnedDateButton", "Set earned date", false);
                setDate.Margin = new Thickness(0, 4, 0, 0);
                controls.Children.Add(setDate);
                panel.Children.Add(controls);
                var hint = Note("");
                hint.Name = "BadgeEarnedDateHint";
                panel.Children.Add(hint);

                Fh5BadgeUnlockState.Row[] Selected()
                {
                    if (!Fh5BadgeUnlockState.TryRead(_container, out var current)) return Array.Empty<Fh5BadgeUnlockState.Row>();
                    var ids = list.SelectedItems.OfType<UnlockItem>().Select(i => (uint)i.Id).ToHashSet();
                    return current.Where(b => ids.Contains(b.Id)).ToArray();
                }
                void EnableDateAction()
                {
                    var selected = Selected();
                    setDate.IsEnabled = selected.Length > 0 && selected.All(b => b.Unlocked && b.Supported) && TryBadgeDate(dateInput.Text, out _);
                    setDate.Opacity = setDate.IsEnabled ? 1 : 0.45;
                }
                void ShowSelection()
                {
                    var selected = Selected();
                    dateInput.Text = "";
                    if (selected.Length == 1)
                    {
                        var badge = selected[0];
                        if (badge.Unlocked && badge.EarnedUtc is { } date)
                            dateInput.Text = date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                        hint.Text = "Horizon Badge: enter any UTC date up to current time (blank uses current UTC time).";
                        if (!badge.Supported) hint.Text += " Definition changed or missing; this badge is read-only.";
                    }
                    else
                    {
                        hint.Text = selected.Length == 0 ? "Choose badges above." : "One entered date applies to all selected unlocked badges.";
                    }
                    EnableDateAction();
                }
                list.SelectionChanged += (_, _) => ShowSelection();
                dateInput.TextChanged += (_, _) => EnableDateAction();
                setDate.Click += (_, _) =>
                {
                    try
                    {
                        var selected = Selected();
                        var requested = RequestedDate();
                        if (requested is null || selected.Length == 0 || selected.Any(b => !b.Unlocked || !b.Supported)) return;
                        if (!_confirmUnlockTest($"Set the earned date of {selected.Length} selected unlocked FH5 badges to {requested:yyyy-MM-dd HH:mm:ss.ffffff} UTC? Only their dates change. Export save writes a separate file."))
                            return;
                        if (!Fh5BadgeUnlockState.TrySetEarnedDate(_container, selected.Select(b => (ulong)b.Id), requested.Value, out int changed, out string failure))
                            throw new InvalidDataException(failure);
                        _hasUnlockTestEdits |= changed > 0;
                        refresh();
                        RefreshUnlockTestDisplays($"Badges: {changed} earned dates updated");
                    }
                    catch (Exception ex) { Fail("set FH5 badge earned date", ex); }
                };
                ShowSelection();
            });
    }
}
#endif
