using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public enum Fh5ProfileFormat
{
    Unknown = 0,
    Latest,
    LegacyV1_619_349_0,
    LegacyLaunch,
}

public sealed record Fh5SwapResult(
    byte[] Encrypted,
    Fh5ProfileFormat DonorFormat,
    Fh5ProfileFormat TargetFormat,
    ulong DonorXuid,
    ulong TargetXuid,
    bool DonorWasPlaintext = false);

/// <summary>Authenticated FH5 profile decryption, re-encryption and save swapping across latest and legacy FH5 profile formats.</summary>
public static class Fh5ProfileCrypto
{
    public const int MaxFileBytes = 128 * 1024 * 1024;
    const int Header = 36, Chunk = 512, Slot = 528;

    static readonly string[] SqliteExtensions = { ".sqlite", ".sqlite3", ".db", ".db3" };

    static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fh5-profile-data.json", true));
    static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fh5-profile-mac.json", false));
    static readonly Lazy<LegacyContext> Legacy619Context = new(() =>
        LegacyKeyStore.Find(LegacyGame.FH5_v1_619_349_0, LegacyKeyType.Profile)
        ?? throw new InvalidOperationException("Missing FH5 v1.619.349.0 Profile key context."));
    static readonly Lazy<LegacyContext> LegacyLaunchContext = new(() =>
        LegacyKeyStore.Find(LegacyGame.FH5, LegacyKeyType.Profile)
        ?? throw new InvalidOperationException("Missing legacy FH5 Profile key context."));

    public static string DisplayFormat(Fh5ProfileFormat format) => format switch
    {
        Fh5ProfileFormat.Latest => "FH5 (latest)",
        Fh5ProfileFormat.LegacyV1_619_349_0 => "FH5 (v1.619.349.0)",
        Fh5ProfileFormat.LegacyLaunch => "FH5 (legacy / launch–v1.614)",
        _ => "FH5",
    };

