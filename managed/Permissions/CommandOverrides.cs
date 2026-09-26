using System.Text.Json;

namespace DeadworksManaged.PermissionSystem;

/// <summary><c>overrides.jsonc</c>: the server owner changing the permission a command requires.</summary>
internal static class CommandOverrides
{
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

    private static volatile Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string path)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, Header + JsonSerializer.Serialize(new OverridesFile(), WriteOptions) + "\n");

        try
        {
            var parsed = JsonSerializer.Deserialize<OverridesFile>(File.ReadAllText(path), ReadOptions);
            var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, permission) in parsed?.Commands ?? [])
                commands[TrimPrefix(name)] = permission?.Trim() ?? "";
            _commands = commands;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Permissions] Failed to parse {Path.GetFileName(path)}, keeping the previous overrides: {ex.Message}");
        }
    }

    internal static void Set(Dictionary<string, string> commands)
        => _commands = new Dictionary<string, string>(commands, StringComparer.OrdinalIgnoreCase);

    /// <summary>The permission a command requires after overrides. Any of its names may be overridden; the first listed wins.</summary>
    public static string Resolve(IReadOnlyList<string> names, string declared, out bool overridden)
    {
        var commands = _commands;
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

    // Owners may write the name the way they type it; "dw_ban", "!ban" and "/ban" all mean "ban".
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
        //     "rcon": "server.rcon",   // require a permission the plugin didn't ask for
        //     "rtd":  "",              // make the command public
        //     "ban":  "custom.bans"    // require a different permission
        //   }
        //
        // Use the command's name without "!", "/" or "dw_". One entry covers all of the command's aliases.
        // generated/<Plugin>.jsonc lists every command and shows which ones are overridden.
        // Run dw_perm_reload after editing.

        """;
}
