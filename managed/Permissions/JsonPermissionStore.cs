using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace DeadworksManaged.PermissionSystem;

/// <summary>The built-in store: <c>roles.jsonc</c> and <c>players.jsonc</c> in <c>configs/permissions/</c>.</summary>
internal sealed class JsonPermissionStore : IPermissionStore
{
    public const string StoreName = "json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // How entries are written: empty lists and unset immunity are left out, so they stay as short as hand-written ones.
    private sealed record StoredRole(List<string> Permissions, List<string>? Inherits, int? Immunity);
    private sealed record StoredPlayer(string? Name, List<string>? Roles, List<string>? Permissions, int? Immunity);

    private static StoredRole Trim(RoleDefinition r) => new(r.Permissions, r.Inherits.Count > 0 ? r.Inherits : null, r.Immunity);
    private static StoredPlayer Trim(PlayerEntry e)
        => new(e.Name, e.Roles.Count > 0 ? e.Roles : null, e.Permissions.Count > 0 ? e.Permissions : null, e.Immunity);

    private static readonly Dictionary<string, RoleDefinition> DefaultRoles = new()
    {
        ["default"] = new RoleDefinition(),
        ["admin"] = new RoleDefinition { Permissions = ["*"], Immunity = 100 }
    };

    private readonly string _dir;
    private readonly Lock _lock = new();
    private Dictionary<ulong, PlayerEntry> _players = [];

    public JsonPermissionStore(string dir) => _dir = dir;

    public string RolesPath => Path.Combine(_dir, "roles.jsonc");
    public string PlayersPath => Path.Combine(_dir, "players.jsonc");

    public event Action<ulong?>? Changed { add { } remove { } }

    public void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(_dir);
        if (!File.Exists(RolesPath))
            File.WriteAllText(RolesPath, Render(RolesHeader, DefaultRoles.ToDictionary(kv => kv.Key, kv => Trim(kv.Value))));
        if (!File.Exists(PlayersPath))
            File.WriteAllText(PlayersPath, Render(PlayersHeader, new Dictionary<string, StoredPlayer>()));
    }

    /// <summary>Re-reads both files. Players are read here too, so one reload sees one consistent pair.</summary>
    public Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct)
    {
        var roles = Read<Dictionary<string, RoleDefinition>>(RolesPath) ?? [];
        var rawPlayers = Read<Dictionary<string, PlayerEntry>>(PlayersPath) ?? [];

        var players = new Dictionary<ulong, PlayerEntry>();
        foreach (var (key, entry) in rawPlayers)
        {
            if (!SteamIds.TryParse(key, out var steamId64))
            {
                Console.WriteLine($"[Permissions] players.jsonc: '{key}' is not a SteamID64, Steam2 or Steam3 ID; skipping");
                continue;
            }
            if (players.ContainsKey(steamId64))
                Console.WriteLine($"[Permissions] players.jsonc: {key} is listed more than once; using the last entry");
            players[steamId64] = entry;
        }

        lock (_lock)
            _players = players;

        IReadOnlyDictionary<string, RoleDefinition> result = new Dictionary<string, RoleDefinition>(roles, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }

    public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(_players.TryGetValue(steamId64, out var e) ? e.Clone() : null);
    }

    /// <summary>Rewrites <c>players.jsonc</c>. Only the header comment survives; the file says so.</summary>
    public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct)
    {
        Dictionary<ulong, PlayerEntry> snapshot;
        lock (_lock)
        {
            if (entry == null)
                _players.Remove(steamId64);
            else
                _players[steamId64] = entry.Clone();
            snapshot = new(_players);
        }

        AtomicWrite(PlayersPath, Render(PlayersHeader, snapshot.ToDictionary(kv => kv.Key.ToString(), kv => Trim(kv.Value))));
        return Task.CompletedTask;
    }

    /// <summary>A comment header, then <paramref name="value"/> as JSON.</summary>
    private static string Render<T>(string header, T value) => header + JsonSerializer.Serialize(value, WriteOptions) + "\n";

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    internal static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private const string RolesHeader =
        """
        // Roles for the Deadworks permission system.
        //
        // "permissions" takes exact permissions ("moderation.player.ban"), wildcards that stop at dots
        // ("moderation.player.*", or "*" for everything), and denies with a leading "-" ("-moderation.player.ban").
        // The most specific grant wins, so ["*", "-moderation.player.ban"] is everything except banning.
        //
        // "inherits" pulls in other roles' permissions. "immunity" stops lower-immunity players from targeting
        // holders of this role with commands like kick or ban.
        //
        // "default" applies to every player, listed in players.jsonc or not.
        //
        // Every plugin's permissions are listed in generated/<Plugin>.jsonc.
        // Run dw_perm_reload after editing.

        """;

    private const string PlayersHeader =
        """
        // Players and the roles they hold. Keys can be SteamID64, Steam2 or Steam3 IDs.
        //
        //   "76561197960287930": {
        //     "name": "wisp",                          // just a note
        //     "roles": ["admin"],
        //     "permissions": ["-moderation.player.ban"], // on top of the roles; beats them on a tie
        //     "immunity": 90                           // replaces the roles' immunity
        //   }
        //
        // dw_role_grant, dw_role_revoke, dw_perm_grant and dw_perm_revoke rewrite this file,
        // and only this header is kept. Run dw_perm_reload after editing by hand.

        """;
}
