using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Result of a VPhys2 shape trace. Check <see cref="DidHit"/> and inspect <see cref="HitPoint"/>, <see cref="HitNormal"/>, and <see cref="HitEntity"/>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public unsafe struct CGameTrace {
	[FieldOffset(0x00)] public nint SurfaceProperties;
	[FieldOffset(0x08)] public nint pEntity;
	[FieldOffset(0x10)] public nint HitBox;
	[FieldOffset(0x18)] public nint Body;
	[FieldOffset(0x20)] public nint Shape;
	[FieldOffset(0x28)] public ulong Contents;
	[FieldOffset(0x30)] public fixed byte BodyTransform[32];
	/// <summary>Collision attributes of the shape the trace hit. <see cref="RnCollisionAttr_t.EntityId"/> is the hit entity's handle.</summary>
	[FieldOffset(0x50)] public RnCollisionAttr_t ShapeAttributes;
	[FieldOffset(0x78)] public Vector3 StartPos;
	[FieldOffset(0x84)] public Vector3 EndPos;
	[FieldOffset(0x90)] public Vector3 HitNormal;
	[FieldOffset(0x9C)] public Vector3 HitPoint;
	[FieldOffset(0xA8)] public float HitOffset;
	[FieldOffset(0xAC)] public float Fraction;
	[FieldOffset(0xB0)] public int Triangle;
	[FieldOffset(0xB4)] public short HitboxBoneIndex;
	[FieldOffset(0xB6)] public RayType_t RayType;
	[FieldOffset(0xB7)] public bool StartInSolid;
	[FieldOffset(0xB8)] public bool ExactHitPoint;

	public readonly bool DidHit => Fraction < 1.0f || StartInSolid;

	public readonly float Distance => Vector3.Distance(EndPos, StartPos);

	public readonly Vector3 Direction {
		get {
			var dir = EndPos - StartPos;
			return dir == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(dir);
		}
	}

	public readonly CBaseEntity? HitEntity =>
		pEntity != 0 ? new CBaseEntity(pEntity) : null;

	public readonly bool HitEntityByDesignerName(string designerName, out CBaseEntity? outEntity, NameMatchType matchType = NameMatchType.StartsWith) {
		outEntity = null;
		if (!DidHit || pEntity == 0) return false;
		var ent = new CBaseEntity(pEntity);
		var name = ent.DesignerName;
		if (name == null) return false;
		var isMatch = matchType switch {
			NameMatchType.Exact => name.Equals(designerName, StringComparison.OrdinalIgnoreCase),
			NameMatchType.StartsWith => name.StartsWith(designerName, StringComparison.OrdinalIgnoreCase),
			NameMatchType.EndsWith => name.EndsWith(designerName, StringComparison.OrdinalIgnoreCase),
			NameMatchType.Contains => name.Contains(designerName, StringComparison.OrdinalIgnoreCase),
			_ => false,
		};
		if (isMatch) outEntity = ent;
		return isMatch;
	}

	public readonly bool HitEntityByDesignerName(string designerName, NameMatchType matchType = NameMatchType.StartsWith) =>
		HitEntityByDesignerName(designerName, out _, matchType);

	public static CGameTrace Create() {
		var trace = new CGameTrace();
		// Identity quaternion (w=1.0) at bodyTransform+0x0C
		*(float*)(trace.BodyTransform + 0x0C) = 1.0f;
		trace.Fraction = 1.0f;
		trace.Triangle = -1;
		trace.HitboxBoneIndex = -1;
		return trace;
	}
}
