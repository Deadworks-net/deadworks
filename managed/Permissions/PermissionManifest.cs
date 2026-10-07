using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace DeadworksManaged.PermissionSystem;

/// <summary>
/// Writes <c>configs/permissions/generated/&lt;Plugin&gt;.jsonc</c>: a read-only listing of each plugin's commands and
/// permissions, so server owners can set up roles without reading plugin source. Never read back.
/// </summary>
internal static class PermissionManifest
{
    internal sealed record CommandInfo(
        IReadOnlyList<string> Names, string Description, string DeclaredPermission, TargetImmunity TargetImmunity,
        bool ChatOnly, bool ConsoleOnly, bool ServerOnly, CommandOverrides.Owner? Owner = null);

    internal sealed record PluginInfo(
        string FileKey, CommandOverrides.Owner Owner, IReadOnlyList<CommandInfo> Commands, IReadOnlyList<DeclarePermissionAttribute> Declared)
    {
        public string PluginName => Owner.PluginName;
    }

    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, List<PluginInfo>> _byPluginPath = new(StringComparer.OrdinalIgnoreCase);
    private static string _dir = "";
    // Every declared permission, rebuilt when a plugin comes or goes or overrides.jsonc changes.
    private static (IReadOnlyList<string> Overrides, HashSet<string> Set)? _declared;

    public static void Initialize(string permissionsDir) => _dir = Path.Combine(permissionsDir, "generated");

    /// <summary>The file name core commands are listed under.</summary>
    public const string CoreFileKey = "deadworks";

    /// <summary>The owner of commands loaded from <paramref name="normalizedPath"/>; core's path has no DLL name.</summary>
    public static CommandOverrides.Owner OwnerOf(string normalizedPath, IDeadworksPlugin plugin)
        => new(plugin.Name, normalizedPath.Contains("://") ? "" : Path.GetFileNameWithoutExtension(normalizedPath));

    /// <summary>
    /// A plugin's file is named after its DLL, which is unique in plugins/; class names aren't (two plugins can both be
    /// called Plugin). Plugins loaded without a DLL (tests) use the class name. Core's name is reserved.
    /// </summary>
    private static string FileKeyFor(string normalizedPath, IDeadworksPlugin plugin)
    {
        var key = normalizedPath.Contains("://") ? plugin.GetType().Name : Path.GetFileNameWithoutExtension(normalizedPath);
        return key.Equals(CoreFileKey, StringComparison.OrdinalIgnoreCase) ? key + "-plugin" : key;
    }

    public static void Add(string normalizedPath, IDeadworksPlugin plugin, IReadOnlyList<CommandInfo> commands, string? fileKey = null)
    {
        var declared = plugin.GetType().GetCustomAttributes<DeclarePermissionAttribute>().ToList();
        var owner = OwnerOf(normalizedPath, plugin);
        var info = new PluginInfo(fileKey ?? FileKeyFor(normalizedPath, plugin), owner,
            commands.Select(c => c with { Owner = c.Owner ?? owner }).ToList(), declared);

        lock (_lock)
        {
            _declared = null;
            if (!_byPluginPath.TryGetValue(normalizedPath, out var list))
                _byPluginPath[normalizedPath] = list = [];
            // Several plugin classes can share one file (core's command classes do); list them together.
            var existing = list.FindIndex(p => p.FileKey == info.FileKey);
            if (existing >= 0)
            {
                var previous = list[existing];
                info = info with { Commands = [.. previous.Commands, .. info.Commands], Declared = [.. previous.Declared, .. info.Declared] };
                list[existing] = info;
            }
            else
            {
                list.Add(info);
            }
        }

        Write(info);
        WarnOnForeignPermissions(info);
    }

    /// <summary>Forgets a plugin on unload. Its file stays until the next startup, so hot reloads don't make it flicker.</summary>
    public static void Remove(string normalizedPath)
    {
        lock (_lock)
        {
            _byPluginPath.Remove(normalizedPath);
            _declared = null;
        }
    }

    /// <summary>
    /// Every permission something declares, normalized: on a command (as the plugin asks for it and after overrides),
    /// in <see cref="DeclarePermissionAttribute"/>, or in overrides.jsonc. Checks outside this set are probably typos.
    /// </summary>
    public static IReadOnlySet<string> DeclaredPermissions()
    {
        var overrides = CommandOverrides.Permissions;
        lock (_lock)
        {
            if (_declared is { } cached && ReferenceEquals(cached.Overrides, overrides))
                return cached.Set;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in Snapshot())
        {
            foreach (var c in info.Commands)
            {
                set.Add(PermissionEvaluator.Normalize(c.DeclaredPermission));
                set.Add(PermissionEvaluator.Normalize(CommandOverrides.Resolve(c.Names, c.DeclaredPermission, c.Owner ?? info.Owner, out _)));
            }
            foreach (var d in info.Declared)
                set.Add(PermissionEvaluator.Normalize(d.Permission));
        }
        foreach (var p in overrides)
            set.Add(PermissionEvaluator.Normalize(p));
        set.Remove("");

        lock (_lock)
            _declared = (overrides, set);
        return set;
    }

