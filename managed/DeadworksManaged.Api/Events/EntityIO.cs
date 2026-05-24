namespace DeadworksManaged.Api;

/// <summary>
/// Hooks into the Valve entity I/O system to observe or intercept inputs and outputs.
/// Two symmetric APIs: <see cref="HookInput"/> for <c>CEntityInstance::AcceptInput</c>, <see cref="HookOutput"/> for <c>CEntityIOOutput::FireOutputInternal</c>.
/// </summary>
/// <remarks>
/// <para>Class and input/output names support <c>"*"</c> as a wildcard. Hooks are matched against four keys in priority order:
/// <c>(class, name)</c>, <c>(class, "*")</c>, <c>("*", name)</c>, <c>("*", "*")</c>.</para>
/// <para>Pre-mode handlers return <see cref="HookResult"/> (<see cref="HookResult.Stop"/> blocks the original).
/// Post-mode handlers return <see cref="void"/> and run after the original — they cannot block.</para>
/// </remarks>
public static class EntityIO {
	internal static Func<string, string, Delegate, HookMode, IHandle>? OnHookInput;
	internal static Func<string, string, Delegate, HookMode, IHandle>? OnHookOutput;

	// --- Input hooks ---

	/// <summary>Subscribes to entity inputs matching <paramref name="className"/> + <paramref name="inputName"/>. Handler may return <see cref="HookResult.Stop"/> to block the input.</summary>
	/// <param name="className">Receiving entity's designer name, or <c>"*"</c>.</param>
	/// <param name="inputName">Input name (e.g. <c>"Kill"</c>), or <c>"*"</c>.</param>
	/// <param name="handler">Pre-mode handler. Return <see cref="HookResult.Continue"/> to let the original run.</param>
	/// <param name="mode">When the handler runs. Defaults to <see cref="HookMode.Pre"/>.</param>
	/// <returns>A handle that cancels the hook when <see cref="IHandle.Cancel"/> is called. Plugin unload also auto-cancels.</returns>
	public static IHandle HookInput(string className, string inputName, Func<EntityInputEvent, HookResult> handler, HookMode mode = HookMode.Pre)
		=> OnHookInput?.Invoke(className, inputName, handler, mode) ?? CallbackHandle.Noop;

	/// <summary>Post-mode overload: observe-only, runs after the original input is processed.</summary>
	public static IHandle HookInput(string className, string inputName, Action<EntityInputEvent> handler)
		=> OnHookInput?.Invoke(className, inputName, handler, HookMode.Post) ?? CallbackHandle.Noop;

	// --- Output hooks ---

	/// <summary>Subscribes to entity outputs matching <paramref name="className"/> + <paramref name="outputName"/>. Handler may return <see cref="HookResult.Stop"/> to block the output.</summary>
	/// <param name="className">Firing entity's designer name, or <c>"*"</c>.</param>
	/// <param name="outputName">Output name (e.g. <c>"OnStartTouch"</c>), or <c>"*"</c>.</param>
	/// <param name="handler">Pre-mode handler. Return <see cref="HookResult.Continue"/> to let the original run.</param>
	/// <param name="mode">When the handler runs. Defaults to <see cref="HookMode.Pre"/>.</param>
	/// <returns>A handle that cancels the hook when <see cref="IHandle.Cancel"/> is called. Plugin unload also auto-cancels.</returns>
	public static IHandle HookOutput(string className, string outputName, Func<EntityOutputEvent, HookResult> handler, HookMode mode = HookMode.Pre)
		=> OnHookOutput?.Invoke(className, outputName, handler, mode) ?? CallbackHandle.Noop;

	/// <summary>Post-mode overload: observe-only, runs after the original output is fired.</summary>
	public static IHandle HookOutput(string className, string outputName, Action<EntityOutputEvent> handler)
		=> OnHookOutput?.Invoke(className, outputName, handler, HookMode.Post) ?? CallbackHandle.Noop;
}
