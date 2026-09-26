using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.AdminSystem;

/// <summary>
/// Holds the active bans, gags and mutes, and enforces them: bans at connect and again once Steam validates the player,
/// gags in chat dispatch. Storage is the active <see cref="IPenaltyStore"/>.
/// </summary>
internal static class PenaltyManager
{
    private sealed record StoreRegistration(IDeadworksPlugin Owner, string Name, IPenaltyStore Store);

    private static readonly Lock _lock = new();
    private static List<Penalty> _active = [];
    private static JsonPenaltyStore? _jsonStore;
    private static IPenaltyStore? _store;
    private static string _storeName = JsonPenaltyStore.StoreName;
    private static readonly List<StoreRegistration> _registered = [];
    private static int _generation;

    /// <summary>Overridable so tests can move time.</summary>
    internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public static void Initialize()
    {
        var configsDir = Path.Combine(DeadworksConfig.BaseDir, "configs");
        var settings = DeadworksConfig.Penalties;
        Initialize(Path.Combine(configsDir, "penalties"), settings.Store, settings.HistoryDays);
    }

    internal static void Initialize(string dir, string storeName = JsonPenaltyStore.StoreName, int historyDays = 90)
    {
        _jsonStore = new JsonPenaltyStore(Path.Combine(dir, "penalties.jsonc"), historyDays, () => Now());
        _storeName = string.IsNullOrWhiteSpace(storeName) ? JsonPenaltyStore.StoreName : storeName.Trim();
        lock (_lock)
        {
            _registered.Clear();
            _active = [];
        }
        Penalties.Backend = new Backend();
        if (!_storeName.Equals(JsonPenaltyStore.StoreName, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[Penalties] Using the JSON store until a plugin registers the '{_storeName}' store");
        SetStore(_jsonStore);
    }

    // --- Loading ---

    public static bool Reload()
    {
        var store = _store;
        if (store == null)
            return false;

        Task<IReadOnlyList<Penalty>> task;
        try
        {
            task = store.LoadActiveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Penalties] Failed to load penalties, keeping the previous ones: {ex.Message}");
            return false;
        }

        int generation;
        lock (_lock)
            generation = ++_generation;

        if (task.IsCompleted)
            return Apply(task, generation);
        task.ContinueWith(t => TimerEngine.EnqueueNextTick(() => Apply(t, generation)), TaskScheduler.Default);
        return true;
    }

    private static bool Apply(Task<IReadOnlyList<Penalty>> task, int generation)
    {
        if (!task.IsCompletedSuccessfully)
        {
            Console.WriteLine($"[Penalties] Failed to load penalties, keeping the previous ones: {task.Exception?.GetBaseException().Message}");
            return false;
        }

        var now = Now();
        lock (_lock)
        {
            if (generation != _generation)
                return false;
            _active = task.Result.Where(p => p.IsActiveAt(now)).ToList();
        }
        Console.WriteLine($"[Penalties] Loaded {_active.Count} active penalties from the '{_storeName}' store");

        // Anyone connected who is now banned has to go.
        foreach (var player in Players.GetAll())
            EnforceBan(player.Slot);
        return true;
    }

    // --- Changes ---

    public static Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason,
        CCitadelPlayerController? admin, string? playerName)
    {
        if (steamId64 == 0)
            throw new ArgumentException("A penalty needs a SteamID.", nameof(steamId64));

        var now = Now();
        var (adminId, adminName) = Identify(admin);
        playerName ??= FindOnline(steamId64)?.PlayerName;

        var penalty = new Penalty
        {
            Type = type,
            SteamId64 = steamId64,
            PlayerName = playerName,
            CreatedUtc = now,
            ExpiresUtc = duration is { } d ? now + d : null,
            Reason = reason.Trim(),
            AdminSteamId64 = adminId,
            AdminName = adminName
        };

        Penalty? replaced = null;
        lock (_lock)
        {
            var index = _active.FindIndex(p => p.Type == type && p.SteamId64 == steamId64);
            if (index >= 0)
            {
                replaced = _active[index] with { RemovedUtc = now, RemovedBySteamId64 = adminId };
                _active.RemoveAt(index);
            }
            _active.Add(penalty);
        }

        if (replaced != null)
            Persist(s => s.UpdateAsync(replaced, CancellationToken.None));
        Persist(s => s.AddAsync(penalty, CancellationToken.None));

        if (replaced != null)
            Penalties.RaiseRemoved(replaced);
        Penalties.RaiseAdded(penalty);

        if (type == PenaltyType.Ban && FindOnline(steamId64) is { } online)
            Server.Kick(online.Slot, BanMessage(penalty));
        return penalty;
    }

    public static bool Remove(PenaltyType type, ulong steamId64, CCitadelPlayerController? admin)
    {
        var (adminId, _) = Identify(admin);
        Penalty? removed = null;
        lock (_lock)
        {
            var index = _active.FindIndex(p => p.Type == type && p.SteamId64 == steamId64);
            if (index >= 0)
            {
                removed = _active[index] with { RemovedUtc = Now(), RemovedBySteamId64 = adminId };
                _active.RemoveAt(index);
            }
        }

        if (removed == null)
            return false;
        Persist(s => s.UpdateAsync(removed, CancellationToken.None));
        Penalties.RaiseRemoved(removed);
        return true;
    }

