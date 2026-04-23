using System.Runtime.CompilerServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Plugin-to-plugin pub/sub. Publishers call <see cref="Publish(string, object?)"/> with an event name
/// and optional payload; subscribers receive events by subscribing to the name via
/// <see cref="Subscribe(string, System.Action{CustomEventContext})"/> (and overloads) or by
/// annotating a method with <see cref="CustomEventHandlerAttribute"/>.
/// </summary>
/// <remarks>
/// Recommended naming: <c>plugin_name:event_name</c> (snake_case), e.g. <c>"my_plugin:round_started"</c>.
/// Use <see cref="HasSubscribers(string)"/> to skip expensive payload construction when nobody is listening.
/// Manually-subscribed handlers obtained via <see cref="Subscribe(string, System.Action{CustomEventContext})"/>
/// are automatically removed when the calling plugin is unloaded — no <c>OnUnload</c> bookkeeping required.
/// </remarks>
public static class CustomEvents {
	internal static Func<string, Delegate, IHandle>? OnSubscribe;
	internal static Action<string, Delegate>? OnUnsubscribe;
	internal static Func<string, object?, HookResult>? OnPublish;
	internal static Func<string, int>? OnSubscriberCount;

	/// <summary>Subscribe with a context-receiving handler that can return a <see cref="HookResult"/>.</summary>
	public static IHandle Subscribe(string name, Func<CustomEventContext, HookResult> handler)
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a fire-and-forget context-receiving handler (implicitly returns <see cref="HookResult.Continue"/>).</summary>
	public static IHandle Subscribe(string name, Action<CustomEventContext> handler)
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a typed-payload handler. Only fires when the published payload is of type <typeparamref name="T"/>; silently skipped otherwise.</summary>
	public static IHandle Subscribe<T>(string name, Func<T, HookResult> handler) where T : class
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a typed fire-and-forget payload handler. Only fires when the published payload is of type <typeparamref name="T"/>.</summary>
	public static IHandle Subscribe<T>(string name, Action<T> handler) where T : class
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Remove a previously-registered handler. Prefer <see cref="IHandle.Cancel"/> on the handle returned by <c>Subscribe</c>.</summary>
	public static void Unsubscribe(string name, Delegate handler) => OnUnsubscribe?.Invoke(name, handler);

	/// <summary>Publish a custom event. Returns the aggregated <see cref="HookResult"/> across all subscribers (max-wins).</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static HookResult Publish(string name, object? payload = null)
		=> OnPublish?.Invoke(name, payload) ?? HookResult.Continue;

	/// <summary>Returns <c>true</c> if at least one handler is subscribed to the named event. Use to gate expensive payload construction.</summary>
	public static bool HasSubscribers(string name) => SubscriberCount(name) > 0;

	/// <summary>Returns the number of handlers currently subscribed to the named event.</summary>
	public static int SubscriberCount(string name) => OnSubscriberCount?.Invoke(name) ?? 0;
}
