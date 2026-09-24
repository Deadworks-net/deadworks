namespace DeadworksManaged.Api;

/// <summary>
/// Passed to <see cref="IDeadworksPlugin.OnClientDisconnecting"/> and <see cref="IDeadworksPlugin.OnClientDisconnect"/>.
/// </summary>
public sealed class ClientDisconnectedEvent {
	/// <summary>The player's slot.</summary>
	public required int Slot { get; init; }

	/// <summary>Why they left, e.g. <see cref="ENetworkDisconnectionReason.NetworkDisconnectKicked"/>.</summary>
	public required ENetworkDisconnectionReason Reason { get; init; }

	/// <summary>The player's controller, or null if it's already gone.</summary>
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
