namespace DeadworksManaged.Api;

/// <summary>Fired when a client has fully connected and is in-game. Passed to <see cref="IDeadworksPlugin.OnClientFullConnect"/>.</summary>
public sealed class ClientFullConnectEvent {
	public required int Slot { get; init; }

	/// <summary>
	/// True for a player reloading after a map change, rather than joining. See
	/// <see cref="ClientConnectEvent.IsMapChangeReconnect"/>.
	/// </summary>
	public bool IsMapChangeReconnect { get; init; }

	[System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
	public unsafe CCitadelPlayerController? Controller {
		get {
			if (NativeInterop.GetPlayerController == null)
				return null;
			var ptr = NativeInterop.GetPlayerController(Slot);
			return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
		}
	}
}
