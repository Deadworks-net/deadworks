using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// An axis-aligned world volume that raises <see cref="Entered"/> and <see cref="Left"/> as players'
/// hero pawns move in and out of it. Use it for checkpoints, start/finish lines, reset volumes, safe
/// areas, or anything else that would otherwise be a hand-written point-in-box test polled every frame.
/// </summary>
/// <remarks>
/// <para>
/// Zones are evaluated once per simulated game frame against each connected player's pawn origin
/// (their feet), so a zone meant to catch a standing player needs to extend a little below the floor
/// and up to roughly head height. A player whose pawn goes away (for example while dead or before a
/// hero is picked) is treated as outside every zone.
/// </para>
/// <para>
/// Keep a reference to the zone for as long as it should be active and call <see cref="Dispose"/> when
/// done. The registry holds zones weakly so an abandoned zone stops ticking on its own, and all zones
/// are dropped when plugins are unloaded. To draw the volume, pass <see cref="Mins"/> and <see cref="Maxs"/>
/// to a beam or particle helper.
/// </para>
/// </remarks>
public sealed class Zone : IDisposable {
	private Vector3 _mins;
	private Vector3 _maxs;
	private readonly HashSet<int> _inside = new();
	private bool _disposed;

	/// <summary>Optional label, useful in logs and when a handler is shared between zones.</summary>
	public string? Name { get; set; }

	/// <summary>Arbitrary data for the owning plugin (an id, a checkpoint index, a route).</summary>
	public object? Tag { get; set; }

	/// <summary>When false the zone is skipped; everyone is treated as outside and no events fire.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Raised on the frame a player's pawn origin first lies inside the volume.</summary>
	public event Action<Zone, CCitadelPlayerController>? Entered;

	/// <summary>Raised on the frame a player's pawn origin first lies outside the volume after having been inside.</summary>
	public event Action<Zone, CCitadelPlayerController>? Left;

	/// <summary>Creates a zone spanning the box from <paramref name="mins"/> to <paramref name="maxs"/> in world space. The corners may be given in any order.</summary>
	public Zone(Vector3 mins, Vector3 maxs) {
		SetBounds(mins, maxs);
		ZoneRegistry.Register(this);
	}

	/// <summary>Creates a zone centred on <paramref name="center"/> extending <paramref name="halfExtents"/> in each direction.</summary>
	public static Zone FromCenter(Vector3 center, Vector3 halfExtents)
		=> new(center - halfExtents, center + halfExtents);

	/// <summary>
	/// Creates a zone at <paramref name="origin"/> with the given relative bounds, the way trigger
	/// entities are authored (for example an origin on the floor with mins of (-64,-64,-8) and maxs of (64,64,96)).
	/// </summary>
	public static Zone FromOrigin(Vector3 origin, Vector3 minsOffset, Vector3 maxsOffset)
		=> new(origin + minsOffset, origin + maxsOffset);

	/// <summary>Minimum corner in world space.</summary>
	public Vector3 Mins {
		get => _mins;
		set => SetBounds(value, _maxs);
	}

	/// <summary>Maximum corner in world space.</summary>
	public Vector3 Maxs {
		get => _maxs;
		set => SetBounds(_mins, value);
	}

	/// <summary>Centre of the volume.</summary>
	public Vector3 Center => (_mins + _maxs) * 0.5f;

	/// <summary>Extent of the volume along each axis.</summary>
	public Vector3 Size => _maxs - _mins;

	/// <summary>Replaces both corners at once. The corners may be given in any order.</summary>
	public void SetBounds(Vector3 a, Vector3 b) {
		_mins = Vector3.Min(a, b);
		_maxs = Vector3.Max(a, b);
	}

	/// <summary>Moves the zone so that its centre is at <paramref name="center"/>, keeping its size.</summary>
	public void MoveTo(Vector3 center) {
		var half = Size * 0.5f;
		SetBounds(center - half, center + half);
	}

	/// <summary>True if <paramref name="point"/> lies inside the volume (inclusive).</summary>
	public bool Contains(Vector3 point) => BoundingBox.Contains(_mins, _maxs, point);

	/// <summary>True if the player in <paramref name="slot"/> was inside the zone at the last evaluation.</summary>
	public bool IsInside(int slot) => _inside.Contains(slot);

