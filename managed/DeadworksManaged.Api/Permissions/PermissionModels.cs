namespace DeadworksManaged.Api;

/// <summary>A role from <c>roles.jsonc</c> (or a custom <see cref="IPermissionStore"/>).</summary>
public sealed class RoleDefinition
{
    /// <summary>Grants such as <c>moderation.player.kick</c>, <c>moderation.*</c>, <c>*</c>, or <c>-moderation.player.ban</c> to deny.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Roles whose permissions this role also gets. Immunity is not inherited.</summary>
    public List<string> Inherits { get; set; } = [];

    /// <summary>Immunity for players who hold this role directly. Null means 0.</summary>
    public int? Immunity { get; set; }
}

/// <summary>A player's entry in <c>players.jsonc</c> (or a custom <see cref="IPermissionStore"/>).</summary>
public sealed class PlayerEntry
{
    /// <summary>A note for humans. Never used for matching.</summary>
    public string? Name { get; set; }

    /// <summary>Names of roles in <c>roles.jsonc</c>. Everyone also has <c>default</c>.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Grants on this player alone. They beat role grants of equal specificity.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Replaces the immunity the player's roles would give them.</summary>
    public int? Immunity { get; set; }

    /// <summary>A copy whose lists can be changed without touching this entry.</summary>
    public PlayerEntry Clone() => new()
    {
        Name = Name,
        Roles = [.. Roles],
        Permissions = [.. Permissions],
        Immunity = Immunity
    };
}

/// <summary>Why <see cref="Permissions.Has(ulong, string)"/> answered the way it did.</summary>
/// <param name="Allowed">The answer.</param>
/// <param name="Grant">The grant that decided it, as written (e.g. <c>-moderation.player.ban</c>), or null when nothing matched.</param>
/// <param name="Source"><c>player</c>, <c>role:&lt;name&gt;</c>, <c>console</c>, or <c>unauthenticated</c>; null when nothing matched.</param>
public sealed record PermissionExplanation(bool Allowed, string? Grant, string? Source)
{
    /// <summary>e.g. <c>denied by "-moderation.player.ban" from role:moderator</c>.</summary>
    public override string ToString() => Grant == null
        ? (Source == null ? $"{(Allowed ? "allowed" : "denied")}: no grant matches" : $"{(Allowed ? "allowed" : "denied")}: {Source}")
        : $"{(Allowed ? "allowed" : "denied")} by \"{Grant}\" from {Source}";
}
