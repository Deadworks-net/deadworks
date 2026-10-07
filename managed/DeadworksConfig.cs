using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadworksManaged;

internal class ServerBrowserConfig
{
    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = "https://api.deadworks.net";

    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; set; } = 90;

    [JsonPropertyName("content_addons")]
    public List<string> ContentAddons { get; set; } = [];

    [JsonPropertyName("extra_maps")]
    public List<string> ExtraMaps { get; set; } = [];

    [JsonPropertyName("unlisted")]
    public bool Unlisted { get; set; } = false;
}

internal class PermissionsConfig
{
    /// <summary>Which <see cref="DeadworksManaged.Api.IPermissionStore"/> holds roles and players. "json" is configs/permissions/*.jsonc.</summary>
    [JsonPropertyName("store")]
    public string Store { get; set; } = "json";

    /// <summary>Only apply grants once Steam has validated the player. Turn off only for LAN or local testing.</summary>
    [JsonPropertyName("require_steam_auth")]
    public bool RequireSteamAuth { get; set; } = true;
}

internal class ShowActivityConfig
{
    /// <summary>What players without deadworks.admin.notify see: "named", "anonymous" or "none".</summary>
    [JsonPropertyName("players")]
    public string Players { get; set; } = "anonymous";

    /// <summary>What holders of deadworks.admin.notify see: "named", "anonymous" or "none".</summary>
    [JsonPropertyName("notified")]
    public string Notified { get; set; } = "named";
}

internal class AdminConfig
{
    [JsonPropertyName("show_activity")]
    public ShowActivityConfig ShowActivity { get; set; } = new();

    /// <summary>Folder for the daily admin action logs, relative to the folder that holds configs/.</summary>
    [JsonPropertyName("log_dir")]
    public string LogDir { get; set; } = "logs/admin";
}

internal class PenaltiesConfig
{
    /// <summary>Which IPenaltyStore holds bans, gags and mutes. "json" is configs/penalties/penalties.jsonc.</summary>
    [JsonPropertyName("store")]
    public string Store { get; set; } = "json";

    /// <summary>How many days the JSON store keeps lifted and expired penalties as history. 0 keeps them forever.</summary>
    [JsonPropertyName("history_days")]
    public int HistoryDays { get; set; } = 90;
}

internal class DeadworksConfigRoot
{
    [JsonPropertyName("serverbrowser")]
    public ServerBrowserConfig ServerBrowser { get; set; } = new();

    [JsonPropertyName("permissions")]
    public PermissionsConfig Permissions { get; set; } = new();

    [JsonPropertyName("admin")]
    public AdminConfig Admin { get; set; } = new();

    [JsonPropertyName("penalties")]
    public PenaltiesConfig Penalties { get; set; } = new();
}

internal static class DeadworksConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static DeadworksConfigRoot _root = new();

    /// <summary>
    /// The store name used when deadworks.jsonc can't be read: no plugin can register it, so the permission and penalty
    /// stores stay unavailable. Falling back to the defaults would quietly swap a server's database for the JSON files.
    /// </summary>
    internal const string BrokenStoreName = "(deadworks.jsonc has an error)";

    /// <summary>deadworks.jsonc exists but couldn't be read, so everything that depends on it fails closed.</summary>
    public static bool Broken { get; private set; }
    private static string _configPath = "";

    public static ServerBrowserConfig ServerBrowser => _root.ServerBrowser;
    public static PermissionsConfig Permissions => _root.Permissions;
    public static AdminConfig Admin => _root.Admin;
    public static PenaltiesConfig Penalties => _root.Penalties;

    /// <summary>The folder that holds configs/ (game/bin/win64), for paths like admin.log_dir.</summary>
    public static string BaseDir => Path.GetDirectoryName(Path.GetDirectoryName(_configPath) ?? ".") ?? ".";

    public static void Initialize()
    {
        var managedDir = Path.GetDirectoryName(typeof(DeadworksConfig).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        _configPath = Path.Combine(configsDir, "deadworks.jsonc");

        Load();
    }

    /// <summary>Reads <paramref name="path"/> as deadworks.jsonc from scratch; null just resets to the defaults.</summary>
    internal static void LoadForTests(string? path)
    {
        _root = new();
        Broken = false;
        if (path == null)
            return;
        _configPath = path;
        Load();
    }

    private static void Load()
    {
        if (!File.Exists(_configPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                var json = JsonSerializer.Serialize(_root, JsonOptions);
                File.WriteAllText(_configPath, $"// Deadworks configuration\n{json}\n");
                Console.WriteLine($"[DeadworksConfig] Created default config: {_configPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DeadworksConfig] Failed to write default config: {ex.Message}");
            }
            return;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _root = JsonSerializer.Deserialize<DeadworksConfigRoot>(json, JsonOptions) ?? new();
            UnknownJsonKeys.Warn(json, typeof(DeadworksConfigRoot), "deadworks.jsonc", "[DeadworksConfig] WARNING:");
        }
        catch (Exception ex)
        {
            Broken = true;
            _root = new();
            _root.ServerBrowser.Unlisted = true;
            _root.Permissions.Store = BrokenStoreName;
            _root.Penalties.Store = BrokenStoreName;
            Console.WriteLine($"[DeadworksConfig] ERROR: failed to parse {JsonErrors.Describe("deadworks.jsonc", ex)}. Until it's fixed and the "
                              + "server restarted, nobody has any permissions, new players can't join (the ban list can't be checked) and "
                              + "the server isn't listed. The server console still works.");
        }
    }
}