    /// <summary>
    /// Every registered command called <paramref name="name"/> (any of its names, without !, / or dw_), with the plugin
    /// it belongs to and the permission it needs after overrides.jsonc ("" for anyone).
    /// </summary>
    public static List<(string Plugin, string Command, string Permission)> FindCommand(string name)
    {
        name = name.Trim().TrimStart('!', '/');
        if (name.StartsWith("dw_", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        return Snapshot()
            .SelectMany(info => info.Commands
                .Where(c => c.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(c => (info.PluginName, c.Names[0], CommandOverrides.Resolve(c.Names, c.DeclaredPermission, c.Owner ?? info.Owner, out _))))
            .ToList();
    }

    /// <summary>Every registered command with its owner, for checking overrides.jsonc against.</summary>
    public static IReadOnlyCollection<(CommandOverrides.Owner Owner, IReadOnlyList<string> Names)> AllCommands()
        => Snapshot().SelectMany(info => info.Commands.Select(c => (c.Owner ?? info.Owner, c.Names))).ToList();

    /// <summary>Rewrites every file, e.g. after overrides.jsonc changed.</summary>
    public static void WriteAll()
    {
        foreach (var info in Snapshot())
            Write(info);
    }

    /// <summary>Deletes files of plugins that are no longer loaded. Called once after startup loading.</summary>
    public static void DeleteStale()
    {
        if (_dir.Length == 0 || !Directory.Exists(_dir))
            return;

        var live = Snapshot().Select(p => p.FileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(_dir, "*.jsonc"))
        {
            if (live.Contains(Path.GetFileNameWithoutExtension(file)))
                continue;
            try
            {
                File.Delete(file);
                Console.WriteLine($"[Permissions] Removed {Path.GetFileName(file)}: its plugin is not loaded");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Permissions] Failed to remove {file}: {ex.Message}");
            }
        }
    }

    private static List<PluginInfo> Snapshot()
    {
        lock (_lock)
            return _byPluginPath.Values.SelectMany(l => l).ToList();
    }

    private static void Write(PluginInfo info)
    {
        if (_dir.Length == 0)
            return;

        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"{MakeSafeFileName(info.FileKey)}.jsonc");
            var content = Render(info);

            // Skip identical content so hot reloads don't touch the file's timestamp.
            if (File.Exists(path) && File.ReadAllText(path) == content)
                return;

            JsonPermissionStore.AtomicWrite(path, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Permissions] Failed to write the permissions file for {info.PluginName}: {ex.Message}");
        }
    }

    // What each list entry serializes to. Nulls are left out, so declaredPermission only appears when overridden.
    private sealed record CommandEntry(
        string Name, IReadOnlyList<string> Aliases, string Description, string Permission, string? DeclaredPermission, TargetImmunity TargetImmunity);

    private sealed record PermissionEntry(string Tag, string Description, IReadOnlyList<string> DeclaredBy);

    private static readonly JsonSerializerOptions EntryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // People read this file, so keep apostrophes and non-ASCII names as typed rather than as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string DeclaredInCode = "[DeclarePermission]";

    internal static string Render(PluginInfo info)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("plugin", info.PluginName);

            writer.WriteStartArray("commands");
            foreach (var c in info.Commands)
            {
                var effective = CommandOverrides.Resolve(c.Names, c.DeclaredPermission, c.Owner ?? info.Owner, out var overridden);
                var immunity = c.TargetImmunity == TargetImmunity.Auto
                    ? (c.DeclaredPermission.Length > 0 ? TargetImmunity.Enforce : TargetImmunity.Ignore)
                    : c.TargetImmunity;

                var comments = new List<string> { $"{Invocations(c)}{(c.Description.Length > 0 ? $": {c.Description}" : "")}" };
                if (c.ServerOnly)
                    comments.Add("Server console only; players can never run it.");
                else if (effective.Length == 0)
                    comments.Add("Anyone can run it.");
                if (overridden)
                    comments.Add($"OVERRIDDEN in overrides.jsonc. The plugin asks for {(c.DeclaredPermission.Length > 0 ? $"\"{c.DeclaredPermission}\"" : "no permission")}.");

