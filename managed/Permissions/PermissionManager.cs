using DeadworksManaged.Api;

namespace DeadworksManaged.PermissionSystem;

/// <summary>
/// Owns the loaded roles and players, answers checks, and applies changes made with the management commands.
/// Data comes from the active <see cref="IPermissionStore"/>; evaluation is always <see cref="PermissionEvaluator"/>.
/// </summary>
internal static class PermissionManager
{
    /// <summary>Changes made with <c>--temp</c>. Layered over the stored entry and lost on restart.</summary>
    private sealed class SessionOverlay
    {
        public HashSet<string> AddedRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RemovedRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AddedPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RemovedPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record StoreRegistration(IDeadworksPlugin Owner, string Name, IPermissionStore Store);

    /// <summary>
    /// Stands in for a configured store no plugin has registered: no roles and no players, so nobody has any
    /// permission. Falling back to the JSON files instead could hand out access the real store has taken away.
    /// </summary>
    private sealed class UnavailableStore(string name) : IPermissionStore
    {
        public string Name { get; } = name;
        public Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, RoleDefinition>>(new Dictionary<string, RoleDefinition>());
        public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct) => Task.FromResult<PlayerEntry?>(null);
        public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct)
            => Task.FromException(new InvalidOperationException($"the '{Name}' store isn't available"));
        public event Action<ulong?>? Changed { add { } remove { } }
    }

    private static readonly Lock _lock = new();

    private static JsonPermissionStore? _jsonStore;
    private static IPermissionStore? _store;
    private static string _storeName = JsonPermissionStore.StoreName;
    private static readonly List<StoreRegistration> _registeredStores = [];
    private static string _overridesPath = "";

    private static IReadOnlyDictionary<string, RoleDefinition> _roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase);
    // null value = the store has no entry for this player.
    private static readonly Dictionary<ulong, PlayerEntry?> _players = [];
    private static readonly HashSet<ulong> _loading = [];
    private static readonly HashSet<ulong> _saving = [];
    // A failed load is retried no sooner than this (Environment.TickCount64), so a broken store isn't hit on every check.
    private static readonly Dictionary<ulong, long> _retryAfter = [];
    private static readonly Dictionary<ulong, SessionOverlay> _overlays = [];
    private static readonly Dictionary<ulong, CompiledSubject> _compiled = [];
    private static CompiledSubject? _defaultSubject;
    // Bumped on every reload so a slow load started before it can't overwrite newer data.
    private static int _generation;

    private static readonly ulong[] _slotSteamIds = new ulong[Players.MaxSlot];
    // Whether OnClientAuthorized has been dispatched for the current connection in each slot.
    private static readonly bool[] _slotAuthorizedRaised = new bool[Players.MaxSlot];

    // Plugins have loaded, so undeclared permissions can be told apart from ones a plugin hasn't declared yet.
    private static bool _startupComplete;
    private static readonly HashSet<string> _warnedUndeclared = new(StringComparer.Ordinal);

    internal const long LoadRetryMs = 30_000;

    /// <summary>When false, grants apply before Steam has validated the player. Only for LAN or testing.</summary>
    internal static bool RequireSteamAuth { get; set; } = true;

    /// <summary>Overridable so tests can stand in for the engine.</summary>
    internal static Func<int, bool> IsSlotAuthenticated { get; set; } = DefaultIsSlotAuthenticated;

    /// <summary>Whether <c>sv_lan</c> is on, where Steam never validates anyone. Overridable for tests.</summary>
    internal static Func<bool> IsLanServer { get; set; } = DefaultIsLanServer;

    /// <summary>Raised after a reload or any grant or revoke, with the affected SteamID64 or null for everyone.</summary>
    internal static event Action<ulong?>? Changed;

    public static void Initialize()
    {
        var managedDir = Path.GetDirectoryName(typeof(PermissionManager).Assembly.Location);
        var configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));
        var settings = DeadworksConfig.Permissions;
        RequireSteamAuth = settings.RequireSteamAuth;
        Initialize(Path.Combine(configsDir, "permissions"), settings.Store);
    }

    internal static void Initialize(string permissionsDir, string storeName = JsonPermissionStore.StoreName)
    {
        _jsonStore = new JsonPermissionStore(permissionsDir);
        _jsonStore.EnsureDefaultFiles();
        _overridesPath = Path.Combine(permissionsDir, "overrides.jsonc");
        _storeName = string.IsNullOrWhiteSpace(storeName) ? JsonPermissionStore.StoreName : storeName.Trim();

        lock (_lock)
        {
            _registeredStores.Clear();
            _overlays.Clear();
            _retryAfter.Clear();
            _saving.Clear();
            Array.Clear(_slotSteamIds);
            Array.Clear(_slotAuthorizedRaised);
            _startupComplete = false;
            _warnedUndeclared.Clear();
            _rolesLoaded = false;
            LastLoadError = null;
        }

        PermissionManifest.Initialize(permissionsDir);
        DeadworksManaged.Api.Permissions.Backend = new Backend();
        Players.AuthorizedResolver = IsAuthorized;

        SetStore(IsJson(_storeName) ? _jsonStore : new UnavailableStore(_storeName));
    }

    private static bool IsJson(string name) => name.Equals(JsonPermissionStore.StoreName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True while the configured store isn't registered, so nobody has any permission.</summary>
    public static bool StoreUnavailable => _store is UnavailableStore;

    public static string UnavailableMessage => _storeName == DeadworksConfig.BrokenStoreName
        ? "deadworks.jsonc has an error, so Deadworks doesn't know which permission store to use. Nobody has any permissions until it's fixed and the server restarted; the server console still works."
        : $"permissions.store is '{_storeName}', but no plugin has registered that store. Nobody has any permissions until one does; the server console still works.";

    // --- Loading ---

    /// <summary>Why the last load of roles and players failed, or null if it worked; for replies to staff who can't see the console.</summary>
    public static string? LastLoadError { get; private set; }

    // Whether roles have ever loaded: failing before that means nobody has any role at all, which deserves an ERROR.
    private static bool _rolesLoaded;

    private static void ReportLoadFailure(string? reason)
    {
        LastLoadError = reason ?? "unknown error";
        Console.WriteLine(_rolesLoaded
            ? $"[Permissions] Failed to load permissions, keeping the previous ones: {LastLoadError}"
            : $"[Permissions] ERROR: failed to load permissions: {LastLoadError.TrimEnd('.')}. Nobody has any roles until it's fixed and "
              + "dw_perm_reload is run; the server console still works.");
    }

    /// <summary>Re-reads roles, players and overrides from the active store. Returns false if anything failed to load.</summary>
    public static bool Reload()
    {
        var overridesOk = CommandOverrides.Load(_overridesPath);

        var store = _store;
        if (store == null)
            return false;

        Task<IReadOnlyDictionary<string, RoleDefinition>> task;
        try
        {
            task = store.LoadRolesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportLoadFailure(ex.Message);
            return false;
        }

        int generation;
        lock (_lock)
            generation = ++_generation;

        if (task.IsCompleted)
            return ApplyRoles(task, generation) && overridesOk;

        task.ContinueWith(t => TimerEngine.EnqueueNextTick(() => ApplyRoles(t, generation)), TaskScheduler.Default);
        return overridesOk;
    }

    private static bool ApplyRoles(Task<IReadOnlyDictionary<string, RoleDefinition>> task, int generation)
    {
        if (!task.IsCompletedSuccessfully)
        {
            ReportLoadFailure(task.Exception?.GetBaseException().Message);
            return false;
        }

        Dictionary<string, RoleDefinition> roles;
        try
        {
            roles = Normalize(task.Result);
        }
        catch (Exception ex)
        {
            ReportLoadFailure(ex.Message);
            return false;
        }

        ulong[] online;
        var firstLoad = false;
        lock (_lock)
        {
            if (generation != _generation)
                return false;

            firstLoad = !_rolesLoaded;
            _roles = roles;
            _rolesLoaded = true;
            LastLoadError = null;
            _players.Clear();
            _loading.Clear();
            _retryAfter.Clear();
            InvalidateAll();
            online = _slotSteamIds.Where(id => id != 0).Distinct().ToArray();
        }

        foreach (var warning in PermissionEvaluator.Validate(_roles))
            Console.WriteLine($"[Permissions] {warning}");

        foreach (var id in online)
            EnsurePlayerLoaded(id);

        if (StoreUnavailable)
            Console.WriteLine($"[Permissions] WARNING: {UnavailableMessage}");
        else
            Console.WriteLine($"[Permissions] Loaded {_roles.Count} roles from the '{_storeName}' store");

        // A fresh server has nobody to run admin commands in game; say how, where the owner is looking right now.
        if (firstLoad && ReferenceEquals(_store, _jsonStore) && _jsonStore!.AllPlayers().Count == 0)
            Console.WriteLine("[Permissions] No admins yet. Join the server, then run this here: dw_role_grant <your name> admin");

        PermissionManifest.WriteAll();
        if (_startupComplete)
            WarnAboutConfig();
        Changed?.Invoke(null);
        return true;
    }

    /// <summary>
    /// A clean copy of what a store returned: names compared ignoring case, and no null lists or entries, which a
    /// hand-edited <c>"permissions": null</c> or a custom store could otherwise hand us.
    /// </summary>
    private static Dictionary<string, RoleDefinition> Normalize(IReadOnlyDictionary<string, RoleDefinition>? source)
    {
        var roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in source ?? new Dictionary<string, RoleDefinition>())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (roles.ContainsKey(name))
                Console.WriteLine($"[Permissions] role '{name}' is defined more than once (role names ignore case); using the last one");
            roles[name] = new RoleDefinition
            {
                Permissions = CleanList(role?.Permissions),
                Inherits = CleanList(role?.Inherits),
                Immunity = role?.Immunity
            };
        }
        return roles;
    }

    private static PlayerEntry? Normalize(PlayerEntry? entry) => entry == null ? null : new PlayerEntry
    {
        Name = entry.Name,
        Roles = CleanList(entry.Roles),
        Permissions = CleanList(entry.Permissions),
        Immunity = entry.Immunity
    };

    private static List<string> CleanList(List<string>? list) => list?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [];

    /// <summary>
    /// Whether the store's answer for this player has arrived (an entry, or that there is none), starting to load it if
    /// nothing has asked yet. The JSON store answers at once, so it's true straight away there.
    /// </summary>
    public static bool IsLoaded(ulong steamId64)
    {
        if (steamId64 == 0 || HasArrived(steamId64))
            return true;
        EnsurePlayerLoaded(steamId64);
        return HasArrived(steamId64);
    }

    /// <summary>
    /// Players in players.jsonc whose saved name is exactly <paramref name="name"/> (ignoring case), for managing staff
    /// who aren't on the server by name. Empty for custom stores, which may not keep names at all.
    /// </summary>
    public static List<(ulong SteamId64, string Name)> FindSavedByName(string name)
        => ReferenceEquals(_store, _jsonStore) && _jsonStore != null
            ? _jsonStore.AllPlayers().Where(p => string.Equals(p.Entry.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Id, p.Entry.Name!)).ToList()
            : [];

    /// <summary>The name saved with this player's entry, if their entry has arrived and has one.</summary>
    public static string? SavedName(ulong steamId64)
    {
        lock (_lock)
            return _players.TryGetValue(steamId64, out var entry) ? entry?.Name : null;
    }

    /// <summary>Whether the entry is in memory, without starting a load.</summary>
    internal static bool HasArrived(ulong steamId64)
    {
        lock (_lock)
            return _players.ContainsKey(steamId64);
    }

    /// <summary>Returns the player's stored entry, starting a load if it hasn't been fetched. Null while loading or absent.</summary>
    private static PlayerEntry? EnsurePlayerLoaded(ulong steamId64)
    {
        IPermissionStore? store;
        int generation;
        lock (_lock)
        {
            if (_players.TryGetValue(steamId64, out var cached))
                return cached;
            if (_retryAfter.TryGetValue(steamId64, out var retryAt) && Environment.TickCount64 < retryAt)
                return null;
            if (!_loading.Add(steamId64))
                return null;
            store = _store;
            generation = _generation;
        }

        if (store == null)
            return null;

        Task<PlayerEntry?> task;
        try
        {
            task = store.LoadPlayerAsync(steamId64, CancellationToken.None);
        }
        catch (Exception ex)
        {
            task = Task.FromException<PlayerEntry?>(ex);
        }

        if (task.IsCompleted)
            return ApplyPlayer(steamId64, task, generation, raise: false);

        task.ContinueWith(t => TimerEngine.EnqueueNextTick(() => ApplyPlayer(steamId64, t, generation, raise: true)), TaskScheduler.Default);
        return null;
    }

    private static PlayerEntry? ApplyPlayer(ulong steamId64, Task<PlayerEntry?> task, int generation, bool raise)
    {
        PlayerEntry? entry = null;
        if (task.IsCompletedSuccessfully)
            entry = Normalize(task.Result);
        else
            Console.WriteLine($"[Permissions] Failed to load player {steamId64}, retrying in {LoadRetryMs / 1000}s: {task.Exception?.GetBaseException().Message}");

        lock (_lock)
        {
            _loading.Remove(steamId64);
            if (generation != _generation)
                return null;
            if (task.IsCompletedSuccessfully)
            {
                _players[steamId64] = entry;
                _retryAfter.Remove(steamId64);
            }
            else
            {
                _retryAfter[steamId64] = Environment.TickCount64 + LoadRetryMs;
            }
            _compiled.Remove(steamId64);
        }

        if (entry != null)
        {
            foreach (var warning in PermissionEvaluator.Validate(steamId64, entry, _roles))
                Console.WriteLine($"[Permissions] {warning}");
            if (_startupComplete)
                foreach (var grant in PermissionEvaluator.UnknownGrants(entry.Permissions, PermissionManifest.DeclaredPermissions()))
                    Console.WriteLine($"[Permissions] player {steamId64} has '{grant}', which no loaded plugin declares (a typo, or a plugin that isn't installed?)");
        }

        if (raise)
            Changed?.Invoke(steamId64);
        return entry;
    }

    // --- Evaluation ---

    private static CompiledSubject GetSubject(ulong steamId64)
    {
        if (steamId64 == 0)
            return GetDefaultSubject();

        lock (_lock)
        {
            if (_compiled.TryGetValue(steamId64, out var cached))
                return cached;
        }

        var stored = EnsurePlayerLoaded(steamId64);

        lock (_lock)
        {
            var entry = ApplyOverlay(stored, _overlays.GetValueOrDefault(steamId64));
            var subject = PermissionEvaluator.Compile(_roles, entry);
            // Don't cache while a load is in flight or waiting to be retried; the player would be stuck on "default".
            if (_players.ContainsKey(steamId64))
                _compiled[steamId64] = subject;
            return subject;
        }
    }

    private static CompiledSubject GetDefaultSubject()
    {
        lock (_lock)
            return _defaultSubject ??= PermissionEvaluator.Compile(_roles, null);
    }

    /// <summary>What a connected player is checked as: themselves once Steam validated them, "default" before that.</summary>
    private static CompiledSubject GetSlotSubject(int slot, out ulong steamId64, out bool authenticated)
    {
        steamId64 = (uint)slot < (uint)_slotSteamIds.Length ? _slotSteamIds[slot] : 0;
        authenticated = steamId64 != 0 && IsTrusted(slot);
        return authenticated ? GetSubject(steamId64) : GetDefaultSubject();
    }

    /// <summary>Whether the SteamID in <paramref name="slot"/> may be used for grants: Steam confirmed it, or nothing will.</summary>
    private static bool IsTrusted(int slot) => !RequireSteamAuth || IsLanServer() || IsSlotAuthenticated(slot);

    private static PlayerEntry? ApplyOverlay(PlayerEntry? stored, SessionOverlay? overlay)
    {
        if (overlay == null)
            return stored;

        var entry = stored?.Clone() ?? new PlayerEntry();
        entry.Roles = entry.Roles.Where(r => !overlay.RemovedRoles.Contains(r)).Concat(overlay.AddedRoles)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        entry.Permissions = entry.Permissions.Where(p => !overlay.RemovedPermissions.Contains(p)).Concat(overlay.AddedPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return entry;
    }

    // Must be called under _lock.
    private static void InvalidateAll()
    {
        _compiled.Clear();
        _defaultSubject = null;
    }

    /// <summary>
    /// What a SteamID may do: its saved entry, unless it belongs to a player on the server whom Steam hasn't confirmed
    /// yet, who gets "default" exactly as their slot does. Otherwise a plugin checking caller.SteamId64 would hand out a
    /// player's permissions before their ticket was validated.
    /// </summary>
    private static CompiledSubject GetActingSubject(ulong steamId64)
    {
        var slot = FindSlot(steamId64);
        return slot >= 0 && !IsTrusted(slot) ? GetDefaultSubject() : GetSubject(steamId64);
    }

    public static PermissionExplanation Explain(ulong steamId64, string permission)
        => PermissionEvaluator.Evaluate(GetActingSubject(steamId64), permission);

    public static PermissionExplanation ExplainSlot(int slot, string permission)
    {
        var subject = GetSlotSubject(slot, out var steamId64, out var authenticated);
        var result = PermissionEvaluator.Evaluate(subject, permission);
        if (!authenticated && steamId64 != 0 && !result.Allowed)
            return result with { Source = "unauthenticated (Steam hasn't validated this player yet, so only \"default\" applies)" };
        return result;
    }

    public static bool HasForSlot(int slot, string permission)
        => PermissionEvaluator.Evaluate(GetSlotSubject(slot, out _, out _), permission).Allowed;

    public static bool CanTargetSlots(int callerSlot, int targetSlot)
    {
        if (callerSlot < 0 || callerSlot == targetSlot)
            return true;
        var caller = GetSlotSubject(callerSlot, out _, out _);
        // The target is judged by their saved entry even before Steam confirms them, as CanTarget(ulong) does: a freshly
        // joined admin, or every admin while Steam is down, must not drop to default's immunity and become kickable.
        var targetId = GetSlotSteamId(targetSlot);
        if (targetId == 0)
            return GetDefaultSubject().Immunity <= caller.Immunity;
        var target = GetSubject(targetId);
        // Until their entry arrives, their immunity is unknown; treating it as 0 would fail open.
        if (!IsLoaded(targetId))
            return false;
        return target.Immunity <= caller.Immunity;
    }

    public static bool CanTarget(ulong caller, ulong target)
    {
        if (caller == target)
            return true;
        // The target is judged by their saved entry even before Steam confirms them, which errs towards protecting them;
        // the caller only acts with what Steam has confirmed.
        var targetSubject = GetSubject(target);
        if (target != 0 && !IsLoaded(target))
            return false;
        return targetSubject.Immunity <= GetActingSubject(caller).Immunity;
    }

    public static CompiledSubject Describe(ulong steamId64) => GetSubject(steamId64);

    public static CompiledSubject DescribeSlot(int slot, out bool authenticated)
        => GetSlotSubject(slot, out _, out authenticated);

    public static IReadOnlyDictionary<string, RoleDefinition> Roles
    {
        get { lock (_lock) return _roles; }
    }

    public static bool IsTemporary(ulong steamId64)
    {
        lock (_lock)
            return _overlays.ContainsKey(steamId64);
    }

    // --- Config warnings ---

    /// <summary>Called once plugins have loaded: from now on, permissions nobody declares are worth a warning.</summary>
    public static void OnStartupComplete()
    {
        _startupComplete = true;
        WarnAboutConfig();
    }

    /// <summary>Grants and overrides that match nothing any loaded plugin declares or registers.</summary>
    private static void WarnAboutConfig()
    {
        var declared = PermissionManifest.DeclaredPermissions();
        foreach (var (name, role) in Roles)
            foreach (var grant in PermissionEvaluator.UnknownGrants(role.Permissions, declared))
                Console.WriteLine($"[Permissions] role '{name}' has '{grant}', which no loaded plugin declares (a typo, or a plugin that isn't installed?)");

        List<(ulong Id, PlayerEntry Entry)> players;
        if (_store is JsonPermissionStore json)
            players = json.AllPlayers();
        else
            lock (_lock)
                players = _players.Where(kv => kv.Value != null).Select(kv => (kv.Key, kv.Value!)).ToList();
        foreach (var (id, entry) in players)
            foreach (var grant in PermissionEvaluator.UnknownGrants(entry.Permissions, declared))
                Console.WriteLine($"[Permissions] player {id} has '{grant}', which no loaded plugin declares (a typo, or a plugin that isn't installed?)");

        foreach (var key in CommandOverrides.UnknownKeys(PermissionManifest.AllCommands()))
            Console.WriteLine($"[Permissions] overrides.jsonc: '{key}' doesn't match any command. Check the name in generated/<Plugin>.jsonc.");
    }

    /// <summary>Warns once when a plugin checks a permission that no loaded plugin declares, which is usually a typo.</summary>
    private static void NoteChecked(string permission)
    {
        if (!_startupComplete)
            return;
        var normalized = PermissionEvaluator.Normalize(permission);
        if (normalized.Length == 0 || PermissionManifest.DeclaredPermissions().Contains(normalized))
            return;
        lock (_lock)
            if (!_warnedUndeclared.Add(normalized))
                return;
        // A plugin checking its own permission in OnLoad declares it moments later, when its commands are registered.
        TimerEngine.EnqueueNextTick(() =>
        {
            if (!PermissionManifest.DeclaredPermissions().Contains(normalized))
                Console.WriteLine($"[Permissions] A plugin checked '{normalized}', which no loaded plugin declares. If it isn't a typo, "
                                  + "list it with [DeclarePermission] so server owners can find it.");
        });
    }

    // --- Identity ---

    public static void OnClientConnect(int slot, ulong steamId64)
    {
        if ((uint)slot >= (uint)_slotSteamIds.Length)
            return;
        lock (_lock)
        {
            // A map change runs connect again for everyone staying, with no disconnect in between. The engine keeps
            // their confirmation, so OnClientAuthorized isn't raised a second time for the same player.
            if (_slotSteamIds[slot] != steamId64)
                _slotAuthorizedRaised[slot] = false;
            _slotSteamIds[slot] = steamId64;
        }
        if (steamId64 != 0)
            EnsurePlayerLoaded(steamId64);
    }

    public static void OnClientPutInServer(int slot, bool isBot)
    {
        if (isBot && (uint)slot < (uint)_slotSteamIds.Length)
            lock (_lock)
                _slotSteamIds[slot] = 0;
    }

    public static void OnClientDisconnect(int slot)
    {
        if ((uint)slot >= (uint)_slotSteamIds.Length)
            return;
        lock (_lock)
        {
            var id = _slotSteamIds[slot];
            _slotSteamIds[slot] = 0;
            _slotAuthorizedRaised[slot] = false;
            // Forget their entry (not their --temp changes) unless they're in another slot or mid-save, so the next
            // connect reads the store afresh and the cache doesn't grow with everyone who ever joined.
            if (id != 0 && Array.IndexOf(_slotSteamIds, id) < 0 && !_loading.Contains(id) && !_saving.Contains(id))
            {
                _players.Remove(id);
                _compiled.Remove(id);
                _retryAfter.Remove(id);
            }
        }
    }

    /// <summary>Whether the player in <paramref name="slot"/> has a trustworthy SteamID (see <see cref="Players.IsAuthorized"/>).</summary>
    public static bool IsAuthorized(int slot) => GetSlotSteamId(slot) != 0 && IsTrusted(slot);

    /// <summary>
    /// The slot of the connected player with this SteamID, or -1. If two slots claim it (one of them spoofed), the one
    /// Steam confirmed wins.
    /// </summary>
    public static int FindSlot(ulong steamId64)
    {
        if (steamId64 == 0)
            return -1;
        var found = -1;
        for (int slot = 0; slot < _slotSteamIds.Length; slot++)
        {
            if (GetSlotSteamId(slot) != steamId64)
                continue;
            if (IsAuthorized(slot))
                return slot;
            if (found < 0)
                found = slot;
        }
        return found;
    }

    /// <summary>Slots that became authorized since the last call, each reported once per connection.</summary>
    public static List<(int Slot, ulong SteamId64)> TakeNewlyAuthorized()
    {
        var result = new List<(int, ulong)>();
        for (int slot = 0; slot < _slotSteamIds.Length; slot++)
        {
            ulong id;
            lock (_lock)
            {
                id = _slotSteamIds[slot];
                if (id == 0 || _slotAuthorizedRaised[slot])
                    continue;
            }
            if (!IsAuthorized(slot))
                continue;
            lock (_lock)
                _slotAuthorizedRaised[slot] = true;
            result.Add((slot, id));
        }

        // Until now these players only had default; their own roles apply from here on, which is a change like any
        // grant. (With the Steam wait off, they applied at connect and nothing changes now.) Only asked once someone is
        // confirmed: this runs every tick, and asking reads sv_lan for good, which must wait until server.cfg has run.
        if (result.Count > 0 && RequireSteamAuth && !IsLanServer())
            foreach (var (_, id) in result)
                Changed?.Invoke(id);
        return result;
    }

    public static ulong GetSlotSteamId(int slot)
        => (uint)slot < (uint)_slotSteamIds.Length ? _slotSteamIds[slot] : 0;

    internal static void SetSlotSteamIdForTests(int slot, ulong steamId64) => _slotSteamIds[slot] = steamId64;

    private static bool _warnedNoAuthHook;

    private static unsafe bool DefaultIsSlotAuthenticated(int slot)
    {
        // A native build without the hook can't say who Steam confirmed, so nobody counts as confirmed.
        if (NativeInterop.IsClientAuthenticated == null)
        {
            if (!_warnedNoAuthHook)
            {
                _warnedNoAuthHook = true;
                Console.WriteLine("[Permissions] WARNING: this deadworks native build can't tell when Steam confirms a player, so nobody's "
                                  + "roles apply. Update Deadworks, or set permissions.require_steam_auth to false on a server nobody untrusted can reach.");
            }
            return false;
        }
        return NativeInterop.IsClientAuthenticated(slot) != 0;
    }

    private static bool? _svLan;

    private static unsafe bool DefaultIsLanServer()
    {
        if (_svLan is { } latched)
            return latched;
        if (NativeInterop.FindConVar == null)
            return false;
        // Read once, when the first player connects (server.cfg has run by then), and kept until restart: otherwise
        // anyone allowed to change cvars could turn sv_lan on and have unconfirmed SteamIDs trusted.
        _svLan = ConVar.Find("sv_lan")?.GetBool() ?? false;
        if (_svLan.Value)
            Console.WriteLine("[Permissions] sv_lan is on, so players' roles apply without waiting for Steam. Changing sv_lan needs a restart to take effect here.");
        return _svLan.Value;
    }

    // --- Changes ---

    internal enum ChangeKind { GrantRole, RevokeRole, GrantPermission, RevokePermission }

    /// <summary>
    /// Applies a management change. Completes with an error for the caller, or null once the change is saved; the
    /// change only takes effect after the store accepts it. A store that saves asynchronously completes on a later frame.
    /// </summary>
    public static Task<string?> ChangeAsync(ulong steamId64, ChangeKind kind, string value, bool temporary, string? nameHint = null)
    {
        value = value.Trim();
        if (kind is ChangeKind.GrantPermission or ChangeKind.RevokePermission)
        {
            if (!Grant.TryParse(value, out _, out var error))
                return Task.FromResult<string?>(error);
            value = value.ToLowerInvariant();
        }
        else if (kind == ChangeKind.GrantRole && !Roles.ContainsKey(value))
        {
            return Task.FromResult<string?>($"There is no role '{value}'. Roles: {string.Join(", ", Roles.Keys)}");
        }
        else if (value.Equals(PermissionEvaluator.DefaultRole, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>("Everyone has the default role already.");
        }

        EnsurePlayerLoaded(steamId64);
        if (!IsLoaded(steamId64))
            return Task.FromResult<string?>("That player's permissions are still loading; try again in a moment.");

        if (temporary)
        {
            lock (_lock)
            {
                if (!_overlays.TryGetValue(steamId64, out var overlay))
                    _overlays[steamId64] = overlay = new SessionOverlay();
                var (add, remove) = kind switch
                {
                    ChangeKind.GrantRole => (overlay.AddedRoles, overlay.RemovedRoles),
                    ChangeKind.RevokeRole => (overlay.RemovedRoles, overlay.AddedRoles),
                    ChangeKind.GrantPermission => (overlay.AddedPermissions, overlay.RemovedPermissions),
                    _ => (overlay.RemovedPermissions, overlay.AddedPermissions),
                };
                remove.Remove(value);
                add.Add(value);
                _compiled.Remove(steamId64);
            }
            Changed?.Invoke(steamId64);
            return Task.FromResult<string?>(null);
        }

        IPermissionStore? store;
        lock (_lock)
        {
            if (_loading.Contains(steamId64))
                return Task.FromResult<string?>("That player's permissions are still loading; try again in a moment.");
            if (_saving.Contains(steamId64))
                return Task.FromResult<string?>("A change to that player is still being saved; try again in a moment.");
            store = _store;
        }
        if (store == null || StoreUnavailable)
            return Task.FromResult<string?>($"Can't save: {UnavailableMessage}");

        var stored = EnsurePlayerLoaded(steamId64);
        lock (_lock)
        {
            if (!_players.ContainsKey(steamId64))
                return Task.FromResult<string?>("That player's permissions are still loading; try again in a moment.");
        }

        var entry = stored?.Clone() ?? new PlayerEntry();
        entry.Name ??= nameHint;
        var list = kind is ChangeKind.GrantRole or ChangeKind.RevokeRole ? entry.Roles : entry.Permissions;
        var existing = list.FindIndex(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));

        if (kind is ChangeKind.GrantRole or ChangeKind.GrantPermission)
        {
            if (existing >= 0)
                return Task.FromResult<string?>($"Already has {value}.");
            list.Add(value);
        }
        else
        {
            if (existing < 0)
                return Task.FromResult<string?>($"Doesn't have {value} in the saved config{(IsTemporary(steamId64) ? " (it may be a --temp change; use --temp to revoke it)" : "")}.");
            list.RemoveAt(existing);
        }

        var isEmpty = entry.Roles.Count == 0 && entry.Permissions.Count == 0 && entry.Immunity == null;
        int generation;
        lock (_lock)
            generation = _generation;

        Task save;
        try
        {
            save = store.SavePlayerAsync(steamId64, isEmpty ? null : entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            save = Task.FromException(ex);
        }

        string? Finish(Task t)
        {
            lock (_lock)
                _saving.Remove(steamId64);
            if (!t.IsCompletedSuccessfully)
                return $"Failed to save, so nothing changed: {t.Exception?.GetBaseException().Message ?? "the save was cancelled"}";

            lock (_lock)
            {
                // If the store was reloaded or swapped while this was saving, its data is newer than ours; the next
                // load of this player picks the change up from the store instead.
                if (generation == _generation && ReferenceEquals(_store, store))
                    _players[steamId64] = isEmpty ? null : entry;
                // A saved change replaces any --temp change to the same value.
                if (_overlays.TryGetValue(steamId64, out var overlay))
                {
                    overlay.AddedRoles.Remove(value);
                    overlay.RemovedRoles.Remove(value);
                    overlay.AddedPermissions.Remove(value);
                    overlay.RemovedPermissions.Remove(value);
                }
                _compiled.Remove(steamId64);
            }
            Changed?.Invoke(steamId64);
            return null;
        }

        if (save.IsCompleted)
            return Task.FromResult(Finish(save));

        lock (_lock)
            _saving.Add(steamId64);
        var done = new TaskCompletionSource<string?>();
        save.ContinueWith(t => TimerEngine.EnqueueNextTick(() => done.SetResult(Finish(t))), TaskScheduler.Default);
        return done.Task;
    }

    // --- Stores ---

    public static void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store)
    {
        lock (_lock)
        {
            _registeredStores.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _registeredStores.Add(new StoreRegistration(owner, name, store));
        }

        Console.WriteLine($"[Permissions] {owner.Name} registered the '{name}' store");
        if (name.Equals(_storeName, StringComparison.OrdinalIgnoreCase))
        {
            // Registration runs in the plugin's OnLoad, which hot reload calls off the game thread; the reload
            // notifies plugins, so it waits for the next tick. Until then nobody gains anything.
            SetStore(store, reload: false);
            TimerEngine.EnqueueNextTick(() => Reload());
        }
    }

    /// <summary>Drops stores registered by plugins that are unloading. The active one leaves nobody with permissions until it's back.</summary>
    public static void UnregisterStoresOwnedBy(IReadOnlyCollection<IDeadworksPlugin> plugins)
    {
        bool activeRemoved;
        lock (_lock)
        {
            var removed = _registeredStores.Where(r => plugins.Contains(r.Owner)).ToList();
            if (removed.Count == 0)
                return;
            _registeredStores.RemoveAll(removed.Contains);
            activeRemoved = removed.Any(r => ReferenceEquals(r.Store, _store));
        }

        if (activeRemoved)
        {
            Console.WriteLine($"[Permissions] The '{_storeName}' store was unloaded");
            // Unloads can run off the game thread (hot reload): drop everyone's access now, and do the reload, which
            // notifies plugins, on the next tick.
            SetStore(new UnavailableStore(_storeName), reload: false);
            lock (_lock)
            {
                _roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase);
                _players.Clear();
                _loading.Clear();
                _retryAfter.Clear();
                ++_generation;
                InvalidateAll();
            }
            TimerEngine.EnqueueNextTick(() => Reload());
        }
    }

    private static void SetStore(IPermissionStore store, bool reload = true)
    {
        var previous = _store;
        if (previous != null)
            previous.Changed -= OnStoreChanged;
        _store = store;
        store.Changed += OnStoreChanged;
        if (reload)
            Reload();
    }

    private static void OnStoreChanged(ulong? steamId64)
    {
        TimerEngine.EnqueueNextTick(() =>
        {
            if (steamId64 is not { } id)
            {
                Reload();
                return;
            }
            lock (_lock)
            {
                _players.Remove(id);
                _retryAfter.Remove(id);
                _compiled.Remove(id);
            }
            EnsurePlayerLoaded(id);
            Changed?.Invoke(id);
        });
    }

    private sealed class Backend : IPermissionBackend
    {
        public bool Has(ulong steamId64, string permission)
        {
            NoteChecked(permission);
            return Explain(steamId64, permission).Allowed;
        }

        public bool HasForSlot(int slot, string permission)
        {
            NoteChecked(permission);
            return PermissionManager.HasForSlot(slot, permission);
        }

        public PermissionExplanation Explain(ulong steamId64, string permission) => PermissionManager.Explain(steamId64, permission);
        public bool CanTarget(ulong caller, ulong target) => PermissionManager.CanTarget(caller, target);
        public bool CanTargetSlots(int callerSlot, int targetSlot) => PermissionManager.CanTargetSlots(callerSlot, targetSlot);
        public int GetImmunity(ulong steamId64) => GetSubject(steamId64).Immunity;
        public bool IsLoaded(ulong steamId64) => PermissionManager.IsLoaded(steamId64);
        // Roles grant things, so they wait for Steam like Has does; immunity protects, so it doesn't (see CanTargetSlots).
        public IReadOnlyList<string> GetRoles(ulong steamId64) => GetActingSubject(steamId64).AssignedRoles;
        public ulong GetSlotSteamId(int slot) => PermissionManager.GetSlotSteamId(slot);
        public void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store) => PermissionManager.RegisterStore(owner, name, store);
    }
}
