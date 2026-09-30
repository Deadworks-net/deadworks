using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Base class of every AI-driven NPC: lane troopers, neutral camp creeps, the mid boss, guardians, walkers,
/// the Patron, hero summons such as Necro's skeletons, and the brain that drives a bot's hero.
/// </summary>
/// <remarks>
/// Hero pawns are not NPCs, even bot heroes: their AI lives in a separate <c>CCitadelPlayerBotNPCBrain</c>.
/// </remarks>
[NativeClass("CAI_BaseNPC", "CAI_CitadelNPC", "CCitadelPlayerBotNPCBrain",
	"CNPC_Trooper", "CNPC_TrooperNeutral", "CNPC_Neutral_SinnersSacrifice", "CNPC_Neutral_SinnersSacrifice_Hideout",
	"CNPC_MidBoss", "CNPC_TrooperBoss", "CNPC_Boss_Tier2", "CNPC_Boss_Tier3", "CNPC_BarrackBoss",
	"CNPC_NecroSkele", "CNPC_FamiliarHelper", "CNPC_Escort", "CNPC_YakuzaGangster")]
public unsafe class CAI_BaseNPC : CBaseEntity {
	internal CAI_BaseNPC(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CAI_BaseNPC"u8;

	private static readonly SchemaAccessor<int> _npcState = new(Class, "m_NPCState"u8);
	private static readonly SchemaAccessor<int> _idealNpcState = new(Class, "m_IdealNPCState"u8);
	private static readonly SchemaAccessor<nint> _enemyServices = new(Class, "m_pEnemyServices"u8);
	private static readonly SchemaAccessor<uint> _hEnemy = new("CAI_EnemyServices"u8, "m_hEnemy"u8);
	private static readonly SchemaAccessor<byte> _scheduler = new(Class, "m_Scheduler"u8);
	private static readonly SchemaAccessor<nint> _translatedSchedule = new("CAI_Scheduler"u8, "m_translatedSchedule"u8);

	/// <summary>The NPC's current AI state.</summary>
	public NpcState State => (NpcState)_npcState.Get(Handle);

	/// <summary>The state the AI is moving towards. It becomes <see cref="State"/> on the NPC's next think.</summary>
	public NpcState IdealState => (NpcState)_idealNpcState.Get(Handle);

	/// <summary>
	/// True while the NPC is switched off (<see cref="NpcState.Inert"/>). The game keeps a pool of hidden,
	/// inert troopers and brings them back as new spawns, so skip these when giving orders.
	/// </summary>
	public bool IsDormant => State == NpcState.Inert;

	/// <summary>
	/// Name of the schedule the NPC is running, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c> or
	/// <c>SCHED_FORCED_GO</c>, or null between schedules.
	/// </summary>
	public string? Schedule {
		get {
			// ScheduleId_t is a CGlobalSymbol: a pointer to the interned name.
			nint name = _translatedSchedule.Get(_scheduler.GetAddress(Handle));
			return name != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) : null;
		}
	}

	/// <summary>The entity the NPC is currently fighting, or null.</summary>
	public CBaseEntity? Enemy {
		get {
			nint services = _enemyServices.Get(Handle);
			return services != 0 ? FromHandle(_hEnemy.Get(services)) : null;
		}
	}

	/// <summary>
	/// Sends the NPC to <paramref name="goal"/>, the same order as the <c>npc_go</c> console command, but with no
	/// cheats or NPC selection needed.
	/// </summary>
	/// <remarks>
	/// The move can't be interrupted: the NPC ignores enemies and damage until it arrives or the path fails,
	/// unless the <c>npc_go_allow_interrupt_by_ai_conditions</c> convar is on. It then goes back to its own
	/// behaviour, which for a lane trooper means walking back to its lane. To keep an NPC following a moving
	/// target, call this again every half second or so. The goal doesn't need to be on the navmesh.
	/// </remarks>
	/// <param name="goal">Where to go.</param>
	/// <param name="run">Run instead of walking.</param>
	/// <param name="tolerance">How close counts as arrived. 0 is what <c>npc_go</c> uses.</param>
	public void MoveTo(Vector3 goal, bool run = true, float tolerance = 0f) {
		float* g = stackalloc float[3] { goal.X, goal.Y, goal.Z };
		NativeInterop.NpcMoveTo((void*)Handle, g, run ? (byte)1 : (byte)0, tolerance);
	}

	/// <summary>
	/// Drops the NPC's current movement goal, like <c>npc_stop_moving</c>. The AI picks a new schedule on its
	/// next think, so a lane trooper sets off down its lane again.
	/// </summary>
	/// <param name="instant">Stop on the spot instead of slowing down first.</param>
	public void Stop(bool instant = false) => NativeInterop.NpcStopMoving((void*)Handle, instant ? (byte)1 : (byte)0);

	/// <summary>
	/// Makes <paramref name="enemy"/> the NPC's enemy, or clears it with null.
	/// </summary>
	/// <remarks>
	/// The AI still chooses its own enemy every time it thinks, so it can replace this one straight away, for
	/// example with a closer target. It never keeps an entity on its own team as an enemy.
	/// </remarks>
	public void SetEnemy(CBaseEntity? enemy) => NativeInterop.NpcSetEnemy((void*)Handle, enemy != null ? (void*)enemy.Handle : null);

	/// <summary>
	/// Switches the NPC to the named schedule now, such as <c>SCHED_IDLE_STAND</c>, the way the game's own
	/// "set schedule" task does. Returns false if the NPC refuses it.
	/// </summary>
	/// <remarks>
	/// The schedule runs until it finishes or something interrupts it (a new enemy, damage...), and then the
	/// NPC picks its next one as usual; <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/> can steer that choice.
	/// A name the NPC's class has no schedule for runs <c>SCHED_IDLE_STAND</c>, and the game logs "No CASE for
	/// Schedule Type".
	/// </remarks>
	public bool SetSchedule(string name) {
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* p = utf8)
			return NativeInterop.NpcSetSchedule((void*)Handle, p) != 0;
	}

	/// <summary>
	/// Changes the NPC's AI state. <see cref="NpcState.Idle"/> also clears the enemy. In some conditions the game
	/// turns a request for <see cref="NpcState.Idle"/> into <see cref="NpcState.Alert"/>.
	/// </summary>
	public void SetState(NpcState state) => NativeInterop.NpcSetState((void*)Handle, (int)state);
}
