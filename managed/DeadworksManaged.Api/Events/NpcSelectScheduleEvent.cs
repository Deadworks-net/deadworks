namespace DeadworksManaged.Api;

/// <summary>
/// Passed to <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/> each time an NPC picks its next schedule: its
/// previous one finished, failed or was interrupted (for example by a new enemy).
/// </summary>
public sealed class NpcSelectScheduleEvent {
	/// <summary>The NPC. Use <see cref="CBaseEntity.As{T}"/> for its class, e.g. <see cref="CNPC_Trooper"/>.</summary>
	public required CAI_BaseNPC Npc { get; init; }

	/// <summary>
	/// The schedule the game chose, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c>, or <c>SCHED_FAIL</c> after a
	/// failure. This is the id before the NPC's class translates it, so it can differ from
	/// <see cref="CAI_BaseNPC.Schedule"/> once the schedule runs.
	/// </summary>
	public required string Schedule { get; init; }

	/// <summary>
	/// Set to a schedule name to run that instead, for example <c>SCHED_IDLE_STAND</c> to keep a trooper where
	/// it is. Null keeps the game's choice. With several plugins, the last one to set it wins.
	/// </summary>
	/// <remarks>
	/// The name is resolved like the game's own choice. A name the NPC's class has no schedule for runs
	/// <c>SCHED_IDLE_STAND</c>, and the game logs "No CASE for Schedule Type".
	/// </remarks>
	public string? Override { get; set; }
}
