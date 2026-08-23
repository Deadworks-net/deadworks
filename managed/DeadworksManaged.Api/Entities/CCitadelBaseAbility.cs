namespace DeadworksManaged.Api;

/// <summary>Base class for all Deadlock abilities (hero abilities, items, innates, etc.).</summary>
[NativeClass("CCitadelBaseAbility")]
public unsafe class CCitadelBaseAbility : CBaseEntity {
	internal CCitadelBaseAbility(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CCitadelBaseAbility"u8;

	private static readonly SchemaAccessor<short> _abilitySlot = new(Class, "m_eAbilitySlot"u8);
	private static readonly SchemaAccessor<bool> _channeling = new(Class, "m_bChanneling"u8);
	private static readonly SchemaAccessor<bool> _canBeUpgraded = new(Class, "m_bCanBeUpgraded"u8);
	private static readonly SchemaAccessor<bool> _toggleState = new(Class, "m_bToggleState"u8);
	private static readonly SchemaAccessor<float> _cooldownEnd = new(Class, "m_flCooldownEnd"u8);
	private static readonly SchemaAccessor<float> _cooldownStart = new(Class, "m_flCooldownStart"u8);
	private static readonly SchemaAccessor<byte> _vecImbuedAbilities = new(Class, "m_vecImbuedAbilities"u8);

	private static int UpgradeBitsOffset => _abilitySlot.Offset - 0x20;

	public int UpgradeBits {
		get => *(short*)((byte*)Handle + UpgradeBitsOffset + 2);
		set => NativeInterop.SetUpgradeBits((void*)Handle, value);
	}
	public EAbilitySlot AbilitySlot => (EAbilitySlot)_abilitySlot.Get(Handle);
	public bool IsChanneling => _channeling.Get(Handle);
	public bool CanBeUpgraded { get => _canBeUpgraded.Get(Handle); set => _canBeUpgraded.Set(Handle, value); }
	public bool ToggleState => _toggleState.Get(Handle);
	public float CooldownEnd { get => _cooldownEnd.Get(Handle); set => _cooldownEnd.Set(Handle, value); }
	public float CooldownStart { get => _cooldownStart.Get(Handle); set => _cooldownStart.Set(Handle, value); }
	public bool IsUnlocked => (UpgradeBits & 1) != 0;

	public bool IsSignature => AbilitySlot >= EAbilitySlot.Signature1 && AbilitySlot <= EAbilitySlot.Signature4;
	public bool IsActiveItem => AbilitySlot >= EAbilitySlot.ActiveItem1 && AbilitySlot <= EAbilitySlot.ActiveItem4;
	public bool IsInnate => AbilitySlot >= EAbilitySlot.Innate1 && AbilitySlot <= EAbilitySlot.Innate3;
	public bool IsWeapon => AbilitySlot >= EAbilitySlot.WeaponSecondary && AbilitySlot <= EAbilitySlot.WeaponMelee;
	public bool IsItem => (SubclassVData?.Name ?? "").StartsWith("upgrade_");
	public string AbilityName => SubclassVData?.Name ?? "";

	/// <summary>
	/// True when this is an item that has to be imbued into one of the hero's abilities.
	/// See <see cref="ImbuedAbilities"/> for what it is actually imbued into.
	/// </summary>
	public bool CanBeImbued => ItemInfo.CanBeImbued(AbilityName);

	/// <summary>
	/// Subclass IDs of the abilities this item is imbued into (<c>m_vecImbuedAbilities</c>).
	/// Empty for abilities and for items that have not been imbued.
	/// </summary>
	public IReadOnlyList<uint> ImbuedAbilityIds {
		get {
			nint vecAddr = _vecImbuedAbilities.GetAddress(Handle);
			int count = NativeInterop.GetUtlVectorSize((void*)vecAddr);
			uint* data = (uint*)NativeInterop.GetUtlVectorData((void*)vecAddr);
			if (data == null || count <= 0) return Array.Empty<uint>();

			var ids = new uint[count];
			for (int i = 0; i < count; i++) ids[i] = data[i];
			return ids;
		}
	}

	/// <summary>Internal names of the abilities this item is imbued into (e.g. "citadel_ability_shiv_dash").</summary>
	public IReadOnlyList<string> ImbuedAbilities {
		get {
			var ids = ImbuedAbilityIds;
			if (ids.Count == 0) return Array.Empty<string>();

			var names = new List<string>(ids.Count);
			foreach (uint id in ids) {
				// 4 == SUBCLASS_SCOPE_ABILITIES
				void* vdata = NativeInterop.LookupVDataByHash(4, id);
				if (vdata != null) names.Add(new CEntitySubclassVDataBase((nint)vdata).Name);
			}
			return names;
		}
	}

	/// <summary>True when this item is imbued into at least one ability.</summary>
	public bool IsImbued => ImbuedAbilityIds.Count > 0;

	/// <summary>
	/// True when the item named <paramref name="itemName"/> may be imbued into this ability.
	/// Runs the game's own pairing check, so it rejects e.g. an
	/// <see cref="ImbueEffects.ActiveNonUltimate"/> item aimed at an ultimate.
	/// </summary>
	public bool CanBeImbuedBy(string itemName) {
		Span<byte> utf8 = Utf8.Encode(itemName, stackalloc byte[Utf8.Size(itemName)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.CanImbueAbility((void*)Handle, ptr) != 0;
		}
	}
}
