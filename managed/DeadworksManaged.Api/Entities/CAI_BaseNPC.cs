using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Base class of every NPC: troopers, neutral creeps, bosses, the Patron, hero summons and bot brains.
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
	/// Spawns an NPC by VData subclass name, such as <c>neutral_trooper_strong</c> or <c>npc_boss_tier1</c>. Returns
	/// null if the name is not an NPC subclass.
	/// </summary>
	/// <remarks>
	/// Class names such as <c>npc_trooper_neutral</c> return null. For lane troopers use
	/// <see cref="CNPC_Trooper.Create"/>, which also assigns a lane.
	/// </remarks>
	/// <param name="designerName">Subclass name. <c>SubclassNames</c> in DeadworksManaged.Game lists them.</param>
	/// <param name="position">Spawn position.</param>
	/// <param name="team">Team. 0 keeps the NPC's default.</param>
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

	/// <summary>Current AI state.</summary>
	public NpcState State => (NpcState)_npcState.Get(Handle);

	/// <summary>AI state the NPC switches to on its next think.</summary>
	public NpcState IdealState => (NpcState)_idealNpcState.Get(Handle);

	/// <summary>True for a hidden trooper the game keeps for reuse (<see cref="NpcState.Inert"/>). Skip these.</summary>
	public bool IsDormant => State == NpcState.Inert;

	/// <summary>Running schedule, such as <c>SCHED_TROOPER_RUN_DOWN_LANE</c>. Null between schedules.</summary>
	public string? Schedule {
		get {
			// ScheduleId_t is a CGlobalSymbol: a pointer to the interned name.
			nint name = _translatedSchedule.Get(_scheduler.GetAddress(Handle));
			return name != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) : null;
		}
	}

	/// <summary>Entity the NPC is fighting, or null.</summary>
	public CBaseEntity? Enemy {
		get {
			nint services = _enemyServices.Get(Handle);
			return services != 0 ? FromHandle(_hEnemy.Get(services)) : null;
		}
	}

	/// <summary>Sends the NPC to a point, like <c>npc_go</c>.</summary>
	/// <remarks>
	/// The NPC ignores enemies and damage until it arrives or the path fails, then resumes its own AI. Most orders
	/// over 3000 units of path fail. Shorter ones can stop partway in <c>SCHED_FAIL</c>. Send long trips in legs along
	/// <see cref="Nav.FindPath(Vector3, Vector3, CBaseEntity, bool, bool)"/>, and order again after a failure. To
	/// follow a moving target, call again every 0.5 s.
	/// </remarks>
	/// <param name="goal">Destination. Need not be on the navmesh.</param>
	/// <param name="run">Run instead of walking.</param>
	/// <param name="tolerance">Arrival distance. 0 uses the <c>npc_go</c> default.</param>
	public void MoveTo(Vector3 goal, bool run = true, float tolerance = 0f) {
		float* g = stackalloc float[3] { goal.X, goal.Y, goal.Z };
		NativeInterop.NpcMoveTo((void*)Handle, g, run ? (byte)1 : (byte)0, tolerance);
	}

	/// <summary>Drops the current move order, like <c>npc_stop_moving</c>.</summary>
	/// <param name="instant">Stop on the spot instead of slowing down.</param>
	public void Stop(bool instant = false) => NativeInterop.NpcStopMoving((void*)Handle, instant ? (byte)1 : (byte)0);

	/// <summary>Sets the NPC's enemy. Null clears it.</summary>
	/// <remarks>The AI re-picks its enemy every think and may switch to a closer one. An enemy on the NPC's own team is dropped.</remarks>
	public void SetEnemy(CBaseEntity? enemy) => NativeInterop.NpcSetEnemy((void*)Handle, enemy != null ? (void*)enemy.Handle : null);

	/// <summary>Starts a schedule, such as <c>SCHED_IDLE_STAND</c>. Returns false if the NPC refuses it.</summary>
	/// <remarks>
	/// Runs until it finishes or is interrupted. A schedule the NPC's class lacks runs <c>SCHED_IDLE_STAND</c>
	/// instead and logs "No CASE for Schedule Type". To replace the NPC's next pick, use
	/// <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/>.
	/// </remarks>
	public bool SetSchedule(string name) {
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* p = utf8)
			return NativeInterop.NpcSetSchedule((void*)Handle, p) != 0;
	}

	/// <summary>Sets the AI state. <see cref="NpcState.Idle"/> also clears the enemy.</summary>
	/// <remarks>The game may turn <see cref="NpcState.Idle"/> into <see cref="NpcState.Alert"/>.</remarks>
	public void SetState(NpcState state) => NativeInterop.NpcSetState((void*)Handle, (int)state);
}
