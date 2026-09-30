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

	/// <summary>
	/// The navmesh hull an NPC or hero paths with (<c>m_nNavHullIdx</c>), or 0 for other entities.
	/// </summary>
	public static int GetHull(CBaseEntity entity) =>
		entity.Is<CAI_BaseNPC>() || entity.Is<CCitadelPlayerPawn>() || entity.Is<CBasePlayerPawn>()
			? (int)_navHullIdx.Get(entity.Handle) : 0;
}
