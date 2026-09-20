namespace DeadworksManaged.Api;

/// <summary>Entity movement mode (<c>MoveType_t</c>). Read and written through <see cref="CBaseEntity.MoveType"/>.</summary>
public enum MoveType : byte {
	/// <summary>Never moves. Freezes a pawn in place while keeping it fully interactive.</summary>
	None = 0,
	Obsolete = 1,
	/// <summary>Normal player movement with gravity and collision.</summary>
	Walk = 2,
	/// <summary>Flies without gravity but still collides with the world.</summary>
	Fly = 3,
	/// <summary>Flies with gravity applied.</summary>
	FlyGravity = 4,
	/// <summary>Driven by the physics simulation.</summary>
	VPhysics = 5,
	/// <summary>Moves along a path, pushing entities it touches (doors, platforms).</summary>
	Push = 6,
	/// <summary>Flies through everything, ignoring collision.</summary>
	NoClip = 7,
	/// <summary>Spectator movement.</summary>
	Observer = 8,
	/// <summary>Custom NPC step movement.</summary>
	Step = 9,
	Sync = 10,
	Custom = 11,
}
