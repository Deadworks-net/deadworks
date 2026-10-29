using System.Numerics;
using DeadworksManaged.Api;

namespace NpcDemoPlugin;

/// <summary>
/// Troopers that work for a hero (SetMinion + SetSummoner). They hold spots around it that turn with its facing and
/// fight whatever comes in range. Call <see cref="Update"/> every <see cref="Interval"/> seconds.
/// </summary>
internal sealed class Squad {
	public const double Interval = 0.25;

	private const float SpotTolerance = 120f;
	private const float Repath = 150f;
	private const float LeashRadius = 900f;  // pulled back even mid-fight beyond this

	// Lane-walking schedules, swapped for standing still so a minion doesn't head back to its lane.
	private static readonly HashSet<string> LaneSchedules = [
		"SCHED_TROOPER_RUN_DOWN_LANE", "SCHED_TROOPER_RUN_TO_ENEMY_BASE_OBJECTIVE", "SCHED_TROOPER_SENTRY",
		"SCHED_TROOPER_PAUSE",
	];

	private static readonly HashSet<string> Busy = [
		"SCHED_TROOPER_START_ZIPLINE", "SCHED_TROOPER_RIDE_ZIPLINE", "SCHED_TROOPER_DISMOUNT_ZIPLINE",
		"SCHED_TROOPER_WAIT_UNTIL_LAND_ON_GROUND", "SCHED_TROOPER_JUMP_TO_LANE",
		"SCHED_TROOPER_JUMP_TO_NAVIGABLE_AREA", "SCHED_TROOPER_STUNNED",
	];

	// Spots around the owner in its facing frame: (forward, right).
	private static readonly Vector2[] Formation = [
		new(170, -110), new(170, 110), new(0, -190), new(0, 190), new(-170, -110), new(-170, 110), new(260, 0), new(-260, 0),
	];

	private sealed class Member {
		public required CNPC_Trooper Npc;
		public int Slot;
		public Vector3? LastOrder;
	}

	private readonly List<Member> _members = [];

	public CCitadelPlayerPawn Owner { get; }
	public static int MaxSize => Formation.Length;
	public int Count => _members.Count;
	public IEnumerable<CNPC_Trooper> Troopers => _members.Select(m => m.Npc);

	public Squad(CCitadelPlayerPawn owner) => Owner = owner;

	public void Add(CNPC_Trooper trooper) {
		trooper.SetMinion(true);
		trooper.SetSummoner(Owner);
		int slot = Enumerable.Range(0, Formation.Length).First(i => _members.All(m => m.Slot != i));
		_members.Add(new Member { Npc = trooper, Slot = slot });
	}

	public bool Contains(CBaseEntity npc) => _members.Any(m => m.Npc.EntityHandle == npc.EntityHandle);

	/// <summary>Call from OnNpcSelectSchedule.</summary>
	public void OnSelectSchedule(NpcSelectScheduleEvent e) {
		if (LaneSchedules.Contains(e.Schedule) && Contains(e.Npc))
			e.Override = "SCHED_IDLE_STAND";
	}

	/// <param name="origin">Where the owner stands on the navmesh, even while it is in the air.</param>
	public void Update(Vector3 origin) {
		if (!Owner.IsValid)
			return;
		float yaw = Owner.EyeAngles.Y * MathF.PI / 180f;
		var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
		var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);

		for (int i = _members.Count - 1; i >= 0; i--) {
			var m = _members[i];
			var t = m.Npc;
			// Dead, or reused by the game as a new spawn.
			if (!t.IsValid || t.IsDormant || t.Health <= 0 || !t.IsMinion) {
				_members.RemoveAt(i);
				continue;
			}
			var schedule = t.Schedule;
			if (schedule == null || Busy.Contains(schedule))
				continue;

			var pos = t.Position;
			float fromOwner = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(origin.X, origin.Y));
			bool fighting = t.Enemy != null && schedule != "SCHED_FORCED_GO" && schedule != "SCHED_IDLE_STAND";
			if (fighting && fromOwner < LeashRadius)
				continue;

			var offset = Formation[m.Slot];
			var spot = origin + forward * offset.X + right * offset.Y;
			spot = Nav.GetNearestPoint(spot, t, maxDistance: 300f) ?? origin;

			float toSpot = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(spot.X, spot.Y));
			if (toSpot <= SpotTolerance)
				continue;
			if (schedule == "SCHED_FORCED_GO" && m.LastOrder is { } last && Vector3.Distance(last, spot) < MathF.Max(Repath, 0.3f * toSpot))
				continue;

			t.MoveTo(spot, run: true);
			m.LastOrder = spot;
		}
	}

	/// <summary>Removes the squad's troopers.</summary>
	public void Disband() {
		foreach (var m in _members)
			if (m.Npc.IsValid)
				m.Npc.Remove();
		_members.Clear();
	}
}
