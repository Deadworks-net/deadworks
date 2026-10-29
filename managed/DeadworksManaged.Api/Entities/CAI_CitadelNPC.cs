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

	/// <summary>The NPC's abilities.</summary>
	public CCitadelAbilityComponent AbilityComponent => new(_abilityComp.GetAddress(Handle));

	/// <summary>Ability that spawned this summon. Null for map NPCs.</summary>
	public CCitadelBaseAbility? OwningAbility {
		get {
			// Every ability is its own subclass, so FromHandle<CCitadelBaseAbility> wouldn't match the class name.
			void* ability = NativeInterop.GetEntityFromHandle(_hAbilityOwner.Get(Handle));
			return ability != null ? new CCitadelBaseAbility((nint)ability) : null;
		}
	}

	/// <summary>True for a hero's minion. See <see cref="SetMinion"/>.</summary>
	public bool IsMinion => _minion.Get(Handle);

	/// <summary>Makes the NPC a hero's minion, like Graves's skeletons, or a normal NPC again.</summary>
	/// <remarks>A minion's damage and kills count for its living <see cref="Summoner"/>. Minions also attack neutral creeps.</remarks>
	public void SetMinion(bool minion) => NativeInterop.NpcSetMinion((void*)Handle, minion ? (byte)1 : (byte)0);

	/// <summary>
	/// Entity the NPC works for: the one set with <see cref="SetSummoner"/>, else the owner of
	/// <see cref="OwningAbility"/>. Null for map NPCs.
	/// </summary>
	public CBaseEntity? Summoner {
		get {
			void* summoner = NativeInterop.NpcGetSummoner((void*)Handle);
			return summoner != null ? new CBaseEntity((nint)summoner) : null;
		}
	}

	/// <summary>Sets the entity the NPC works for, usually a hero pawn. Null clears it.</summary>
	/// <remarks>Minions are removed with the summoner's pawn, for example on a hero swap. They survive the hero's death.</remarks>
	public void SetSummoner(CBaseEntity? summoner) =>
		NativeInterop.NpcSetSummoner((void*)Handle, summoner?.EntityHandle ?? InvalidEntityHandle);
}
