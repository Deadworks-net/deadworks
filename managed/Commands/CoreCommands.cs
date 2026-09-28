using DeadworksManaged.Api;

namespace DeadworksManaged.Commands;

/// <summary>
/// Deadworks' general console commands. Written as [Command]s like any plugin's, so they get permission checks,
/// overrides (as <c>Deadworks:&lt;name&gt;</c>) and a listing in <c>generated/deadworks.jsonc</c>.
/// </summary>
[DeclarePermission(AdminSystem.AdminActivityService.NotifyPermission, Description = "See which admin did something in admin-action announcements")]
internal sealed class CoreCommands : DeadworksPluginBase
{
    public override string Name => "Deadworks";

    [Command("reloadconfig", Description = "Reload plugin configs: reloadconfig [plugin]", Permission = "deadworks.config.reload", ConsoleOnly = true)]
    public void ReloadConfig(Caller caller, string plugin = "")
    {
        var matching = PluginLoader.PluginSnapshot
            .Where(p => plugin.Length == 0
                        || string.Equals(p.GetType().Name, plugin, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Name, plugin, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
            throw new CommandException($"There's no loaded plugin called '{plugin}'. Run dw_plugin list to see them.");

        AdminActivity.Log(caller, plugin.Length > 0 ? $"reloaded the config of {matching[0].Name}" : "reloaded plugin configs");
        foreach (var p in matching)
        {
            try
            {
                // Say what's wrong: whoever edited the file may not be able to see the server console.
                caller.PrintToConsole(p.ReloadConfig()
                    ? $"[ConfigManager] Reloaded config for {p.Name}"
                    : ConfigManager.HasConfig(p)
                        ? $"[ConfigManager] Failed to reload config for {p.Name}: {ConfigManager.LastError ?? "see the server console"}. It keeps its previous settings."
                        : $"[ConfigManager] {p.Name} has no config to reload");
            }
            catch (Exception ex)
            {
                caller.PrintToConsole($"[ConfigManager] Failed to reload config for {p.Name}: {ex.Message}");
            }
        }
    }

    [Command("plugin", Description = "Manage plugins: plugin <list|enable|disable|commands> [plugin]", Permission = "deadworks.plugins.manage", ConsoleOnly = true)]
    public void Plugin(Caller caller, string action = "list", string plugin = "")
    {
        // Only names from plugins/ or builtin/: anything else would be turned into a path and could load any DLL.
        if (plugin.Length > 0)
            plugin = PluginLoader.InstalledPluginNames().FirstOrDefault(n => n.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                     ?? throw new CommandException($"There's no plugin called '{plugin}'. Run dw_plugin list to see them.");

        switch (action.ToLowerInvariant())
        {
            case "list":
                var names = PluginLoader.InstalledPluginNames().ToList();
                if (names.Count == 0)
                {
                    caller.PrintToConsole("[PluginLoader] No plugins installed");
                    return;
                }
                caller.PrintToConsole("[PluginLoader] Installed plugins:");
                foreach (var name in names)
                {
                    var enabled = PluginStateManager.IsEnabled(name);
                    var loaded = PluginLoader.IsPluginLoaded(name);
                    var status = enabled ? (loaded ? "enabled (loaded)" : "enabled (not loaded)") : "disabled";
                    var origin = PluginLoader.IsBuiltin(name) ? " [ships with Deadworks]" : "";
                    caller.PrintToConsole($"  {name}: {status}{origin}");
                }
                return;

            case "enable" when plugin.Length > 0:
                AdminActivity.Log(caller, $"enabled the plugin {plugin}");
                PluginLoader.EnablePlugin(plugin);
                return;

            case "disable" when plugin.Length > 0:
                AdminActivity.Log(caller, $"disabled the plugin {plugin}");
                PluginLoader.DisablePlugin(plugin);
                return;

            case "commands" when plugin.Length > 0:
                ListPluginCommands(caller, plugin);
                return;

            default:
                throw new CommandException("Usage: dw_plugin <list|enable|disable|commands> [plugin]");
        }
    }

    [Command("help", Description = "List the commands you can use", ConsoleOnly = true)]
    public void Help(Caller caller)
    {
        if (!caller.IsConsole && caller.Player == null)
            return;
        var entries = PluginRegistrationTracker.GetAllEntries()
            .Where(e => !e.Hidden && (e.CanRun == null || e.CanRun(caller.Player)))
            .ToList();

        caller.PrintToConsole("Available commands:");
        var any = false;
        foreach (var (kind, heading) in new[] { ("command", "Console:"), ("chat", "Chat:") })
        {
            var list = entries.Where(e => e.Kind == kind).ToList();
            if (list.Count == 0)
                continue;
            any = true;
            caller.PrintToConsole(heading);
            // Each command once, under its first name, with its other names after it.
            var aliases = list.Where(e => e.AliasOf != null).ToLookup(e => e.AliasOf!, e => e.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var e in list.Where(e => e.AliasOf == null).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var also = aliases[e.Name].ToList();
                caller.PrintToConsole($"  {e.Name}{(also.Count > 0 ? $" (also {string.Join(", ", also)})" : "")}"
                                      + (string.IsNullOrEmpty(e.Description) ? "" : $" - {e.Description}"));
            }
        }
        if (!any)
            caller.PrintToConsole("  (none)");
    }

    private static void ListPluginCommands(Caller caller, string pluginName)
    {
        var normalizedPath = PluginLoader.ResolvePluginPath(pluginName);
        if (normalizedPath == null || !PluginLoader.IsPluginLoaded(pluginName))
            throw new CommandException($"[ConCommandManager] Plugin '{pluginName}' is not loaded");

        var entries = PluginRegistrationTracker.GetEntries(normalizedPath);
        if (entries.Count == 0)
        {
            caller.PrintToConsole($"[ConCommandManager] Plugin '{pluginName}' has no registered commands");
            return;
        }

        caller.PrintToConsole($"[ConCommandManager] Commands registered by '{pluginName}':");
        foreach (var entry in entries)
            caller.PrintToConsole($"  [{entry.Kind}] {entry.Name}{(string.IsNullOrEmpty(entry.Description) ? "" : $" - {entry.Description}")}");
    }
}
