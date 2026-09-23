namespace DeadworksManaged.Api;

/// <summary>
/// How an entity moves. Read it with <see cref="CBaseEntity.MoveType"/> and change it with
/// <see cref="CBaseEntity.SetMoveType"/>.
/// </summary>
/// <remarks>
/// Heroes can only move under <see cref="Walk"/> and <see cref="NoClip"/>. Any other value leaves a hero frozen in place.
/// </remarks>
public enum MoveType : byte {
	/// <summary>Doesn't move at all. Set this on a hero to freeze them in place.</summary>
	None = 0,
	/// <summary>Left over from older versions of the engine. Don't use it.</summary>
	Obsolete = 1,
	/// <summary>Normal hero movement: walking and jumping, with gravity and collision.</summary>
	Walk = 2,
	/// <summary>Flying movement without gravity. A hero set to this can't move.</summary>
	Fly = 3,
	/// <summary>Flying movement with gravity, like a thrown object. A hero set to this can't move.</summary>
	FlyGravity = 4,
	/// <summary>Moved by the physics engine, like a prop that can tumble and be knocked around.</summary>
	VPhysics = 5,
	/// <summary>Used by doors, elevators and other movers that push whatever is in their way.</summary>
	Push = 6,
	/// <summary>Flies freely through walls. This is what the <c>noclip</c> cheat command uses on heroes.</summary>
	NoClip = 7,
	/// <summary>Used for spectators.</summary>
	Observer = 8,
	/// <summary>Walking movement used by some NPCs, such as neutral creeps.</summary>
	Step = 9,
	/// <summary>Used internally by the game. The game's own tools warn that switching to it behaves strangely.</summary>
	Sync = 10,
	/// <summary>Movement handled entirely by the entity's own code. Used internally by the game.</summary>
	Custom = 11,
}
