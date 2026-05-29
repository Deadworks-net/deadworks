namespace DeadworksManaged.Api;

/// <summary>Event data passed to <see cref="IDeadworksPlugin.OnProcessUsercmds"/>. This is the full protobuf mutation/debug path; use <see cref="FastProcessUsercmdsEvent"/> for cheap read-only telemetry.</summary>
public sealed class ProcessUsercmdsEvent {
	/// <summary>Player slot (0-63).</summary>
	public required int PlayerSlot { get; init; }

	/// <summary>Parsed CCitadelUserCmdPB protobuf messages for this tick. Modifications are serialized back to the engine.</summary>
	public required List<CCitadelUserCmdPB> Usercmds { get; init; }

	/// <summary>Whether the game is paused.</summary>
	public required bool Paused { get; init; }

	/// <summary>Network margin in seconds.</summary>
	public required float Margin { get; init; }

	/// <summary>Gets the player controller for this player slot.</summary>
	[System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
	public unsafe CCitadelPlayerController? Controller {
		get {
			if (NativeInterop.GetPlayerController == null)
				return null;
			var ptr = NativeInterop.GetPlayerController(PlayerSlot);
			return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
		}
	}
}
