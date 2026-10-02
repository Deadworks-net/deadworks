using System.Runtime.CompilerServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Unified plugin-to-plugin communication bus. Supports two interaction styles:
/// <list type="bullet">
///   <item><description><b>Events</b> — fire-and-forget fan-out. Publishers call <see cref="Publish(string, object?)"/>;
///   every subscriber registered for that name receives the event. Use <see cref="Subscribe(string, System.Action{EventContext})"/>
///   (and overloads) or annotate a method with <see cref="EventHandlerAttribute"/>.</description></item>
///   <item><description><b>Queries</b> — request/response, collect-all. Callers invoke <see cref="Query{TResponse}(string, object?)"/>
///   and receive a list of responses from every handler that declared a matching <c>TResponse</c> type.
///   Register responders with <see cref="HandleQuery{TResponse}(string, System.Func{TResponse})"/> (and overloads) or
///   annotate a method with <see cref="QueryHandlerAttribute"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Recommended naming: <c>plugin_name:event_or_query_name</c> (snake_case), e.g. <c>"my_plugin:round_started"</c> or
/// <c>"my_plugin:get_player_count"</c>. Use <see cref="HasSubscribers(string)"/> / <see cref="HasQueryHandlers(string)"/>
/// to skip expensive payload construction when nobody is listening. Manually-registered handlers are automatically
/// removed when the calling plugin is unloaded — no <c>OnUnload</c> bookkeeping required.
/// Dispatch is synchronous; handlers run on the calling thread.
/// </remarks>
public static class PluginBus {
	// --- Event plumbing (host sets these at init) ---
	internal static Func<string, Delegate, IHandle>? OnSubscribe;
	internal static Func<string, object?, HookResult>? OnPublish;
	internal static Func<string, int>? OnSubscriberCount;

	// --- Query plumbing (host sets these at init) ---
	internal static Func<string, Delegate, Type, IHandle>? OnHandleQuery;
	internal static Func<string, object?, Type, IReadOnlyList<object?>>? OnQuery;
	internal static Func<string, int>? OnQueryHandlerCount;

	// ========== Events ==========

	/// <summary>Subscribe with a context-receiving handler that can return a <see cref="HookResult"/>.</summary>
	public static IHandle Subscribe(string name, Func<EventContext, HookResult> handler)
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a fire-and-forget context-receiving handler (implicitly returns <see cref="HookResult.Continue"/>).</summary>
	public static IHandle Subscribe(string name, Action<EventContext> handler)
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a typed-payload handler. Only fires when the published payload is of type <typeparamref name="T"/>; silently skipped otherwise.</summary>
	public static IHandle Subscribe<T>(string name, Func<T, HookResult> handler) where T : class
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Subscribe with a typed fire-and-forget payload handler. Only fires when the published payload is of type <typeparamref name="T"/>.</summary>
	public static IHandle Subscribe<T>(string name, Action<T> handler) where T : class
		=> OnSubscribe?.Invoke(name, handler) ?? CallbackHandle.Noop;

	/// <summary>Publish an event. Returns the aggregated <see cref="HookResult"/> across all subscribers (max-wins).</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static HookResult Publish(string name, object? payload = null)
		=> OnPublish?.Invoke(name, payload) ?? HookResult.Continue;

	/// <summary>Returns <c>true</c> if at least one handler is subscribed to the named event. Use to gate expensive payload construction.</summary>
	public static bool HasSubscribers(string name) => SubscriberCount(name) > 0;

	/// <summary>Returns the number of handlers currently subscribed to the named event.</summary>
	public static int SubscriberCount(string name) => OnSubscriberCount?.Invoke(name) ?? 0;

	// ========== Queries ==========

	/// <summary>Register a no-argument query handler that returns a <typeparamref name="TResponse"/>.</summary>
	public static IHandle HandleQuery<TResponse>(string name, Func<TResponse> handler)
		=> OnHandleQuery?.Invoke(name, handler, typeof(TResponse)) ?? CallbackHandle.Noop;

	/// <summary>Register a query handler that receives a <see cref="QueryContext"/> and returns a <typeparamref name="TResponse"/>.</summary>
	public static IHandle HandleQuery<TResponse>(string name, Func<QueryContext, TResponse> handler)
		=> OnHandleQuery?.Invoke(name, handler, typeof(TResponse)) ?? CallbackHandle.Noop;

	/// <summary>Register a typed-request query handler. Only fires when the request is of type <typeparamref name="TRequest"/>.</summary>
	public static IHandle HandleQuery<TRequest, TResponse>(string name, Func<TRequest, TResponse> handler) where TRequest : class
		=> OnHandleQuery?.Invoke(name, handler, typeof(TResponse)) ?? CallbackHandle.Noop;

	/// <summary>
	/// Issue a query and collect responses from every registered handler whose declared response type matches <typeparamref name="TResponse"/>.
	/// Returns an empty list if no matching handler is registered. Handlers that throw are logged and skipped.
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static IReadOnlyList<TResponse> Query<TResponse>(string name, object? request = null) {
		var results = OnQuery?.Invoke(name, request, typeof(TResponse));
		if (results == null || results.Count == 0)
			return Array.Empty<TResponse>();
		var typed = new TResponse[results.Count];
		for (var i = 0; i < results.Count; i++)
			typed[i] = (TResponse)results[i]!;
		return typed;
	}

	/// <summary>Returns <c>true</c> if at least one query handler is registered for the named query (regardless of response type).</summary>
	public static bool HasQueryHandlers(string name) => QueryHandlerCount(name) > 0;

	/// <summary>Returns the number of query handlers currently registered for the named query (regardless of response type).</summary>
	public static int QueryHandlerCount(string name) => OnQueryHandlerCount?.Invoke(name) ?? 0;
}
