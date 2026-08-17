namespace DeadworksManaged.Api;

/// <summary>
/// Framework-level identity. The version here is the single source of truth:
/// it rides the UI channel's heartbeat, so every connected client learns it
/// automatically without the UI addon having to hardcode anything.
/// </summary>
public static class Deadworks {
	/// <summary>
	/// Framework version, surfaced to clients on the heartbeat.
	///
	/// Sent verbatim, so keep it free of the wire's field separator (0x1F).
	/// Bump this on release; nothing else needs to change — the client shows
	/// whatever the server it is currently connected to reports.
	/// </summary>
	public const string Version = "v0.4.14";
}
