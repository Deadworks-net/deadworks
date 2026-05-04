namespace DeadworksManaged.Api;

/// <summary>Incoming chat message from a player. Passed to <see cref="IDeadworksPlugin.OnChatMessage"/>.</summary>
public sealed class ChatMessage {
	public required int SenderSlot { get; init; }
	/// <summary>The sender's SteamID64 when it was available at dispatch time.</summary>
	public ulong? SenderSteamId64 { get; init; }
	public required string ChatText { get; init; }
	public required bool AllChat { get; init; }
	public required LaneColor LaneColor { get; init; }

	[System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
	public unsafe CCitadelPlayerController? Controller {
		get {
			if (NativeInterop.GetPlayerController == null)
				return null;
			var ptr = NativeInterop.GetPlayerController(SenderSlot);
			return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
		}
	}
}
