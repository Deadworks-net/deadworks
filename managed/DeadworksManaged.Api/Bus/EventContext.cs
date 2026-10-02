namespace DeadworksManaged.Api;

/// <summary>
/// Context passed to event subscribers on <see cref="PluginBus"/>. Carries the event name, the
/// publisher's payload, and the name of the plugin (or <see cref="HostSenderName"/>) that published.
/// </summary>
public sealed class EventContext {
	/// <summary><see cref="SenderPluginName"/> value used when an event is published from host code, not from a plugin.</summary>
	public const string HostSenderName = "<host>";

	/// <summary>The event name as given to <see cref="PluginBus.Publish(string, object?)"/>.</summary>
	public string Name { get; }

	/// <summary>The payload supplied by the publisher, or <c>null</c> if none was provided.</summary>
	public object? Payload { get; }

	/// <summary>Name of the plugin that published the event, or <see cref="HostSenderName"/> when published from host code.</summary>
	public string SenderPluginName { get; }

	internal EventContext(string name, object? payload, string senderPluginName) {
		Name = name;
		Payload = payload;
		SenderPluginName = senderPluginName;
	}

	/// <summary>Returns <see cref="Payload"/> cast to <typeparamref name="T"/>, or <c>null</c> if the payload is null or a different type.</summary>
	public T? PayloadAs<T>() where T : class => Payload as T;
}
