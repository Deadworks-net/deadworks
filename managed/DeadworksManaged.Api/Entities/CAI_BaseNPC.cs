using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Base class of every AI-controlled NPC: lane troopers, neutral creeps, the mid boss, guardians, walkers, the
/// Patron, hero summons and bot brains.
/// </summary>
/// <remarks>Hero pawns are not NPCs, even a bot's. A bot's AI is its <see cref="CCitadelPlayerBotNPCBrain"/>.</remarks>
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

	/// <summary>
	/// Creates and spawns an NPC by its VData subclass name, such as <c>neutral_trooper_strong</c> or
	/// <c>npc_boss_tier1</c>. Returns null if the name is not an NPC subclass.
	/// </summary>
	/// <remarks>
	/// A class name such as <c>npc_trooper_neutral</c> returns null: an NPC without VData crashes the server. For
	/// lane troopers use <see cref="CNPC_Trooper.Create"/>, which also assigns a lane.
	/// </remarks>
	/// <param name="designerName">The subclass name. <c>SubclassNames</c> in DeadworksManaged.Game lists them.</param>
	/// <param name="position">Where the NPC appears.</param>
	/// <param name="team">0 keeps the NPC's default team.</param>
	/// <param name="angles">Pitch, yaw, roll. Null faces +X.</param>
	public static CAI_BaseNPC? Create(string designerName, Vector3 position, int team = 0, Vector3? angles = null) {
		// Checked before anything is created: removing an entity that was never spawned can crash as well.
		uint subclassId = 0;
		string? baseClass;
		Span<byte> utf8 = Utf8.Encode(designerName, stackalloc byte[Utf8.Size(designerName)]);
		fixed (byte* name = utf8)
			baseClass = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)NativeInterop.ResolveDesignerName(name, &subclassId));
		if (subclassId == 0 || baseClass == null || !baseClass.StartsWith("npc_"))
			return null;

		var entity = CreateByDesignerName(designerName);
		if (entity == null)
			return null;
		var npc = entity.As<CAI_BaseNPC>();
		if (npc == null) {
			entity.Remove();
			return null;
		}
		var kv = new CEntityKeyValues();
		kv.SetVector("origin", position);
		if (angles is { } a)
			kv.SetVector("angles", a);
		if (team != 0)
			kv.SetInt("teamnumber", team);
		npc.Spawn(kv);
		return npc;
	}

	/// <summary>The NPC's current AI state.</summary>
	public NpcState State => (NpcState)_npcState.Get(Handle);

	/// <summary>The state the AI is moving towards. It becomes <see cref="State"/> on the NPC's next think.</summary>
	public NpcState IdealState => (NpcState)_idealNpcState.Get(Handle);

	/// <summary>
	/// True while the NPC is <see cref="NpcState.Inert"/>, such as a hidden trooper the game keeps for reuse. Skip
	/// these when giving orders.
	/// </summary>
	public bool IsDormant => State == NpcState.Inert;

	/// <summary>The running schedule, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c>, or null between schedules.</summary>
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

	/// <summary>Sends the NPC to <paramref name="goal"/>, as <c>npc_go</c> does, without cheats.</summary>
	/// <remarks>
	/// The NPC ignores enemies and damage until it arrives or the path fails, unless
	/// <c>npc_go_allow_interrupt_by_ai_conditions</c> is on. It then resumes its own behaviour. Most orders more
	/// than 3000 units of path away fail. To follow a moving target, call this again every half second. The goal
	/// need not be on the navmesh.
	/// </remarks>
	/// <param name="goal">Where to go.</param>
	/// <param name="run">Run instead of walking.</param>
	/// <param name="tolerance">How close counts as arrived. 0 matches <c>npc_go</c>.</param>
	public void MoveTo(Vector3 goal, bool run = true, float tolerance = 0f) {
		float* g = stackalloc float[3] { goal.X, goal.Y, goal.Z };
		NativeInterop.NpcMoveTo((void*)Handle, g, run ? (byte)1 : (byte)0, tolerance);
	}

	/// <summary>Drops the current movement goal, as <c>npc_stop_moving</c> does. The AI picks a new schedule on its next think.</summary>
	/// <param name="instant">Stop on the spot instead of slowing down.</param>
	public void Stop(bool instant = false) => NativeInterop.NpcStopMoving((void*)Handle, instant ? (byte)1 : (byte)0);

	/// <summary>Sets the NPC's enemy, or clears it with null.</summary>
	/// <remarks>
	/// The AI re-chooses its enemy every think and may replace this one, for example with a closer target. It never
	/// keeps an enemy on its own team.
	/// </remarks>
	public void SetEnemy(CBaseEntity? enemy) => NativeInterop.NpcSetEnemy((void*)Handle, enemy != null ? (void*)enemy.Handle : null);

	/// <summary>Starts the named schedule, such as <c>SCHED_IDLE_STAND</c>. Returns false if the NPC refuses it.</summary>
	/// <remarks>
	/// The schedule runs until it finishes or is interrupted, then the NPC picks its next one;
	/// <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/> can replace that choice. A schedule the NPC's class does
	/// not have runs <c>SCHED_IDLE_STAND</c> and logs "No CASE for Schedule Type".
	/// </remarks>
	public bool SetSchedule(string name) {
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* p = utf8)
			return NativeInterop.NpcSetSchedule((void*)Handle, p) != 0;
	}

	/// <summary>
	/// Changes the NPC's AI state. <see cref="NpcState.Idle"/> also clears the enemy, and the game sometimes turns it
	/// into <see cref="NpcState.Alert"/>.
	/// </summary>
	public void SetState(NpcState state) => NativeInterop.NpcSetState((void*)Handle, (int)state);
}
