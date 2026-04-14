using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly Dictionary<string, Assembly> _sharedAssemblies;

    public PluginLoadContext(string pluginPath, Dictionary<string, Assembly> sharedAssemblies)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _sharedAssemblies = sharedAssemblies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared host assemblies: return the exact instance the host uses
        // so plugin types share identity with the host (e.g. IDeadworksPlugin).
        if (assemblyName.Name != null && _sharedAssemblies.TryGetValue(assemblyName.Name, out var shared))
            return shared;

        // Resolve plugin-local dependencies (from the plugin's deps.json)
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null)
            return LoadFromAssemblyPath(path);

        return null;
    }
}

internal sealed class PluginEntry
{
    public required PluginLoadContext Context { get; init; }
    public required List<IDeadworksPlugin> Plugins { get; init; }
}

internal static partial class PluginLoader
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, PluginEntry> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private static volatile IDeadworksPlugin[] _pluginSnapshot = [];
    internal static IDeadworksPlugin[] PluginSnapshot => _pluginSnapshot;

    private static readonly HandlerRegistry<string, GameEventHandler> _eventRegistry = new(StringComparer.Ordinal);
    private static readonly HandlerRegistry<string, Func<ChatCommandContext, HookResult>> _chatCommandRegistry = new(StringComparer.OrdinalIgnoreCase);

    // Net message hooks: msgId -> list of handler delegates
    private static readonly Dictionary<int, List<Delegate>> _outgoingNetMsgHandlers = new();
    private static readonly Dictionary<int, List<Delegate>> _incomingNetMsgHandlers = new();
    private static readonly Dictionary<string, List<(int msgId, NetMessageDirection dir, Delegate handler)>> _pluginNetMsgHandlers = new(StringComparer.OrdinalIgnoreCase);

    private static string _pluginsDir = "";
    public static string PluginsDir => _pluginsDir;

    private static FileSystemWatcher? _watcher;
    private static Timer? _debounceTimer;
    private static readonly HashSet<string> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);

    // Assemblies that plugins may reference from the host. Resolved once at startup
    // so every PluginLoadContext returns the same instance (preserving type identity).
    private static readonly Dictionary<string, Assembly> SharedAssemblies = BuildSharedAssemblies();

    private static Dictionary<string, Assembly> BuildSharedAssemblies()
    {
        var map = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        // DeadworksManaged.Api - contains IDeadworksPlugin, shared types, and generated proto classes
        var apiAsm = typeof(IDeadworksPlugin).Assembly;
        map[apiAsm.GetName().Name!] = apiAsm;

        // DeadworksManaged itself - plugins might reference host utilities
        var hostAsm = typeof(PluginLoader).Assembly;
        map[hostAsm.GetName().Name!] = hostAsm;

        // Google.Protobuf - shared so plugins use the same protobuf runtime as the host
        var protobufAsm = typeof(IMessage).Assembly;
        map[protobufAsm.GetName().Name!] = protobufAsm;

        // Microsoft.Extensions.Logging.Abstractions - shared so plugins use the same ILogger type
        var loggingAsm = typeof(ILogger).Assembly;
        map[loggingAsm.GetName().Name!] = loggingAsm;

        return map;
    }

    private static ILogger _logger = null!;

    public static void LoadAll()
    {
        DeadworksConfig.Initialize();
        DeadworksTelemetry.Initialize();
        PluginLoggerRegistry.Initialize();
        _logger = DeadworksTelemetry.CreateLogger("PluginLoader");

        TimerRegistry.Initialize();
        ConfigManager.Initialize();
        ConCommandManager.Initialize();
        ServerBrowser.Initialize();
        PluginStateManager.Initialize();
        PluginRegistry.Resolve = () => _pluginSnapshot.Select(p => p.Name).ToArray();

        GameEvents.OnAddListener = OnManualAddListenerWithHandle;
        GameEvents.OnRemoveListener = OnManualRemoveListener;

        NetMessageRegistry.EnsureInitialized();
        NetMessages.OnSend = OnNetMessageSend;
        NetMessages.OnHookAdd = OnNetMessageHookAddWithHandle;
        NetMessages.OnHookRemove = OnNetMessageHookRemove;

        EntityIO.OnHookInput = OnEntityIOHookInputProgrammatic;
        EntityIO.OnHookOutput = OnEntityIOHookOutputProgrammatic;

        var baseDir = Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location);
        if (baseDir is null)
            return;

        _pluginsDir = Path.Combine(baseDir, "plugins");
        if (!Directory.Exists(_pluginsDir))
        {
            _logger.LogWarning("No plugins directory found at {PluginsDir}", _pluginsDir);
            return;
        }

        var dlls = Directory.GetFiles(_pluginsDir, "*.dll");
        _logger.LogInformation("Scanning {PluginsDir} ({DllCount} DLLs found)", _pluginsDir, dlls.Length);

        foreach (var dll in dlls)
        {
            var dllName = Path.GetFileNameWithoutExtension(dll);
            if (!PluginStateManager.IsEnabled(dllName))
            {
                _logger.LogDebug("Skipping disabled plugin {PluginName}", dllName);
                continue;
            }

            try
            {
                LoadPlugin(dll, isReload: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin {DllName}", Path.GetFileName(dll));
                DeadworksMetrics.PluginLoadErrors.Add(1);
            }
        }

        // Register observable gauges now that plugin loading is complete
        DeadworksMetrics.RegisterObservableGauges(
            () => _pluginSnapshot.Length,
            () =>
            {
                int count = 0;
                for (int i = 0; i < Players.MaxSlot; i++)
                    if (Players.IsConnected(i)) count++;
                return count;
            });

        StartWatching(_pluginsDir);
    }

    public static bool IsPluginLoaded(string dllName)
    {
        if (_pluginsDir.Length == 0) return false;
        var normalizedPath = Path.GetFullPath(Path.Combine(_pluginsDir, dllName + ".dll"));
        lock (_lock)
        {
            return _loaded.ContainsKey(normalizedPath);
        }
    }

    /// <summary>Returns the normalized full path for a plugin DLL name, or null if the plugins directory is not set.</summary>
    public static string? ResolvePluginPath(string dllName)
    {
        if (_pluginsDir.Length == 0) return null;
        return Path.GetFullPath(Path.Combine(_pluginsDir, dllName + ".dll"));
    }

    public static void EnablePlugin(string dllName)
    {
        PluginStateManager.SetEnabled(dllName, true);

        var dllPath = Path.Combine(_pluginsDir, dllName + ".dll");
        if (!File.Exists(dllPath))
        {
            _logger.LogWarning("Cannot enable plugin {PluginName}: DLL not found in plugins directory", dllName);
            return;
        }

        var normalizedPath = Path.GetFullPath(dllPath);
        lock (_lock)
        {
            if (_loaded.ContainsKey(normalizedPath))
            {
                _logger.LogInformation("Plugin {PluginName} is already loaded", dllName);
                return;
            }
        }

        try
        {
            LoadPlugin(normalizedPath, isReload: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable plugin {PluginName}", dllName);
        }
    }

    public static void DisablePlugin(string dllName)
    {
        PluginStateManager.SetEnabled(dllName, false);

        if (_pluginsDir.Length == 0) return;
        var normalizedPath = Path.GetFullPath(Path.Combine(_pluginsDir, dllName + ".dll"));
        UnloadPlugin(normalizedPath);
    }

    private static void LoadPlugin(string dllPath, bool isReload)
    {
        var normalizedPath = Path.GetFullPath(dllPath);
        var pluginFileName = Path.GetFileNameWithoutExtension(dllPath);

        using var activity = DeadworksTracing.Source.StartActivity("plugin.load");
        activity?.SetTag("plugin.name", pluginFileName);
        activity?.SetTag("is_reload", isReload);

        var sw = Stopwatch.StartNew();
        var context = new PluginLoadContext(normalizedPath, SharedAssemblies);

        // Load DLL from memory so the file isn't locked by the runtime.
        var dllBytes = File.ReadAllBytes(normalizedPath);
        Assembly assembly;

        var pdbPath = Path.ChangeExtension(normalizedPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            var pdbBytes = File.ReadAllBytes(pdbPath);
            assembly = context.LoadFromStream(new MemoryStream(dllBytes), new MemoryStream(pdbBytes));
        }
        else
        {
            assembly = context.LoadFromStream(new MemoryStream(dllBytes));
        }

        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(IDeadworksPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        var plugins = new List<IDeadworksPlugin>();

        foreach (var type in pluginTypes)
        {
            if (Activator.CreateInstance(type) is IDeadworksPlugin plugin)
            {
                // Create and register services before OnLoad so they're available immediately
                var timerService = new TimerService();
                TimerRegistry.Register(plugin, timerService);
                PluginLoggerRegistry.Register(plugin);

                ConfigManager.LoadConfig(plugin);
                plugin.OnLoad(isReload);
                plugins.Add(plugin);
                _logger.LogInformation("Loaded plugin {PluginName} (reload: {IsReload})", plugin.Name, isReload);
            }
        }

        lock (_lock)
        {
            _loaded[normalizedPath] = new PluginEntry { Context = context, Plugins = plugins };
            RebuildSnapshot();
            RegisterPluginEventHandlers(normalizedPath, plugins);
            RegisterPluginNetMessageHandlers(normalizedPath, plugins);
            RegisterPluginEntityIOHooks(normalizedPath, plugins);
            RegisterPluginChatCommands(normalizedPath, plugins);
            ConCommandManager.RegisterPlugin(normalizedPath, plugins);
            Commands.CommandRegistration.RegisterPluginCommands(normalizedPath, plugins, _chatCommandRegistry);
        }

        sw.Stop();
        DeadworksMetrics.PluginLoadDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("plugin.name", pluginFileName));
        DeadworksMetrics.PluginsLoaded.Add(1,
            new KeyValuePair<string, object?>("plugin.name", pluginFileName));
    }

    private static void UnloadPlugin(string normalizedPath)
    {
        using var activity = DeadworksTracing.Source.StartActivity("plugin.unload");
        activity?.SetTag("plugin.name", Path.GetFileNameWithoutExtension(normalizedPath));

        PluginEntry? entry;
        lock (_lock)
        {
            if (!_loaded.Remove(normalizedPath, out entry))
                return;
            RebuildSnapshot();
            _eventRegistry.UnregisterPlugin(normalizedPath);
            UnregisterPluginNetMessageHandlers(normalizedPath);
            UnregisterPluginEntityIOHooks(normalizedPath);
            _chatCommandRegistry.UnregisterPlugin(normalizedPath);
            ConCommandManager.UnregisterPlugin(normalizedPath);
            PluginRegistrationTracker.Remove(normalizedPath);
        }

        foreach (var plugin in entry.Plugins)
        {
            try
            {
                // Dispose timer service before OnUnload so timers stop firing
                TimerRegistry.GetService(plugin)?.Dispose();
                TimerRegistry.Unregister(plugin);

                plugin.OnUnload();
                PluginLoggerRegistry.Unregister(plugin);
                _logger.LogInformation("Unloaded plugin {PluginName}", plugin.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unloading plugin {PluginName}", plugin.Name);
            }
        }

        entry.Context.Unload();
        DeadworksMetrics.PluginsUnloaded.Add(1);
    }

    // --- File watcher ---

    private static void StartWatching(string pluginsDir)
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(pluginsDir, "*.dll")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnDllChanged;
        _watcher.Created += OnDllChanged;

        _logger.LogInformation("Watching for plugin changes in {PluginsDir}", pluginsDir);
    }

    private static void OnDllChanged(object sender, FileSystemEventArgs e)
    {
        lock (_pendingReloads)
        {
            _pendingReloads.Add(Path.GetFullPath(e.FullPath));
        }

        _debounceTimer?.Change(500, Timeout.Infinite);
    }

    private static void OnDebounceElapsed(object? state)
    {
        string[] paths;
        lock (_pendingReloads)
        {
            paths = [.. _pendingReloads];
            _pendingReloads.Clear();
        }

        foreach (var dllPath in paths)
        {
            var dllName = Path.GetFileNameWithoutExtension(dllPath);
            if (!PluginStateManager.IsEnabled(dllName))
            {
                _logger.LogDebug("Skipping reload of disabled plugin {PluginName}", dllName);
                continue;
            }

            try
            {
                _logger.LogInformation("Detected change for {DllName}, reloading", Path.GetFileName(dllPath));
                UnloadPlugin(dllPath);
                LoadPlugin(dllPath, isReload: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload plugin {DllName}", Path.GetFileName(dllPath));
                DeadworksMetrics.PluginLoadErrors.Add(1);
            }
        }
    }

    // Must be called under _lock.
    private static void RebuildSnapshot()
    {
        _pluginSnapshot = _loaded.Values.SelectMany(e => e.Plugins).ToArray();
    }

    // --- Dispatch helpers ---

    private static void DispatchToPlugins(Action<IDeadworksPlugin> invoke, string methodName)
    {
        var snapshot = _pluginSnapshot;
        foreach (var plugin in snapshot)
        {
            try
            {
                invoke(plugin);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {PluginName}.{MethodName} threw", plugin.Name, methodName);
                DeadworksMetrics.EventHandlerErrors.Add(1,
                    new KeyValuePair<string, object?>("plugin.name", plugin.Name));
            }
        }
    }

    private static HookResult DispatchToPluginsWithResult(Func<IDeadworksPlugin, HookResult> invoke, string methodName)
    {
        var snapshot = _pluginSnapshot;
        var result = HookResult.Continue;
        foreach (var plugin in snapshot)
        {
            try
            {
                var hr = invoke(plugin);
                if (hr > result) result = hr;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {PluginName}.{MethodName} threw", plugin.Name, methodName);
                DeadworksMetrics.EventHandlerErrors.Add(1,
                    new KeyValuePair<string, object?>("plugin.name", plugin.Name));
            }
        }
        return result;
    }

    // --- Plugin lifecycle dispatchers ---

    public static void DispatchPrecacheResources()
        => DispatchToPlugins(p => p.OnPrecacheResources(), nameof(IDeadworksPlugin.OnPrecacheResources));

    public static void DispatchStartupServer()
    {
        using var activity = DeadworksTracing.Source.StartActivity("server.startup");
        activity?.SetTag("map.name", Server.MapName);

        TimerRegistry.CancelAllMapChangeTimers();
        ServerBrowser.OnStartupServer();
        DispatchToPlugins(p => p.OnStartupServer(), nameof(IDeadworksPlugin.OnStartupServer));
        _logger.LogInformation("Server started on map {MapName}", Server.MapName);
    }

    private static readonly Stopwatch _frameStopwatch = new();
    private static int _frameCounter;

    public static void DispatchGameFrame(bool simulating, bool firstTick, bool lastTick)
    {
        _frameStopwatch.Restart();
        TimerEngine.OnTick();
        DispatchToPlugins(p => p.OnGameFrame(simulating, firstTick, lastTick), nameof(IDeadworksPlugin.OnGameFrame));
        _frameStopwatch.Stop();

        // Sample every 64th frame to avoid histogram overhead at ~64Hz
        if (++_frameCounter >= 64)
        {
            _frameCounter = 0;
            DeadworksMetrics.GameFrameDuration.Record(_frameStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public static bool DispatchClientConnect(ClientConnectEvent args)
    {
        using var activity = DeadworksTracing.Source.StartActivity("client.connect");
        activity?.SetTag("player.slot", args.Slot);
        activity?.SetTag("player.steamid", args.SteamId);

        DeadworksMetrics.PlayerConnections.Add(1);

        var snapshot = _pluginSnapshot;
        bool allowed = true;
        foreach (var plugin in snapshot)
        {
            try
            {
                if (!plugin.OnClientConnect(args))
                    allowed = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {PluginName}.OnClientConnect threw", plugin.Name);
                DeadworksMetrics.EventHandlerErrors.Add(1,
                    new KeyValuePair<string, object?>("plugin.name", plugin.Name));
            }
        }

        if (!allowed)
        {
            DeadworksMetrics.PlayerConnectionsRejected.Add(1);
            _logger.LogInformation("Client connection rejected (slot={Slot}, steamid={SteamId})", args.Slot, args.SteamId);
        }
        else
        {
            _logger.LogInformation("Client connected (slot={Slot}, steamid={SteamId}, name={Name})", args.Slot, args.SteamId, args.Name);
        }

        return allowed;
    }

    public static void DispatchClientPutInServer(ClientPutInServerEvent args)
        => DispatchToPlugins(p => p.OnClientPutInServer(args), nameof(IDeadworksPlugin.OnClientPutInServer));

    public static void DispatchClientFullConnect(ClientFullConnectEvent args)
        => DispatchToPlugins(p => p.OnClientFullConnect(args), nameof(IDeadworksPlugin.OnClientFullConnect));

    public static void DispatchClientDisconnect(ClientDisconnectedEvent args)
    {
        using var activity = DeadworksTracing.Source.StartActivity("client.disconnect");
        activity?.SetTag("player.slot", args.Slot);
        activity?.SetTag("disconnect.reason", args.Reason);

        DeadworksMetrics.PlayerDisconnections.Add(1);
        _logger.LogInformation("Client disconnected (slot={Slot}, reason={Reason})", args.Slot, args.Reason);

        DispatchToPlugins(p => p.OnClientDisconnect(args), nameof(IDeadworksPlugin.OnClientDisconnect));
    }

    public static void DispatchEntityCreated(EntityCreatedEvent args)
        => DispatchToPlugins(p => p.OnEntityCreated(args), nameof(IDeadworksPlugin.OnEntityCreated));

    public static void DispatchEntitySpawned(EntitySpawnedEvent args)
    {
        GameRules.OnEntitySpawned(args.Entity);
        DispatchToPlugins(p => p.OnEntitySpawned(args), nameof(IDeadworksPlugin.OnEntitySpawned));
    }

    public static void DispatchEntityDeleted(EntityDeletedEvent args)
    {
        GameRules.OnEntityDeleted(args.Entity);
        DispatchToPlugins(p => p.OnEntityDeleted(args), nameof(IDeadworksPlugin.OnEntityDeleted));
        EntityDataRegistry.OnEntityDeleted(args.Entity.EntityHandle);
        CCitadelPlayerPawn.OnEntityDeleted(args.Entity.Handle);
    }

    public static HookResult DispatchTakeDamage(TakeDamageEvent args)
        => DispatchToPluginsWithResult(p => p.OnTakeDamage(args), nameof(IDeadworksPlugin.OnTakeDamage));

    public static HookResult DispatchModifyCurrency(ModifyCurrencyEvent args)
        => DispatchToPluginsWithResult(p => p.OnModifyCurrency(args), nameof(IDeadworksPlugin.OnModifyCurrency));

    public static HookResult DispatchClientConCommand(ClientConCommandEvent args)
        => DispatchToPluginsWithResult(p => p.OnClientConCommand(args), nameof(IDeadworksPlugin.OnClientConCommand));

    public static void DispatchEntityStartTouch(EntityTouchEvent args)
        => DispatchToPlugins(p => p.OnEntityStartTouch(args), nameof(IDeadworksPlugin.OnEntityStartTouch));

    public static void DispatchEntityEndTouch(EntityTouchEvent args)
        => DispatchToPlugins(p => p.OnEntityEndTouch(args), nameof(IDeadworksPlugin.OnEntityEndTouch));

    public static void DispatchAbilityAttempt(AbilityAttemptEvent args)
        => DispatchToPlugins(p => p.OnAbilityAttempt(args), nameof(IDeadworksPlugin.OnAbilityAttempt));

    public static void DispatchProcessUsercmds(ProcessUsercmdsEvent args)
        => DispatchToPlugins(p => p.OnProcessUsercmds(args), nameof(IDeadworksPlugin.OnProcessUsercmds));

    public static HookResult DispatchAddModifier(AddModifierEvent args)
        => DispatchToPluginsWithResult(p => p.OnAddModifier(args), nameof(IDeadworksPlugin.OnAddModifier));

    public static void DispatchCheckTransmit(CheckTransmitEvent args)
        => DispatchToPlugins(p => p.OnCheckTransmit(args), nameof(IDeadworksPlugin.OnCheckTransmit));

    public static void DispatchPawnHeroInitialized(CCitadelPlayerPawn pawn)
        => DispatchToPlugins(p => p.OnPawnHeroInitialized(pawn), nameof(IDeadworksPlugin.OnPawnHeroInitialized));

    public static void UnloadAll()
    {
        ServerBrowser.Shutdown();

        _watcher?.Dispose();
        _watcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        List<PluginEntry> entries;
        lock (_lock)
        {
            entries = [.. _loaded.Values];
            _loaded.Clear();
            _pluginSnapshot = [];
            _eventRegistry.Clear();
            _chatCommandRegistry.Clear();
            _outgoingNetMsgHandlers.Clear();
            _incomingNetMsgHandlers.Clear();
            _pluginNetMsgHandlers.Clear();
            _entityInputHooks.Clear();
            _entityOutputHooks.Clear();
            _pluginEntityIOHandlers.Clear();
        }

        ConCommandManager.Clear();
        PluginRegistrationTracker.Clear();

        // Dispose all timer services and reset engine
        TimerRegistry.Clear();
        TimerEngine.Reset();

        foreach (var entry in entries)
        {
            foreach (var plugin in entry.Plugins)
            {
                try
                {
                    plugin.OnUnload();
                    _logger.LogInformation("Unloaded plugin {PluginName}", plugin.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unloading plugin {PluginName}", plugin.Name);
                }
            }

            entry.Context.Unload();
        }

        PluginLoggerRegistry.Clear();
        DeadworksTelemetry.Shutdown();
    }
}
