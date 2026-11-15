using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    private string? _templateLegacyProfile;
    private readonly Dictionary<string, ForzaGame> _sqliteGames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Marker, LegacyGame Game, string Label)[] LegacyProfileMarkers =
    {
        (".fh4-profile.", LegacyGame.FH4, "FH4 Profile"),
        (".fh3dev-profile.", LegacyGame.FH3Dev, "FH3 OpusDev Profile"),
        (".fh3-profile.", LegacyGame.FH3, "FH3 Profile"),
        (".fm7-profile.", LegacyGame.FM7, "FM7 Profile"),
        (".fm6apex-profile.", LegacyGame.FM6Apex, "FM6 Apex Profile"),
    };

    private static bool TryGetLegacyProfileMarker(string path, out LegacyGame game, out string label)
    {
        string fileName = Path.GetFileName(path);
        foreach (var entry in LegacyProfileMarkers)
        {
            if (fileName.Contains(entry.Marker, StringComparison.OrdinalIgnoreCase))
            {
                game = entry.Game;
                label = entry.Label;
                return true;
            }
        }
        game = default;
        label = "";
        return false;
    }

    private static string StripLegacyProfileSuffix(string fileName)
    {
        if (fileName.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^10];
        foreach (var entry in LegacyProfileMarkers)
        {
            string tag = entry.Marker.TrimEnd('.');
            if (fileName.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                return fileName[..^tag.Length];
        }
        return fileName;
    }

    private bool TryPeekLegacyProfileTemplate(string plainPath, out string? templatePath, out LegacyFileInfo? info)
    {
        templatePath = null;
        info = null;
        string full = Path.GetFullPath(plainPath);

        if (_assetTemplates.TryGetValue(full, out string? mapped)
            && File.Exists(mapped)
            && LegacyAssetContainer.TryIdentify(mapped, out info)
            && info!.Context.KeyType == LegacyKeyType.Profile)
        {
            templatePath = mapped;
            return true;
        }

        string baseName = StripLegacyProfileSuffix(Path.GetFileName(plainPath));
        string candidate = Path.Combine(Path.GetDirectoryName(full) ?? ".", baseName);
        if (!string.Equals(Path.GetFullPath(candidate), full, StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate)
            && LegacyAssetContainer.TryIdentify(candidate, out info)
            && info!.Context.KeyType == LegacyKeyType.Profile)
        {
            templatePath = candidate;
            return true;
        }

        if (_templateLegacyProfile is not null
            && File.Exists(_templateLegacyProfile)
            && LegacyAssetContainer.TryIdentify(_templateLegacyProfile, out info)
            && info!.Context.KeyType == LegacyKeyType.Profile)
        {
            if (!TryGetLegacyProfileMarker(plainPath, out var expectedGame, out _) || info.Context.Game == expectedGame)
            {
                templatePath = _templateLegacyProfile;
                return true;
            }
        }

        return false;
    }

    private bool IsLegacyDecryptedProfile(string path, out string label)
    {
        label = "";
        if (!File.Exists(path) || LegacyAssetContainer.TryIdentify(path, out _)) return false;
        if (TryGetLegacyProfileMarker(path, out _, out label)) return true;

        string name = Path.GetFileName(path);
        bool hasDecryptedSuffix = name.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(name).EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase);
        if (hasDecryptedSuffix && TryPeekLegacyProfileTemplate(path, out _, out var info))
        {
            label = info!.Context.Label;
            return true;
        }
        return false;
    }

    private static bool IsEncryptedLegacyProfile(string path, out LegacyFileInfo? info)
    {
        if (LegacyAssetContainer.TryIdentify(path, out info) && info!.Context.KeyType == LegacyKeyType.Profile)
            return true;
        info = null;
        return false;
    }

    private static bool IsLegacyAssetContainer(string path, out LegacyFileInfo? info)
    {
        if (LegacyAssetContainer.TryIdentify(path, out info) && info!.Context.KeyType != LegacyKeyType.Profile)
            return true;
        info = null;
        return false;
    }

    private bool StageLegacyProfileInput(string path)
    {
        if (IsEncryptedLegacyProfile(path, out var encryptedInfo))
        {
            ProfileTemplateRow.Visibility = Visibility.Visible;
            DbTemplateRow.Visibility = Visibility.Collapsed;
            StagedText.Text = $"Staged: {Path.GetFileName(path)}   ({encryptedInfo!.Context.Label} → Decrypt (authenticated))";
            StagedText.Visibility = Visibility.Visible;
            Log($"Authenticated {encryptedInfo.Context.Label} staged. Decrypt exports the complete profile without modifying the original.");
            Status($"{encryptedInfo.Context.Label} staged.");
            return true;
        }

        if (IsLegacyDecryptedProfile(path, out string plainLabel))
        {
            ProfileTemplateRow.Visibility = Visibility.Visible;
            DbTemplateRow.Visibility = Visibility.Collapsed;
            StagedText.Text = $"Staged: {Path.GetFileName(path)}   ({plainLabel} → Re-encrypt)";
            StagedText.Visibility = Visibility.Visible;
            Log($"Decrypted {plainLabel} staged. Click Re-encrypt with its original encrypted profile template.");
            Status($"Decrypted {plainLabel} staged.");
            return true;
        }

        return false;
    }

    private async Task DecryptLegacyProfileFlow(string input)
    {
        Status($"Decrypting {Path.GetFileName(input)}…");
        var (output, contextLabel, inBytes, outBytes) = await Task.Run(() =>
        {
            byte[] encrypted = File.ReadAllBytes(input);
            byte[] plain = LegacyAssetContainer.Decrypt(encrypted, out var info, trimMotorsportTextPadding: false);
            if (info.Context.KeyType != LegacyKeyType.Profile)
                throw new InvalidDataException($"Expected a profile container, but authenticated {info.Context.Label}.");
            string tag = LegacyKeyStore.ProfileTag(info.Context.Game);
            string outPath = UniqueOutputPath(Path.Combine(OutputDirFor(input), $"{Path.GetFileName(input)}.{tag}.decrypted"));
            WriteNewAsset(outPath, plain);
            return (outPath, info.Context.Label, encrypted.Length, plain.Length);
        });

        _templateLegacyProfile = input;
        _assetTemplates[Path.GetFullPath(output)] = input;
        ProfileTemplateText.Text = input;
        Log($"Decrypted [{contextLabel} / authenticated] {Path.GetFileName(input)} -> {Path.GetFileName(output)} ({inBytes:n0} -> {outBytes:n0} bytes). Original unchanged.");
        Log("Drop the decrypted profile here and click Re-encrypt to rebuild with its original profile template.");
        Done(output, $"{contextLabel} decrypted OK.");
    }

    private async Task EncryptLegacyProfileFlow(string plainPath)
    {
        string? template = null;
        if (TryPeekLegacyProfileTemplate(plainPath, out string? found, out _))
            template = found;
        else
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select the original encrypted FH3 / FH4 / FM7 / FM6 Apex profile template",
                Filter = "All files|*.*"
            };
            if (dlg.ShowDialog(this) == true) template = dlg.FileName;
        }

        if (template is null || !File.Exists(template))
        {
            Log("Re-encrypt needs the original encrypted profile template. Decrypt it first or select it with Browse….");
            Status("No profile template set; no output written.");
            return;
        }

        bool hasMarker = TryGetLegacyProfileMarker(plainPath, out var expectedGame, out string expectedLabel);
        Status($"Re-encrypting {Path.GetFileName(plainPath)}…");
        string stem = StripLegacyProfileSuffix(Path.GetFileName(plainPath));
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(plainPath), stem + ".re-encrypted"));

        var (contextLabel, inBytes, outBytes) = await Task.Run(() =>
        {
            byte[] plaintext = File.ReadAllBytes(plainPath);
            byte[] original = File.ReadAllBytes(template);
            _ = LegacyAssetContainer.Decrypt(original, out var templateInfo, trimMotorsportTextPadding: false);
            if (templateInfo.Context.KeyType != LegacyKeyType.Profile)
                throw new InvalidDataException($"The selected template is {templateInfo.Context.Label}, not a Profile save.");
            if (hasMarker && templateInfo.Context.Game != expectedGame)
                throw new InvalidDataException($"The decrypted file is {expectedLabel}, but the selected template is {templateInfo.Context.Label}. Choose the matching original save.");

            byte[] encrypted = LegacyAssetContainer.Encrypt(plaintext, original, out var context, trimMotorsportTextPadding: false);
            WriteNewAsset(output, encrypted);
            return (context.Label, plaintext.Length, encrypted.Length);
        });

        _templateLegacyProfile = template;
        ProfileTemplateText.Text = template;
        Log($"Re-encrypted [{contextLabel} / authenticated and verified] {Path.GetFileName(plainPath)} -> {Path.GetFileName(output)} ({inBytes:n0} -> {outBytes:n0} bytes). Originals unchanged.");
        Done(output, $"{contextLabel} re-encrypted and verified.");
    }

    private async Task<bool> TrySetLegacyProfileTemplateAsync(string path)
    {
        if (!IsEncryptedLegacyProfile(path, out _)) return false;
        string label = await Task.Run(() =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            _ = LegacyAssetContainer.Decrypt(bytes, out var verified, trimMotorsportTextPadding: false);
            return verified.Context.Label;
        });
        _templateLegacyProfile = path;
        ProfileTemplateText.Text = path;
        Log($"Validated {label} template set: {Path.GetFileName(path)}");
        Status($"{label} template set.");
        return true;
    }

    private async Task RepackLegacyZipFlow(string workspace)
    {
        string name = LegacyZipArchive.OriginalName(workspace);
        string ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) ext = ".zip";
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(workspace), Path.GetFileNameWithoutExtension(name) + ".modded" + ext));
        Status("Rebuilding and verifying legacy ZIP…");
        var progress = new Progress<LegacyZipArchive.Progress>(p => Status($"Checking/rebuilding ZIP: {p.Completed:n0}/{p.EntryCount:n0} entries…"));
        var result = await Task.Run(() => LegacyZipArchive.Repack(workspace, output, progress));
        Log($"Rebuilt [{result.ContextLabel} ZIP, all lengths/CRCs and encrypted MACs verified] {result.EntryCount:n0} entries, {result.ChangedEntries:n0} changed -> {output}");
        if (result.ChangedEntries == 0) Log("    Unchanged workspace: byte-for-byte original ZIP. Untouched payloads and original entry methods/metadata are preserved after edits.");
        Log("    Original files and previous exports were not overwritten. No game files were installed.");
        Done(output, $"{result.ContextLabel} ZIP rebuilt and verified OK.");
    }

    private bool IsDecryptedBinaryOrTrackedAsset(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string full = Path.GetFullPath(path);
            bool hasMarker = _assetTemplates.ContainsKey(full)
                || Path.GetFileName(full).EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(full).EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase);
            if (!hasMarker) return false;
            if (IsSqlite(full) || IsSkeld(full) || IsSkeldJson(full)) return false;
            if (MotorsportCmsArchive.HasZipSignature(full)) return false;
            if (IsGamedbContainer(full) || IsAssetContainer(full)) return false;
            if (IsEncryptedLegacyProfile(full, out _) || Fh5ProfileCrypto.IsEncryptedProfile(full)) return false;
            return true;
        }
        catch { return false; }
    }
}
