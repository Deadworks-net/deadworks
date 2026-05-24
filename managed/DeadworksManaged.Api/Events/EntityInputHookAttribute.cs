namespace DeadworksManaged.Api;

/// <summary>
/// Marks a method as an entity-input hook. The method is auto-registered on plugin load and dropped on unload.
/// </summary>
/// <example>
/// <code>
/// [EntityInputHook("func_button", "Kill")]
/// public HookResult OnKillInput(EntityInputEvent e) {
///     return HookResult.Stop;   // veto: button won't kill the activator
/// }
///
/// [EntityInputHook("*", "Toggle", HookMode.Post)]
/// public void OnToggleObserved(EntityInputEvent e) { /* post: cannot block */ }
/// </code>
/// </example>
/// <remarks>
/// Pre handlers must return <see cref="HookResult"/>. Post handlers must return <c>void</c>.
/// Use <c>"*"</c> as a wildcard for either <paramref name="className"/> or <paramref name="inputName"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EntityInputHookAttribute : Attribute {
	/// <summary>Designer name of the receiving entity (e.g. <c>"func_button"</c>) or <c>"*"</c> for any.</summary>
	public string ClassName { get; }

	/// <summary>Input name (e.g. <c>"Kill"</c>, <c>"Toggle"</c>) or <c>"*"</c> for any.</summary>
	public string InputName { get; }

	/// <summary>When the handler runs relative to the original input. Defaults to <see cref="HookMode.Pre"/>.</summary>
	public HookMode Mode { get; }

	public EntityInputHookAttribute(string className, string inputName, HookMode mode = HookMode.Pre) {
		ClassName = className;
		InputName = inputName;
		Mode = mode;
	}
}
