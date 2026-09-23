using System.Numerics;

namespace DeadworksManaged.Api;

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

	private static readonly SchemaAccessor<byte> _heroComp = new("CCitadelPlayerPawn"u8, "m_CCitadelHeroComponent"u8);
	public CCitadelHeroComponent HeroComponent => new(_heroComp.GetAddress(Handle));

	public Heroes HeroID => (Heroes)HeroComponent.SpawnedHero.HeroID;

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
	/// <summary>Which way the player's camera is facing. Save this to restore their view later with <see cref="TeleportWithView"/>.</summary>
	public Vector3 CameraAngles => _clientCamera.Get(Handle);

	/// <summary>Raw server-side view angles from CUserCmd (v_angle). Full float precision, no quantization.</summary>
	public unsafe Vector3 ViewAngles {
		get {
			// v_angle is at offset 0xC48 in CBasePlayerPawn (not networked, server-only)
			float* p = (float*)(Handle + 0xC48);
			return new Vector3(p[0], p[1], p[2]);
		}
	}

	private static readonly SchemaAccessor<int> _level = new("CCitadelPlayerPawn"u8, "m_nLevel"u8);
	public int Level { get => _level.Get(Handle); set => _level.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _flRespawnTime = new("CCitadelPlayerPawn"u8, "m_flRespawnTime"u8);
	public float RespawnTime { get => _flRespawnTime.Get(Handle); set => _flRespawnTime.Set(Handle, value); }

	/// <summary>Modifier that pins a pawn in place. Used by <see cref="Freeze"/>.</summary>
	public const string FreezeModifier = "modifier_bookworm_immobilize";

	/// <summary>Modifier that makes a pawn untargetable and immune to damage, as inside a hideout. Used by <see cref="Freeze"/>.</summary>
	public const string SafeModifier = "modifier_citadel_in_hideout_zone";

	/// <summary>
	/// Pins this pawn in place, optionally for a limited time. The player keeps their camera and can
	/// still open menus; only movement is blocked. With <paramref name="invulnerable"/> the pawn is also
	/// made untargetable and immune to damage for the same period, which is what countdowns and
	/// cinematic holds usually want.
	/// </summary>
	/// <param name="duration">Seconds to stay frozen, or <see langword="null"/> to stay frozen until <see cref="Unfreeze"/>.</param>
	/// <param name="invulnerable">Also apply <see cref="SafeModifier"/> so the frozen player cannot be killed.</param>
	public void Freeze(float? duration = null, bool invulnerable = true) {
		using var kv = new KeyValues3();
		if (duration.HasValue)
			kv.SetFloat("duration", duration.Value);

		AddModifier(FreezeModifier, kv);
		if (invulnerable)
			AddModifier(SafeModifier, kv);
	}

	/// <summary>Removes the modifiers applied by <see cref="Freeze"/>. Safe to call when not frozen.</summary>
	public void Unfreeze() {
		RemoveModifier(FreezeModifier);
		RemoveModifier(SafeModifier);
	}

	/// <summary>True while the <see cref="Freeze"/> modifier is present.</summary>
	public bool IsFrozen => ModifierProp?.HasModifier(FreezeModifier) ?? false;

	/// <summary>
	/// Kills this pawn. With <paramref name="instantRespawn"/> the respawn timer is cleared on the next
	/// tick so the player comes straight back instead of waiting out the death timer. The timer has to
	/// be cleared a tick later because the engine assigns it during death processing, after this call
	/// returns.
	/// </summary>
	public void Kill(bool instantRespawn = false) {
		Hurt(1_000_000f);
		if (!instantRespawn) return;

		var self = this;
		TimerResolver.RunNextTick(() => {
			if (self.IsValid)
				self.RespawnTime = 0f;
		});
	}

	private static readonly SchemaArrayAccessor<int> _currencies = new("CCitadelPlayerPawn"u8, "m_nCurrencies"u8);
	public int GetCurrency(ECurrencyType type) => _currencies.Get(Handle, (int)type);
	public void SetCurrency(ECurrencyType type, int value) => _currencies.Set(Handle, (int)type, value);

	/// <summary>
	/// Moves this hero to <paramref name="position"/> and turns the player's camera to face
	/// <paramref name="angles"/>. The hero arrives standing still instead of keeping their old speed.
	/// </summary>
	/// <example>
	/// Saving a checkpoint and sending the player back to it later:
	/// <code>
	/// var savedPos = pawn.Position;
	/// var savedView = pawn.CameraAngles;
	/// // ...
	/// pawn.TeleportWithView(savedPos, savedView);
	/// </code>
	/// </example>
	public void TeleportWithView(Vector3 position, Vector3 angles) {
		Teleport(position, velocity: Vector3.Zero);
		Controller?.SetCameraAngles(angles);
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

	/// <summary>
	/// Ensures this pawn is on <paramref name="hero"/> with a fresh ability loadout.
	/// Swaps via <see cref="CCitadelPlayerController.SelectHero"/> if currently on a different hero
	/// (queues the async GC swap), or calls <see cref="ResetHero"/> if already on that hero
	/// (synchronous teardown + re-init). If <paramref name="onReady"/> is supplied it is registered
	/// via <see cref="OnceHeroInitialized"/> and runs once the new ability slots are populated
	/// server-side - synchronously inside this call for the same-hero path, or later (when
	/// <c>OnHeroLoaded</c> lands) for the async path.
	/// </summary>
	public void SwapOrReset(Heroes hero, Action? onReady = null) {
		if (onReady != null)
			OnceHeroInitialized(onReady);
		if (HeroID == hero)
			ResetHero();
		else
			Controller?.SelectHero(hero);
	}

	// Per-pawn one-shot continuations fired by Hook_InitializeHeroOnPawn ->
	// EntryPoint.OnPawnHeroInitialized. Keyed by raw pawn pointer (Handle) since
	// the same pointer survives across hero swaps (only the contents change).
	private static readonly Dictionary<nint, List<Action>> _heroInitContinuations = new();

	/// <summary>
	/// Runs <paramref name="action"/> the next time the server populates this pawn's
	/// abilities — i.e. after <see cref="CCitadelPlayerController.SelectHero"/> settles,
	/// after <see cref="ResetHero"/>, on initial spawn, or after the <c>resethero</c> command.
	/// Fires exactly once. Multiple registrations are queued and dispatched in order.
	/// </summary>
	/// <remarks>
	/// The action runs synchronously inside the native InitializeHeroOnPawn return path.
	/// For the same-hero <c>ResetHero</c> case this means re-entrantly during the C# call.
	/// </remarks>
	public void OnceHeroInitialized(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		if (!_heroInitContinuations.TryGetValue(Handle, out var list))
			_heroInitContinuations[Handle] = list = [];
		list.Add(action);
	}

	/// <summary>Internal: invoked from EntryPoint.OnPawnHeroInitialized before plugin dispatch.</summary>
	internal static void DrainHeroInitializedContinuations(CCitadelPlayerPawn pawn) {
		if (!_heroInitContinuations.Remove(pawn.Handle, out var pending)) return;
		foreach (var action in pending) {
			try { action(); }
			catch (Exception ex) {
				Console.WriteLine($"[CCitadelPlayerPawn.OnceHeroInitialized] {ex}");
			}
		}
	}

	/// <summary>Internal: invoked from PluginLoader.DispatchEntityDeleted to drop continuations
	/// queued for a pawn that's about to be destroyed. Without this, an entry sits forever, and
	/// when the entity system reuses the pointer for a future pawn the stale continuations would
	/// fire against the wrong player.</summary>
	internal static void OnEntityDeleted(nint pawnHandle) {
		_heroInitContinuations.Remove(pawnHandle);
	}

	/// <summary>
	/// Makes the ability in <paramref name="slot"/> ready to use again straight away, like
	/// <see cref="CCitadelBaseAbility.ResetCooldown"/>. Returns false if the slot is empty.
	/// </summary>
	public bool ResetAbilityCooldown(EAbilitySlot slot) {
		var ability = AbilityComponent.GetAbilityBySlot(slot);
		if (ability == null || !ability.IsValid) return false;
		ability.ResetCooldown();
		return true;
	}

	/// <summary>
	/// Makes all of this hero's abilities ready to use again straight away, like
	/// <see cref="CCitadelBaseAbility.ResetCooldown"/>. Unlike the Refresher item, this also covers items,
	/// innates and weapons, not just the hero's abilities and ultimate.
	/// </summary>
	public void ResetAllAbilityCooldowns() {
		if (!IsValid) return;
		foreach (var ability in AbilityComponent.Abilities)
			if (ability.IsValid)
				ability.ResetCooldown();
	}

	/// <summary>Removes an ability from this pawn by internal ability name. Returns true on success.</summary>
	public bool RemoveAbility(string abilityName) {
		Span<byte> utf8 = Utf8.Encode(abilityName, stackalloc byte[Utf8.Size(abilityName)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.RemoveAbility((void*)Handle, ptr) != 0;
		}
	}

	/// <summary>Removes an ability from this pawn by ability entity. Returns true on success.</summary>
	public bool RemoveAbility(CCitadelBaseAbility ability) {
		return NativeInterop.RemoveAbilityByEntity((void*)Handle, (void*)ability.Handle) != 0;
	}

	/// <summary>Adds an ability to this pawn by internal ability name into the given slot. Returns the new ability entity, or null on failure.</summary>
	public CBaseEntity? AddAbility(string abilityName, ushort slot) {
		Span<byte> utf8 = Utf8.Encode(abilityName, stackalloc byte[Utf8.Size(abilityName)]);
		fixed (byte* ptr = utf8) {
			void* result = NativeInterop.AddAbility((void*)Handle, ptr, slot);
			return result != null ? new CBaseEntity((nint)result) : null;
		}
	}

	/// <summary>
	/// Gives an item to this pawn by internal item name (e.g. "upgrade_sprint_booster").
	/// <para>
	/// Items that have to be imbued (Echo Shard, Mystic Reverb, ...) are granted unattached by
	/// this overload and do nothing until imbued - use
	/// <see cref="AddItem(string, EAbilitySlot, bool)"/> for those.
	/// </para>
	/// <param name="itemName">Internal item name.</param>
	/// <param name="enhanced">Should the enhanced version of the item be given</param>
	/// Returns the new item entity, or null on failure.
	/// </summary>
	public CCitadelBaseAbility? AddItem(string itemName, bool enhanced = false) {
		Span<byte> utf8 = Utf8.Encode(itemName, stackalloc byte[Utf8.Size(itemName)]);
		fixed (byte* ptr = utf8) {
			var bits = UpgradeFlags.Owned;
			if (enhanced) bits |= UpgradeFlags.Enhanced;
			void* result = NativeInterop.AddItem((void*)Handle, ptr, (int)bits);
			return result != null ? new CCitadelBaseAbility((nint)result) : null;
		}
	}

	/// <summary>
	/// Gives an imbuable item to this pawn and imbues it into the ability in
	/// <paramref name="imbueSlot"/> - the managed equivalent of <c>giveitem &lt;item&gt; &lt;slot&gt;</c>.
	/// <para>
	/// Nothing is granted unless the whole operation can succeed, so a rejected pairing never
	/// leaves the pawn holding an unattached item. Use
	/// <see cref="TryAddItem(string, EAbilitySlot, out CCitadelBaseAbility, bool)"/> when you
	/// need to know <em>why</em> it failed.
	/// </para>
	/// <param name="itemName">Internal item name, e.g. "upgrade_echo_shard".</param>
	/// <param name="imbueSlot">
	/// Ability to imbue into. The four imbuable slots are <see cref="EAbilitySlot.Signature1"/>
	/// through <see cref="EAbilitySlot.Signature4"/> (indices 0-3).
	/// </param>
	/// <param name="enhanced">Should the enhanced version of the item be given</param>
	/// Returns the new item entity, or null on failure.
	/// </summary>
	public CCitadelBaseAbility? AddItem(string itemName, EAbilitySlot imbueSlot, bool enhanced = false) {
		TryAddItem(itemName, imbueSlot, out var item, enhanced);
		return item;
	}

	/// <summary>
	/// <see cref="AddItem(string, EAbilitySlot, bool)"/> with a reason on failure.
	/// <paramref name="item"/> is non-null only when the result is <see cref="ImbueResult.Success"/>.
	/// </summary>
	public ImbueResult TryAddItem(string itemName, EAbilitySlot imbueSlot, out CCitadelBaseAbility? item, bool enhanced = false) {
		item = null;

		if (!ItemInfo.CanBeImbued(itemName))
			return ItemInfo.Exists(itemName) ? ImbueResult.ItemNotImbuable : ImbueResult.UnknownItem;

		var target = AbilityComponent.GetAbilityBySlot(imbueSlot);
		if (target == null) return ImbueResult.NoAbilityInSlot;
		if (!target.CanBeImbuedBy(itemName)) return ImbueResult.AbilityRejected;

		// Validated above, so this only fails on grant-side problems (already owned, no slot).
		var granted = AddItem(itemName, enhanced);
		if (granted == null) return ImbueResult.GrantFailed;

		if (NativeInterop.ImbueAbility((void*)granted.Handle, (void*)target.Handle) == 0) {
			// The native runs the same check we already passed, so this is unreachable in
			// practice - undo the grant anyway so the all-or-nothing contract always holds.
			RemoveItem(itemName);
			return ImbueResult.AbilityRejected;
		}

		item = granted;
		return ImbueResult.Success;
	}

	/// <summary>
	/// Imbues an item this pawn already owns into the ability in <paramref name="slot"/>.
	/// Imbuing into an ability the item is already imbued into is a no-op that reports
	/// <see cref="ImbueResult.Success"/>.
	/// </summary>
	public ImbueResult ImbueItem(string itemName, EAbilitySlot slot) {
		var owned = AbilityComponent.FindAbilityByName(itemName);
		if (owned == null)
			return ItemInfo.Exists(itemName) ? ImbueResult.ItemNotOwned : ImbueResult.UnknownItem;
		return ImbueItem(owned, slot);
	}

	/// <summary>
	/// Imbues an item entity this pawn already owns into the ability in <paramref name="slot"/>.
	/// </summary>
	public ImbueResult ImbueItem(CCitadelBaseAbility item, EAbilitySlot slot) {
		if (!item.CanBeImbued) return ImbueResult.ItemNotImbuable;

		var target = AbilityComponent.GetAbilityBySlot(slot);
		if (target == null) return ImbueResult.NoAbilityInSlot;

		return NativeInterop.ImbueAbility((void*)item.Handle, (void*)target.Handle) != 0
			? ImbueResult.Success
			: ImbueResult.AbilityRejected;
	}

	/// <summary>
	/// True when <paramref name="itemName"/> can be imbued into the ability this pawn has in
	/// <paramref name="slot"/>. False for non-imbuable items, empty slots and pairings the game
	/// rejects (an ultimate-restricted item aimed at the ultimate, an active item aimed at a passive).
	/// </summary>
	public bool CanImbue(string itemName, EAbilitySlot slot) {
		var target = AbilityComponent.GetAbilityBySlot(slot);
		return target != null && target.CanBeImbuedBy(itemName);
	}

	/// <summary>
	/// Removes an item from this pawn by name, using the ability removal path.
	/// This bypasses sell checks and does not refund gold - it directly removes the item entity
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

	/// <summary>Current stamina value (m_flCurrentValue on m_ResourceStamina).</summary>
	public float GetStamina() => AbilityComponent.ResourceStamina.CurrentValue;

	/// <summary>Helper to set stamina properly.</summary>
	public void SetStamina(float value) {
		var stamina = AbilityComponent.ResourceStamina;
		stamina.CurrentValue = value;
		stamina.LatchValue = value;
		stamina.LatchTime = GlobalVars.CurTime;
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
