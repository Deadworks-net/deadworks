namespace DeadworksManaged.Api;

/// <summary>Static helpers to enumerate all connected player controllers and pawns.</summary>
public static class Players {
	/// <summary>Maximum number of player slots on the server.</summary>
	public const int MaxSlot = 31;

	private static readonly bool[] _connected = new bool[MaxSlot];
	private static readonly ulong[] _steamIds = new ulong[MaxSlot];
	private static readonly bool[] _reconnecting = new bool[MaxSlot];

	// SteamIDs of the players who were in the game when the last map ended and haven't finished loading into this one.
	private static readonly HashSet<ulong> _expected = new();

	/// <summary>
	/// A new map is starting. Everyone connected, or still loading back in, reloads into it and connects again.
	/// </summary>
	internal static void OnMapStart() {
		_expected.Clear();
		for (int i = 0; i < MaxSlot; i++) {
			if ((_connected[i] || _reconnecting[i]) && _steamIds[i] != 0)
				_expected.Add(_steamIds[i]);
		}
		Array.Clear(_connected);
		Array.Clear(_steamIds);
		Array.Clear(_reconnecting);
	}

	/// <summary>A client is connecting to <paramref name="slot"/>. Returns whether they're reloading after a map change.</summary>
	internal static bool OnConnect(int slot, ulong steamId) {
		if ((uint)slot >= MaxSlot) return false;
		_steamIds[slot] = steamId;
		return _reconnecting[slot] = steamId != 0 && _expected.Contains(steamId);
	}

	/// <summary>Bots skip <see cref="OnConnect"/>, so this records every player, bots included.</summary>
	internal static void OnPutInServer(int slot, ulong steamId) {
		if ((uint)slot >= MaxSlot) return;
		_steamIds[slot] = steamId;
		_reconnecting[slot] = steamId != 0 && _expected.Contains(steamId);
	}

	internal static void OnFullConnect(int slot) {
		if ((uint)slot >= MaxSlot) return;
		_connected[slot] = true;
		_expected.Remove(_steamIds[slot]);
	}

	internal static void OnDisconnect(int slot) {
		if ((uint)slot >= MaxSlot) return;
		_expected.Remove(_steamIds[slot]);
		_connected[slot] = false;
		_steamIds[slot] = 0;
		_reconnecting[slot] = false;
	}

	/// <summary>
	/// Whether the player in <paramref name="slot"/> is reloading after a map change, rather than joining. See
	/// <see cref="ClientConnectEvent.IsMapChangeReconnect"/>.
	/// </summary>
	public static bool IsMapChangeReconnect(int slot) => (uint)slot < MaxSlot && _reconnecting[slot];

	/// <summary>
	/// Slots this server has, from its player count. Entities past the last slot are ordinary map entities, not
	/// player controllers, so nothing reads them as one.
	/// </summary>
	private static int SlotCount => Math.Clamp(GlobalVars.MaxClients, 0, MaxSlot);

	/// <summary>Returns whether the given slot is marked as fully connected.</summary>
	public static bool IsConnected(int slot) => (uint)slot < MaxSlot && _connected[slot];

	/// <summary>Returns all player controllers that exist in the entity system.</summary>
	public static unsafe IEnumerable<CCitadelPlayerController> GetAllControllers() {
		var list = new List<CCitadelPlayerController>();
		for (int i = 0; i < SlotCount; i++) {
			var ptr = NativeInterop.GetPlayerController(i);
			if (ptr != null)
				list.Add(new CCitadelPlayerController((nint)ptr));
		}
		return list;
	}

	/// <summary>Returns all player controllers for fully connected players.</summary>
	public static unsafe IEnumerable<CCitadelPlayerController> GetAll() {
		var list = new List<CCitadelPlayerController>();
		for (int i = 0; i < SlotCount; i++) {
			if (!_connected[i]) continue;
			var ptr = NativeInterop.GetPlayerController(i);
			if (ptr != null)
				list.Add(new CCitadelPlayerController((nint)ptr));
		}
		return list;
	}

	/// <summary>Returns the hero pawn for every connected player that has one.</summary>
	public static unsafe IEnumerable<CCitadelPlayerPawn> GetAllPawns() {
		var list = new List<CCitadelPlayerPawn>();
		for (int i = 0; i < SlotCount; i++) {
			if (!_connected[i]) continue;
			var ptr = NativeInterop.GetPlayerController(i);
			if (ptr == null) continue;
			var pawn = NativeInterop.GetHeroPawn(ptr);
			if (pawn != null)
				list.Add(new CCitadelPlayerPawn((nint)pawn));
		}
		return list;
	}

	/// <summary>Returns the player controller in the given slot, or null if the slot is empty.</summary>
	public static unsafe CCitadelPlayerController? FromSlot(int slot) {
		if ((uint)slot >= SlotCount) return null;
		var ptr = NativeInterop.GetPlayerController(slot);
		return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
	}
}
