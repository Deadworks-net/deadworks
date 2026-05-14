using System.Text.Json;
using DeadworksManaged.Api;

namespace AdminPlugin;

public sealed class AdminBan
{
    public string SteamId64 { get; set; } = "";
    public string Reason { get; set; } = "Banned by admin";
    public string AdminSteamId64 { get; set; } = "server";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

public sealed class AdminPluginConfig : IConfig
{
    public List<AdminBan> Bans { get; set; } = [];
    public List<AdminChatMute> ChatMutes { get; set; } = [];

    public void Validate()
    {
        Bans.RemoveAll(b => !SteamIds.TryParse(b.SteamId64, out _));
        ChatMutes.RemoveAll(m => !SteamIds.TryParse(m.SteamId64, out _));
    }
}

public sealed class AdminChatMute
{
    public string SteamId64 { get; set; } = "";
    public string Reason { get; set; } = "Chat muted by admin";
    public string AdminSteamId64 { get; set; } = "server";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

public sealed class AdminPlugin : DeadworksPluginBase
{
    private const string ChatMuteSource = "Admin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public override string Name => "Admin";

    [PluginConfig]
    public AdminPluginConfig Config { get; set; } = new();

    public override void OnLoad(bool isReload)
    {
        PruneExpiredBans(save: true);
        PruneExpiredChatMutes(save: true);
        SyncChatMutes();
        Console.WriteLine("[Admin] Loaded");
    }

    public override void OnUnload()
    {
        ChatMutes.Clear(ChatMuteSource);
        Console.WriteLine("[Admin] Unloaded");
    }

    public override bool OnClientConnect(ClientConnectEvent args)
    {
        PruneExpiredBans(save: true);

        var ban = FindActiveBan(args.SteamId);
        if (ban == null)
            return true;

        Console.WriteLine($"[Admin] Rejected banned player {args.Name} ({args.SteamId}): {ban.Reason}");
        return false;
    }

    [Command("kick", Description = "Kick a connected player from the server", Permission = "admin.kick")]
    public void Kick(CCitadelPlayerController? caller, string target, params string[] reasonParts)
    {
        var player = ResolveTarget(caller, target);
        var reason = BuildReason(reasonParts, "Kicked by admin");

        if (!player.Kick(reason))
            throw new CommandException($"Failed to kick {Describe(player)}.");

        Reply(caller, $"Kicked {Describe(player)}: {reason}");
    }

    [Command("ban", Description = "Ban a player by target or SteamID64", Permission = "admin.ban")]
    public void Ban(CCitadelPlayerController? caller, string target, int minutes, params string[] reasonParts)
    {
        if (minutes < 0)
            throw new CommandException("Ban duration must be 0 or greater.");

        var reason = BuildReason(reasonParts, "Banned by admin");

        if (SteamIds.TryParse(target, out var parsedSteamId))
        {
            AddOrReplaceBan(parsedSteamId, minutes, reason, caller);
            var online = FindBySteamId(parsedSteamId);
            online?.Kick(reason);

            Reply(caller, $"Banned {parsedSteamId} for {FormatDuration(minutes)}: {reason}");
            return;
        }

        var player = ResolveTarget(caller, target);
        AddOrReplaceBan(player.PlayerSteamId, minutes, reason, caller);
        player.Kick(reason);

        Reply(caller, $"Banned {Describe(player)} for {FormatDuration(minutes)}: {reason}");
    }

    [Command("unban", Description = "Remove a SteamID64 from the ban list", Permission = "admin.unban")]
    public void Unban(CCitadelPlayerController? caller, string steamId64)
    {
        if (!SteamIds.TryParse(steamId64, out var parsedSteamId))
            throw new CommandException("Steam ID must be Steam2, bracketed Steam3, or SteamID64.");

        var removed = Config.Bans.RemoveAll(b => SteamIds.TryParse(b.SteamId64, out var banSteamId) && banSteamId == parsedSteamId);
        if (removed == 0)
            throw new CommandException($"No ban found for {parsedSteamId}.");

        SaveConfig();
        Reply(caller, $"Removed ban for {parsedSteamId}.");
    }

    [Command("gag", "chatmute", Description = "Chat mute a player by target or SteamID64", Permission = "admin.chat")]
    public void Gag(CCitadelPlayerController? caller, string target, int minutes, params string[] reasonParts)
    {
        if (minutes < 0)
            throw new CommandException("Chat mute duration must be 0 or greater.");

        var reason = BuildReason(reasonParts, "Chat muted by admin");

        if (SteamIds.TryParse(target, out var parsedSteamId))
        {
            AddOrReplaceChatMute(parsedSteamId, minutes, reason, caller);
            Reply(caller, $"Chat muted {parsedSteamId} for {FormatDuration(minutes)}: {reason}");
            return;
        }

        var player = ResolveTarget(caller, target);
        AddOrReplaceChatMute(player.PlayerSteamId, minutes, reason, caller);
        Reply(caller, $"Chat muted {Describe(player)} for {FormatDuration(minutes)}: {reason}");
    }

