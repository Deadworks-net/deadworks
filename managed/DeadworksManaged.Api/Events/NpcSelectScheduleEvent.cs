namespace DeadworksManaged.Api;

/// <summary>Passed to <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/> when an NPC picks its next schedule.</summary>
public sealed class NpcSelectScheduleEvent {
	/// <summary>The NPC. Use <see cref="CBaseEntity.As{T}"/> for its class, such as <see cref="CNPC_Trooper"/>.</summary>
	public required CAI_BaseNPC Npc { get; init; }

	/// <summary>
	/// Schedule the game picked, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c>, or <c>SCHED_FAIL</c> after a failure.
	/// NPC classes rename some schedules, so <see cref="CAI_BaseNPC.Schedule"/> can differ once it runs.
	/// </summary>
	public required string Schedule { get; init; }

	/// <summary>
	/// Schedule to run instead, such as <c>SCHED_IDLE_STAND</c>. Null keeps the game's pick. The last plugin to set it
	/// wins.
	/// </summary>
	/// <remarks>A schedule the NPC's class lacks runs <c>SCHED_IDLE_STAND</c> and logs "No CASE for Schedule Type".</remarks>
	public string? Override { get; set; }
}
