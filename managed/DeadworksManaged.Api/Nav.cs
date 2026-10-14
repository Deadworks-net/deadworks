using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>How a path gets to a waypoint, so something steering along it knows when walking isn't enough.</summary>
public enum NavTraverse {
	/// <summary>Walk.</summary>
	Ground,
	/// <summary>Jump across a gap, or up or down a ledge.</summary>
	Jump,
	/// <summary>Climb up, as the game's NPCs mantle.</summary>
	Climb,
	Ladder,
	/// <summary>A nav link: a hand-placed connection such as a jump pad or a drop.</summary>
	Link,
}

/// <summary>One point of a <see cref="NavPath"/>.</summary>
/// <param name="Position">Where to go.</param>
/// <param name="Traverse">How the path reaches this point from the one before.</param>
public readonly record struct NavWaypoint(Vector3 Position, NavTraverse Traverse);

/// <summary>A walkable route across the navmesh, from <see cref="Nav.FindPath(Vector3, Vector3, int, bool, bool)"/>.</summary>
public sealed class NavPath {
	internal NavPath(NavWaypoint[] waypoints) {
		Waypoints = waypoints;
		for (int i = 1; i < waypoints.Length; i++)
			Length += Vector3.Distance(waypoints[i - 1].Position, waypoints[i].Position);
	}

	/// <summary>The points to pass through, in order. The first is the start.</summary>
	public IReadOnlyList<NavWaypoint> Waypoints { get; }

	/// <summary>Walking distance along the path, in units.</summary>
	public float Length { get; }

	/// <summary>Where the path ends: the goal, or as close as it got for a partial path.</summary>
	public Vector3 End => Waypoints[^1].Position;
}

/// <summary>
/// Walks something along a <see cref="NavPath"/>: call <see cref="Update"/> with the current position every
/// tick and steer towards what it returns. For a bot hero, pass that to <see cref="Bots.SetMoveInput"/>.
/// </summary>
/// <remarks>
/// A waypoint counts as reached once the walker is within <see cref="ArriveRadius"/> of it on the ground plane,
/// or has gone past it towards the next one. The follower doesn't re-path: when the walker is pushed far off the
/// route (<see cref="DistanceFromPath"/>), find a new path.
/// </remarks>
public sealed class NavPathFollower {
	/// <param name="path">The path to follow, usually a simplified one from <see cref="Nav.FindPath(Vector3, Vector3, int, bool, bool)"/>.</param>
	/// <param name="arriveRadius">How close counts as reaching a waypoint, in units.</param>
	public NavPathFollower(NavPath path, float arriveRadius = 40f) {
		Path = path;
		ArriveRadius = arriveRadius;
		Index = path.Waypoints.Count > 1 ? 1 : path.Waypoints.Count;
	}

	/// <summary>The path being followed.</summary>
	public NavPath Path { get; }

	/// <summary>How close counts as reaching a waypoint, in units.</summary>
	public float ArriveRadius { get; set; }

	/// <summary>The index in <see cref="NavPath.Waypoints"/> of the waypoint being headed for.</summary>
	public int Index { get; private set; }

	/// <summary>Whether the last waypoint has been reached.</summary>
	public bool IsFinished => Index >= Path.Waypoints.Count;

	/// <summary>The waypoint being headed for, or null once finished.</summary>
	public NavWaypoint? Target => IsFinished ? null : Path.Waypoints[Index];

	/// <summary>
	/// Moves on past every waypoint <paramref name="position"/> has reached and returns where to head next,
	/// or null once the end is reached.
	/// </summary>
	public Vector3? Update(Vector3 position) {
		var waypoints = Path.Waypoints;
		while (Index < waypoints.Count) {
			var target = waypoints[Index].Position;
			if (Flat(target - position).Length() <= ArriveRadius) {
				Index++;
				continue;
			}
			// Gone past it: closer to the next waypoint than it is.
			if (Index + 1 < waypoints.Count && waypoints[Index + 1].Traverse == NavTraverse.Ground) {
				var leg = Flat(waypoints[Index + 1].Position - target);
				if (leg.LengthSquared() > 0f && Vector2.Dot(Flat(position - target), leg) > 0f) {
					Index++;
					continue;
				}
			}
			return target;
		}
		return null;
	}

	/// <summary>
	/// Whether to jump now: the waypoint ahead is reached by jumping or climbing, is more than a step above
	/// <paramref name="position"/>, and is close.
	/// </summary>
	public bool ShouldJump(Vector3 position, float within = 160f) {
		if (Target is not { } target || target.Traverse is not (NavTraverse.Jump or NavTraverse.Climb))
			return false;
		return target.Position.Z - position.Z > 18f && Flat(target.Position - position).Length() <= within;
	}

