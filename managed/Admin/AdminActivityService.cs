using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.AdminSystem;

/// <summary>Backs <see cref="AdminActivity"/>: announces admin actions in chat and writes the daily admin log.</summary>
internal static class AdminActivityService
{
    internal enum Visibility { Named, Anonymous, None }

    public const string NotifyPermission = "deadworks.admin.notify";

    private static readonly Lock _fileLock = new();
    private static string _logDir = "";
    private static Visibility _players = Visibility.Anonymous;
    private static Visibility _notified = Visibility.Named;

    /// <summary>Overridable so tests can capture chat.</summary>
    internal static Action<CCitadelPlayerController, string> SendChat { get; set; } = Chat.PrintToChat;

    internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public static void Initialize()
    {
        var config = DeadworksConfig.Admin;
        Initialize(Path.GetFullPath(Path.Combine(DeadworksConfig.BaseDir, config.LogDir)),
            Parse(config.ShowActivity.Players, Visibility.Anonymous),
            Parse(config.ShowActivity.Notified, Visibility.Named));
    }

    internal static void Initialize(string logDir, Visibility players, Visibility notified)
    {
        _logDir = logDir;
        _players = players;
        _notified = notified;
        AdminActivity.Backend = Handle;
    }

    private static Visibility Parse(string value, Visibility fallback)
    {
        if (Enum.TryParse<Visibility>(value, ignoreCase: true, out var v))
            return v;
        Console.WriteLine($"[AdminActivity] '{value}' is not named, anonymous or none; using {fallback.ToString().ToLowerInvariant()}");
        return fallback;
    }

    private static void Handle(CCitadelPlayerController? admin, string action, string? details, bool announce)
    {
        var adminId = admin == null ? 0 : PermissionManager.GetSlotSteamId(admin.Slot);
        var adminName = admin?.PlayerName ?? "Console";
        var entry = new AdminLogEntry(Now(), adminId, adminName, action, details);

        Console.WriteLine($"[Admin] {entry}");
        WriteToFile(entry);
        AdminActivity.RaiseLogged(entry);

        if (!announce)
            return;
        foreach (var player in Players.GetAll())
        {
            var visibility = PermissionManager.HasForSlot(player.Slot, NotifyPermission) ? _notified : _players;
            if (Format(visibility, adminName, action) is { } text)
                SendChat(player, text);
        }
    }

    internal static string? Format(Visibility visibility, string adminName, string action) => visibility switch
    {
        Visibility.Named => $"{adminName}: {action}",
        Visibility.Anonymous => $"ADMIN: {action}",
        _ => null
    };

    private static void WriteToFile(AdminLogEntry entry)
    {
        if (_logDir.Length == 0)
            return;
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(_logDir);
                File.AppendAllText(Path.Combine(_logDir, $"admin-{entry.TimeUtc:yyyy-MM-dd}.log"), entry + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AdminActivity] Failed to write the admin log: {ex.Message}");
        }
    }
}
