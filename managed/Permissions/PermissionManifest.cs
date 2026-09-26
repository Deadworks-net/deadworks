using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        bool ChatOnly, bool ConsoleOnly, bool ServerOnly);

    internal sealed record PluginInfo(
        string FileKey, string PluginName, IReadOnlyList<CommandInfo> Commands, IReadOnlyList<DeclarePermissionAttribute> Declared);

    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, List<PluginInfo>> _byPluginPath = new(StringComparer.OrdinalIgnoreCase);
    private static string _dir = "";

    public static void Initialize(string permissionsDir) => _dir = Path.Combine(permissionsDir, "generated");

    /// <summary>The file name core commands are listed under.</summary>
    public const string CoreFileKey = "deadworks";

    public static void Add(string normalizedPath, IDeadworksPlugin plugin, IReadOnlyList<CommandInfo> commands, string? fileKey = null)
    {
        var declared = plugin.GetType().GetCustomAttributes<DeclarePermissionAttribute>().ToList();
        var info = new PluginInfo(fileKey ?? plugin.GetType().Name, plugin.Name, commands, declared);

        lock (_lock)
        {
            if (!_byPluginPath.TryGetValue(normalizedPath, out var list))
                _byPluginPath[normalizedPath] = list = [];
            list.RemoveAll(p => p.FileKey == info.FileKey);
            list.Add(info);
        }

        Write(info);
        WarnOnForeignPermissions(info);
    }

    /// <summary>Forgets a plugin on unload. Its file stays until the next startup, so hot reloads don't make it flicker.</summary>
    public static void Remove(string normalizedPath)
    {
        lock (_lock)
            _byPluginPath.Remove(normalizedPath);
    }

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

    internal static string Render(PluginInfo info)
    {
        var sb = new StringBuilder();
        sb.Append(Header.Replace("{PLUGIN}", info.PluginName));
        sb.Append("{\n");
        sb.Append($"  \"plugin\": {Str(info.PluginName)},\n\n");

        // --- commands ---
        sb.Append("  \"commands\": [");
        for (int i = 0; i < info.Commands.Count; i++)
        {
            var c = info.Commands[i];
            var effective = CommandOverrides.Resolve(c.Names, c.DeclaredPermission, out var overridden);
            var immunity = c.TargetImmunity == TargetImmunity.Auto
                ? (effective.Length > 0 ? TargetImmunity.Enforce : TargetImmunity.Ignore)
                : c.TargetImmunity;

            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append($"    // {Invocations(c)}{(c.Description.Length > 0 ? $": {OneLine(c.Description)}" : "")}\n");
            if (c.ServerOnly)
                sb.Append("    // Server console only; players can never run it.\n");
            else if (effective.Length == 0)
                sb.Append("    // Anyone can run it.\n");
            if (overridden)
                sb.Append($"    // OVERRIDDEN in overrides.jsonc. The plugin asks for {(c.DeclaredPermission.Length > 0 ? $"\"{c.DeclaredPermission}\"" : "no permission")}.\n");

            sb.Append("    {\n");
            sb.Append($"      \"name\": {Str(c.Names[0])},\n");
            sb.Append($"      \"aliases\": [{string.Join(", ", c.Names.Skip(1).Select(Str))}],\n");
            sb.Append($"      \"description\": {Str(c.Description)},\n");
            sb.Append($"      \"permission\": {Str(effective)},\n");
            if (overridden)
                sb.Append($"      \"declaredPermission\": {Str(c.DeclaredPermission)},\n");
            sb.Append($"      \"targetImmunity\": {Str(immunity.ToString())}\n");
            sb.Append("    }");
        }
        sb.Append(info.Commands.Count > 0 ? "\n  ],\n\n" : "],\n\n");

        // --- permissions ---
        var permissions = new SortedDictionary<string, (string Description, List<string> DeclaredBy)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in info.Commands.Where(c => c.DeclaredPermission.Length > 0))
        {
            var key = PermissionEvaluator.Normalize(c.DeclaredPermission);
            if (!permissions.TryGetValue(key, out var entry))
                permissions[key] = entry = ("", []);
            entry.DeclaredBy.Add(c.Names[0]);
        }
        foreach (var d in info.Declared)
        {
            var key = PermissionEvaluator.Normalize(d.Permission);
            var declaredBy = permissions.TryGetValue(key, out var existing) ? existing.DeclaredBy : [];
            declaredBy.Add("[DeclarePermission]");
            permissions[key] = (d.Description, declaredBy);
        }

        sb.Append("  \"permissions\": [");
        var n = 0;
        foreach (var (tag, (description, declaredBy)) in permissions)
        {
            sb.Append(n++ == 0 ? "\n" : ",\n");
            var commands = declaredBy.Where(d => d != "[DeclarePermission]").ToList();
            var parts = new List<string>();
            if (commands.Count > 0) parts.Add($"Required by: {string.Join(", ", commands)}");
            if (commands.Count == 0) parts.Add("Checked in code");
            if (description.Length > 0) parts.Add(OneLine(description));
            sb.Append($"    // {string.Join(". ", parts)}\n");
            sb.Append($"    {{ \"tag\": {Str(tag)}, \"description\": {Str(description)}, \"declaredBy\": [{string.Join(", ", declaredBy.Select(Str))}] }}");
        }
        sb.Append(n > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        return sb.ToString();
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

    // People read this file, so keep apostrophes and non-ASCII names as typed rather than as \u escapes.
    private static readonly JsonSerializerOptions StrOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Str(string value) => JsonSerializer.Serialize(value, StrOptions);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

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
        //           "moderator": { "permissions": ["moderation.player.kick"] }
        //         Wildcards work at dot boundaries: "moderation.player.*" or "*".
        //
        //    Give a role or a single permission to one player
        //      -> configs/permissions/players.jsonc
        //           "76561197960287930": { "roles": ["moderator"],
        //                                  "permissions": ["moderation.player.ban"] }
        //         or in the console: dw_role_grant <player> <role>
        //
        //    Take a permission away (beats a wildcard)
        //      -> prefix it with "-" in either file: "-moderation.player.ban"
        //
        //    Change the permission a command requires, or make it public
        //      -> configs/permissions/overrides.jsonc
        //           "commands": { "ban": "my.custom.permission",   // remap
        //                         "kick": "" }                     // anyone can use it
        //
        //  Then run dw_perm_reload (or restart). Use dw_perm_check <player> <perm>
        //  to see which rule decided a result.
        // ============================================================================

        """;
}
