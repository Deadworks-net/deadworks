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
        2 or 3 => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        _ => $"{string.Join(", ", names.Take(3))} and {names.Count - 3} others",
    };

    /// <summary>Every target's SteamID for the log line, which names them all even when the announcement doesn't. Bots are 0.</summary>
    private static string TargetIds(IEnumerable<CCitadelPlayerController> players)
        => $"target={string.Join(',', players.Select(p => Permissions.GetSteamId(p.Slot)))}";

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
        => $"{p.PlayerName ?? "?"} ({p.SteamId64}) {p.Type.ToString().ToLowerInvariant()} {p.DescribeRemaining(now)}"
           + $" by {p.AdminName ?? "Console"}{(p.Reason.Length > 0 ? $": {p.Reason}" : "")}";
}
