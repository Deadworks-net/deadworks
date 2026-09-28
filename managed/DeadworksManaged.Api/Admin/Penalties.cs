using System.Text.Json.Serialization;

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

/// <summary>How a penalty stopped applying.</summary>
public enum PenaltyEnd
{
    /// <summary>Lifted early by an admin: unban, ungag, unmute.</summary>
    Lifted,
    /// <summary>Replaced by a newer penalty of the same type, e.g. a ban extended or shortened. The player is still penalized.</summary>
    Replaced,
    /// <summary>Ran out.</summary>
    Expired
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
    /// <summary>Who lifted or replaced it, or 0 for the server console.</summary>
    public ulong RemovedBySteamId64 { get; init; }
    /// <summary>The name of who lifted or replaced it, for display only; null if it hasn't been.</summary>
    public string? RemovedByName { get; init; }
    /// <summary>Why it was lifted, if the admin said.</summary>
    public string? RemovalReason { get; init; }
    /// <summary>The penalty that replaced this one, if it was replaced rather than lifted.</summary>
    public Guid? ReplacedBy { get; init; }

    /// <summary>True for a penalty with no end date.</summary>
    [JsonIgnore]
    public bool IsPermanent => ExpiresUtc == null;

    /// <summary>How it stopped applying by <paramref name="nowUtc"/>, or null if it still applies.</summary>
    public PenaltyEnd? HowEnded(DateTime nowUtc)
    {
        // Running out comes first: a penalty lifted or replaced after it had already expired simply expired.
        if (ExpiresUtc is { } expires && expires <= (RemovedUtc ?? nowUtc))
            return PenaltyEnd.Expired;
        if (ReplacedBy != null)
            return PenaltyEnd.Replaced;
        return RemovedUtc != null ? PenaltyEnd.Lifted : null;
    }

    /// <summary>Whether it applies at <paramref name="nowUtc"/>: not lifted and not expired.</summary>
    public bool IsActiveAt(DateTime nowUtc) => RemovedUtc == null && (ExpiresUtc == null || ExpiresUtc > nowUtc);

    /// <summary>
    /// "permanently", or how long is left, rounded up to the minute and worded like an admin announcement:
    /// "for 45 minutes", "for 1 hour", "for 1h 5m", "for 3 days", "for 2d 4h".
    /// </summary>
    public string DescribeRemaining(DateTime nowUtc)
    {
        if (ExpiresUtc is not { } expires)
            return "permanently";
        var minutes = (long)Math.Ceiling((expires - nowUtc).TotalMinutes);
        if (minutes < 1) return "for less than a minute";
        if (minutes < 60) return $"for {minutes} minute{(minutes == 1 ? "" : "s")}";
        if (minutes < 1440)
            return minutes % 60 == 0 ? $"for {minutes / 60} hour{(minutes == 60 ? "" : "s")}" : $"for {minutes / 60}h {minutes % 60}m";
        var hours = minutes / 60;
        return hours % 24 == 0 ? $"for {hours / 24} day{(hours == 24 ? "" : "s")}" : $"for {hours / 24}d {hours % 24}h";
    }
}

/// <summary>
/// Bans, gags and mutes. Deadworks stores and enforces all three: banned players can't join, gagged players' chat
/// never reaches chat or any plugin, and muted players' voice is dropped before anyone hears it or any plugin sees it.
/// Plugins only decide when to add or
/// remove them, and hear about changes through <see cref="IDeadworksPlugin.OnPenaltyAdded"/> and
/// <see cref="IDeadworksPlugin.OnPenaltyRemoved"/>. Immunity is not checked here; check <see cref="Caller.CanTarget"/>
/// or <see cref="Permissions.CanTarget(ulong, ulong)"/> before penalizing someone on another player's behalf.
/// </summary>
public static class Penalties
{
    internal static IPenaltyBackend? Backend;

    private static IPenaltyBackend B => Backend ?? throw new InvalidOperationException("Penalty system not initialized.");

    /// <summary>
    /// Adds a penalty on behalf of <paramref name="by"/>. A null <paramref name="duration"/> is permanent. An active
    /// penalty of the same type on the same player is replaced, even by a shorter one: check <see cref="WouldShorten"/>
    /// first if shortening should need more than adding. Adding a ban kicks the player if they're connected.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is zero or negative; use null for permanent.</exception>
    /// <exception cref="CommandException">
    /// The SteamID belongs to a player on the server whom Steam hasn't confirmed yet, so it can't be trusted to penalize
    /// (the server console may anyway); or the penalty store is unavailable or its file has an error.
    /// </exception>
    /// <remarks>
    /// The penalty applies at once and is saved in the background. If that save fails, it still applies until the server
    /// restarts, and the console prints an ERROR; this method has already returned by then.
    /// </remarks>
    public static Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, Caller by, string? playerName = null)
        => B.Add(type, steamId64, duration, reason, by, playerName);

    /// <summary>
    /// Lifts the player's active penalty of this type on behalf of <paramref name="by"/>, with an optional reason kept in
    /// its history. Returns false if they had none.
    /// </summary>
    public static bool Remove(PenaltyType type, ulong steamId64, Caller by, string reason = "") => B.Remove(type, steamId64, by, reason);

    /// <summary>The player's active penalty of this type, or null.</summary>
    public static Penalty? GetActive(PenaltyType type, ulong steamId64) => B.GetActive(type, steamId64);

    /// <summary>
    /// The active penalty that adding one of <paramref name="duration"/> (null = permanent) would cut short, or null if
    /// there is none or the new one lasts at least as long. Replacing a penalty with a shorter one partly lifts it, so a
    /// command may want the lifting permission for that, as the Admin plugin's ban does.
    /// </summary>
    public static Penalty? WouldShorten(PenaltyType type, ulong steamId64, TimeSpan? duration) => B.WouldShorten(type, steamId64, duration);

    /// <summary>Every active penalty, optionally of one type.</summary>
    public static IReadOnlyList<Penalty> GetActive(PenaltyType? type = null) => B.GetAllActive(type);

    /// <summary>
    /// Every penalty the store still has for this player, newest first, including removed and expired ones. Faults
    /// while the store is unavailable, rather than answering with an empty history.
    /// </summary>
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
    Penalty Add(PenaltyType type, ulong steamId64, TimeSpan? duration, string reason, Caller by, string? playerName);
    bool Remove(PenaltyType type, ulong steamId64, Caller by, string reason);
    Penalty? GetActive(PenaltyType type, ulong steamId64);
    Penalty? WouldShorten(PenaltyType type, ulong steamId64, TimeSpan? duration);
    IReadOnlyList<Penalty> GetAllActive(PenaltyType? type);
    Task<IReadOnlyList<Penalty>> GetHistoryAsync(ulong steamId64);
    void RegisterStore(IDeadworksPlugin owner, string name, IPenaltyStore store);
}
