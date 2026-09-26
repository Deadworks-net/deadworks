namespace DeadworksManaged.Api;

/// <summary>
/// Lists a permission that is only checked in code (with <see cref="PermissionExtensions.HasPermission"/>) in the
/// plugin's generated permissions file, so server owners can find it. Permissions on <see cref="CommandAttribute"/> are listed automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class DeclarePermissionAttribute : Attribute
{
    /// <summary>The permission, e.g. <c>moderation.player.ban.permanent</c>.</summary>
    public string Permission { get; }
    /// <summary>What holding it allows, shown to server owners.</summary>
    public string Description { get; set; } = "";

    /// <summary>Lists <paramref name="permission"/> in the plugin's generated permissions file.</summary>
    public DeclarePermissionAttribute(string permission) => Permission = permission;
}
