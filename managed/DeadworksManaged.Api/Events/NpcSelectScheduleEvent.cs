namespace DeadworksManaged.Api;

/// <summary>
/// Passed to <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/> when an NPC picks its next schedule, after the last
/// one finished, failed or was interrupted.
/// </summary>
public sealed class NpcSelectScheduleEvent {
	/// <summary>The NPC. Use <see cref="CBaseEntity.As{T}"/> for its class, e.g. <see cref="CNPC_Trooper"/>.</summary>
	public required CAI_BaseNPC Npc { get; init; }

	/// <summary>
	/// The schedule the game chose, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c>, or <c>SCHED_FAIL</c> after a failure.
	/// The NPC's class may translate it, so <see cref="CAI_BaseNPC.Schedule"/> can differ once it runs.
	/// </summary>
	public required string Schedule { get; init; }

	/// <summary>
	/// A schedule to run instead, such as <c>SCHED_IDLE_STAND</c>. Null keeps the game's choice. The last plugin to
	/// set it wins.
	/// </summary>
	/// <remarks>
	/// A schedule the NPC's class does not have runs <c>SCHED_IDLE_STAND</c> and logs "No CASE for Schedule Type".
	/// </remarks>
	public string? Override { get; set; }
}
