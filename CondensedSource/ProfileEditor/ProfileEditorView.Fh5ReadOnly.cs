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
    bool _isFh5;
    int _fh5TableOffset;
    long _fh5TableRows;

    void ApplyFh5Mode()
    {
        RowGrid.IsReadOnly = PropGrid.IsReadOnly = StringGrid.IsReadOnly = false;
        SaveBtn.IsEnabled = true;
        XuidBox.IsReadOnly = false;
        XuidApplyBtn.IsEnabled = _container?.Career?.HasXuid == true;
        GridHint.Text = "double-click a cell to edit";
        PropEditHint.Text = "double-click a value to edit";
        StringEditHint.Text = "double-click the Text column to edit";
        Fh5TablePages.Visibility = _isFh5 ? Visibility.Visible : Visibility.Collapsed;
        SqlCard.Visibility = DbBody.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    string ViewHelpForMode(string normal) => _isFh5
        ? normal.Replace("C_ProfileData", "ProfileData").Replace("FH6", "FH5")
        : normal;

    void BuildFh5Overview()
    {
        OverviewPanel.Children.Clear();
        if (_container == null) return;

        // ---- Your save at a glance ----
        OverviewPanel.Children.Add(SectionHeader("Your save at a glance"));
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (table, label) in new[]
                 {
                     ("Career_Garage", "Cars in garage"),
                     ("FreeCars", "Free cars"),
                     ("BarnFinds", "Barn finds"),
                     ("Career_PurchasedParts", "Purchased parts"),
                     ("PhotoCaptures", "Promo cars photographed"),
                 })
        {
            long n = TableCountOrNeg(table);
            if (n >= 0) chips.Children.Add(StatChip(n.ToString("n0", CultureInfo.InvariantCulture), label));
        }
        if (_container.Properties != null)
            chips.Children.Add(StatChip(_allProps.Count(p => p.Node.IsEditable).ToString("n0", CultureInfo.InvariantCulture), "editable settings"));
        var career = _container.Career;
        OverviewPanel.Children.Add(chips);

        // ---- Everyday values ----
        OverviewPanel.Children.Add(SectionHeader("Everyday values"));
        int everyday = 0;
        everyday += AddProfileEdit("/Main/Credits", "Credits", "your spendable in-game credits") ? 1 : 0;
        everyday += AddProfileEdit("/Main/TotalCredits", "Total Credits (stats)", "lifetime credits recorded in your profile stats") ? 1 : 0;
        everyday += AddDriverXpControls();
        everyday += AddProfileEdit("/Main/TotalXP", "Total XP (stats)", "lifetime XP recorded in your profile stats") ? 1 : 0;
        everyday += AddFh5SkillPointsEdit() ? 1 : (AddConsumableEdit(ProfileConsumableState.Kind.SkillPoints, "Skill points", "points currently available to spend") ? 1 : 0);
        everyday += AddFh5WheelspinsEdit(false, "Wheelspins", "normal spins available; prize history stays unchanged") ? 1 : (AddConsumableEdit(ProfileConsumableState.Kind.Wheelspins, "Wheelspins", "normal spins available; prize history stays unchanged") ? 1 : 0);
        everyday += AddFh5WheelspinsEdit(true, "Super Wheelspins", "super spins available; VIP grants stay unchanged") ? 1 : (AddConsumableEdit(ProfileConsumableState.Kind.SuperWheelspins, "Super Wheelspins", "super spins available; VIP grants stay unchanged") ? 1 : 0);
        everyday += AddBxmlIntegerEdit("CurrencyBucket_ForzathonPoints_forzathon_points", "Total", "Forzathon Points", "points available for the Forzathon Shop") ? 1 : 0;
        if (everyday == 0)
            OverviewPanel.Children.Add(Note("This save does not expose the usual credits, level, XP or skill-point fields in a safely editable format."));

        // ---- Festival Playlist ----
        OverviewPanel.Children.Add(SectionHeader("Festival Playlist"));
        bool currentPlaylist = AddSeasonPlaylistEdit();
        if (!currentPlaylist)
        {
            OverviewPanel.Children.Add(Note("No safely recognized Festival Playlist counters were found in this save. Playlist activity data is still preserved."));
        }

        // ---- World and unlocks ----
        OverviewPanel.Children.Add(SectionHeader("World and unlocks"));
        var worldChips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        long barnFinds = TableCountOrNeg("BarnFinds");
        if (barnFinds >= 0) worldChips.Children.Add(StatChip(barnFinds.ToString("n0", CultureInfo.InvariantCulture), "Barn finds saved"));
        long carUnlocks = TableCountOrNeg("CarExperienceUnlocks");
        if (carUnlocks >= 0) worldChips.Children.Add(StatChip(carUnlocks.ToString("n0", CultureInfo.InvariantCulture), "Car experience unlocks"));
        var characterItems = career?.Records.FirstOrDefault(record => record.Name == "CharacterCustomisation");
        if (characterItems != null && CareerSaveStateInsights.TryGetCharacterItemCount(characterItems.Payload, out int characterItemCount))
            worldChips.Children.Add(StatChip(characterItemCount.ToString("n0", CultureInfo.InvariantCulture), "Character items owned"));
        var sites = FindSaveMapValuesByType("FestivalSiteState").ToList();
        if (sites.Count > 0)
        {
            int built = sites.Count(site => IsBxmlTrue(GetBxmlPropertyValue(site, "Built")));
            int visited = sites.Count(site => IsBxmlTrue(GetBxmlPropertyValue(site, "Visited")));
            worldChips.Children.Add(StatChip($"{built}/{sites.Count}", "Festival sites built"));
            worldChips.Children.Add(StatChip($"{visited}/{sites.Count}", "Festival sites visited"));
        }
        OverviewPanel.Children.Add(worldChips);

        if (career != null)
        {
            var available = new HashSet<string>(career.Records.Select(r => r.Name), StringComparer.Ordinal);
            var friendlyRecords = new[]
            {
                ("FestivalPassSaveState", "Festival Playlist"),
                ("ForzathonDailyChallengeSaveState", "daily challenges"),
                ("ForzathonWeeklyChallengeSaveState", "weekly challenges"),
                ("SeasonalPRStuntsSaveState", "seasonal PR stunts"),
                ("TreasureHuntChallengeSaveState", "Treasure Hunts"),
                ("PhotoChallengeSaveState", "Photo Challenges"),
                ("CarHornSaveState", "car horns"),
                ("EmoteSaveState", "emotes"),
                ("QuickChatSaveState", "quick chat phrases"),
                ("BadgeTitleUnlocks", "badges"),
            }.Where(item => available.Contains(item.Item1)).Select(item => item.Item2).ToList();
            if (friendlyRecords.Count > 0)
                OverviewPanel.Children.Add(Note($"Preserved FH5 progression: {string.Join(", ", friendlyRecords)}. These stay fully intact across edits."));
        }

        // ---- Save account ----
        OverviewPanel.Children.Add(SectionHeader("Save account"));
        if (career != null && career.HasXuid)
        {
            OverviewPanel.Children.Add(EditRow("Account ID (XUID)", "only change this when moving a save to another Xbox/Steam account",
                career.Xuid.ToString(CultureInfo.InvariantCulture),
                text =>
                {
                    if (!ulong.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong x))
                        return (false, "not a valid number");
                    career.SetXuid(x);
                    return (true, x.ToString(CultureInfo.InvariantCulture));
                }));
        }

        OverviewPanel.Children.Add(Note("Advanced users have the full Cars & Data, Profile Values, Save State XML, and Career views. "
            + "Custom SQL commands and DataGrid edits are enabled. Nothing is written to disk until you click Export save."));
    }

    void Fh5PreviousRows_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFh5 || _currentTable == null || _fh5TableOffset == 0) return;
        _fh5TableOffset = Math.Max(0, _fh5TableOffset - RowLimit);
        try { LoadTable(_currentTable); } catch (Exception ex) { Fail("previous page", ex); }
    }

    void Fh5NextRows_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFh5 || _currentTable == null || _fh5TableOffset + RowLimit >= _fh5TableRows) return;
        _fh5TableOffset += RowLimit;
        try { LoadTable(_currentTable); } catch (Exception ex) { Fail("next page", ex); }
    }

    bool AddFh5WheelspinsEdit(bool isSuper, string label, string hint)
    {
        var bx = _container?.SaveState;
        if (bx == null) return false;
        var tombola = FindSaveMapValue("TombolaState");
        if (tombola == null) return false;
        string childId = isSuper ? "SuperSpinInfo" : "NormalSpinInfo";
        var spinInfo = tombola.Children.FirstOrDefault(n => string.Equals(bx.GetAttribute(n, "id"), childId, StringComparison.OrdinalIgnoreCase));
        if (spinInfo == null) return false;
        var spinsNode = spinInfo.Children.FirstOrDefault(n => string.Equals(bx.GetAttribute(n, "id"), "Spins", StringComparison.OrdinalIgnoreCase));
        string current = spinsNode != null ? bx.GetAttribute(spinsNode, "value") : null;
        if (spinsNode == null || current == null) return false;

        OverviewPanel.Children.Add(EditRow(label, hint, current,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                string normalized = value.ToString(CultureInfo.InvariantCulture);
                if (!bx.SetAttribute(spinsNode, "value", normalized)) return (false, "this value could not be updated");
                RefreshSaveStateDisplays();
                return (true, normalized);
            }));
        return true;
    }

    bool AddFh5SkillPointsEdit()
    {
        var bx = _container?.SaveState;
        if (bx == null) return false;
        var carPerks = FindSaveMapValue("CarPerkSaveState");
        if (carPerks == null) return false;
        var unspentNode = carPerks.Children.FirstOrDefault(n => string.Equals(bx.GetAttribute(n, "id"), "UnspentSkillPointsTotal", StringComparison.OrdinalIgnoreCase));
        var tokensNode = carPerks.Children.FirstOrDefault(n => string.Equals(bx.GetAttribute(n, "id"), "CurrentSkillTokensTotal", StringComparison.OrdinalIgnoreCase));
        var targetNode = unspentNode ?? tokensNode;
        string current = targetNode != null ? bx.GetAttribute(targetNode, "value") : null;
        if (targetNode == null || current == null) return false;

        OverviewPanel.Children.Add(EditRow("Skill points", "points currently available to spend on car mastery perks", current,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 999");
                string normalized = value.ToString(CultureInfo.InvariantCulture);
                bool ok = true;
                if (unspentNode != null) ok &= bx.SetAttribute(unspentNode, "value", normalized);
                if (tokensNode != null) ok &= bx.SetAttribute(tokensNode, "value", normalized);
                if (!ok) return (false, "this value could not be updated");
                RefreshSaveStateDisplays();
                return (true, normalized);
            }));
        return true;
    }
}
