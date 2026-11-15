using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>
/// Converts an encrypted GameDB to a private temporary SQLite file for the
/// existing editor/merge engine. Legacy plain-SQLite SLTs retain their format.
/// Callers own only their outputs; source .slt and .sqlite files are read-only.
/// </summary>
public static class GameDbSqliteBridge
{
    private const string TempPrefix = "forza_mod_studio_slt_";
    public static bool IsSlt(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".slt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".~b1", StringComparison.OrdinalIgnoreCase); // FH3 obfuscated .slt extension
    }

    public static string SltExtension(string path) =>
        Path.GetExtension(path).Equals(".~b1", StringComparison.OrdinalIgnoreCase) ? ".~b1" : ".slt";

    public sealed class MaterializedDatabase : IDisposable
    {
        private readonly bool _ownsFile;
        public string SqlitePath { get; }
        public string? TemplateSlt { get; }
        public string? SourceHash { get; }

        internal MaterializedDatabase(string path, string? templateSlt, bool ownsFile, string? sourceHash = null)
        {
            SqlitePath = path;
            TemplateSlt = templateSlt;
            _ownsFile = ownsFile;
            SourceHash = sourceHash;
        }

        public void VerifySourceUnchanged()
        {
            if (TemplateSlt is not null && SourceHash is not null)
                VerifySourceHash(TemplateSlt, SourceHash);
        }

        public void Dispose()
        {
            if (!_ownsFile) return;
            foreach (string path in new[] { SqlitePath, SqlitePath + "-wal", SqlitePath + "-shm", SqlitePath + "-journal" })
                try { File.Delete(path); } catch { /* cleanup is best effort */ }
        }
    }

