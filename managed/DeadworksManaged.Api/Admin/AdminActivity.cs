namespace DeadworksManaged.Api;

/// <summary>One line of the admin action log.</summary>
/// <param name="TimeUtc">When it happened.</param>
/// <param name="AdminSteamId64">Who did it, or 0 for the server console.</param>
/// <param name="AdminName">The admin's name, or "Console".</param>
/// <param name="Action">What they did, as announced, e.g. <c>kicked lapka: spam</c>.</param>
/// <param name="Details">Extra detail for the log only, e.g. the target's SteamID.</param>
public sealed record AdminLogEntry(DateTime TimeUtc, ulong AdminSteamId64, string AdminName, string Action, string? Details = null)
{
    /// <summary>The line as written to the log file.</summary>
    public override string ToString()
        => $"{TimeUtc:yyyy-MM-ddTHH:mm:ssZ} {AdminName}{(AdminSteamId64 != 0 ? $" ({AdminSteamId64})" : "")} {Action}"
           + (string.IsNullOrEmpty(Details) ? "" : $" [{Details}]");
}

/// <summary>
/// Announces admin actions to players and records them in the admin log, so every admin plugin behaves the same.
/// Players see "ADMIN: kicked lapka"; holders of <c>deadworks.admin.notify</c> see who did it. Both are configurable
/// under <c>admin.show_activity</c> in <c>configs/deadworks.jsonc</c>.
/// </summary>
public static class AdminActivity
{
    internal static Action<CCitadelPlayerController?, string, string?, bool>? Backend;

    /// <summary>
    /// Announces <paramref name="action"/> (e.g. "kicked lapka: spam") and logs it. <paramref name="details"/>, such as
    /// the target's SteamID, goes in the log only.
    /// </summary>
    public static void Show(CCitadelPlayerController? admin, string action, string? details = null) => Invoke(admin, action, details, announce: true);

    /// <summary>Logs <paramref name="action"/> without announcing it, e.g. for rcon or unban.</summary>
    public static void Log(CCitadelPlayerController? admin, string action, string? details = null) => Invoke(admin, action, details, announce: false);

    /// <summary>Raised for every logged action, e.g. to forward admin actions to Discord.</summary>
    public static event Action<AdminLogEntry>? Logged;

    internal static void RaiseLogged(AdminLogEntry entry)
    {
        var handlers = Logged;
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AdminLogEntry>)handler)(entry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdminActivity] Logged handler threw: {ex.Message}");
            }
        }
    }

    private static void Invoke(CCitadelPlayerController? admin, string action, string? details, bool announce)
    {
        if (Backend == null)
            throw new InvalidOperationException("Admin activity is not initialized.");
        Backend(admin, action, details, announce);
    }
}
