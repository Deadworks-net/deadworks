namespace DeadworksManaged.Api;

/// <summary>Permission checks on a player controller.</summary>
public static class PermissionExtensions
{
    /// <summary>Whether this player holds <paramref name="permission"/>. A null player holds nothing; see <see cref="Caller"/>.</summary>
    public static bool HasPermission(this CCitadelPlayerController player, string permission)
        => Permissions.Has(player, permission);

    /// <summary>Whether this player may act on <paramref name="target"/> given both players' immunity. A null player can't target anyone.</summary>
    public static bool CanTarget(this CCitadelPlayerController caller, CCitadelPlayerController target)
        => Permissions.CanTarget(caller, target);
}
