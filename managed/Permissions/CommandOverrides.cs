using System.Text.Json;

namespace DeadworksManaged.PermissionSystem;

/// <summary><c>overrides.jsonc</c>: the server owner changing the permission a command requires.</summary>
internal static class CommandOverrides
{
    /// <summary>Which plugin a command belongs to: its display name and its DLL name, either of which may qualify an override.</summary>
    internal readonly record struct Owner(string PluginName, string DllName)
    {
        public bool Is(string name)
            => name.Equals(PluginName, StringComparison.OrdinalIgnoreCase) || name.Equals(DllName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OverridesFile
    {
        public Dictionary<string, string> Commands { get; set; } = [];
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Keyed by the entry as normalized: "cmd" or "plugin:cmd", prefixes stripped from the command part.
    private static volatile Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);
    private static volatile IReadOnlyList<string> _permissions = [];

    private static bool _loadedOnce;

    /// <summary>
    /// True when overrides.jsonc couldn't be read and there is no earlier good copy. Any command's permission might
    /// have been changed there, so players can't run commands until it's fixed; the console still can.
    /// </summary>
    public static bool Unreadable { get; private set; }

    /// <summary>Why overrides.jsonc last failed to load, or null if it loaded.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Reads overrides.jsonc. Returns false if it has an error, keeping the previous overrides if there are any.</summary>
    public static bool Load(string path)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, Header + JsonSerializer.Serialize(new OverridesFile(), WriteOptions) + "\n");

        try
        {
            var text = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<OverridesFile>(text, ReadOptions);
            UnknownJsonKeys.Warn(text, typeof(OverridesFile), Path.GetFileName(path), "[Permissions] WARNING:");
            Set(parsed?.Commands ?? []);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = JsonErrors.Describe(Path.GetFileName(path), ex);
            if (_loadedOnce)
            {
                Console.WriteLine($"[Permissions] Failed to parse {LastError}. Keeping the previous overrides.");
            }
            else
            {
                Unreadable = true;
                Console.WriteLine($"[Permissions] ERROR: failed to parse {LastError}. Players can't run any "
                                  + "commands until it's fixed and dw_perm_reload is run; the server console still can.");
            }
            return false;
        }
    }

    internal static void ResetForTests()
    {
        _commands = new(StringComparer.OrdinalIgnoreCase);
        _permissions = [];
        _loadedOnce = false;
        Unreadable = false;
    }

    internal static void Set(Dictionary<string, string> commands)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, permission) in commands)
            normalized[NormalizeKey(key)] = permission?.Trim() ?? "";
        _commands = normalized;
        _permissions = normalized.Values.Where(p => p.Length > 0).ToList();
        _loadedOnce = true;
        Unreadable = false;
    }

    /// <summary>Every permission an override asks for, so they count as declared. A new list whenever overrides change.</summary>
    public static IReadOnlyList<string> Permissions => _permissions;

    /// <summary>
    /// The permission a command requires after overrides. An entry naming the command's plugin beats a bare one;
    /// among several names (aliases), the first listed wins.
    /// </summary>
    public static string Resolve(IReadOnlyList<string> names, string declared, Owner owner, out bool overridden)
    {
        var commands = _commands;
        foreach (var qualifier in new[] { owner.PluginName, owner.DllName })
        {
            if (qualifier.Length == 0)
                continue;
            foreach (var name in names)
            {
                if (commands.TryGetValue($"{qualifier}:{name}", out var permission))
                {
                    overridden = true;
                    return permission;
                }
            }
        }
        foreach (var name in names)
        {
            if (commands.TryGetValue(name, out var permission))
            {
                overridden = true;
                return permission;
            }
        }
        overridden = false;
        return declared;
    }

    /// <summary>Entries that match none of <paramref name="commands"/>, as normalized.</summary>
    public static IEnumerable<string> UnknownKeys(IReadOnlyCollection<(Owner Owner, IReadOnlyList<string> Names)> commands)
    {
        foreach (var key in _commands.Keys)
        {
            var colon = key.IndexOf(':');
            var plugin = colon >= 0 ? key[..colon] : null;
            var name = colon >= 0 ? key[(colon + 1)..] : key;
            var matches = commands.Any(c =>
                (plugin == null || c.Owner.Is(plugin)) && c.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (!matches)
                yield return key;
        }
    }

    // Owners may write the name the way they type it; "dw_ban", "!ban" and "/ban" all mean "ban".
    private static string NormalizeKey(string key)
    {
        key = key.Trim();
        var colon = key.IndexOf(':');
        return colon >= 0 ? $"{key[..colon].Trim()}:{TrimPrefix(key[(colon + 1)..])}" : TrimPrefix(key);
    }

    private static string TrimPrefix(string name)
    {
        name = name.Trim();
        if (name.StartsWith('!') || name.StartsWith('/'))
            return name[1..];
        if (name.StartsWith("dw_", StringComparison.OrdinalIgnoreCase))
            return name[3..];
        return name;
    }

    private const string Header =
        """
        // Change the permission a plugin's command requires, without modifying the plugin.
        //
        //   "commands": {
        //     "rtd":        "",              // make the command public, in every plugin that has one
        //     "ban":        "custom.bans",   // require a different permission
        //     "Admin:kick": "custom.kick"    // only the Admin plugin's kick; beats a plain "kick" entry
        //   }
        //
        // Use the command's name without "!", "/" or "dw_". One entry covers all of the command's aliases.
        // To pick one plugin, write "<Plugin>:<command>" with the "plugin" name at the top of its generated file, or its DLL name.
        // Built-in commands belong to "Deadworks", e.g. "Deadworks:plugin".
        // generated/<Plugin>.jsonc lists every command and shows which ones are overridden.
        // Run dw_perm_reload after editing.

        """;
}
