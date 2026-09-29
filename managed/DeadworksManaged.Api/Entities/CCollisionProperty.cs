using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// An entity's bounds and collision settings: what it collides with, what traces hit it, and its physics shape.
/// Get it with <see cref="CBaseEntity.Collision"/>.
/// </summary>
/// <remarks>
/// Two things collide when either one's <see cref="InteractsWith"/> overlaps the other's <see cref="InteractsAs"/>,
/// unless either one's <see cref="InteractsExclude"/> overlaps the other's <see cref="InteractsAs"/>.
/// </remarks>
public unsafe class CCollisionProperty : NativeEntity {
	private readonly CBaseEntity _owner;

	private static ReadOnlySpan<byte> Class => "CCollisionProperty"u8;
	private static readonly SchemaAccessor<Vector3> _vecMins = new(Class, "m_vecMins"u8);
	private static readonly SchemaAccessor<Vector3> _vecMaxs = new(Class, "m_vecMaxs"u8);
	private static readonly SchemaAccessor<float> _flBoundingRadius = new(Class, "m_flBoundingRadius"u8);
	private static readonly SchemaAccessor<byte> _collisionAttribute = new(Class, "m_collisionAttribute"u8);
	private static readonly SchemaAccessor<RnCollisionAttr_t> _attribute = new("VPhysicsCollisionAttribute_t"u8, "m_nInteractsAs"u8);
	private static readonly SchemaAccessor<byte> _collisionGroup = new(Class, "m_CollisionGroup"u8);
	private static readonly SchemaAccessor<byte> _solidType = new(Class, "m_nSolidType"u8);
	private static readonly SchemaAccessor<byte> _solidFlags = new(Class, "m_usSolidFlags"u8);

	private const byte InteractsAsMask = 0;
	private const byte InteractsWithMask = 1;
	private const byte InteractsExcludeMask = 2;

	internal CCollisionProperty(nint handle, CBaseEntity owner) : base(handle) {
		_owner = owner;
	}

	public override bool IsValid => Handle != 0 && _owner.IsValid;

	/// <summary>The owning entity.</summary>
	public CBaseEntity Owner => _owner;

	/// <summary>Lower OBB corner in local space.</summary>
	public Vector3 Mins { get => _vecMins.Get(Handle); set => _vecMins.Set(Handle, value); }

	/// <summary>Upper OBB corner in local space.</summary>
	public Vector3 Maxs { get => _vecMaxs.Get(Handle); set => _vecMaxs.Set(Handle, value); }

	public float BoundingRadius => _flBoundingRadius.Get(Handle);

	/// <summary>Mins in world space (local Mins + owner's AbsOrigin). Assumes identity rotation.</summary>
	public Vector3 WorldMins => Mins + _owner.Position;

	/// <summary>Maxs in world space (local Maxs + owner's AbsOrigin). Assumes identity rotation.</summary>
	public Vector3 WorldMaxs => Maxs + _owner.Position;

	/// <summary>Returns true if <paramref name="point"/> lies within the entity's world-space AABB (inclusive).</summary>
	public bool Contains(Vector3 point) => BoundingBox.Contains(WorldMins, WorldMaxs, point);

	/// <summary>All of the entity's collision attributes in one read.</summary>
	public RnCollisionAttr_t Attribute => _attribute.Get(_collisionAttribute.GetAddress(Handle));

	/// <summary>Layers the entity belongs to, such as <see cref="MaskTrace.Solid"/>.</summary>
	public MaskTrace InteractsAs => Attribute.InteractsAs;

	/// <summary>Layers the entity collides with.</summary>
	public MaskTrace InteractsWith => Attribute.InteractsWith;

	/// <summary>Layers that never collide with the entity. Wins over <see cref="InteractsAs"/>, <see cref="InteractsWith"/> and the collision group.</summary>
	public MaskTrace InteractsExclude => Attribute.InteractsExclude;

	/// <summary>Adds <paramref name="layers"/> to <see cref="InteractsAs"/>.</summary>
	/// <inheritdoc cref="AddInteractsExclude" path="/remarks"/>
	public void AddInteractsAs(MaskTrace layers) => NativeInterop.AddCollisionLayers((void*)Handle, InteractsAsMask, (ulong)layers);

	/// <summary>Removes <paramref name="layers"/> from <see cref="InteractsAs"/>.</summary>
	/// <inheritdoc cref="AddInteractsExclude" path="/remarks"/>
	public void RemoveInteractsAs(MaskTrace layers) => NativeInterop.RemoveCollisionLayers((void*)Handle, InteractsAsMask, (ulong)layers);

