using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Base player controller entity. Manages the link between a player slot and their pawn.</summary>
[NativeClass("CBasePlayerController")]
public unsafe class CBasePlayerController : CBaseEntity {
	internal CBasePlayerController(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _playerName = new("CBasePlayerController"u8, "m_iszPlayerName"u8);

	/// <summary>The player's display name (char[128] inline buffer).</summary>
	public string PlayerName {
		get => Marshal.PtrToStringUTF8(_playerName.GetAddress(Handle)) ?? "";
		set {
			nint addr = _playerName.GetAddress(Handle);
			Span<byte> utf8 = Utf8.Encode(value, stackalloc byte[Utf8.Size(value)]);
			int len = Math.Min(utf8.Length, 127);
			fixed (byte* src = utf8) {
				Buffer.MemoryCopy(src, (void*)addr, 128, len);
			}
			((byte*)addr)[len] = 0;
			NativeInterop.NotifyStateChanged((void*)Handle, _playerName.Offset, _playerName.ChainOffset, 0);
		}
	}

	/// <summary>Assigns a new pawn to this controller, optionally transferring team and movement state.</summary>
	public void SetPawn(CBasePlayerPawn? pawn, bool retainOldPawnTeam = false, bool copyMovementState = false, bool allowTeamMismatch = false, bool preserveMovementState = false) {
		NativeInterop.SetPawn((void*)Handle, pawn != null ? (void*)pawn.Handle : null,
			retainOldPawnTeam ? (byte)1 : (byte)0,
			copyMovementState ? (byte)1 : (byte)0,
			allowTeamMismatch ? (byte)1 : (byte)0,
			preserveMovementState ? (byte)1 : (byte)0);
	}
}

/// <summary>Deadlock-specific player controller. Provides access to player data, hero selection, team changes, and console messaging.</summary>
[NativeClass("CCitadelPlayerController")]
public sealed unsafe class CCitadelPlayerController : CBasePlayerController {
	internal CCitadelPlayerController(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _playerDataGlobal = new("CCitadelPlayerController"u8, "m_PlayerDataGlobal"u8);
	public PlayerDataGlobal PlayerDataGlobal => new(_playerDataGlobal.GetAddress(Handle));

	/// <summary>Returns the player's current hero pawn, or null if they have none.</summary>
	public CCitadelPlayerPawn? GetHeroPawn() {
		var ptr = NativeInterop.GetHeroPawn((void*)Handle);
		return ptr != null ? new CCitadelPlayerPawn((nint)ptr) : null;
	}

	/// <summary>Moves this player to the specified team.</summary>
	public void ChangeTeam(int teamNum) {
		NativeInterop.ChangeTeam((void*)Handle, teamNum);
	}

	/// <summary>Forces the player to select the specified hero.</summary>
	public void SelectHero(Heroes hero) {
		var name = hero.ToHeroName();
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.SelectHero((void*)Handle, ptr);
		}
	}

	/// <summary>Sends a message to this player's console via "echo" client command.</summary>
	public void PrintToConsole(string message) {
		Server.ClientCommand(EntityIndex, $"echo {message}");
	}

	/// <summary>Sends a message to all connected players' consoles.</summary>
	public static void PrintToConsoleAll(string message) {
		foreach (var player in Players.GetAll())
			player.PrintToConsole(message);
	}

