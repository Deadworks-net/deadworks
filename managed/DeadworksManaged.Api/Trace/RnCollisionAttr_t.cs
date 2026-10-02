using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Collision attributes of a physics object. Read an entity's with <see cref="CCollisionProperty.Attribute"/> and a
/// traced shape's with <see cref="CGameTrace.ShapeAttributes"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RnCollisionAttr_t {
	/// <summary>Layers the object belongs to.</summary>
	public InteractionLayer InteractsAs;
	/// <summary>Layers the object collides with.</summary>
	public InteractionLayer InteractsWith;
	/// <summary>Layers that never collide with the object.</summary>
	public InteractionLayer InteractsExclude;
	/// <summary>Handle of the entity the object belongs to.</summary>
	public uint EntityId;
	/// <summary>Handle of the entity's owner, or <c>0xFFFFFFFF</c>. An object never collides with its owner.</summary>
	public uint OwnerId;
	/// <summary>Shared by entities attached to each other, which never collide with each other. 0 for none.</summary>
	public ushort HierarchyId;
	/// <summary>Detail layers the object is on.</summary>
	public ushort DetailLayerMask;
	/// <summary>How <see cref="DetailLayerMask"/> is matched.</summary>
	public byte DetailLayerMaskType;
	/// <summary>Detail layer the object targets.</summary>
	public byte TargetDetailLayer;
	/// <summary>The object's collision group.</summary>
	public CollisionGroup CollisionGroup;
	/// <summary>Which collision functions are on.</summary>
	public CollisionFunctionMask_t CollisionFunctionMask;
}
