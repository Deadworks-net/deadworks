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
