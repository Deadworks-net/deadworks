namespace DeadworksManaged.Api;

/// <summary>
/// Context passed to query handlers on <see cref="PluginBus"/>. Carries the query name, the
/// requester's request payload, and the name of the plugin (or <see cref="HostSenderName"/>) that issued the query.
/// </summary>
public sealed class QueryContext {
	/// <summary><see cref="SenderPluginName"/> value used when a query is issued from host code, not from a plugin.</summary>
	public const string HostSenderName = "<host>";

	/// <summary>The query name as given to <see cref="PluginBus.Query{TResponse}(string, object?)"/>.</summary>
	public string Name { get; }

	/// <summary>The request supplied by the caller, or <c>null</c> if none was provided.</summary>
	public object? Request { get; }

	/// <summary>Name of the plugin that issued the query, or <see cref="HostSenderName"/> when issued from host code.</summary>
	public string SenderPluginName { get; }

	internal QueryContext(string name, object? request, string senderPluginName) {
		Name = name;
		Request = request;
		SenderPluginName = senderPluginName;
	}

	/// <summary>Returns <see cref="Request"/> cast to <typeparamref name="T"/>, or <c>null</c> if the request is null or a different type.</summary>
	public T? RequestAs<T>() where T : class => Request as T;
}
