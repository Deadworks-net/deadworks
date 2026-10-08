using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Queries on the map's navigation mesh, the walkable surface NPCs path over.
/// </summary>
public static unsafe class Nav {
	private static readonly SchemaAccessor<uint> _navHullIdx = new("CBaseCombatCharacter"u8, "m_nNavHullIdx"u8);

	/// <summary>
	/// Finds the closest point on the navmesh to <paramref name="position"/>, the same way NPCs snap a goal onto
	/// the navmesh before they path to it. Returns null when nothing is in range, or the map has no navmesh.
	/// </summary>
	/// <remarks>
	/// Use it before <see cref="CAI_BaseNPC.MoveTo"/> when the goal might not be walkable, for example a point
	/// offset from a player, which can land inside a wall or off a ledge. A goal off the navmesh makes the move
	/// fail (the NPC's schedule becomes <c>SCHED_FAIL</c>). A query costs a few microseconds, so snapping a goal
	/// for every order is fine.
	/// </remarks>
	/// <param name="position">The point to snap.</param>
	/// <param name="maxDistance">How far to search, in units. 10000 is what the game's NPCs use.</param>
	/// <param name="hull">The navmesh hull (NPC size class) to search. Out-of-range values use hull 0.</param>
	public static Vector3? GetNearestPoint(Vector3 position, float maxDistance = 10000f, int hull = 0) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float* result = stackalloc float[3];
		if (NativeInterop.NavGetNearestPoint(pos, maxDistance, hull, result) == 0)
			return null;
		return new Vector3(result[0], result[1], result[2]);
	}

	/// <summary>
	/// <see cref="GetNearestPoint(Vector3, float, int)"/> on the hull <paramref name="walker"/> paths with, so the
	/// point is one that NPC fits through. Works for NPCs and hero pawns.
	/// </summary>
	public static Vector3? GetNearestPoint(Vector3 position, CBaseEntity walker, float maxDistance = 10000f) =>
		GetNearestPoint(position, maxDistance, GetHull(walker));

	/// <summary>The most waypoints a path can have.</summary>
	public const int MaxPathPoints = 256;

	/// <summary>
	/// Finds a walkable path from <paramref name="start"/> to <paramref name="goal"/> with the game's own
	/// navmesh pathfinder, without needing an NPC. Returns the waypoints in order, or null when there is no path.
	/// </summary>
	/// <remarks>
	/// The first waypoint is the start and the last is the goal; walk them in order. There is one per navmesh
	/// area the path crosses, so they are close together (usually under 100 units apart), and a path has at most
	/// <see cref="MaxPathPoints"/> of them.
	/// <para>
	/// Both ends must be on the navmesh: a start in the air, for example, finds no path. Snap a point that
	/// might be off it with <see cref="GetNearestPoint(Vector3, float, int)"/> first. Hulls differ in where they
	/// can go: a point the trooper hull can't reach may be reachable on hull 0.
	/// </para>
	/// <para>
	/// A query takes about a millisecond for a path across half the map and about three when it searches the
	/// whole map and fails, so don't run one per bot every tick; path once and re-path when the goal moves.
	/// </para>
	/// <para>
	/// The path uses the default path cost for the hull, so it ignores things only a particular NPC cares
	/// about, such as a trooper's lane. Use it to steer something yourself, for example a bot driven with
	/// movement input, or to measure walking distance; to move an NPC, <see cref="CAI_BaseNPC.MoveTo"/> already
	/// paths for it.
	/// </para>
	/// </remarks>
	/// <param name="start">Where the path starts.</param>
	/// <param name="goal">Where it should end.</param>
	/// <param name="hull">The navmesh hull (NPC size class) to path for. Out-of-range values use hull 0.</param>
	/// <param name="allowPartial">
	/// Also accept a path that stops short of the goal, as far as the pathfinder got towards it. Compare the
	/// last waypoint with the goal to tell.
	/// </param>
	public static Vector3[]? FindPath(Vector3 start, Vector3 goal, int hull = 0, bool allowPartial = false) {
		float* from = stackalloc float[3] { start.X, start.Y, start.Z };
		float* to = stackalloc float[3] { goal.X, goal.Y, goal.Z };
		float* points = stackalloc float[MaxPathPoints * 3];
		int count = NativeInterop.NavFindPath(from, to, hull, allowPartial ? (byte)1 : (byte)0, points, MaxPathPoints);
		if (count <= 0)
			return null;
		// The pathfinder emits a waypoint per navmesh area crossing, and some repeat the one before.
		var path = new List<Vector3>(Math.Min(count, MaxPathPoints));
		for (int i = 0; i < count && i < MaxPathPoints; i++) {
			var point = new Vector3(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
			if (path.Count == 0 || Vector3.DistanceSquared(path[^1], point) > 1f)
				path.Add(point);
		}
		return path.ToArray();
	}

	/// <summary>
	/// <see cref="FindPath(Vector3, Vector3, int, bool)"/> on the hull <paramref name="walker"/> paths with, so
	/// the path is one that NPC or hero fits through.
	/// </summary>
	public static Vector3[]? FindPath(Vector3 start, Vector3 goal, CBaseEntity walker, bool allowPartial = false) =>
		FindPath(start, goal, GetHull(walker), allowPartial);

	/// <summary>
	/// The navmesh hull an NPC or hero paths with (<c>m_nNavHullIdx</c>), or 0 for other entities.
	/// </summary>
	public static int GetHull(CBaseEntity entity) =>
		entity.Is<CAI_BaseNPC>() || entity.Is<CCitadelPlayerPawn>() || entity.Is<CBasePlayerPawn>()
			? (int)_navHullIdx.Get(entity.Handle) : 0;
}