    public static bool HasLatestHeaderAuthentication(long length, ReadOnlySpan<byte> header)
    {
        if (length < Header + Slot || length > MaxFileBytes || (length - Header) % Slot != 0 || header.Length < Header) return false;
        int padded = checked((int)((length - Header) / Slot * Chunk));
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (padding >= Chunk || padding >= padded) return false;
        Span<byte> input = stackalloc byte[24], tag = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)padded);
        header[..20].CopyTo(input[4..]);
        BinaryPrimitives.WriteUInt128LittleEndian(tag, Mac.Value.Cmac(input));
        return CryptographicOperations.FixedTimeEquals(tag, header.Slice(20, 16));
    }

    static bool HasLegacyHeaderAuthentication(LegacyContext context, long length, ReadOnlySpan<byte> header, out LegacyFileInfo? info)
    {
        info = null;
        if (length < Header + Slot || length > MaxFileBytes || (length - Header) % Slot != 0 || header.Length < Header) return false;
        int padded = checked((int)((length - Header) / Slot * Chunk));
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (padding >= Chunk || padding >= padded) return false;
        Span<byte> signed = stackalloc byte[24], tag = stackalloc byte[TfitMac.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(signed, (uint)padded);
        header[..16].CopyTo(signed[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(signed[20..], padding);
        TfitMac.Compute(context.Tables.Mac, context.MacKeys, signed, tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, header.Slice(20, 16))) return false;
        info = new LegacyFileInfo(context, header[..16].ToArray(), padding, (uint)padded);
        return true;
    }

    public static Fh5ProfileFormat DetectFormat(long length, ReadOnlySpan<byte> header)
    {
        if (HasLatestHeaderAuthentication(length, header)) return Fh5ProfileFormat.Latest;
        if (HasLegacyHeaderAuthentication(Legacy619Context.Value, length, header, out _)) return Fh5ProfileFormat.LegacyV1_619_349_0;
        if (HasLegacyHeaderAuthentication(LegacyLaunchContext.Value, length, header, out _)) return Fh5ProfileFormat.LegacyLaunch;
        return Fh5ProfileFormat.Unknown;
    }

    public static Fh5ProfileFormat DetectFormat(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[Header];
            return file.Read(header) == Header ? DetectFormat(file.Length, header) : Fh5ProfileFormat.Unknown;
        }
        catch (IOException) { return Fh5ProfileFormat.Unknown; }
        catch (UnauthorizedAccessException) { return Fh5ProfileFormat.Unknown; }
    }

    public static bool HasHeaderAuthentication(long length, ReadOnlySpan<byte> header)
        => DetectFormat(length, header) != Fh5ProfileFormat.Unknown;

    public static bool IsEncryptedProfile(string path)
        => DetectFormat(path) != Fh5ProfileFormat.Unknown;

    public static bool IsDatabaseExport(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Contains(".fh5-profile", StringComparison.OrdinalIgnoreCase) &&
            SqliteExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            return true;
        // Keep a renamed FH5 export out of the FH6 re-encryption path too.
        // The shared Career_Garage/PurchasedParts pair alone is not game identity.
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Span<byte> header = stackalloc byte[16];
                if (file.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8)) return false;
            }
            using var db = OpenReadOnly(path);
            return HasFh5ProfileSchema(db);
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }

    static bool HasFh5ProfileSchema(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('Career_Garage','BadgeTitleProgress','BadgeTitleUnlocks','PerkState','PerkUnlock')";
        return Convert.ToInt64(command.ExecuteScalar()) == 5;
    }

    public static bool IsPlainPayload(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> marker = stackalloc byte[4];
            if (file.Length > MaxFileBytes || file.Read(marker) != 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(marker) != ProfileContainer.MarkerProfile) return false;
            file.Position = 0;
            byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            return IsProfileDatabase(ProfileContainer.Parse(bytes).Database);
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // A manually decrypted/renamed section stream must not bypass the disabled
    // FH5 viewer and enter the editable FH6 profile path.
    public static bool IsProfileDatabase(byte[] database)
    {
        if (database.Length < 16 || !database.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) return false;
        Directory.CreateDirectory(TempWorkspace.Root);
        string work = Path.Combine(TempWorkspace.Root, "profile-schema-check-" + Guid.NewGuid().ToString("N") + ".sqlite");
        bool created = false;
        try
        {
            using (var output = new FileStream(work, FileMode.CreateNew, FileAccess.Write)) { created = true; output.Write(database); }
            return IsDatabaseExport(work);
        }
        finally { if (created) CleanupDatabase(work); }
    }

    public static byte[] ReadInput(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < Header + Slot || file.Length > MaxFileBytes) throw new InvalidDataException("FH5 save exceeds safe size limits or is truncated.");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }

    static byte[] ReadAnyInput(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaxFileBytes) throw new InvalidDataException("FH5 profile input exceeds safe size limits.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }

    public static byte[] Decrypt(byte[] encrypted) => Decrypt(encrypted, out _);

    public static byte[] Decrypt(byte[] encrypted, out Fh5ProfileFormat format)
    {
        format = DetectFormat(encrypted.Length, encrypted);
        if (format == Fh5ProfileFormat.Unknown)
            throw new InvalidDataException("Not an authenticated supported FH5 profile save. The file may be damaged or use another crypto profile/build.");

        byte[] unpadded = format switch
        {
            Fh5ProfileFormat.Latest => DecryptLatestUnpadded(encrypted),
            Fh5ProfileFormat.LegacyV1_619_349_0 => DecryptLegacyUnpadded(encrypted, Legacy619Context.Value),
            Fh5ProfileFormat.LegacyLaunch => DecryptLegacyUnpadded(encrypted, LegacyLaunchContext.Value),
            _ => throw new InvalidDataException("Unsupported FH5 profile format."),
        };

        if (unpadded.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(unpadded) != ProfileContainer.MarkerProfile)
        {
            if (unpadded.Length <= Chunk)
                throw new InvalidDataException("This file is an authenticated FH5 profile metadata sidecar (such as .VersionFlags or .Meta), not a full FH5 ProfileData save. Select User_<id>.ProfileData instead.");
            throw new InvalidDataException("Unsupported FH5 profile section framing.");
        }

        return ParsePayload(unpadded);
    }

    static byte[] DecryptLatestUnpadded(byte[] encrypted)
    {
        int count = (encrypted.Length - Header) / Slot;
        byte[] raw = new byte[count * Chunk];
        var data = Data.Value; var mac = Mac.Value;
        UInt128 previous = BinaryPrimitives.ReadUInt128LittleEndian(encrypted);
        Span<byte> actual = stackalloc byte[16], expected = stackalloc byte[16];
        for (int n = 0; n < count; n++)
        {
            int start = Header + n * Slot;
            Span<byte> plain = raw.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                UInt128 current = BinaryPrimitives.ReadUInt128LittleEndian(encrypted.AsSpan(start + i));
                BinaryPrimitives.WriteUInt128LittleEndian(plain[i..], data.Forward(current) ^ previous);
                previous = current;
            }
            UInt128 tag = BinaryPrimitives.ReadUInt128LittleEndian(encrypted.AsSpan(start + Chunk));
            BinaryPrimitives.WriteUInt128LittleEndian(actual, data.Forward(tag) ^ previous);
            BinaryPrimitives.WriteUInt128LittleEndian(expected, mac.Cmac(plain));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new InvalidDataException($"FH5 save authentication failed in chunk {n + 1}. No output was written.");
            previous = tag;
        }
        int padding = (int)BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(16));
        if (raw.AsSpan(raw.Length - padding).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Nonzero FH5 save padding.");
        return raw.AsSpan(0, raw.Length - padding).ToArray();
    }

    static byte[] DecryptLegacyUnpadded(byte[] encrypted, LegacyContext context)
    {
        if (!HasLegacyHeaderAuthentication(context, encrypted.Length, encrypted, out var info) || info is null)
            throw new InvalidDataException($"{context.Label} header authentication failed.");
        return LegacyAssetContainer.DecryptAny(encrypted, info);
    }

    static byte[] EncryptRaw(ReadOnlySpan<byte> payload, Fh5ProfileFormat format, ReadOnlySpan<byte> iv)
    {
        if (payload.Length is <= 0 or > MaxFileBytes)
            throw new InvalidDataException("FH5 profile payload size is invalid.");
        if (iv.Length != 16)
            throw new ArgumentException("FH5 profile IV must be 16 bytes.", nameof(iv));

        return format switch
        {
            Fh5ProfileFormat.Latest => EncryptLatestRaw(payload, iv),
            Fh5ProfileFormat.LegacyV1_619_349_0 => LegacyAssetContainer.EncryptRaw(payload, Legacy619Context.Value, iv),
            Fh5ProfileFormat.LegacyLaunch => LegacyAssetContainer.EncryptRaw(payload, LegacyLaunchContext.Value, iv),
            _ => throw new InvalidDataException("Unsupported FH5 profile encryption format."),
        };
    }

    static byte[] EncryptLatestRaw(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> iv)
    {
        int paddedLength = checked((payload.Length + Chunk - 1) / Chunk * Chunk);
        uint padding = checked((uint)(paddedLength - payload.Length));
        byte[] padded = new byte[paddedLength];
        payload.CopyTo(padded);

        byte[] result = new byte[checked(Header + paddedLength / Chunk * Slot)];
        iv.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), padding);

        Span<byte> headerInput = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(headerInput, checked((uint)paddedLength));
        result.AsSpan(0, 20).CopyTo(headerInput[4..]);
        BinaryPrimitives.WriteUInt128LittleEndian(result.AsSpan(20, 16), Mac.Value.Cmac(headerInput));

        var data = Data.Value;
        var mac = Mac.Value;
        UInt128 previous = BinaryPrimitives.ReadUInt128LittleEndian(result);
        for (int n = 0; n < paddedLength / Chunk; n++)
        {
            int start = Header + n * Slot;
            ReadOnlySpan<byte> plain = padded.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                previous = data.Inverse(BinaryPrimitives.ReadUInt128LittleEndian(plain[i..]) ^ previous);
                BinaryPrimitives.WriteUInt128LittleEndian(result.AsSpan(start + i), previous);
            }
            previous = data.Inverse(mac.Cmac(plain) ^ previous);
            BinaryPrimitives.WriteUInt128LittleEndian(result.AsSpan(start + Chunk), previous);
        }
        return result;
    }

    public static byte[] Encrypt(byte[] payload, byte[] template) => Encrypt(payload, template, out _);

    public static byte[] Encrypt(byte[] payload, byte[] template, out Fh5ProfileFormat format)
    {
        ValidatePayload(payload);
        byte[] templatePayload = Decrypt(template, out format);
        ValidateDatabase(ProfileContainer.Parse(templatePayload).Database);

        byte[] encrypted = EncryptRaw(payload, format, template.AsSpan(0, 16));
        byte[] verify = Decrypt(encrypted, out var verifyFormat);
        if (verifyFormat != format || !verify.AsSpan().SequenceEqual(payload))
            throw new CryptographicException("Re-encrypted FH5 profile failed authentication or exact payload verification; no output was written.");
        return encrypted;
    }

    static void ValidatePayload(byte[] payload)
    {
        if (payload.Length is < 32 or > MaxFileBytes)
            throw new InvalidDataException("Decrypted FH5 profile exceeds safe size limits or is truncated.");
        var profile = ProfileContainer.Parse(payload);
        if (!IsProfileDatabase(profile.Database))
            throw new InvalidDataException("Decrypted profile database does not match the FH5 save schema. Do not mix FH5 and FH6 saves.");
        ValidateDatabase(profile.Database);
    }

    public static Fh5ProfileFormat ValidateTemplate(string template)
    {
        byte[] bytes = ReadInput(template);
        byte[] payload = Decrypt(bytes, out var format);
        ValidatePayload(payload);
        return format;
    }

    public static Fh5ProfileFormat Encrypt(string input, string template, string output)
    {
        byte[] payload = ReadAnyInput(input);
        ValidatePayload(payload);
        byte[] original = ReadInput(template);
        byte[] encrypted = Encrypt(payload, original, out var format);
        VerifyUnchanged(input, payload);
        VerifyUnchanged(template, original);
        WriteNew(output, encrypted);
        return format;
    }

    /// <summary>Reinsert an edited FH5 SQLite snapshot into the original 4-section FH5 profile template while preserving sections 0-2 byte-for-byte.</summary>
    public static Fh5ProfileFormat EncryptDatabase(string input, string template, string output)
    {
        string fingerprint = DatabaseFingerprint(input);
        byte[] original = ReadInput(template);
        byte[] originalPayload = Decrypt(original, out var format);
        ValidatePayload(originalPayload);

        var profile = ProfileContainer.Parse(originalPayload);
        profile.Database = SnapshotDatabase(input, fingerprint);
        byte[] payload = profile.Serialize();
        ValidatePayload(payload);

        byte[] encrypted = Encrypt(payload, original, out var outFormat);
        if (outFormat != format)
            throw new CryptographicException("FH5 profile template format changed unexpectedly; no output was written.");
        if (DatabaseFingerprint(input) != fingerprint)
            throw new IOException("The FH5 profile database changed during processing; no output was written. Save your edits and try again.");
        VerifyUnchanged(template, original);
        WriteNew(output, encrypted);
        return format;
    }

    public static (int Offset, ulong Xuid) LocateCanonicalAccount(byte[] payload, string label = "FH5 save")
    {
        // Account identity belongs to the binary career section's header [recordCount, account XUID],
        // verified by full tag + ordinal + round-trip serialization of every record in section 2.
        // Note: offline/EMPRESS/Goldberg FH5 saves use 32-bit XUIDs (< 2^32), so any nonzero 64-bit XUID is valid.
        var container = ProfileContainer.Parse(payload);
        byte[] binary = container.Sections[ProfileContainer.BinaryIndex].Payload;
        BinaryCareer career;
        try
        {
            career = BinaryCareer.Parse(binary, hasXuid: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"{label} career account header could not be safely parsed. No swapped save was written.", ex);
        }

        if (!career.RoundTripsCleanly(binary) || career.Xuid == 0)
            throw new InvalidDataException($"{label} career account header is invalid or unsupported. No swapped save was written.");

        int xuidOffset = 0;
        for (int index = 0; index < ProfileContainer.BinaryIndex; index++)
            xuidOffset = checked(xuidOffset + 8 + container.Sections[index].Payload.Length);
        xuidOffset = checked(xuidOffset + 8 + 4);
        if (BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(xuidOffset, 8)) != career.Xuid)
            throw new InvalidDataException($"{label} career account offset failed verification. No swapped save was written.");
        return (xuidOffset, career.Xuid);
    }

    /// <summary>
    /// Swaps donor FH5 progress onto the target FH5 save's account XUID and re-encrypts in the target's exact
    /// FH5 crypto format (Latest, v1.619.349.0, or Legacy Launch) and IV. Supports cross-generation swaps
    /// (e.g. legacy 2021-2023 donor save -> latest 2025/2026 FH5 target save).
    /// </summary>
    public static Fh5SwapResult Swap(byte[] donorInput, byte[] targetEncrypted)
    {
        byte[] targetPayload = DecryptSwapInput(targetEncrypted, "Target", allowPlaintext: false, out var targetFormat, out _);
        byte[] donorPayload = DecryptSwapInput(donorInput, "Donor", allowPlaintext: true, out var donorFormat, out bool donorPlain);

        var donorAccount = LocateCanonicalAccount(donorPayload, "Donor FH5 save");
        var targetAccount = LocateCanonicalAccount(targetPayload, "Target FH5 save");

        byte[] swappedPayload = (byte[])donorPayload.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(swappedPayload.AsSpan(donorAccount.Offset, 8), targetAccount.Xuid);

        byte[] swapped = Encrypt(swappedPayload, targetEncrypted, out var encryptedFormat);
        if (encryptedFormat != targetFormat)
            throw new CryptographicException("Swapped FH5 save failed target format verification. No swapped save was written.");

        byte[] check = Decrypt(swapped, out var verifiedFormat);
        if (verifiedFormat != targetFormat || !check.AsSpan().SequenceEqual(swappedPayload))
            throw new CryptographicException("Swapped FH5 save failed payload verification. No swapped save was written.");
        if (LocateCanonicalAccount(check, "Swapped FH5 save").Xuid != targetAccount.Xuid)
            throw new CryptographicException("Swapped FH5 save failed account verification. No swapped save was written.");
        ValidateDatabase(ProfileContainer.Parse(check).Database);

        return new Fh5SwapResult(swapped, donorFormat, targetFormat, donorAccount.Xuid, targetAccount.Xuid, donorPlain);
    }

    static byte[] DecryptSwapInput(byte[] input, string label, bool allowPlaintext, out Fh5ProfileFormat format, out bool wasPlaintext)
    {
        wasPlaintext = false;
        var fh6Key = Fh6Keys.Get("ProfileData");
        if (ForzaZip.ValidateContainer(input, fh6Key.DataKey, fh6Key.MacKey, out _))
            throw new InvalidDataException($"{label} is an FH6 C_ProfileData save, while the other file is an FH5 save. Both saves must be from the same game. No swapped save was written.");

        format = DetectFormat(input.Length, input);
        if (format != Fh5ProfileFormat.Unknown)
        {
            byte[] payload;
            try
            {
                payload = Decrypt(input, out format);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"{label}: {ex.Message}", ex);
            }
            ValidatePayload(payload);
            return payload;
        }

        if (allowPlaintext && input.Length >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(input) == ProfileContainer.MarkerProfile)
        {
            var container = ProfileContainer.Parse(input);
            if (!IsProfileDatabase(container.Database))
                throw new InvalidDataException($"{label} is a decrypted non-FH5 profile stream. Both saves must be from Forza Horizon 5. No swapped save was written.");
            ValidateDatabase(container.Database);
            wasPlaintext = true;
            return input;
        }

        if (LegacyAssetContainer.Identify(input, input.Length) is { } legacyOther)
            throw new InvalidDataException($"{label} is a {legacyOther.Context.Label} file. Save Swap supports Forza Horizon 5 and Forza Horizon 6 saves only. No swapped save was written.");

        throw new InvalidDataException($"{label} is not an authenticated FH5 ProfileData save. No swapped save was written.");
    }

    // FH5 stores the four sections directly, without FH6's size/zlib envelope.
    // Native saves can sign padding=0 while carrying up to 511 zero bytes after
    // the database; allow only that bounded, fully authenticated zero tail.
    static byte[] ParsePayload(ReadOnlySpan<byte> raw)
    {
        uint[] markers = { ProfileContainer.MarkerProfile, ProfileContainer.MarkerSaveState, ProfileContainer.MarkerBinary, ProfileContainer.MarkerDatabase };
        int offset = 0;
        foreach (uint marker in markers)
        {
            if (raw.Length - offset < 8 || BinaryPrimitives.ReadUInt32LittleEndian(raw[offset..]) != marker)
                throw new InvalidDataException("Unsupported FH5 profile section framing.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw[(offset + 4)..]);
            if (size == 0 || size > raw.Length - offset - 8) throw new InvalidDataException("Truncated FH5 profile section.");
            offset += 8 + (int)size;
        }
        if (raw.Length - offset >= Chunk || raw[offset..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Unexpected bytes after the FH5 profile database.");
        byte[] payload = raw[..offset].ToArray();
        _ = ProfileContainer.Parse(payload);
        return payload;
    }

    public static ProfileContainer Load(string path) => ProfileContainer.Parse(Decrypt(ReadInput(path)));

    public static void Decrypt(string input, string output) => ExportDecrypted(input, output, databaseOnly: false);

    public static Fh5ProfileFormat DecryptWithFormat(string input, string output) => ExportDecrypted(input, output, databaseOnly: false);

    public static void DecryptDatabase(string input, string output) => ExportDecrypted(input, output, databaseOnly: true);

    static Fh5ProfileFormat ExportDecrypted(string input, string output, bool databaseOnly)
    {
        byte[] original = ReadInput(input);
        byte[] payload = Decrypt(original, out var format);
        var profile = ProfileContainer.Parse(payload);
        ValidateDatabase(profile.Database);
        if (!ReadInput(input).AsSpan().SequenceEqual(original)) throw new IOException("FH5 save changed during processing. Retry with a stable copy; no output written.");
        WriteNew(output, databaseOnly ? profile.Database : payload);
        return format;
    }

    static string DatabaseFingerprint(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in new[] { path, path + "-wal", path + "-journal" })
        {
            bool exists = File.Exists(file);
            hash.AppendData(new[] { exists ? (byte)1 : (byte)0 });
            if (exists)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash.AppendData(SHA256.HashData(stream));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static byte[] SnapshotDatabase(string input, string expectedFingerprint)
    {
        Directory.CreateDirectory(TempWorkspace.Root);
        string work = Path.Combine(TempWorkspace.Root, "fh5-profile-snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var source = OpenReadOnly(input))
            {
                using var begin = source.CreateCommand(); begin.CommandText = "BEGIN DEFERRED"; begin.ExecuteNonQuery();
                if (!HasFh5ProfileSchema(source))
                    throw new InvalidDataException("Choose the SQLite database extracted from an FH5 ProfileData save, not a GameDB or FH6 profile database.");
                CheckQuickCheck(source);
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = work, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
                {
                    target.Open();
                    source.BackupDatabase(target);
                    using var journal = target.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=DELETE"; journal.ExecuteScalar();
                    CheckQuickCheck(target);
                }
            }
            if (DatabaseFingerprint(input) != expectedFingerprint)
                throw new IOException("The FH5 profile database changed during processing; no output was written. Save your edits and try again.");
            return ReadAnyInput(work);
        }
        finally { CleanupDatabase(work); }
    }

    static void CheckQuickCheck(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA quick_check";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) || reader.Read())
            throw new InvalidDataException("FH5 profile database failed quick_check.");
    }

    static void CleanupDatabase(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(file)) File.Delete(file);
    }

    static void VerifyUnchanged(string path, byte[] snapshot)
    {
        if (!ReadAnyInput(path).AsSpan().SequenceEqual(snapshot))
            throw new IOException("An input changed during processing; no output was written. Try again with a stable copy.");
    }

    static void WriteNew(string output, byte[] bytes)
    {
        bool created = false;
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            file.Write(bytes);
        }
        catch
        {
            if (created && File.Exists(output)) File.Delete(output);
            throw;
        }
    }

    public static void ValidateDatabase(byte[] database)
    {
        Directory.CreateDirectory(TempWorkspace.Root);
        string work = Path.Combine(TempWorkspace.Root, "fh5-profile-check-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var file = new FileStream(work, FileMode.CreateNew, FileAccess.Write)) file.Write(database);
            using var db = OpenReadOnly(work);
            CheckQuickCheck(db);
        }
        finally { CleanupDatabase(work); }
    }
}
