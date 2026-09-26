namespace DeadworksManaged.Api;

/// <summary>Permission checks on a player controller.</summary>
public static class PermissionExtensions
{
    /// <summary>Whether this player holds <paramref name="permission"/>. A null player is the server console, which holds everything.</summary>
    public static bool HasPermission(this CCitadelPlayerController? player, string permission)
        => Permissions.Has(player, permission);

    /// <summary>Whether this player may act on <paramref name="target"/> given both players' immunity. A null caller is the server console.</summary>
    public static bool CanTarget(this CCitadelPlayerController? caller, CCitadelPlayerController target)
        => Permissions.CanTarget(caller, target);
}
