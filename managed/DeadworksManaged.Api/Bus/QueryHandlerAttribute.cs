namespace DeadworksManaged.Api;

/// <summary>
/// Marks a method to be auto-registered as a <see cref="PluginBus"/> query responder for the named query.
/// The handler method may take zero parameters, a single <see cref="QueryContext"/>, or a single
/// reference-type parameter matching the expected request (the handler is skipped when the request
/// is null or a different type). Return type must be a non-void response type — the declared response
/// type must match the caller's <c>TResponse</c> for the handler to be invoked.
/// Multiple plugins (and multiple methods within a plugin) may declare handlers for the same query name;
/// every matching handler contributes one entry to the result list.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QueryHandlerAttribute : Attribute {
	/// <summary>The query name this handler responds to (e.g. <c>"plugin_a:get_player_count"</c>).</summary>
	public string QueryName { get; }

	public QueryHandlerAttribute(string queryName) => QueryName = queryName;
}
