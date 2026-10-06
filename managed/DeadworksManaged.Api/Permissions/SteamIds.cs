using System.Globalization;
using System.Text.RegularExpressions;

namespace DeadworksManaged.Api;

/// <summary>Helpers for parsing and formatting Steam2, Steam3, and SteamID64 identifiers.</summary>
public static partial class SteamIds
{
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>
    /// Attempts to parse Steam2, bracketed Steam3, or SteamID64 text into a SteamID64. Only IDs of individual Steam
    /// accounts parse, so a mistyped leading digit is refused rather than read as a different account.
    /// </summary>
    public static bool TryParse(string value, out ulong steamId64)
    {
        if (TryParseAny(value, out steamId64) && IsIndividual(steamId64))
            return true;
        steamId64 = 0;
        return false;
    }

    // Individual accounts are the base plus a 32-bit account number other than 0.
    private static bool IsIndividual(ulong steamId64) => steamId64 > SteamId64Base && steamId64 - SteamId64Base <= uint.MaxValue;

    private static bool TryParseAny(string value, out ulong steamId64)
    {
        steamId64 = 0;
        value = value.Trim();

        if (value.Length == 17 && value.All(char.IsDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steamId64))
            return true;

        var steam2 = Steam2Regex().Match(value);
        if (steam2.Success)
        {
            var authServer = ulong.Parse(steam2.Groups["auth"].Value, CultureInfo.InvariantCulture);
            if (!ulong.TryParse(steam2.Groups["account"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var accountNumber) || accountNumber > uint.MaxValue)
                return false;
            steamId64 = SteamId64Base + accountNumber * 2UL + authServer;
            return true;
        }

        var steam3 = Steam3Regex().Match(value);
        if (steam3.Success)
        {
            if (!ulong.TryParse(steam3.Groups["account"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId))
                return false;
            steamId64 = SteamId64Base + accountId;
            return true;
        }

        return false;
    }

    /// <summary>Formats a SteamID64 as the canonical permission-file key.</summary>
    public static string ToPermissionKey(ulong steamId64) => steamId64.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a SteamID64 as a Steam2 ID.</summary>
    public static string ToSteam2(ulong steamId64)
    {
        var accountId = steamId64 - SteamId64Base;
        return string.Create(CultureInfo.InvariantCulture, $"STEAM_0:{accountId % 2UL}:{accountId / 2UL}");
    }

    /// <summary>Formats a SteamID64 as a bracketed Steam3 ID.</summary>
    public static string ToSteam3(ulong steamId64)
    {
        var accountId = steamId64 - SteamId64Base;
        return string.Create(CultureInfo.InvariantCulture, $"[U:1:{accountId}]");
    }

    [GeneratedRegex(@"^STEAM_[0-5]:(?<auth>[0-1]):(?<account>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Steam2Regex();

    [GeneratedRegex(@"^\[U:1:(?<account>\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Steam3Regex();
}
