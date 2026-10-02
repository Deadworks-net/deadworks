using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace DeadworksManaged.Api.Utils;

/// <summary>
/// A box in the world that tells you when players walk into it and out of it, through <see cref="Entered"/>
/// and <see cref="Left"/>. Use it for checkpoints, start and finish lines, reset areas, safe areas, or anything
/// else you'd otherwise check by hand every frame.
/// </summary>
/// <remarks>
/// <para>
/// Zones check each player's position every game tick. The position is at the hero's feet, so to catch a
/// player standing in an area, start the box a little below the floor and go up to about head height. Dead
/// players, and players who haven't picked a hero yet, count as outside every zone.
/// </para>
/// <para>
/// Because positions are only checked once a tick, a fast player (dashing, on a zipline) can cross a thin zone
/// between two checks without ever being inside. Make zones that catch moving players, like finish lines, at
/// least 100 units deep in the direction they're crossed.
/// </para>
/// <para>
/// A zone keeps working until you call <see cref="Dispose"/> or your plugin unloads. To show it in the world,
/// draw it with <c>CBeam.CreateBox(zone.Mins, zone.Maxs)</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var finish = new Zone(new Vector3(-100, -100, 0), new Vector3(100, 100, 128)) { Name = "finish" };
/// finish.Entered += (zone, player, pawn) => Chat.PrintToChat(player, "You finished!");
///
/// var safeSpot = new Vector3(0, 0, 64);
/// var pit = new Zone(new Vector3(-500, -500, -300), new Vector3(500, 500, -200));
/// pit.Entered += (zone, player, pawn) => pawn.Teleport(position: safeSpot);
/// </code>
/// </example>
public sealed class Zone : IDisposable {
	private Vector3 _mins;
	private Vector3 _maxs;
	private readonly HashSet<int> _inside = new();
	private bool _disposed;

	/// <summary>Optional label, handy in logs and when one handler serves several zones.</summary>
	public string? Name { get; set; }

	/// <summary>Anything you want to attach to the zone, such as a checkpoint number.</summary>
	public object? Tag { get; set; }

	/// <summary>
	/// Turns the zone on and off. While off, everyone counts as outside: players inside get <see cref="Left"/>,
	/// and no <see cref="Entered"/> fires until it's turned back on.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Fires when a player gets inside the zone, whether they walked, teleported or respawned there. Along with the
	/// player you get their hero, which is alive and inside the zone.
	/// </summary>
	public event Action<Zone, CCitadelPlayerController, CCitadelPlayerPawn>? Entered;

	/// <summary>
	/// Fires when a player who was inside leaves, dies or loses their hero. Moving the zone off them or turning it
	/// off counts too. Along with the player you get their hero, which may be dead, or null if they no longer have one.
	/// </summary>
	public event Action<Zone, CCitadelPlayerController, CCitadelPlayerPawn?>? Left;

	// The plugin that created the zone, so its zones stop when it unloads.
	internal AssemblyLoadContext? Owner { get; }

	/// <summary>Creates a zone between two opposite corners, given in any order.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public Zone(Vector3 mins, Vector3 maxs) : this(mins, maxs, Assembly.GetCallingAssembly()) { }

	private Zone(Vector3 mins, Vector3 maxs, Assembly creator) {
		Owner = AssemblyLoadContext.GetLoadContext(creator);
		SetBounds(mins, maxs);
		ZoneRegistry.Register(this);
	}

	/// <summary>Creates a zone of the given <paramref name="size"/> with its middle at <paramref name="center"/>.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static Zone FromCenter(Vector3 center, Vector3 size)
		=> new(center - size * 0.5f, center + size * 0.5f, Assembly.GetCallingAssembly());

	/// <summary>
	/// Creates a zone from a point and corners relative to it, the way map triggers are set up. For example, an
	/// origin on the floor with <paramref name="minsOffset"/> (-64, -64, -8) and <paramref name="maxsOffset"/>
	/// (64, 64, 96) covers a 128 wide area from just below the floor to about head height.
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static Zone FromOrigin(Vector3 origin, Vector3 minsOffset, Vector3 maxsOffset)
		=> new(origin + minsOffset, origin + maxsOffset, Assembly.GetCallingAssembly());

	/// <summary>The corner with the lowest coordinates. Setting it beyond <see cref="Maxs"/> on an axis swaps the two on that axis.</summary>
	public Vector3 Mins {
		get => _mins;
		set => SetBounds(value, _maxs);
	}

	/// <summary>The corner with the highest coordinates. Setting it below <see cref="Mins"/> on an axis swaps the two on that axis.</summary>
	public Vector3 Maxs {
		get => _maxs;
		set => SetBounds(_mins, value);
	}

	/// <summary>The middle of the zone.</summary>
	public Vector3 Center => (_mins + _maxs) * 0.5f;

	/// <summary>How big the zone is along each axis.</summary>
	public Vector3 Size => _maxs - _mins;

	/// <summary>
	/// Moves both corners at once. They can be given in any order. On the next tick, players the zone no longer
	/// covers get <see cref="Left"/> and players it now covers get <see cref="Entered"/>.
	/// </summary>
	public void SetBounds(Vector3 a, Vector3 b) {
		_mins = Vector3.Min(a, b);
		_maxs = Vector3.Max(a, b);
	}