	/// <summary>How far <paramref name="position"/> is from the leg being walked, on the ground plane.</summary>
	public float DistanceFromPath(Vector3 position) {
		if (IsFinished || Index == 0)
			return Flat(Path.End - position).Length();
		var a = Flat(Path.Waypoints[Index - 1].Position);
		var b = Flat(Path.Waypoints[Index].Position);
		var p = Flat(position);
		var ab = b - a;
		float t = ab.LengthSquared() > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
		return Vector2.Distance(p, a + ab * t);
	}

	private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
}

/// <summary>What a nav area allows or asks of whatever walks it (<c>NavAttributeEnum</c>).</summary>
[Flags]
public enum NavAreaAttributes : ulong {
	None = 0,
	Jump = 0x2,
	NoJump = 0x8,
	Stop = 0x10,
	Run = 0x20,
	Walk = 0x40,
	Avoid = 0x80,
	Transient = 0x100,
	DontHide = 0x200,
	Stand = 0x400,
	Stairs = 0x1000,
	NoMerge = 0x2000,
	ObstacleTop = 0x4000,
	NonZUp = 0x8000,
	/// <summary>Only crouch height of clearance.</summary>
	CrouchHeight = 0x10000,
	NonZUpTransition = 0x20000,
	/// <summary>Only crawl height of clearance.</summary>
	CrawlHeight = 0x40000,
}

/// <summary>State the game keeps on a nav area at runtime (<c>NavDynamicAttributeEnum</c>).</summary>
[Flags]
public enum NavAreaFlags : uint {
	None = 0,
	UnderWater = 0x1,
	UnderWaterDeep = 0x2,
	/// <summary>An obstacle, such as a nav blocker, has cut this area up.</summary>
	SplitByObstacle = 0x20,
	HasLadders = 0x100,
	NavLink = 0x200,
	NavLinkTerminus = 0x400,
	Movable = 0x2000,
	Boundary = 0x10000,
}

/// <summary>A polygon of the navmesh, from <see cref="Nav.GetArea"/>.</summary>
/// <param name="Id">The area's id. Ids change when the map's navmesh is rebuilt, and split areas get new ones.</param>
/// <param name="Center">The area's centre.</param>
/// <param name="NearestPoint">The queried position moved onto the area.</param>
/// <param name="Attributes">What the area allows or asks for.</param>
/// <param name="Flags">Runtime state.</param>
/// <param name="Hull">The hull this area belongs to.</param>
/// <param name="IsBlocked">True while something blocks the area for at least one team.</param>
public readonly record struct NavArea(int Id, Vector3 Center, Vector3 NearestPoint, NavAreaAttributes Attributes,
	NavAreaFlags Flags, int Hull, bool IsBlocked);

/// <summary>
/// Queries on the map's navigation mesh, the walkable surface NPCs path over. Everything here runs the game's
/// own navmesh code and needs no NPC.
/// </summary>
/// <remarks>
/// The navmesh is generated per <i>hull</i>, a size class: an area a small hull can cross may not exist for
/// a larger one. Heroes and bots use hull 0 and lane troopers hull 1; <see cref="GetHull"/> reads it from an
/// entity. Out-of-range hulls are treated as hull 0. A map without a navmesh answers every query with null.
/// </remarks>
public static unsafe class Nav {
	private static readonly SchemaAccessor<uint> _navHullIdx = new("CBaseCombatCharacter"u8, "m_nNavHullIdx"u8);

	/// <summary>The most waypoints a path can have.</summary>
	public const int MaxPathPoints = 256;

	[StructLayout(LayoutKind.Sequential)]
	private struct NavAreaInfo {
		public Vector3 Center;
		public int Id;
		public ulong Attributes;
		public uint DynamicFlags;
		public byte Hull;
		public byte BlockedMask;
		private readonly ushort _pad;
		public Vector3 Nearest;
		private readonly uint _pad2;
	}

	/// <summary>
	/// Finds the closest point on the navmesh to <paramref name="position"/>, the same way NPCs snap a goal onto
	/// the navmesh before they path to it. Returns null when nothing is in range.
	/// </summary>
	/// <remarks>
	/// Use it before <see cref="CAI_BaseNPC.MoveTo"/> or <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/>
	/// when a point might not be walkable, for example one offset from a player, which can land inside a wall
	/// or off a ledge. A query costs a few microseconds.
	/// </remarks>
	/// <param name="position">The point to snap.</param>
	/// <param name="maxDistance">How far to search, in units. 10000 is what the game's NPCs use.</param>
	/// <param name="hull">The navmesh hull to search.</param>
	public static Vector3? GetNearestPoint(Vector3 position, float maxDistance = 10000f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float* result = stackalloc float[3];
		if (NativeInterop.NavGetNearestPoint(pos, maxDistance, hull, result) == 0)
			return null;
		return new Vector3(result[0], result[1], result[2]);
	}

