namespace DeadworksManaged.Api;

/// <summary>When an entity I/O hook fires relative to the original handler.</summary>
public enum HookMode {
	/// <summary>Fires before the original. Return <see cref="HookResult.Stop"/> or <see cref="HookResult.Handled"/> to block it.</summary>
	Pre = 0,
	/// <summary>Fires after the original ran. Handler returns void; cannot block.</summary>
	Post = 1,
}