                WriteEntry(writer, new CommandEntry(
                    c.Names[0], c.Names.Skip(1).ToList(), c.Description, effective,
                    overridden ? c.DeclaredPermission : null, immunity), comments);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("permissions");
            foreach (var p in CollectPermissions(info))
            {
                var commands = p.DeclaredBy.Where(d => d != DeclaredInCode).ToList();
                var comment = commands.Count > 0 ? $"Required by: {string.Join(", ", commands)}" : "Checked in code";
                if (p.Description.Length > 0)
                    comment += $". {p.Description}";
                WriteEntry(writer, p, [comment]);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Header.Replace("{PLUGIN}", info.PluginName) + Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static List<PermissionEntry> CollectPermissions(PluginInfo info)
    {
        var byTag = new SortedDictionary<string, (string Description, List<string> DeclaredBy)>(StringComparer.Ordinal);
        foreach (var c in info.Commands.Where(c => c.DeclaredPermission.Length > 0))
        {
            var tag = PermissionEvaluator.Normalize(c.DeclaredPermission);
            if (!byTag.TryGetValue(tag, out var entry))
                byTag[tag] = entry = ("", []);
            entry.DeclaredBy.Add(c.Names[0]);
        }
        foreach (var d in info.Declared)
        {
            var tag = PermissionEvaluator.Normalize(d.Permission);
            var declaredBy = byTag.TryGetValue(tag, out var existing) ? existing.DeclaredBy : [];
            declaredBy.Add(DeclaredInCode);
            byTag[tag] = (d.Description, declaredBy);
        }
        return byTag.Select(kv => new PermissionEntry(kv.Key, kv.Value.Description, kv.Value.DeclaredBy)).ToList();
    }

    /// <summary>Serializes <paramref name="value"/> as an object whose first lines are <c>/* comment */</c>s.</summary>
    private static void WriteEntry<T>(Utf8JsonWriter writer, T value, IEnumerable<string> comments)
    {
        writer.WriteStartObject();
        foreach (var comment in comments)
            // A plugin's description could contain "*/", which would end the comment early.
            writer.WriteCommentValue($" {comment.ReplaceLineEndings(" ").Replace("*/", "* /")} ");
        foreach (var (name, node) in JsonSerializer.SerializeToNode(value, EntryOptions)!.AsObject())
        {
            writer.WritePropertyName(name);
            if (node == null)
                writer.WriteNullValue();
            else
                node.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static string Invocations(CommandInfo c)
    {
        var name = c.Names[0];
        var forms = new List<string>();
        if (!c.ConsoleOnly && !c.ServerOnly) forms.Add($"!{name} / /{name}");
        if (!c.ChatOnly) forms.Add($"dw_{name}");
        var aliases = c.Names.Count > 1 ? $" (aliases: {string.Join(", ", c.Names.Skip(1))})" : "";
        return string.Join(" / ", forms) + aliases;
    }

    private static void WarnOnForeignPermissions(PluginInfo info)
    {
        if (info.FileKey == CoreFileKey)
            return;

        var own = new string(info.PluginName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var tags = info.Commands.Select(c => c.DeclaredPermission).Concat(info.Declared.Select(d => d.Permission))
            .Where(p => p.Length > 0)
            .Select(PermissionEvaluator.Normalize)
            .Distinct();

        foreach (var tag in tags)
        {
            if (tag.StartsWith("deadworks.", StringComparison.Ordinal))
                Console.WriteLine($"[Permissions] {info.PluginName} uses '{tag}'; the deadworks.* namespace is reserved for core");
            else if (own.Length > 0 && !tag.StartsWith(own + ".", StringComparison.Ordinal))
                Console.WriteLine($"[Permissions] {info.PluginName} uses '{tag}'; by convention its permissions start with '{own}.'");
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "plugin" : safe;
    }

    private const string Header =
        """
        // ============================================================================
        //  AUTO-GENERATED. DO NOT EDIT. This file is rewritten every time the
        //  "{PLUGIN}" plugin loads. Any changes you make here will be lost.
        //
        //  It lists the commands and permissions this plugin declares, so you can
        //  set up access without reading the plugin's source.
        //
        //  To change who can do what, WITHOUT modifying the plugin:
        //
        //    Give a permission to a group of players
        //      -> configs/permissions/roles.jsonc
        //           "moderator": { "permissions": ["admin.moderation.kick"] }
        //         Wildcards work at dot boundaries: "admin.moderation.*" or "*".
        //
        //    Give a role or a single permission to one player
        //      -> configs/permissions/players.jsonc
        //           "76561197960287930": { "roles": ["moderator"],
        //                                  "permissions": ["admin.moderation.ban"] }
        //         or in the console: dw_role_grant <player> <role>
        //
        //    Take a permission away from one player
        //      -> prefix it with "-" in their players.jsonc entry: "-admin.moderation.ban"
        //         (a deny in a role only limits that role, not the player's other roles)
        //
        //    Change the permission a command requires, or make it public
        //      -> configs/permissions/overrides.jsonc
        //           "commands": { "ban": "my.custom.permission",   // remap
        //                         "{PLUGIN}:kick": "" }            // anyone can use this plugin's kick
        //
        //  Then run dw_perm_reload (or restart). Use dw_perm_check <player> <perm>
        //  to see which rule decided a result.
        // ============================================================================

        """;
}