    [Command("ungag", "unchatmute", Description = "Remove a chat mute by target or SteamID64", Permission = "admin.chat")]
    public void Ungag(CCitadelPlayerController? caller, string target)
    {
        var steamId64 = SteamIds.TryParse(target, out var parsedSteamId)
            ? parsedSteamId
            : ResolveTarget(caller, target).PlayerSteamId;

        var removed = Config.ChatMutes.RemoveAll(m => SteamIds.TryParse(m.SteamId64, out var muteSteamId) && muteSteamId == steamId64);
        if (removed == 0)
            throw new CommandException($"No chat mute found for {steamId64}.");

        ChatMutes.Remove(ChatMuteSource, steamId64);
        SaveConfig();
        Reply(caller, $"Removed chat mute for {steamId64}.");
    }

    [Command("who", Description = "List connected players and their roles", Permission = "admin.generic")]
    public void Who(CCitadelPlayerController? caller, string target = "")
    {
        var players = string.IsNullOrWhiteSpace(target)
            ? Players.GetAll().ToArray()
            : [ResolveTarget(caller, target)];

        if (players.Length == 0)
        {
            Reply(caller, "No connected players.");
            return;
        }

        foreach (var player in players.OrderBy(p => p.Slot))
        {
            var roles = Permissions.GetRoles(player.PlayerSteamId);
            var roleText = roles.Length == 0 ? "none" : string.Join(", ", roles);
            Reply(caller, $"#{player.Slot} {player.PlayerName} ({player.PlayerSteamId}) roles: {roleText}");
        }
    }

    [Command("map", Description = "Change the current map", Permission = "admin.changemap")]
    public void Map(CCitadelPlayerController? caller, string map)
    {
        EnsureSafeAtom(map, "Map name");
        Server.ExecuteCommand($"changelevel {map}");
        Reply(caller, $"Changing map to {map}.");
    }

    [Command("cvar", Description = "Get or set a server cvar", Permission = "admin.cvar")]
    public void Cvar(CCitadelPlayerController? caller, string[] rawArgs)
    {
        if (rawArgs.Length == 0)
            throw new CommandException("Usage: cvar <cvar> [value]");

        var name = rawArgs[0];
        EnsureSafeAtom(name, "Cvar name");

        if (rawArgs.Length == 1)
        {
            Server.ExecuteCommand(name);
            Reply(caller, $"Requested cvar: {name}");
            return;
        }

        var value = string.Join(" ", rawArgs.Skip(1));
        EnsureNoCommandSeparator(value, "Cvar value");
        Server.ExecuteCommand($"{name} {value}");
        Reply(caller, $"Set cvar {name}.");
    }

    [Command("execcfg", Description = "Execute a server config file", Permission = "admin.config")]
    public void ExecCfg(CCitadelPlayerController? caller, string filename)
    {
        EnsureSafeAtom(filename, "Config filename");
        Server.ExecuteCommand($"exec {filename}");
        Reply(caller, $"Executed config {filename}.");
    }

    [Command("rcon", Description = "Execute a server console command", Permission = "admin.rcon", SuppressChat = true)]
    public void Rcon(CCitadelPlayerController? caller, string[] rawArgs)
    {
        if (rawArgs.Length == 0)
            throw new CommandException("Usage: rcon <command> [args...]");

        var command = string.Join(" ", rawArgs);
        EnsureNoNewline(command, "Command");
        Server.ExecuteCommand(command);
        Reply(caller, $"Executed: {command}");
    }

    private void AddOrReplaceBan(ulong steamId64, int minutes, string reason, CCitadelPlayerController? caller)
    {
        Config.Bans.RemoveAll(b => SteamIds.TryParse(b.SteamId64, out var banSteamId) && banSteamId == steamId64);
        Config.Bans.Add(new AdminBan
        {
            SteamId64 = steamId64.ToString(),
            Reason = reason,
            AdminSteamId64 = caller?.PlayerSteamId.ToString() ?? "server",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = minutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(minutes)
        });
        SaveConfig();
    }

    private void AddOrReplaceChatMute(ulong steamId64, int minutes, string reason, CCitadelPlayerController? caller)
    {
        DateTimeOffset? expiresAtUtc = minutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(minutes);
        Config.ChatMutes.RemoveAll(m => SteamIds.TryParse(m.SteamId64, out var muteSteamId) && muteSteamId == steamId64);
        Config.ChatMutes.Add(new AdminChatMute
        {
            SteamId64 = steamId64.ToString(),
            Reason = reason,
            AdminSteamId64 = caller?.PlayerSteamId.ToString() ?? "server",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        });

        ChatMutes.SetMuted(ChatMuteSource, new ChatMuteInfo
        {
            SteamId64 = steamId64,
            Reason = reason,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        });

        SaveConfig();
    }

