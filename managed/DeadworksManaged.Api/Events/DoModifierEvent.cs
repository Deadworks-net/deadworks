namespace DeadworksManaged.Api;

/// <summary>
/// Event data for the DoModifierEvent hook, fired when a modifier event occurs.
/// </summary>
public sealed class DoModifierEvent
{
    /// <summary>The modifier event type identifier.</summary>
    public required EModifierEvent Event { get; init; }

    /// <summary>The entity that is creating the event.</summary>
    public required CBaseEntity Caster { get; init; }

    /// <summary>The optional target entity, or null if not provided.</summary>
    public CBaseEntity? OptCastTarget { get; init; }

    /// <summary>The optional cast entity, or null if not provided.</summary>
    public CBaseEntity? OptCastEntity { get; init; }

    /// <summary>Pointer to the native modifier event data.</summary>
    public required nint EventData { get; init; }
}