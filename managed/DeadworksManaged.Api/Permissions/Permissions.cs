namespace DeadworksManaged.Api;

/// <summary>
/// Checks against the server's roles and players. Most plugins only need <see cref="CommandAttribute.Permission"/>
/// and <see cref="PermissionExtensions.HasPermission"/>; this class covers offline players and custom stores.
/// A null caller is the server console (or rcon), which is always allowed.
/// </summary>
public static class Permissions
{
    internal static IPermissionBackend? Backend;

    private static IPermissionBackend B => Backend ?? throw new InvalidOperationException("Permission system not initialized.");

    /// <summary>Whether the player holds <paramref name="permission"/>. An empty permission is always held.</summary>
    public static bool Has(ulong steamId64, string permission) => B.Has(steamId64, permission);

    /// <summary>Whether the player in this controller holds <paramref name="permission"/>. Null is the server console.</summary>
    public static bool Has(CCitadelPlayerController? player, string permission)
        => player == null || B.HasForSlot(player.Slot, permission);

    /// <summary>Which grant decided <see cref="Has(ulong, string)"/>. This is what <c>dw_perm_check</c> prints.</summary>
    public static PermissionExplanation Explain(ulong steamId64, string permission) => B.Explain(steamId64, permission);

    /// <summary>Whether the caller may act on the target: the target's immunity is not above the caller's.</summary>
    public static bool CanTarget(ulong callerSteamId64, ulong targetSteamId64) => B.CanTarget(callerSteamId64, targetSteamId64);

    /// <summary>Whether the caller may act on the target. A null caller is the server console and may target anyone.</summary>
    public static bool CanTarget(CCitadelPlayerController? caller, CCitadelPlayerController target)
        => caller == null || B.CanTargetSlots(caller.Slot, target.Slot);

    /// <summary>The player's immunity: the highest of their roles', or their own if set.</summary>
    public static int GetImmunity(ulong steamId64) => B.GetImmunity(steamId64);

    /// <summary>Roles assigned to the player, not counting <c>default</c> or inherited roles.</summary>
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

    /// <summary>Raised after a reload or any grant or revoke, with the affected SteamID64, or null for everyone.</summary>
    public static event Action<ulong?>? Changed;

    internal static void RaiseChanged(ulong? steamId64)
    {
        var handlers = Changed;
        if (handlers == null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ulong?>)handler)(steamId64);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Permissions] Changed handler threw: {ex.Message}");
            }
        }
    }
}

internal interface IPermissionBackend
{
    bool Has(ulong steamId64, string permission);
    bool HasForSlot(int slot, string permission);
    PermissionExplanation Explain(ulong steamId64, string permission);
    bool CanTarget(ulong callerSteamId64, ulong targetSteamId64);
    bool CanTargetSlots(int callerSlot, int targetSlot);
    int GetImmunity(ulong steamId64);
    IReadOnlyList<string> GetRoles(ulong steamId64);
    ulong GetSlotSteamId(int slot);
    void RegisterStore(IDeadworksPlugin owner, string name, IPermissionStore store);
}
