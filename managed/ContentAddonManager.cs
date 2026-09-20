using DeadworksManaged.Api;

namespace DeadworksManaged;

/// <summary>
/// Owns the content addon set clients are asked to download: the static
/// <c>serverbrowser.content_addons</c> config list plus whatever loaded plugins declare through
/// <see cref="IDeadworksPlugin.ContentAddons"/>. Re-evaluated whenever plugins load or unload,
/// so a plugin can bring its own addons without them being configured ahead of time.
/// </summary>
internal static class ContentAddonManager
{
    private static readonly Lock _lock = new();
    private static readonly HashSet<string> _mounted = new(StringComparer.OrdinalIgnoreCase);

    private static Func<IReadOnlyList<IDeadworksPlugin>> _pluginSource = () => [];
    private static string[] _active = [];
    private static bool _applied;

    /// <summary>The merged addon list currently advertised to clients.</summary>
    public static IReadOnlyList<string> Active
    {
        get { lock (_lock) return _active; }
    }

    public static void Initialize(Func<IReadOnlyList<IDeadworksPlugin>> pluginSource)
    {
        _pluginSource = pluginSource;
        ContentAddons.ResolveActive = () => Active;
        ContentAddons.RequestRefresh = Refresh;

        // Applies the config list on its own; plugins refresh it as they load.
        Update(remount: false);
    }

    /// <summary>Re-reads every plugin's declared addons and applies the merged list if it changed.</summary>
    public static void Refresh() => Update(remount: false);

    /// <summary>Unconditionally re-applies the merged list and re-mounts its VPKs on map load.</summary>
    public static void OnStartupServer() => Update(remount: true);

    /// <summary>
    /// Re-reads <c>deadworks.jsonc</c> from disk and applies the merged list. This is how an edit made
    /// outside the process (the hosting portal, SFTP) becomes live without a restart. Returns false if
    /// the file failed to parse; the previous list stays in effect.
    /// </summary>
    public static bool Reload()
    {
        if (!DeadworksConfig.Reload())
            return false;
        Update(remount: false);
        return true;
    }

    /// <summary>One row of <see cref="Describe"/>: where an addon came from and whether its VPK is mounted.</summary>
    internal readonly record struct AddonStatus(string Name, string Source, bool Mounted);

    /// <summary>The merged list with, for each addon, its first source ("config" or a plugin name) and mount state.</summary>
    internal static IReadOnlyList<AddonStatus> Describe()
    {
        lock (_lock)
        {
            var rows = new List<AddonStatus>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(string? addon, string source)
            {
                var name = addon?.Trim();
                if (string.IsNullOrEmpty(name) || name.Contains(',') || !seen.Add(name))
                    return;
                rows.Add(new AddonStatus(name, source, _mounted.Contains(name)));
            }

            foreach (var addon in DeadworksConfig.ServerBrowser.ContentAddons)
                Visit(addon, "config");

            foreach (var plugin in _pluginSource())
            {
                IReadOnlyList<string>? declared;
                try { declared = plugin.ContentAddons; }
                catch { continue; }
                foreach (var addon in declared ?? [])
                    Visit(addon, plugin.Name);
            }

            return rows;
        }
    }