    private static void Persist(Func<IPenaltyStore, Task> write)
    {
        var store = _store;
        if (store == null)
            return;
        try
        {
            write(store).ContinueWith(t => Console.WriteLine($"[Penalties] Failed to save: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Penalties] Failed to save: {ex.Message}");
        }
    }

    // --- Queries ---

    public static Penalty? GetActive(PenaltyType type, ulong steamId64)
    {
        Sweep();
        lock (_lock)
            return _active.Find(p => p.Type == type && p.SteamId64 == steamId64);
    }

    public static IReadOnlyList<Penalty> GetAllActive(PenaltyType? type)
    {
        Sweep();
        lock (_lock)
            return _active.Where(p => type == null || p.Type == type).OrderBy(p => p.CreatedUtc).ToList();
    }

    /// <summary>Drops penalties that have run out, raising <see cref="Penalties.Removed"/> for each.</summary>
    public static void Sweep()
    {
        var now = Now();
        List<Penalty> expired;
        lock (_lock)
        {
            expired = _active.Where(p => !p.IsActiveAt(now)).ToList();
            if (expired.Count == 0)
                return;
            _active.RemoveAll(p => !p.IsActiveAt(now));
        }
        foreach (var penalty in expired)
            Penalties.RaiseRemoved(penalty);
    }

    // --- Enforcement ---

    /// <summary>The message to reject a connecting SteamID with, or null to let them in.</summary>
    public static string? ConnectRejection(ulong steamId64)
        => steamId64 != 0 && GetActive(PenaltyType.Ban, steamId64) is { } ban ? BanMessage(ban) : null;

    /// <summary>Kicks the player in <paramref name="slot"/> if their SteamID is banned. Called once Steam validates them.</summary>
    public static void EnforceBan(int slot)
    {
        var id = PermissionManager.GetSlotSteamId(slot);
        if (ConnectRejection(id) is { } message)
        {
            Console.WriteLine($"[Penalties] Kicking banned player in slot {slot} ({id})");
            Server.Kick(slot, message);
        }
    }

    /// <summary>The player's active gag, looked up by the SteamID they connected with.</summary>
    public static Penalty? GagForSlot(int slot)
    {
        var id = PermissionManager.GetSlotSteamId(slot);
        return id == 0 ? null : GetActive(PenaltyType.Gag, id);
    }

    public static string BanMessage(Penalty ban)
        => $"You are banned from this server {ban.DescribeRemaining(Now())}.{(ban.Reason.Length > 0 ? $" Reason: {ban.Reason}" : "")}";

    public static string GagMessage(Penalty gag)
        => $"You are gagged {gag.DescribeRemaining(Now())} and can't use chat.{(gag.Reason.Length > 0 ? $" Reason: {gag.Reason}" : "")}";

    // --- Helpers ---

    private static (ulong Id, string Name) Identify(CCitadelPlayerController? admin)
        => admin == null ? (0UL, "Console") : (PermissionManager.GetSlotSteamId(admin.Slot), admin.PlayerName);

    private static unsafe CCitadelPlayerController? FindOnline(ulong steamId64)
    {
        if (NativeInterop.GetPlayerController == null)
            return null;
        for (int slot = 0; slot < Players.MaxSlot; slot++)
            if (PermissionManager.GetSlotSteamId(slot) == steamId64)
                return Players.FromSlot(slot);
        return null;
    }

    // --- Stores ---

    public static void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store)
    {
        lock (_lock)
        {
            _registered.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _registered.Add(new StoreRegistration(owner, name, store));
        }
        Console.WriteLine($"[Penalties] {owner.Name} registered the '{name}' store");
        if (name.Equals(_storeName, StringComparison.OrdinalIgnoreCase))
            SetStore(store);
    }

    public static void UnregisterStoresOwnedBy(IReadOnlyCollection<IDeadworksPlugin> plugins)
    {
        bool activeRemoved;
        lock (_lock)
        {
            var removed = _registered.Where(r => plugins.Contains(r.Owner)).ToList();
            if (removed.Count == 0)
                return;
            _registered.RemoveAll(removed.Contains);
            activeRemoved = removed.Any(r => ReferenceEquals(r.Store, _store));
        }
        if (activeRemoved)
        {
            Console.WriteLine($"[Penalties] The '{_storeName}' store was unloaded; falling back to the JSON store");
            SetStore(_jsonStore!);
        }
    }

    private static void SetStore(IPenaltyStore store)
    {
        if (_store != null)
            _store.Changed -= OnStoreChanged;
        _store = store;
        store.Changed += OnStoreChanged;
        Reload();
    }

    private static void OnStoreChanged() => TimerEngine.EnqueueNextTick(() => Reload());

    private sealed class Backend : IPenaltyBackend
    {
        public Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, CCitadelPlayerController? admin, string? playerName)
            => PenaltyManager.Add(type, steamId64, duration, reason, admin, playerName);
        public bool Remove(PenaltyType type, ulong steamId64, CCitadelPlayerController? admin) => PenaltyManager.Remove(type, steamId64, admin);
        public Penalty? GetActive(PenaltyType type, ulong steamId64) => PenaltyManager.GetActive(type, steamId64);
        public IReadOnlyList<Penalty> GetAllActive(PenaltyType? type) => PenaltyManager.GetAllActive(type);
        public Task<IReadOnlyList<Penalty>> GetHistoryAsync(ulong steamId64)
            => _store?.LoadHistoryAsync(steamId64, CancellationToken.None) ?? Task.FromResult<IReadOnlyList<Penalty>>([]);
        public void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store) => PenaltyManager.RegisterStore(owner, name, store);
    }
}
