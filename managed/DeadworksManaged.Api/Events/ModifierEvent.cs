namespace DeadworksManaged.Api;

/// <summary>
/// Fired for every modifier event the game dispatches through <c>FireModifierEvent</c>, before the modifiers themselves process it.
/// Passed to <see cref="IDeadworksPlugin.OnModifierEvent"/>. Observe-only: the event cannot be cancelled or altered.
/// </summary>
public sealed class ModifierEvent {
	/// <summary>The modifier event being dispatched.</summary>
	public required EModifierEvent Event { get; init; }

	/// <summary>The entity that raised the event. Null for events raised without a caster.</summary>
	public CBaseEntity? Caster { get; init; }

	/// <summary>The target of the event, if any. Null when absent or when it is the same entity as <see cref="Caster"/>.</summary>
	public CBaseEntity? Target { get; init; }

	/// <summary>The entity that was cast/spawned by the event (projectile, bullet, etc.), if any. Meaning depends on <see cref="Event"/>.</summary>
	public CBaseEntity? CastEntity { get; init; }

	/// <summary>
	/// Pointer to the event-specific native payload. Its layout depends on <see cref="Event"/> and it points to caller stack memory,
	/// so it is only valid for the duration of the callback and must not be retained.
	/// </summary>
	public required nint EventData { get; init; }
}
