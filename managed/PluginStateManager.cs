using System.Text.Json;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal static class PluginStateManager
{
    private static ILogger _logger = null!;
    private static string _filePath = "";
    private static Dictionary<string, bool> _states = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static void Initialize()
    {
        _logger = DeadworksTelemetry.CreateLogger("PluginStateManager");
        var managedDir = Path.GetDirectoryName(typeof(PluginStateManager).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        _filePath = Path.Combine(configsDir, "plugins.jsonc");
        Load();
    }

    /// <summary>Returns true if the plugin should be loaded (default: enabled).</summary>
    public static bool IsEnabled(string dllName)
        => !_states.TryGetValue(dllName, out var enabled) || enabled;

    public static void SetEnabled(string dllName, bool enabled)
    {
        _states[dllName] = enabled;
        Save();
    }

    private static void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOptions);
            if (loaded != null)
                _states = new Dictionary<string, bool>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin state from {FilePath}", _filePath);
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var sorted = _states
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var json = JsonSerializer.Serialize(sorted, JsonOptions);
            File.WriteAllText(_filePath, $"// Plugin enable/disable state.\n// Set a plugin name (without .dll) to false to disable it on restart, or use: dw_plugin enable/disable <name>\n{json}\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save plugin state to {FilePath}", _filePath);
        }
    }
}
