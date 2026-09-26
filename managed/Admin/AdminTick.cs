using System.Diagnostics;
using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.AdminSystem;

/// <summary>Once a second: notices players Steam has just validated, and drops penalties that have run out.</summary>
internal static class AdminTick
{
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static long _nextMs;

    public static void OnGameFrame()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now < _nextMs)
            return;
        _nextMs = now + 1000;

        foreach (var (slot, steamId64) in PermissionManager.TakeNewlyAuthorized())
        {
            // The SteamID a player connects with isn't checked until now, so bans get a second look.
            PenaltyManager.EnforceBan(slot);
            Players.RaiseClientAuthorized(slot, steamId64);
        }

        PenaltyManager.Sweep();
    }
}
