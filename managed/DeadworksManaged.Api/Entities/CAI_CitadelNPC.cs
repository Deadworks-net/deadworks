namespace DeadworksManaged.Api;

/// <summary>
/// A Deadlock NPC: every <see cref="CAI_BaseNPC"/> the game spawns has abilities, regeneration and a minimap
/// icon through this class.
/// </summary>
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

	/// <summary>
	/// For a summon, the ability that spawned it, for example Necro's skeleton ability. Null for map NPCs.
	/// </summary>
	public CCitadelBaseAbility? OwningAbility {
		get {
			// Every ability is its own subclass, so FromHandle<CCitadelBaseAbility> wouldn't match the class name.
			void* ability = NativeInterop.GetEntityFromHandle(_hAbilityOwner.Get(Handle));
			return ability != null ? new CCitadelBaseAbility((nint)ability) : null;
		}
	}

	/// <summary>
	/// True for a hero's summon. Its kills and damage are credited to the hero while the hero is alive, and it
	/// also attacks neutral creeps.
	/// </summary>
	public bool IsMinion => _minion.Get(Handle);
}
