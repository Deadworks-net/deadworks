namespace DeadworksManaged.Api;

/// <summary>Static helpers to enumerate all connected player controllers and pawns.</summary>
public static class Players {
	/// <summary>Maximum number of player slots on the server.</summary>
	public const int MaxSlot = 31;

	private static readonly bool[] _connected = new bool[MaxSlot];

	/// <summary>Mark a slot as fully connected. Called from EntryPoint on ClientFullConnect.</summary>
	internal static void SetConnected(int slot, bool connected) {
		if ((uint)slot < MaxSlot)
			_connected[slot] = connected;
	}

	/// <summary>Reset all connection state. Called on map change / server startup.</summary>
	internal static void ResetAll() => Array.Clear(_connected);

	internal static Func<int, bool>? AuthenticatedResolver;

	/// <summary>
	/// Whether Steam has validated the player in <paramref name="slot"/>, so their SteamID can be trusted. Always true
	/// when <c>permissions.require_steam_auth</c> is off in <c>deadworks.jsonc</c>.
	/// </summary>
	public static bool IsAuthenticated(int slot) => AuthenticatedResolver?.Invoke(slot) ?? false;

	/// <summary>
	/// Raised once per connection when Steam validates a player (slot, SteamID64), usually a few seconds after they
	/// connect. Unsubscribe in <see cref="IDeadworksPlugin.OnUnload"/>.
	/// </summary>
	public static event Action<int, ulong>? ClientAuthorized;

	internal static void RaiseClientAuthorized(int slot, ulong steamId64) {
		var handlers = ClientAuthorized;
		if (handlers == null) return;
		foreach (var handler in handlers.GetInvocationList()) {
			try {
				((Action<int, ulong>)handler)(slot, steamId64);
			} catch (Exception ex) {
				Console.WriteLine($"[Players] ClientAuthorized handler threw: {ex.Message}");
			}
		}
	}

	/// <summary>Returns whether the given slot is marked as fully connected.</summary>
	public static bool IsConnected(int slot) => (uint)slot < MaxSlot && _connected[slot];

	/// <summary>Returns all player controllers that exist in the entity system.</summary>
	public static unsafe IEnumerable<CCitadelPlayerController> GetAllControllers() {
		var list = new List<CCitadelPlayerController>();
		for (int i = 0; i < MaxSlot; i++) {
			var ptr = NativeInterop.GetPlayerController(i);
			if (ptr != null)
				list.Add(new CCitadelPlayerController((nint)ptr));
		}
		return list;
	}

	/// <summary>Returns all player controllers for fully connected players.</summary>
	public static unsafe IEnumerable<CCitadelPlayerController> GetAll() {
		var list = new List<CCitadelPlayerController>();
		for (int i = 0; i < MaxSlot; i++) {
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
		for (int i = 0; i < MaxSlot; i++) {
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
		if ((uint)slot >= MaxSlot) return null;
		var ptr = NativeInterop.GetPlayerController(slot);
		return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
	}
}
