namespace DeadworksManaged.Api;

/// <summary>A Deadlock NPC. Every <see cref="CAI_BaseNPC"/> the game spawns is one.</summary>
[NativeClass("CAI_CitadelNPC", "CCitadelPlayerBotNPCBrain",
	"CNPC_Trooper", "CNPC_TrooperNeutral", "CNPC_Neutral_SinnersSacrifice", "CNPC_Neutral_SinnersSacrifice_Hideout",
	"CNPC_MidBoss", "CNPC_TrooperBoss", "CNPC_Boss_Tier2", "CNPC_Boss_Tier3", "CNPC_BarrackBoss",
	"CNPC_NecroSkele", "CNPC_FamiliarHelper", "CNPC_Escort", "CNPC_YakuzaGangster")]
public unsafe class CAI_CitadelNPC : CAI_BaseNPC {
	internal CAI_CitadelNPC(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CAI_CitadelNPC"u8;

	private static readonly SchemaAccessor<byte> _abilityComp = new(Class, "m_CCitadelAbilityComponent"u8);
	private static readonly SchemaAccessor<uint> _hAbilityOwner = new(Class, "m_hAbilityOwner"u8);
	private static readonly SchemaAccessor<bool> _minion = new(Class, "m_bMinion"u8);

	/// <summary>The NPC's abilities, such as a boss's attacks or a trooper's grenade.</summary>
	public CCitadelAbilityComponent AbilityComponent => new(_abilityComp.GetAddress(Handle));

	/// <summary>The ability that spawned a summon. Null for map NPCs.</summary>
	public CCitadelBaseAbility? OwningAbility {
		get {
			// Every ability is its own subclass, so FromHandle<CCitadelBaseAbility> wouldn't match the class name.
			void* ability = NativeInterop.GetEntityFromHandle(_hAbilityOwner.Get(Handle));
			return ability != null ? new CCitadelBaseAbility((nint)ability) : null;
		}
	}

	/// <summary>True for a hero's summon. See <see cref="SetMinion"/>.</summary>
	public bool IsMinion => _minion.Get(Handle);

	/// <summary>
	/// Makes the NPC a minion, like Necro's skeletons, or a regular NPC again. A minion's damage and kills count for
	/// its living <see cref="Summoner"/>, it attacks neutral creeps, and enemy abilities treat it as a minion.
	/// </summary>
	public void SetMinion(bool minion) => NativeInterop.NpcSetMinion((void*)Handle, minion ? (byte)1 : (byte)0);

	/// <summary>
	/// Who the NPC works for: the entity set with <see cref="SetSummoner"/>, else the owner of
	/// <see cref="OwningAbility"/>. Null for map NPCs.
	/// </summary>
	public CBaseEntity? Summoner {
		get {
			void* summoner = NativeInterop.NpcGetSummoner((void*)Handle);
			return summoner != null ? new CBaseEntity((nint)summoner) : null;
		}
	}

	/// <summary>
	/// Sets who the NPC works for, usually a hero pawn, or clears it with null. With <see cref="SetMinion"/>, the
	/// hero gets credit for its damage and kills. Minions are removed with the pawn, such as on a hero swap, but
	/// survive the hero's death.
	/// </summary>
	public void SetSummoner(CBaseEntity? summoner) =>
		NativeInterop.NpcSetSummoner((void*)Handle, summoner?.EntityHandle ?? InvalidEntityHandle);
}