	/// <summary>
	/// <see cref="GetNearestPoint(Vector3, float, int)"/> on the hull <paramref name="walker"/> paths with, so the
	/// point is one that NPC or hero fits through.
	/// </summary>
	public static Vector3? GetNearestPoint(Vector3 position, CBaseEntity walker, float maxDistance = 10000f) =>
		GetNearestPoint(position, maxDistance, GetHull(walker));

	/// <summary>
	/// The nav area nearest to <paramref name="position"/>: what it allows (jumping, crouch height...), whether
	/// it is blocked, and where it is. Returns null when nothing is in range.
	/// </summary>
	public static NavArea? GetArea(Vector3 position, float maxDistance = 200f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		NavAreaInfo info;
		if (NativeInterop.NavGetArea(pos, maxDistance, hull, &info) == 0)
			return null;
		return new NavArea(info.Id, info.Center, info.Nearest, (NavAreaAttributes)info.Attributes,
			(NavAreaFlags)info.DynamicFlags, info.Hull, info.BlockedMask != 0);
	}

	/// <summary>
	/// The height of the navmesh at <paramref name="position"/>'s x and y, on the nav area nearest to it.
	/// Returns null when no area is in range.
	/// </summary>
	/// <remarks>
	/// "Nearest" is measured in 3D, so pass a position close to the floor you mean: under a bridge, a point at
	/// bridge height gives the bridge.
	/// </remarks>
	public static float? GetGroundHeight(Vector3 position, float maxDistance = 1000f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float z;
		return NativeInterop.NavGetGroundHeight(pos, maxDistance, hull, &z) != 0 ? z : null;
	}

	/// <summary>
	/// Walks a straight line across the navmesh from <paramref name="start"/> towards <paramref name="end"/>.
	/// Returns true if it gets there without leaving the walkable surface, so something of that hull can
	/// walk the line directly.
	/// </summary>
	/// <remarks>
	/// It follows the mesh, not the air above it, and steps or drops of more than 20 units stop it; it does
	/// not see nav blockers. A start more than 200 units from the navmesh fails. Cheaper than
	/// <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/>, so it is the way to check whether a path is
	/// needed at all.
	/// </remarks>
	/// <param name="start">Where the line starts. Moved onto the navmesh first.</param>
	/// <param name="end">Where it should reach.</param>
	/// <param name="stoppedAt">Where the line stopped: <paramref name="end"/> on the mesh, or the edge it ran into.</param>
	/// <param name="hull">The navmesh hull to test.</param>
	public static bool Raycast(Vector3 start, Vector3 end, out Vector3 stoppedAt, int hull = 0) {
		float* from = stackalloc float[3] { start.X, start.Y, start.Z };
		float* to = stackalloc float[3] { end.X, end.Y, end.Z };
		float* stopped = stackalloc float[3];
		float* normal = stackalloc float[3];
		bool reached = NativeInterop.NavRaycast(from, to, hull, stopped, normal) != 0;
		stoppedAt = new Vector3(stopped[0], stopped[1], stopped[2]);
		return reached;
	}

	/// <summary>
	/// Picks a random point that can be walked to from <paramref name="origin"/>, between
	/// <paramref name="minDistance"/> and <paramref name="maxDistance"/> units away. Returns null when there is
	/// none. For wandering, patrols and spawn points.
	/// </summary>
	/// <remarks>
	/// The search floods the navmesh out to <paramref name="maxDistance"/>, so its cost grows with it; values
	/// above 6000 are treated as 6000.
	/// </remarks>
	public static Vector3? GetRandomPoint(Vector3 origin, float minDistance, float maxDistance, int hull = 0) {
		float* from = stackalloc float[3] { origin.X, origin.Y, origin.Z };
		float* result = stackalloc float[3];
		if (NativeInterop.NavGetRandomPoint(from, minDistance, maxDistance, hull, result) == 0)
			return null;
		return new Vector3(result[0], result[1], result[2]);
	}

