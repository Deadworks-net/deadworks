namespace DeadworksManaged.Api;

/// <summary>
/// The content addons connecting clients are told to download.
/// <para>
/// The active list is the server config's <c>serverbrowser.content_addons</c> merged with whatever every
/// loaded plugin declares through <see cref="IDeadworksPlugin.ContentAddons"/>, de-duplicated and kept in
/// that order. Declaring an addon from a plugin means it no longer has to be listed in the server config
/// ahead of time - loading the plugin is enough.
/// </para>
/// </summary>
public static class ContentAddons {
	internal static Func<IReadOnlyList<string>>? ResolveActive;
	internal static Action? RequestRefresh;

	/// <summary>The merged addon list currently advertised to clients.</summary>
	public static IReadOnlyList<string> Active => ResolveActive?.Invoke() ?? [];

	/// <summary>
	/// Re-reads <see cref="IDeadworksPlugin.ContentAddons"/> from every loaded plugin and re-applies the
	/// merged list. Plugins are queried automatically when they load and unload, so this is only needed
	/// when a plugin's own list changes while it is running.
	/// </summary>
	public static void Refresh() => RequestRefresh?.Invoke();
}
