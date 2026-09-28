using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    [Command("kick", Description = "Kick a player: kick <player> [reason]", Permission = Perm.Kick, SuppressChat = true)]
    public void CmdKick(Caller caller, Target player, params string[] reason)
    {
        var why = Reason(reason, Config.DefaultKickReason);
        // @all and @team leave out whoever ran the command.
        var players = player.IsGroup && caller.Player is { } self
            ? player.Where(p => p.Slot != self.Slot).ToList()
            : player.ToList();
        if (players.Count == 0)
            throw new CommandException($"'{player.Input}' only matches you.");

        var names = players.Select(p => p.PlayerName).ToList();
        var ids = TargetIds(players);
        foreach (var p in players)
            p.Kick($"You were kicked: {why}");
        AdminActivity.Show(caller, $"kicked {ListNames(names)}: {why}", details: ids);
    }

    // "addban" is SourceMod's name for banning by SteamID; here ban already takes one, online or not.
    [Command("ban", "addban", Description = "Ban a player, or a SteamID online or not: ban <player|steamid> <minutes> [reason], 0 = permanent", Permission = Perm.Ban, SuppressChat = true)]
    public void CmdBan(Caller caller, string player, int minutes, params string[] reason)
    {
        var duration = Duration(minutes);
        var why = Reason(reason, Config.DefaultBanReason);
        var (id, name) = PlayerOrSteamId(caller, player, "ban");

        var replacing = CheckReplace(caller, PenaltyType.Ban, id, name ?? id.ToString(), duration, liftPermission: Perm.Unban);
        // Adding the ban kicks them, so it goes last; it also refuses players Steam hasn't verified yet.
        var penalty = Penalties.Add(PenaltyType.Ban, id, duration, why, caller, name);
        Announce(caller, name, id, $"banned {name ?? id.ToString()} {DescribeDuration(duration)}: {why}{replacing}", $"target={id} penalty={penalty.Id}");
    }

    [Command("unban", Description = "Lift a ban: unban <steamid> [reason]", Permission = Perm.Unban, SuppressChat = true)]
    public void CmdUnban(Caller caller, string steamId, params string[] reason)
    {
        if (!SteamIds.TryParse(steamId, out var id))
            throw new CommandException($"'{steamId}' isn't a SteamID64, Steam2 or Steam3 ID.");
        var name = Penalties.GetActive(PenaltyType.Ban, id)?.PlayerName;
        var why = string.Join(' ', reason).Trim();
        if (!Penalties.Remove(PenaltyType.Ban, id, caller, why))
            throw new CommandException($"{id} isn't banned.");

        var who = name != null ? $"{name} ({id})" : id.ToString();
        AdminActivity.Log(caller, $"unbanned {who}{(why.Length > 0 ? $": {why}" : "")}", details: $"target={id}");
        caller.Reply($"Unbanned {who}.");
    }

    [Command("bans", Description = "List active bans", Permission = Perm.Ban, SuppressChat = true)]
    public void CmdBans(Caller caller) => ListActive(caller, PenaltyType.Ban, "ban");

    [Command("gag", Description = "Stop a player using chat: gag <player|steamid> <minutes> [reason], 0 = permanent", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdGag(Caller caller, string player, int minutes, params string[] reason)
        => AddPenalty(caller, PenaltyType.Gag, player, minutes, Reason(reason, Config.DefaultGagReason));

    [Command("ungag", Description = "Let a gagged player use chat again: ungag <player|steamid> [reason]", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdUngag(Caller caller, string player, params string[] reason) => LiftPenalty(caller, PenaltyType.Gag, player, reason);

    [Command("gags", Description = "List active gags", Permission = Perm.Gag, SuppressChat = true)]
    public void CmdGags(Caller caller) => ListActive(caller, PenaltyType.Gag, "gag");

    [Command("mute", Description = "Stop a player using voice chat: mute <player|steamid> <minutes> [reason], 0 = permanent", Permission = Perm.Mute, SuppressChat = true)]
    public void CmdMute(Caller caller, string player, int minutes, params string[] reason)
        => AddPenalty(caller, PenaltyType.Mute, player, minutes, Reason(reason, Config.DefaultMuteReason));

    [Command("unmute", Description = "Let a muted player use voice chat again: unmute <player|steamid> [reason]", Permission = Perm.Mute, SuppressChat = true)]
    public void CmdUnmute(Caller caller, string player, params string[] reason) => LiftPenalty(caller, PenaltyType.Mute, player, reason);

    /// <summary>gag and mute: the same steps as ban, without a kick or a separate lifting permission.</summary>
    private void AddPenalty(Caller caller, PenaltyType type, string player, int minutes, string why)
    {
        var duration = Duration(minutes);
        var verb = Past(type);
        var (id, name) = PlayerOrSteamId(caller, player, Noun(type));
        var replacing = CheckReplace(caller, type, id, name ?? id.ToString(), duration, liftPermission: null);
        Penalties.Add(type, id, duration, why, caller, name);
        Announce(caller, name, id, $"{verb} {name ?? id.ToString()} {DescribeDuration(duration)}: {why}{replacing}", $"target={id}");
    }

    /// <summary>ungag and unmute: whoever gave it, online or not.</summary>
    private static void LiftPenalty(Caller caller, PenaltyType type, string player, string[] reason)
    {
        var (id, name) = PlayerOrSteamId(caller, player, $"un{Noun(type)}");
        var why = string.Join(' ', reason).Trim();
        if (!Penalties.Remove(type, id, caller, why))
            throw new CommandException($"{name ?? id.ToString()} isn't {Past(type)}.");
        Announce(caller, name, id, $"un{Past(type)} {name ?? id.ToString()}{(why.Length > 0 ? $": {why}" : "")}", $"target={id}");
    }

    [Command("mutes", Description = "List active mutes", Permission = Perm.Mute, SuppressChat = true)]
    public void CmdMutes(Caller caller) => ListActive(caller, PenaltyType.Mute, "mute");

    [Command("slay", Description = "Kill a player's hero: slay <player>", Permission = Perm.Slay, SuppressChat = true)]
    public void CmdSlay(Caller caller, Target player)
    {
        var slain = new List<CCitadelPlayerController>();
        foreach (var p in player)
        {
            var pawn = p.GetHeroPawn();
            if (pawn == null || pawn.Health <= 0)
                continue;
            using var damage = new CTakeDamageInfo(999999f, attacker: pawn);
            damage.DamageFlags |= TakeDamageFlags.ForceDeath | TakeDamageFlags.AllowSuicide;
            pawn.TakeDamage(damage);
            slain.Add(p);
        }
        if (slain.Count == 0)
            throw new CommandException(player.IsGroup ? $"Nobody in {player.Input} is alive." : $"{player.Single().PlayerName} isn't alive.");
        AdminActivity.Show(caller, $"slayed {ListNames(slain.Select(p => p.PlayerName).ToList())}", details: TargetIds(slain));
    }

    [Command("who", Description = "List players with their SteamID, team, roles and penalties: who [player]", Permission = Perm.Who, SuppressChat = true)]
    public void CmdWho(Caller caller, Target? player = null)
    {
        var players = player?.ToList() ?? Players.GetAll().ToList();
        var lines = new List<string> { $"{players.Count} player{(players.Count == 1 ? "" : "s")}:" };
        foreach (var p in players.OrderBy(p => p.Slot))
        {
            var id = Permissions.GetSteamId(p.Slot);
            var roles = id == 0 ? "bot" : string.Join(", ", Permissions.GetRoles(id).DefaultIfEmpty("no roles"));
            var flags = new List<string>();
            if (id != 0 && !Players.IsAuthenticated(p.Slot)) flags.Add("not Steam-verified");
            if (id != 0 && Penalties.IsGagged(id)) flags.Add("gagged");
            if (id != 0 && Penalties.IsMuted(id)) flags.Add("muted");
            var suffix = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            lines.Add($"  #{p.Slot} {p.PlayerName} ({(id == 0 ? "bot" : id)}) team {p.TeamNum}, {roles}{suffix}");
        }
        ReplyLines(caller, lines);
    }

    [Command("penalties", Description = "Show your own bans, gags and mutes, or another player's: penalties [player|steamid]", SuppressChat = true)]
    public async Task CmdPenalties(Caller caller, string player = "")
    {
        ulong id;
        string? name = null;
        if (player.Length > 0)
        {
            if (!caller.HasPermission(Perm.Who))
                throw new CommandException("You can only look up your own penalties. Use penalties on its own.");
            // Anyone's record can be read, whatever their immunity: looking isn't acting on them.
            if (!SteamIds.TryParse(player, out id))
            {
                var target = OnePlayer(Target.Resolve(caller, player, enforceImmunity: false), "penalties");
                (id, name) = (SteamIdOf(target), target.PlayerName);
            }
        }
        else
        {
            id = !caller.IsConsole ? caller.SteamId64 : throw new CommandException("Give a player or a SteamID.");
            name = caller.Name;
        }

        IReadOnlyList<Penalty> history;
        try
        {
            history = await Penalties.GetHistoryAsync(id); // a database store may take a while; this resumes on the game thread
        }
        catch (Exception ex)
        {
            throw new CommandException($"Couldn't load penalties: {ex.Message}");
        }

        // Replies to a player who has left go nowhere, not to whoever took their slot.
        var now = DateTime.UtcNow;
        name ??= history.Select(p => p.PlayerName).FirstOrDefault(n => n != null);
        var lines = new List<string> { $"Penalties for {(name != null ? $"{name} ({id})" : id.ToString())}:" };
        lines.AddRange(history.Select(p => $"  {DescribeHistory(p, now)}"));
        if (history.Count == 0)
            lines.Add("  none");
        ReplyLines(caller, lines);
    }

    private static void ListActive(Caller caller, PenaltyType type, string what)
    {
        var now = DateTime.UtcNow;
        var active = Penalties.GetActive(type);
        var lines = new List<string> { $"{active.Count} active {what}{(active.Count == 1 ? "" : "s")}:" };
        lines.AddRange(active.Select(p => $"  {Describe(p, now)}"));
        ReplyLines(caller, lines);
    }
}
