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

internal class DeadworksConfigRoot
{
    [JsonPropertyName("serverbrowser")]
    public ServerBrowserConfig ServerBrowser { get; set; } = new();
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
