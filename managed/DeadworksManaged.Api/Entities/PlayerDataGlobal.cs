namespace DeadworksManaged.Api;

/// <summary>Wraps the networked PlayerDataGlobal_t struct on a player controller - provides read access to stats like kills, gold, level, and damage.</summary>
public unsafe class PlayerDataGlobal : NativeEntity {
	private static ReadOnlySpan<byte> Class => "PlayerDataGlobal_t"u8;

	// The controller this struct is embedded in. PlayerDataGlobal_t has no network chainer of its own, so a change
	// has to be reported on the controller at the struct's own offset plus the field's.
	private readonly nint _owner;

	internal PlayerDataGlobal(nint handle, nint owner) : base(handle) { _owner = owner; }

	private void Set<T>(SchemaAccessor<T> field, T value) where T : unmanaged {
		*(T*)field.GetAddress(Handle) = value;
		NativeInterop.NotifyStateChanged((void*)_owner, (int)(Handle - _owner) + field.Offset, 0, 0);
	}

	private static readonly SchemaAccessor<int> _iLevel = new(Class, "m_iLevel"u8);
	public int Level => _iLevel.Get(Handle);

	private static readonly SchemaAccessor<int> _nHeroID = new(Class, "m_nHeroID"u8);
	public int HeroID { get => _nHeroID.Get(Handle); set => Set(_nHeroID, value); }

	private static readonly SchemaAccessor<int> _iMaxAmmo = new(Class, "m_iMaxAmmo"u8);
	public int MaxAmmo => _iMaxAmmo.Get(Handle);

	private static readonly SchemaAccessor<int> _iHealthMax = new(Class, "m_iHealthMax"u8);
	public int HealthMax => _iHealthMax.Get(Handle);

	private static readonly SchemaAccessor<int> _iGoldNetWorth = new(Class, "m_iGoldNetWorth"u8);
	public int GoldNetWorth => _iGoldNetWorth.Get(Handle);

	private static readonly SchemaAccessor<int> _iAPNetWorth = new(Class, "m_iAPNetWorth"u8);
	public int APNetWorth => _iAPNetWorth.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGold = new(Class, "m_iCreepGold"u8);
	public int CreepGold => _iCreepGold.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldSoloBonus = new(Class, "m_iCreepGoldSoloBonus"u8);
	public int CreepGoldSoloBonus => _iCreepGoldSoloBonus.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldKill = new(Class, "m_iCreepGoldKill"u8);
	public int CreepGoldKill => _iCreepGoldKill.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldAirOrb = new(Class, "m_iCreepGoldAirOrb"u8);
	public int CreepGoldAirOrb => _iCreepGoldAirOrb.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldGroundOrb = new(Class, "m_iCreepGoldGroundOrb"u8);
	public int CreepGoldGroundOrb => _iCreepGoldGroundOrb.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldDeny = new(Class, "m_iCreepGoldDeny"u8);
	public int CreepGoldDeny => _iCreepGoldDeny.Get(Handle);

	private static readonly SchemaAccessor<int> _iCreepGoldNeutral = new(Class, "m_iCreepGoldNeutral"u8);
	public int CreepGoldNeutral => _iCreepGoldNeutral.Get(Handle);

	private static readonly SchemaAccessor<int> _iFarmBaseline = new(Class, "m_iFarmBaseline"u8);
	public int FarmBaseline => _iFarmBaseline.Get(Handle);

	private static readonly SchemaAccessor<int> _iHealth = new(Class, "m_iHealth"u8);
	public int Health => _iHealth.Get(Handle);

	private static readonly SchemaAccessor<int> _iPlayerKills = new(Class, "m_iPlayerKills"u8);
	public int PlayerKills => _iPlayerKills.Get(Handle);

	private static readonly SchemaAccessor<int> _iPlayerAssists = new(Class, "m_iPlayerAssists"u8);
	public int PlayerAssists => _iPlayerAssists.Get(Handle);

	private static readonly SchemaAccessor<int> _iDeaths = new(Class, "m_iDeaths"u8);
	public int Deaths => _iDeaths.Get(Handle);

	private static readonly SchemaAccessor<int> _iDenies = new(Class, "m_iDenies"u8);
	public int Denies => _iDenies.Get(Handle);

	private static readonly SchemaAccessor<int> _iLastHits = new(Class, "m_iLastHits"u8);
	public int LastHits => _iLastHits.Get(Handle);

	private static readonly SchemaAccessor<int> _iKillStreak = new(Class, "m_iKillStreak"u8);
	public int KillStreak => _iKillStreak.Get(Handle);

	private static readonly SchemaAccessor<int> _nHeroDraftPosition = new(Class, "m_nHeroDraftPosition"u8);
	public int HeroDraftPosition => _nHeroDraftPosition.Get(Handle);

	private static readonly SchemaAccessor<int> _iHeroDamage = new(Class, "m_iHeroDamage"u8);
	public int HeroDamage => _iHeroDamage.Get(Handle);

	private static readonly SchemaAccessor<int> _iHeroHealing = new(Class, "m_iHeroHealing"u8);
	public int HeroHealing => _iHeroHealing.Get(Handle);

	private static readonly SchemaAccessor<int> _iSelfHealing = new(Class, "m_iSelfHealing"u8);
	public int SelfHealing => _iSelfHealing.Get(Handle);

	private static readonly SchemaAccessor<int> _iObjectiveDamage = new(Class, "m_iObjectiveDamage"u8);
	public int ObjectiveDamage => _iObjectiveDamage.Get(Handle);
}
