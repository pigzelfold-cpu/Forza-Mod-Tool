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
    void BuildFh4Overview()
    {
        OverviewPanel.Children.Clear();
        if (_container == null) return;

        OverviewPanel.Children.Add(SectionHeader("Your save at a glance"));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        void AddTableChip(string table, string label)
        {
            long count = TableCountOrNeg(table);
            chips.Children.Add(StatChip(count >= 0 ? count.ToString("N0", CultureInfo.CurrentCulture) : "—", label));
        }

        AddTableChip("Career_Garage", "Owned cars");
        AddTableChip("FreeCars", "Free cars");
        AddTableChip("BarnFinds", "Barn finds");
        AddTableChip("Career_PurchasedParts", "Purchased parts");
        AddTableChip("PhotoCaptures", "Saved photos");
        int editableSettings = _container?.Properties?.Walk().Count(p => p.Node.IsEditable) ?? 0;
        chips.Children.Add(StatChip(editableSettings.ToString("N0", CultureInfo.CurrentCulture), "Editable settings"));
        OverviewPanel.Children.Add(chips);

#if PROFILE_UNLOCK_TESTING
        AddFh4GameDataControls();
#endif

        OverviewPanel.Children.Add(SectionHeader("Everyday values"));
#if PROFILE_UNLOCK_TESTING
        _refreshOnlineXpPreview = null;
#endif
        int shown = 0;
        if (AddProfileEdit("/Main/Credits", "Credits", "Current wallet balance")) shown++;
        if (AddProfileEdit("/Main/TotalCredits", "Total credits earned", "Lifetime credits counter")) shown++;
#if PROFILE_UNLOCK_TESTING
        shown += AddDriverXpControls();
#endif
        if (AddProfileEdit("/Main/TotalXP", "Total XP", "Lifetime career XP statistic")) shown++;
        if (AddFh4SkillPointsEdit()) shown++;
        if (AddFh4WheelspinsEdit(super: false, "Wheelspins", "Standard Wheelspin count in SaveState (TombolaState)")) shown++;
        if (AddFh4WheelspinsEdit(super: true, "Super Wheelspins", "Super Wheelspin count in SaveState (TombolaState)")) shown++;
        if (AddFh4ObjectIntegerEdit("CurrencyBucket_ForzathonPoints_forzathon_points", "Total", "Forzathon Points", "Forzathon Points stored in SaveState (CurrencyBucket)")) shown++;
        if (shown == 0)
            OverviewPanel.Children.Add(Note("No recognized everyday value paths were found in this FH4 profile."));

        AddFh4ExpansionsControls();

        OverviewPanel.Children.Add(SectionHeader("World and unlocks"));
        var worldChips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        long barnCount = TableCountOrNeg("BarnFinds");
        worldChips.Children.Add(StatChip(barnCount >= 0 ? barnCount.ToString("N0", CultureInfo.CurrentCulture) : "—", "Barn finds"));
        long carExpCount = TableCountOrNeg("CarExperienceUnlocks");
        worldChips.Children.Add(StatChip(carExpCount >= 0 ? carExpCount.ToString("N0", CultureInfo.CurrentCulture) : "—", "Car Mastery unlocks"));
#if PROFILE_UNLOCK_TESTING
        if (Fh4ClothingUnlockState.TryReadUnlocked(_container, out var ownedClothes))
            worldChips.Children.Add(StatChip(ownedClothes.Length.ToString("N0", CultureInfo.CurrentCulture), "Owned clothing"));
#endif
        var festivalSites = Fh4ProfileHelper.FindObjectsByType(_container?.SaveState, "FestivalSiteState").ToArray();
        if (festivalSites.Length > 0)
        {
            int built = festivalSites.Count(site => IsBxmlTrue(Fh4ProfileHelper.GetPropertyValue(_container?.SaveState, site, "Built")));
            int activated = festivalSites.Count(site => IsBxmlTrue(Fh4ProfileHelper.GetPropertyValue(_container?.SaveState, site, "Activated")));
            worldChips.Children.Add(StatChip($"{built}/{festivalSites.Length}", "Festival sites built"));
            worldChips.Children.Add(StatChip($"{activated}/{festivalSites.Length}", "Festival sites activated"));
        }
        OverviewPanel.Children.Add(worldChips);

#if PROFILE_UNLOCK_TESTING
        AddFh4RoadDiscoveryControls();
        AddFh4UnlockControls();
#endif

        OverviewPanel.Children.Add(Note("Festival Playlist history (FestivalPassSaveState) and server-backed records are left untouched."));

        OverviewPanel.Children.Add(SectionHeader("Save account"));
        OverviewPanel.Children.Add(Note("FH4 Steam profiles bind ownership externally through your Steam user folder and container metadata; no embedded XUID is stored in ProfileData."));

        OverviewPanel.Children.Add(Note("Advanced users have the full Cars & Data, Profile Values, Save State XML, and Career views. "
            + "Nothing is written to disk until you click Export save."));
    }

    bool AddFh4SkillPointsEdit()
    {
        var perkObj = Fh4ProfileHelper.FindObject(_container?.SaveState, "CarPerkSaveState");
        var bxmlProp = Fh4ProfileHelper.GetPropertyNode(_container?.SaveState, perkObj, "CurrentSkillTokensTotal");
        string bxmlRaw = bxmlProp != null ? _container?.SaveState?.GetAttribute(bxmlProp, "Value") : null;
        var treeNode = _container?.Properties?.Walk().FirstOrDefault(p => p.Path == "/Main/NumSkillTokens").Node;

        if (bxmlProp == null && treeNode == null) return false;

        string currentText = bxmlRaw ?? treeNode?.DisplayValue() ?? "0";
        var row = EditRow(
            "Skill Points",
            "Updates CarPerkSaveState.CurrentSkillTokensTotal and /Main/NumSkillTokens (leaves UnspentSkillPointsTotal progress untouched)",
            currentText,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");

                string formatted = value.ToString(CultureInfo.InvariantCulture);
                string oldBxml = bxmlProp != null && _container?.SaveState != null ? _container.SaveState.GetAttribute(bxmlProp, "Value") : null;
                if (bxmlProp != null && _container?.SaveState != null && !_container.SaveState.SetAttribute(bxmlProp, "Value", formatted))
                    return (false, "this value could not be updated");
                if (treeNode != null && !treeNode.TrySetFromText(formatted))
                {
                    if (bxmlProp != null && _container?.SaveState != null && oldBxml != null)
                        _container.SaveState.SetAttribute(bxmlProp, "Value", oldBxml);
                    return (false, "could not update /Main/NumSkillTokens");
                }

                BuildProperties();
                RefreshSaveStateDisplays();
                return (true, formatted);
            });
        row.Name = "Fh4SkillPointsRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

    bool AddFh4WheelspinsEdit(bool super, string label, string hint)
    {
        var bx = _container?.SaveState;
        var tombola = Fh4ProfileHelper.FindObject(bx, "TombolaState");
        var spinInfo = Fh4ProfileHelper.GetPropertyNode(bx, tombola, super ? "SuperSpinInfo" : "NormalSpinInfo");
        var spinsNode = Fh4ProfileHelper.GetPropertyNode(bx, spinInfo, "Spins");
        string current = spinsNode != null ? bx?.GetAttribute(spinsNode, "Value") : null;
        if (spinsNode == null || current == null) return false;

        var row = EditRow(label, hint, current, text =>
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                return (false, "enter a whole number from 0 to 2,147,483,647");

            string formatted = value.ToString(CultureInfo.InvariantCulture);
            if (!bx.SetAttribute(spinsNode, "Value", formatted))
                return (false, "this value could not be updated");
            RefreshSaveStateDisplays();
            return (true, formatted);
        });
        row.Name = super ? "Fh4SuperWheelspinsRow" : "Fh4WheelspinsRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

    bool AddFh4ObjectIntegerEdit(string objectId, string propName, string label, string hint)
    {
        var obj = Fh4ProfileHelper.FindObject(_container?.SaveState, objectId);
        var prop = Fh4ProfileHelper.GetPropertyNode(_container?.SaveState, obj, propName);
        if (prop == null || _container?.SaveState == null) return false;

        string current = _container.SaveState.GetAttribute(prop, "Value") ?? "0";
        var row = EditRow(label, hint, current, text =>
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                return (false, "enter a whole number from 0 to 2,147,483,647");

            string formatted = value.ToString(CultureInfo.InvariantCulture);
            if (!_container.SaveState.SetAttribute(prop, "Value", formatted))
                return (false, "this value could not be updated");
            RefreshSaveStateDisplays();
            return (true, formatted);
        });
        row.Name = "Fh4" + objectId + propName + "Row";
        OverviewPanel.Children.Add(row);
        return true;
    }

