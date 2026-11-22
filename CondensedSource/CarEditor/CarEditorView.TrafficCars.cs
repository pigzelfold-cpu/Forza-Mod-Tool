#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FH6CarEditor;

public partial class CarEditorView
{
    readonly HashSet<long> _trafficCarIds = new();
    readonly Dictionary<long, TrafficCarBaseline> _loadedTrafficBaselines = new();
    readonly Dictionary<long, long> _sessionTrafficOnDiscRowIds = new();

    bool HasTrafficUnlockSupport => _conn != null && (HasAutoshow || IsFh2) && _trafficCarIds.Count > 0;
    bool IsTrafficCarId(long carId) => _trafficCarIds.Contains(carId);
    bool CanToggleAutoshow(IReadOnlyList<CarItem> selected) =>
        HasAutoshow || (IsFh2 && selected.Count > 0 && selected.All(x => IsTrafficCarId(x.Id)));

    void ResetTrafficCarsState()
    {
        _trafficCarIds.Clear();
        _loadedTrafficBaselines.Clear();
        _sessionTrafficOnDiscRowIds.Clear();
        AddTrafficBtn.IsEnabled = RemoveTrafficBtn.IsEnabled = false;
        AddTrafficBtn.ToolTip = RemoveTrafficBtn.ToolTip = null;
    }

    void CaptureTrafficCarsState()
    {
        _trafficCarIds.Clear();
        _loadedTrafficBaselines.Clear();
        _sessionTrafficOnDiscRowIds.Clear();
        if (_conn == null || (!HasAutoshow && !IsFh2))
        {
            ConfigureTrafficButtons();
            return;
        }

        var ids = TrafficCarAutoshowService.ResolveTrafficCarIds(
            _conn,
            table => TableExists("main", table),
            HasColumn,
            _stockReferenceAttached);
        foreach (long id in ids) _trafficCarIds.Add(id);

        if (_trafficCarIds.Count > 0)
        {
            foreach (var pair in TrafficCarAutoshowService.CaptureBaselines(
                _conn, _trafficCarIds, table => TableExists("main", table), HasColumn, _stockReferenceAttached))
            {
                _loadedTrafficBaselines[pair.Key] = pair.Value;
            }
        }
        ConfigureTrafficButtons();
    }

    void ConfigureTrafficButtons()
    {
        bool enabled = HasTrafficUnlockSupport;
        AddTrafficBtn.IsEnabled = RemoveTrafficBtn.IsEnabled = enabled;
        if (enabled)
        {
            AddTrafficBtn.ToolTip = $"Make all {_trafficCarIds.Count} traffic, unobtainable, story/special, and non-drivable cars purchasable in the Autoshow (preserves free-roam TrafficCars spawning and excludes null cars).";
            RemoveTrafficBtn.ToolTip = $"Hide all {_trafficCarIds.Count} traffic/unobtainable cars back from the Autoshow (restores original flags, buckets, and UnobtainableCars without altering performance or swaps).";
        }
        else
        {
            string reason = (!HasAutoshow && !IsFh2)
                ? "Motorsport schemas do not use Horizon Autoshow traffic unlocks."
                : "No unlockable traffic or unobtainable cars found in this database.";
            AddTrafficBtn.ToolTip = RemoveTrafficBtn.ToolTip = reason;
        }
    }

    bool IsCarAvailableInAutoshowUi(long carId)
    {
        if (_conn == null) return false;
        if (IsTrafficCarId(carId) && (HasAutoshow || IsFh2))
        {
            return TrafficCarAutoshowService.IsCarUnlockedForAutoshow(
                _conn, carId, table => TableExists("main", table), HasColumn);
        }
        return HasAutoshow && (ScalarL("SELECT NotAvailableInAutoshow FROM Data_Car WHERE Id=?", carId) ?? 1) == 0;
    }

    void AddTraffic_Click(object s, RoutedEventArgs e) => SetTrafficCarsAutoshowAvailability(true);
    void RemoveTraffic_Click(object s, RoutedEventArgs e) => SetTrafficCarsAutoshowAvailability(false);

    void SetTrafficCarsAutoshowAvailability(bool available)
    {
        if (_conn == null || !HasTrafficUnlockSupport) return;
        long[] targetIds = _trafficCarIds.OrderBy(id => id).ToArray();
        try
        {
            if (available)
            {
                var res = TrafficCarAutoshowService.Unlock(
                    _conn, targetIds, _carBuckets, _editorGame,
                    table => TableExists("main", table), HasColumn, _sessionTrafficOnDiscRowIds);
                RefreshModifiedAfterExcludedEdit();
                var parts = new List<string> { $"{res.NewlyUnlocked} newly unlocked" };
                if (res.AddedBuckets > 0) parts.Add($"{res.AddedBuckets} bucket(s) assigned");
                if (res.ClearedUnobtainable > 0) parts.Add($"{res.ClearedUnobtainable} removed from UnobtainableCars");
                if (res.AddedOnDisc > 0) parts.Add($"{res.AddedOnDisc} added to OnDiscContent");
                Log($"✓ unlocked all {res.TotalTrafficCars} traffic/unobtainable cars for Autoshow · {string.Join(" · ", parts)} (free-roam TrafficCars table preserved)", "ok");
            }
            else
            {
                var res = TrafficCarAutoshowService.Revert(
                    _conn, targetIds, _loadedTrafficBaselines,
                    table => TableExists("main", table), HasColumn, _sessionTrafficOnDiscRowIds);
                RefreshModifiedAfterExcludedEdit();
                var parts = new List<string> { $"{res.NewlyHidden} newly hidden" };
                if (res.RemovedBuckets > 0) parts.Add($"{res.RemovedBuckets} bucket(s) removed");
                if (res.RestoredUnobtainable > 0) parts.Add($"{res.RestoredUnobtainable} restored to UnobtainableCars");
                Log($"reverted {res.TotalTrafficCars} traffic/unobtainable cars back to restricted status · {string.Join(" · ", parts)}", "warn");
            }
        }
        catch (Exception ex)
        {
            Log($"traffic car Autoshow update failed: {ex.Message}", "err");
        }
        if (_car != null)
        {
            if (SelectedCars().Count > 1) PickCar();
            else { ShowCarInfo(); ShowCarBucket(); }
        }
    }

    void ApplyAutoshowToggleForTargets(IReadOnlyList<CarItem> targets, bool avail)
    {
        var trafficTargets = targets.Where(t => IsTrafficCarId(t.Id)).Select(t => t.Id).ToArray();
        var normalTargets = targets.Where(t => !IsTrafficCarId(t.Id)).Select(t => t.Id).ToArray();

        if (trafficTargets.Length > 0)
        {
            if (avail)
            {
                TrafficCarAutoshowService.Unlock(
                    _conn, trafficTargets, _carBuckets, _editorGame,
                    table => TableExists("main", table), HasColumn, _sessionTrafficOnDiscRowIds);
            }
            else
            {
                TrafficCarAutoshowService.Revert(
                    _conn, trafficTargets, _loadedTrafficBaselines,
                    table => TableExists("main", table), HasColumn, _sessionTrafficOnDiscRowIds);
            }
        }
        if (HasAutoshow && normalTargets.Length > 0)
        {
            foreach (long id in normalTargets)
                Exec("UPDATE Data_Car SET NotAvailableInAutoshow=? WHERE Id=?", avail ? 0 : 1, id);
        }
    }
}
