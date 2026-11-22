#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    string _fh5AccoladeCollection = "All collections";
    string _fh5AccoladeSubcategory = "All subcategories";
    string _fh5AccoladeSearch = "";
    bool _fh5AccoladeIncompleteOnly = true;

    void AddFh5AccoladesControls()
    {
        var heading = SectionHeader("Accolades");
        heading.Name = "Fh5AccoladesHeading";
        OverviewPanel.Children.Add(heading);

        var display = Fh5AccoladesState.ReadForDisplay(_container);
        string StatusText() => display.CanEdit
            ? $"{display.Completed:n0} / {display.Total:n0} completed · Accolade Points: {display.SavedPoints:n0}"
              + (display.TotalsAgree ? "" : " · " + display.Warning)
            : "Read-only · " + display.Warning;

        var status = Note(StatusText());
        status.Name = "Fh5AccoladesStatus";
        OverviewPanel.Children.Add(status);

        var help = Note(display.CanEdit
            ? "Select rows (Ctrl-click for multiple, Ctrl+A for all), then click Complete selected accolades or Reset selected accolades. Collection records and Accolade points sync automatically; rewards remain unclaimed."
            : "Accolades cannot be edited until matching FH5 game data (TIDETables.zip) is loaded and CareerChallengeSaveState is recognized.");
        help.Name = "Fh5AccoladesHelp";
        OverviewPanel.Children.Add(help);

        var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        var collectionBox = new ComboBox
        {
            Name = "Fh5AccoladeCollectionFilter",
            ItemsSource = new[] { "All collections" }.Concat(display.Rows.Select(r => r.CollectionName).Distinct()).ToArray(),
            MinWidth = 200,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 8, 4),
        };
        collectionBox.SelectedItem = ((string[])collectionBox.ItemsSource).Contains(_fh5AccoladeCollection)
            ? _fh5AccoladeCollection
            : "All collections";

        var subcategoryBox = new ComboBox
        {
            Name = "Fh5AccoladeSubcategoryFilter",
            MinWidth = 205,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 8, 4),
        };

        var search = new TextBox
        {
            Name = "Fh5AccoladeSearch",
            Text = _fh5AccoladeSearch,
            Width = 250,
            MinHeight = 30,
            Style = (Style)FindResource("Input"),
            ToolTip = "Search accolade name, description, subcategory or how to unlock",
            Margin = new Thickness(0, 0, 8, 4),
        };

        var incompleteOnly = new CheckBox
        {
            Name = "Fh5AccoladeIncompleteOnly",
            Content = "Unfinished only",
            Style = (Style)FindResource("PinkCheckBox"),
            IsChecked = _fh5AccoladeIncompleteOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 4),
        };

        filters.Children.Add(collectionBox);
        filters.Children.Add(subcategoryBox);
        filters.Children.Add(search);
        filters.Children.Add(incompleteOnly);
        OverviewPanel.Children.Add(filters);

        var grid = new DataGrid
        {
            Name = "Fh5AccoladesGrid",
            Style = (Style)FindResource("PdGrid"),
            Height = 400,
            Margin = new Thickness(0, 4, 0, 4),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        var statusCheckStyle = (Style)FindResource("JournalStatusCheckBox");
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Done", Binding = new Binding("Completed"), Width = 50, ElementStyle = statusCheckStyle });
        grid.Columns.Add(new DataGridTextColumn { Header = "Collection", Binding = new Binding("CollectionName"), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Subcategory", Binding = new Binding("Subcategory"), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Accolade", Binding = new Binding("Name"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new Binding("Description"), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Points", Binding = new Binding("PointsDisplay"), Width = 65 });
        OverviewPanel.Children.Add(grid);

        var completeBtn = TestButton("CompleteFh5AccoladesButton", "Complete selected accolades", display.CanEdit);
        var resetBtn = TestButton("ResetFh5AccoladesButton", "Reset selected accolades", display.CanEdit);
        resetBtn.Margin = new Thickness(8, 10, 0, 0);
        var syncBtn = TestButton("SyncFh5AccoladesButton", "Sync points & collections", display.CanEdit);
        syncBtn.Margin = new Thickness(8, 10, 0, 0);
        syncBtn.ToolTip = "Reconcile CareerChallengeCollectionSaveState0..21 and Accolade points bucket with completed accolades in CareerChallengeSaveState.";

        var actions = new WrapPanel();
        actions.Children.Add(completeBtn);
        actions.Children.Add(resetBtn);
        actions.Children.Add(syncBtn);
        OverviewPanel.Children.Add(actions);

        bool ready = false;
        void PopulateSubcategories()
        {
            bool prior = ready;
            ready = false;
            string colFilter = Convert.ToString(collectionBox.SelectedItem) ?? "All collections";
            string[] names = new[] { "All subcategories" }
                .Concat(display.Rows
                    .Where(r => colFilter == "All collections" || r.CollectionName == colFilter)
                    .Select(r => r.Subcategory)
                    .Distinct()
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            subcategoryBox.ItemsSource = names;
            subcategoryBox.SelectedItem = names.Contains(_fh5AccoladeSubcategory) ? _fh5AccoladeSubcategory : names[0];
            ready = prior;
        }

        void Render()
        {
            if (!ready) return;
            _fh5AccoladeCollection = Convert.ToString(collectionBox.SelectedItem) ?? "All collections";
            _fh5AccoladeSubcategory = Convert.ToString(subcategoryBox.SelectedItem) ?? "All subcategories";
            _fh5AccoladeSearch = search.Text.Trim();
            _fh5AccoladeIncompleteOnly = incompleteOnly.IsChecked == true;

            grid.ItemsSource = display.Rows.Where(row =>
                (_fh5AccoladeCollection == "All collections" || row.CollectionName == _fh5AccoladeCollection) &&
                (_fh5AccoladeSubcategory == "All subcategories" || row.Subcategory == _fh5AccoladeSubcategory) &&
                (!_fh5AccoladeIncompleteOnly || !row.Completed) &&
                (_fh5AccoladeSearch.Length == 0 ||
                 (row.Name + " " + row.Description + " " + row.Subcategory + " " + row.HowToUnlock)
                 .Contains(_fh5AccoladeSearch, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.CollectionId)
                .ThenBy(r => r.Subcategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id)
                .ToArray();
        }

        collectionBox.SelectionChanged += (_, _) => { PopulateSubcategories(); Render(); };
        subcategoryBox.SelectionChanged += (_, _) => Render();
        search.TextChanged += (_, _) => Render();
        incompleteOnly.Checked += (_, _) => Render();
        incompleteOnly.Unchecked += (_, _) => Render();
        PopulateSubcategories();
        ready = true;
        Render();

        completeBtn.Click += (_, _) => ApplyAccoladeEdit(isReset: false, syncOnly: false);
        resetBtn.Click += (_, _) => ApplyAccoladeEdit(isReset: true, syncOnly: false);
        syncBtn.Click += (_, _) => ApplyAccoladeEdit(isReset: false, syncOnly: true);

        void ApplyAccoladeEdit(bool isReset, bool syncOnly)
        {
            try
            {
                if (!display.CanEdit)
                {
                    Log("Accolades are read-only until matching FH5 game data is loaded.", "info");
                    return;
                }

                ulong[] ids = Array.Empty<ulong>();
                int unavailable = 0;
                if (!syncOnly)
                {
                    var chosen = grid.SelectedItems.OfType<Fh5AccoladesState.Row>().ToArray();
                    if (chosen.Length == 0)
                    {
                        Log("Select accolades in the table above first.", "info");
                        return;
                    }
                    ids = chosen.Where(r => r.Supported).Select(r => (ulong)r.Id).ToArray();
                    unavailable = chosen.Length - ids.Length;
                    if (ids.Length == 0)
                    {
                        Log("Selected accolades have unsupported objective layouts and remain read-only.", "info");
                        return;
                    }
                }

                Fh5AccoladesState.ChangeSummary preview;
                string error;
                bool ok = syncOnly
                    ? Fh5AccoladesState.TryPreviewSyncPointsAndCollections(_container, out preview, out error)
                    : isReset
                        ? Fh5AccoladesState.TryPreviewReset(_container, ids, out preview, out error)
                        : Fh5AccoladesState.TryPreviewComplete(_container, ids, out preview, out error);
                if (!ok) throw new InvalidOperationException(error);
                if (!preview.Changed)
                {
                    Log(syncOnly ? "Accolade points and collections are already synchronized." : "No selected accolades needed changes.", "info");
                    return;
                }

                string verb = syncOnly
                    ? $"Synchronize Accolade collections ({preview.CollectionsUpdated} collection record(s))?"
                    : $"{(isReset ? "Reset" : "Complete")} {preview.AccoladesUpdated:n0} selected accolade(s)?";
                string prompt = $"{verb} Accolade points: {preview.PreviousPoints:n0} → {preview.NewPoints:n0}. "
                    + $"{preview.CollectionsUpdated} collection record(s) will update. "
                    + (unavailable > 0 ? $"{unavailable} unsupported row(s) will stay unchanged. " : "")
                    + "Reward claims and Hall of Fame upload counters stay unchanged. Export save writes a separate file.";
                if (!_confirmUnlockTest(prompt)) return;

                Fh5AccoladesState.ChangeSummary result;
                bool applied = syncOnly
                    ? Fh5AccoladesState.TrySyncPointsAndCollections(_container, out result, out error)
                    : isReset
                        ? Fh5AccoladesState.TryReset(_container, ids, out result, out error)
                        : Fh5AccoladesState.TryComplete(_container, ids, out result, out error);
                if (!applied) throw new InvalidOperationException(error);

                _hasUnlockTestEdits |= result.Changed;
                RefreshSaveStateDisplays();
                BuildCareer();
                BuildOverview();
                string actionWord = syncOnly ? "synced" : (isReset ? "reset" : "completed");
                Log($"FH5 Accolades {actionWord}: {result.AccoladesUpdated:n0} accolade(s), {result.CollectionsUpdated} collection(s), points {result.PreviousPoints:n0} → {result.NewPoints:n0} — click Export save", "ok");
                StatusLine.Text = "FH5 Accolades updated in working copy — click Export save";
            }
            catch (Exception ex) { Fail("update FH5 Accolades", ex); }
        }
    }
}
#endif
