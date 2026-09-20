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

    private const string Header = "// Deadworks configuration\n// dw_addons add/remove rewrites this file; hand-written comments are not preserved.\n";

    private static DeadworksConfigRoot _root = new();
    private static string _configPath = "";

    /// <summary>
    /// The live config. Read it through this property each time rather than caching the object:
    /// <see cref="Reload"/> replaces it with a fresh instance.
    /// </summary>
    public static ServerBrowserConfig ServerBrowser => _root.ServerBrowser;

    public static string ConfigPath => _configPath;

    public static void Initialize()
    {
        var managedDir = Path.GetDirectoryName(typeof(DeadworksConfig).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        InitializeAt(Path.Combine(configsDir, "deadworks.jsonc"));
    }

    /// <summary>Loads the config from an explicit path. Tests use this to avoid the assembly-relative location.</summary>
    internal static void InitializeAt(string configPath)
    {
        _configPath = configPath;
        _root = new();
        Load();
    }

    /// <summary>
    /// Re-reads the config file from disk so edits made outside the process (the hosting portal writing
    /// <c>deadworks.jsonc</c> directly, or an admin over SFTP) take effect without a restart. On a parse
    /// error the previous config is kept and false is returned.
    /// </summary>
    public static bool Reload() => Load();

    /// <summary>Writes the current config back to disk. Returns false (and logs) if the write fails.</summary>
    public static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var json = JsonSerializer.Serialize(_root, JsonOptions);
            File.WriteAllText(_configPath, $"{Header}{json}\n");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeadworksConfig] Failed to save config: {ex.Message}");
            return false;
        }
    }

    private static bool Load()
    {
        if (!File.Exists(_configPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                var json = JsonSerializer.Serialize(_root, JsonOptions);
                File.WriteAllText(_configPath, $"{Header}{json}\n");
                Console.WriteLine($"[DeadworksConfig] Created default config: {_configPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DeadworksConfig] Failed to write default config: {ex.Message}");
            }
            return true;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _root = JsonSerializer.Deserialize<DeadworksConfigRoot>(json, JsonOptions) ?? new();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeadworksConfig] Failed to parse config: {ex.Message}");
            return false;
        }
    }
}
