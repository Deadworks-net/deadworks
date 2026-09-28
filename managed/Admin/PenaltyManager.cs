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

    /// <summary>Stands in for a configured store no plugin has registered. It never loads, so nobody new gets in.</summary>
    private sealed class UnavailableStore(string name) : IPenaltyStore
    {
        private Exception Missing => new InvalidOperationException(name == DeadworksConfig.BrokenStoreName
            ? "deadworks.jsonc has an error, so which store to use is unknown"
            : $"no plugin has registered the '{name}' store");
        public Task<IReadOnlyList<Penalty>> LoadActiveAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<Penalty>>(Missing);
        public Task AddAsync(Penalty penalty, CancellationToken ct) => Task.FromException(Missing);
        public Task UpdateAsync(Penalty penalty, CancellationToken ct) => Task.FromException(Missing);
        public Task<IReadOnlyList<Penalty>> LoadHistoryAsync(ulong steamId64, CancellationToken ct) => Task.FromResult<IReadOnlyList<Penalty>>([]);
        public event Action? Changed { add { } remove { } }
    }

    public const string UnavailableRejection = "This server can't check its ban list right now. Try again in a few minutes.";

    private static readonly Lock _lock = new();
    private static List<Penalty> _active = [];
    // Active mutes by SteamID, rebuilt whenever _active changes: voice is checked many times a second per speaker, so
    // that check is one lookup, with no lock, sweep or allocation.
    private static volatile Dictionary<ulong, Penalty> _mutes = [];
    private static JsonPenaltyStore? _jsonStore;
    private static IPenaltyStore? _store;
    private static string _storeName = JsonPenaltyStore.StoreName;
    private static readonly List<StoreRegistration> _registered = [];
    private static int _generation;
    // Whether the active store has loaded. Until it has, Deadworks can't know who is banned, so nobody new gets in.
    private static bool _ready;

    /// <summary>Raised after a penalty is added; the plugin loader forwards it to <see cref="IDeadworksPlugin.OnPenaltyAdded"/>.</summary>
    internal static event Action<Penalty>? Added;
    /// <summary>Raised after a penalty ends; forwarded to <see cref="IDeadworksPlugin.OnPenaltyRemoved"/>.</summary>
    internal static event Action<Penalty>? Removed;

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
            RefreshMutes();
        }
        Penalties.Backend = new Backend();
        if (_storeName.Equals(JsonPenaltyStore.StoreName, StringComparison.OrdinalIgnoreCase))
        {
            SetStore(_jsonStore);
        }
        else
        {
            Console.WriteLine(_storeName == DeadworksConfig.BrokenStoreName
                ? "[Penalties] deadworks.jsonc has an error, so the ban list can't be checked. New players can't join until it's fixed and the server restarted."
                : $"[Penalties] Waiting for a plugin to register the '{_storeName}' store. New players can't join until it does.");
            SetStore(new UnavailableStore(_storeName));
        }
    }

    // --- Loading ---

    /// <summary>Why the last load of penalties failed, or null if it worked; for replies to staff who can't see the console.</summary>
    public static string? LastLoadError { get; private set; }

    private static void ReportLoadFailure(string? reason)
    {
        LastLoadError = reason ?? "unknown error";
        Console.WriteLine(_ready
            ? $"[Penalties] Failed to load penalties, keeping the previous ones: {LastLoadError}"
            : $"[Penalties] ERROR: failed to load penalties: {LastLoadError.TrimEnd('.')}. New players can't join until they load; run dw_penalties_reload once it's fixed.");
    }

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
            ReportLoadFailure(ex.Message);
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
            ReportLoadFailure(task.Exception?.GetBaseException().Message);
            return false;
        }

        var now = Now();
        lock (_lock)
        {
            if (generation != _generation)
                return false;
            _active = task.Result.Where(p => p.IsActiveAt(now)).ToList();
            RefreshMutes();
            _ready = true;
            LastLoadError = null;
        }
        Console.WriteLine($"[Penalties] Loaded {_active.Count} active penalties from the '{_storeName}' store");

        // Anyone on the server, or still connecting, who is now banned has to go.
        for (int slot = 0; slot < Players.MaxSlot; slot++)
            EnforceBan(slot);
        return true;
    }

    // --- Changes ---

    /// <summary>Why penalties can't be changed right now, or null. A penalty that can't be saved would vanish on restart.</summary>
    private static string? CantSave()
    {
        if (!_ready)
            return $"Penalties can't be changed right now: the '{_storeName}' store isn't available.";
        // Checked against the file as it is now: it may have been broken by hand since it was loaded.
        if (ReferenceEquals(_store, _jsonStore) && _jsonStore!.CheckReadable() is { } error)
            return $"Penalties can't be changed right now: penalties.jsonc has an error. Fix it and run dw_penalties_reload. ({error})";
        return null;
    }

    public static Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, Caller by, string? playerName)
    {
        if (steamId64 == 0)
            throw new ArgumentException("A penalty needs a SteamID; bots don't have one.", nameof(steamId64));
        // Zero is a common way to say "permanent" elsewhere; here it would add a penalty that has already run out.
        if (duration is { } length && length <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A penalty needs a positive duration; use null for permanent.");
        if (CantSave() is { } cantSave)
            throw new CommandException(cantSave);

        // Until Steam confirms a connected player, the SteamID they claim isn't safe to record a penalty against.
        var slot = PermissionManager.FindSlot(steamId64);
        if (slot >= 0 && !PermissionManager.IsAuthorized(slot))
            throw new CommandException($"{playerName ?? FindOnline(steamId64)?.PlayerName ?? steamId64.ToString()} hasn't been verified by Steam yet. Try again in a moment.");

        var now = Now();
        var (adminId, adminName) = (by.SteamId64, by.Name);
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
                replaced = _active[index] with { RemovedUtc = now, RemovedBySteamId64 = adminId, RemovedByName = adminName, ReplacedBy = penalty.Id };
                _active.RemoveAt(index);
            }
            _active.Add(penalty);
            RefreshMutes();
        }

        // In order: a store that saw the new penalty first could briefly hold two active ones.
        Persist(async s =>
        {
            if (replaced != null)
                await s.UpdateAsync(replaced, CancellationToken.None);
            await s.AddAsync(penalty, CancellationToken.None);
        });

        if (replaced != null)
            Raise(Removed, replaced);
        Raise(Added, penalty);

        if (type == PenaltyType.Ban && FindOnline(steamId64) is { } online)
            Server.Kick(online.Slot, BanMessage(penalty));
        return penalty;
    }

    public static bool Remove(PenaltyType type, ulong steamId64, Caller by, string reason = "")
    {
        if (CantSave() is { } cantSave)
            throw new CommandException(cantSave);
        var adminId = by.SteamId64;
        Penalty? removed = null;
        lock (_lock)
        {
            var index = _active.FindIndex(p => p.Type == type && p.SteamId64 == steamId64);
            if (index >= 0)
            {
                removed = _active[index] with
                {
                    RemovedUtc = Now(), RemovedBySteamId64 = adminId, RemovedByName = by.Name,
                    RemovalReason = reason.Trim() is { Length: > 0 } why ? why : null
                };
                _active.RemoveAt(index);
                RefreshMutes();
            }
        }

        if (removed == null)
            return false;
        Persist(s => s.UpdateAsync(removed, CancellationToken.None));
        Raise(Removed, removed);
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

    public static Penalty? WouldShorten(PenaltyType type, ulong steamId64, TimeSpan? duration)
    {
        if (GetActive(type, steamId64) is not { } current)
            return null;
        if (current.ExpiresUtc is not { } currentEnd)
            return duration == null ? null : current; // only another permanent one lasts as long as a permanent one
        return duration is { } d && Now() + d < currentEnd ? current : null;
    }

    public static IReadOnlyList<Penalty> GetAllActive(PenaltyType? type)
    {
        Sweep();
        lock (_lock)
            return _active.Where(p => type == null || p.Type == type).OrderBy(p => p.CreatedUtc).ToList();
    }

    /// <summary>Drops penalties that have run out, raising <see cref="Removed"/> for each.</summary>
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
            RefreshMutes();
        }
        foreach (var penalty in expired)
            Raise(Removed, penalty);
    }

    // --- Enforcement ---

    /// <summary>
    /// The message to refuse a connecting SteamID with, or null to let them in. Someone reloading after a map change
    /// was already on the server, so a ban list that's down doesn't turn them away; a ban still does.
    /// </summary>
    public static string? ConnectRejection(ulong steamId64, bool isMapChangeReconnect = false)
    {
        if (steamId64 == 0)
            return null;
        if (!_ready)
            return isMapChangeReconnect ? null : UnavailableRejection;
        return GetActive(PenaltyType.Ban, steamId64) is { } ban ? BanMessage(ban) : null;
    }

    /// <summary>Kicks the player in <paramref name="slot"/> if their SteamID is banned. Called once Steam validates them.</summary>
    public static void EnforceBan(int slot)
    {
        // Only real bans: a store that's down keeps new players out at connect, but doesn't kick people already here.
        var id = PermissionManager.GetSlotSteamId(slot);
        if (id != 0 && GetActive(PenaltyType.Ban, id) is { } ban)
        {
            Console.WriteLine($"[Penalties] Kicking banned player in slot {slot} ({id})");
            Server.Kick(slot, BanMessage(ban));
        }
    }

    /// <summary>The player's active gag, looked up by the SteamID they connected with.</summary>
    public static Penalty? GagForSlot(int slot)
    {
        var id = PermissionManager.GetSlotSteamId(slot);
        return id == 0 ? null : GetActive(PenaltyType.Gag, id);
    }

    /// <summary>
    /// Whether an incoming message is voice from a muted player, to be dropped. Voice arrives as clc_VoiceData
    /// many times a second while someone talks, and a dropped packet is simply never relayed to anyone.
    /// </summary>
    public static bool DropsVoice(int senderSlot, int msgId)
    {
        if (msgId != (int)CLC_Messages.ClcVoiceData || senderSlot < 0)
            return false;
        var mutes = _mutes;
        if (mutes.Count == 0)
            return false;
        var id = PermissionManager.GetSlotSteamId(senderSlot);
        return id != 0 && mutes.TryGetValue(id, out var mute) && mute.IsActiveAt(Now());
    }

    /// <summary>Call with <see cref="_lock"/> held, after changing <see cref="_active"/>.</summary>
    private static void RefreshMutes()
        => _mutes = _active.Where(p => p.Type == PenaltyType.Mute).ToDictionary(p => p.SteamId64);

    public static string BanMessage(Penalty ban)
        => $"You are banned from this server {ban.DescribeRemaining(Now())}.{(ban.Reason.Length > 0 ? $" Reason: {ban.Reason}" : "")}";

    public static string GagMessage(Penalty gag)
        => $"You are gagged {gag.DescribeRemaining(Now())} and can't use chat.{(gag.Reason.Length > 0 ? $" Reason: {gag.Reason}" : "")}";

    // --- Helpers ---

    private static void Raise(Action<Penalty>? handlers, Penalty penalty)
    {
        try
        {
            handlers?.Invoke(penalty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Penalties] A penalty handler threw: {ex.Message}");
        }
    }

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
        {
            // Loading enforces bans (engine calls), and OnLoad can run off the game thread during hot reload.
            SetStore(store, reload: false);
            TimerEngine.EnqueueNextTick(() => Reload());
        }
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
            // Unloads can run off the game thread (hot reload): stop letting people in now, and reload on the next tick.
            Console.WriteLine($"[Penalties] The '{_storeName}' store was unloaded. New players can't join until it's back.");
            SetStore(new UnavailableStore(_storeName), reload: false);
            TimerEngine.EnqueueNextTick(() => Reload());
        }
    }

    private static void SetStore(IPenaltyStore store, bool reload = true)
    {
        if (_store != null)
            _store.Changed -= OnStoreChanged;
        lock (_lock)
        {
            _store = store;
            _ready = false;
            ++_generation;
        }
        store.Changed += OnStoreChanged;
        if (reload)
            Reload();
    }

    private static void OnStoreChanged() => TimerEngine.EnqueueNextTick(() => Reload());

    private sealed class Backend : IPenaltyBackend
    {
        public Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, Caller by, string? playerName)
            => PenaltyManager.Add(type, steamId64, duration, reason, by, playerName);
        public bool Remove(PenaltyType type, ulong steamId64, Caller by, string reason) => PenaltyManager.Remove(type, steamId64, by, reason);
        public Penalty? GetActive(PenaltyType type, ulong steamId64) => PenaltyManager.GetActive(type, steamId64);
        public Penalty? WouldShorten(PenaltyType type, ulong steamId64, TimeSpan? duration) => PenaltyManager.WouldShorten(type, steamId64, duration);
        public IReadOnlyList<Penalty> GetAllActive(PenaltyType? type) => PenaltyManager.GetAllActive(type);
        public Task<IReadOnlyList<Penalty>> GetHistoryAsync(ulong steamId64)
            => _store?.LoadHistoryAsync(steamId64, CancellationToken.None) ?? Task.FromResult<IReadOnlyList<Penalty>>([]);
        public void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store) => PenaltyManager.RegisterStore(owner, name, store);
    }
}