	/// <summary>Executes a client concommand directly on the server for this controller (synchronous, bypasses client).</summary>
	public void ServerCommand(string command) {
		Span<byte> utf8 = Utf8.Encode(command, stackalloc byte[Utf8.Size(command)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.ServerControllerCommand((void*)Handle, ptr);
		}
	}
}

/// <summary>Base player pawn entity. Provides access to the owning controller.</summary>
[NativeClass("CBasePlayerPawn")]
public unsafe class CBasePlayerPawn : CBaseEntity {
	internal CBasePlayerPawn(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<uint> _hController = new("CBasePlayerPawn"u8, "m_hController"u8);
	public CBasePlayerController? Controller {
		get {
			uint handle = _hController.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CBasePlayerController((nint)ptr) : null;
		}
	}
}

/// <summary>
/// Wraps CModifierProperty — manages modifier state bits on an entity.
/// Uses CNetworkVarChainer to chain network notifications to the owning entity.
/// </summary>
public unsafe class CModifierProperty : NativeEntity {
	internal CModifierProperty(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CModifierProperty"u8;

	private static readonly SchemaArrayAccessor<uint> _enabledStateMask = new(Class, "m_bvEnabledStateMask"u8);

	/// <summary>Sets or clears the specified modifier state bit on this entity, notifying the network if changed.</summary>
	public void SetModifierState(EModifierState state, bool enabled) {
		int s = (int)state;
		int index = s >> 5;
		uint bit = 1u << (s & 0x1F);
		uint current = _enabledStateMask.Get(Handle, index);
		uint updated = enabled ? (current | bit) : (current & ~bit);
		if (current != updated)
			_enabledStateMask.Set(Handle, index, updated);
	}

	/// <summary>Returns true if the specified modifier state bit is currently set on this entity.</summary>
	public bool HasModifierState(EModifierState state) {
		int s = (int)state;
		int index = s >> 5;
		uint bit = 1u << (s & 0x1F);
		return (_enabledStateMask.Get(Handle, index) & bit) != 0;
	}
}

/// <summary>
/// Wraps AbilityResource_t — stamina or ability resource with latch-based networking.
/// This is an embedded struct, not an entity — setters use raw pointer writes
/// since NotifyStateChanged requires the owning entity, not the struct address.
/// </summary>
public unsafe class AbilityResource : NativeEntity {
	private static ReadOnlySpan<byte> Class => "AbilityResource_t"u8;

