namespace DeadworksManaged.Api;

/// <summary>
/// Where roles and player entries come from. The built-in store reads <c>configs/permissions/*.jsonc</c>;
/// register another with <see cref="Permissions.RegisterStore"/> to keep them in a database or web panel.
/// Wildcards, immunity and every other rule are evaluated by Deadworks, so they behave the same on every store.
/// </summary>
public interface IPermissionStore
{
    /// <summary>Every role by name. Called at startup and on <c>dw_perm_reload</c>.</summary>
    Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct);

    /// <summary>Called when a player connects, and on demand for offline checks. Null means no entry.</summary>
    Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct);

    /// <summary>Persists a change made with the <c>dw_role_*</c>/<c>dw_perm_*</c> commands. A null entry deletes the player.</summary>
    Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct);

    /// <summary>Raise when data changed outside Deadworks. Pass the affected SteamID64, or null to reload everything.</summary>
    event Action<ulong?>? Changed;
}