    private AdminBan? FindActiveBan(ulong steamId64)
    {
        var now = DateTimeOffset.UtcNow;
        return Config.Bans.FirstOrDefault(b =>
            SteamIds.TryParse(b.SteamId64, out var banSteamId) &&
            banSteamId == steamId64 &&
            (b.ExpiresAtUtc == null || b.ExpiresAtUtc > now));
    }

    private void PruneExpiredBans(bool save)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = Config.Bans.RemoveAll(b => b.ExpiresAtUtc != null && b.ExpiresAtUtc <= now);
        if (removed > 0 && save)
            SaveConfig();
    }

    private void PruneExpiredChatMutes(bool save)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = Config.ChatMutes.RemoveAll(m => m.ExpiresAtUtc != null && m.ExpiresAtUtc <= now);
        if (removed > 0 && save)
            SaveConfig();
    }

    private void SyncChatMutes()
    {
        ChatMutes.ReplaceAll(ChatMuteSource, Config.ChatMutes
            .Where(m => SteamIds.TryParse(m.SteamId64, out _))
            .Select(m =>
            {
                SteamIds.TryParse(m.SteamId64, out var steamId64);
                return new ChatMuteInfo
                {
                    SteamId64 = steamId64,
                    Reason = m.Reason,
                    CreatedAtUtc = m.CreatedAtUtc,
                    ExpiresAtUtc = m.ExpiresAtUtc
                };
            }));
    }

    private void SaveConfig()
    {
        var path = this.GetConfigPath();
        if (path == null)
        {
            Console.WriteLine("[Admin] Cannot save config: config path is unavailable");
            return;
        }

        File.WriteAllText(path, $"// Configuration for {Name}\n{JsonSerializer.Serialize(Config, JsonOptions)}\n");
    }

    private static CCitadelPlayerController ResolveTarget(CCitadelPlayerController? caller, string target)
    {
        if (string.Equals(target, "@me", StringComparison.OrdinalIgnoreCase))
        {
            if (caller == null)
                throw new CommandException("@me can only be used by a player.");

            return caller;
        }

        if (target.StartsWith('@'))
            throw new CommandException("Only single-player targets are supported: name, unique partial name, #slot, @me, or SteamID64.");

        if (target.StartsWith('#') && int.TryParse(target[1..], out var slot))
        {
            var bySlot = Players.FromSlot(slot);
            return bySlot ?? throw new CommandException($"No connected player in slot {slot}.");
        }

        if (SteamIds.TryParse(target, out var steamId))
        {
            var bySteam = FindBySteamId(steamId);
            return bySteam ?? throw new CommandException($"No connected player with SteamID64 {steamId}.");
        }

        var players = Players.GetAll().ToArray();
        var exact = players
            .Where(p => string.Equals(p.PlayerName, target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length == 1)
            return exact[0];
        if (exact.Length > 1)
            throw new CommandException($"Multiple players are named '{target}'. Use #slot instead.");

        var partial = players
            .Where(p => p.PlayerName.Contains(target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (partial.Length == 1)
            return partial[0];
        if (partial.Length > 1)
            throw new CommandException($"Target '{target}' matched multiple players. Use #slot instead.");

        throw new CommandException($"No connected player matched '{target}'.");
    }

    private static CCitadelPlayerController? FindBySteamId(ulong steamId64)
    {
        return Players.GetAll().FirstOrDefault(p => p.PlayerSteamId == steamId64);
    }

    private static string BuildReason(string[] parts, string fallback)
    {
        var reason = parts.Length == 0 ? fallback : string.Join(" ", parts).Trim();
        EnsureNoNewline(reason, "Reason");
        return reason.Length == 0 ? fallback : reason;
    }

    private static string FormatDuration(int minutes)
    {
        return minutes == 0 ? "permanent" : $"{minutes} minute{(minutes == 1 ? "" : "s")}";
    }

    private static string Describe(CCitadelPlayerController player)
    {
        return $"{player.PlayerName} ({player.PlayerSteamId})";
    }

    private static void Reply(CCitadelPlayerController? caller, string message)
    {
        if (caller != null)
            Chat.PrintToChat(caller, message);
        else
            Console.WriteLine(message);
    }

    private static void EnsureSafeAtom(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CommandException($"{label} is required.");

        EnsureNoCommandSeparator(value, label);

        if (value.Any(char.IsWhiteSpace))
            throw new CommandException($"{label} cannot contain whitespace.");
    }

    private static void EnsureNoCommandSeparator(string value, string label)
    {
        EnsureNoNewline(value, label);
        if (value.Contains(';'))
            throw new CommandException($"{label} cannot contain command separators.");
    }

    private static void EnsureNoNewline(string value, string label)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new CommandException($"{label} cannot contain newlines.");
    }
}
