using System.Reflection;
using System.Runtime.Loader;
using Google.Protobuf;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

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

        return map;
    }

    public static void LoadAll()
    {
        TimerRegistry.Initialize();
        DeadworksConfig.Initialize();
        ConfigManager.Initialize();
        ConCommandManager.Initialize();
        UIBootstrap.Initialize();
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
            Console.WriteLine($"[PluginLoader] No plugins directory found at: {_pluginsDir}");
            return;
        }

        var dlls = Directory.GetFiles(_pluginsDir, "*.dll");
        Console.WriteLine($"[PluginLoader] Scanning {_pluginsDir} ({dlls.Length} DLLs found)");

        foreach (var dll in dlls)
        {
            var dllName = Path.GetFileNameWithoutExtension(dll);
            if (!PluginStateManager.IsEnabled(dllName))
            {
                Console.WriteLine($"[PluginLoader] Skipping disabled plugin: {dllName}");
                continue;
            }

            try
            {
                LoadPlugin(dll, isReload: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Failed to load {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

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
            Console.WriteLine($"[PluginLoader] Cannot enable '{dllName}': DLL not found in plugins directory");
            return;
        }

        var normalizedPath = Path.GetFullPath(dllPath);
        lock (_lock)
        {
            if (_loaded.ContainsKey(normalizedPath))
            {
                Console.WriteLine($"[PluginLoader] Plugin '{dllName}' is already loaded");
                return;
            }
        }

        try
        {
            LoadPlugin(normalizedPath, isReload: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLoader] Failed to enable '{dllName}': {ex.Message}");
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
                // Create and register timer service before OnLoad so it's available immediately
                var timerService = new TimerService();
                TimerRegistry.Register(plugin, timerService);

                ConfigManager.LoadConfig(plugin);
                plugin.OnLoad(isReload);
                plugins.Add(plugin);
                Console.WriteLine($"[PluginLoader] Loaded plugin: {plugin.Name}{(isReload ? " (reloaded)" : "")}");
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
    }

    private static void UnloadPlugin(string normalizedPath)
    {
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
                Console.WriteLine($"[PluginLoader] Unloaded plugin: {plugin.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Error unloading {plugin.Name}: {ex.Message}");
            }
        }

        entry.Context.Unload();
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

        Console.WriteLine($"[PluginLoader] Watching for plugin changes in: {pluginsDir}");
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
                Console.WriteLine($"[PluginLoader] Skipping reload of disabled plugin: {dllName}");
                continue;
            }

            try
            {
                Console.WriteLine($"[PluginLoader] Detected change: {Path.GetFileName(dllPath)}");
                UnloadPlugin(dllPath);
                LoadPlugin(dllPath, isReload: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Failed to reload {Path.GetFileName(dllPath)}: {ex.Message}");
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
                Console.WriteLine($"[PluginLoader] {plugin.Name}.{methodName} threw: {ex.Message}");
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
                Console.WriteLine($"[PluginLoader] {plugin.Name}.{methodName} threw: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Any plugin returning false vetoes. Every plugin is still invoked; not a HookResult-style max, a plain AND.</summary>
    private static bool DispatchToPluginsAllAllow(Func<IDeadworksPlugin, bool> invoke, string methodName)
    {
        var snapshot = _pluginSnapshot;
        var allow = true;
        foreach (var plugin in snapshot)
        {
            try
            {
                if (!invoke(plugin))
                    allow = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] {plugin.Name}.{methodName} threw: {ex.Message}");
            }
        }
        return allow;
    }

    // --- Plugin lifecycle dispatchers ---

    public static void DispatchPrecacheResources()
        => DispatchToPlugins(p => p.OnPrecacheResources(), nameof(IDeadworksPlugin.OnPrecacheResources));

    public static void DispatchStartupServer()
    {
        TimerRegistry.CancelAllMapChangeTimers();
        ServerBrowser.OnStartupServer();
        DispatchToPlugins(p => p.OnStartupServer(), nameof(IDeadworksPlugin.OnStartupServer));
    }

    public static void DispatchGameFrame(bool simulating, bool firstTick, bool lastTick)
    {
        TimerEngine.OnTick();
        UI.Tick();
        DispatchToPlugins(p => p.OnGameFrame(simulating, firstTick, lastTick), nameof(IDeadworksPlugin.OnGameFrame));
    }

    public static bool DispatchClientConnect(ClientConnectEvent args)
        => DispatchToPluginsAllAllow(p => p.OnClientConnect(args), nameof(IDeadworksPlugin.OnClientConnect));

    public static void DispatchClientPutInServer(ClientPutInServerEvent args)
        => DispatchToPlugins(p => p.OnClientPutInServer(args), nameof(IDeadworksPlugin.OnClientPutInServer));

    public static void DispatchClientFullConnect(ClientFullConnectEvent args)
        => DispatchToPlugins(p => p.OnClientFullConnect(args), nameof(IDeadworksPlugin.OnClientFullConnect));

    public static void DispatchClientDisconnect(ClientDisconnectedEvent args)
        => DispatchToPlugins(p => p.OnClientDisconnect(args), nameof(IDeadworksPlugin.OnClientDisconnect));

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

    public static void DispatchModifierEvent(ModifierEvent args)
        => DispatchToPlugins(p => p.OnModifierEvent(args), nameof(IDeadworksPlugin.OnModifierEvent));

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

    public static void DispatchGameStateChanged(EGameState newState)
        => DispatchToPlugins(p => p.OnGameStateChanged(newState), nameof(IDeadworksPlugin.OnGameStateChanged));

    public static bool DispatchShouldAllowGameStateChange(EGameState currentState, EGameState newState)
        => DispatchToPluginsAllAllow(p => p.OnGameStateChanging(currentState, newState), nameof(IDeadworksPlugin.OnGameStateChanging));

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
        GameRules.SetWaitingForPlayersRoster(0, 0);

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
                    Console.WriteLine($"[PluginLoader] Unloaded plugin: {plugin.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginLoader] Error unloading {plugin.Name}: {ex.Message}");
                }
            }

            entry.Context.Unload();
        }
    }
}
