using System.Runtime.CompilerServices;

namespace DeadworksManaged.Api;

/// <summary>
/// A modifier event (damage taken, ability cast, modifier gained/lost, ...) fired by the game.
/// Passed to <see cref="IDeadworksPlugin.OnModifierEvent"/>. Observe-only.
/// </summary>
/// <remarks><c>*Broadcast</c> events are not delivered through this hook.</remarks>
public sealed class ModifierEvent {
	/// <summary>Which event this is.</summary>
	public required EModifierEvent Event { get; init; }

	/// <summary>The entity that raised the event, if any.</summary>
	public CBaseEntity? Caster { get; init; }

	/// <summary>The target of the event, if any.</summary>
	public CBaseEntity? Target { get; init; }

	/// <summary>The entity spawned by the event (projectile, bullet, ...), if any. Meaning depends on <see cref="Event"/>.</summary>
	public CBaseEntity? CastEntity { get; init; }

	/// <summary>
	/// Raw pointer to the event-specific payload. Only valid inside the callback.
	/// Use <see cref="Damage"/> or <see cref="Ability"/> when available, or <see cref="Read{T}"/> if you know the layout.
	/// </summary>
	public required nint EventData { get; init; }

	/// <summary>Damage details for <c>PreDamageTaken</c>, <c>DamageTaken</c> and <c>HealthTaken</c>; null for other events.</summary>
	public ModifierDamageEventData? Damage => Event is EModifierEvent.PreDamageTaken or EModifierEvent.DamageTaken or EModifierEvent.HealthTaken
		? new ModifierDamageEventData(Event, EventData)
		: null;

	/// <summary>Ability details for <c>AbilityCastStarted</c>, <c>AbilityPreExecuted</c> and <c>AbilityExecuted</c>; null for other events.</summary>
	public ModifierAbilityEventData? Ability => Event is EModifierEvent.AbilityCastStarted or EModifierEvent.AbilityPreExecuted or EModifierEvent.AbilityExecuted
		? new ModifierAbilityEventData(EventData)
		: null;

	/// <summary>Reads a value at <paramref name="offset"/> bytes into <see cref="EventData"/>. Unchecked.</summary>
	public unsafe T Read<T>(int offset) where T : unmanaged => Unsafe.ReadUnaligned<T>((void*)(EventData + offset));

	/// <summary>Reads an entity handle at <paramref name="offset"/> bytes into <see cref="EventData"/>, or null if invalid.</summary>
	public CBaseEntity? ReadEntity(int offset) => CBaseEntity.FromHandle(Read<uint>(offset));
}

/// <summary>Payload of the damage events. Properties not carried by the current event return null or 0.</summary>
public readonly struct ModifierDamageEventData {
	// Native layouts:
	//   PreDamageTaken: { CHandle hVictim; CHandle hAttacker (invalid); CTakeDamageInfo* pInfo; }
	//   DamageTaken:    { CHandle hVictim; CHandle hAttacker; CTakeDamageInfo* pInfo; CTakeDamageResult* pResult; int nDamageType; }
	//   HealthTaken:    { CHandle hEntity; float flRequested; int nApplied; }
	private readonly EModifierEvent _event;
	private readonly nint _data;

	internal ModifierDamageEventData(EModifierEvent ev, nint data) {
		_event = ev;
		_data = data;
	}

	private unsafe T Read<T>(int offset) where T : unmanaged => Unsafe.ReadUnaligned<T>((void*)(_data + offset));

	/// <summary>The entity taking damage or receiving health.</summary>
	public CBaseEntity? Victim => CBaseEntity.FromHandle(Read<uint>(0));

	/// <summary>The attacker. <c>DamageTaken</c> only.</summary>
	public CBaseEntity? Attacker => _event == EModifierEvent.DamageTaken ? CBaseEntity.FromHandle(Read<uint>(4)) : null;

	/// <summary>The damage info. <c>PreDamageTaken</c> and <c>DamageTaken</c> only; valid inside the callback.</summary>
	public CTakeDamageInfo? Info => _event is EModifierEvent.PreDamageTaken or EModifierEvent.DamageTaken
		? CTakeDamageInfo.FromExisting(Read<nint>(8))
		: null;

	private static readonly SchemaAccessor<int> _healthLost = new("CTakeDamageResult"u8, "m_nHealthLost"u8);
	private static readonly SchemaAccessor<int> _healthBefore = new("CTakeDamageResult"u8, "m_nHealthBefore"u8);
	private static readonly SchemaAccessor<int> _damageDealt = new("CTakeDamageResult"u8, "m_nDamageDealt"u8);
	private static readonly SchemaAccessor<float> _preModifiedDamage = new("CTakeDamageResult"u8, "m_flPreModifiedDamage"u8);

	private T ReadResult<T>(SchemaAccessor<T> field) where T : unmanaged {
		if (_event != EModifierEvent.DamageTaken) return default;
		nint result = Read<nint>(16);
		return result != 0 ? field.Get(result) : default;
	}

	/// <summary>Damage dealt after mitigation. <c>DamageTaken</c> only.</summary>
	public int DamageDealt => ReadResult(_damageDealt);

	/// <summary>Health the victim actually lost. <c>DamageTaken</c> only.</summary>
	public int HealthLost => ReadResult(_healthLost);

	/// <summary>Victim's health before the damage was applied. <c>DamageTaken</c> only.</summary>
	public int HealthBefore => ReadResult(_healthBefore);

	/// <summary>Damage before modifiers were applied. <c>DamageTaken</c> only.</summary>
	public float PreModifiedDamage => ReadResult(_preModifiedDamage);

	/// <summary>Damage type bits. <c>DamageTaken</c> only.</summary>
	public int DamageType => _event == EModifierEvent.DamageTaken ? Read<int>(24) : 0;

	/// <summary>Health requested. <c>HealthTaken</c> only.</summary>
	public float HealthRequested => _event == EModifierEvent.HealthTaken ? Read<float>(4) : 0f;

	/// <summary>Health actually applied after clamping to max health. <c>HealthTaken</c> only.</summary>
	public int HealthApplied => _event == EModifierEvent.HealthTaken ? Read<int>(8) : 0;
}

/// <summary>Payload of the ability events.</summary>
public readonly struct ModifierAbilityEventData {
	// Native layout: { CHandle hCaster; CHandle hAbility; CHandle hTarget; }
	private readonly nint _data;

	internal ModifierAbilityEventData(nint data) => _data = data;

	private unsafe T Read<T>(int offset) where T : unmanaged => Unsafe.ReadUnaligned<T>((void*)(_data + offset));

	/// <summary>The entity casting the ability.</summary>
	public CBaseEntity? Caster => CBaseEntity.FromHandle(Read<uint>(0));

	/// <summary>The ability being cast.</summary>
	public CCitadelBaseAbility? Ability => CBaseEntity.FromHandle<CCitadelBaseAbility>(Read<uint>(4));

	/// <summary>The ability's target, if it has one.</summary>
	public CBaseEntity? Target => CBaseEntity.FromHandle(Read<uint>(8));
}
