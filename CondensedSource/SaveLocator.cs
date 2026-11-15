using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

public static class SaveLocator
{
    public enum SaveGame { Fh5, Fh6 }

    public sealed record Candidate(
        string Path,
        SaveGame Game,
        string Source,
        DateTime Modified,
        long Size,
        string? Xuid = null,
        string? CryptoDetail = null)
    {
        public string FileName => System.IO.Path.GetFileName(Path);

        public string SizeLabel => Size >= 1024 * 1024
            ? $"{Size / (1024.0 * 1024.0):0.00} MB"
            : $"{Size / 1024.0:0.0} KB";

        public string HeaderLine => CryptoDetail is { Length: > 0 }
            ? $"{Source}  ·  {CryptoDetail}  ·  {FileName}"
            : $"{Source}  ·  {FileName}";

        public string SubLine =>
            $"{(Xuid is { Length: > 0 } ? $"XUID {Xuid}   ·   " : "")}{SizeLabel}   ·   Modified {Modified:yyyy-MM-dd HH:mm}   ·   {Path}";
    }

    private const string Fh5SteamAppId = "1551360";

    public static bool IsLiveWgsContainerPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.Contains(@"\SystemAppData\wgs\", StringComparison.OrdinalIgnoreCase)
                || full.EndsWith(@"\SystemAppData\wgs", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static List<Candidate> FindCandidates()
    {
        var found = new List<Candidate>();
        TryAdd(found, ScanFh5Steam);
        TryAdd(found, ScanFh5MsStore);
        TryAdd(found, ScanFh5OfflineStores);
        TryAdd(found, ScanFh6Pgs);
        TryAdd(found, ScanFh6Rune);

        return found
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(c => c.Modified)
            .ToList();
    }

    private static void TryAdd(List<Candidate> list, Func<IEnumerable<Candidate>> scan)
    {
        try { list.AddRange(scan()); }
        catch { /* Best-effort read-only discovery */ }
    }

    private static IEnumerable<Candidate> ScanFh5Steam()
    {
        foreach (string userdata in SteamUserdataRoots())
        {
            foreach (string steamAccount in SafeDirs(userdata))
            {
                string remote = Path.Combine(steamAccount, Fh5SteamAppId, "remote");
                foreach (string xuidDir in SafeDirs(remote))
                {
                    string name = Path.GetFileName(xuidDir);
                    string? xuid = name.Length > 0 && name.All(char.IsDigit) ? name : null;
                    foreach (string save in SafeFiles(xuidDir, "*.ProfileData"))
                    {
                        if (BuildFh5Candidate(save, "FH5 · Steam", xuid) is { } candidate)
                            yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SteamUserdataRoots()
    {
        var roots = new List<string> { @"C:\Program Files (x86)\Steam" };
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string path && path.Length > 0)
                    roots.Add(path.Replace('/', '\\'));
            }
            catch { }
        }

        return roots
            .Select(root => Path.Combine(root, "userdata"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<Candidate> ScanFh5MsStore()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string package in new[] { "Microsoft.624F8B84B80_8wekyb3d8bbwe", "Microsoft.SunriseBaseGame_8wekyb3d8bbwe" })
        {
            string wgs = Path.Combine(localAppData, "Packages", package, "SystemAppData", "wgs");
            foreach (string account in SafeDirs(wgs))
            {
                if (Path.GetFileName(account).Equals("t", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string container in SafeDirs(account))
                {
                    var files = SafeFiles(container, "*").ToList();
                    bool holdsProfile = files
                        .Where(f => Path.GetFileName(f).StartsWith("container.", StringComparison.OrdinalIgnoreCase))
                        .Any(f => FileContainsTextAsciiOrUtf16(f, "ProfileData"));
                    if (!holdsProfile) continue;

                    var blobs = files
                        .Where(f => !Path.GetFileName(f).StartsWith("container.", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => SafeLength(f));
                    foreach (string blob in blobs)
                    {
                        if (Fh5ProfileCrypto.DetectFormat(blob) != Fh5ProfileFormat.Unknown &&
                            BuildFh5Candidate(blob, "FH5 · MS Store / Xbox (WGS)", null) is { } candidate)
                        {
                            yield return candidate;
                            break;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<Candidate> ScanFh5OfflineStores()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new (string Dir, string Source)[]
        {
            ($@"C:\Users\Public\Documents\OnlineFix\{Fh5SteamAppId}\Saves", "FH5 · OnlineFix"),
            ($@"C:\Users\Public\Documents\EMPRESS\{Fh5SteamAppId}", "FH5 · EMPRESS"),
            (Path.Combine(appData, "EMPRESS", Fh5SteamAppId), "FH5 · EMPRESS"),
            (Path.Combine(appData, "Goldberg SteamEmu Saves", Fh5SteamAppId), "FH5 · Goldberg"),
        };

        foreach (var (dir, source) in roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string save in SafeFilesRecursive(dir, "*.ProfileData", maxDepth: 4))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(save) ?? "");
                string? xuid = parent.Length > 0 && parent.All(char.IsDigit) ? parent : null;
                if (BuildFh5Candidate(save, source, xuid) is { } candidate)
                    yield return candidate;
            }
        }
    }

    private static Candidate? BuildFh5Candidate(string path, string source, string? xuid)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0) return null;
            var format = Fh5ProfileCrypto.DetectFormat(info.FullName);
            string? detail = format != Fh5ProfileFormat.Unknown ? Fh5ProfileCrypto.DisplayFormat(format) : null;
            return new Candidate(info.FullName, SaveGame.Fh5, source, info.LastWriteTime, info.Length, xuid, detail);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Candidate> ScanFh6Pgs()
    {
        var roots = new List<string> { @"C:\XboxGames\GameSave\pgs" };
        foreach (var drive in SafeDrives())
        {
            string candidate = Path.Combine(drive.RootDirectory.FullName, "XboxGames", "GameSave", "pgs");
            if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                roots.Add(candidate);
        }

        foreach (string pgsRoot in roots)
        {
            if (!Directory.Exists(pgsRoot)) continue;
            foreach (string accountRoot in SafeDirs(pgsRoot))
            {
                string accountName = Path.GetFileName(accountRoot);
                if (!accountName.StartsWith("u_", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = accountName.Split('_');
                string? xuid = parts.Length >= 2 && parts[1].All(char.IsDigit) ? parts[1] : null;
                string? versionDir = ResolvePgsVersionDir(accountRoot);
                if (versionDir is null) continue;

                foreach (var c in ScanFh6ContainersRoot(Path.Combine(versionDir, "ContainersRoot"), "FH6 · Xbox / Retail (PGS)", xuid))
                    yield return c;
            }
        }
    }

    private static string? ResolvePgsVersionDir(string accountRoot)
    {
        string current = Path.Combine(accountRoot, "current");
        if (Directory.Exists(current)) return current;
        return SafeDirs(accountRoot)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .Where(v => int.TryParse(v.Name, out _))
            .OrderByDescending(v => int.Parse(v.Name))
            .Select(v => v.Path)
            .FirstOrDefault();
    }

    private static IEnumerable<Candidate> ScanFh6Rune()
    {
        var roots = new List<string> { @"C:\Users\Public\Documents\MicrosoftStore\RUNE" };
        foreach (var drive in SafeDrives())
        {
            string candidate = Path.Combine(drive.RootDirectory.FullName, "Users", "Public", "Documents", "MicrosoftStore", "RUNE");
            if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                roots.Add(candidate);
        }

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string titleDir in SafeDirs(root))
            {
                if (!Path.GetFileName(titleDir).StartsWith("Forza Horizon 6", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (string accountDir in SafeDirs(titleDir))
                {
                    string xuid = Path.GetFileName(accountDir);
                    if (xuid.Length == 0 || !xuid.All(char.IsDigit)) continue;
                    string containers = Path.Combine(accountDir, "SaveGames", "ContainersRoot");
                    foreach (var c in ScanFh6ContainersRoot(containers, "FH6 · RUNE", xuid))
                        yield return c;
                }
            }
        }
    }

    private static IEnumerable<Candidate> ScanFh6ContainersRoot(string containersRoot, string source, string? xuid)
    {
        if (!Directory.Exists(containersRoot)) yield break;
        foreach (string userDir in SafeDirs(containersRoot))
        {
            string userName = Path.GetFileName(userDir);
            if (!userName.StartsWith("User_", StringComparison.OrdinalIgnoreCase)
                || userName.EndsWith("_Backup", StringComparison.OrdinalIgnoreCase))
                continue;

            string profile = Path.Combine(userDir, "C_ProfileData");
            if (!File.Exists(profile)) continue;
            var fi = new FileInfo(profile);
            if (fi.Length > 0)
                yield return new Candidate(fi.FullName, SaveGame.Fh6, source, fi.LastWriteTime, fi.Length, xuid, "FH6 C_ProfileData");
        }
    }

    private static bool FileContainsTextAsciiOrUtf16(string path, string text)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] buffer = new byte[Math.Min(stream.Length, 1 << 20)];
            int read = stream.Read(buffer, 0, buffer.Length);
            ReadOnlySpan<byte> span = buffer.AsSpan(0, read);
            return span.IndexOf(Encoding.Unicode.GetBytes(text)) >= 0
                || span.IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }
        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch { ok = false; }
            if (ok) yield return d;
        }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFilesRecursive(string root, string pattern, int maxDepth)
    {
        foreach (string file in SafeFiles(root, pattern))
            yield return file;
        if (maxDepth <= 0) yield break;
        foreach (string sub in SafeDirs(root))
            foreach (string file in SafeFilesRecursive(sub, pattern, maxDepth - 1))
                yield return file;
    }
}
