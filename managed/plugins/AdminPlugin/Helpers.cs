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
        var id = Permissions.GetSteamId64(player.Slot);
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
        => $"target={string.Join(',', players.Select(p => Permissions.GetSteamId64(p.Slot)))}";

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
        if (SteamIds.TryParse(player, out var offlineId) && !Players.GetAll().Any(p => Permissions.GetSteamId64(p.Slot) == offlineId))
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

    /// <summary>
    /// Announces an action on a player, or, for a SteamID that isn't on the server (no name), only logs it and tells the
    /// caller: everyone else would see "ADMIN: banned 76561197960287930", which means nothing to them. A new penalty on
    /// a SteamID also gets a link to its Steam profile, since a mistyped one is a valid ID of someone else.
    /// </summary>
    private static void Announce(Caller caller, string? name, ulong id, string action, string details, bool newPenalty = false)
    {
        if (name != null)
        {
            AdminActivity.Show(caller, action, details: details);
            return;
        }
        AdminActivity.Log(caller, action, details: details);
        caller.Reply($"{char.ToUpperInvariant(action[0])}{action[1..]}."
                     + (newPenalty ? $" Check it's the right account: https://steamcommunity.com/profiles/{id}" : ""));
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

    private string Reason(string[] words, string fallback)
    {
        var reason = string.Join(' ', words).Trim();
        if (reason.Length > 0)
            return reason;
        if (Config.RequireReason)
            throw new CommandException("Give a reason.");
        return fallback;
    }

    /// <summary>
    /// One line of someone's record, as needed for an appeal: when, what and for how long it was given, by whom and why,
    /// and how it ended. "2026-09-26 ban for 1 day by wisp: cheating; lifted early by lapka on 2026-09-27: appeal accepted".
    /// </summary>
    internal static string DescribeHistory(Penalty p, DateTime now)
    {
        var type = p.Type.ToString().ToLowerInvariant();
        var length = Penalties.DescribeDuration(p.ExpiresUtc - p.CreatedUtc);
        var given = $"{p.CreatedUtc:yyyy-MM-dd} {type} {length} by {Who(p.AdminName, p.AdminSteamId64)}"
                    + (p.Reason.Length > 0 ? $": {p.Reason}" : "");
        var ended = p.HowEnded(now) switch
        {
            PenaltyEnd.Lifted => $"lifted{(p.IsPermanent ? "" : " early")} by {Who(p.RemovedByName, p.RemovedBySteamId64)} on {p.RemovedUtc:yyyy-MM-dd}"
                                 + (p.RemovalReason is { Length: > 0 } why ? $": {why}" : ""),
            PenaltyEnd.Replaced => $"replaced by a new {type} from {Who(p.RemovedByName, p.RemovedBySteamId64)} on {p.RemovedUtc:yyyy-MM-dd}",
            PenaltyEnd.Expired => "ran out",
            _ => $"ACTIVE, {Left(p, now)}",
        };
        return $"{given}; {ended}";
    }

    /// <summary>One line of a bans/gags/mutes list: who, what's left of it, and who gave it why.</summary>
    private static string Describe(Penalty p, DateTime now)
        => $"{(p.PlayerName != null ? $"{p.PlayerName} ({p.SteamId64})" : p.SteamId64.ToString())}: {p.Type.ToString().ToLowerInvariant()}, {Left(p, now)}"
           + $", by {Who(p.AdminName, p.AdminSteamId64)}{(p.Reason.Length > 0 ? $": {p.Reason}" : "")}";

    // "2 hours left", not "for 2 hours", which reads like how long it was given for.
    private static string Left(Penalty p, DateTime now)
        => p.IsPermanent ? "permanent" : $"{p.DescribeRemaining(now)["for ".Length..]} left";

    // A name, or the SteamID when the record has none (older entries, custom stores). 0 is the server console.
    private static string Who(string? name, ulong steamId64) => name ?? (steamId64 == 0 ? "Console" : steamId64.ToString());
}
