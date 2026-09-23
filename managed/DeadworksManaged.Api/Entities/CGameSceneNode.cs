using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Scene graph node that holds the entity's world transform and absolute origin.</summary>
public unsafe class CGameSceneNode : NativeEntity {
	internal CGameSceneNode(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<Vector3> _vecAbsOrigin = new("CGameSceneNode"u8, "m_vecAbsOrigin"u8);
	public Vector3 AbsOrigin => _vecAbsOrigin.Get(Handle);

	private static readonly SchemaAccessor<Vector3> _angAbsRotation = new("CGameSceneNode"u8, "m_angAbsRotation"u8);
	/// <summary>World rotation of this node as (pitch, yaw, roll) in degrees.</summary>
	public Vector3 AbsRotation => _angAbsRotation.Get(Handle);

	private static readonly SchemaAccessor<float> _flScale = new("CGameSceneNode"u8, "m_flScale"u8);
	public float Scale { get => _flScale.Get(Handle); set => _flScale.Set(Handle, value); }
}
