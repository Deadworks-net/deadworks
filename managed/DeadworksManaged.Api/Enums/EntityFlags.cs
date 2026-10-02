namespace DeadworksManaged.Api;

/// <summary>Engine-level flag bits stored in <c>CBaseEntity.m_fFlags</c> (on-ground, ducking, client, in-vehicle, godmode, etc.).</summary>
[Flags]
public enum EntityFlags : uint {
	None = 0x0,
	OnGround = 0x1,
	Ducking = 0x2,
	WaterJump = 0x4,
	Bot = 0x10,
	Frozen = 0x20,
	AtControls = 0x40,
	Client = 0x80,
	FakeClient = 0x100,
	Fly = 0x400,
	SuppressSave = 0x800,
	InVehicle = 0x1000,
	Godmode = 0x4000,
	NoTarget = 0x8000,
	AimTarget = 0x10000,
	Grenade = 0x100000,
	DontTouch = 0x400000,
	OnFire = 0x8000000,
	Dissolving = 0x10000000,
	TransRagdoll = 0x20000000,
	UnblockableByPlayer = 0x40000000,
}
