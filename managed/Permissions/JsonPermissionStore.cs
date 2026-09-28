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
        => new(e.Name, e.Roles is { Count: > 0 } ? e.Roles : null, e.Permissions is { Count: > 0 } ? e.Permissions : null, e.Immunity);

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
        var roles = Read<Dictionary<string, RoleDefinition>>(RolesPath, warn: true) ?? [];
        var players = ParsePlayers(Read<Dictionary<string, PlayerEntry>>(PlayersPath, warn: true) ?? [], warn: true);

        lock (_lock)
            _players = players;

        IReadOnlyDictionary<string, RoleDefinition> result = roles;
        return Task.FromResult(result);
    }

    private static Dictionary<ulong, PlayerEntry> ParsePlayers(Dictionary<string, PlayerEntry> raw, bool warn)
    {
        var players = new Dictionary<ulong, PlayerEntry>();
        foreach (var (key, entry) in raw)
        {
            if (!SteamIds.TryParse(key, out var steamId64))
            {
                if (warn)
                    Console.WriteLine($"[Permissions] players.jsonc: '{key}' is not a SteamID64, Steam2 or Steam3 ID; skipping");
                continue;
            }
            if (warn && players.ContainsKey(steamId64))
                Console.WriteLine($"[Permissions] players.jsonc: {key} is listed more than once; using the last entry");
            players[steamId64] = Clean(entry);
        }
        return players;
    }

    // Hand-edited files can say "roles": null; nothing downstream should have to cope with that.
    private static PlayerEntry Clean(PlayerEntry? e) => new()
    {
        Name = e?.Name,
        Roles = e?.Roles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [],
        Permissions = e?.Permissions?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [],
        Immunity = e?.Immunity
    };

    private static string Canonical(PlayerEntry? e) => e == null ? "" : JsonSerializer.Serialize(Trim(Clean(e)), WriteOptions);

    public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(_players.TryGetValue(steamId64, out var e) ? e.Clone() : null);
    }

    /// <summary>Every player in the file as last read, for checking grants against what plugins declare.</summary>
    internal List<(ulong Id, PlayerEntry Entry)> AllPlayers()
    {
        lock (_lock)
            return _players.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Changes one player's entry in <c>players.jsonc</c>. The file is read again first, so entries edited by hand since
    /// the last reload are kept as written; if it can't be read, nothing is written. Only the header comment survives.
    /// </summary>
    public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct)
    {
        lock (_lock)
        {
            Dictionary<string, PlayerEntry> current;
            try
            {
                current = Read<Dictionary<string, PlayerEntry>>(PlayersPath) ?? [];
            }
            catch (Exception ex)
            {
                return Task.FromException(new InvalidDataException(
                    $"players.jsonc has an error. Fix it and run dw_perm_reload. ({ex.Message})"));
            }

            // The change was worked out from this player's entry as of the last reload. If the file now says something
            // else for them, writing would silently undo that edit.
            var onDisk = ParsePlayers(current, warn: false).GetValueOrDefault(steamId64);
            if (Canonical(onDisk) != Canonical(_players.GetValueOrDefault(steamId64)))
                return Task.FromException(new InvalidDataException(
                    "players.jsonc was edited for this player since the last reload. Run dw_perm_reload, then try again."));

            // Keep every other entry under the key it was written with; this player's is rewritten as a SteamID64.
            var next = current
                .Where(kv => !(SteamIds.TryParse(kv.Key, out var id) && id == steamId64))
                .ToDictionary(kv => kv.Key, kv => kv.Value ?? new PlayerEntry());
            if (entry != null)
                next[steamId64.ToString()] = entry.Clone();

            try
            {
                AtomicWrite(PlayersPath, Render(PlayersHeader, next.ToDictionary(kv => kv.Key, kv => Trim(kv.Value))));
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            // Other players' hand edits take effect on dw_perm_reload, as usual; only this player's entry is new.
            var updated = new Dictionary<ulong, PlayerEntry>(_players);
            if (entry == null)
                updated.Remove(steamId64);
            else
                updated[steamId64] = Clean(entry);
            _players = updated;
        }
        return Task.CompletedTask;
    }

    /// <summary>A comment header, then <paramref name="value"/> as JSON.</summary>
    private static string Render<T>(string header, T value) => header + JsonSerializer.Serialize(value, WriteOptions) + "\n";

    /// <param name="warn">Report keys the file has but <typeparamref name="T"/> doesn't; only on a real load, not before each save.</param>
    private static T? Read<T>(string path, bool warn = false) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var text = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(text, ReadOptions);
            if (warn)
                UnknownJsonKeys.Warn(text, typeof(T), Path.GetFileName(path), "[Permissions] WARNING:");
            return value;
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
        // "permissions" takes exact permissions ("admin.moderation.ban"), wildcards that stop at dots
        // ("admin.moderation.*", or "*" for everything), and denies with a leading "-" ("-admin.moderation.ban").
        // Within one role the most specific grant wins, so ["*", "-admin.moderation.ban"] is everything except banning.
        //
        // "inherits" pulls in other roles. A role's own permissions beat what it inherits, so a role can undo a
        // deny from a role it inherits. "immunity" stops lower-immunity players from targeting holders of this
        // role with commands like kick or ban; without one, a role has the highest immunity of the roles it inherits.
        //
        // A player has a permission if any of their roles gives it: a deny in one role never takes away what another
        // role gives. To take something away from one player, put the deny in their players.jsonc entry.
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
        //     "name": "wisp",                            // just a note
        //     "roles": ["admin"],
        //     "permissions": ["-admin.moderation.ban"],  // checked before any role, so this beats them
        //     "immunity": 90                             // replaces the roles' immunity
        //   }
        //
        // dw_role_grant, dw_role_revoke, dw_perm_grant and dw_perm_revoke rewrite this file: they keep your
        // entries but not your comments (only this header is kept). If the file has an error they leave it alone.
        // Run dw_perm_reload after editing by hand.

        """;
}
