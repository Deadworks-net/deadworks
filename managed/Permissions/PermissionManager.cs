using System.Text.Json;
using DeadworksManaged.Api;

namespace DeadworksManaged.Permissions;

internal sealed class RoleDefinition
{
    public string[] Permissions { get; set; } = [];
}

internal sealed class PlayerDefinition
{
    public string[] Roles { get; set; } = [];
}

internal static class PermissionManager
{
    private static readonly Lock _lock = new();
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static string _permissionsDir = "";
    private static Dictionary<string, RoleDefinition> _roles = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, PlayerDefinition> _players = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        var managedDir = Path.GetDirectoryName(typeof(PermissionManager).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        Initialize(configsDir);
    }

    internal static void Initialize(string permissionsDir)
    {
        lock (_lock)
        {
            _permissionsDir = permissionsDir;
        }

        PermissionResolver.GetRoles = GetRoles;
        PermissionResolver.HasPermission = HasPermission;

        EnsureDefaultFiles();
        Reload();
    }

    public static bool Reload()
    {
        if (_permissionsDir.Length == 0)
            return false;

        try
        {
            var roles = LoadDictionary<RoleDefinition>(Path.Combine(_permissionsDir, "roles.jsonc"));
            var players = LoadPlayers(Path.Combine(_permissionsDir, "players.jsonc"));

            lock (_lock)
            {
                _roles = roles;
                _players = players;
            }

            Console.WriteLine($"[PermissionManager] Loaded {_roles.Count} roles and {_players.Count} player entries");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PermissionManager] Failed to load permissions: {ex.Message}");
            return false;
        }
    }

    public static bool HasPermission(CCitadelPlayerController? controller, string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
            return true;

        if (controller == null)
            return false;

        return HasPermission(controller.PlayerSteamId, permission);
    }

    public static bool HasPermission(ulong steamId64, string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
            return true;

        var playerKey = SteamIds.ToPermissionKey(steamId64);
        Dictionary<string, RoleDefinition> roles;
        Dictionary<string, PlayerDefinition> players;

        lock (_lock)
        {
            roles = new Dictionary<string, RoleDefinition>(_roles, StringComparer.OrdinalIgnoreCase);
            players = new Dictionary<string, PlayerDefinition>(_players, StringComparer.OrdinalIgnoreCase);
        }

        if (!players.TryGetValue(playerKey, out var player))
            return false;

        foreach (var roleName in player.Roles)
        {
            if (!roles.TryGetValue(roleName, out var role))
                continue;

            foreach (var granted in role.Permissions)
            {
                if (PermissionMatches(granted, permission))
                    return true;
            }
        }

        return false;
    }

    public static string[] GetRoles(ulong steamId64)
    {
        var playerKey = SteamIds.ToPermissionKey(steamId64);

        lock (_lock)
        {
            return _players.TryGetValue(playerKey, out var player)
                ? [.. player.Roles]
                : [];
        }
    }

    internal static bool PermissionMatches(string granted, string requested)
    {
        granted = granted.Trim();
        requested = requested.Trim();

        if (granted.Length == 0 || requested.Length == 0)
            return false;

        if (granted == "*")
            return true;

        if (string.Equals(granted, requested, StringComparison.OrdinalIgnoreCase))
            return true;

        const string wildcardSuffix = ".*";
        if (!granted.EndsWith(wildcardSuffix, StringComparison.Ordinal))
            return false;

        var prefix = granted[..^1];
        return requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && requested.Length > prefix.Length;
    }

    private static Dictionary<string, T> LoadDictionary<T>(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, T>>(json, ReadOptions);
        return parsed != null
            ? new Dictionary<string, T>(parsed, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, PlayerDefinition> LoadPlayers(string path)
    {
        var loaded = LoadDictionary<PlayerDefinition>(path);
        var normalized = new Dictionary<string, PlayerDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, player) in loaded)
        {
            if (SteamIds.TryParse(key, out var steamId64))
                normalized[SteamIds.ToPermissionKey(steamId64)] = player;
            else
                normalized[key] = player;
        }

        return normalized;
    }

    private static void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(_permissionsDir);

        var rolesPath = Path.Combine(_permissionsDir, "roles.jsonc");
        if (!File.Exists(rolesPath))
        {
            File.WriteAllText(rolesPath,
                """
                {
                  "admin": {
                    "permissions": ["*"]
                  },
                  "moderator": {
                    "permissions": [
                      "admin.changemap",
                      "admin.kick",
                      "admin.generic",
                      "admin.chat"
                    ]
                  }
                }
                """ + Environment.NewLine);
        }

        var playersPath = Path.Combine(_permissionsDir, "players.jsonc");
        if (!File.Exists(playersPath))
        {
            File.WriteAllText(playersPath,
                """
                {
                  // Player keys can use any common Steam ID format. Deadworks normalizes
                  // each valid key to SteamID64 internally; use one key per player.
                  //
                  // These examples all identify the same player:
                  //
                  // SteamID64:
                  // "76561197960287930": {
                  //   "roles": ["admin"]
                  // },
                  //
                  // Steam2:
                  // "STEAM_0:0:11101": {
                  //   "roles": ["admin"]
                  // },
                  //
                  // Steam3:
                  // "[U:1:22202]": {
                  //   "roles": ["admin"]
                  // }
                }
                """ + Environment.NewLine);
        }
    }

}
