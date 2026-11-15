using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>Authenticated TransformIT GameDB codec for FH3, FH4, FM7 and FM6 Apex.
/// Verifies the header CMAC, every 128 KiB chunk CMAC, and SQLite framing before
/// returning data or writing re-encrypted outputs.</summary>
public static class LegacyGameDb
{
    private const int Chunk = GameDbContainerFormat.DataChunkSize;
    private const int CipherBlock = 16;
    private const int Slot = Chunk + CipherBlock;
    private static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };

    public static bool HasFraming(ForzaGame game, long length)
    {
        var context = LegacyKeyStore.RequireGameDb(game);
        int headerSize = context.HeaderSize;
        return length >= headerSize + Slot && (length - headerSize) % Slot == 0;
    }

    public static bool HasHeaderAuthentication(ForzaGame game, long length, ReadOnlySpan<byte> header)
    {
        var context = LegacyKeyStore.RequireGameDb(game);
        int headerSize = context.HeaderSize;
        if (length < headerSize + Slot || (length - headerSize) % Slot != 0 || header.Length < headerSize)
            return false;

        long payload = (length - headerSize) / Slot * Chunk;
        if (payload > int.MaxValue) return false;

        uint padding = 0;
        if (context.Container == LegacyContainer.Horizon)
        {
            padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            if (padding >= Chunk || padding >= payload) return false;
        }

        Span<byte> expected = stackalloc byte[TfitMac.Size];
        ComputeHeaderMac(context, (uint)payload, header[..16], padding, expected);
        return CryptographicOperations.FixedTimeEquals(expected, header.Slice(headerSize - TfitMac.Size, TfitMac.Size));
    }

    public static byte[] Decrypt(byte[] encrypted, ForzaGame game)
    {
        var context = LegacyKeyStore.RequireGameDb(game);
        string label = GameDbIdentity.DisplayName(game);
        var (raw, sqliteLength) = DecryptRawAndMeasure(encrypted, game, context, label);
        context.Scramble!.Apply(0, raw.AsSpan(0, sqliteLength));
        return raw.AsSpan(0, sqliteLength).ToArray();
    }

    public static byte[] Encrypt(byte[] sqlite, byte[] template, ForzaGame game)
    {
        var context = LegacyKeyStore.RequireGameDb(game);
        string label = GameDbIdentity.DisplayName(game);
        if (SqliteLength(sqlite, sqlite.Length, label) != sqlite.Length)
            throw new InvalidDataException($"{label} SQLite length does not match its page count. Checkpoint the DB before exporting.");

        // Authenticate the complete template (header + all chunk MACs + SQLite header), not only its framing.
        var (oldRaw, oldSqliteLength) = DecryptRawAndMeasure(template, game, context, label);

        int length = sqlite.Length == oldSqliteLength
            ? oldRaw.Length
            : checked((sqlite.Length + Chunk - 1) / Chunk * Chunk);

        byte[] raw = new byte[length];
        int tailStart = Math.Max(oldSqliteLength, sqlite.Length);
        if (tailStart < Math.Min(oldRaw.Length, length))
            oldRaw.AsSpan(tailStart, Math.Min(oldRaw.Length, length) - tailStart).CopyTo(raw.AsSpan(tailStart));

        sqlite.CopyTo(raw, 0);
        context.Scramble!.Apply(0, raw.AsSpan(0, sqlite.Length));

        int headerSize = context.HeaderSize;
        int count = length / Chunk;
        uint padding = context.Container == LegacyContainer.Horizon
            ? checked((uint)(length - sqlite.Length))
            : 0u;

        byte[] result = new byte[checked(headerSize + count * Slot)];
        template.AsSpan(0, CipherBlock).CopyTo(result);
        if (context.Container == LegacyContainer.Horizon)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), padding);
        ComputeHeaderMac(context, checked((uint)length), result.AsSpan(0, CipherBlock), padding,
            result.AsSpan(headerSize - TfitMac.Size, TfitMac.Size));

        byte[][] tags = new byte[count][];
        Parallel.For(0, count, Workers, n =>
        {
            byte[] tag = new byte[TfitMac.Size];
            TfitMac.Compute(context.Tables.Mac, context.MacKeys, raw.AsSpan(n * Chunk, Chunk), tag);
            tags[n] = tag;
        });

        var encryptor = context.Tables.Encryptor;
        Span<byte> previous = stackalloc byte[CipherBlock];
        Span<byte> mixed = stackalloc byte[CipherBlock];
        result.AsSpan(0, CipherBlock).CopyTo(previous);

        for (int n = 0; n < count; n++)
        {
            int output = headerSize + n * Slot;
            ReadOnlySpan<byte> chunk = raw.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += CipherBlock)
            {
                for (int b = 0; b < CipherBlock; b++) mixed[b] = (byte)(chunk[i + b] ^ previous[b]);
                Span<byte> dest = result.AsSpan(output + i, CipherBlock);
                encryptor.ProcessBlock(context.DecryptionKeys, mixed, dest);
                dest.CopyTo(previous);
            }
            ReadOnlySpan<byte> tag = tags[n];
            for (int b = 0; b < CipherBlock; b++) mixed[b] = (byte)(tag[b] ^ previous[b]);
            Span<byte> tagDest = result.AsSpan(output + Chunk, CipherBlock);
            encryptor.ProcessBlock(context.DecryptionKeys, mixed, tagDest);
            tagDest.CopyTo(previous);
        }

        if (!Decrypt(result, game).AsSpan().SequenceEqual(sqlite))
            throw new InvalidDataException($"Authenticated {label} output did not reproduce the edited database.");
        return result;
    }

    private static (byte[] Raw, int SqliteLength) DecryptRawAndMeasure(
        byte[] encrypted, ForzaGame game, LegacyContext context, string label)
    {
        if (!HasHeaderAuthentication(game, encrypted.Length, encrypted))
            throw new InvalidDataException($"{label} GameDB header authentication failed. This build/key is unsupported or the file is damaged.");

        int headerSize = context.HeaderSize;
        int count = (encrypted.Length - headerSize) / Slot;
        int length = checked(count * Chunk);
        byte[] raw = new byte[length];
        var decryptor = context.Tables.Decryption;
        var macCipher = context.Tables.Mac;

        try
        {
            Parallel.For(0, count, Workers, n =>
            {
                int input = headerSize + n * Slot;
                Span<byte> previous = stackalloc byte[CipherBlock];
                Span<byte> clear = stackalloc byte[CipherBlock];
                encrypted.AsSpan(n == 0 ? 0 : input - CipherBlock, CipherBlock).CopyTo(previous);

                Span<byte> output = raw.AsSpan(n * Chunk, Chunk);
                for (int i = 0; i < Chunk; i += CipherBlock)
                {
                    ReadOnlySpan<byte> cipherBlock = encrypted.AsSpan(input + i, CipherBlock);
                    decryptor.ProcessBlock(context.DecryptionKeys, cipherBlock, clear);
                    for (int b = 0; b < CipherBlock; b++) output[i + b] = (byte)(clear[b] ^ previous[b]);
                    cipherBlock.CopyTo(previous);
                }

                Span<byte> storedTag = stackalloc byte[TfitMac.Size];
                Span<byte> actualTag = stackalloc byte[TfitMac.Size];
                ReadOnlySpan<byte> tagCipher = encrypted.AsSpan(input + Chunk, CipherBlock);
                decryptor.ProcessBlock(context.DecryptionKeys, tagCipher, clear);
                for (int b = 0; b < CipherBlock; b++) storedTag[b] = (byte)(clear[b] ^ previous[b]);

                TfitMac.Compute(macCipher, context.MacKeys, output, actualTag);
                if (!CryptographicOperations.FixedTimeEquals(storedTag, actualTag))
                    throw new InvalidDataException($"{label} GameDB authentication failed in chunk {n + 1}. No output was written.");
            });
        }
        catch (AggregateException error)
        {
            ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        if (length < 100)
            throw new InvalidDataException($"{label} payload is too short to hold a SQLite header.");

        Span<byte> headerProbe = stackalloc byte[100];
        raw.AsSpan(0, 100).CopyTo(headerProbe);
        context.Scramble!.Apply(0, headerProbe);
        int sqliteLength = SqliteLength(headerProbe, length, label);

        if (context.Container == LegacyContainer.Horizon)
        {
            uint padding = BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(16));
            if (sqliteLength != length - (int)padding)
                throw new InvalidDataException($"{label} SQLite page count and authenticated container padding differ.");
        }

        return (raw, sqliteLength);
    }

    private static void ComputeHeaderMac(
        LegacyContext context, uint dataBytes, ReadOnlySpan<byte> iv, uint padding, Span<byte> destination)
    {
        Span<byte> signed = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(signed, dataBytes);
        iv[..CipherBlock].CopyTo(signed[4..]);
        int signedLength = 20;
        if (context.Container == LegacyContainer.Horizon)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(signed[20..], padding);
            signedLength = 24;
        }
        TfitMac.Compute(context.Tables.Mac, context.MacKeys, signed[..signedLength], destination);
    }

    private static int SqliteLength(ReadOnlySpan<byte> header, int totalLength, string label)
    {
        if (header.Length < 100 || !header[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException($"{label} payload is not SQLite.");
        int page = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        if (page == 1) page = 65536;
        uint pages = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        long size = (long)page * pages;
        if (page < 512 || (page & (page - 1)) != 0 || pages == 0 || size > totalLength)
            throw new InvalidDataException($"Invalid {label} SQLite page size/count.");
        return checked((int)size);
    }
}
