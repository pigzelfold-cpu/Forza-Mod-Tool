using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>Authenticated FH3 profile decryption, re-encryption, and stream disambiguation.</summary>
public static class Fh3ProfileHelper
{
    public const uint AustraliaNavSignature = 0x34024176u;
    public const uint MountainNavSignature = 0x4026A4BBu;
    public const uint OpusIslandNavSignature = 0xB46177AAu;

    public static bool IsEncryptedProfile(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 36 &&
        LegacyAssetContainer.Identify(bytes[..36], bytes.Length) is
        {
            Context: { Game: LegacyGame.FH3, KeyType: LegacyKeyType.Profile }
        };

    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] plain = LegacyAssetContainer.Decrypt(encrypted, out var info, trimMotorsportTextPadding: false);
        if (info.Context.Game != LegacyGame.FH3 || info.Context.KeyType != LegacyKeyType.Profile)
            throw new InvalidDataException($"Expected an FH3 Profile save, but authenticated {info.Context.Label}.");
        return plain;
    }

    public static byte[] Encrypt(byte[] stream, byte[] template)
    {
        byte[] encrypted = LegacyAssetContainer.Encrypt(stream, template, out var context, trimMotorsportTextPadding: false);
        if (context.Game != LegacyGame.FH3 || context.KeyType != LegacyKeyType.Profile)
            throw new InvalidDataException($"Expected an FH3 Profile template, but authenticated {context.Label}.");
        byte[] verified = Decrypt(encrypted);
        if (!verified.AsSpan().SequenceEqual(stream))
            throw new CryptographicException("Re-encrypted FH3 profile failed round-trip verification; no output was written.");
        return encrypted;
    }

    public static bool IsDecryptedFh3Profile(ProfileContainer? container)
    {
        if (container is null) return false;
        if (container.Career?.Records.Any(r =>
                r.Name == "RoadDiscovery" &&
                r.Payload.Length == 4104 &&
                BinaryPrimitives.ReadUInt32LittleEndian(r.Payload.AsSpan(4, 4)) == AustraliaNavSignature) == true)
            return true;

        if (container.SaveState is { } bx &&
            bx.Root.NameIndex >= 0 && bx.Root.NameIndex < bx.Strings.Count &&
            bx.Strings[bx.Root.NameIndex] == "ObjectDictionary" &&
            (Fh4ProfileHelper.FindObject(bx, "skills_save_state") != null ||
             Fh4ProfileHelper.FindObject(bx, "CurrencyBucket_Fans_offline_currency") != null))
            return true;

        return false;
    }
}
