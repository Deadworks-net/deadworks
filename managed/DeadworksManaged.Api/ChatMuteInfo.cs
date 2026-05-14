namespace DeadworksManaged.Api;

/// <summary>Describes a player whose normal chat messages are blocked.</summary>
public sealed class ChatMuteInfo
{
	/// <summary>The muted player's SteamID64.</summary>
	public required ulong SteamId64 { get; init; }

	/// <summary>The reason shown to the muted player when their chat is blocked.</summary>
	public string Reason { get; init; } = "Chat muted";

	/// <summary>When the chat mute was created.</summary>
	public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

	/// <summary>When the chat mute expires, or <see langword="null"/> for a permanent mute.</summary>
	public DateTimeOffset? ExpiresAtUtc { get; init; }
}
