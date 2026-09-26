namespace DeadworksManaged.Api;

/// <summary>Whether a command's <see cref="Target"/> parameters skip players the caller can't target.</summary>
public enum TargetImmunity
{
    /// <summary>Enforce when the command requires a permission; ignore when anyone can run it.</summary>
    Auto,
    /// <summary>Always leave out players with higher immunity than the caller.</summary>
    Enforce,
    /// <summary>Never consider immunity, e.g. for a stats or spectate command.</summary>
    Ignore
}
