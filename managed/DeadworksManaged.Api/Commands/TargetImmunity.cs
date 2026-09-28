namespace DeadworksManaged.Api;

/// <summary>Whether a command's <see cref="Target"/> parameters skip players the caller can't target.</summary>
public enum TargetImmunity
{
    /// <summary>
    /// Enforce when the command declares a permission; ignore when it declares none. Decided by the declaration, so a
    /// server making the command public in <c>overrides.jsonc</c> doesn't switch immunity off.
    /// </summary>
    Auto,
    /// <summary>Always leave out players with higher immunity than the caller.</summary>
    Enforce,
    /// <summary>Never consider immunity, e.g. for a stats or spectate command.</summary>
    Ignore
}
