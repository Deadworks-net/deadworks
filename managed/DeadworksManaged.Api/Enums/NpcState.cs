namespace DeadworksManaged.Api;

/// <summary>An NPC's AI state (<c>NPC_STATE</c>). See <see cref="CAI_BaseNPC.State"/>.</summary>
public enum NpcState : int {
	Invalid = -1,
	/// <summary>Just spawned. The AI doesn't pick enemies yet.</summary>
	Init = 0,
	/// <summary>No enemy. Setting it clears the enemy.</summary>
	Idle = 1,
	Alert = 2,
	Combat = 3,
	Script = 4,
	Dead = 5,
	/// <summary>Switched off. Pooled troopers wait in this state.</summary>
	Inert = 6,
	SynchronizedSecondary = 7,
}
