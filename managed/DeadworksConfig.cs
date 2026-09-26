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

    /// <summary>How long the JSON store keeps lifted and expired penalties as history.</summary>
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
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static DeadworksConfigRoot _root = new();
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeadworksConfig] Failed to parse config: {ex.Message}");
        }
    }
}
