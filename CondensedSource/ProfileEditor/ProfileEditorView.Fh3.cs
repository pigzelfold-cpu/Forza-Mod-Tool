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
    void BuildFh3Overview()
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
        AddTableChip("PhotoCaptures", "Promo photos");
        AddTableChip("PerkUnlock", "Active perks");
        int editableSettings = _container?.Properties?.Walk().Count(p => p.Node.IsEditable) ?? 0;
        chips.Children.Add(StatChip(editableSettings.ToString("N0", CultureInfo.CurrentCulture), "Editable settings"));
        OverviewPanel.Children.Add(chips);

#if PROFILE_UNLOCK_TESTING
        AddFh3GameDataControls();
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
        if (AddFh3FansEdit()) shown++;
        if (AddFh3SkillPointsEdit()) shown++;
        if (AddFh3WheelspinsEdit()) shown++;
        if (shown == 0)
            OverviewPanel.Children.Add(Note("No recognized everyday value paths were found in this FH3 profile."));

        OverviewPanel.Children.Add(SectionHeader("FH3 Expansions (Blizzard Mountain & Hot Wheels)"));
        int expansionControls = 0;
        if (AddFh3ExpansionStarsEdit(isHotWheels: false)) expansionControls++;
        if (AddFh3ExpansionStarsEdit(isHotWheels: true)) expansionControls++;
        var expansionChips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        void AddPropChip(string path, string label)
        {
            var node = _container?.Properties?.Walk().FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)).Node;
            if (node != null && uint.TryParse(node.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint val))
                expansionChips.Children.Add(StatChip(val.ToString("N0", CultureInfo.CurrentCulture), label));
        }
        AddPropChip("/Mountain/RoadsDiscovered", "Blizzard Mountain roads");
        AddPropChip("/Mountain/SnowmenSmashed", "Snowmen smashed");
        AddPropChip("/OpusIsland/RoadsDiscovered", "Hot Wheels roads");
        AddPropChip("/OpusIsland/ExhibitionEventsCompleted", "Hot Wheels events");
        if (expansionChips.Children.Count > 0)
            OverviewPanel.Children.Add(expansionChips);
        else if (expansionControls == 0)
            OverviewPanel.Children.Add(Note("No Blizzard Mountain or Hot Wheels star counters were found in this profile."));

        OverviewPanel.Children.Add(SectionHeader("World and unlocks"));
        var worldChips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        long barnCount = TableCountOrNeg("BarnFinds");
        worldChips.Children.Add(StatChip(barnCount >= 0 ? barnCount.ToString("N0", CultureInfo.CurrentCulture) : "—", "Barn finds"));
        long carExpCount = TableCountOrNeg("CarExperienceUnlocks");
        worldChips.Children.Add(StatChip(carExpCount >= 0 ? carExpCount.ToString("N0", CultureInfo.CurrentCulture) : "—", "Car experience unlocks"));
        AddPropChip("/CompareStats/PerksUnlocked", "Skill perks unlocked");
#if PROFILE_UNLOCK_TESTING
        if (Fh3CarHornUnlockState.TryReadUnlocked(_container, out var ownedHorns))
            worldChips.Children.Add(StatChip(ownedHorns.Length.ToString("N0", CultureInfo.CurrentCulture), "Owned car horns"));
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
        AddFh3RoadDiscoveryControls();
        AddFh3UnlockControls();
