using System.Diagnostics;
using DeadworksManaged.Api;

namespace DeadworksManaged;

/// <summary>
/// Backs <c>dw_host_status</c>: gathers the server, player and plugin state the launcher polls over RCON
/// and prints it through <see cref="HostStatusFormatter"/>.
/// </summary>
internal static class HostStatus
{
    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // When each slot's current client first went in game, keyed by SteamID so a map change (which re-runs
    // full connect for everyone) keeps the time. 0 = no client.
    private static readonly long[] _inGameSince = new long[Players.MaxSlot];
    private static readonly ulong[] _inGameSteamId = new ulong[Players.MaxSlot];

    public static void OnClientFullConnect(int slot)
    {
        if ((uint)slot >= Players.MaxSlot)
            return;

        var steamId = Players.FromSlot(slot)?.PlayerSteamId ?? 0;
        if (_inGameSince[slot] != 0 && _inGameSteamId[slot] == steamId)
            return;

        _inGameSince[slot] = Stopwatch.GetTimestamp();
        _inGameSteamId[slot] = steamId;
    }

    public static void OnClientDisconnect(int slot)
    {
        if ((uint)slot < Players.MaxSlot)
            _inGameSince[slot] = 0;
    }

    public static void OnCommand(ConCommandContext ctx)
    {
        var fromSlot = 0;
        if (ctx.Args.Length > 1 && !int.TryParse(ctx.Args[1], out fromSlot))
        {
            Console.WriteLine("Usage: dw_host_status [fromSlot]");
            return;
        }

        Console.WriteLine(HostStatusFormatter.Format(Collect(), fromSlot));
    }

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

            players.Add(new HostStatusPlayer(
                slot, controller.PlayerSteamId, controller.PlayerName, controller.TeamNum, hero, controller.IsBot, connected));
        }

        return new HostStatusSnapshot(
            Server.MapName,
            (long)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds,
            GlobalVars.IsValid ? GlobalVars.MaxClients : null,
            Deadworks.Version,
            players,
            CollectPlugins());
    }

    private static List<HostStatusPlugin> CollectPlugins()
    {
        // A DLL deleted while loaded stays loaded (the watcher ignores deletes), so list loaded ones too.
        var onDisk = Directory.Exists(PluginLoader.PluginsDir)
            ? Directory.GetFiles(PluginLoader.PluginsDir, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            : [];
        var loaded = PluginLoader.GetLoadedAssemblies().ToDictionary(a => a.DllName, a => a.PluginCount, StringComparer.OrdinalIgnoreCase);
        var onDiskSet = new HashSet<string>(onDisk, StringComparer.OrdinalIgnoreCase);

        return onDiskSet.Union(loaded.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new HostStatusPlugin(
                name,
                PluginStateManager.IsEnabled(name),
                loaded.ContainsKey(name),
                onDiskSet.Contains(name),
                loaded.TryGetValue(name, out var count) ? count : null))
            .ToList();
    }
}
