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
    private static readonly Dictionary<ulong, SessionOverlay> _overlays = [];
    private static readonly Dictionary<ulong, CompiledSubject> _compiled = [];
    private static CompiledSubject? _defaultSubject;
    // Bumped on every reload so a slow load started before it can't overwrite newer data.
    private static int _generation;

    private static readonly ulong[] _slotSteamIds = new ulong[Players.MaxSlot];

    /// <summary>When false, grants apply before Steam has validated the player. Only for LAN or testing.</summary>
    internal static bool RequireSteamAuth { get; set; } = true;

    /// <summary>Overridable so tests can stand in for the engine.</summary>
    internal static Func<int, bool> IsSlotAuthenticated { get; set; } = DefaultIsSlotAuthenticated;

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
            Array.Clear(_slotSteamIds);
        }

        PermissionManifest.Initialize(permissionsDir);
        DeadworksManaged.Api.Permissions.Backend = new Backend();

        if (!_storeName.Equals(JsonPermissionStore.StoreName, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[Permissions] Using the JSON store until a plugin registers the '{_storeName}' store");
        SetStore(_jsonStore);
    }

    // --- Loading ---

    /// <summary>Re-reads roles, players and overrides from the active store. Returns false if anything failed to load.</summary>
    public static bool Reload()
    {
        CommandOverrides.Load(_overridesPath);

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
            Console.WriteLine($"[Permissions] Failed to load roles: {ex.Message}");
            return false;
        }

        int generation;
        lock (_lock)
            generation = ++_generation;

        if (task.IsCompleted)
            return ApplyRoles(task, generation);

        task.ContinueWith(t => TimerEngine.EnqueueNextTick(() => ApplyRoles(t, generation)), TaskScheduler.Default);
        return true;
    }

    private static bool ApplyRoles(Task<IReadOnlyDictionary<string, RoleDefinition>> task, int generation)
    {
        if (!task.IsCompletedSuccessfully)
        {
            Console.WriteLine($"[Permissions] Failed to load roles, keeping the previous ones: {task.Exception?.GetBaseException().Message}");
            return false;
        }

        ulong[] online;
        lock (_lock)
        {
            if (generation != _generation)
                return false;

            _roles = new Dictionary<string, RoleDefinition>(task.Result, StringComparer.OrdinalIgnoreCase);
            _players.Clear();
            _loading.Clear();
            InvalidateAll();
            online = _slotSteamIds.Where(id => id != 0).Distinct().ToArray();
        }

        foreach (var warning in PermissionEvaluator.Validate(_roles))
            Console.WriteLine($"[Permissions] {warning}");

        foreach (var id in online)
            EnsurePlayerLoaded(id);

        Console.WriteLine($"[Permissions] Loaded {_roles.Count} roles from the '{_storeName}' store");
        PermissionManifest.WriteAll();
        DeadworksManaged.Api.Permissions.RaiseChanged(null);
        return true;
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
            entry = task.Result;
        else
            Console.WriteLine($"[Permissions] Failed to load player {steamId64}: {task.Exception?.GetBaseException().Message}");

        lock (_lock)
        {
            _loading.Remove(steamId64);
            if (generation != _generation)
                return null;
            // A failed load is not cached, so the next check retries.
            if (task.IsCompletedSuccessfully)
                _players[steamId64] = entry;
            _compiled.Remove(steamId64);
        }

        if (entry != null)
            foreach (var warning in PermissionEvaluator.Validate(steamId64, entry, _roles))
                Console.WriteLine($"[Permissions] {warning}");

        if (raise)
            DeadworksManaged.Api.Permissions.RaiseChanged(steamId64);
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
            // Don't cache while a load is in flight; the player would be stuck on "default".
            if (!_loading.Contains(steamId64))
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
        authenticated = steamId64 != 0 && (!RequireSteamAuth || IsSlotAuthenticated(slot));
        return authenticated ? GetSubject(steamId64) : GetDefaultSubject();
    }

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

    public static PermissionExplanation Explain(ulong steamId64, string permission)
        => PermissionEvaluator.Evaluate(GetSubject(steamId64), permission);

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
        var target = GetSlotSubject(targetSlot, out _, out _);
        return target.Immunity <= caller.Immunity;
    }

    public static bool CanTarget(ulong caller, ulong target)
        => caller == target || GetSubject(target).Immunity <= GetSubject(caller).Immunity;

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

    // --- Identity ---

    public static void OnClientConnect(int slot, ulong steamId64)
    {
        if ((uint)slot >= (uint)_slotSteamIds.Length)
            return;
        lock (_lock)
            _slotSteamIds[slot] = steamId64;
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
            _slotSteamIds[slot] = 0;
    }

    public static ulong GetSlotSteamId(int slot)
        => (uint)slot < (uint)_slotSteamIds.Length ? _slotSteamIds[slot] : 0;

    internal static void SetSlotSteamIdForTests(int slot, ulong steamId64) => _slotSteamIds[slot] = steamId64;

    private static unsafe bool DefaultIsSlotAuthenticated(int slot)
    {
        // Without the native hook (tests, or an older native build) there is nothing to wait for.
        if (NativeInterop.IsClientAuthenticated == null)
            return true;
        return NativeInterop.IsClientAuthenticated(slot) != 0;
    }

    // --- Changes ---

    internal enum ChangeKind { GrantRole, RevokeRole, GrantPermission, RevokePermission }

    /// <summary>Applies a management change. Returns an error for the caller, or null on success.</summary>
    public static string? Change(ulong steamId64, ChangeKind kind, string value, bool temporary, string? nameHint = null)
    {
        value = value.Trim();
        if (kind is ChangeKind.GrantPermission or ChangeKind.RevokePermission)
        {
            if (!Grant.TryParse(value, out _, out var error))
                return error;
            value = value.ToLowerInvariant();
        }
        else if (!_roles.ContainsKey(value))
        {
            return $"There is no role '{value}'. Roles: {string.Join(", ", _roles.Keys)}";
        }
        else if (value.Equals(PermissionEvaluator.DefaultRole, StringComparison.OrdinalIgnoreCase))
        {
            return "Everyone has the default role already.";
        }

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
            DeadworksManaged.Api.Permissions.RaiseChanged(steamId64);
            return null;
        }

        PlayerEntry? stored;
        IPermissionStore? store;
        lock (_lock)
        {
            if (_loading.Contains(steamId64))
                return "That player's permissions are still loading; try again in a moment.";
            store = _store;
        }
        stored = EnsurePlayerLoaded(steamId64);
        lock (_lock)
        {
            if (_loading.Contains(steamId64))
                return "That player's permissions are still loading; try again in a moment.";
        }

        var entry = stored?.Clone() ?? new PlayerEntry();
        entry.Name ??= nameHint;
        var list = kind is ChangeKind.GrantRole or ChangeKind.RevokeRole ? entry.Roles : entry.Permissions;
        var existing = list.FindIndex(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));

        if (kind is ChangeKind.GrantRole or ChangeKind.GrantPermission)
        {
            if (existing >= 0)
                return $"Already has {value}.";
            list.Add(value);
        }
        else
        {
            if (existing < 0)
                return $"Doesn't have {value} in the saved config{(IsTemporary(steamId64) ? " (it may be a --temp change; use --temp to revoke it)" : "")}.";
            list.RemoveAt(existing);
        }

        var isEmpty = entry.Roles.Count == 0 && entry.Permissions.Count == 0 && entry.Immunity == null;
        try
        {
            store?.SavePlayerAsync(steamId64, isEmpty ? null : entry, CancellationToken.None)
                .ContinueWith(t => Console.WriteLine($"[Permissions] Failed to save player {steamId64}: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            return $"Failed to save: {ex.Message}";
        }

        lock (_lock)
        {
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

        DeadworksManaged.Api.Permissions.RaiseChanged(steamId64);
        return null;
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
            SetStore(store);
    }

    /// <summary>Drops stores registered by plugins that are unloading, falling back to the JSON store.</summary>
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
            Console.WriteLine($"[Permissions] The '{_storeName}' store was unloaded; falling back to the JSON store");
            SetStore(_jsonStore!);
        }
    }

    private static void SetStore(IPermissionStore store)
    {
        var previous = _store;
        if (previous != null)
            previous.Changed -= OnStoreChanged;
        _store = store;
        store.Changed += OnStoreChanged;
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
                _compiled.Remove(id);
            }
            EnsurePlayerLoaded(id);
            DeadworksManaged.Api.Permissions.RaiseChanged(id);
        });
    }

    private sealed class Backend : IPermissionBackend
    {
        public bool Has(ulong steamId64, string permission) => Explain(steamId64, permission).Allowed;
        public bool HasForSlot(int slot, string permission) => PermissionManager.HasForSlot(slot, permission);
        public PermissionExplanation Explain(ulong steamId64, string permission) => PermissionManager.Explain(steamId64, permission);
        public bool CanTarget(ulong caller, ulong target) => PermissionManager.CanTarget(caller, target);
        public bool CanTargetSlots(int callerSlot, int targetSlot) => PermissionManager.CanTargetSlots(callerSlot, targetSlot);
        public int GetImmunity(ulong steamId64) => GetSubject(steamId64).Immunity;
        public IReadOnlyList<string> GetRoles(ulong steamId64) => GetSubject(steamId64).AssignedRoles;
        public ulong GetSlotSteamId(int slot) => PermissionManager.GetSlotSteamId(slot);
        public void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store) => PermissionManager.RegisterStore(owner, name, store);
    }
}
