namespace DeadworksManaged.Api;

/// <summary>Wraps a CCitadelBaseAbility — the base class for all Deadlock hero abilities and items. Provides upgrade state, cooldowns, charges, and cast state.</summary>
[NativeClass("CCitadelBaseAbility")]
public unsafe class CCitadelBaseAbility : CBaseEntity {
	internal CCitadelBaseAbility(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CCitadelBaseAbility"u8;

	// --- Upgrade / Lock State ---

	private static readonly SchemaAccessor<int> _upgradeBits = new(Class, "m_nUpgradeBits"u8);
	/// <summary>Bitmask of upgrade states. Each bit represents an upgrade tier (bit 0 = T1, bit 1 = T2, bit 2 = T3).</summary>
	public int UpgradeBits { get => _upgradeBits.Get(Handle); set => _upgradeBits.Set(Handle, value); }

	/// <summary>Returns the number of upgrades applied (popcount of UpgradeBits).</summary>
	public int UpgradeCount => System.Numerics.BitOperations.PopCount((uint)UpgradeBits);

	/// <summary>Checks if a specific upgrade tier is unlocked (0-indexed).</summary>
	public bool HasUpgrade(int tier) => (UpgradeBits & (1 << tier)) != 0;

	/// <summary>Sets or clears a specific upgrade tier (0-indexed).</summary>
	public void SetUpgrade(int tier, bool unlocked) {
		int bits = UpgradeBits;
		int updated = unlocked ? (bits | (1 << tier)) : (bits & ~(1 << tier));
		if (bits != updated)
			UpgradeBits = updated;
	}

	private static readonly SchemaAccessor<bool> _canBeUpgraded = new(Class, "m_bCanBeUpgraded"u8);
	/// <summary>Whether this ability can accept upgrades (effectively the unlock state for purchasing upgrades).</summary>
	public bool CanBeUpgraded { get => _canBeUpgraded.Get(Handle); set => _canBeUpgraded.Set(Handle, value); }

	// --- Slot / Bucket ---

	private static readonly SchemaAccessor<short> _abilitySlot = new(Class, "m_eAbilitySlot"u8);
	/// <summary>The slot this ability occupies (EAbilitySlots_t, 2 bytes).</summary>
	public EAbilitySlot AbilitySlot => (EAbilitySlot)_abilitySlot.Get(Handle);

	private static readonly SchemaAccessor<int> _bucketID = new(Class, "m_iBucketID"u8);
	/// <summary>The bucket type for this ability (EAbilityBucketType).</summary>
	public int BucketID { get => _bucketID.Get(Handle); set => _bucketID.Set(Handle, value); }

	// --- Cooldown ---

	private static readonly SchemaAccessor<bool> _isCoolingDownInternal = new(Class, "m_bIsCoolingDownInternal"u8);
	/// <summary>Whether the ability is internally tracking a cooldown (server-only, not networked).</summary>
	public bool IsCoolingDownInternal { get => _isCoolingDownInternal.Get(Handle); set => _isCoolingDownInternal.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _cooldownStart = new(Class, "m_flCooldownStart"u8);
	/// <summary>Game time when the cooldown started.</summary>
	public float CooldownStart { get => _cooldownStart.Get(Handle); set => _cooldownStart.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _cooldownEnd = new(Class, "m_flCooldownEnd"u8);
	/// <summary>Game time when the cooldown ends.</summary>
	public float CooldownEnd { get => _cooldownEnd.Get(Handle); set => _cooldownEnd.Set(Handle, value); }

	/// <summary>Returns the remaining cooldown in seconds, or 0 if off cooldown.</summary>
	public float GetCooldownRemaining(float currentGameTime) {
		float remaining = CooldownEnd - currentGameTime;
		return remaining > 0 ? remaining : 0;
	}

	/// <summary>Returns true if this ability is currently on cooldown.</summary>
	public bool IsOnCooldown(float currentGameTime) => CooldownEnd > currentGameTime;

	/// <summary>Resets the cooldown by setting CooldownEnd to 0.</summary>
	public void ResetCooldown() {
		CooldownEnd = 0;
		CooldownStart = 0;
		IsCoolingDownInternal = false;
	}

	// --- Charges ---

	private static readonly SchemaAccessor<int> _remainingCharges = new(Class, "m_iRemainingCharges"u8);
	/// <summary>The number of remaining charges for this ability.</summary>
	public int RemainingCharges { get => _remainingCharges.Get(Handle); set => _remainingCharges.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _chargeRechargeStart = new(Class, "m_flChargeRechargeStart"u8);
	/// <summary>Game time when the current charge recharge started.</summary>
	public float ChargeRechargeStart { get => _chargeRechargeStart.Get(Handle); set => _chargeRechargeStart.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _chargeRechargeEnd = new(Class, "m_flChargeRechargeEnd"u8);
	/// <summary>Game time when the current charge recharge ends.</summary>
	public float ChargeRechargeEnd { get => _chargeRechargeEnd.Get(Handle); set => _chargeRechargeEnd.Set(Handle, value); }

	// --- Cast State ---

	private static readonly SchemaAccessor<bool> _channeling = new(Class, "m_bChanneling"u8);
	/// <summary>Whether this ability is currently being channeled.</summary>
	public bool IsChanneling => _channeling.Get(Handle);

	private static readonly SchemaAccessor<bool> _inCastDelay = new(Class, "m_bInCastDelay"u8);
	/// <summary>Whether this ability is in its cast delay (wind-up) phase.</summary>
	public bool IsInCastDelay => _inCastDelay.Get(Handle);

	private static readonly SchemaAccessor<bool> _shouldBeExecuted = new(Class, "m_bShouldBeExecuted"u8);
	/// <summary>Whether this ability is queued for execution.</summary>
	public bool ShouldBeExecuted => _shouldBeExecuted.Get(Handle);

	private static readonly SchemaAccessor<bool> _toggleState = new(Class, "m_bToggleState"u8);
	/// <summary>The toggle state for toggle abilities (on/off).</summary>
	public bool ToggleState { get => _toggleState.Get(Handle); set => _toggleState.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _castCompletedTime = new(Class, "m_flCastCompletedTime"u8);
	/// <summary>Game time when the last cast completed.</summary>
	public float CastCompletedTime => _castCompletedTime.Get(Handle);

	private static readonly SchemaAccessor<float> _channelStartTime = new(Class, "m_flChannelStartTime"u8);
	/// <summary>Game time when channeling started.</summary>
	public float ChannelStartTime => _channelStartTime.Get(Handle);

	private static readonly SchemaAccessor<float> _castDelayStartTime = new(Class, "m_flCastDelayStartTime"u8);
	/// <summary>Game time when the cast delay started.</summary>
	public float CastDelayStartTime => _castDelayStartTime.Get(Handle);

	private static readonly SchemaAccessor<float> _postCastDelayEndTime = new(Class, "m_flPostCastDelayEndTime"u8);
	/// <summary>Game time when the post-cast delay ends.</summary>
	public float PostCastDelayEndTime => _postCastDelayEndTime.Get(Handle);

	private static readonly SchemaAccessor<float> _movementControlActiveTime = new(Class, "m_flMovementControlActiveTime"u8);
	/// <summary>Game time when movement control became active for this ability.</summary>
	public float MovementControlActiveTime => _movementControlActiveTime.Get(Handle);

	// --- Selection ---

	private static readonly SchemaAccessor<float> _selectedChangedTime = new(Class, "m_flSelectedChangedTime"u8);
	/// <summary>Game time when this ability's selection state last changed.</summary>
	public float SelectedChangedTime => _selectedChangedTime.Get(Handle);

	private static readonly SchemaAccessor<bool> _selectionModeIsAltMode = new(Class, "m_bSelectionModeIsAltMode"u8);
	/// <summary>Whether the ability is in alt-cast selection mode.</summary>
	public bool SelectionModeIsAltMode => _selectionModeIsAltMode.Get(Handle);

	private static readonly SchemaAccessor<float> _altCastHoldStartTime = new(Class, "m_flAltCastHoldStartTime"u8);
	/// <summary>Game time when alt-cast hold started.</summary>
	public float AltCastHoldStartTime => _altCastHoldStartTime.Get(Handle);

	private static readonly SchemaAccessor<float> _altCastDoubleTapStartTime = new(Class, "m_flAltCastDoubleTapStartTime"u8);
	/// <summary>Game time when alt-cast double-tap started.</summary>
	public float AltCastDoubleTapStartTime => _altCastDoubleTapStartTime.Get(Handle);

	// --- Imbue ---

	private static readonly SchemaAccessor<bool> _canBeImbued = new(Class, "m_bCanBeImbued"u8);
	/// <summary>Whether this ability can be imbued with another ability.</summary>
	public bool CanBeImbued { get => _canBeImbued.Get(Handle); set => _canBeImbued.Set(Handle, value); }
}
