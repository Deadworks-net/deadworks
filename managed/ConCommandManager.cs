using System.Reflection;
using DeadworksManaged.Api;

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

    /// <summary>An internal command that isn't a [Command], such as the capture marker. Server-only ones refuse players.</summary>
    internal static void RegisterBuiltInCommand(string name, string description, bool serverOnly, Action<ConCommandContext> handler)
    {
        Action<ConCommandContext> wrapped = serverOnly
            ? ctx =>
            {
                if (!ctx.IsServerCommand)
                {
                    ctx.Controller?.PrintToConsole(Commands.CommandRegistration.DeniedMessage);
                    return;
                }
                handler(ctx);
            }
            : handler;

        AddHandler(name, wrapped);
        NativeRegisterConCommand(name, description, BuildConCommandFlags(serverOnly));

        Console.WriteLine($"[ConCommandManager] Registered built-in concommand: {name}{(serverOnly ? " (server-only)" : "")}");
    }

    public static void RegisterPlugin(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        var registered = new List<(string name, Action<ConCommandContext> handler)>();

        foreach (var plugin in plugins)
        {
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

        var ctx = new ConCommandContext(playerSlot, command, args);
        foreach (var handler in handlers)
        {
            try
            {
                handler(ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConCommandManager] Handler for '{command}' threw: {ex.Message}");
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
                Console.WriteLine($"[ConCommandManager] Warning: ConVar '{attr.Name}' on {plugin.Name} must have both getter and setter, skipping");
                continue;
            }

            var capturedPlugin = plugin;
            var capturedProp = prop;
            bool serverOnly = attr.ServerOnly;

            Action<ConCommandContext> handler = ctx =>
            {
                if (serverOnly && !ctx.IsServerCommand)
                {
                    Console.WriteLine($"[ConCommandManager] ConVar '{attr.Name}' is server-only");
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
                    Console.WriteLine($"[ConCommandManager] Failed to set '{attr.Name}': {ex.Message}");
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

            Console.WriteLine($"[ConCommandManager] Registered convar: {plugin.Name} -> {attr.Name} ({prop.PropertyType.Name}){(serverOnly ? " (server-only)" : "")}");
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
        bool hidden = false,
        Func<CCitadelPlayerController?, bool>? canRun = null,
        string? aliasOf = null)
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

        PluginRegistrationTracker.Add(normalizedPath, "command", name, description, hidden, canRun, aliasOf);

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
