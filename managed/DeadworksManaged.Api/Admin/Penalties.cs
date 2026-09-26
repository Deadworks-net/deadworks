namespace DeadworksManaged.Api;

/// <summary>What a <see cref="Penalty"/> stops a player doing.</summary>
public enum PenaltyType
{
    /// <summary>Joining the server.</summary>
    Ban,
    /// <summary>Typing in chat.</summary>
    Gag,
    /// <summary>Talking on voice chat.</summary>
    Mute
}

/// <summary>A ban, gag or mute on one SteamID. Removed and expired penalties are kept as history.</summary>
public sealed record Penalty
{
    /// <summary>Identifies this penalty in its store.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>What it stops the player doing.</summary>
    public required PenaltyType Type { get; init; }
    /// <summary>The penalized player.</summary>
    public required ulong SteamId64 { get; init; }
    /// <summary>The player's name when the penalty was added, for display only.</summary>
    public string? PlayerName { get; init; }
    /// <summary>When it was added.</summary>
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>When it runs out, or null for permanent.</summary>
    public DateTime? ExpiresUtc { get; init; }
    /// <summary>Why it was added.</summary>
    public string Reason { get; init; } = "";
    /// <summary>Who added it, or 0 for the server console.</summary>
    public ulong AdminSteamId64 { get; init; }
    /// <summary>The admin's name at the time, for display only.</summary>
    public string? AdminName { get; init; }
    /// <summary>When it was lifted early (unban, ungag, ...), or replaced by a newer penalty of the same type.</summary>
    public DateTime? RemovedUtc { get; init; }
    /// <summary>Who lifted it, or 0 for the server console.</summary>
    public ulong RemovedBySteamId64 { get; init; }

    /// <summary>True for a penalty with no end date.</summary>
    public bool IsPermanent => ExpiresUtc == null;

    /// <summary>Whether it applies at <paramref name="nowUtc"/>: not lifted and not expired.</summary>
    public bool IsActiveAt(DateTime nowUtc) => RemovedUtc == null && (ExpiresUtc == null || ExpiresUtc > nowUtc);

    /// <summary>"permanently", or how long is left, e.g. "for 1h 5m".</summary>
    public string DescribeRemaining(DateTime nowUtc)
    {
        if (ExpiresUtc is not { } expires)
            return "permanently";
        var left = expires - nowUtc;
        if (left.TotalMinutes < 1) return "for less than a minute";
        if (left.TotalHours < 1) return $"for {(int)Math.Ceiling(left.TotalMinutes)}m";
        if (left.TotalDays < 1) return $"for {(int)left.TotalHours}h {left.Minutes}m";
        return $"for {(int)left.TotalDays}d {left.Hours}h";
    }
}

/// <summary>
/// Bans, gags and mutes. Deadworks stores and enforces them: banned players can't join, gagged players' chat never
/// reaches chat or any plugin, and muted players can't be heard. Plugins only decide when to add or remove them.
/// Immunity is not checked here; check <see cref="Permissions.CanTarget(CCitadelPlayerController?, CCitadelPlayerController)"/>
/// before penalizing someone on another player's behalf.
/// </summary>
public static class Penalties
{
    internal static IPenaltyBackend? Backend;

    private static IPenaltyBackend B => Backend ?? throw new InvalidOperationException("Penalty system not initialized.");

    /// <summary>
    /// Adds a penalty. A null <paramref name="duration"/> is permanent. An active penalty of the same type on the same
    /// player is replaced. Adding a ban kicks the player if they're connected.
    /// </summary>
    public static Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason,
        CCitadelPlayerController? admin, string? playerName = null)
        => B.Add(type, steamId64, duration, reason, admin, playerName);

    /// <summary>Lifts the player's active penalty of this type. Returns false if they had none.</summary>
    public static bool Remove(PenaltyType type, ulong steamId64, CCitadelPlayerController? admin) => B.Remove(type, steamId64, admin);

    /// <summary>The player's active penalty of this type, or null.</summary>
    public static Penalty? GetActive(PenaltyType type, ulong steamId64) => B.GetActive(type, steamId64);

    /// <summary>Every active penalty, optionally of one type.</summary>
    public static IReadOnlyList<Penalty> GetActive(PenaltyType? type = null) => B.GetAllActive(type);

    /// <summary>Every penalty the store still has for this player, newest first, including removed and expired ones.</summary>
    public static Task<IReadOnlyList<Penalty>> GetHistoryAsync(ulong steamId64) => B.GetHistoryAsync(steamId64);

    /// <summary>Whether the player has an active ban.</summary>
    public static bool IsBanned(ulong steamId64) => GetActive(PenaltyType.Ban, steamId64) != null;
    /// <summary>Whether the player has an active gag.</summary>
    public static bool IsGagged(ulong steamId64) => GetActive(PenaltyType.Gag, steamId64) != null;
    /// <summary>Whether the player has an active mute.</summary>
    public static bool IsMuted(ulong steamId64) => GetActive(PenaltyType.Mute, steamId64) != null;

    /// <summary>
    /// Offers a store for penalties. It becomes active when <c>penalties.store</c> in <c>configs/deadworks.jsonc</c> names
    /// it, and is dropped automatically when <paramref name="owner"/> unloads.
    /// </summary>
    public static void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store) => B.RegisterStore(owner, name, store);

    /// <summary>Raised after a penalty is added.</summary>
    public static event Action<Penalty>? Added;

    /// <summary>Raised after a penalty is lifted, replaced or runs out. The penalty passed is the ended one.</summary>
    public static event Action<Penalty>? Removed;

    internal static void RaiseAdded(Penalty penalty) => Raise(Added, penalty, nameof(Added));
    internal static void RaiseRemoved(Penalty penalty) => Raise(Removed, penalty, nameof(Removed));

    private static void Raise(Action<Penalty>? handlers, Penalty penalty, string name)
    {
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Penalty>)handler)(penalty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Penalties] {name} handler threw: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Where penalties are kept. The built-in store is <c>configs/penalties/penalties.jsonc</c>; register another with
/// <see cref="Penalties.RegisterStore"/> to share bans between servers or with a web panel.
/// </summary>
public interface IPenaltyStore
{
    /// <summary>Every penalty that may still be active. Called at startup and when the store is selected.</summary>
    Task<IReadOnlyList<Penalty>> LoadActiveAsync(CancellationToken ct);

    /// <summary>Saves a new penalty.</summary>
    Task AddAsync(Penalty penalty, CancellationToken ct);

    /// <summary>Saves a penalty that was lifted or replaced (its <see cref="Penalty.RemovedUtc"/> is now set).</summary>
    Task UpdateAsync(Penalty penalty, CancellationToken ct);

    /// <summary>Every penalty the store has for one player, newest first.</summary>
    Task<IReadOnlyList<Penalty>> LoadHistoryAsync(ulong steamId64, CancellationToken ct);

    /// <summary>Raise when penalties changed outside Deadworks, e.g. a ban added on another server. Deadworks reloads them.</summary>
    event Action? Changed;
}

internal interface IPenaltyBackend
{
    Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, CCitadelPlayerController? admin, string? playerName);
    bool Remove(PenaltyType type, ulong steamId64, CCitadelPlayerController? admin);
    Penalty? GetActive(PenaltyType type, ulong steamId64);
    IReadOnlyList<Penalty> GetAllActive(PenaltyType? type);
    Task<IReadOnlyList<Penalty>> GetHistoryAsync(ulong steamId64);
    void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store);
}
