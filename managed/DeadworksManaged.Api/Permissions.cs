namespace DeadworksManaged.Api;

/// <summary>Permission helpers exposed to plugins.</summary>
public static class Permissions
{
    /// <summary>Returns the roles assigned to the player, or an empty array when none are assigned.</summary>
    public static string[] GetRoles(ulong steamId64)
    {
        if (PermissionResolver.GetRoles == null)
            throw new InvalidOperationException("Permission system not initialized.");

        return PermissionResolver.GetRoles(steamId64);
    }

    /// <summary>Returns whether the player has the requested permission.</summary>
    public static bool HasPermission(ulong steamId64, string permission)
    {
        if (PermissionResolver.HasPermission == null)
            throw new InvalidOperationException("Permission system not initialized.");

        return PermissionResolver.HasPermission(steamId64, permission);
    }
}
