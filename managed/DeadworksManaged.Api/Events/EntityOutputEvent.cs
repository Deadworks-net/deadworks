namespace DeadworksManaged.Api;

/// <summary>Payload for an entity output fire (<c>CEntityIOOutput::FireOutputInternal</c>), passed to handlers registered via <see cref="EntityIO.HookOutput"/> or <c>[EntityOutputHook]</c>.</summary>
public sealed class EntityOutputEvent {
	/// <summary>Designer name of the entity firing the output (e.g. <c>"trigger_multiple"</c>).</summary>
	public required string CallerClass { get; init; }

	/// <summary>The name of the output being fired (e.g. <c>"OnStartTouch"</c>).</summary>
	public required string OutputName { get; init; }

	/// <summary>The entity that triggered this output (typically a player pawn).</summary>
	public CBaseEntity? Activator { get; init; }

	/// <summary>The entity that fired the output. Usually equivalent to looking up an entity with <see cref="CallerClass"/>.</summary>
	public CBaseEntity? Caller { get; init; }

	/// <summary>The typed variant value carried by the output. Pointer is only valid for the duration of the callback.</summary>
	public required EntityIOValue Value { get; init; }

	/// <summary>Delay (seconds) before the output's connected inputs will fire. Read-only.</summary>
	public required float Delay { get; init; }
}
