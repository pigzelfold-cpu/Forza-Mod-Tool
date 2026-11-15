using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

public sealed record LegacyFileInfo(LegacyContext Context, byte[] Iv, uint PaddingBytes, uint DataBytes)
{
    public long PlainBytes => (long)DataBytes - PaddingBytes;
    public string IvHex => Convert.ToHexString(Iv);
}

/// <summary>Authenticated TransformIT non-GameDB containers for FH3, FH4, FM7 and
/// FM6 Apex (File, ConfigFile, Profile, Reward, Photo, Dynamic and SFS). Every
/// header and block CMAC is verified before output is returned.</summary>
public static class LegacyAssetContainer
{
    private const int CipherBlock = 16;

    public static bool HasHeaderAuthentication(long length, ReadOnlySpan<byte> header)
        => Identify(header, length) is not null;

    public static bool TryIdentify(string path, out LegacyFileInfo? info)
    {
        info = null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 32 || stream.Length > uint.MaxValue) return false;
            Span<byte> header = stackalloc byte[36];
            int read = stream.Read(header);
            info = Identify(header[..read], stream.Length);
            return info is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static LegacyFileInfo? Identify(ReadOnlySpan<byte> header, long totalBytes)
        => IdentifyFrom(header, totalBytes, LegacyKeyStore.SupportedContexts, includeGameDb: false, allowEmptyPayload: false);

    public static LegacyFileInfo? IdentifyAny(ReadOnlySpan<byte> header, long totalBytes)
        => IdentifyFrom(header, totalBytes, LegacyKeyStore.Contexts, includeGameDb: true, allowEmptyPayload: true);

    private static LegacyFileInfo? IdentifyFrom(
        ReadOnlySpan<byte> header,
        long totalBytes,
        IReadOnlyList<LegacyContext> contexts,
        bool includeGameDb,
        bool allowEmptyPayload)
    {
        if (totalBytes < 0 || totalBytes > uint.MaxValue) return null;

        Span<byte> signed = stackalloc byte[24];
        Span<byte> mac = stackalloc byte[TfitMac.Size];

        foreach (var context in contexts)
        {
            if (!includeGameDb && context.KeyType == LegacyKeyType.GameDB) continue;

            int headerSize = context.HeaderSize;
            int stride = context.BlockSize + CipherBlock;
            long minBytes = allowEmptyPayload ? headerSize : headerSize + stride;
            if (header.Length < headerSize || totalBytes < minBytes) continue;
            if ((totalBytes - headerSize) % stride != 0) continue;

            long blocks = (totalBytes - headerSize) / stride;
            long rawBytes = blocks * context.BlockSize;
            if (rawBytes > uint.MaxValue) continue;
            uint dataBytes = (uint)rawBytes;
            uint padding = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(signed, dataBytes);
            header[..CipherBlock].CopyTo(signed[4..]);
            int signedLength = 20;
            if (context.Container == LegacyContainer.Horizon)
            {
                padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
                if (blocks == 0 ? padding != 0 : padding >= (uint)context.BlockSize) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(signed[20..], padding);
                signedLength = 24;
            }

            TfitMac.Compute(context.Tables.Mac, context.MacKeys, signed[..signedLength], mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, header.Slice(headerSize - TfitMac.Size, TfitMac.Size)))
                continue;

            return new LegacyFileInfo(context, header[..CipherBlock].ToArray(), padding, dataBytes);
        }

