using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    /// <summary>A short reply: chat for players, the server console otherwise.</summary>
    private static void Reply(CCitadelPlayerController? caller, string message)
    {
        if (caller != null)
            Chat.PrintToChat(caller, message);
        else
            Console.WriteLine(message);
    }

    /// <summary>A multi-line reply: the player's console, with a pointer to it in chat.</summary>
    private static void ReplyLines(CCitadelPlayerController? caller, IEnumerable<string> lines)
    {
        if (caller == null)
        {
            foreach (var line in lines)
                Console.WriteLine(line);
            return;
        }
        foreach (var line in lines)
            caller.PrintToConsole(line);
        Chat.PrintToChat(caller, "See your console for the output.");
    }

    /// <summary>The SteamID the player connected with. Bots have none, and can't be penalized.</summary>
    private static ulong SteamIdOf(CCitadelPlayerController player)
    {
        var id = Permissions.GetSteamId(player.Slot);
        return id != 0 ? id : throw new CommandException($"{player.PlayerName} is a bot.");
    }

    /// <summary>One named player; groups like @all are refused for penalties.</summary>
    private static CCitadelPlayerController OnePlayer(Target target, string command)
        => target.IsGroup
            ? throw new CommandException($"{command} works on one player at a time, not {target.Input}.")
            : target.Single();

    /// <summary>
    /// Turns a minutes argument into a duration. 0 means permanent, which needs <see cref="Perm.BanPermanent"/>.
    /// </summary>
    private static TimeSpan? Duration(CCitadelPlayerController? caller, int minutes)
    {
        if (minutes < 0)
            throw new CommandException("The time can't be negative. Use 0 for permanent.");
        if (minutes > 0)
            return TimeSpan.FromMinutes(minutes);
        if (!caller.HasPermission(Perm.BanPermanent))
            throw new CommandException("You can't give permanent penalties. Give a time in minutes.");
        return null;
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
