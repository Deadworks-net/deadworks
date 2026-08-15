using System.Reflection;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal static class ConCommandManager
{
    // command name -> list of handlers
    private static readonly Dictionary<string, List<Action<ConCommandContext>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    // plugin path -> list of (name, handler) for cleanup
    private static readonly Dictionary<string, List<(string name, Action<ConCommandContext> handler)>> _pluginHandlers = new(StringComparer.OrdinalIgnoreCase);
    // convar name -> (plugin, PropertyInfo) for get/set
    private static readonly Dictionary<string, (IDeadworksPlugin plugin, PropertyInfo prop)> _conVars = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock _lock = new();
    private static ILogger _logger = null!;

    public static void Initialize()
    {
        _logger = DeadworksTelemetry.CreateLogger("ConCommandManager");
        RegisterBuiltInCommand("dw_reloadconfig", "Reload plugin configs. Usage: dw_reloadconfig [PluginName]", true, OnReloadConfig);
        RegisterBuiltInCommand("dw_plugin", "Manage plugins. Usage: dw_plugin <list|enable|disable|commands> [PluginName]", true, OnPluginCommand);
        RegisterBuiltInCommand("dw_help", "List all available commands.", false, OnHelp);
    }

    private static void OnReloadConfig(ConCommandContext ctx)
    {
        var targetName = ctx.Args.Length > 1 ? ctx.Args[1] : null;

        var plugins = PluginLoader.PluginSnapshot;
        foreach (var plugin in plugins)
        {
            if (targetName != null
                && !string.Equals(plugin.GetType().Name, targetName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(plugin.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (plugin.ReloadConfig())
                    _logger.LogInformation("Reloaded config for {PluginName}", plugin.Name);
                else
                    _logger.LogDebug("No config to reload for {PluginName}", plugin.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload config for {PluginName}", plugin.Name);
            }
        }
    }

    private static void OnPluginCommand(ConCommandContext ctx)
    {
        var sub = ctx.Args.Length > 1 ? ctx.Args[1] : "list";

        if (string.Equals(sub, "list", StringComparison.OrdinalIgnoreCase))
        {
            var dir = PluginLoader.PluginsDir;
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("[PluginLoader] No plugins directory found");
                return;
            }

            Console.WriteLine("[PluginLoader] Installed plugins:");
            foreach (var dll in Directory.GetFiles(dir, "*.dll").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                var enabled = PluginStateManager.IsEnabled(name);
                var loaded = PluginLoader.IsPluginLoaded(name);
                var status = enabled ? (loaded ? "enabled (loaded)" : "enabled (not loaded)") : "disabled";
                Console.WriteLine($"  {name}: {status}");
            }
        }
        else if (string.Equals(sub, "enable", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Args.Length < 3)
            {
                Console.WriteLine("Usage: dw_plugin enable <PluginName>");
                return;
            }
            PluginLoader.EnablePlugin(ctx.Args[2]);
        }
        else if (string.Equals(sub, "disable", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Args.Length < 3)
            {
                Console.WriteLine("Usage: dw_plugin disable <PluginName>");
                return;
            }
            PluginLoader.DisablePlugin(ctx.Args[2]);
        }
        else if (string.Equals(sub, "commands", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Args.Length < 3)
            {
                Console.WriteLine("Usage: dw_plugin commands <PluginName>");
                return;
            }
            ListPluginCommands(ctx.Args[2]);
        }
        else
        {
            Console.WriteLine("Usage: dw_plugin <list|enable|disable|commands> [PluginName]");
        }
    }

    private static void OnHelp(ConCommandContext ctx)
    {
        var entries = PluginRegistrationTracker.GetAllEntries();

        var consoleCmds = entries
            .Where(e => e.Kind == "command" && !e.Hidden)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chatCmds = entries
            .Where(e => e.Kind == "chat" && !e.Hidden)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var to = ctx.Controller;
        Reply(to, "Available commands:");

        if (consoleCmds.Count > 0)
        {
            Reply(to, "Console:");
            foreach (var e in consoleCmds)
            {
                var desc = string.IsNullOrEmpty(e.Description) ? "" : $" - {e.Description}";
                Reply(to, $"  {e.Name}{desc}");
            }
        }

        if (chatCmds.Count > 0)
        {
            Reply(to, "Chat:");
            foreach (var e in chatCmds)
            {
                var desc = string.IsNullOrEmpty(e.Description) ? "" : $" - {e.Description}";
                Reply(to, $"  {e.Name}{desc}");
            }
        }

        if (consoleCmds.Count == 0 && chatCmds.Count == 0)
            Reply(to, "  (none)");
    }

    private static void Reply(CCitadelPlayerController? to, string message)
    {
        if (to != null)
            to.PrintToConsole(message);
        else
            Console.WriteLine(message);
    }

    private static void ListPluginCommands(string pluginName)
    {
        var normalizedPath = PluginLoader.ResolvePluginPath(pluginName);
        if (normalizedPath == null || !PluginLoader.IsPluginLoaded(pluginName))
        {
            Console.WriteLine($"[ConCommandManager] Plugin '{pluginName}' is not loaded");
            return;
        }

        var entries = PluginRegistrationTracker.GetEntries(normalizedPath);
        if (entries.Count == 0)
        {
            Console.WriteLine($"[ConCommandManager] Plugin '{pluginName}' has no registered commands");
            return;
        }

        Console.WriteLine($"[ConCommandManager] Commands registered by '{pluginName}':");
        foreach (var entry in entries)
        {
            var desc = string.IsNullOrEmpty(entry.Description) ? "" : $" - {entry.Description}";
            Console.WriteLine($"  [{entry.Kind}] {entry.Name}{desc}");
        }
    }

    internal static void RegisterBuiltInCommand(string name, string description, bool serverOnly, Action<ConCommandContext> handler)
    {
        Action<ConCommandContext> wrapped = serverOnly
            ? ctx =>
            {
                if (!ctx.IsServerCommand)
                {
                    _logger.LogWarning("Command {CommandName} is server-only", name);
                    return;
                }
                handler(ctx);
            }
            : handler;

        AddHandler(name, wrapped);

        NativeRegisterConCommand(name, description, BuildConCommandFlags(serverOnly));

        _logger.LogDebug("Registered built-in concommand: {CommandName}{ServerOnly}", name, serverOnly ? " (server-only)" : "");
    }

    public static void RegisterPlugin(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        var registered = new List<(string name, Action<ConCommandContext> handler)>();

        foreach (var plugin in plugins)
        {
            RegisterConCommands(normalizedPath, plugin, registered);
            RegisterConVars(normalizedPath, plugin, registered);
        }

        lock (_lock)
        {
            _pluginHandlers[normalizedPath] = registered;
        }
    }

    public static void UnregisterPlugin(string normalizedPath)
    {
        lock (_lock)
        {
            if (!_pluginHandlers.Remove(normalizedPath, out var registered))
                return;

            foreach (var (name, handler) in registered)
            {
                if (_handlers.TryGetValue(name, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _handlers.Remove(name);
                        NativeUnregisterConCommand(name);
                    }
                }

                _conVars.Remove(name);
            }
        }
    }

    public static void Dispatch(int playerSlot, string command, string[] args)
    {
        List<Action<ConCommandContext>>? handlers;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(command, out handlers))
                return;
            handlers = [.. handlers]; // snapshot
        }

        using var activity = DeadworksTracing.Source.StartActivity("command.dispatch");
        activity?.SetTag("command.name", command);
        activity?.SetTag("player.slot", playerSlot);

        DeadworksMetrics.CommandsDispatched.Add(1);

        var ctx = new ConCommandContext(playerSlot, command, args);
        foreach (var handler in handlers)
        {
            try
            {
                handler(ctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler for command {CommandName} threw", command);
            }
        }
    }

    /// <summary>Returns true if the command name has a registered [ConCommand]/[ConVar] handler.</summary>
    public static bool IsRegistered(string command)
    {
        lock (_lock)
        {
            return _handlers.ContainsKey(command);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _handlers.Clear();
            _pluginHandlers.Clear();
            _conVars.Clear();
        }

        // Re-register built-in commands
        Initialize();
    }

    private static void RegisterConCommands(string normalizedPath, IDeadworksPlugin plugin, List<(string, Action<ConCommandContext>)> registered)
    {
        var methods = plugin.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
#pragma warning disable CS0618 // ConCommandAttribute is obsolete; intentionally scanned for back-compat
            var attrs = method.GetCustomAttributes<ConCommandAttribute>();
#pragma warning restore CS0618
            foreach (var attr in attrs)
            {
                var del = (Action<ConCommandContext>)Delegate.CreateDelegate(
                    typeof(Action<ConCommandContext>), plugin, method);

                bool serverOnly = attr.ServerOnly;
                Action<ConCommandContext> handler = serverOnly
                    ? ctx =>
                    {
                        if (!ctx.IsServerCommand)
                        {
                            _logger.LogWarning("Command {CommandName} is server-only", attr.Name);
                            return;
                        }
                        del(ctx);
                    }
                    : del;

                AddHandler(attr.Name, handler);
                registered.Add((attr.Name, handler));
                PluginRegistrationTracker.Add(normalizedPath, "command", attr.Name, attr.Description);

                NativeRegisterConCommand(attr.Name, attr.Description, BuildConCommandFlags(serverOnly));

                _logger.LogDebug("Registered concommand: {PluginName} -> {CommandName}{ServerOnly}", plugin.Name, attr.Name, serverOnly ? " (server-only)" : "");
            }
        }
    }

    private static void RegisterConVars(string normalizedPath, IDeadworksPlugin plugin, List<(string, Action<ConCommandContext>)> registered)
    {
        var properties = plugin.GetType().GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<ConVarAttribute>();
            if (attr == null)
                continue;

            if (!prop.CanRead || !prop.CanWrite)
            {
                _logger.LogWarning("ConVar {ConVarName} on {PluginName} must have both getter and setter, skipping", attr.Name, plugin.Name);
                continue;
            }

            var capturedPlugin = plugin;
            var capturedProp = prop;
            bool serverOnly = attr.ServerOnly;

            Action<ConCommandContext> handler = ctx =>
            {
                if (serverOnly && !ctx.IsServerCommand)
                {
                    _logger.LogWarning("ConVar {ConVarName} is server-only", attr.Name);
                    return;
                }

                if (ctx.Args.Length <= 1)
                {
                    // Print current value
                    var value = capturedProp.GetValue(capturedPlugin);
                    Console.WriteLine($"  \"{attr.Name}\" = \"{value}\" ({attr.Description})");
                    return;
                }

                // Set value
                var arg = ctx.Args[1];
                try
                {
                    var converted = ConvertValue(arg, capturedProp.PropertyType);
                    capturedProp.SetValue(capturedPlugin, converted);
                    Console.WriteLine($"  \"{attr.Name}\" set to \"{converted}\"");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to set ConVar {ConVarName}", attr.Name);
                }
            };

            AddHandler(attr.Name, handler);
            registered.Add((attr.Name, handler));
            PluginRegistrationTracker.Add(normalizedPath, "convar", attr.Name, attr.Description);

            lock (_lock)
            {
                _conVars[attr.Name] = (plugin, prop);
            }

            NativeRegisterConCommand(attr.Name, attr.Description, BuildConCommandFlags(serverOnly));

            _logger.LogDebug("Registered convar: {PluginName} -> {ConVarName} ({TypeName}){ServerOnly}", plugin.Name, attr.Name, prop.PropertyType.Name, serverOnly ? " (server-only)" : "");
        }
    }

    private static void AddHandler(string name, Action<ConCommandContext> handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(name, out var list))
            {
                list = new List<Action<ConCommandContext>>();
                _handlers[name] = list;
            }
            list.Add(handler);
        }
    }

    internal static object ConvertValue(string arg, Type type)
    {
        if (type == typeof(int)) return int.Parse(arg);
        if (type == typeof(float)) return float.Parse(arg);
        if (type == typeof(double)) return double.Parse(arg);
        if (type == typeof(bool))
        {
            if (arg == "1" || arg.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (arg == "0" || arg.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return bool.Parse(arg);
        }
        if (type == typeof(string)) return arg;
        if (type == typeof(long)) return long.Parse(arg);

        throw new NotSupportedException($"Type '{type.Name}' is not supported");
    }

    /// <summary>Registers a console command from outside this manager; tracked for plugin-scoped cleanup.</summary>
    internal static void RegisterExternal(
        string normalizedPath,
        string name,
        string description,
        bool serverOnly,
        Action<ConCommandContext> handler,
        bool hidden = false)
    {
        Action<ConCommandContext> wrapped = serverOnly
            ? ctx =>
            {
                if (!ctx.IsServerCommand)
                {
                    Console.WriteLine($"[ConCommandManager] Command '{name}' is server-only");
                    return;
                }
                handler(ctx);
            }
            : handler;

        AddHandler(name, wrapped);

        lock (_lock)
        {
            if (!_pluginHandlers.TryGetValue(normalizedPath, out var registered))
            {
                registered = new List<(string name, Action<ConCommandContext> handler)>();
                _pluginHandlers[normalizedPath] = registered;
            }
            registered.Add((name, wrapped));
        }

        PluginRegistrationTracker.Add(normalizedPath, "command", name, description, hidden);

        NativeRegisterConCommand(name, description, BuildConCommandFlags(serverOnly));
    }

    private static unsafe void NativeRegisterConCommand(string name, string description, FCVar flags)
    {
        if (NativeInterop.RegisterConCommand == null)
            return;

        Span<byte> nameUtf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
        Span<byte> descUtf8 = Utf8.Encode(description, stackalloc byte[Utf8.Size(description)]);

        fixed (byte* namePtr = nameUtf8)
        fixed (byte* descPtr = descUtf8)
        {
            NativeInterop.RegisterConCommand(namePtr, descPtr, (ulong)flags);
        }
    }

    private static FCVar BuildConCommandFlags(bool serverOnly)
    {
        var flags = FCVar.Unregistered;
        if (!serverOnly)
            flags |= FCVar.AccessibleFromThreads;
        return flags;
    }

    private static unsafe void NativeUnregisterConCommand(string name)
    {
        if (NativeInterop.UnregisterConCommand == null)
            return;

        Span<byte> nameUtf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);

        fixed (byte* namePtr = nameUtf8)
        {
            NativeInterop.UnregisterConCommand(namePtr);
        }
    }
}
