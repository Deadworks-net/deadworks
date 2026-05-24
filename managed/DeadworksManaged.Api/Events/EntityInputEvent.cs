namespace DeadworksManaged.Api;

/// <summary>Payload for an entity input dispatch, passed to handlers registered via <see cref="EntityIO.HookInput"/> or <c>[EntityInputHook]</c>.</summary>
public sealed class EntityInputEvent {
	/// <summary>The entity receiving the input.</summary>
	public required CBaseEntity Entity { get; init; }

	/// <summary>Designer name of the receiving entity (e.g. <c>"func_button"</c>). Already resolved at the native boundary — cheaper than reading <see cref="CBaseEntity.DesignerName"/>.</summary>
	public required string ClassName { get; init; }

	/// <summary>The name of the input being received (e.g. <c>"Kill"</c>).</summary>
	public required string InputName { get; init; }

	/// <summary>The entity that activated the input, if any.</summary>
	public CBaseEntity? Activator { get; init; }

	/// <summary>The entity that called the input, if any.</summary>
	public CBaseEntity? Caller { get; init; }

	/// <summary>The typed variant value carried by the input. Pointer is only valid for the duration of the callback.</summary>
	public required EntityIOValue Value { get; init; }
}
