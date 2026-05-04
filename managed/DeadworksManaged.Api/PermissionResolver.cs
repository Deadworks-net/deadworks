namespace DeadworksManaged.Api;

internal static class PermissionResolver
{
    internal static Func<ulong, string[]>? GetRoles;
    internal static Func<ulong, string, bool>? HasPermission;
}
