using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Static API for VPhys2 ray and shape casting. All methods are no-ops if the physics query system is not yet ready.</summary>
public static class Trace {
	private static unsafe bool IsReady =>
		NativeInterop.TraceShapeFn != 0 && NativeInterop.PhysicsQueryPtr != null &&
		*NativeInterop.PhysicsQueryPtr != null;

	/// <summary>Executes a raw VPhys2 trace using the provided ray shape and filter, writing results into <paramref name="trace"/>.</summary>
	public static unsafe void TraceShape(Vector3 start, Vector3 end, Ray_t ray, CTraceFilter filter, ref CGameTrace trace) {
		if (!IsReady) return;
		filter.EnsureValid();
		fixed (CGameTrace* tracePtr = &trace) {
			var fn = (delegate* unmanaged[Cdecl]<void*, Ray_t*, Vector3*, Vector3*, CTraceFilter*, CGameTrace*, void>)
				NativeInterop.TraceShapeFn;
			fn(*NativeInterop.PhysicsQueryPtr, &ray, &start, &end, &filter, tracePtr);
		}
	}

	/// <summary>Convenience trace that constructs a <see cref="CTraceFilter"/> and <see cref="Ray_t"/> from individual parameters, optionally ignoring up to two entities.</summary>
	public static unsafe void SimpleTrace(
		Vector3 start, Vector3 end,
		RayType_t rayKind, RnQueryObjectSet objectQuery,
		InteractionLayer interactWith, InteractionLayer interactExclude, InteractionLayer interactAs,
		CollisionGroup collision, ref CGameTrace trace,
		CBaseEntity? filterEntity = null, CBaseEntity? filterSecondEntity = null) {

		var filter = new CTraceFilter(true) {
			IterateEntities = true,
			QueryShapeAttributes = new RnQueryShapeAttr_t {
				ObjectSetMask = objectQuery,
				InteractsWith = interactWith,
				InteractsExclude = interactExclude,
				InteractsAs = interactAs,
				CollisionGroup = collision,
				HitSolid = true,
			}
		};

		var ray = new Ray_t { Type = rayKind };

		if (filterEntity != null)
			filter.QueryShapeAttributes.EntityIdsToIgnore[0] = (uint)filterEntity.EntityIndex;
		if (filterSecondEntity != null)
			filter.QueryShapeAttributes.EntityIdsToIgnore[1] = (uint)filterSecondEntity.EntityIndex;

		TraceShape(start, end, ray, filter, ref trace);
	}

	/// <summary>Like <see cref="SimpleTrace"/> but takes pitch/yaw angles instead of an end point, projecting <paramref name="maxDistance"/> units forward.</summary>
	public static void SimpleTraceAngles(
		Vector3 start, Vector3 angles,
		RayType_t rayKind, RnQueryObjectSet objectQuery,
		InteractionLayer interactWith, InteractionLayer interactExclude, InteractionLayer interactAs,
		CollisionGroup collision, ref CGameTrace trace,
		CBaseEntity? filterEntity = null, CBaseEntity? filterSecondEntity = null,
		float maxDistance = 8192f) {

		float pitch = angles.X * MathF.PI / 180f;
		float yaw = angles.Y * MathF.PI / 180f;
		var forward = new Vector3(
			MathF.Cos(pitch) * MathF.Cos(yaw),
			MathF.Cos(pitch) * MathF.Sin(yaw),
			-MathF.Sin(pitch));
		var end = start + forward * maxDistance;
		SimpleTrace(start, end, rayKind, objectQuery, interactWith, interactExclude, interactAs, collision, ref trace, filterEntity, filterSecondEntity);
	}

	/// <summary>Fires a simple line ray from <paramref name="start"/> to <paramref name="end"/>. Returns a <see cref="TraceResult"/> with hit position and fraction.</summary>
	public static unsafe TraceResult Ray(Vector3 start, Vector3 end, InteractionLayer mask = InteractionLayer.Solid | InteractionLayer.Hitbox, CBaseEntity? ignore = null) {
		if (!IsReady)
			return new TraceResult { HitPosition = end, Fraction = 1.0f, DidHit = false };

		var trace = CGameTrace.Create();
		SimpleTrace(start, end, RayType_t.Line, RnQueryObjectSet.All, mask, InteractionLayer.None, InteractionLayer.None,
			CollisionGroup.Always, ref trace, ignore);

		return new TraceResult {
			HitPosition = start + (end - start) * trace.Fraction,
			Fraction = trace.Fraction,
			DidHit = trace.DidHit,
			Trace = trace,
		};
	}
}
