using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>PlayerXPLevels-backed preview and synchronized earned-XP totals; no level-up reward grants.</summary>
public static class DriverXpState
{
    // Installed PlayerXPLevels (2026-10-14), grouped contiguous segments by
    // PrestigeLevel. All prestige-0 segments cost 15,000 XP/level; later
    // segments cost 30,000. The final prestige's final level is infinite.
    static readonly uint[] Levels = { 199, 299, 399, 499, 599, 749, 849, 899, 999, 1499, 2999 };
    static readonly (string Colour, uint NumLevels, uint BaseXp, uint IncreasePerLevel, bool InfiniteLast)[] Fh3Tiers =
    {
        ("White", 14u, 20_000u, 0u, false),
        ("Yellow", 15u, 20_000u, 0u, false),
        ("Orange", 15u, 20_000u, 0u, false),
        ("Green", 15u, 20_000u, 0u, false),
        ("Turquoise", 15u, 20_000u, 0u, false),
        ("Blue", 15u, 20_000u, 0u, false),
        ("Purple", 20u, 20_000u, 0u, false),
        ("Red", 20u, 20_000u, 0u, false),
        ("Pink", 20u, 20_000u, 0u, false),
        ("Bronze", 50u, 20_000u, 0u, false),
        ("Silver", 100u, 20_000u, 0u, false),
        ("Gold", 700u, 20_000u, 300u, false),
        ("Platinum", 2u, 200_000u, 0u, true),
    };

    public sealed record Level(uint Xp, uint Number, int Prestige, uint XpIntoLevel, uint? XpToNextLevel, string? Wristband = null, uint TierLevelIndex = 0)
    {
        public string Display => string.IsNullOrEmpty(Wristband)
            ? $"Level {Number:n0} · Prestige {Prestige}"
            : $"Level {Number:n0} · {Wristband} Wristband";
    }

    public static Level Calculate(uint xp)
    {
        ulong remaining = xp;
        for (int prestige = 0; prestige < Levels.Length; prestige++)
        {
            uint rate = prestige == 0 ? 15_000U : 30_000U;
            uint count = Levels[prestige]; bool last = prestige == Levels.Length - 1;
            if (last && remaining >= (ulong)(count - 1) * rate)
                return new(xp, count, prestige, checked((uint)(remaining - (ulong)(count - 1) * rate)), null);
            ulong span = (ulong)count * rate;
            if (remaining < span)
                return new(xp, checked(1 + (uint)(remaining / rate)), prestige, (uint)(remaining % rate), rate - (uint)(remaining % rate));
            remaining -= span;
        }
        throw new InvalidOperationException("XP curve does not have a terminal level.");
    }

    public static Level CalculateFh3(uint xp)
    {
        ulong remaining = xp;
        uint level = 1;
        for (int tierIdx = 0; tierIdx < Fh3Tiers.Length; tierIdx++)
        {
            var (colour, numLevels, baseXp, incXp, infLast) = Fh3Tiers[tierIdx];
            for (uint i = 0; i < numLevels; i++)
            {
                bool isInf = infLast && i == numLevels - 1;
                ulong req = (ulong)baseXp + (ulong)i * incXp;
                if (isInf)
                    return new(xp, level, tierIdx, checked((uint)remaining), null, colour, i);
                if (remaining < req)
                    return new(xp, level, tierIdx, (uint)remaining, (uint)(req - remaining), colour, i);
                remaining -= req;
                level++;
            }
        }
        throw new InvalidOperationException("FH3 Wristband XP curve does not have a terminal level.");
    }

    public static bool TryRead(ProfileContainer? container, out Level? level)
    {
        level = null;
        if (!TryField(container, out var field) || !uint.TryParse(field!.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint xp)) return false;
        level = Fh3ProfileHelper.IsDecryptedFh3Profile(container) ? CalculateFh3(xp) : Calculate(xp);
        return true;
    }

    public static bool TrySet(ProfileContainer? container, uint xp, out bool changed)
        => TrySet(container, xp, out changed, out _);

    public static bool TryPreview(ProfileContainer? container, uint xp, out Level? level, out string reason)
    {
        level = null;
        if (!ProfileXpEdit.TryDriver(container, xp, out _, out reason)) return false;
        level = Fh3ProfileHelper.IsDecryptedFh3Profile(container) ? CalculateFh3(xp) : Calculate(xp);
        return true;
    }

    public static bool TrySet(ProfileContainer? container, uint xp, out bool changed, out string reason)
    {
        changed = false;
        // Do not short-circuit when Main/XP already matches: re-applying repairs
        // the ledger/counters left inconsistent by older versions of the editor.
        if (!ProfileXpEdit.TryDriver(container, xp, out var edit, out reason)) return false;
        return edit!.TryCommit(container!, out changed, out reason);
    }

    static bool TryField(ProfileContainer? container, out PropertyTree.PropertyNode? field)
    {
        field = null;
        var nodes = container?.Properties?.Walk().Where(p => p.Path == "/Main/XP").Select(p => p.Node).ToArray();
        if (nodes is not { Length: 1 } || nodes[0].TypeId is not (3 or 17) || nodes[0].RawValue.Length != 4 || !nodes[0].IsEditable) return false;
        field = nodes[0]; return true;
    }
}
