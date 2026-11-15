using System;
using System.Collections.Generic;
using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>
/// Bidirectional path codec and de-obfuscator for Forza Horizon 3 PC asset names.
/// Turn 10 obfuscated files on disk in FH3 PC using a 1-to-1 character substitution cipher.
/// </summary>
public static class Fh3NameCodec
{
    static readonly Dictionary<char, char> EncToDec = new()
    {
        ['E'] = 'D', ['N'] = 'E', ['C'] = 'C', ['O'] = 'O', ['D'] = 'D', [' '] = ' ', ['A'] = 'A', ['M'] = 'M',
        ['g'] = 'm', ['q'] = 'e', ['6'] = 'd', ['$'] = 'i', ['l'] = 'a', ['1'] = 't', ['h'] = 'o', ['v'] = 'f',
        ['!'] = 'y', ['4'] = 'r', ['^'] = 'c', ['s'] = 'k', ['~'] = 's', ['8'] = 'n', ['u'] = 'p', ['\''] = 'v',
        ['`'] = 'b', ['='] = 'u', ['b'] = 'l', ['@'] = 'h', ['m'] = 'w', [']'] = 'x', ['y'] = '_', ['{'] = 'g',
        ['.'] = '.', [','] = 'z', ['('] = '4', ['j'] = '6', ['['] = '0', ['w'] = '2', ['7'] = 'j', ['0'] = '1',
        ['k'] = '3', ['+'] = '9', ['2'] = '5', [';'] = '8', ['}'] = '7', ['_'] = '-', ['f'] = 'q', ['i'] = 'i'
    };

    static readonly Dictionary<char, char> DecToEnc = new()
    {
        ['D'] = 'E', ['E'] = 'N', ['C'] = 'C', ['O'] = 'O', [' '] = ' ', ['A'] = 'A', ['M'] = 'M',
        ['m'] = 'g', ['e'] = 'q', ['d'] = '6', ['i'] = '$', ['a'] = 'l', ['t'] = '1', ['o'] = 'h', ['f'] = 'v',
        ['y'] = '!', ['r'] = '4', ['c'] = '^', ['k'] = 's', ['s'] = '~', ['n'] = '8', ['p'] = 'u', ['v'] = '\'',
        ['b'] = '`', ['u'] = '=', ['l'] = 'b', ['h'] = '@', ['w'] = 'm', ['x'] = ']', ['_'] = 'y', ['g'] = '{',
        ['.'] = '.', ['z'] = ',', ['4'] = '(', ['6'] = 'j', ['0'] = '[', ['2'] = 'w', ['j'] = '7', ['1'] = '0',
        ['3'] = 'k', ['9'] = '+', ['5'] = '2', ['8'] = ';', ['7'] = '}', ['-'] = '_', ['q'] = 'f'
    };

    public static string Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return encoded;
        char[] buf = new char[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            buf[i] = EncToDec.TryGetValue(c, out char d) ? d :
                (char.IsLetter(c) && EncToDec.TryGetValue(char.ToLowerInvariant(c), out d) ? d : c);
        }
        return new string(buf);
    }

    public static string Encode(string decoded)
    {
        if (string.IsNullOrEmpty(decoded)) return decoded;
        char[] buf = new char[decoded.Length];
        for (int i = 0; i < decoded.Length; i++)
        {
            char c = decoded[i];
            buf[i] = DecToEnc.TryGetValue(c, out char e) ? e :
                (char.IsLetter(c) && DecToEnc.TryGetValue(char.ToLowerInvariant(c), out e) ? e : c);
        }
        return new string(buf);
    }

    public static bool IsEncoded(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string lower = path.ToLowerInvariant();
        return lower.EndsWith(".,$u") || lower.EndsWith(".~14") || lower.EndsWith(".~b1") ||
               lower.EndsWith(".]gb") || lower.EndsWith(".~ml1^@`$8") ||
               lower.Contains("gq6$l") || lower.Contains("~14$uuq6") || lower.Contains("{lgq6`4^") ||
               lower.Contains("4^[") || lower.Contains("~14$8{1l`bq~") ||
               lower.Contains("1@=g`8l$b") || lower.Contains(".~ml1^@`$8") ||
               lower.Contains('~') || lower.Contains('`') || lower.Contains('^') || lower.Contains('{') || lower.Contains('}');
    }

    /// <summary>
    /// Resolves a relative or base path to the actual path on disk, trying both decoded and encoded variants.
    /// </summary>
    public static string ResolveExistingPath(string basePath, string relativePath)
    {
        // 1. Direct path
        string direct = Path.Combine(basePath, relativePath);
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;

        // 2. Encoded path
        string encoded = Encode(relativePath);
        string encodedPath = Path.Combine(basePath, encoded);
        if (File.Exists(encodedPath) || Directory.Exists(encodedPath)) return encodedPath;

        // 3. Normalized separators
        string encNorm = encoded.Replace('/', '\\');
        string encPathNorm = Path.Combine(basePath, encNorm);
        if (File.Exists(encPathNorm) || Directory.Exists(encPathNorm)) return encPathNorm;

        return direct;
    }

    /// <summary>
    /// Returns a human-friendly decoded path for display in UI, headers, and logs.
    /// </summary>
    public static string DisplayPath(string physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath)) return physicalPath;
        if (!IsEncoded(physicalPath)) return physicalPath;

        // Keep root/drive untouched, decode subdirectories and filename
        string root = Path.GetPathRoot(physicalPath) ?? "";
        string rel = physicalPath.Substring(root.Length);
        string decodedRel = Decode(rel);
        return Path.Combine(root, decodedRel);
    }
}
