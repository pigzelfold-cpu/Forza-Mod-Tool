using System.IO;
using System.Windows;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    private string? _templateFh5Profile;

    bool StageFh5ProfileInput(string path)
    {
        var format = Fh5ProfileCrypto.DetectFormat(path);
        bool encrypted = format != Fh5ProfileFormat.Unknown;
        bool plain = !encrypted && Fh5ProfileCrypto.IsPlainPayload(path);
        bool database = !encrypted && !plain && Fh5ProfileCrypto.IsDatabaseExport(path);
        if (!encrypted && !plain && !database) return false;

        ProfileTemplateRow.Visibility = Visibility.Visible;
        DbTemplateRow.Visibility = Visibility.Collapsed;

        if ((plain || database)
            && (_templateFh5Profile is null || !File.Exists(_templateFh5Profile) || !Fh5ProfileCrypto.IsEncryptedProfile(_templateFh5Profile))
            && TryAutoFindFh5ProfileTemplate(path, out string? autoTemplate))
        {
            _templateFh5Profile = autoTemplate;
            ProfileTemplateText.Text = autoTemplate!;
        }

        string modeLabel = encrypted
            ? $"{Fh5ProfileCrypto.DisplayFormat(format)} save → Decrypt"
            : plain
                ? "FH5 decrypted profile → Re-encrypt"
                : "FH5 profile SQLite → Re-encrypt";

        StagedText.Text = $"Staged: {Path.GetFileName(path)}   ({modeLabel})";
        StagedText.Visibility = Visibility.Visible;
        Log(database
            ? "FH5 profile SQLite staged. Re-encrypt inserts the edited database into the selected FH5 ProfileData template while retaining its other three sections."
            : plain
                ? "Decrypted full FH5 profile staged. Click Re-encrypt to rebuild using the selected FH5 ProfileData template."
                : $"Authenticated {Fh5ProfileCrypto.DisplayFormat(format)} save staged. Decrypt exports the complete .fh5-profile.decrypted payload without changing the original.");
        Status("FH5 save staged.");
        return true;
    }

    private bool TryAutoFindFh5ProfileTemplate(string plainPath, out string? templatePath)
    {
        templatePath = null;
        string full = Path.GetFullPath(plainPath);
        if (_assetTemplates.TryGetValue(full, out string? mapped)
            && File.Exists(mapped)
            && Fh5ProfileCrypto.IsEncryptedProfile(mapped))
        {
            templatePath = mapped;
            return true;
        }

        string stem = StripFh5ProfileSuffix(Path.GetFileName(full), isDatabase: false);
        string candidate = Path.Combine(Path.GetDirectoryName(full) ?? ".", stem);
        if (!string.Equals(Path.GetFullPath(candidate), full, StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate)
            && Fh5ProfileCrypto.IsEncryptedProfile(candidate))
        {
            templatePath = candidate;
            return true;
        }

        return false;
    }

    private static string StripFh5ProfileSuffix(string fileName, bool isDatabase)
    {
        if (isDatabase)
            fileName = Path.GetFileNameWithoutExtension(fileName);
        else if (fileName.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^10];

        if (fileName.EndsWith(".fh5-profile", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^12];

        return fileName;
    }

    private string? ResolveSafeFh5OutputDir(string sourcePath)
    {
        if (_outputDir is not null && Directory.Exists(_outputDir) && !SaveLocator.IsLiveWgsContainerPath(_outputDir))
            return _outputDir;

        if (!SaveLocator.IsLiveWgsContainerPath(sourcePath))
            return OutputDirFor(sourcePath);

        // Never add generated files to a live WGS container by default.
        BrowseOutput_Click(this, new RoutedEventArgs());
        if (_outputDir is null || !Directory.Exists(_outputDir) || SaveLocator.IsLiveWgsContainerPath(_outputDir))
        {
            Log("Choose an output folder outside the live SystemAppData\\wgs container first; original unchanged.");
            Status("Choose an output folder outside wgs; original unchanged.");
            return null;
        }
        return _outputDir;
    }

    async Task DecryptFh5ProfileFlow(string input)
    {
        string? outDir = ResolveSafeFh5OutputDir(input);
        if (outDir is null) return;

        string output = UniqueOutputPath(Path.Combine(outDir, Path.GetFileName(input) + ".fh5-profile.decrypted"));
        Status("Decrypting and authenticating FH5 save…");
        var format = await Task.Run(() => Fh5ProfileCrypto.DecryptWithFormat(input, output));
        _templateFh5Profile = input;
        _assetTemplates[Path.GetFullPath(output)] = input;
        ProfileTemplateText.Text = input;
        Log($"Decrypted [{Fh5ProfileCrypto.DisplayFormat(format)} / authenticated] {Path.GetFileName(input)} -> {output}. All save sections retained; original unchanged.");
        Log("Drop the .fh5-profile.decrypted profile here and click Re-encrypt to rebuild with its FH5 profile template.");
        Done(output, "FH5 profile decrypted OK.");
    }

    async Task EncryptFh5ProfileFlow(string path)
    {
        string? template = _templateFh5Profile;
        if (template is null || !File.Exists(template) || !Fh5ProfileCrypto.IsEncryptedProfile(template))
        {
            if (!TryAutoFindFh5ProfileTemplate(path, out template))
                template = BrowseProfile("Select the original encrypted FH5 ProfileData template");
        }

        if (template is null || !File.Exists(template))
        {
            Log("Re-encrypt needs an encrypted FH5 ProfileData template. Decrypt your FH5 save first or use the Profile template Browse… button.");
            Status("No FH5 profile template set; no output written.");
            return;
        }

        string? outDir = ResolveSafeFh5OutputDir(path);
        if (outDir is null) return;

        bool database = !Fh5ProfileCrypto.IsPlainPayload(path) && Fh5ProfileCrypto.IsDatabaseExport(path);
        string stem = StripFh5ProfileSuffix(Path.GetFileName(path), database);
        string output = UniqueOutputPath(Path.Combine(outDir, stem + ".re-encrypted"));

        Status($"Re-encrypting FH5 profile {Path.GetFileName(path)}…");
        var format = await Task.Run(() => database
            ? Fh5ProfileCrypto.EncryptDatabase(path, template, output)
            : Fh5ProfileCrypto.Encrypt(path, template, output));

        _templateFh5Profile = template;
        ProfileTemplateText.Text = template;
        if (database)
            Log("Edited FH5 SQLite reinserted; original profile, save-state and binary career sections preserved byte-for-byte from the selected template.");
        Log($"Re-encrypted [{Fh5ProfileCrypto.DisplayFormat(format)} / authenticated and verified] {Path.GetFileName(output)}. Exact decrypted-payload and database checks passed; originals unchanged.");
        Log("Back up your entire save folder and close FH5 before replacing your ProfileData file.");
        Done(output, "FH5 profile re-encrypted and verified.");
    }

    private async Task<bool> TrySetFh5ProfileTemplateAsync(string path)
    {
        if (!Fh5ProfileCrypto.IsEncryptedProfile(path)) return false;
        var format = await Task.Run(() => Fh5ProfileCrypto.ValidateTemplate(path));
        _templateFh5Profile = path;
        ProfileTemplateText.Text = path;
        string label = Fh5ProfileCrypto.DisplayFormat(format);
        Log($"Validated {label} profile template set: {Path.GetFileName(path)}");
        Status($"{label} template set.");
        return true;
    }
}
