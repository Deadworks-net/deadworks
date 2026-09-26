using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    [Command("kick", Description = "Kick a player: kick <player> [reason]", Permission = Perm.Kick, SuppressChat = true)]
    public void CmdKick(CCitadelPlayerController? caller, Target target, params string[] reason)
    {
        var why = Reason(reason, Config.DefaultKickReason);
        foreach (var player in target)
        {
            var name = player.PlayerName;
            var id = Permissions.GetSteamId(player.Slot);
            player.Kick($"You were kicked: {why}");
            AdminActivity.Show(caller, $"kicked {name}: {why}", details: $"target={id}");
        }
    }

    [Command("ban", Description = "Ban a player: ban <player> <minutes> [reason], 0 = permanent", Permission = Perm.Ban, SuppressChat = true)]
    public void CmdBan(CCitadelPlayerController? caller, Target target, int minutes, params string[] reason)
    {
        var player = OnePlayer(target, "ban");
        var id = SteamIdOf(player);
        var duration = Duration(caller, minutes);
        var why = Reason(reason, Config.DefaultBanReason);
        var name = player.PlayerName;

        // Adding the ban kicks them, so announce first while the name is still on the server.
        AdminActivity.Show(caller, $"banned {name} {DescribeDuration(duration)}: {why}", details: $"target={id}");
        Penalties.Add(PenaltyType.Ban, id, duration, why, caller, name);
    }

    [Command("addban", Description = "Ban a SteamID, online or not: addban <steamid> <minutes> [reason]", Permission = Perm.BanOffline, SuppressChat = true)]
    public void CmdAddBan(CCitadelPlayerController? caller, string steamId, int minutes, params string[] reason)
    {
        if (!SteamIds.TryParse(steamId, out var id))
            throw new CommandException($"'{steamId}' isn't a SteamID64, Steam2 or Steam3 ID.");
        if (caller != null && !Permissions.CanTarget(Permissions.GetSteamId(caller.Slot), id))
            throw new CommandException($"You can't ban {id}: their immunity is higher than yours.");

        var duration = Duration(caller, minutes);
        var why = Reason(reason, Config.DefaultBanReason);
        AdminActivity.Show(caller, $"banned {id} {DescribeDuration(duration)}: {why}", details: $"target={id}");
        Penalties.Add(PenaltyType.Ban, id, duration, why, caller);
    }

    [Command("unban", Description = "Lift a ban: unban <steamid>", Permission = Perm.Unban, SuppressChat = true)]
    public void CmdUnban(CCitadelPlayerController? caller, string steamId)
    {
        if (!SteamIds.TryParse(steamId, out var id))
            throw new CommandException($"'{steamId}' isn't a SteamID64, Steam2 or Steam3 ID.");
        if (!Penalties.Remove(PenaltyType.Ban, id, caller))
            throw new CommandException($"{id} isn't banned.");

        AdminActivity.Log(caller, $"unbanned {id}", details: $"target={id}");
        Reply(caller, $"Unbanned {id}.");
    }

    [Command("bans", Description = "List active bans", Permission = Perm.Ban, SuppressChat = true)]
    public void CmdBans(CCitadelPlayerController? caller) => ListActive(caller, PenaltyType.Ban, "bans");

    [Command("gag", Description = "Stop a player using chat: gag <player> [minutes] [reason], no time = permanent", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdGag(CCitadelPlayerController? caller, Target target, int minutes = 0, params string[] reason)
    {
        var player = OnePlayer(target, "gag");
        var id = SteamIdOf(player);
        var duration = Duration(caller, minutes);
        var why = Reason(reason, Config.DefaultGagReason);

        Penalties.Add(PenaltyType.Gag, id, duration, why, caller, player.PlayerName);
        AdminActivity.Show(caller, $"gagged {player.PlayerName} {DescribeDuration(duration)}: {why}", details: $"target={id}");
    }

    [Command("ungag", Description = "Let a gagged player use chat again: ungag <player>", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdUngag(CCitadelPlayerController? caller, Target target)
    {
        var player = OnePlayer(target, "ungag");
        var id = SteamIdOf(player);
        if (!Penalties.Remove(PenaltyType.Gag, id, caller))
            throw new CommandException($"{player.PlayerName} isn't gagged.");
        AdminActivity.Show(caller, $"ungagged {player.PlayerName}", details: $"target={id}");
    }

    [Command("gags", Description = "List active gags", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdGags(CCitadelPlayerController? caller) => ListActive(caller, PenaltyType.Gag, "gags");

    [Command("slay", Description = "Kill a player's hero: slay <player>", Permission = Perm.Slay, SuppressChat = true)]
    public void CmdSlay(CCitadelPlayerController? caller, Target target)
    {
        foreach (var player in target)
        {
            var pawn = player.GetHeroPawn();
            if (pawn == null || pawn.Health <= 0)
                continue;
            using var damage = new CTakeDamageInfo(999999f, attacker: pawn);
            damage.DamageFlags |= TakeDamageFlags.ForceDeath | TakeDamageFlags.AllowSuicide;
            pawn.TakeDamage(damage);
            AdminActivity.Show(caller, $"slayed {player.PlayerName}", details: $"target={Permissions.GetSteamId(player.Slot)}");
        }
    }

    [Command("who", Description = "List players with their SteamID, team, roles and penalties: who [player]", Permission = Perm.Who, SuppressChat = true)]
    public void CmdWho(CCitadelPlayerController? caller, Target? target = null)
    {
        var players = target?.ToList() ?? Players.GetAll().ToList();
        var lines = new List<string> { $"{players.Count} player(s):" };
        foreach (var p in players.OrderBy(p => p.Slot))
        {
            var id = Permissions.GetSteamId(p.Slot);
            var roles = id == 0 ? "bot" : string.Join(", ", Permissions.GetRoles(id).DefaultIfEmpty("no roles"));
            var flags = new List<string>();
            if (id != 0 && !Players.IsAuthenticated(p.Slot)) flags.Add("not Steam-verified");
            if (id != 0 && Penalties.IsGagged(id)) flags.Add("gagged");
            var suffix = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            lines.Add($"  #{p.Slot} {p.PlayerName} ({(id == 0 ? "bot" : id)}) team {p.TeamNum}, {roles}{suffix}");
        }
        ReplyLines(caller, lines);
    }

    [Command("penalties", Description = "Show your own bans and gags, or another player's: penalties [steamid]", SuppressChat = true)]
    public void CmdPenalties(CCitadelPlayerController? caller, string steamId = "")
    {
        ulong id;
        if (steamId.Length > 0)
        {
            if (!caller.HasPermission(Perm.Who))
                throw new CommandException("You can only look up your own penalties. Use penalties with no SteamID.");
            if (!SteamIds.TryParse(steamId, out id))
                throw new CommandException($"'{steamId}' isn't a SteamID64, Steam2 or Steam3 ID.");
        }
        else
        {
            id = caller != null ? Permissions.GetSteamId(caller.Slot) : throw new CommandException("Give a SteamID.");
        }

        var slot = caller?.Slot ?? -1;
        var history = Penalties.GetHistoryAsync(id);
        if (history.IsCompleted)
            PrintHistory(slot, id, history);
        else
            history.ContinueWith(t => Timer.NextTick(() => PrintHistory(slot, id, t)), TaskScheduler.Default);
    }

    private static void PrintHistory(int slot, ulong id, Task<IReadOnlyList<Penalty>> history)
    {
        // The command may have come from a player who has since left; don't print to whoever took the slot.
        var caller = slot < 0 ? null : Players.FromSlot(slot);
        if (slot >= 0 && caller == null)
            return;

        if (!history.IsCompletedSuccessfully)
        {
            Reply(caller, "Couldn't load penalties; the server console has details.");
            return;
        }

        var now = DateTime.UtcNow;
        var lines = new List<string> { $"Penalties for {id}:" };
        foreach (var p in history.Result)
        {
            var state = p.IsActiveAt(now) ? "ACTIVE" : p.RemovedUtc != null ? "lifted" : "expired";
            lines.Add($"  [{state}] {p.CreatedUtc:yyyy-MM-dd} {Describe(p, now)}");
        }
        if (history.Result.Count == 0)
            lines.Add("  none");
        ReplyLines(caller, lines);
    }

    private static void ListActive(CCitadelPlayerController? caller, PenaltyType type, string what)
    {
        var now = DateTime.UtcNow;
        var active = Penalties.GetActive(type);
        var lines = new List<string> { $"{active.Count} active {what}:" };
        lines.AddRange(active.Select(p => $"  {Describe(p, now)}"));
        ReplyLines(caller, lines);
    }
}
