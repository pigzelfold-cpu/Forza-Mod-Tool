using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>Authenticated FH4 profile decryption, re-encryption, stream disambiguation,
/// and ObjectDictionary BXML helpers.</summary>
public static class Fh4ProfileHelper
{
    public const uint UkNavSignature = 0x0D65B617u;

    public static bool IsEncryptedProfile(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 36 &&
        LegacyAssetContainer.Identify(bytes[..36], bytes.Length) is
        {
            Context: { Game: LegacyGame.FH4, KeyType: LegacyKeyType.Profile }
        };

    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] plain = LegacyAssetContainer.Decrypt(encrypted, out var info, trimMotorsportTextPadding: false);
        if (info.Context.Game != LegacyGame.FH4 || info.Context.KeyType != LegacyKeyType.Profile)
            throw new InvalidDataException($"Expected an FH4 Profile save, but authenticated {info.Context.Label}.");
        return plain;
    }

    public static byte[] Encrypt(byte[] stream, byte[] template)
    {
        byte[] encrypted = LegacyAssetContainer.Encrypt(stream, template, out var context, trimMotorsportTextPadding: false);
        if (context.Game != LegacyGame.FH4 || context.KeyType != LegacyKeyType.Profile)
            throw new InvalidDataException($"Expected an FH4 Profile template, but authenticated {context.Label}.");
        byte[] verified = Decrypt(encrypted);
        if (!verified.AsSpan().SequenceEqual(stream))
            throw new CryptographicException("Re-encrypted FH4 profile failed round-trip verification; no output was written.");
        return encrypted;
    }

    public static bool IsDecryptedFh4Profile(ProfileContainer? container)
    {
        if (container is null || Fh3ProfileHelper.IsDecryptedFh3Profile(container)) return false;
        if (container.SaveState is { } bx &&
            bx.Root.NameIndex >= 0 && bx.Root.NameIndex < bx.Strings.Count &&
            bx.Strings[bx.Root.NameIndex] == "ObjectDictionary")
            return true;

        if (container.Career?.Records.Any(r =>
                r.Name == "RoadDiscovery" &&
                r.Payload.Length == 4104 &&
                BinaryPrimitives.ReadUInt32LittleEndian(r.Payload.AsSpan(4, 4)) == UkNavSignature) == true)
            return true;

        return false;
    }

    internal static Bxml.Node? FindObject(Bxml? bx, string id)
    {
        if (bx is null) return null;
        foreach (var child in bx.Root.Children)
        {
            if (bx.Strings[child.NameIndex] == "Object" &&
                string.Equals(bx.GetAttribute(child, "Id"), id, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    internal static IEnumerable<Bxml.Node> FindObjectsByType(Bxml? bx, string typeName)
    {
        if (bx is null) yield break;
        foreach (var child in bx.Root.Children)
        {
            if (bx.Strings[child.NameIndex] == "Object" &&
                string.Equals(bx.GetAttribute(child, "Type"), typeName, StringComparison.Ordinal))
                yield return child;
        }
    }

    internal static Bxml.Node? GetPropertyNode(Bxml? bx, Bxml.Node? parent, string propName)
    {
        if (bx is null || parent is null) return null;
        foreach (var child in parent.Children)
        {
            if (bx.Strings[child.NameIndex] == "Property" &&
                string.Equals(bx.GetAttribute(child, "Name"), propName, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    internal static string? GetPropertyValue(Bxml? bx, Bxml.Node? parent, string propName)
    {
        var prop = GetPropertyNode(bx, parent, propName);
        return bx is null || prop is null ? null : bx.GetAttribute(prop, "Value");
    }
}