    /// <summary>
    /// Adds an addon to the config list, saves <c>deadworks.jsonc</c>, and applies the merged list.
    /// Returns a message for the console; <paramref name="changed"/> is false when nothing was written
    /// (invalid name, already listed, or the save failed).
    /// </summary>
    public static string AddConfigured(string addon, out bool changed)
    {
        changed = false;
        if (!TryValidateName(addon, out var name, out var error))
            return $"[ContentAddons] Not added: {error}";

        var list = DeadworksConfig.ServerBrowser.ContentAddons;
        if (list.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"[ContentAddons] '{name}' is already in content_addons.";

        list.Add(name);
        if (!DeadworksConfig.Save())
        {
            list.Remove(name);
            return $"[ContentAddons] Not added: could not write {DeadworksConfig.ConfigPath}.";
        }

        changed = true;
        Update(remount: false);
        return $"[ContentAddons] Added '{name}' to content_addons. Clients connecting from now on will download it.";
    }

    /// <summary>
    /// Removes an addon from the config list, saves, and applies the merged list. An addon a loaded
    /// plugin still declares stays active. A VPK that is already mounted stays mounted until the next
    /// map load; the engine has no unmount, but clients are no longer told to download it.
    /// </summary>
    public static string RemoveConfigured(string addon, out bool changed)
    {
        changed = false;
        var name = addon?.Trim() ?? "";
        var list = DeadworksConfig.ServerBrowser.ContentAddons;
        var index = list.FindIndex(a => string.Equals(a?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (name.Length == 0 || index < 0)
            return $"[ContentAddons] '{name}' is not in content_addons.";

        var removed = list[index];
        list.RemoveAt(index);
        if (!DeadworksConfig.Save())
        {
            list.Insert(index, removed);
            return $"[ContentAddons] Not removed: could not write {DeadworksConfig.ConfigPath}.";
        }

        changed = true;
        Update(remount: false);
        var stillDeclared = Active.Contains(name, StringComparer.OrdinalIgnoreCase);
        return stillDeclared
            ? $"[ContentAddons] Removed '{name}' from content_addons, but a loaded plugin still declares it so it stays active."
            : $"[ContentAddons] Removed '{name}' from content_addons. Clients are no longer asked to download it.";
    }

    /// <summary>
    /// Validates an addon name the way both the engine and the launcher need it: one token with no
    /// comma (the engine joins the list with commas), no path or shell characters (the launcher writes
    /// <c>&lt;name&gt;.vpk</c> to disk), and no <c>.vpk</c> suffix (the name is the VPK's stem).
    /// </summary>
    internal static bool TryValidateName(string? input, out string name, out string error)
    {
        name = input?.Trim() ?? "";
        error = "";

        if (name.Length == 0) { error = "addon name is empty."; return false; }
        if (name.Length > 128) { error = "addon name is longer than 128 characters."; return false; }
        if (name is "." or "..") { error = $"'{name}' is not a valid addon name."; return false; }
        if (name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            error = $"use the name without the .vpk extension ('{name[..^4]}').";
            return false;
        }
        if (name.Contains(','))
        {
            error = "addon names cannot contain ','.";
            return false;
        }
        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\:*?\"<>|".Contains(c)))
        {
            error = $"'{name}' contains whitespace or a character not allowed in a file name.";
            return false;
        }
        return true;
    }

    private static void Update(bool remount)
    {
        lock (_lock)
        {
            var merged = Collect();
            if (!remount && merged.SequenceEqual(_active, StringComparer.OrdinalIgnoreCase))
                return;

            _active = merged;

            // Nothing to advertise and nothing ever advertised: no need to tell the engine anything.
            if (merged.Length == 0 && !_applied)
                return;

            _applied = true;
            Server.SetAddons(string.Join(',', merged));
            Console.WriteLine(merged.Length > 0
                ? $"[ContentAddons] Clients will download: {string.Join(", ", merged)}"
                : "[ContentAddons] No content addons registered.");

            if (remount)
                _mounted.Clear();

            foreach (var addon in merged)
                Mount(addon);
        }
    }

    private static string[] Collect()
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var addon in DeadworksConfig.ServerBrowser.ContentAddons)
            Add(merged, seen, addon, "config");

        foreach (var plugin in _pluginSource())
        {
            IReadOnlyList<string>? declared;
            try
            {
                declared = plugin.ContentAddons;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContentAddons] {plugin.Name}.ContentAddons threw: {ex.Message}");
                continue;
            }

            if (declared == null)
                continue;

            foreach (var addon in declared)
                Add(merged, seen, addon, plugin.Name);
        }

        return [.. merged];
    }

    private static void Add(List<string> merged, HashSet<string> seen, string? addon, string source)
    {
        var name = addon?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        // The engine takes the addons as one comma-separated string, so a comma would split the entry.
        if (name.Contains(','))
        {
            Console.WriteLine($"[ContentAddons] Ignoring '{name}' from {source}: addon names cannot contain ','.");
            return;
        }

        if (seen.Add(name))
            merged.Add(name);
    }

    private static void Mount(string addon)
    {
        if (!_mounted.Add(addon))
            return;

        var vpkPath = $"deadworks_mods/vpks/{addon}.vpk";
        if (Server.AddSearchPath(vpkPath))
            Console.WriteLine($"[ContentAddons] Mounted: {vpkPath}");
        else
            Console.WriteLine($"[ContentAddons] Failed to mount: {vpkPath}");
    }
}
