namespace DeadworksManaged.Api;

/// <summary>
/// Fired once per connection when Steam confirms who a player is, usually a few seconds after they connect.
/// Passed to <see cref="IDeadworksPlugin.OnClientAuthorized"/>.
/// </summary>
public sealed class ClientAuthorizedEvent {
	/// <summary>The player's slot.</summary>
	public required int Slot { get; init; }
	/// <summary>The SteamID the player connected with, now confirmed by Steam.</summary>
	public required ulong SteamId64 { get; init; }

	/// <summary>The player's controller, or null if they've already left.</summary>
	[System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
	public CCitadelPlayerController? Controller => Players.FromSlot(Slot);
}
