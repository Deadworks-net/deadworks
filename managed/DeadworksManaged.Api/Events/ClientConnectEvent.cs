namespace DeadworksManaged.Api;

/// <summary>Fired when a client is connecting to the server. Passed to <see cref="IDeadworksPlugin.OnClientConnect"/>.</summary>
public sealed class ClientConnectEvent {
	public required int Slot { get; init; }
	public required string Name { get; init; }
	public required ulong SteamId { get; init; }
	public required string IpAddress { get; init; }

	/// <summary>
	/// True for a player who was already in the game and is reloading after a map change, rather than joining. A map
	/// change doesn't disconnect anyone, but everyone goes through the connect events again on the new map; this tells
	/// them apart from new arrivals. <see cref="ClientPutInServerEvent"/> and <see cref="ClientFullConnectEvent"/> carry
	/// the same value for the same connection. Always false for bots, which a map change removes.
	/// </summary>
	public bool IsMapChangeReconnect { get; init; }
}
