using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    private bool _swapBusy;

    private sealed record SwapExecutionResult(
        bool IsFh5,
        byte[] SwappedBytes,
        ulong DonorXuid,
        ulong TargetXuid,
        string FormatSummary,
        string TargetInstallName);

    private void SaveInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool hasFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DonorSave_Drop(object sender, DragEventArgs e)
    {
        var files = GetFiles(e);
        if (files.Length > 0 && File.Exists(files[0]))
        {
            DonorSaveText.Text = files[0];
            DescribeSelectedSave(files[0], isDonor: true);
        }
    }

    private void TargetSave_Drop(object sender, DragEventArgs e)
    {
        var files = GetFiles(e);
        if (files.Length > 0 && File.Exists(files[0]))
        {
            TargetSaveText.Text = files[0];
            DescribeSelectedSave(files[0], isDonor: false);
        }
    }

    private void BrowseDonorSave_Click(object sender, RoutedEventArgs e)
    {
        string? path = BrowseProfile("Select the donor FH5 ProfileData or FH6 C_ProfileData save");
        if (path is not null)
        {
            DonorSaveText.Text = path;
            DescribeSelectedSave(path, isDonor: true);
        }
    }

    private void BrowseTargetSave_Click(object sender, RoutedEventArgs e)
    {
        string? path = BrowseProfile("Select your target FH5 ProfileData or FH6 C_ProfileData save");
        if (path is not null)
        {
            TargetSaveText.Text = path;
            DescribeSelectedSave(path, isDonor: false);
        }
    }

    private void DescribeSelectedSave(string path, bool isDonor)
    {
        string role = isDonor ? "Donor" : "Target";
        var fh5Format = Fh5ProfileCrypto.DetectFormat(path);
        if (fh5Format != Fh5ProfileFormat.Unknown)
        {
            SwapStatusText.Text = $"{role} selected: {Path.GetFileName(path)} ({Fh5ProfileCrypto.DisplayFormat(fh5Format)}).";
            return;
        }
        if (isDonor && Fh5ProfileCrypto.IsPlainPayload(path))
        {
            SwapStatusText.Text = $"{role} selected: {Path.GetFileName(path)} (decrypted FH5 profile payload).";
            return;
        }
        if (Path.GetFileName(path).StartsWith("C_ProfileData", StringComparison.OrdinalIgnoreCase))
        {
            SwapStatusText.Text = $"{role} selected: {Path.GetFileName(path)} (FH6 C_ProfileData).";
        }
    }

    private async void FindTargetSave_Click(object sender, RoutedEventArgs e)
    {
        if (_swapBusy) return;
        try
        {
            SwapStatusText.Text = "Scanning local Steam, MS Store / Xbox (WGS & PGS), OnlineFix, EMPRESS and RUNE save folders…";
            var candidates = await Task.Run(SaveLocator.FindCandidates);
            if (candidates.Count == 0)
            {
                SwapStatusText.Text = "No local FH5 or FH6 profile saves were found automatically. Use Browse… to pick your save.";
                MessageBox.Show(
                    "No Forza Horizon 5 or Forza Horizon 6 saves were found in standard Steam, MS Store / Xbox (WGS & PGS), OnlineFix, EMPRESS or RUNE folders.\n\nUse Browse… to select your save file manually.",
                    "Forza Mod Tool — Find Save",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var picked = ShowSaveCandidatePicker(candidates);
            if (picked is not null)
            {
                TargetSaveText.Text = picked.Path;
                SwapStatusText.Text = $"Target selected: {picked.HeaderLine}\n{picked.Path}";
            }
            else
            {
                SwapStatusText.Text = $"Found {candidates.Count} local save(s). Selection cancelled.";
            }
        }
        catch (Exception ex)
        {
            SwapStatusText.Text = "ERROR: " + ex.Message;
        }
    }

    private SaveLocator.Candidate? ShowSaveCandidatePicker(IReadOnlyList<SaveLocator.Candidate> candidates)
    {
        var bg = Freeze(Color.FromRgb(0x1D, 0x1D, 0x1F));
        var card = Freeze(Color.FromRgb(0x29, 0x29, 0x2C));
        var line = Freeze(Color.FromRgb(0x3C, 0x3C, 0x40));
        var ink = Freeze(Color.FromRgb(0xF4, 0xF4, 0xF6));
        var dim = Freeze(Color.FromRgb(0x9C, 0x9C, 0xA2));
        var pink = Freeze(Color.FromRgb(0xE8, 0x17, 0x5D));

        var dialog = new Window
        {
            Title = "Select Your Target Save (FH5 / FH6)",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 760,
            Height = 440,
            MinWidth = 540,
            MinHeight = 320,
            Background = bg,
            Foreground = ink,
            ResizeMode = ResizeMode.CanResize,
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = $"Detected {candidates.Count} local Forza Horizon 5 / 6 profile save(s). Double-click or press Use Selected Save:",
            Foreground = dim,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var listBox = new ListBox
        {
            Background = card,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            Foreground = ink,
            Padding = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);

        foreach (var candidate in candidates)
        {
            var itemStack = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };
            itemStack.Children.Add(new TextBlock
            {
                Text = candidate.HeaderLine,
                Foreground = ink,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
            });
            itemStack.Children.Add(new TextBlock
            {
                Text = candidate.SubLine,
                Foreground = dim,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            var item = new ListBoxItem
            {
                Content = itemStack,
                Tag = candidate,
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
            };
            item.MouseDoubleClick += (_, _) => { dialog.DialogResult = true; };
            listBox.Items.Add(item);
        }

        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 1);
        root.Children.Add(listBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 100,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
            Background = card,
            Foreground = ink,
            BorderBrush = line,
            Cursor = Cursors.Hand,
        };
        var okBtn = new Button
        {
            Content = "Use Selected Save",
            Padding = new Thickness(18, 0, 18, 0),
            Height = 36,
            IsDefault = true,
            Background = pink,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        okBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem) dialog.DialogResult = true;
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        return dialog.ShowDialog() == true && listBox.SelectedItem is ListBoxItem { Tag: SaveLocator.Candidate chosen }
            ? chosen
            : null;
    }

    private static byte[] ReadBoundedSaveBytes(string path, string label)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 32 || file.Length > Fh5ProfileCrypto.MaxFileBytes)
            throw new InvalidDataException($"{label} save file size is invalid or exceeds safe limits ({Fh5ProfileCrypto.MaxFileBytes / (1024 * 1024)} MB).");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string UniqueSwappedOutputPath(string directory, string baseFileName)
    {
        string candidate = Path.Combine(directory, baseFileName);
        if (!File.Exists(candidate)) return candidate;
        string stamped = Path.Combine(directory, $"{baseFileName}.{DateTime.Now:yyyyMMdd-HHmmss}");
        if (!File.Exists(stamped)) return stamped;
        return Path.Combine(directory, $"{baseFileName}.{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
    }

    private string? ResolveSwappedOutputPath(string donorFull, string targetFull, bool isFh5)
    {
        string baseFileName = isFh5
            ? Path.GetFileName(targetFull) + ".swapped"
            : "C_ProfileData.swapped";

        if (isFh5 && SaveLocator.IsLiveWgsContainerPath(targetFull))
        {
            // Never drop extra generated files inside a live UWP SystemAppData\wgs container folder.
            if (_outputDir is not null && Directory.Exists(_outputDir) && !SaveLocator.IsLiveWgsContainerPath(_outputDir))
                return UniqueSwappedOutputPath(_outputDir, baseFileName);

            var dlg = new SaveFileDialog
            {
                Title = "Save swapped FH5 WGS save outside the live SystemAppData\\wgs folder",
                FileName = baseFileName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Filter = "Swapped Forza Save (*.swapped)|*.swapped|All files|*.*",
                OverwritePrompt = false,
            };
            if (dlg.ShowDialog(this) != true)
                return null;

            string chosen = Path.GetFullPath(dlg.FileName);
            if (SaveLocator.IsLiveWgsContainerPath(chosen))
                throw new InvalidOperationException("Do not write extra .swapped files directly inside a live SystemAppData\\wgs container folder. Save to Desktop or a backup folder first, then replace the container blob after backing up wgs.");
            if (chosen.Equals(donorFull, StringComparison.OrdinalIgnoreCase) || chosen.Equals(targetFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a new output file; donor and target saves are never overwritten.");
            if (File.Exists(chosen))
                chosen = UniqueSwappedOutputPath(Path.GetDirectoryName(chosen)!, Path.GetFileName(chosen));
            return chosen;
        }

        string outputDir = Path.GetDirectoryName(targetFull)!;
        return UniqueSwappedOutputPath(outputDir, baseFileName);
    }

    private async void SwapSave_Click(object sender, RoutedEventArgs e)
    {
        if (_swapBusy) return;
        string donor = DonorSaveText.Text.Trim().Trim('"');
        string target = TargetSaveText.Text.Trim().Trim('"');
        try
        {
            if (!File.Exists(donor)) throw new FileNotFoundException("Choose a valid donor save file (FH5 ProfileData or FH6 C_ProfileData).");
            if (!File.Exists(target)) throw new FileNotFoundException("Choose a valid target save file (FH5 ProfileData or FH6 C_ProfileData).");

            string donorFull = Path.GetFullPath(donor);
            string targetFull = Path.GetFullPath(target);
            if (string.Equals(donorFull, targetFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Donor and target must be different files.");

            _swapBusy = true;
            SwapSaveButton.IsEnabled = false;
            SwapStatusText.Text = "Decrypting, authenticating and verifying donor and target saves…";

            var result = await Task.Run(() => ExecuteVerifiedSaveSwap(donorFull, targetFull));

            string? outPath = ResolveSwappedOutputPath(donorFull, targetFull, result.IsFh5);
            if (outPath is null)
            {
                SwapStatusText.Text = "Save Swap cancelled before writing output; original files are unchanged.";
                return;
            }

            await Task.Run(() =>
            {
                using var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(result.SwappedBytes);
                stream.Flush(flushToDisk: true);
            });

            string gameName = result.IsFh5 ? "Forza Horizon 5" : "Forza Horizon 6";
            SwapStatusText.Text =
                $"Verified {gameName} swapped save created ({result.FormatSummary}):\n" +
                $"{outPath}\n\n" +
                $"Account identity remapped: Donor XUID {result.DonorXuid} (0x{result.DonorXuid:X16})  →  Target XUID {result.TargetXuid} (0x{result.TargetXuid:X16})\n" +
                $"The originals were not changed. Close {gameName} and back up your entire save folder first, then replace {result.TargetInstallName} with a renamed copy of this file.";

            Log($"Save swap [{result.FormatSummary}]: {Path.GetFileName(donorFull)} (XUID {result.DonorXuid}) -> target XUID {result.TargetXuid} -> {outPath}");
            if (OpenFolderCheck.IsChecked == true) OpenFolder(outPath);
        }
        catch (Exception ex)
        {
            SwapStatusText.Text = "ERROR: " + ex.Message;
            MessageBox.Show(ex.Message, "Forza Mod Tool — Save Swap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _swapBusy = false;
            SwapSaveButton.IsEnabled = true;
        }
    }

    private static SwapExecutionResult ExecuteVerifiedSaveSwap(string donorPath, string targetPath)
    {
        byte[] donorBytes = ReadBoundedSaveBytes(donorPath, "Donor");
        byte[] targetBytes = ReadBoundedSaveBytes(targetPath, "Target");

        var fh6Key = Fh6Keys.Get("ProfileData");
        bool targetIsFh6 = ForzaZip.ValidateContainer(targetBytes, fh6Key.DataKey, fh6Key.MacKey, out _);
        if (targetIsFh6)
        {
            var (swapped, donorXuid, targetXuid) = ProfileData.SwapWithDetails(donorBytes, targetBytes);
            return new SwapExecutionResult(
                IsFh5: false,
                SwappedBytes: swapped,
                DonorXuid: donorXuid,
                TargetXuid: targetXuid,
                FormatSummary: "FH6 C_ProfileData",
                TargetInstallName: "C_ProfileData");
        }

        bool targetIsFh5 = Fh5ProfileCrypto.DetectFormat(targetBytes.Length, targetBytes) != Fh5ProfileFormat.Unknown;
        bool donorIsFh5 = Fh5ProfileCrypto.DetectFormat(donorBytes.Length, donorBytes) != Fh5ProfileFormat.Unknown
            || Fh5ProfileCrypto.IsPlainPayload(donorPath);

        if (targetIsFh5 || donorIsFh5)
        {
            var fh5 = Fh5ProfileCrypto.Swap(donorBytes, targetBytes);
            string donorLabel = fh5.DonorWasPlaintext ? "FH5 (decrypted)" : Fh5ProfileCrypto.DisplayFormat(fh5.DonorFormat);
            string targetLabel = Fh5ProfileCrypto.DisplayFormat(fh5.TargetFormat);
            string summary = !fh5.DonorWasPlaintext && fh5.DonorFormat == fh5.TargetFormat
                ? targetLabel
                : $"{donorLabel} donor → {targetLabel} target";

            return new SwapExecutionResult(
                IsFh5: true,
                SwappedBytes: fh5.Encrypted,
                DonorXuid: fh5.DonorXuid,
                TargetXuid: fh5.TargetXuid,
                FormatSummary: summary,
                TargetInstallName: Path.GetFileName(targetPath));
        }

        if (LegacyAssetContainer.Identify(targetBytes, targetBytes.Length) is { } legacyTarget)
            throw new InvalidDataException($"Target is a {legacyTarget.Context.Label} file. Save Swap supports Forza Horizon 5 and Forza Horizon 6 saves only. No swapped save was written.");
        if (LegacyAssetContainer.Identify(donorBytes, donorBytes.Length) is { } legacyDonor)
            throw new InvalidDataException($"Donor is a {legacyDonor.Context.Label} file. Save Swap supports Forza Horizon 5 and Forza Horizon 6 saves only. No swapped save was written.");

        throw new InvalidDataException("Target is not an authenticated Forza Horizon 5 (ProfileData) or Forza Horizon 6 (C_ProfileData) save. No swapped save was written.");
    }
}