	/// <summary>Moves the zone so its middle is at <paramref name="center"/>, keeping its size. Enter and leave events follow as for <see cref="SetBounds"/>.</summary>
	public void MoveTo(Vector3 center) {
		var half = Size * 0.5f;
		SetBounds(center - half, center + half);
	}

	/// <summary>True if <paramref name="point"/> is inside the zone. Points exactly on the edge count as inside.</summary>
	public bool Contains(Vector3 point) => BoundingBox.Contains(_mins, _maxs, point);

	/// <summary>True if the player in <paramref name="slot"/> was inside at the last check.</summary>
	public bool IsInside(int slot) => _inside.Contains(slot);

	/// <inheritdoc cref="IsInside(int)"/>
	public bool IsInside(CBasePlayerController player) => _inside.Contains(player.Slot);

	/// <summary>The players inside right now.</summary>
	public IReadOnlyList<CCitadelPlayerController> Occupants {
		get {
			var players = new List<CCitadelPlayerController>(_inside.Count);
			foreach (int slot in _inside)
				if (Players.FromSlot(slot) is { } player)
					players.Add(player);
			return players;
		}
	}

	/// <summary>How many players are inside right now.</summary>
	public int OccupantCount => _inside.Count;

	// Occupants as slots, which the unit tests can check without a running game.
	internal IReadOnlyCollection<int> OccupantSlots => _inside.ToArray();

	/// <summary>
	/// Forgets who is inside, without firing <see cref="Left"/>. Players still inside get <see cref="Entered"/>
	/// again on the next tick, which is useful when restarting a round.
	/// </summary>
	public void ResetOccupants() => _inside.Clear();

	// Advances one player's state and reports what changed. Pure, so the unit tests drive it directly.
	internal ZoneTransition Step(int slot, Vector3? position) {
		bool inside = Enabled && position.HasValue && Contains(position.Value);
		bool wasInside = _inside.Contains(slot);

		if (inside && !wasInside) {
			_inside.Add(slot);
			return ZoneTransition.Entered;
		}
		if (!inside && wasInside) {
			_inside.Remove(slot);
			return ZoneTransition.Left;
		}
		return ZoneTransition.None;
	}

	internal void RaiseEntered(CCitadelPlayerController player, CCitadelPlayerPawn pawn) => Entered?.Invoke(this, player, pawn);

	internal void RaiseLeft(CCitadelPlayerController player, CCitadelPlayerPawn? pawn) => Left?.Invoke(this, player, pawn);

	// Drops a disconnected player without firing Left.
	internal void Forget(int slot) => _inside.Remove(slot);

	/// <summary>Stops the zone for good. No more events fire, not even <see cref="Left"/> for players still inside.</summary>
	public void Dispose() {
		if (_disposed) return;
		_disposed = true;
		Enabled = false;
		_inside.Clear();
		ZoneRegistry.Unregister(this);
	}

	/// <inheritdoc/>
	public override string ToString() => Name != null ? $"Zone '{Name}' {_mins}..{_maxs}" : $"Zone {_mins}..{_maxs}";
}

internal enum ZoneTransition {
	None,
	Entered,
	Left,
}

// Every live zone. The host ticks it once per simulated frame and tells it about disconnects and unloads.
// Zones are held until disposed or until the plugin that created them unloads.
internal static class ZoneRegistry {
	private static readonly List<Zone> _zones = new();

	internal static void Register(Zone zone) {
		lock (_zones) _zones.Add(zone);
	}

	internal static void Unregister(Zone zone) {
		lock (_zones) _zones.Remove(zone);
	}

	internal static int Count {
		get {
			lock (_zones) return _zones.Count;
		}
	}

	// Called when a plugin unloads, before its load context goes away.
	internal static void RemoveOwnedBy(AssemblyLoadContext context) {
		foreach (var zone in Snapshot())
			if (zone.Owner == context)
				zone.Dispose();
	}

	// Called when every plugin is unloaded.
	internal static void Clear() {
		foreach (var zone in Snapshot())
			zone.Dispose();
	}

	internal static void OnDisconnect(int slot) {
		foreach (var zone in Snapshot())
			zone.Forget(slot);
	}

	internal static void Tick() {
		var zones = Snapshot();
		if (zones.Length == 0) return;

		for (int slot = 0; slot < Players.MaxSlot; slot++) {
			if (!Players.IsConnected(slot)) continue;

			var player = Players.FromSlot(slot);
			if (player == null) continue;

			var pawn = player.GetHeroPawn();
			if (pawn != null && !pawn.IsValid)
				pawn = null;
			Vector3? position = pawn != null && pawn.IsAlive ? pawn.Position : null;

			foreach (var zone in zones) {
				switch (zone.Step(slot, position)) {
					// Entered only happens with a position, which needs a live pawn.
					case ZoneTransition.Entered: Raise(zone, nameof(Zone.Entered), () => zone.RaiseEntered(player, pawn!)); break;
					case ZoneTransition.Left: Raise(zone, nameof(Zone.Left), () => zone.RaiseLeft(player, pawn)); break;
				}
			}
		}
	}

	// A throwing handler must not stop the other zones, or the plugins' OnGameFrame that runs after this.
	private static void Raise(Zone zone, string eventName, Action raise) {
		try {
			raise();
		} catch (Exception ex) {
			Console.WriteLine($"[Zone] {eventName} handler for {zone} threw: {ex.Message}");
		}
	}

	// A copy, so handlers can create and dispose zones while the tick walks the list.
	private static Zone[] Snapshot() {
		lock (_zones) return _zones.ToArray();
	}
}
