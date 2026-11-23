using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FH6LocalCryptoTool;

/// <summary>Recomputes Size and SHA-256 Checksum entries inside FH5 CMS manifests before repacking CMS.zip.</summary>
public static class Fh5CmsManifest
{
    public readonly record struct SyncResult(int UpdatedManifests, int UpdatedEntries);

    public static SyncResult SyncWorkspace(string workspaceRoot)
    {
        string root = Path.GetFullPath(workspaceRoot);
        string onDiskPath = Path.Combine(root, "on-disk-manifest.xml");
        if (!File.Exists(onDiskPath) || !IsCmsManifestFile(onDiskPath))
            return new(0, 0);

        var targets = new List<string> { onDiskPath };
        foreach (string snap in Directory.EnumerateFiles(root, "snapshot-manifest-*.xml", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (IsCmsManifestFile(snap)) targets.Add(snap);
        }

        var fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.Equals(Fh5ZipArchive.ManifestName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(".__original.zip", StringComparison.OrdinalIgnoreCase))
                continue;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            fileMap[rel] = file;
            byName.TryAdd(name, file);
        }

        var metrics = new Dictionary<string, (string Size, string Sha256)>(StringComparer.OrdinalIgnoreCase);
        (string Size, string Sha256)? GetMetrics(string cmsId)
        {
            if (string.IsNullOrWhiteSpace(cmsId)) return null;
            string norm = cmsId.Replace('\\', '/').TrimStart('/');
            if (!fileMap.TryGetValue(norm, out string? diskPath) && !byName.TryGetValue(Path.GetFileName(norm), out diskPath))
                return null;
            if (!metrics.TryGetValue(diskPath, out var cached))
            {
                if ((File.GetAttributes(diskPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("ZIP workspace links/junctions are not supported.");
                byte[] bytes = File.ReadAllBytes(diskPath);
                cached = (bytes.Length.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(SHA256.HashData(bytes)));
                metrics[diskPath] = cached;
            }
            return cached;
        }

        int updatedManifests = 0;
        int updatedEntries = 0;

        foreach (string manifestPath in targets)
        {
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP workspace links/junctions are not supported.");
            byte[] raw = File.ReadAllBytes(manifestPath);
            bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            var filesProp = doc.Root?.Elements("property").FirstOrDefault(e => (string?)e.Attribute("id") == "Files");
            if (filesProp == null) continue;

            int manifestChanged = 0;
            foreach (var mapEl in filesProp.Elements("map_element"))
            {
                var valEl = mapEl.Element("value");
                if (valEl == null) continue;
                var cmsIdProp = valEl.Elements("property").FirstOrDefault(p => (string?)p.Attribute("id") == "CmsId");
                var sizeProp = valEl.Elements("property").FirstOrDefault(p => (string?)p.Attribute("id") == "Size");
                var chkProp = valEl.Elements("property").FirstOrDefault(p => (string?)p.Attribute("id") == "Checksum");
                if (cmsIdProp == null || sizeProp == null || chkProp == null) continue;

                string cmsId = (string?)cmsIdProp.Attribute("value") ?? "";
                if (GetMetrics(cmsId) is not { } m) continue;

                string oldSize = (string?)sizeProp.Attribute("value") ?? "";
                string oldChk = (string?)chkProp.Attribute("value") ?? "";
                if (!string.Equals(oldSize, m.Size, StringComparison.Ordinal) ||
                    !string.Equals(oldChk, m.Sha256, StringComparison.Ordinal))
                {
                    sizeProp.SetAttributeValue("value", m.Size);
                    chkProp.SetAttributeValue("value", m.Sha256);
                    manifestChanged++;
                }
            }

            if (manifestChanged == 0) continue;

            using var ms = new MemoryStream();
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = doc.Declaration == null,
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = nl,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom, throwOnInvalidBytes: true),
            };
            using (var xw = XmlWriter.Create(ms, settings))
                doc.Save(xw);

            File.WriteAllBytes(manifestPath, ms.ToArray());
            updatedManifests++;
            updatedEntries += manifestChanged;
        }

        return new(updatedManifests, updatedEntries);
    }

    static bool IsCmsManifestFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var fi = new FileInfo(path);
            if (fi.Length is < 32 or > 20_000_000) return false;
            var doc = XDocument.Load(path, LoadOptions.None);
            return doc.Root?.Name.LocalName == "manifest"
                && doc.Root.Elements("property").Any(e => (string?)e.Attribute("id") == "Files");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }
}