	/// <summary>
	/// Finds a walkable path from <paramref name="start"/> to <paramref name="goal"/> with the game's own
	/// navmesh pathfinder. Returns null when there is no path.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Both ends must be on the navmesh: a start in the air, for example, finds no path. Snap a point that
	/// might be off it with <see cref="GetNearestPoint(Vector3, float, int)"/> first. Hulls differ in where they
	/// can go: a point the trooper hull can't reach may be reachable on hull 0.
	/// </para>
	/// <para>
	/// A query takes about a millisecond for a path across half the map and about three when it searches the
	/// whole map and fails, so don't run one per bot every tick; path once and re-path when the goal moves.
	/// A path has at most <see cref="MaxPathPoints"/> waypoints.
	/// </para>
	/// <para>
	/// The path uses the default path cost for the hull, so it ignores things only a particular NPC cares
	/// about, such as a trooper's lane. Use it to steer something yourself or to measure walking distance; to
	/// move an NPC or a bot, <see cref="CAI_BaseNPC.MoveTo"/> already paths for it.
	/// </para>
	/// </remarks>
	/// <param name="start">Where the path starts.</param>
	/// <param name="goal">Where it should end.</param>
	/// <param name="hull">The navmesh hull to path for.</param>
	/// <param name="allowPartial">
	/// Also accept a path that stops short of the goal, as far as the pathfinder got towards it. Compare
	/// <see cref="NavPath.End"/> with the goal to tell.
	/// </param>
	/// <param name="simplify">
	/// Run the game's path optimizer, which keeps only the corners: each waypoint can be walked to in a
	/// straight line from the one before. Without it there is a waypoint for every nav area the path crosses.
	/// </param>
	public static NavPath? FindPath(Vector3 start, Vector3 goal, int hull = 0, bool allowPartial = false, bool simplify = true) {
		float* from = stackalloc float[3] { start.X, start.Y, start.Z };
		float* to = stackalloc float[3] { goal.X, goal.Y, goal.Z };
		float* points = stackalloc float[MaxPathPoints * 3];
		int* types = stackalloc int[MaxPathPoints];
		uint flags = (allowPartial ? 1u : 0u) | (simplify ? 2u : 0u);
		int count = Math.Min(NativeInterop.NavFindPath(from, to, hull, flags, points, types, MaxPathPoints), MaxPathPoints);
		if (count <= 0)
			return null;

		// The pathfinder repeats a waypoint where two areas meet at the same point.
		var waypoints = new List<NavWaypoint>(count);
		for (int i = 0; i < count; i++) {
			var position = new Vector3(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
			if (waypoints.Count > 0 && Vector3.DistanceSquared(waypoints[^1].Position, position) <= 1f)
				continue;
			waypoints.Add(new NavWaypoint(position, ToTraverse(types[i])));
		}
		return new NavPath(waypoints.ToArray());
	}

	/// <summary>
	/// <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/> on the hull <paramref name="walker"/> paths
	/// with, so the path is one that NPC or hero fits through.
	/// </summary>
	public static NavPath? FindPath(Vector3 start, Vector3 goal, CBaseEntity walker, bool allowPartial = false, bool simplify = true) =>
		FindPath(start, goal, GetHull(walker), allowPartial, simplify);

	/// <summary>
	/// Blocks a box of the navmesh by spawning the game's own <c>script_nav_blocker</c> entity: NPCs path around
	/// it and <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/> routes around it. Remove the returned
	/// entity to open the area again. Returns null if the entity could not be created.
	/// </summary>
	/// <remarks>
	/// The navmesh updates a moment after the spawn, not within the same call.
	/// </remarks>
	/// <param name="center">The middle of the box.</param>
	/// <param name="halfExtents">Half the box's size along each axis, before <paramref name="angles"/>.</param>
	/// <param name="angles">The box's rotation (pitch, yaw, roll). Null leaves it axis-aligned.</param>
	/// <param name="team">The only team to block, or 0 to block everyone.</param>
	public static CBaseEntity? CreateBlocker(Vector3 center, Vector3 halfExtents, Vector3? angles = null, int team = 0) {
		var blocker = CBaseEntity.CreateByDesignerName("script_nav_blocker");
		if (blocker == null)
			return null;
		var kv = new CEntityKeyValues();
		kv.SetVector("origin", center);
		if (angles is { } rotation)
			kv.SetVector("angles", rotation);
		kv.SetString("extent", FormattableString.Invariant($"{halfExtents.X} {halfExtents.Y} {halfExtents.Z}"));
		if (team != 0)
			kv.SetInt("teamToBlock", team);
		blocker.Spawn(kv);
		return blocker;
	}

	/// <summary>
	/// The navmesh hull an NPC or hero paths with (<c>m_nNavHullIdx</c>), or 0 for other entities.
	/// </summary>
	public static int GetHull(CBaseEntity entity) =>
		entity.Is<CAI_BaseNPC>() || entity.Is<CCitadelPlayerPawn>() || entity.Is<CBasePlayerPawn>()
			? (int)_navHullIdx.Get(entity.Handle) : 0;

	// The path's segment types, as the game's own path drawing groups them.
	private static NavTraverse ToTraverse(int segmentType) => segmentType switch {
		1 or 2 or 3 => NavTraverse.Jump,
		4 => NavTraverse.Climb,
		5 or 6 => NavTraverse.Ladder,
		7 or 8 => NavTraverse.Link,
		_ => NavTraverse.Ground,
	};
}
