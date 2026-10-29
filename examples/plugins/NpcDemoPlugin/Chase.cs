using System.Numerics;
using DeadworksManaged.Api;

namespace NpcDemoPlugin;

/// <summary>
/// Keeps a group of NPCs running at a moving target. Call <see cref="Update"/> every <see cref="Interval"/> seconds.
/// </summary>
/// <remarks>
/// Each NPC gets its own spot on a ring around the target, so the group spreads out. A running order is replaced
/// only once the target has moved far compared with the NPC's remaining distance, since each new order restarts the
/// path. NPCs that arrive are left to fight.
/// </remarks>
internal sealed class Chase {
	public const double Interval = 0.1;

	private const int Slices = 5;              // each NPC is considered every Slices * Interval seconds
	private const float ArriveRadius = 150f;
	private const float LeashRadius = 450f;    // after arriving, re-order only once it strays this far
	private const float MinRepath = 150f;
	private const float RepathFraction = 0.3f;

	// Schedules to leave alone until they finish, or the landing animation is cut short.
	private static readonly HashSet<string> Busy = [
		"SCHED_TROOPER_START_ZIPLINE", "SCHED_TROOPER_RIDE_ZIPLINE", "SCHED_TROOPER_DISMOUNT_ZIPLINE",
		"SCHED_TROOPER_WAIT_UNTIL_LAND_ON_GROUND", "SCHED_TROOPER_JUMP_TO_LANE",
		"SCHED_TROOPER_JUMP_TO_NAVIGABLE_AREA", "SCHED_TROOPER_STUNNED",
	];

	private sealed class Spot {
		public float Angle;
		public float Radius;
		public Vector3? LastGoal;
		public int Failures;
		public DateTime RetryAt;
	}

	private readonly Func<IEnumerable<CAI_BaseNPC>> _members;
	private readonly Dictionary<uint, Spot> _spots = [];
	private List<CAI_BaseNPC> _list = [];
	private DateTime _lastScan = DateTime.MinValue;
	private int _slice;

	public Chase(Func<IEnumerable<CAI_BaseNPC>> members) => _members = members;

	public int Orders { get; private set; }

	public void Update(Vector3 target) {
		if ((DateTime.UtcNow - _lastScan).TotalSeconds >= 1.0) {
			_list = _members().ToList();
			_lastScan = DateTime.UtcNow;
		}

		_slice = (_slice + 1) % Slices;
		for (int i = _slice; i < _list.Count; i += Slices) {
			var npc = _list[i];
			if (!npc.IsValid || npc.IsDormant || npc.Health <= 0) {
				_spots.Remove(npc.EntityHandle);
				continue;
			}
			var schedule = npc.Schedule;
			if (schedule == null || Busy.Contains(schedule))
				continue;

			var pos = npc.Position;
			if (!_spots.TryGetValue(npc.EntityHandle, out var spot)) {
				// Keep the side it comes from, spread by up to 60 degrees either way, on one of four rings.
				var from = pos - target;
				float jitter = ((npc.EntityIndex * 37) % 121 - 60) * MathF.PI / 180f;
				spot = new Spot { Angle = MathF.Atan2(from.Y, from.X) + jitter, Radius = 120f + 60f * (npc.EntityIndex % 4) };
				_spots[npc.EntityHandle] = spot;
			}
			if (schedule == "SCHED_FAIL" && spot.LastGoal != null) {
				// The last order failed, usually because the spot was off the navmesh. Pull it in and back off.
				spot.Failures++;
				spot.LastGoal = null;
				spot.RetryAt = DateTime.UtcNow.AddSeconds(Math.Min(spot.Failures, 3));
			}
			if (DateTime.UtcNow < spot.RetryAt)
				continue;

			float radius = spot.Failures switch { 0 => spot.Radius, 1 => spot.Radius * 0.5f, _ => 0f };
			var goal = target + new Vector3(MathF.Cos(spot.Angle), MathF.Sin(spot.Angle), 0f) * radius;
			if (radius > 0f) {
				var snapped = Nav.GetNearestPoint(goal, npc, maxDistance: radius + 100f);
				goal = snapped is { } s && Vector3.Distance(s, target) <= radius + 100f ? s : target;
			}
			float dist = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(goal.X, goal.Y));

			if (schedule == "SCHED_FORCED_GO" && spot.LastGoal is { } last) {
				if (dist <= ArriveRadius || Vector3.Distance(goal, last) < MathF.Max(MinRepath, RepathFraction * dist))
					continue;
			} else if (dist <= (spot.LastGoal == null ? ArriveRadius : LeashRadius)) {
				continue;
			}

			npc.MoveTo(goal, run: true);
			spot.LastGoal = goal;
			Orders++;
		}
	}

	/// <summary>Drops the orders still running.</summary>
	public void Release() {
		foreach (var npc in _list)
			if (npc.IsValid && !npc.IsDormant && npc.Schedule == "SCHED_FORCED_GO")
				npc.Stop();
		_spots.Clear();
		_list = [];
	}
}