#endif

        OverviewPanel.Children.Add(SectionHeader("Save account"));
        OverviewPanel.Children.Add(Note("FH3 Windows Store / UWP (Microsoft.OpusPG_8wekyb3d8bbwe) profiles bind account ownership externally through your WGS container folder; no embedded XUID is stored in the profile payload. Export to a separate folder before replacing your save while the game is closed."));
        OverviewPanel.Children.Add(Note("Advanced users have the full Cars & Data, Profile Values, Save State XML, and Career views. "
            + "Nothing is written to disk until you click Export save."));
    }

    bool AddFh3FansEdit()
    {
        var bx = _container?.SaveState;
        var fansObj = Fh4ProfileHelper.FindObject(bx, "CurrencyBucket_Fans_offline_currency");
        var bxmlProp = Fh4ProfileHelper.GetPropertyNode(bx, fansObj, "Total");
        string bxmlRaw = bxmlProp != null ? bx?.GetAttribute(bxmlProp, "Value") : null;
        var totalSkillsNode = _container?.Properties?.Walk().FirstOrDefault(p => p.Path == "/Main/TotalSkills").Node;
        var totalFansNode = _container?.Properties?.Walk().FirstOrDefault(p => p.Path == "/MiscStats/TotalFansEarned").Node;

        if (bxmlProp == null && totalSkillsNode == null && totalFansNode == null) return false;

        string currentText = bxmlRaw ?? totalSkillsNode?.DisplayValue() ?? totalFansNode?.DisplayValue() ?? "0";
        var row = EditRow(
            "Fans",
            "Syncs CurrencyBucket_Fans_offline_currency.Total, /Main/TotalSkills, and /MiscStats/TotalFansEarned",
            currentText,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");

                string formatted = value.ToString(CultureInfo.InvariantCulture);
                string oldBxml = bxmlProp != null && bx != null ? bx.GetAttribute(bxmlProp, "Value") : null;
                string oldSkills = totalSkillsNode?.DisplayValue();

                if (bxmlProp != null && bx != null && !bx.SetAttribute(bxmlProp, "Value", formatted))
                    return (false, "this value could not be updated");
                if (totalSkillsNode != null && !totalSkillsNode.TrySetFromText(formatted))
                {
                    if (bxmlProp != null && bx != null && oldBxml != null) bx.SetAttribute(bxmlProp, "Value", oldBxml);
                    return (false, "could not update /Main/TotalSkills");
                }
                if (totalFansNode != null && !totalFansNode.TrySetFromText(formatted))
                {
                    if (bxmlProp != null && bx != null && oldBxml != null) bx.SetAttribute(bxmlProp, "Value", oldBxml);
                    if (totalSkillsNode != null && oldSkills != null) totalSkillsNode.TrySetFromText(oldSkills);
                    return (false, "could not update /MiscStats/TotalFansEarned");
                }

                BuildProperties();
                RefreshSaveStateDisplays();
                return (true, formatted);
            });
        row.Name = "Fh3FansRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

    bool AddFh3SkillPointsEdit()
    {
        var bx = _container?.SaveState;
        var perkObj = Fh4ProfileHelper.FindObject(bx, "skills_save_state");
        var bxmlProp = Fh4ProfileHelper.GetPropertyNode(bx, perkObj, "CurrentSkillTokensTotal");
        string bxmlRaw = bxmlProp != null ? bx?.GetAttribute(bxmlProp, "Value") : null;
        var treeNode = _container?.Properties?.Walk().FirstOrDefault(p => p.Path == "/Main/NumSkillTokens").Node;

        if (bxmlProp == null && treeNode == null) return false;

        string currentText = bxmlRaw ?? treeNode?.DisplayValue() ?? "0";
        var row = EditRow(
            "Skill Points",
            "Updates skills_save_state.CurrentSkillTokensTotal and adjusts /Main/NumSkillTokens (leaves UnspentSkillPointsTotal progress untouched)",
            currentText,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");

                string formatted = value.ToString(CultureInfo.InvariantCulture);
                string oldBxml = bxmlProp != null && bx != null ? bx.GetAttribute(bxmlProp, "Value") : null;
                if (bxmlProp != null && bx != null && !bx.SetAttribute(bxmlProp, "Value", formatted))
                    return (false, "this value could not be updated");

                if (treeNode != null)
                {
                    long nextTree = value;
                    if (oldBxml != null &&
                        int.TryParse(oldBxml, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oldTokens) &&
                        uint.TryParse(treeNode.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint oldTree))
                    {
                        nextTree = Math.Clamp((long)oldTree + ((long)value - oldTokens), 0L, uint.MaxValue);
                    }
                    if (!treeNode.TrySetFromText(nextTree.ToString(CultureInfo.InvariantCulture)))
                    {
                        if (bxmlProp != null && bx != null && oldBxml != null) bx.SetAttribute(bxmlProp, "Value", oldBxml);
                        return (false, "could not update /Main/NumSkillTokens");
                    }
                }

                bxmlRaw = formatted;
                BuildProperties();
                RefreshSaveStateDisplays();
                return (true, formatted);
            });
        row.Name = "Fh3SkillPointsRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

    bool AddFh3WheelspinsEdit()
    {
        var bx = _container?.SaveState;
        var tombola = Fh4ProfileHelper.FindObject(bx, "TombolaState");
        var prop = Fh4ProfileHelper.GetPropertyNode(bx, tombola, "Spins");
        string current = prop != null ? bx?.GetAttribute(prop, "Value") : null;
        if (prop == null || current == null) return false;

        var row = EditRow(
            "Wheelspins",
            "Available Wheelspin tickets in SaveState (TombolaState.Spins)",
            current,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");

                string formatted = value.ToString(CultureInfo.InvariantCulture);
                if (!bx.SetAttribute(prop, "Value", formatted))
                    return (false, "this value could not be updated");
                RefreshSaveStateDisplays();
                return (true, formatted);
            });
        row.Name = "Fh3WheelspinsRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

    bool AddFh3ExpansionStarsEdit(bool isHotWheels)
    {
        var bx = _container?.SaveState;
        string bucketId = isHotWheels ? "CurrencyBucket_OpusIslandStars_offline_currency" : "CurrencyBucket_Stars_offline_currency";
        string propPath = isHotWheels ? "/OpusIsland/Stars" : "/Mountain/Stars";
        string label = isHotWheels ? "Hot Wheels Stars" : "Blizzard Mountain Stars";
        string hint = isHotWheels
            ? "Updates CurrencyBucket_OpusIslandStars_offline_currency.Total (when present) and /OpusIsland/Stars"
            : "Updates CurrencyBucket_Stars_offline_currency.Total (when present) and /Mountain/Stars";

        var obj = Fh4ProfileHelper.FindObject(bx, bucketId);
        var bxmlProp = Fh4ProfileHelper.GetPropertyNode(bx, obj, "Total");
        string bxmlRaw = bxmlProp != null ? bx?.GetAttribute(bxmlProp, "Value") : null;
        var treeNode = _container?.Properties?.Walk().FirstOrDefault(p => p.Path == propPath).Node;

        if (bxmlProp == null && treeNode == null) return false;

        string currentText = bxmlRaw ?? treeNode?.DisplayValue() ?? "0";
        var row = EditRow(label, hint, currentText, text =>
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                return (false, "enter a whole number from 0 to 2,147,483,647");

            string formatted = value.ToString(CultureInfo.InvariantCulture);
            string oldBxml = bxmlProp != null && bx != null ? bx.GetAttribute(bxmlProp, "Value") : null;
            if (bxmlProp != null && bx != null && !bx.SetAttribute(bxmlProp, "Value", formatted))
                return (false, "this value could not be updated");
            if (treeNode != null && !treeNode.TrySetFromText(formatted))
            {
                if (bxmlProp != null && bx != null && oldBxml != null) bx.SetAttribute(bxmlProp, "Value", oldBxml);
                return (false, "could not update " + propPath);
            }

            BuildProperties();
            RefreshSaveStateDisplays();
            return (true, formatted);
        });
        row.Name = isHotWheels ? "Fh3HotWheelsStarsRow" : "Fh3BlizzardMountainStarsRow";
        OverviewPanel.Children.Add(row);
        return true;
    }

