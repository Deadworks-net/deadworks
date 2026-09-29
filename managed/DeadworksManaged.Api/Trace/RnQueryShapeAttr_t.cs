using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Query attributes for a VPhys2 shape trace: which layers and object sets to consider,
/// entities to skip, and flags like <see cref="HitSolid"/> and <see cref="HitTrigger"/>.
/// Default-constructed with sensible defaults (hit solid, ignore disabled pairs, all object sets).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct RnQueryShapeAttr_t {
	public InteractionLayer InteractsWith;
	public InteractionLayer InteractsExclude;
	public InteractionLayer InteractsAs;
	public fixed uint EntityIdsToIgnore[2];
	public fixed uint OwnerIdsToIgnore[2];
	public fixed ushort HierarchyIds[2];
	public ushort IncludedDetailLayers;
	public byte TargetDetailLayer;
	public RnQueryObjectSet ObjectSetMask;
	public CollisionGroup CollisionGroup;
	private byte _data;

	public bool HitSolid {
		get => (_data & (1 << 0)) != 0;
		set { if (value) _data |= 1 << 0; else _data &= unchecked((byte)~(1 << 0)); }
	}
	public bool HitSolidRequiresGenerateContacts {
		get => (_data & (1 << 1)) != 0;
		set { if (value) _data |= 1 << 1; else _data &= unchecked((byte)~(1 << 1)); }
	}
	public bool HitTrigger {
		get => (_data & (1 << 2)) != 0;
		set { if (value) _data |= 1 << 2; else _data &= unchecked((byte)~(1 << 2)); }
	}
	public bool ShouldIgnoreDisabledPairs {
		get => (_data & (1 << 3)) != 0;
		set { if (value) _data |= 1 << 3; else _data &= unchecked((byte)~(1 << 3)); }
	}
	public bool IgnoreIfBothInteractWithHitboxes {
		get => (_data & (1 << 4)) != 0;
		set { if (value) _data |= 1 << 4; else _data &= unchecked((byte)~(1 << 4)); }
	}
	public bool ForceHitEverything {
		get => (_data & (1 << 5)) != 0;
		set { if (value) _data |= 1 << 5; else _data &= unchecked((byte)~(1 << 5)); }
	}
	public bool Unknown {
		get => (_data & (1 << 6)) != 0;
		set { if (value) _data |= 1 << 6; else _data &= unchecked((byte)~(1 << 6)); }
	}

	public RnQueryShapeAttr_t() {
		InteractsWith = 0;
		InteractsExclude = 0;
		InteractsAs = 0;
		EntityIdsToIgnore[0] = uint.MaxValue;
		EntityIdsToIgnore[1] = uint.MaxValue;
		OwnerIdsToIgnore[0] = uint.MaxValue;
		OwnerIdsToIgnore[1] = uint.MaxValue;
		HierarchyIds[0] = 0;
		HierarchyIds[1] = 0;
		IncludedDetailLayers = ushort.MaxValue;
		TargetDetailLayer = 0;
		ObjectSetMask = RnQueryObjectSet.All;
		CollisionGroup = CollisionGroup.Always;
		HitSolid = true;
		ShouldIgnoreDisabledPairs = true;
		Unknown = true;
	}
}
