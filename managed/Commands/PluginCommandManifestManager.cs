using System.Text.Json;

namespace DeadworksManaged.Commands;

internal sealed class PluginCommandManifestSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string Description { get; init; } = "";
    public string Permission { get; init; } = "";
}

internal sealed class PluginCommandManifestEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string[]? Aliases { get; set; }
    public string? Description { get; set; }
    public string? Permission { get; set; }
}

internal sealed class PluginCommandManifest
{
    public string Plugin { get; set; } = "";
    public List<PluginCommandManifestEntry> Commands { get; set; } = [];
}

internal static class PluginCommandManifestManager
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string _commandsDir = "";

    private static readonly Dictionary<string, Dictionary<string, string>> LegacyDefaultPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin.kick"] = new(StringComparer.OrdinalIgnoreCase) { ["moderation.player.kick"] = "admin.kick" },
        ["admin.ban"] = new(StringComparer.OrdinalIgnoreCase) { ["moderation.player.ban"] = "admin.ban" },
        ["admin.unban"] = new(StringComparer.OrdinalIgnoreCase) { ["moderation.player.unban"] = "admin.unban" },
        ["admin.gag"] = new(StringComparer.OrdinalIgnoreCase) { ["admin.gag"] = "admin.chat" },
        ["admin.ungag"] = new(StringComparer.OrdinalIgnoreCase) { ["admin.ungag"] = "admin.chat" },
        ["admin.who"] = new(StringComparer.OrdinalIgnoreCase) { ["moderation.player.who"] = "admin.generic" },
        ["admin.map"] = new(StringComparer.OrdinalIgnoreCase) { ["server.map"] = "admin.changemap", ["admin.map"] = "admin.changemap" },
        ["admin.cvar"] = new(StringComparer.OrdinalIgnoreCase) { ["server.cvar"] = "admin.cvar" },
        ["admin.execcfg"] = new(StringComparer.OrdinalIgnoreCase) { ["server.config.exec"] = "admin.config" },
        ["admin.rcon"] = new(StringComparer.OrdinalIgnoreCase) { ["server.rcon"] = "admin.rcon" },
    };

    public static void Initialize()
    {
        var managedDir = Path.GetDirectoryName(typeof(PluginCommandManifestManager).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        Initialize(configsDir);
    }

    internal static void Initialize(string commandsDir)
    {
        _commandsDir = commandsDir;
    }

    public static IReadOnlyDictionary<string, PluginCommandManifestEntry> LoadOrCreate(
        string pluginKey,
        string pluginName,
        IReadOnlyCollection<PluginCommandManifestSource> sources)
    {
        var defaults = BuildDefaultEntries(sources);

        if (_commandsDir.Length == 0)
            Initialize();

        var pluginDir = Path.Combine(_commandsDir, MakeSafeFileName(pluginKey));
        Directory.CreateDirectory(pluginDir);

        var path = Path.Combine(pluginDir, $"{MakeSafeFileName(pluginKey)}.commands.jsonc");
        MigrateLegacyManifestPath(pluginKey, path);

        var manifest = ReadManifest(path) ?? new PluginCommandManifest { Plugin = pluginName };
        var changed = !File.Exists(path);

        if (string.IsNullOrWhiteSpace(manifest.Plugin))
        {
            manifest.Plugin = pluginName;
            changed = true;
        }

        var byId = new Dictionary<string, PluginCommandManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in manifest.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id))
                continue;

            if (byId.ContainsKey(command.Id))
            {
                Console.WriteLine($"[CommandManifest] Duplicate command id '{command.Id}' in {path}; using the first entry");
                continue;
            }

            byId[command.Id] = command;
        }

        foreach (var (id, defaultEntry) in defaults)
        {
            if (!byId.ContainsKey(id))
            {
                manifest.Commands.Add(Clone(defaultEntry));
                byId[id] = defaultEntry;
                changed = true;
            }
        }

        for (var i = manifest.Commands.Count - 1; i >= 0; i--)
        {
            var id = manifest.Commands[i].Id;
            if (string.IsNullOrWhiteSpace(id) || defaults.ContainsKey(id))
                continue;

            manifest.Commands.RemoveAt(i);
            byId.Remove(id);
            changed = true;
            Console.WriteLine($"[CommandManifest] Removed stale command id '{id}' from {path}");
        }

        var effective = new Dictionary<string, PluginCommandManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, defaultEntry) in defaults)
        {
            var configured = byId[id];
            if (TryMigrateLegacyDefaultPermission(id, configured, defaultEntry.Permission ?? "", out var migratedPermission))
            {
                configured.Permission = migratedPermission;
                changed = true;
            }

            var defaultName = defaultEntry.Name ?? id;
            var name = ValidateCommandToken(configured.Name, defaultName, path, id, "name");
            var aliases = ValidateAliases(configured.Aliases, name, path, id);

            if (!usedNames.Add(name))
            {
                Console.WriteLine($"[CommandManifest] Duplicate command name '{name}' in {path}; command '{id}' uses default '{defaultName}'");
                name = defaultName;
                aliases = ValidateAliases(defaultEntry.Aliases, name, path, id);
                usedNames.Add(name);
            }

            var distinctAliases = new List<string>();
            foreach (var alias in aliases)
            {
                if (!usedNames.Add(alias))
                {
                    Console.WriteLine($"[CommandManifest] Alias '{alias}' in {path} collides with another command; ignoring it for '{id}'");
                    continue;
                }
                distinctAliases.Add(alias);
            }

            effective[id] = new PluginCommandManifestEntry
            {
                Id = id,
                Name = name,
                Aliases = [.. distinctAliases],
                Description = configured.Description ?? defaultEntry.Description,
                Permission = configured.Permission ?? defaultEntry.Permission
            };
        }

        if (changed)
            WriteManifest(path, pluginName, effective.Values);

        return effective;
    }

    private static bool TryMigrateLegacyDefaultPermission(
        string id,
        PluginCommandManifestEntry configured,
        string defaultPermission,
        out string permission)
    {
        permission = configured.Permission ?? defaultPermission;

        if (configured.Permission == null)
            return false;

        if (!LegacyDefaultPermissions.TryGetValue(id, out var permissionMap))
            return false;

        if (!permissionMap.TryGetValue(configured.Permission, out var migrated))
            return false;

        if (!string.Equals(migrated, defaultPermission, StringComparison.OrdinalIgnoreCase))
            return false;

        permission = migrated;
        return true;
    }

    internal static string GetCommandId(string pluginName, string sourceName)
    {
        return $"{MakeIdSegment(pluginName)}.{MakeIdSegment(sourceName)}";
    }

    private static Dictionary<string, PluginCommandManifestEntry> BuildDefaultEntries(
        IReadOnlyCollection<PluginCommandManifestSource> sources)
    {
        var defaults = new Dictionary<string, PluginCommandManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (defaults.ContainsKey(source.Id))
            {
                Console.WriteLine($"[CommandManifest] Duplicate source command id '{source.Id}'; using the first source command");
                continue;
            }

            defaults[source.Id] = new PluginCommandManifestEntry
            {
                Id = source.Id,
                Name = source.Name,
                Aliases = source.Aliases,
                Description = source.Description,
                Permission = source.Permission
            };
        }
        return defaults;
    }

    private static PluginCommandManifest? ReadManifest(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginCommandManifest>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandManifest] Failed to parse {path}: {ex.Message}; using source command defaults");
            return null;
        }
    }

    private static void MigrateLegacyManifestPath(string pluginKey, string path)
    {
        if (File.Exists(path))
            return;

        var safePluginKey = MakeSafeFileName(pluginKey);
        var legacyPath = Path.Combine(_commandsDir, "commands", safePluginKey, $"{safePluginKey}.commands.jsonc");
        if (!File.Exists(legacyPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(legacyPath, path);
            Console.WriteLine($"[CommandManifest] Moved legacy command manifest from {legacyPath} to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandManifest] Failed to move legacy command manifest {legacyPath}: {ex.Message}");
        }
    }

    private static void WriteManifest(string path, string pluginName, IEnumerable<PluginCommandManifestEntry> commands)
    {
        try
        {
            var manifest = new PluginCommandManifest
            {
                Plugin = pluginName,
                Commands = commands
                    .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(Clone)
                    .ToList()
            };

            File.WriteAllText(path, JsonSerializer.Serialize(manifest, WriteOptions) + Environment.NewLine);
            Console.WriteLine($"[CommandManifest] Wrote command manifest for {pluginName}: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandManifest] Failed to write {path}: {ex.Message}");
        }
    }

    private static string ValidateCommandToken(
        string? configured,
        string fallback,
        string path,
        string id,
        string field)
    {
        if (IsValidCommandToken(configured))
            return configured!.Trim();

        if (!string.IsNullOrWhiteSpace(configured))
            Console.WriteLine($"[CommandManifest] Invalid {field} '{configured}' for '{id}' in {path}; using '{fallback}'");

        return fallback;
    }

    private static string[] ValidateAliases(string[]? aliases, string name, string path, string id)
    {
        if (aliases == null)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };

        foreach (var alias in aliases)
        {
            if (!IsValidCommandToken(alias))
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    Console.WriteLine($"[CommandManifest] Invalid alias '{alias}' for '{id}' in {path}; ignoring it");
                continue;
            }

            var trimmed = alias.Trim();
            if (!seen.Add(trimmed))
                continue;

            result.Add(trimmed);
        }

        return [.. result];
    }

    private static bool IsValidCommandToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c is '/' or '!' or '"')
                return false;
        }

        return true;
    }

    private static PluginCommandManifestEntry Clone(PluginCommandManifestEntry entry)
    {
        return new PluginCommandManifestEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Aliases = entry.Aliases != null ? [.. entry.Aliases] : [],
            Description = entry.Description,
            Permission = entry.Permission
        };
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length == 0 ? "plugin" : safe;
    }

    private static string MakeIdSegment(string value)
    {
        var chars = value
            .Trim()
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray();

        var segment = new string(chars).Trim('_');
        return segment.Length == 0 ? "command" : segment;
    }
}