#if PROFILE_UNLOCK_TESTING
    void AddFh3GameDataControls()
    {
        var data = Fh3ProfileGameData.GetStatus(_container);
        var button = new Button
        {
            Name = "Fh3GameDataButton",
            Content = "Choose FH3 game data…",
            Style = (Style)FindResource("PinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var status = Note(data.Message);
        status.Name = "Fh3GameDataStatus";
        status.ToolTip = data.Source + "\n" + data.Message;
        OverviewPanel.Children.Add(status);
        OverviewPanel.Children.Add(button);
        button.Click += (_, _) => ChooseFh3ProfileGameData();
    }

    void ChooseFh3ProfileGameData()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select FH3 gamedbrc.slt ({lgq6`4^.~b1) or en.zip (q8.,$u) from your FH3 install",
            Filter = "FH3 game data (*.slt;*.~b1;en.zip;q8.,$u)|*.slt;*.~b1;en.zip;q8.,$u|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var result = Fh3ProfileGameData.Attach(_container, dialog.FileName, remember: true);
        Log(result.Message, result.Loaded ? "ok" : "err");
        BuildOverview();
    }

    void AddFh3RoadDiscoveryControls()
    {
        bool recognized = Fh3RoadDiscoverySaveState.TryReadProfile(_container, out var summary);
        var btn = TestButton("Fh3DiscoverRoadsButton", "Discover all roads", recognized);
        btn.ToolTip = "Marks every valid road segment bit discovered across Australia (and Blizzard Mountain / Hot Wheels when present) and updates /CompareStats/RoadsDiscovered.";

        string statusText;
        if (!recognized)
        {
            statusText = "Road discovery layout was not safely recognized in this profile; road discovery editing is disabled.";
        }
        else
        {
            var parts = new List<string>
            {
                $"Australia: {summary.Main.DiscoveredEntries:N0} / {summary.Main.EntryCount:N0} road bits ({summary.Roads:N0} roads)"
            };
            if (summary.Mountain is { } mtn)
                parts.Add($"Blizzard Mountain: {mtn.DiscoveredEntries:N0} / {mtn.EntryCount:N0} bits");
            if (summary.OpusIsland is { } island)
                parts.Add($"Hot Wheels: {island.DiscoveredEntries:N0} / {island.EntryCount:N0} bits");
            statusText = string.Join(" · ", parts);
        }

        var note = Note(statusText);
        note.Name = "Fh3RoadDiscoveryStatus";
        OverviewPanel.Children.Add(btn);
        OverviewPanel.Children.Add(note);

        btn.Click += (_, _) =>
        {
            try
            {
                if (!Fh3RoadDiscoverySaveState.TryReadProfile(_container, out var current))
                    throw new InvalidDataException("Road discovery layout is not safely recognized. Nothing was changed.");

                uint targetRoads = Fh3ProfileGameData.ForOrEmbedded(_container).RoadTargetCount;
                bool allDone = current.Main.DiscoveredEntries == current.Main.EntryCount
                    && current.Roads >= targetRoads
                    && (current.Mountain == null || current.Mountain.Value.DiscoveredEntries == current.Mountain.Value.EntryCount)
                    && (current.OpusIsland == null || current.OpusIsland.Value.DiscoveredEntries == current.OpusIsland.Value.EntryCount);
                if (allDone)
                {
                    Log("All recognized FH3 roads are already discovered; nothing changed.", "info");
                    return;
                }

                if (!_confirmUnlockTest("Discover all recognized FH3 roads on this working copy? Export save writes a separate file."))
                    return;

                if (!Fh3RoadDiscoverySaveState.TryDiscoverAll(_container, out var before, out int expansionsUpdated))
                    throw new InvalidDataException("Road discovery update failed verification. Nothing was changed.");

                _hasUnlockTestEdits = true;
                BuildProperties();
                BuildOverview();
                RefreshUnlockTestDisplays(
                    $"Discovered all FH3 roads ({before.Main.DiscoveredEntries:N0} -> {before.Main.EntryCount:N0} bits" +
                    (expansionsUpdated > 0 ? $", {expansionsUpdated} expansion map(s) updated)" : ")"));
            }
            catch (Exception ex) { Fail("discover all FH3 roads", ex); }
        };
    }

    void AddFh3UnlockControls()
    {
        AddUnlockPicker(
            "fh3Horns",
            "Fh3UnlockHornsButton",
            "car horns",
            () =>
            {
                var data = Fh3ProfileGameData.ForOrEmbedded(_container);
                if (!Fh3CarHornUnlockState.TryReadUnlocked(_container, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Horns
                    .Select(h => new UnlockItem(
                        h.Id,
                        h.Name,
                        string.IsNullOrEmpty(h.CategoryName) ? "Car Horns" : h.CategoryName,
                        ownedSet.Contains(h.Id),
                        true,
                        h.UnlockByPerk >= 0 ? $"Perk #{h.UnlockByPerk}" : "",
                        ownedSet.Contains(h.Id) ? " [owned]" : ""))
                    .ToArray();
            },
            ids => (Fh3CarHornUnlockState.TryUnlockSelected(_container, ids, out int added), added),
            "Equipped horn is preserved.");

        AddUnlockPicker(
            "fh3Perks",
            "Fh3UnlockPerksButton",
            "skill perks",
            () =>
            {
                var data = Fh3ProfileGameData.ForOrEmbedded(_container);
                if (!Fh3PerkUnlockState.TryReadUnlocked(_container, _conn, out var owned)) return null;
                var ownedSet = owned.ToHashSet();
                return data.Perks
                    .Select(p =>
                    {
                        bool isOwned = ownedSet.Contains(p.Id);
                        string title = !string.IsNullOrEmpty(p.DisplayName) && !string.Equals(p.DisplayName, p.Name, StringComparison.OrdinalIgnoreCase)
                            ? $"{p.DisplayName} ({p.Name})"
                            : p.Name;
                        string detail = p.TotalUses > 0
                            ? $"Consumable ({p.TotalUses} uses) · Cost {p.Cost}"
                            : $"Cost {p.Cost}";
                        if (!string.IsNullOrEmpty(p.Description)) detail += " — " + p.Description;
                        string suffix = isOwned ? (p.TotalUses > 0 ? " [active]" : " [owned]") : "";
                        return new UnlockItem(p.Id, title, p.Category, isOwned, true, detail, suffix);
                    })
                    .ToArray();
            },
            ids =>
            {
                bool ok = Fh3PerkUnlockState.TryUnlockSelected(_container, _conn, ids, out int changed);
                if (ok && changed > 0)
                {
                    RenderTables();
                    BuildProperties();
                    if (_currentTable is "PerkUnlock" or "PerkState")
                        try { LoadTable(_currentTable); } catch { }
                }
                return (ok, changed);
            },
            "Permanent perks and consumable uses are synced in PerkUnlock / PerkState and /CompareStats/PerksUnlocked.");
    }
#endif
}