	/// <summary>Adds <paramref name="layers"/> to <see cref="InteractsWith"/>.</summary>
	/// <inheritdoc cref="AddInteractsExclude" path="/remarks"/>
	public void AddInteractsWith(MaskTrace layers) => NativeInterop.AddCollisionLayers((void*)Handle, InteractsWithMask, (ulong)layers);

	/// <summary>Removes <paramref name="layers"/> from <see cref="InteractsWith"/>.</summary>
	/// <inheritdoc cref="AddInteractsExclude" path="/remarks"/>
	public void RemoveInteractsWith(MaskTrace layers) => NativeInterop.RemoveCollisionLayers((void*)Handle, InteractsWithMask, (ulong)layers);

	/// <summary>Adds <paramref name="layers"/> to <see cref="InteractsExclude"/>, so nothing on those layers collides with the entity.</summary>
	/// <remarks>
	/// Takes effect immediately and survives <see cref="CBaseEntity.SetModel"/>. Call it after the entity spawns.
	/// On heroes and other units, the game resets the team, bullet and ability layers whenever it sets their team.
	/// </remarks>
	/// <example>
	/// Let shots pass through a prop that players still collide with:
	/// <code>prop.Collision?.AddInteractsExclude(CollisionLayers.BulletsAndAbilities);</code>
	/// </example>
	public void AddInteractsExclude(MaskTrace layers) => NativeInterop.AddCollisionLayers((void*)Handle, InteractsExcludeMask, (ulong)layers);

	/// <summary>Removes <paramref name="layers"/> from <see cref="InteractsExclude"/>.</summary>
	/// <inheritdoc cref="AddInteractsExclude" path="/remarks"/>
	public void RemoveInteractsExclude(MaskTrace layers) => NativeInterop.RemoveCollisionLayers((void*)Handle, InteractsExcludeMask, (ulong)layers);

	/// <summary>The entity's collision group. Change it with <see cref="SetCollisionGroup"/>.</summary>
	public CollisionGroup CollisionGroup => (CollisionGroup)_collisionGroup.Get(Handle);

	/// <summary>Moves the entity to another collision group. Takes effect immediately.</summary>
	/// <remarks>
	/// Also updates <see cref="InteractsAs"/>: <see cref="Api.CollisionGroup.Debris"/> adds <see cref="MaskTrace.Debris"/>,
	/// and most other groups add <see cref="MaskTrace.TouchAll"/>.
	/// </remarks>
	public void SetCollisionGroup(CollisionGroup group) => NativeInterop.SetCollisionGroup((void*)Handle, (byte)group);

	/// <summary>The shape the entity's physics body is built from. Change it with <see cref="SetSolid"/>.</summary>
	public SolidType SolidType => (SolidType)_solidType.Get(Handle);

	/// <summary>Changes the shape the entity's physics body is built from.</summary>
	/// <remarks>
	/// Only applies when the physics body is rebuilt, which happens when <see cref="CBaseEntity.SetModel"/> is called with
	/// a different model.
	/// </remarks>
	public void SetSolid(SolidType type) => NativeInterop.SetSolid((void*)Handle, (byte)type);

	/// <summary>The entity's solid flags. Change them with <see cref="SetSolidFlags"/>.</summary>
	public SolidFlags SolidFlags => (SolidFlags)_solidFlags.Get(Handle);

	/// <summary>Replaces the entity's solid flags. Takes effect immediately.</summary>
	/// <remarks>To keep the current flags, combine them: <c>collision.SetSolidFlags(collision.SolidFlags | SolidFlags.NotStandable)</c>.</remarks>
	public void SetSolidFlags(SolidFlags flags) => NativeInterop.SetSolidFlags((void*)Handle, (byte)flags);

	/// <summary>Which collision functions are on. Toggle the main three with <see cref="EnableCollision"/> and <see cref="DisableCollision"/>.</summary>
	public CollisionFunctionMask_t CollisionFunctions => Attribute.CollisionFunctionMask;

	/// <summary>Turns solid contact, traces and touch events back on after <see cref="DisableCollision"/>.</summary>
	public void EnableCollision() => NativeInterop.SetCollisionEnabled((void*)Handle, 1);

	/// <summary>Turns off solid contact, traces and touch events, so players, bullets and triggers all pass through the entity.</summary>
	/// <remarks>
	/// To let only some things through, use <see cref="AddInteractsExclude"/>. To ignore one entity, use
	/// <see cref="CBaseEntity.DisableCollisionsWith"/>.
	/// </remarks>
	public void DisableCollision() => NativeInterop.SetCollisionEnabled((void*)Handle, 0);
}