	/// <inheritdoc cref="IsInside(int)"/>
	public bool IsInside(CBasePlayerController player) => _inside.Contains(player.Slot);

	/// <summary>Slots of the players currently inside. Do not modify the zone while iterating.</summary>
	public IEnumerable<int> Occupants => _inside;

	/// <summary>Number of players currently inside.</summary>
	public int OccupantCount => _inside.Count;

	/// <summary>Forgets every occupant without raising <see cref="Left"/>. Players still inside re-enter on the next frame.</summary>
	public void ResetOccupants() => _inside.Clear();

	/// <summary>
	/// Advances the occupancy state for one player. Returns what changed so the caller can raise events.
	/// Pure and deterministic; exposed for the host ticker and for tests.
	/// </summary>
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

	internal void RaiseEntered(CCitadelPlayerController player) => Entered?.Invoke(this, player);

	internal void RaiseLeft(CCitadelPlayerController player) => Left?.Invoke(this, player);

	/// <summary>Removes the player from the occupant set silently, for disconnects.</summary>
	internal void Forget(int slot) => _inside.Remove(slot);

	/// <summary>Stops evaluating the zone. No events fire after this, including <see cref="Left"/> for current occupants.</summary>
	public void Dispose() {
		if (_disposed) return;
		_disposed = true;
		Enabled = false;
		_inside.Clear();
		ZoneRegistry.Unregister(this);
	}

	public override string ToString() => Name != null ? $"Zone '{Name}' {_mins}..{_maxs}" : $"Zone {_mins}..{_maxs}";
}

/// <summary>Result of a single <see cref="Zone.Step"/> evaluation.</summary>
internal enum ZoneTransition {
	None,
	Entered,
	Left,
}

/// <summary>
/// Global registry of live <see cref="Zone"/> instances. The host ticks it once per simulated frame
/// and notifies it of disconnects and unloads. Zones are held weakly so an abandoned zone is dropped
/// by the collector rather than ticking forever.
/// </summary>
public static class ZoneRegistry {
	private static readonly List<WeakReference<Zone>> _zones = new();
	private static readonly List<Zone> _scratch = new();

	internal static void Register(Zone zone) {
		lock (_zones) _zones.Add(new WeakReference<Zone>(zone));
	}

	internal static void Unregister(Zone zone) {
		lock (_zones) {
			for (int i = _zones.Count - 1; i >= 0; i--) {
				if (!_zones[i].TryGetTarget(out var z) || ReferenceEquals(z, zone))
					_zones.RemoveAt(i);
			}
		}
	}

	/// <summary>Number of live zones. Mostly useful for diagnostics.</summary>
	public static int Count {
		get {
			lock (_zones) {
				int n = 0;
				foreach (var w in _zones)
					if (w.TryGetTarget(out _)) n++;
				return n;
			}
		}
	}

	/// <summary>Drops every zone. Called by the host when plugins are unloaded.</summary>
	internal static void Clear() {
		lock (_zones) _zones.Clear();
	}

	/// <summary>Removes <paramref name="slot"/> from every zone without raising events. Called by the host on disconnect.</summary>
	internal static void OnDisconnect(int slot) {
		Snapshot();
		foreach (var zone in _scratch)
			zone.Forget(slot);
	}

	/// <summary>Evaluates every zone against every connected player. Called by the host once per simulated frame.</summary>
	internal static void Tick() {
		Snapshot();
		if (_scratch.Count == 0) return;

		for (int slot = 0; slot < Players.MaxSlot; slot++) {
			if (!Players.IsConnected(slot)) continue;

			var player = Players.FromSlot(slot);
			if (player == null) continue;

			var pawn = player.GetHeroPawn();
			Vector3? position = pawn != null && pawn.IsValid ? pawn.Position : null;

			foreach (var zone in _scratch) {
				switch (zone.Step(slot, position)) {
					case ZoneTransition.Entered: zone.RaiseEntered(player); break;
					case ZoneTransition.Left: zone.RaiseLeft(player); break;
				}
			}
		}
	}

	private static void Snapshot() {
		_scratch.Clear();
		lock (_zones) {
			for (int i = _zones.Count - 1; i >= 0; i--) {
				if (_zones[i].TryGetTarget(out var zone))
					_scratch.Add(zone);
				else
					_zones.RemoveAt(i);
			}
		}
		_scratch.Reverse(); // keep registration order
	}
}
