using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    /// <summary>A multi-line reply: the caller's console, with a pointer to it in chat for players.</summary>
    private static void ReplyLines(Caller caller, IEnumerable<string> lines)
    {
        foreach (var line in lines)
            caller.PrintToConsole(line);
        if (!caller.IsConsole)
            caller.Reply("See your console for the output.");
    }

    /// <summary>The SteamID the player connected with. Bots have none, and can't be penalized.</summary>
    private static ulong SteamIdOf(CCitadelPlayerController player)
    {
        var id = Permissions.GetSteamId(player.Slot);
        return id != 0 ? id : throw new CommandException($"{player.PlayerName} is a bot.");
    }

    /// <summary>
    /// The players an action hit, for its one announcement: "lapka", "lapka and wisp", "lapka, wisp and 3 others".
    /// A group command announces once, not once per player.
    /// </summary>
    internal static string ListNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        <= 4 => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        _ => $"{string.Join(", ", names.Take(3))} and {names.Count - 3} others",
    };

    /// <summary>Every target's SteamID for the log line, which names them all even when the announcement doesn't. Bots are 0.</summary>
    private static string TargetIds(IEnumerable<CCitadelPlayerController> players)
        => $"target={string.Join(',', players.Select(p => Permissions.GetSteamId(p.Slot)))}";

    /// <summary>
    /// Checks a new penalty against the player's current one of the same type, which it replaces. Nobody may penalize
    /// themselves, and cutting a penalty short partly lifts it, so that needs <paramref name="liftPermission"/> (null when
    /// the command's own permission already lifts, as with gag and ungag). Returns the note for the announcement, e.g.
    /// " (replaces a permanent ban by wisp)", or "" when there was nothing to replace.
    /// </summary>
    private static string CheckReplace(Caller caller, PenaltyType type, ulong id, string name, TimeSpan? duration, string? liftPermission)
    {
        var noun = type.ToString().ToLowerInvariant();
        if (!caller.IsConsole && id == caller.SteamId64)
            throw new CommandException($"You can't {noun} yourself.");
        if (Penalties.GetActive(type, id) is not { } current)
            return "";

        var by = current.AdminName ?? "Console";
        var existing = current.IsPermanent
            ? $"a permanent {noun} by {by}"
            : $"a {noun} by {by} with {current.DescribeRemaining(DateTime.UtcNow)["for ".Length..]} left";
        if (liftPermission != null && Penalties.WouldShorten(type, id, duration) != null && !caller.HasPermission(liftPermission))
            throw new CommandException($"{name} already has {existing}. Shortening it needs {liftPermission}.");
        return $" (replaces {existing})";
    }

    /// <summary>
    /// A player on the server, or a SteamID that isn't, for commands that work on both. Immunity is checked either way,
    /// against the saved entry for an offline SteamID. The name is null for an offline SteamID.
    /// </summary>
    private static (ulong Id, string? Name) PlayerOrSteamId(Caller caller, string player, string command)
    {
        if (SteamIds.TryParse(player, out var offlineId) && !Players.GetAll().Any(p => Permissions.GetSteamId(p.Slot) == offlineId))
        {
            if (!caller.CanTarget(offlineId))
                throw new CommandException(Permissions.IsLoaded(offlineId)
                    ? $"You can't {command} {offlineId}: their immunity is higher than yours."
                    : $"{offlineId}'s record is still loading. Try again in a moment.");
            return (offlineId, null);
        }
        var target = OnePlayer(Target.Resolve(caller, player), command);
        return (SteamIdOf(target), target.PlayerName);
    }

    private static string Noun(PenaltyType type) => type.ToString().ToLowerInvariant();

    private static string Past(PenaltyType type) => type switch
    {
        PenaltyType.Ban => "banned",
        PenaltyType.Gag => "gagged",
        _ => "muted",
    };

    /// <summary>One named player; groups like @all are refused for penalties.</summary>
    private static CCitadelPlayerController OnePlayer(Target target, string command)
        => target.IsGroup
            ? throw new CommandException($"{command} works on one player at a time, not {target.Input}.")
            : target.Single();

    /// <summary>Turns a minutes argument into a duration. 0 means permanent.</summary>
    private static TimeSpan? Duration(int minutes)
    {
        if (minutes < 0)
            throw new CommandException("The time can't be negative. Use 0 for permanent.");
        return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }

    /// <summary>"permanently", "for 45 minutes", "for 2 hours", "for 3 days", "for 1h 30m".</summary>
    internal static string DescribeDuration(TimeSpan? duration)
    {
        if (duration is not { } d)
            return "permanently";
        var minutes = (long)d.TotalMinutes;
        if (minutes < 60) return $"for {minutes} minute{(minutes == 1 ? "" : "s")}";
        if (minutes % 1440 == 0) return $"for {minutes / 1440} day{(minutes == 1440 ? "" : "s")}";
        if (minutes % 60 == 0) return $"for {minutes / 60} hour{(minutes == 60 ? "" : "s")}";
        return $"for {minutes / 60}h {minutes % 60}m";
    }

    private string Reason(string[] words, string fallback)
    {
        var reason = string.Join(' ', words).Trim();
        if (reason.Length > 0)
            return reason;
        if (Config.RequireReason)
            throw new CommandException("Give a reason.");
        return fallback;
    }

    private static string Describe(Penalty p, DateTime now)
        => $"{p.PlayerName ?? "unknown player"} ({p.SteamId64}) {p.Type.ToString().ToLowerInvariant()} {p.DescribeRemaining(now)}"
           + $" by {p.AdminName ?? "Console"}{(p.Reason.Length > 0 ? $": {p.Reason}" : "")}";
}