#if PROFILE_UNLOCK_TESTING
    void AddFh4GameDataControls()
    {
        var data = Fh4ProfileGameData.GetStatus(_container);
        var button = new Button
        {
            Name = "Fh4GameDataButton",
            Content = "Choose FH4 game data…",
            Style = (Style)FindResource("PinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var status = Note(data.Message);
        status.Name = "Fh4GameDataStatus";
        status.ToolTip = data.Source + "\n" + data.Message;
        OverviewPanel.Children.Add(status);
        OverviewPanel.Children.Add(button);
        button.Click += (_, _) => ChooseFh4ProfileGameData();
    }

    void ChooseFh4ProfileGameData()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select media\\stripped\\TIDETables.zip (or an XML inside mediapc\\stripped\\TIDETables) from your FH4 install",
            Filter = "FH4 TIDE tables (TIDETables.zip;*.xml)|TIDETables.zip;*.xml|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        string target = dialog.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName
            : dialog.FileName;
        var result = Fh4ProfileGameData.Attach(_container, target, remember: true);
        Log(result.Message, result.Loaded ? "ok" : "err");
        BuildOverview();
    }

    void AddFh4RoadDiscoveryControls()
    {
        bool recognized = Fh4RoadDiscoverySaveState.TryReadProfile(_container, out var summary);
        var btn = TestButton("Fh4DiscoverRoadsButton", "Discover all roads", recognized);
        btn.ToolTip = "Marks every valid road segment bit discovered across Great Britain (and Fortune Island / LEGO Valley when present) and updates /CompareStats/RoadsDiscovered.";

        string statusText;
        if (!recognized)
        {
            statusText = "Road discovery layout was not safely recognized in this profile; road discovery editing is disabled.";
        }
        else
        {
            var parts = new List<string>
            {
                $"Great Britain: {summary.Main.DiscoveredEntries:N0} / {summary.Main.EntryCount:N0} road bits ({summary.Roads:N0} roads)"
            };
            if (summary.Ex1 is { } ex1)
                parts.Add($"Fortune Island: {ex1.DiscoveredEntries:N0} / {ex1.EntryCount:N0} bits");
            if (summary.Ex2 is { } ex2)
                parts.Add($"LEGO Valley: {ex2.DiscoveredEntries:N0} / {ex2.EntryCount:N0} bits");
            statusText = string.Join(" · ", parts);
        }

        var note = Note(statusText);
        note.Name = "Fh4RoadDiscoveryStatus";
        OverviewPanel.Children.Add(btn);
        OverviewPanel.Children.Add(note);

        btn.Click += (_, _) =>
        {
            try
            {
                if (!Fh4RoadDiscoverySaveState.TryReadProfile(_container, out var current))
                    throw new InvalidDataException("Road discovery layout is not safely recognized. Nothing was changed.");

                uint targetRoads = Fh4ProfileGameData.For(_container)?.RoadTargetCount ?? Fh4ProfileGameData.DefaultRoadTargetCount;
                bool allDone = current.Main.DiscoveredEntries == current.Main.EntryCount
                    && current.Roads >= targetRoads
                    && (current.Ex1 == null || current.Ex1.Value.DiscoveredEntries == current.Ex1.Value.EntryCount)
                    && (current.Ex2 == null || current.Ex2.Value.DiscoveredEntries == current.Ex2.Value.EntryCount);
                if (allDone)
                {
                    Log("All recognized FH4 roads are already discovered; nothing changed.", "info");
                    return;
                }

                if (!_confirmUnlockTest("Discover all recognized FH4 roads on this working copy? Export save writes a separate file."))
                    return;

                if (!Fh4RoadDiscoverySaveState.TryDiscoverAll(_container, out var before, out int expansionsUpdated))
                    throw new InvalidDataException("Road discovery update failed verification. Nothing was changed.");

                _hasUnlockTestEdits = true;
                BuildProperties();
                BuildOverview();
                RefreshUnlockTestDisplays(
                    $"Discovered all FH4 roads ({before.Main.DiscoveredEntries:N0} -> {before.Main.EntryCount:N0} bits" +
                    (expansionsUpdated > 0 ? $", {expansionsUpdated} expansion map(s) updated)" : ")"));
            }
            catch (Exception ex) { Fail("discover all FH4 roads", ex); }
        };
    }

    void AddFh4UnlockControls()
    {
        if (Fh4ProfileGameData.For(_container) == null)
            OverviewPanel.Children.Add(Note("Attach FH4 TIDETables.zip above to browse and unlock clothes, car horns, emotes, quick chat phrases, and Star Card flairs."));

        AddUnlockPicker(
            "fh4Clothing",
            "Fh4UnlockClothingButton",
            "clothing items",
            () =>
            {
                var data = Fh4ProfileGameData.For(_container);
                if (data == null || !Fh4ClothingUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Clothing
                    .Select(c => new UnlockItem(c.Id, c.Name, c.Category, ownedSet.Contains(c.Id), true, c.Rarity, ownedSet.Contains(c.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh4ClothingUnlockState.TryUnlockSelected(_container, ids, out int changed), changed),
            "Existing wardrobe flags and equipped items are preserved.");

        AddUnlockPicker(
            "fh4Horns",
            "Fh4UnlockHornsButton",
            "car horns",
            () =>
            {
                var data = Fh4ProfileGameData.For(_container);
                if (data == null || !Fh4CarHornUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Horns
                    .Select(h => new UnlockItem(h.Id, h.Name, string.IsNullOrEmpty(h.CategoryName) ? "Car Horns" : h.CategoryName, ownedSet.Contains(h.Id), true, "", ownedSet.Contains(h.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh4CarHornUnlockState.TryUnlockSelected(_container, ids, out int added), added),
            "Equipped horn is preserved.");

        AddUnlockPicker(
            "fh4Emotes",
            "Fh4UnlockEmotesButton",
            "emotes",
            () =>
            {
                var data = Fh4ProfileGameData.For(_container);
                if (data == null || !Fh4EmoteUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Emotes
                    .Select(e => new UnlockItem(e.Id, e.Name, string.IsNullOrEmpty(e.Rarity) ? "Emotes" : e.Rarity, ownedSet.Contains(e.Id), true, "", ownedSet.Contains(e.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh4EmoteUnlockState.TryUnlockSelected(_container, ids, out int added), added),
            "Equipped emote slots are preserved.");

        AddUnlockPicker(
            "fh4QuickChat",
            "Fh4UnlockQuickChatButton",
            "quick chat phrases",
            () =>
            {
                var data = Fh4ProfileGameData.For(_container);
                if (data == null || !Fh4QuickChatUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Phrases
                    .Select(p => new UnlockItem(p.Id, p.Name, "Quick Chat", ownedSet.Contains(p.Id), true, "", ownedSet.Contains(p.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh4QuickChatUnlockState.TryUnlockSelected(_container, ids, out int added), added),
            "Assigned Quick Chat context bindings are preserved.");

        AddUnlockPicker(
            "fh4Flairs",
            "Fh4UnlockFlairsButton",
            "Star Card flairs",
            () =>
            {
                var data = Fh4ProfileGameData.For(_container);
                if (data == null || !Fh4FlairUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Flairs
                    .Select(f => new UnlockItem(f.Id, f.Name, string.IsNullOrEmpty(f.Category) ? "Star Card Flairs" : f.Category, ownedSet.Contains(f.Id), true, f.Description, ownedSet.Contains(f.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh4FlairUnlockState.TryUnlockSelected(_container, ids, out int added), added),
            "Equipped Star Card flair is preserved.");
    }
#endif
}
