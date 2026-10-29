using System.Numerics;
using DeadworksManaged.Api;

namespace NpcDemoPlugin;

/// <summary>
/// Walks a group of NPCs to one goal in short legs along a <see cref="Nav.FindPath(Vector3, Vector3, CBaseEntity, bool, bool)"/>
/// route. Call <see cref="Update"/> every <see cref="Interval"/> seconds.
/// </summary>
/// <remarks>
/// One <see cref="CAI_BaseNPC.MoveTo"/> to a far goal often fails partway (<c>SCHED_FAIL</c>), or at once on a long
/// detour. Each order here covers at most <see cref="LegLength"/> units of the route, and failed or finished orders
/// are re-issued. NPCs that arrive go back to their own AI.
/// </remarks>
internal sealed class Walk {
	public const double Interval = 0.5;

	private const float LegLength = 600f;
	private const float ArriveRadius = 150f;
	private const float Reorder = 200f;      // re-order once the leg target has moved on this far
	private const float OffRoute = 400f;     // re-plan once this far from the route
	private const int MaxFailures = 5;
	private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(40);

	// Schedules to leave alone until they finish, or the landing animation is cut short.
	private static readonly HashSet<string> Busy = [
		"SCHED_TROOPER_START_ZIPLINE", "SCHED_TROOPER_RIDE_ZIPLINE", "SCHED_TROOPER_DISMOUNT_ZIPLINE",
		"SCHED_TROOPER_WAIT_UNTIL_LAND_ON_GROUND", "SCHED_TROOPER_JUMP_TO_LANE",
		"SCHED_TROOPER_JUMP_TO_NAVIGABLE_AREA", "SCHED_TROOPER_STUNNED",
	];

	private sealed class Walker {
		public required CAI_BaseNPC Npc;
		public required NavPathFollower Route;
		public Vector3? Leg;
		public int Failures;
	}

	private readonly List<Walker> _walkers = [];
	private readonly DateTime _started = DateTime.UtcNow;

	public Walk(Vector3 goal) => Goal = goal;

	public Vector3 Goal { get; }
	public int Arrived { get; private set; }
	public int Died { get; private set; }
	/// <summary>The NPCs that gave up, counted by class.</summary>
	public Dictionary<string, int> GaveUp { get; } = [];
	public bool IsDone => _walkers.Count == 0;
	public TimeSpan Elapsed => DateTime.UtcNow - _started;

	/// <summary>Plans the NPC's route. False if there is none.</summary>
	public bool Add(CAI_BaseNPC npc) {
		if (Plan(npc) is not { } route)
			return false;
		_walkers.Add(new Walker { Npc = npc, Route = route });
		return true;
	}

	public void Update() {
		bool expired = Elapsed > GiveUpAfter;
		for (int i = _walkers.Count - 1; i >= 0; i--) {
			var w = _walkers[i];
			var npc = w.Npc;
			if (!npc.IsValid || npc.IsDormant || npc.Health <= 0) {
				Died++;
				_walkers.RemoveAt(i);
				continue;
			}
			var pos = npc.Position;
			if (Flat(pos - Goal).Length() <= ArriveRadius) {
				Arrived++;
				_walkers.RemoveAt(i);
				continue;
			}
			if (expired || w.Failures >= MaxFailures) {
				GaveUp[npc.Classname] = GaveUp.GetValueOrDefault(npc.Classname) + 1;
				_walkers.RemoveAt(i);
				continue;
			}
			var schedule = npc.Schedule;
			if (schedule == null || Busy.Contains(schedule))
				continue;
			if (schedule == "SCHED_FAIL" && w.Leg != null) {
				w.Failures++;
				w.Leg = null;
			}

			w.Route.Update(pos);
			if (w.Route.DistanceFromPath(pos) > OffRoute) {
				// Knocked or wandered off the route: plan again from here.
				if (Plan(npc) is not { } route) {
					w.Failures++;
					continue;
				}
				w.Route = route;
				w.Leg = null;
			}

			var leg = LegTarget(w.Route, pos);
			if (schedule == "SCHED_FORCED_GO" && w.Leg is { } last && Vector3.Distance(leg, last) < Reorder)
				continue;
			npc.MoveTo(leg, run: true);
			w.Leg = leg;
		}
	}

	/// <summary>Drops the orders still running.</summary>
	public void Release() {
		foreach (var w in _walkers)
			if (w.Npc.IsValid && !w.Npc.IsDormant && w.Npc.Schedule == "SCHED_FORCED_GO")
				w.Npc.Stop();
		_walkers.Clear();
	}

	private NavPathFollower? Plan(CAI_BaseNPC npc) {
		var start = Nav.GetNearestPoint(npc.Position, npc, 400f) ?? npc.Position;
		return Nav.FindPath(start, Goal, npc) is { } path ? new NavPathFollower(path, arriveRadius: 80f) : null;
	}

	/// <summary>The furthest waypoint at most <see cref="LegLength"/> units along the route from the NPC.</summary>
	private static Vector3 LegTarget(NavPathFollower route, Vector3 pos) {
		if (route.IsFinished)
			return route.Path.End;
		var points = route.Path.Waypoints;
		int i = route.Index;
		float left = LegLength - Vector3.Distance(pos, points[i].Position);
		while (i + 1 < points.Count) {
			float step = Vector3.Distance(points[i].Position, points[i + 1].Position);
			if (step > left)
				break;
			left -= step;
			i++;
		}
		return points[i].Position;
	}

	private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
}
