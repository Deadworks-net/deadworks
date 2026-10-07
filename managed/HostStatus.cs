using System.Diagnostics;
using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged;

/// <summary>
/// Backs <c>dw_host_status</c>: gathers the server, player, role and plugin state the launcher polls
/// and formats it with <see cref="HostStatusFormatter"/>.
/// </summary>
internal static class HostStatus
{
    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // When each slot's current client first went in game. 0 = no client.
    private static readonly long[] _inGameSince = new long[Players.MaxSlot];

    public static void OnClientFullConnect(int slot)
    {
        if ((uint)slot >= Players.MaxSlot)
            return;

        // A map change runs full connect again for everyone staying; they keep their time.
        if (_inGameSince[slot] != 0 && Players.IsMapChangeReconnect(slot))
            return;

        _inGameSince[slot] = Stopwatch.GetTimestamp();
    }

    public static void OnClientDisconnect(int slot)
    {
        if ((uint)slot < Players.MaxSlot)
            _inGameSince[slot] = 0;
    }

    /// <summary>The <c>DWHOST {json}</c> line for the players in slot <paramref name="fromSlot"/> and up.</summary>
    public static string Line(int fromSlot) => HostStatusFormatter.Format(Collect(), fromSlot);

    private static HostStatusSnapshot Collect()
    {
        var players = new List<HostStatusPlayer>();
        for (int slot = 0; slot < Players.MaxSlot; slot++)
        {
            if (!Players.IsConnected(slot))
                continue;
            var controller = Players.FromSlot(slot);
            if (controller == null)
                continue;

            var heroId = controller.PlayerDataGlobal.HeroID;
            var hero = heroId > 0 && Enum.IsDefined((Heroes)heroId) ? ((Heroes)heroId).ToHeroName() : null;
            long? connected = _inGameSince[slot] != 0
                ? (long)Stopwatch.GetElapsedTime(_inGameSince[slot]).TotalSeconds
                : null;
            // The SteamID they connected with, not the controller's: it's the one roles and penalties go by, and
            // plugins can't change it. 0 for a bot.
            var steamId = PermissionManager.GetSlotSteamId(slot);
            IReadOnlyList<string> roles = steamId != 0 ? Permissions.GetRoles(steamId) : [];

            players.Add(new HostStatusPlayer(
                slot, steamId, controller.PlayerName, controller.TeamNum, hero, controller.IsBot, connected, roles));
        }

        return new HostStatusSnapshot(
            Server.MapName,
            (long)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds,
            GlobalVars.IsValid ? GlobalVars.MaxClients : null,
            Deadworks.Version,
            PermissionManager.Roles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            // Whichever plugin provides them: the Admin plugin that ships with Deadworks, or a server's replacement.
            ConCommandManager.IsRegistered("dw_kick") && ConCommandManager.IsRegistered("dw_ban"),
            players,
            CollectPlugins());
    }

    private static List<HostStatusPlugin> CollectPlugins()
    {
        // A DLL deleted while loaded stays loaded (the watcher ignores deletes), so list loaded ones too. The same
        // name can then also get loaded from builtin/, hence the grouping.
        var installed = new HashSet<string>(PluginLoader.InstalledPluginNames(), StringComparer.OrdinalIgnoreCase);
        var loaded = PluginLoader.GetLoadedAssemblies()
            .GroupBy(a => a.DllName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.PluginCount), StringComparer.OrdinalIgnoreCase);

        return installed.Union(loaded.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new HostStatusPlugin(
                name,
                PluginStateManager.IsEnabled(name),
                loaded.ContainsKey(name),
                installed.Contains(name),
                PluginLoader.IsBuiltin(name),
                loaded.TryGetValue(name, out var count) ? count : null))
            .ToList();
    }
}
