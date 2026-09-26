using System.Reflection;
using System.Runtime.Loader;
using Google.Protobuf;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;
using DeadworksManaged.Api.Utils;

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
    private static string _builtinDir = "";

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
        PermissionSystem.PermissionManager.Initialize();
        ConCommandManager.Initialize();
        AdminSystem.PenaltyManager.Initialize();
        AdminSystem.AdminActivityService.Initialize();
        AdminSystem.CommandCapture.Initialize();
        Server.ExtraMaps = () => DeadworksConfig.ServerBrowser.ExtraMaps;
        UIBootstrap.Initialize();
        ServerBrowser.Initialize();
        PluginStateManager.Initialize();
        PluginRegistry.Resolve = () => _pluginSnapshot.Select(p => p.Name).ToArray();
        ContentAddonManager.Initialize(() => _pluginSnapshot);

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

        RegisterCoreCommands();

        _pluginsDir = Path.Combine(baseDir, "plugins");
        _builtinDir = Path.Combine(baseDir, "builtin");

        LoadDirectory(_builtinDir, builtin: true);
        if (Directory.Exists(_pluginsDir))
            LoadDirectory(_pluginsDir, builtin: false);
        else
            Console.WriteLine($"[PluginLoader] No plugins directory found at: {_pluginsDir}");

        PermissionSystem.PermissionManifest.DeleteStale();
        if (Directory.Exists(_pluginsDir))
            StartWatching(_pluginsDir);
    }

    private static void LoadDirectory(string dir, bool builtin)
    {
        if (!Directory.Exists(dir))
            return;

        var dlls = Directory.GetFiles(dir, "*.dll");
        Console.WriteLine($"[PluginLoader] Scanning {dir} ({dlls.Length} DLLs found)");

        foreach (var dll in dlls)
        {
            var dllName = Path.GetFileNameWithoutExtension(dll);
            if (!PluginStateManager.IsEnabled(dllName))
            {
                Console.WriteLine($"[PluginLoader] Skipping disabled plugin: {dllName}");
                continue;
            }

            // A plugin of the same name in plugins/ replaces the one that ships with Deadworks.
            if (builtin && File.Exists(Path.Combine(_pluginsDir, dllName + ".dll")))
            {
                Console.WriteLine($"[PluginLoader] Using plugins/{dllName}.dll instead of the built-in one");
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
    }

    private const string CoreCommandsPath = "deadworks://core";

    /// <summary>Built-in commands written as [Command]s, so they get permission checks and a generated listing like any plugin's.</summary>
    private static void RegisterCoreCommands()
    {
        lock (_lock)
        {
            Commands.CommandRegistration.RegisterPluginCommands(
                CoreCommandsPath, [new PermissionSystem.PermissionCommands(), new AdminSystem.PenaltyCommands()], _chatCommandRegistry,
                manifestKey: PermissionSystem.PermissionManifest.CoreFileKey);
        }
    }

    public static bool IsPluginLoaded(string dllName)
    {
        var normalizedPath = ResolvePluginPath(dllName);
        if (normalizedPath == null) return false;
        lock (_lock)
        {
            return _loaded.ContainsKey(normalizedPath);
        }
    }

    /// <summary>Whether <paramref name="dllName"/> is one of the plugins that ship with Deadworks and isn't replaced in plugins/.</summary>
    public static bool IsBuiltin(string dllName)
        => _builtinDir.Length > 0
           && File.Exists(Path.Combine(_builtinDir, dllName + ".dll"))
           && !File.Exists(Path.Combine(_pluginsDir, dllName + ".dll"));

    /// <summary>
    /// The full path a plugin DLL name loads from: plugins/ if it's there, otherwise builtin/ if it ships with
    /// Deadworks, otherwise where it would go in plugins/. Null before loading has started.
    /// </summary>
    public static string? ResolvePluginPath(string dllName)
    {
        if (_pluginsDir.Length == 0) return null;
        var dir = IsBuiltin(dllName) ? _builtinDir : _pluginsDir;
        return Path.GetFullPath(Path.Combine(dir, dllName + ".dll"));
    }

    /// <summary>Every plugin DLL name found in builtin/ and plugins/.</summary>
    public static IEnumerable<string> InstalledPluginNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { _builtinDir, _pluginsDir })
            if (dir.Length > 0 && Directory.Exists(dir))
                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                    names.Add(Path.GetFileNameWithoutExtension(dll));
        return names;
    }

    public static void EnablePlugin(string dllName)
    {
        PluginStateManager.SetEnabled(dllName, true);

        var normalizedPath = ResolvePluginPath(dllName);
        if (normalizedPath == null || !File.Exists(normalizedPath))
        {
            Console.WriteLine($"[PluginLoader] Cannot enable '{dllName}': DLL not found in the plugins or builtin directory");
            return;
        }

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

        if (ResolvePluginPath(dllName) is { } normalizedPath)
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

        ContentAddonManager.Refresh();
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
            PermissionSystem.PermissionManifest.Remove(normalizedPath);
        }

        PermissionSystem.PermissionManager.UnregisterStoresOwnedBy(entry.Plugins);
        AdminSystem.PenaltyManager.UnregisterStoresOwnedBy(entry.Plugins);

        // Stop the plugin's zones before OnUnload, like its timers below.
        ZoneRegistry.RemoveOwnedBy(entry.Context);

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

        ContentAddonManager.Refresh();
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
                if (_builtinDir.Length > 0)
                    UnloadPlugin(Path.GetFullPath(Path.Combine(_builtinDir, dllName + ".dll")));
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
        ContentAddonManager.OnStartupServer();
        DispatchToPlugins(p => p.OnStartupServer(), nameof(IDeadworksPlugin.OnStartupServer));
    }

    public static void DispatchGameFrame(bool simulating, bool firstTick, bool lastTick)
    {
        TimerEngine.OnTick();
        UI.Tick();
        if (simulating)
            ZoneRegistry.Tick();
        DispatchToPlugins(p => p.OnGameFrame(simulating, firstTick, lastTick), nameof(IDeadworksPlugin.OnGameFrame));
    }

    public static bool DispatchClientConnect(ClientConnectEvent args)
        => DispatchToPluginsAllAllow(p => p.OnClientConnect(args), nameof(IDeadworksPlugin.OnClientConnect));

    public static void DispatchClientPutInServer(ClientPutInServerEvent args)
        => DispatchToPlugins(p => p.OnClientPutInServer(args), nameof(IDeadworksPlugin.OnClientPutInServer));

    public static void DispatchClientFullConnect(ClientFullConnectEvent args)
        => DispatchToPlugins(p => p.OnClientFullConnect(args), nameof(IDeadworksPlugin.OnClientFullConnect));

    public static void DispatchClientDisconnecting(ClientDisconnectedEvent args)
        => DispatchToPlugins(p => p.OnClientDisconnecting(args), nameof(IDeadworksPlugin.OnClientDisconnecting));

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
        ZoneRegistry.Clear();

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