    public static MaterializedDatabase Materialize(string inputPath, bool allowMotorsport = false)
    {
        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Database not found.", inputPath);
        if (!IsSlt(inputPath))
        {
            if (LooksLikeSqliteFile(inputPath))
                return new MaterializedDatabase(inputPath, null, false);
            if (GameDbContainerFormat.Detect(inputPath) == GameDbContainerFormat.Kind.Unknown)
                throw new InvalidDataException("Choose a decrypted SQLite database or an encrypted GameDB .slt.");
        }

        string temp = Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, TempPrefix + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            byte[] encrypted = File.ReadAllBytes(inputPath);
            string hash = Convert.ToHexString(SHA256.HashData(encrypted));
            var format = GameDbContainerFormat.Detect(encrypted);
            if (format == GameDbContainerFormat.Kind.Sqlite)
            {
                CheckSqlite(inputPath);
                return new MaterializedDatabase(inputPath, inputPath, false, hash);
            }
            if (format == GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 && !allowMotorsport)
                throw new NotSupportedException("Use the Crypto tab to decrypt Motorsport .slt files, then open the SQLite separately. Motorsport editor/merge integration is not enabled.");
            byte[] sqlite = format switch
            {
                GameDbContainerFormat.Kind.Fh6Aes36 =>
                    GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey),
                GameDbContainerFormat.Kind.Fh5TransformIt36 => Fh5GameDb.Decrypt(encrypted),
                GameDbContainerFormat.Kind.Fh4TransformIt36 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Horizon4),
                GameDbContainerFormat.Kind.Fh3TransformIt36 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Horizon3),
                GameDbContainerFormat.Kind.Fm7TransformIt32 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Motorsport7),
                GameDbContainerFormat.Kind.Fm6ApexTransformIt32 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Motorsport6Apex),
                GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 =>
                    MotorsportGameDb.Decrypt(encrypted),
                _ => throw new InvalidDataException(
                    "The .slt layout is not a recognized Forza GameDB container.")
            };
            if (!GameDb.LooksLikeSqlite(sqlite))
                throw new InvalidDataException(
                    $"The {GameDbContainerFormat.DisplayName(format)} did not decrypt to SQLite. " +
                    "The key or game build is not supported.");
            File.WriteAllBytes(temp, sqlite);
            CheckSqlite(temp);
            return new MaterializedDatabase(temp, inputPath, true, hash);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public static void VerifySourceHash(string path, string expectedHash)
    {
        using var source = File.OpenRead(path);
        string currentHash = Convert.ToHexString(SHA256.HashData(source));
        if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The original .slt changed after it was loaded. Reload it before exporting or merging.");
    }

    public static void EncryptSnapshot(string sqlitePath, string templateSlt, string outputSlt, bool allowMotorsport = false,
        bool exportStringTables = true, string? stringTablesFolder = null)
    {
        sqlitePath = Path.GetFullPath(sqlitePath);
        templateSlt = Path.GetFullPath(templateSlt);
        outputSlt = Path.GetFullPath(outputSlt);
        if (!IsSlt(outputSlt))
            throw new InvalidOperationException("GameDB output must use the .slt (or FH3 .~b1) extension.");
        if (string.Equals(outputSlt, templateSlt, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputSlt, sqlitePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new output path; source files are never overwritten.");
        if (File.Exists(outputSlt))
            throw new IOException("The output .slt already exists. Choose a new filename.");
        CheckSqlite(sqlitePath);

        byte[] sqlite = File.ReadAllBytes(sqlitePath);
        byte[] template = File.ReadAllBytes(templateSlt);
        var format = GameDbContainerFormat.Detect(template);
        if (format is GameDbContainerFormat.Kind.Unknown)
            throw new InvalidDataException("The selected .slt template is not a recognized encrypted GameDB.");
        bool plain = format == GameDbContainerFormat.Kind.Sqlite;
        bool motorsport = format == GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32;
        if (motorsport && !allowMotorsport)
            throw new NotSupportedException("Use the Crypto tab for Motorsport re-encryption. Motorsport editor/merge integration is not enabled.");
        using (var original = Materialize(templateSlt, allowMotorsport))
        {
            original.VerifySourceUnchanged();
            GameDbIdentity.RequireSameGame(sqlitePath, original.SqlitePath);
            var sourceGame = GameDbIdentity.Detect(sqlitePath);
            if (plain && sourceGame is not (ForzaGame.Unknown or ForzaGame.Horizon2))
                throw new InvalidDataException($"{GameDbIdentity.DisplayName(sourceGame)} SLT export requires the original encrypted SLT, not a plaintext SQLite template.");
            var containerGame = GameDbContainerFormat.GameOf(format);
            if (!plain && sourceGame != ForzaGame.Unknown && containerGame != ForzaGame.Unknown && sourceGame != containerGame)
                throw new InvalidDataException($"The edited DB is {GameDbIdentity.DisplayName(sourceGame)}, but the template is {GameDbIdentity.DisplayName(containerGame)}. Choose the original SLT from the same game/build.");
            ValidateTemplateVersion(sqlitePath, original.SqlitePath);
        }
        byte[] encrypted = format switch
        {
            GameDbContainerFormat.Kind.Sqlite => sqlite,
            GameDbContainerFormat.Kind.Fh6Aes36 => GameDb.Encrypt(sqlite, template, Fh6Keys.Get("GameDB").DataKey),
            GameDbContainerFormat.Kind.Fh5TransformIt36 => Fh5GameDb.Encrypt(sqlite, template),
            GameDbContainerFormat.Kind.Fh4TransformIt36 => LegacyGameDb.Encrypt(sqlite, template, ForzaGame.Horizon4),
            GameDbContainerFormat.Kind.Fh3TransformIt36 => LegacyGameDb.Encrypt(sqlite, template, ForzaGame.Horizon3),
            GameDbContainerFormat.Kind.Fm7TransformIt32 => LegacyGameDb.Encrypt(sqlite, template, ForzaGame.Motorsport7),
            GameDbContainerFormat.Kind.Fm6ApexTransformIt32 => LegacyGameDb.Encrypt(sqlite, template, ForzaGame.Motorsport6Apex),
            GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 => MotorsportGameDb.Encrypt(sqlite, template),
            _ => throw new InvalidDataException("The selected .slt template is not a recognized encrypted GameDB.")
        };
        byte[] roundTrip = format switch
        {
            GameDbContainerFormat.Kind.Sqlite => encrypted,
            GameDbContainerFormat.Kind.Fh6Aes36 => GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey),
            GameDbContainerFormat.Kind.Fh5TransformIt36 => Fh5GameDb.Decrypt(encrypted),
            GameDbContainerFormat.Kind.Fh4TransformIt36 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Horizon4),
            GameDbContainerFormat.Kind.Fh3TransformIt36 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Horizon3),
            GameDbContainerFormat.Kind.Fm7TransformIt32 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Motorsport7),
            GameDbContainerFormat.Kind.Fm6ApexTransformIt32 => LegacyGameDb.Decrypt(encrypted, ForzaGame.Motorsport6Apex),
            GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 => MotorsportGameDb.Decrypt(encrypted),
            _ => throw new InvalidDataException("The selected .slt template is not a recognized encrypted GameDB.")
        };
        if (roundTrip.Length < sqlite.Length || !roundTrip.AsSpan(0, sqlite.Length).SequenceEqual(sqlite))
            throw new InvalidDataException("Encrypted output did not reproduce the edited SQLite database.");

        using var labels = exportStringTables ? TrackSpacingLabelExport.Prepare(sqlitePath, outputSlt, templateSlt, stringTablesFolder) : null;
        string partial = outputSlt + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(partial, encrypted);
            labels?.Publish();
            File.Move(partial, outputSlt);
            labels?.Complete();
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    public static GameDbContainerFormat.Kind DetectFormat(string path) => GameDbContainerFormat.Detect(path);

    private static void ValidateTemplateVersion(string sqlitePath, string templateSqlite)
    {
        // Car-only donor exports may omit VersionInfo; full DBs must not be
        // encrypted using a template from another build, even of the same game.
        string? Stamp(string path)
        {
            try { return StockDatabaseCatalog.Version(path); }
            catch (SqliteException) { return null; }
            catch (InvalidDataException) { return null; }
        }
        string? source = Stamp(sqlitePath), template = Stamp(templateSqlite);
        if (source != null && template != null && source != template)
            throw new InvalidDataException("The edited DB and SLT template have different database stamps. Choose the original SLT from the same game/build.");
    }

    private static bool LooksLikeSqliteFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        return stream.Read(header) == 16 &&
               header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static void CheckSqlite(string path)
    {
        if (!LooksLikeSqliteFile(path))
            throw new InvalidDataException("SQLite header not found.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite integrity check failed.");
    }
}
