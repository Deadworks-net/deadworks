namespace DeadworksManaged.Api;

/// <summary>
/// Checks against the server's roles and players. Most plugins only need <see cref="CommandAttribute.Permission"/>
/// and <see cref="Caller.HasPermission"/>; this class covers offline players and custom stores.
/// A null controller has no permissions: use <see cref="Caller"/> for code that runs for the console or a player.
/// </summary>
public static class Permissions
{
    internal static IPermissionBackend? Backend;

    private static IPermissionBackend B => Backend ?? throw new InvalidOperationException("Permission system not initialized.");

    /// <summary>Whether the player holds <paramref name="permission"/>. An empty permission is always held.</summary>
    public static bool Has(ulong steamId64, string permission) => B.Has(steamId64, permission);

    /// <summary>
    /// Whether the player in this controller holds <paramref name="permission"/>. A null controller holds nothing, so a
    /// failed lookup can't act as the console; use <see cref="Caller.HasPermission"/> when the console is possible.
    /// </summary>
    public static bool Has(CCitadelPlayerController player, string permission)
        => player != null && B.HasForSlot(player.Slot, permission);

    /// <summary>Which grant decided <see cref="Has(ulong, string)"/>. This is what <c>dw_perm_check</c> prints.</summary>
    public static PermissionExplanation Explain(ulong steamId64, string permission) => B.Explain(steamId64, permission);

    /// <summary>
    /// Whether the caller may act on the target: the target's immunity is not above the caller's. False while the
    /// target's entry is still loading from the store, since their immunity isn't known yet; see <see cref="IsLoaded"/>.
    /// </summary>
    public static bool CanTarget(ulong callerSteamId64, ulong targetSteamId64) => B.CanTarget(callerSteamId64, targetSteamId64);

    /// <summary>
    /// Whether the store's answer for this SteamID has arrived: its entry, or that it has none. Checks by SteamID start
    /// loading it; until it arrives they answer as <c>default</c>, and <see cref="CanTarget(ulong, ulong)"/> refuses.
    /// Always true for the JSON store.
    /// </summary>
    public static bool IsLoaded(ulong steamId64) => B.IsLoaded(steamId64);

    /// <summary>
    /// Whether the caller may act on the target given both players' immunity. A null caller or target is refused; use
    /// <see cref="Caller.CanTarget"/> when the console is possible.
    /// </summary>
    public static bool CanTarget(CCitadelPlayerController caller, CCitadelPlayerController target)
        => caller != null && target != null && B.CanTargetSlots(caller.Slot, target.Slot);

    /// <summary>
    /// The player's immunity: the highest of their roles', or their own if set. Taken from their saved entry even before
    /// Steam confirms them, since immunity protects them rather than letting them do anything.
    /// </summary>
    public static int GetImmunity(ulong steamId64) => B.GetImmunity(steamId64);

    /// <summary>
    /// Roles assigned to the player, not counting <c>default</c> or inherited roles. Like <see cref="Has(ulong, string)"/>,
    /// none while the player is on the server but not yet confirmed by Steam. For access checks prefer a permission.
    /// </summary>
    public static IReadOnlyList<string> GetRoles(ulong steamId64) => B.GetRoles(steamId64);

    /// <summary>
    /// The SteamID64 the player in <paramref name="slot"/> connected with, or 0 for an empty slot or a bot.
    /// Unlike <see cref="CBasePlayerController.PlayerSteamId"/>, plugins can't change it.
    /// </summary>
    public static ulong GetSteamId(int slot) => B.GetSlotSteamId(slot);

    /// <summary>
    /// Offers a store for roles and players. It becomes active when <c>permissions.store</c> in
    /// <c>configs/deadworks.jsonc</c> names it, and is dropped automatically when <paramref name="owner"/> unloads.
    /// </summary>
    public static void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store) => B.RegisterStore(owner, name, store);
}

internal interface IPermissionBackend
{
    bool Has(ulong steamId64, string permission);
    bool HasForSlot(int slot, string permission);
    PermissionExplanation Explain(ulong steamId64, string permission);
    bool CanTarget(ulong callerSteamId64, ulong targetSteamId64);
    bool CanTargetSlots(int callerSlot, int targetSlot);
    int GetImmunity(ulong steamId64);
    bool IsLoaded(ulong steamId64);
    IReadOnlyList<string> GetRoles(ulong steamId64);
    ulong GetSlotSteamId(int slot);
    void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store);
}