	internal AbilityResource(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<float> _currentValue = new(Class, "m_flCurrentValue"u8);
	public float CurrentValue { get => _currentValue.Get(Handle); set => *(float*)_currentValue.GetAddress(Handle) = value; }

	private static readonly SchemaAccessor<float> _prevRegenRate = new(Class, "m_flPrevRegenRate"u8);
	public float PrevRegenRate { get => _prevRegenRate.Get(Handle); set => *(float*)_prevRegenRate.GetAddress(Handle) = value; }

	private static readonly SchemaAccessor<float> _maxValue = new(Class, "m_flMaxValue"u8);
	public float MaxValue { get => _maxValue.Get(Handle); set => *(float*)_maxValue.GetAddress(Handle) = value; }

	private static readonly SchemaAccessor<float> _latchTime = new(Class, "m_flLatchTime"u8);
	public float LatchTime { get => _latchTime.Get(Handle); set => *(float*)_latchTime.GetAddress(Handle) = value; }

	private static readonly SchemaAccessor<float> _latchValue = new(Class, "m_flLatchValue"u8);
	public float LatchValue { get => _latchValue.Get(Handle); set => *(float*)_latchValue.GetAddress(Handle) = value; }
}

/// <summary>Ability component on a player pawn. Provides access to stamina/ability resources and the list of equipped abilities.</summary>
public unsafe class CCitadelAbilityComponent : NativeEntity {
	internal CCitadelAbilityComponent(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _resourceStamina = new("CCitadelAbilityComponent"u8, "m_ResourceStamina"u8);
	public AbilityResource ResourceStamina => new(_resourceStamina.GetAddress(Handle));

	private static readonly SchemaAccessor<byte> _resourceAbility = new("CCitadelAbilityComponent"u8, "m_ResourceAbility"u8);
	public AbilityResource ResourceAbility => new(_resourceAbility.GetAddress(Handle));

	private static readonly SchemaAccessor<byte> _vecAbilities = new("CCitadelAbilityComponent"u8, "m_vecAbilities"u8);

	public IReadOnlyList<CBaseEntity> Abilities {
		get {
			var result = new List<CBaseEntity>();
			nint vecAddr = _vecAbilities.GetAddress(Handle);
			int count = NativeInterop.GetUtlVectorSize((void*)vecAddr);
			uint* data = (uint*)NativeInterop.GetUtlVectorData((void*)vecAddr);
			if (data == null || count <= 0) return result;
			for (int i = 0; i < count; i++) {
				void* ent = NativeInterop.GetEntityFromHandle(data[i]);
				if (ent != null)
					result.Add(new CBaseEntity((nint)ent));
			}
			return result;
		}
	}

	/// <summary>Executes the ability in the given slot. Returns the execution result (0 = success, negative = error).</summary>
	/// <param name="slot">The ability slot to execute.</param>
	/// <param name="altCast">If true, uses alternate cast mode.</param>
	/// <param name="flags">Execution flags passed to the ability system.</param>
	public int ExecuteAbilityBySlot(EAbilitySlot slot, bool altCast = false, byte flags = 0) {
		return NativeInterop.ExecuteAbilityBySlot((void*)Handle, (short)slot, altCast ? (byte)1 : (byte)0, flags);
	}

	/// <summary>Executes an ability by its runtime ability ID. Returns the execution result (0 = success, negative = error).</summary>
	public int ExecuteAbilityByID(int abilityID, bool altCast = false, byte flags = 0) {
		return NativeInterop.ExecuteAbilityByID((void*)Handle, abilityID, altCast ? (byte)1 : (byte)0, flags);
	}

	/// <summary>Executes a specific ability entity. Returns the execution result (0 = success, negative = error).</summary>
	public int ExecuteAbility(CBaseEntity ability, bool altCast = false, byte flags = 0) {
		return NativeInterop.ExecuteAbility((void*)Handle, (void*)ability.Handle, altCast ? (byte)1 : (byte)0, flags);
	}

	/// <summary>Gets the ability entity in the given slot, or null if no ability occupies that slot.</summary>
	public CBaseEntity? GetAbilityBySlot(EAbilitySlot slot) {
		void* result = NativeInterop.GetAbilityBySlot((void*)Handle, (short)slot);
		return result != null ? new CBaseEntity((nint)result) : null;
	}

	/// <summary>Activates or deactivates an ability (toggle). This is the actual activation path for most abilities.</summary>
	public void ToggleActivate(CBaseEntity ability, bool activate = true) {
		NativeInterop.ToggleActivate((void*)ability.Handle, activate ? (byte)1 : (byte)0);
	}
}

/// <summary>Jump ability entity tracking air jump/wall jump counters for a hero.</summary>
[NativeClass("CCitadel_Ability_Jump")]
public sealed unsafe class CCitadel_Ability_Jump : CBaseEntity {
	internal CCitadel_Ability_Jump(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CCitadel_Ability_Jump"u8;

	private static readonly SchemaAccessor<int> _desiredAirJumpCount = new(Class, "m_nDesiredAirJumpCount"u8);
	public int DesiredAirJumpCount { get => _desiredAirJumpCount.Get(Handle); set => _desiredAirJumpCount.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _executedAirJumpCount = new(Class, "m_nExecutedAirJumpCount"u8);
	public int ExecutedAirJumpCount { get => _executedAirJumpCount.Get(Handle); set => _executedAirJumpCount.Set(Handle, value); }

	private static readonly SchemaAccessor<sbyte> _consecutiveAirJumps = new(Class, "m_nConsecutiveAirJumps"u8);
	public sbyte ConsecutiveAirJumps { get => _consecutiveAirJumps.Get(Handle); set => _consecutiveAirJumps.Set(Handle, value); }

	private static readonly SchemaAccessor<sbyte> _consecutiveWallJumps = new(Class, "m_nConsecutiveWallJumps"u8);
	public sbyte ConsecutiveWallJumps { get => _consecutiveWallJumps.Get(Handle); set => _consecutiveWallJumps.Set(Handle, value); }
}

/// <summary>Dash ability entity tracking consecutive air/down dash counters for a hero.</summary>
[NativeClass("CCitadel_Ability_Dash")]
public sealed unsafe class CCitadel_Ability_Dash : CBaseEntity {
	internal CCitadel_Ability_Dash(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CCitadel_Ability_Dash"u8;

	private static readonly SchemaAccessor<sbyte> _consecutiveAirDashes = new(Class, "m_nConsecutiveAirDashes"u8);
	public sbyte ConsecutiveAirDashes { get => _consecutiveAirDashes.Get(Handle); set => _consecutiveAirDashes.Set(Handle, value); }

	private static readonly SchemaAccessor<sbyte> _consecutiveDownDashes = new(Class, "m_nConsecutiveDownDashes"u8);
	public sbyte ConsecutiveDownDashes { get => _consecutiveDownDashes.Get(Handle); set => _consecutiveDownDashes.Set(Handle, value); }
}

/// <summary>Deadlock hero pawn. The in-game physical representation of a player. Provides currency, abilities, movement state, stamina, and eye angles.</summary>
[NativeClass("CCitadelPlayerPawn")]
public sealed unsafe class CCitadelPlayerPawn : CBasePlayerPawn {
	internal CCitadelPlayerPawn(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<uint> _hCitadelController = new("CBasePlayerPawn"u8, "m_hController"u8);
	public new CCitadelPlayerController? Controller {
		get {
			uint handle = _hCitadelController.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
		}
	}

	public PlayerDataGlobal? PlayerData => Controller?.PlayerDataGlobal;

	private static readonly SchemaAccessor<byte> _abilityComp = new("CCitadelPlayerPawn"u8, "m_CCitadelAbilityComponent"u8);
	public CCitadelAbilityComponent AbilityComponent => new(_abilityComp.GetAddress(Handle));

	private static readonly SchemaAccessor<bool> _inRegenZone = new("CCitadelPlayerPawn"u8, "m_bInRegenerationZone"u8);
	public bool InRegenerationZone => _inRegenZone.Get(Handle);

	private static readonly SchemaAccessor<Vector3> _eyeAngles = new("CCitadelPlayerPawn"u8, "m_angEyeAngles"u8);
	/// <summary>Networked eye angles (quantized to 11 bits, ~0.18 precision). Use ViewAngles for raw precision.</summary>
	public Vector3 EyeAngles => _eyeAngles.Get(Handle);

	private static readonly SchemaAccessor<byte> _viewOffset = new("CBaseModelEntity"u8, "m_vecViewOffset"u8);
	/// <summary>Eye position (AbsOrigin + ViewOffset). This is where the camera sits.</summary>
	public unsafe Vector3 EyePosition {
		get {
			var pos = Position;
			nint voBase = _viewOffset.GetAddress(Handle);
			// CNetworkViewOffsetVector: m_vecX at +0x10, m_vecY at +0x18, m_vecZ at +0x20
			// Each is a CNetworkedQuantizedFloat with float Value at +0x00
			float voX = *(float*)(voBase + 0x10);
			float voY = *(float*)(voBase + 0x18);
			float voZ = *(float*)(voBase + 0x20);
			return new Vector3(pos.X + voX, pos.Y + voY, pos.Z + voZ);
		}
	}

	private static readonly SchemaAccessor<Vector3> _clientCamera = new("CCitadelPlayerPawn"u8, "m_angClientCamera"u8);
	/// <summary>Client camera angles for SourceTV/spectating.</summary>
	public Vector3 CameraAngles => _clientCamera.Get(Handle);

	/// <summary>Raw server-side view angles from CUserCmd (v_angle). Full float precision, no quantization.</summary>
	public unsafe Vector3 ViewAngles {
		get {
			// v_angle is at offset 0xC48 in CBasePlayerPawn (not networked, server-only)
			float* p = (float*)(Handle + 0xC48);
			return new Vector3(p[0], p[1], p[2]);
		}
	}

	/// <summary>Adds or removes currency from this pawn (e.g. gold, ability points). Use negative <paramref name="amount"/> to spend.</summary>
	public void ModifyCurrency(ECurrencyType type, int amount, ECurrencySource source,
								bool silent = false, bool forceGain = false, bool spendOnly = false) {
		NativeInterop.ModifyCurrency((void*)Handle, (uint)type, amount, (uint)source,
									  silent ? (byte)1 : (byte)0, spendOnly ? (byte)1 : (byte)0, forceGain ? (byte)1 : (byte)0,
									  (void*)0, (void*)0);
	}

	/// <summary>
	/// Full pawn-level hero reset: clears loadout, removes items, re-adds starting abilities from VData, resets level.
	/// </summary>
	public void ResetHero(bool resetAbilities = true) {
		if (NativeInterop.ResetHero != null)
			NativeInterop.ResetHero((void*)Handle, resetAbilities ? (byte)1 : (byte)0);
	}

	/// <summary>Removes an ability from this pawn by internal ability name. Returns true on success.</summary>
	public bool RemoveAbility(string abilityName) {
		Span<byte> utf8 = Utf8.Encode(abilityName, stackalloc byte[Utf8.Size(abilityName)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.RemoveAbility((void*)Handle, ptr) != 0;
		}
	}

	/// <summary>Adds an ability to this pawn by internal ability name into the given slot. Returns the new ability entity, or null on failure.
	/// <param name="upgradeLevel">Upgrade level: -1 = locked (needs unlock channel), 0+ = unlocked at that upgrade tier.</param></summary>
	public CBaseEntity? AddAbility(string abilityName, ushort slot, int upgradeLevel = 0) {
		Span<byte> utf8 = Utf8.Encode(abilityName, stackalloc byte[Utf8.Size(abilityName)]);
		fixed (byte* ptr = utf8) {
			void* result = NativeInterop.AddAbility((void*)Handle, ptr, slot, upgradeLevel);
			return result != null ? new CBaseEntity((nint)result) : null;
		}
	}

	/// <summary>
	/// Gives an item to this pawn by internal item name (e.g. "upgrade_sprint_booster").
	/// <param name="itemName">Internal item name.</param>
	/// <param name="upgradeTier">Upgrade tier (0-based). Pass -1 for the base version.
	/// Items with the same name can exist at different upgrade tiers — this controls which version is created.</param>
	/// Returns the new item entity, or null on failure.
	/// </summary>
	public CBaseEntity? AddItem(string itemName, int upgradeTier = -1) {
		Span<byte> utf8 = Utf8.Encode(itemName, stackalloc byte[Utf8.Size(itemName)]);
		fixed (byte* ptr = utf8) {
			void* result = NativeInterop.AddItem((void*)Handle, ptr, upgradeTier);
			return result != null ? new CBaseEntity((nint)result) : null;
		}
	}


	/// <summary>
	/// Removes an item from this pawn by name, using the ability removal path.
	/// This bypasses sell checks and does not refund gold — it directly removes the item entity
	/// from the ability component, slot table, and network state.
	/// Returns true on success.
	/// </summary>
	public bool RemoveItem(string itemName) {
		return RemoveAbility(itemName);
	}

	/// <summary>Executes the ability in the given slot on this pawn's ability component.</summary>
	public int ExecuteAbilityBySlot(EAbilitySlot slot, bool altCast = false, byte flags = 0) {
		return AbilityComponent.ExecuteAbilityBySlot(slot, altCast, flags);
	}

	/// <summary>Executes an ability by its runtime ability ID on this pawn's ability component.</summary>
	public int ExecuteAbilityByID(int abilityID, bool altCast = false, byte flags = 0) {
		return AbilityComponent.ExecuteAbilityByID(abilityID, altCast, flags);
	}

	/// <summary>Executes a specific ability entity on this pawn's ability component.</summary>
	public int ExecuteAbility(CBaseEntity ability, bool altCast = false, byte flags = 0) {
		return AbilityComponent.ExecuteAbility(ability, altCast, flags);
	}

	/// <summary>Gets the ability entity in the given slot from this pawn's ability component.</summary>
	public CBaseEntity? GetAbilityBySlot(EAbilitySlot slot) {
		return AbilityComponent.GetAbilityBySlot(slot);
	}

	/// <summary>Activates or deactivates an ability on this pawn (toggle). This is the actual activation path for most abilities.</summary>
	public void ToggleActivate(CBaseEntity ability, bool activate = true) {
		AbilityComponent.ToggleActivate(ability, activate);
	}

	/// <summary>
	/// Sells an item from this pawn by internal item name.
	/// This always refunds gold (at normal or full sell price) and will fail for items that cannot be sold.
	/// <param name="itemName">Internal item name (e.g. "upgrade_sprint_booster").</param>
	/// <param name="fullRefund">If true, skips partial sell-back tracking (item treated as fully refunded).</param>
	/// <param name="forceSellPrice">If true, forces the item to sell at full sell price even if conditions aren't met.</param>
	/// Returns true on success, false if the item was not found or cannot be sold.
	/// </summary>
	public bool SellItem(string itemName, bool fullRefund = false, bool forceSellPrice = false) {
		Span<byte> utf8 = Utf8.Encode(itemName, stackalloc byte[Utf8.Size(itemName)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.SellItem((void*)Handle, ptr, fullRefund ? (byte)1 : (byte)0, forceSellPrice ? (byte)1 : (byte)0) != 0;
		}
	}
}

/// <summary>Wraps the networked PlayerDataGlobal_t struct on a player controller — provides read access to stats like kills, gold, level, and damage.</summary>
public unsafe class PlayerDataGlobal : NativeEntity {
	private static ReadOnlySpan<byte> Class => "PlayerDataGlobal_t"u8;

	internal PlayerDataGlobal(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<int> _iLevel = new(Class, "m_iLevel"u8);
	public int Level => _iLevel.Get(Handle);

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

	private static readonly SchemaAccessor<bool> _bUltimateTrained = new(Class, "m_bUltimateTrained"u8);
	/// <summary>Whether the player's ultimate (sig4) has been trained/unlocked.</summary>
	public bool UltimateTrained { get => _bUltimateTrained.Get(Handle); set => _bUltimateTrained.Set(Handle, value); }
}
