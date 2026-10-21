using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>How a path reaches a waypoint.</summary>
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
/// Tracks progress along a <see cref="NavPath"/>. Call <see cref="Update"/> with the current position each tick
/// and steer towards the result, for a bot hero with <see cref="Bots.SetMoveInput"/>.
/// </summary>
/// <remarks>
/// A waypoint is reached within <see cref="ArriveRadius"/> on the ground plane, or once passed. The follower does
/// not re-path; find a new path when <see cref="DistanceFromPath"/> grows large.
/// </remarks>
public sealed class NavPathFollower {
	/// <param name="path">The path to follow.</param>
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

	/// <summary>Advances past reached waypoints and returns the next one, or null at the end.</summary>
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

	/// <summary>True when the next waypoint is close, more than a step up, and reached by jumping or climbing.</summary>
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

/// <summary>Queries on the navmesh, the walkable surface NPCs path over. No NPC is needed.</summary>
/// <remarks>
/// The navmesh is generated per hull, a body size: an area one hull crosses may not exist for another. Heroes use
/// hull 0, lane troopers hull 1. Out-of-range hulls are treated as hull 0. Without a navmesh every query returns
/// null.
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
	/// Returns the closest point on the navmesh to <paramref name="position"/>, or null if none is in range. NPCs
	/// snap their goals the same way.
	/// </summary>
	/// <param name="position">The point to snap.</param>
	/// <param name="maxDistance">How far to search. NPCs use 10000.</param>
	/// <param name="hull">The navmesh hull to search.</param>
	public static Vector3? GetNearestPoint(Vector3 position, float maxDistance = 10000f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float* result = stackalloc float[3];
		if (NativeInterop.NavGetNearestPoint(pos, maxDistance, hull, result) == 0)
			return null;
		return new Vector3(result[0], result[1], result[2]);
	}

	/// <summary><see cref="GetNearestPoint(Vector3, float, int)"/> on <paramref name="walker"/>'s hull.</summary>
	public static Vector3? GetNearestPoint(Vector3 position, CBaseEntity walker, float maxDistance = 10000f) =>
		GetNearestPoint(position, maxDistance, GetHull(walker));

	/// <summary>Returns the nav area nearest <paramref name="position"/>, or null if none is in range.</summary>
	public static NavArea? GetArea(Vector3 position, float maxDistance = 200f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		NavAreaInfo info;
		if (NativeInterop.NavGetArea(pos, maxDistance, hull, &info) == 0)
			return null;
		return new NavArea(info.Id, info.Center, info.Nearest, (NavAreaAttributes)info.Attributes,
			(NavAreaFlags)info.DynamicFlags, info.Hull, info.BlockedMask != 0);
	}

	/// <summary>
	/// Returns the navmesh height at <paramref name="position"/>'s x and y, on the nearest area, or null if none is
	/// in range.
	/// </summary>
	/// <remarks>The nearest area is measured in 3D: under a bridge, a point at bridge height gives the bridge.</remarks>
	public static float? GetGroundHeight(Vector3 position, float maxDistance = 1000f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float z;
		return NativeInterop.NavGetGroundHeight(pos, maxDistance, hull, &z) != 0 ? z : null;
	}

	/// <summary>
	/// Traces a straight line along the navmesh from <paramref name="start"/> to <paramref name="end"/>. Returns true
	/// if it stays on the walkable surface.
	/// </summary>
	/// <remarks>
	/// Steps or drops of more than 20 units stop it. Nav blockers do not. A start more than 200 units from the
	/// navmesh fails. Cheaper than <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/>.
	/// </remarks>
	/// <param name="start">Where the line starts. Moved onto the navmesh first.</param>
	/// <param name="end">Where it should reach.</param>
	/// <param name="stoppedAt"><paramref name="end"/> on the mesh, or the edge the line ran into.</param>
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
	/// Returns a random point reachable from <paramref name="origin"/>, between <paramref name="minDistance"/> and
	/// <paramref name="maxDistance"/> away, or null if there is none.
	/// </summary>
	/// <remarks>Cost grows with <paramref name="maxDistance"/>, which is capped at 6000.</remarks>
	public static Vector3? GetRandomPoint(Vector3 origin, float minDistance, float maxDistance, int hull = 0) {
		float* from = stackalloc float[3] { origin.X, origin.Y, origin.Z };
		float* result = stackalloc float[3];
		if (NativeInterop.NavGetRandomPoint(from, minDistance, maxDistance, hull, result) == 0)
			return null;
		return new Vector3(result[0], result[1], result[2]);
	}

	/// <summary>
	/// Finds a path from <paramref name="start"/> to <paramref name="goal"/> with the game's pathfinder. Returns null
	/// if there is none.
	/// </summary>
	/// <remarks>
	/// Both ends must be on the navmesh; snap them with <see cref="GetNearestPoint(Vector3, float, int)"/>. A path
	/// across half the map takes about 1 ms, a failed search of the whole map about 3 ms. The path uses the hull's
	/// default cost, so it ignores NPC-specific rules such as a trooper's lane.
	/// </remarks>
	/// <param name="start">Where the path starts.</param>
	/// <param name="goal">Where it should end.</param>
	/// <param name="hull">The navmesh hull to path for.</param>
	/// <param name="allowPartial">Accept a path that stops short of the goal. Compare <see cref="NavPath.End"/> with the goal.</param>
	/// <param name="simplify">Keep only the corners. Otherwise there is a waypoint per area crossed.</param>
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

	/// <summary><see cref="FindPath(Vector3, Vector3, int, bool, bool)"/> on <paramref name="walker"/>'s hull.</summary>
	public static NavPath? FindPath(Vector3 start, Vector3 goal, CBaseEntity walker, bool allowPartial = false, bool simplify = true) =>
		FindPath(start, goal, GetHull(walker), allowPartial, simplify);

	/// <summary>
	/// Blocks a box of the navmesh with a <c>script_nav_blocker</c>. NPCs and
	/// <see cref="FindPath(Vector3, Vector3, int, bool, bool)"/> route around it. Remove the returned entity to
	/// unblock. Returns null if it could not be created.
	/// </summary>
	/// <remarks>The navmesh updates a moment after the call.</remarks>
	/// <param name="center">The middle of the box.</param>
	/// <param name="halfExtents">Half the box's size along each axis, before <paramref name="angles"/>.</param>
	/// <param name="angles">Pitch, yaw, roll. Null is axis-aligned.</param>
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

	/// <summary>Returns the hull an NPC or hero paths with, or 0 for other entities.</summary>
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