        return null;
    }

    public static byte[] Decrypt(byte[] encrypted, out LegacyFileInfo info, bool trimMotorsportTextPadding = false)
    {
        info = Identify(encrypted, encrypted.Length)
            ?? throw new InvalidDataException("Legacy Forza container header authentication failed. Unsupported key/build or damaged file.");
        return DecryptWithInfo(encrypted, info, trimMotorsportTextPadding);
    }

    public static byte[] DecryptAny(ReadOnlySpan<byte> encrypted, LegacyFileInfo info)
        => DecryptWithInfo(encrypted, info, trimMotorsportTextPadding: false);

    private static byte[] DecryptWithInfo(ReadOnlySpan<byte> encrypted, LegacyFileInfo info, bool trimMotorsportTextPadding)
    {
        var context = info.Context;
        var cipher = context.Tables.Decryption;
        int blockSize = context.BlockSize;
        int expectedTotal = checked(context.HeaderSize + (int)(info.DataBytes / blockSize) * (blockSize + CipherBlock));
        if (encrypted.Length != expectedTotal)
            throw new InvalidDataException($"{context.Label} container size does not match its authenticated header.");

        var plaintext = new byte[checked((int)info.PlainBytes)];
        var block = new byte[blockSize];
        Span<byte> previous = stackalloc byte[CipherBlock];
        Span<byte> storedMac = stackalloc byte[TfitMac.Size];
        Span<byte> mac = stackalloc byte[TfitMac.Size];
        info.Iv.CopyTo(previous);

        var input = encrypted[context.HeaderSize..];
        int chunkNumber = 0;

        for (long offset = 0; offset < info.DataBytes; offset += blockSize)
        {
            chunkNumber++;
            for (int i = 0; i < blockSize; i += CipherBlock)
            {
                DecryptCbc(cipher, context.DecryptionKeys, input[..CipherBlock], previous, block.AsSpan(i));
                input = input[CipherBlock..];
            }

            DecryptCbc(cipher, context.DecryptionKeys, input[..CipherBlock], previous, storedMac);
            input = input[CipherBlock..];

            TfitMac.Compute(context.Tables.Mac, context.MacKeys, block, mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, storedMac))
                throw new InvalidDataException($"{context.Label} authentication failed in chunk {chunkNumber}. No output was written.");

            context.Scramble?.Apply(offset, block);

            int keep = (int)Math.Min(blockSize, plaintext.Length - offset);
            if (keep > 0) block.AsSpan(0, keep).CopyTo(plaintext.AsSpan((int)offset));
        }

        if (trimMotorsportTextPadding
            && context.Container == LegacyContainer.Motorsport
            && context.KeyType == LegacyKeyType.ConfigFile)
        {
            int trimmed = plaintext.Length;
            while (trimmed > 0 && plaintext[trimmed - 1] == 0) trimmed--;
            if (trimmed > 0 && !plaintext.AsSpan(0, trimmed).Contains((byte)0))
                return plaintext.AsSpan(0, trimmed).ToArray();
        }

        return plaintext;
    }

    public static byte[] Encrypt(
        byte[] plaintext,
        byte[] template,
        out LegacyContext context,
        bool trimMotorsportTextPadding = false)
    {
        // Authenticate every block of the template before re-using its IV and context.
        _ = Decrypt(template, out var templateInfo, trimMotorsportTextPadding: false);
        context = templateInfo.Context;
        if (plaintext.Length == 0)
            throw new InvalidDataException($"{context.Label} payload must contain at least one byte.");
        if (trimMotorsportTextPadding
            && context.Container == LegacyContainer.Motorsport
            && context.KeyType == LegacyKeyType.ConfigFile
            && plaintext[^1] == 0)
        {
            // Prevent ambiguity when trimming trailing NUL padding on text config round-trips;
            // binary ConfigFile payloads without trailing NULs or non-trimmed callers pass through.
            if (!plaintext.AsSpan(0, plaintext.Length - 1).Contains((byte)0))
                throw new InvalidDataException($"{context.Label} text assets cannot end with a NUL byte.");
        }

        byte[] encrypted = EncryptRaw(plaintext, context, templateInfo.Iv);
        byte[] verify = Decrypt(encrypted, out var verifyInfo, trimMotorsportTextPadding);
        if (verifyInfo.Context != context)
            throw new InvalidDataException($"{context.Label} round-trip context verification failed.");

        bool exactMatch = verify.AsSpan().SequenceEqual(plaintext);
        bool paddedMotorsportMatch = !exactMatch
            && context.Container == LegacyContainer.Motorsport
            && verify.Length >= plaintext.Length
            && verify.AsSpan(0, plaintext.Length).SequenceEqual(plaintext)
            && !verify.AsSpan(plaintext.Length).ContainsAnyExcept((byte)0);

        if (!exactMatch && !paddedMotorsportMatch)
            throw new InvalidDataException($"{context.Label} authenticated round-trip verification failed. No output was written.");

        return encrypted;
    }

    public static byte[] EncryptRaw(ReadOnlySpan<byte> plaintext, LegacyContext context, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != CipherBlock) throw new ArgumentException("The IV must be 16 bytes.", nameof(iv));

        int blockSize = context.BlockSize;
        long dataBytes = ((long)plaintext.Length + blockSize - 1) / blockSize * blockSize;
        if (dataBytes > uint.MaxValue) throw new NotSupportedException("File is too large for this format.");
        uint padding = (uint)(dataBytes - plaintext.Length);

        long blocks = dataBytes / blockSize;
        var output = new byte[checked(context.HeaderSize + blocks * (blockSize + CipherBlock))];

        Span<byte> signed = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(signed, (uint)dataBytes);
        iv.CopyTo(signed[4..]);
        iv.CopyTo(output);
        int signedLength = 20;
        if (context.Container == LegacyContainer.Horizon)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(signed[20..], padding);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), padding);
            signedLength = 24;
        }
        TfitMac.Compute(context.Tables.Mac, context.MacKeys, signed[..signedLength],
            output.AsSpan(context.HeaderSize - TfitMac.Size, TfitMac.Size));

        var cipher = context.Tables.Encryptor;
        var block = new byte[blockSize];
        Span<byte> previous = stackalloc byte[CipherBlock];
        Span<byte> mac = stackalloc byte[TfitMac.Size];
        iv.CopyTo(previous);
        var destination = output.AsSpan(context.HeaderSize);

        for (long offset = 0; offset < dataBytes; offset += blockSize)
        {
            int take = (int)Math.Min(blockSize, plaintext.Length - offset);
            plaintext.Slice((int)offset, take).CopyTo(block);
            block.AsSpan(take).Clear();

            context.Scramble?.Apply(offset, block.AsSpan(0, take));
            TfitMac.Compute(context.Tables.Mac, context.MacKeys, block, mac);

            for (int i = 0; i < blockSize; i += CipherBlock)
            {
                EncryptCbc(cipher, context.DecryptionKeys, block.AsSpan(i, CipherBlock), previous, destination);
                destination = destination[CipherBlock..];
            }
            EncryptCbc(cipher, context.DecryptionKeys, mac, previous, destination);
            destination = destination[CipherBlock..];
        }

        return output;
    }

    private static void DecryptCbc(
        TfitTables cipher, ReadOnlySpan<uint> keys, ReadOnlySpan<byte> source, Span<byte> previous, Span<byte> destination)
    {
        Span<byte> clear = stackalloc byte[CipherBlock];
        cipher.ProcessBlock(keys, source, clear);
        for (int i = 0; i < CipherBlock; i++) destination[i] = (byte)(clear[i] ^ previous[i]);
        source.CopyTo(previous);
    }

    private static void EncryptCbc(
        TfitInverse cipher, ReadOnlySpan<uint> keys, ReadOnlySpan<byte> source, Span<byte> previous, Span<byte> destination)
    {
        Span<byte> mixed = stackalloc byte[CipherBlock];
        for (int i = 0; i < CipherBlock; i++) mixed[i] = (byte)(source[i] ^ previous[i]);
        cipher.ProcessBlock(keys, mixed, destination);
        destination[..CipherBlock].CopyTo(previous);
    }
}
