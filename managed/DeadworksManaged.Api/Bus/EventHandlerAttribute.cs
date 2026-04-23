namespace DeadworksManaged.Api;

/// <summary>
/// Marks a method to be auto-registered as a <see cref="PluginBus"/> event subscriber for the named event.
/// The handler method may take zero parameters, a single <see cref="EventContext"/>, or a single
/// reference-type parameter matching the expected payload (the handler is skipped when the payload
/// is null or a different type). Return type may be <see cref="HookResult"/> or <c>void</c>
/// (treated as <see cref="HookResult.Continue"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EventHandlerAttribute : Attribute {
	/// <summary>The event name this handler subscribes to (e.g. <c>"plugin_a:round_started"</c>).</summary>
	public string EventName { get; }

	public EventHandlerAttribute(string eventName) => EventName = eventName;
}
