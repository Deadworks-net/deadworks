namespace DeadworksManaged.Api;

/// <summary>
/// Marks a method as an entity-output hook. The method is auto-registered on plugin load and dropped on unload.
/// </summary>
/// <example>
/// <code>
/// [EntityOutputHook("trigger_multiple", "OnStartTouch")]
/// public HookResult OnTriggerTouched(EntityOutputEvent e) {
///     return HookResult.Continue;
/// }
///
/// [EntityOutputHook("*", "OnPlayerPickup", HookMode.Post)]
/// public void OnAnyPickup(EntityOutputEvent e) { /* observer only */ }
/// </code>
/// </example>
/// <remarks>
/// Pre handlers must return <see cref="HookResult"/>. Post handlers must return <c>void</c>.
/// Use <c>"*"</c> as a wildcard for either <paramref name="className"/> or <paramref name="outputName"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EntityOutputHookAttribute : Attribute {
	/// <summary>Designer name of the entity firing the output (e.g. <c>"trigger_multiple"</c>) or <c>"*"</c> for any.</summary>
	public string ClassName { get; }

	/// <summary>Output name (e.g. <c>"OnStartTouch"</c>) or <c>"*"</c> for any.</summary>
	public string OutputName { get; }

	/// <summary>When the handler runs relative to the original output. Defaults to <see cref="HookMode.Pre"/>.</summary>
	public HookMode Mode { get; }

	public EntityOutputHookAttribute(string className, string outputName, HookMode mode = HookMode.Pre) {
		ClassName = className;
		OutputName = outputName;
		Mode = mode;
	}
}
