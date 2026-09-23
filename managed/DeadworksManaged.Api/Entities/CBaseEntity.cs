using System.Drawing;
using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Base managed wrapper for all Source 2 entities. Provides common operations: health, team, lifecycle, modifiers, schema access.</summary>
public unsafe class CBaseEntity : NativeEntity, IEquatable<CBaseEntity> {
	/// <summary>Sentinel value for an invalid CEntityHandle.</summary>
	public const uint InvalidEntityHandle = 0xFFFFFFFF;

	/// <summary>Packed CEntityHandle (serial + index) captured at construction. Stable identity across frames.</summary>
	public uint EntityHandle { get; }

	/// <summary>Resolves the native pointer through the entity system on every access. Returns 0 if the entity has been destroyed (serial mismatch).</summary>
	public override nint Handle => EntityHandle == InvalidEntityHandle ? 0 : (nint)NativeInterop.GetEntityFromHandle(EntityHandle);

	/// <summary>True only if the entity still exists in the entity system. Returns false once the entity is destroyed (serial mismatch) or if the handle was never valid.</summary>
	public override bool IsValid => EntityHandle != InvalidEntityHandle && NativeInterop.GetEntityFromHandle(EntityHandle) != null;

	/// <summary>Entity index (lower 14 bits of the handle).</summary>
	public int EntityIndex => EntityHandle == InvalidEntityHandle ? -1 : (int)(EntityHandle & 0x3FFF);

	internal CBaseEntity(nint ptr) : base() {
		EntityHandle = ptr != 0 ? NativeInterop.GetEntityHandle((void*)ptr) : InvalidEntityHandle;
	}

	/// <summary>Construct directly from a packed entity handle without a pointer round-trip.</summary>
	internal CBaseEntity(uint entityHandle) : base() {
		EntityHandle = entityHandle;
	}

	public override string ToString() {
		nint h = Handle;
		return h != 0 ? $"{Classname} ({DesignerName}) [0x{h:X}]" : "CBaseEntity [null]";
	}

	/// <summary>Two wrappers are equal iff they point at the same native entity (same packed handle: serial + index). Wrapper type is ignored.</summary>
	public bool Equals(CBaseEntity? other) => other is not null && EntityHandle == other.EntityHandle;

	public override bool Equals(object? obj) => obj is CBaseEntity other && Equals(other);

	public override int GetHashCode() => EntityHandle.GetHashCode();

	public static bool operator ==(CBaseEntity? a, CBaseEntity? b) {
		if (ReferenceEquals(a, b)) return true;
		if (a is null || b is null) return false;
		return a.EntityHandle == b.EntityHandle;
	}

	public static bool operator !=(CBaseEntity? a, CBaseEntity? b) => !(a == b);

	/// <summary>Creates a new entity by class name (e.g. "info_particle_system"). Returns null on failure.</summary>
	public static CBaseEntity? CreateByName(string className) {
		Span<byte> utf8 = Utf8.Encode(className, stackalloc byte[Utf8.Size(className)]);
		fixed (byte* ptr = utf8) {
			void* result = NativeInterop.CreateEntityByName(ptr);
			return result != null ? new CBaseEntity((nint)result) : null;
		}
	}

	/// <summary>
	/// Creates an entity by its designer/subclass name (e.g. "npc_boss_tier2").
	/// Resolves the designer name through the subclass registry, creates the entity using the
	/// base class, and writes <c>m_nSubclassID</c> + <c>m_pSubclassVData</c> directly so VData
	/// is available before <see cref="Spawn()"/> is called.
	/// </summary>
	public static CBaseEntity? CreateByDesignerName(string designerName) {
		// Resolve designer name → base entity class + subclass_id hash
		Span<byte> nameUtf8 = Utf8.Encode(designerName, stackalloc byte[Utf8.Size(designerName)]);
		string entityClassName = designerName;
		uint subclassId = 0;
		fixed (byte* namePtr = nameUtf8) {
			byte* baseClassPtr = NativeInterop.ResolveDesignerName(namePtr, &subclassId);
			if (baseClassPtr != null)
				entityClassName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)baseClassPtr) ?? designerName;
		}

		var entity = CreateByName(entityClassName);
		if (entity == null) return null;

		// If this is a subclass, write m_nSubclassID and m_pSubclassVData directly
		if (subclassId != 0) {
			void* vdata = NativeInterop.LookupVDataByHash(-1, subclassId);
			if (vdata != null) {
				nint subclassAddr = _subclassID.GetAddress(entity.Handle);
				*(uint*)subclassAddr = subclassId;          // m_nSubclassID (CUtlStringToken, 4 bytes)
				*(nint*)(subclassAddr + 4) = (nint)vdata;   // m_pSubclassVData (pointer, right after)
			}
		}

		return entity;
	}

	/// <summary>Gets an entity by its entity handle (CEntityHandle as uint32). Returns null if invalid.</summary>
	public static CBaseEntity? FromHandle(uint handle) {
		if (handle == InvalidEntityHandle) return null;
		var ptr = (nint)NativeInterop.GetEntityFromHandle(handle);
		return ptr != 0 ? new CBaseEntity(handle) : null;
	}

	/// <summary>Gets an entity by its global entity index. Returns null if the index is invalid or the entity doesn't exist.</summary>
	public static CBaseEntity? FromIndex(int index) {
		var ptr = (nint)NativeInterop.GetEntityByIndex(index);
		return ptr != 0 ? new CBaseEntity(ptr) : null;
	}

	/// <summary>Gets a typed entity by handle. Returns null if invalid or native class doesn't match T.</summary>
	public static T? FromHandle<T>(uint handle) where T : CBaseEntity {
		if (handle == InvalidEntityHandle) return null;
		var ptr = (nint)NativeInterop.GetEntityFromHandle(handle);
		if (ptr == 0) return null;
		var entity = new CBaseEntity(handle);
		return NativeEntityFactory.IsMatch<T>(entity.Classname) ? NativeEntityFactory.Create<T>(ptr) : null;
	}

	/// <summary>Gets a typed entity by index. Returns null if invalid or native class doesn't match T.</summary>
	public static T? FromIndex<T>(int index) where T : CBaseEntity {
		var ptr = (nint)NativeInterop.GetEntityByIndex(index);
		if (ptr == 0) return null;
		var entity = new CBaseEntity(ptr);
		return NativeEntityFactory.IsMatch<T>(entity.Classname) ? NativeEntityFactory.Create<T>(ptr) : null;
	}

	/// <summary>The designer/map name (e.g. "npc_boss_tier3", "player").</summary>
	public string DesignerName {
		get {
			byte* ptr = NativeInterop.GetEntityDesignerName((void*)Handle);
			return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "";
		}
	}

	private static readonly SchemaAccessor<nint> _pEntity = new("CEntityInstance"u8, "m_pEntity"u8);
	private static readonly SchemaAccessor<nint> _entityName = new("CEntityIdentity"u8, "m_name"u8);

	/// <summary>The entity name (targetname set in Hammer or via code).</summary>
	public string Name {
		get {
			nint identity = _pEntity.Get(Handle);
			if (identity == 0) return "";
			nint namePtr = _entityName.Get(identity);
			return namePtr != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(namePtr) ?? "" : "";
		}
	}

	/// <summary>The C++ DLL class name (e.g. "CCitadelPlayerPawn", "CBaseEntity").</summary>
	public string Classname {
		get {
			byte* ptr = NativeInterop.GetEntityClassname((void*)Handle);
			return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "";
		}
	}

	/// <summary>Check if this entity's native type matches T's class name.</summary>
	public bool Is<T>() where T : CBaseEntity {
		return NativeEntityFactory.IsMatch<T>(Classname);
	}

	/// <summary>Cast this entity to T if the native type matches, otherwise null.</summary>
	public T? As<T>() where T : CBaseEntity {
		return Is<T>() ? NativeEntityFactory.Create<T>(Handle) : null;
	}

	/// <summary>Marks this entity for removal at the end of the current frame (UTIL_Remove).</summary>
	public void Remove() => NativeInterop.RemoveEntity((void*)Handle);

	/// <summary>Queues and executes entity spawn.</summary>
	public void Spawn() {
		NativeInterop.QueueSpawnEntity((void*)Handle, null);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Queues and executes entity spawn with CEntityKeyValues.</summary>
	public void Spawn(void* ekv) {
		NativeInterop.QueueSpawnEntity((void*)Handle, ekv);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Queues and executes entity spawn with CEntityKeyValues.</summary>
	public void Spawn(CEntityKeyValues ekv) {
		NativeInterop.QueueSpawnEntity((void*)Handle, ekv.Handle);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Teleports this entity. Pass null for any parameter to leave it unchanged.</summary>
	public void Teleport(Vector3? position = null, Vector3? angles = null, Vector3? velocity = null) {
		Vector3 pos = position.GetValueOrDefault();
		Vector3 ang = angles.GetValueOrDefault();
		Vector3 vel = velocity.GetValueOrDefault();
		NativeInterop.Teleport((void*)Handle,
			position.HasValue ? (float*)&pos : null,
			angles.HasValue ? (float*)&ang : null,
			velocity.HasValue ? (float*)&vel : null);
	}

	/// <summary>Fires an entity input (e.g. "Start", "Stop", "SetParent").</summary>
	public void AcceptInput(string inputName, CBaseEntity? activator = null, CBaseEntity? caller = null, string? value = null) {
		Span<byte> utf8Input = Utf8.Encode(inputName, stackalloc byte[Utf8.Size(inputName)]);

		int valLen = value != null ? Utf8.Size(value) : 1;
		Span<byte> utf8Val = stackalloc byte[valLen];
		utf8Val[0] = 0;
		if (value != null)
			Utf8.Encode(value, utf8Val);

		fixed (byte* inputPtr = utf8Input)
		fixed (byte* vPtr = utf8Val) {
			NativeInterop.AcceptInput((void*)Handle, inputPtr,
				activator != null ? (void*)activator.Handle : null,
				caller != null ? (void*)caller.Handle : null,
				value != null ? vPtr : null);
		}
	}

	/// <summary>Sets this entity's parent via AcceptInput("SetParent", activator: parent, value: "!activator").</summary>
	public void SetParent(CBaseEntity parent) => AcceptInput("SetParent", activator: parent, value: "!activator");

	/// <summary>Clears this entity's parent.</summary>
	public void ClearParent() => AcceptInput("ClearParent");

	/// <summary>Adds a modifier by VData name (e.g. "modifier_ui_hud_message").</summary>
	public CBaseModifier? AddModifier(string name, KeyValues3? kv = null, CBaseEntity? caster = null, CBaseEntity? ability = null, int team = 0) {
		Span<byte> utf8Name = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);

		fixed (byte* namePtr = utf8Name) {
			var result = NativeInterop.AddModifier(
				(void*)Handle, namePtr,
				kv != null ? (void*)kv.Handle : null,
				caster != null ? (void*)caster.Handle : null,
				ability != null ? (void*)ability.Handle : null,
				team, null, null, 0);
			return result != null ? new CBaseModifier((nint)result) : null;
		}
	}

	/// <summary>
	/// Adds a modifier with per-instance ability property value overrides.
	/// Use this to apply modifiers without a real ability, or to override the default
	/// values from the ability's property map. Property names match the VData's
	/// m_vecAutoRegisterModifierValueFromAbilityPropertyName entries.
	/// </summary>
	/// <example>
	/// pawn.AddModifier("ability_doorman_bomb/debuff", kv: kv,
	///     abilityValues: new() { ["SlowPercent"] = 100.0f });
	/// </example>
	public CBaseModifier? AddModifier(string name, Dictionary<string, float> abilityValues,
		KeyValues3? kv = null, CBaseEntity? caster = null, CBaseEntity? ability = null, int team = 0) {

		if (abilityValues.Count == 0)
			return AddModifier(name, kv, caster, ability, team);

		Span<byte> utf8Name = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);

		int count = abilityValues.Count;
		Span<float> values = count <= 16 ? stackalloc float[count] : new float[count];

		// Encode all property names as null-terminated UTF-8 into a contiguous buffer
		int totalBytes = 0;
		foreach (var kvp in abilityValues)
			totalBytes += Utf8.Size(kvp.Key);
		Span<byte> nameBuf = totalBytes <= 1024 ? stackalloc byte[totalBytes] : new byte[totalBytes];

		int offset = 0;
		int idx = 0;
		foreach (var kvp in abilityValues) {
			int len = Utf8.Size(kvp.Key);
			Utf8.Encode(kvp.Key, nameBuf.Slice(offset, len));
			values[idx] = kvp.Value;
			offset += len;
			idx++;
		}

		fixed (byte* namePtr = utf8Name)
		fixed (float* valPtr = values)
		fixed (byte* nameBufPtr = nameBuf) {
			byte** namePtrs = stackalloc byte*[count];
			int off = 0;
			idx = 0;
			foreach (var kvp in abilityValues) {
				namePtrs[idx] = nameBufPtr + off;
				off += Utf8.Size(kvp.Key);
				idx++;
			}

			var result = NativeInterop.AddModifier(
				(void*)Handle, namePtr,
				kv != null ? (void*)kv.Handle : null,
				caster != null ? (void*)caster.Handle : null,
				ability != null ? (void*)ability.Handle : null,
				team, namePtrs, valPtr, count);
			return result != null ? new CBaseModifier((nint)result) : null;
		}
	}

	/// <summary>Removes a specific modifier instance from this entity.</summary>
	public bool RemoveModifier(CBaseModifier modifier) {
		return NativeInterop.RemoveModifier((void*)Handle, (void*)modifier.Handle) != 0;
	}

	/// <summary>Removes the first modifier matching the given VData name (e.g. "modifier_stunned") from this entity.</summary>
	public bool RemoveModifier(string name) {
		var modProp = ModifierProp;
		if (modProp == null) return false;
		foreach (var mod in modProp.Modifiers) {
			if (mod.SubclassVData?.Name == name)
				return RemoveModifier(mod);
		}
		return false;
	}

	/// <summary>Plays a sound event on this entity.</summary>
	public void EmitSound(string soundName, int pitch = 100, float volume = 1.0f, float delay = 0.0f) {
		Span<byte> utf8 = Utf8.Encode(soundName, stackalloc byte[Utf8.Size(soundName)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.EmitSound((void*)Handle, ptr, pitch, volume, delay);
		}
	}

	private static readonly SchemaAccessor<nint> _bodyComponent = new("CBaseEntity"u8, "m_CBodyComponent"u8);
	public CBodyComponent? BodyComponent {
		get {
			nint ptr = _bodyComponent.Get(Handle);
			return ptr != 0 ? new CBodyComponent(ptr) : null;
		}
	}

	public Vector3 Position => BodyComponent?.SceneNode?.AbsOrigin ?? Vector3.Zero;

	private static readonly SchemaAccessor<nint> _pCollision = new("CBaseEntity"u8, "m_pCollision"u8);
	/// <summary>Collision property (OBB mins/maxs, bounding radius). Null for entities without a collision representation.</summary>
	public CCollisionProperty? Collision {
		get {
			nint ptr = _pCollision.Get(Handle);
			return ptr != 0 ? new CCollisionProperty(ptr, this) : null;
		}
	}

	private static readonly SchemaAccessor<int> _health = new("CBaseEntity"u8, "m_iHealth"u8);
	public int Health { get => _health.Get(Handle); set => _health.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _maxHealth = new("CBaseEntity"u8, "m_iMaxHealth"u8);
	public int MaxHealth => _maxHealth.Get(Handle);

	/// <summary>Gets the effective max health through the engine virtual call (accounts for modifiers, abilities, buffs).</summary>
	public int GetMaxHealth() => NativeInterop.GetMaxHealth((void*)Handle);

	/// <summary>Heals the entity by the specified amount (clamped to max health). Returns the actual amount healed.</summary>
	public int Heal(float amount) => NativeInterop.Heal((void*)Handle, amount);

	private static readonly SchemaAccessor<byte> _teamNum = new("CBaseEntity"u8, "m_iTeamNum"u8);
	public int TeamNum { get => _teamNum.Get(Handle); set => _teamNum.Set(Handle, (byte)value); }

	private static readonly SchemaAccessor<uint> _lifeState = new("CBaseEntity"u8, "m_lifeState"u8);
	public LifeState LifeState { get => (LifeState)_lifeState.Get(Handle); set => _lifeState.Set(Handle, (uint)value); }
	public bool IsAlive => LifeState == LifeState.Alive;

	private static readonly SchemaAccessor<uint> _fFlags = new("CBaseEntity"u8, "m_fFlags"u8);
	/// <summary>Engine flag bits (on-ground, ducking, client, in-vehicle, godmode, etc.). See <see cref="EntityFlags"/>.</summary>
	public EntityFlags Flags { get => (EntityFlags)_fFlags.Get(Handle); set => _fFlags.Set(Handle, (uint)value); }

	/// <summary>True if this entity has the <see cref="EntityFlags.FakeClient"/> flag set (i.e. it is a bot).</summary>
	public bool IsBot => (Flags & EntityFlags.FakeClient) != 0;

	private static readonly SchemaAccessor<uint> _hGroundEntity = new("CBaseEntity"u8, "m_hGroundEntity"u8);
	/// <summary>The entity this entity is standing on, or null if airborne.</summary>
	public CBaseEntity? GroundEntity => FromHandle(_hGroundEntity.Get(Handle));
	/// <summary>Returns true if this entity is on the ground (has a valid ground entity).</summary>
	public bool IsOnGround => _hGroundEntity.Get(Handle) != 0xFFFFFFFF;

	private static readonly SchemaAccessor<Vector3> _vecAbsVelocity = new("CBaseEntity"u8, "m_vecAbsVelocity"u8);
	/// <summary>The entity's absolute velocity.</summary>
	public Vector3 AbsVelocity { get => _vecAbsVelocity.Get(Handle); set => _vecAbsVelocity.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _flFriction = new("CBaseEntity"u8, "m_flFriction"u8);
	public float Friction { get => _flFriction.Get(Handle); set => _flFriction.Set(Handle, value); }

	private static readonly SchemaAccessor<byte> _actualMoveType = new("CBaseEntity"u8, "m_nActualMoveType"u8);
	/// <summary>How this entity is moving right now. Change it with <see cref="SetMoveType"/>.</summary>
	/// <remarks>
	/// This can differ from what you set: the <c>noclip</c> cheat command and abilities or items that change
	/// movement win while they're active, and an entity attached to another one usually reads
	/// <see cref="Api.MoveType.None"/>.
	/// </remarks>
	public MoveType MoveType => (MoveType)_actualMoveType.Get(Handle);

	/// <summary>
	/// Changes how this entity moves. Use <see cref="Api.MoveType.None"/> to freeze a hero in place,
	/// <see cref="Api.MoveType.NoClip"/> to let them fly through walls, and <see cref="Api.MoveType.Walk"/>
	/// to give them normal movement back. Takes effect straight away.
	/// </summary>
	/// <remarks>
	/// The <c>noclip</c> cheat command and abilities or items that change movement override your value while
	/// they're active. It applies again once they end.
	/// </remarks>
	public void SetMoveType(MoveType moveType) => NativeInterop.SetMoveType((void*)Handle, (byte)moveType);

	private static readonly SchemaAccessor<float> _flGravityScale = new("CBaseEntity"u8, "m_flGravityScale"u8);
	/// <summary>
	/// This entity's gravity multiplier: 1 is normal, 0.5 is half gravity, 2 is double and 0 is no gravity.
	/// Change it with <see cref="SetGravityScale"/>.
	/// </summary>
	/// <remarks>
	/// Abilities and items that change gravity multiply on top of this, so it isn't always the gravity the
	/// entity actually feels.
	/// </remarks>
	public float GravityScale => _flGravityScale.Get(Handle);

	/// <summary>
	/// Changes this entity's gravity multiplier: 1 is normal, 0.5 is half gravity, 2 is double and 0 turns
	/// gravity off. Takes effect straight away. Abilities and items that change gravity multiply on top of it.
	/// </summary>
	public void SetGravityScale(float scale) => NativeInterop.SetGravityScale((void*)Handle, scale);

	/// <summary>
	/// True for entities that can have a model, such as heroes, NPCs, props, beams and world text. Only these
	/// have a <see cref="RenderColor"/>, and only these can use <see cref="SetModel"/> and <see cref="ModelName"/>.
	/// </summary>
	public bool IsModelEntity {
		get {
			fixed (byte* modelEntity = "CBaseModelEntity\0"u8) {
				return NativeInterop.EntityDerivesFrom((void*)Handle, modelEntity) != 0;
			}
		}
	}

	private static readonly SchemaAccessor<uint> _clrRender = new("CBaseModelEntity"u8, "m_clrRender"u8);
	/// <summary>
	/// Tint and transparency of the entity's model. <see cref="Color.White"/> means no tint. Lowering alpha
	/// only makes the entity see-through if it renders translucently.
	/// </summary>
	/// <remarks>
	/// Only entities with a model have one (see <see cref="IsModelEntity"/>). On anything else, like a player
	/// controller, reading or setting it throws.
	/// </remarks>
	/// <exception cref="InvalidOperationException">The entity has no model.</exception>
	public Color RenderColor {
		get {
			uint v = _clrRender.Get(RequireModelEntity());
			return Color.FromArgb((byte)(v >> 24), (byte)v, (byte)(v >> 8), (byte)(v >> 16));
		}
		set => _clrRender.Set(RequireModelEntity(), (uint)(value.R | (value.G << 8) | (value.B << 16) | (value.A << 24)));
	}

	// m_clrRender is a CBaseModelEntity field; any other entity keeps unrelated data at that offset.
	private nint RequireModelEntity() {
		if (!IsModelEntity)
			throw new InvalidOperationException($"'{DesignerName}' has no model, so it has no render color.");
		return Handle;
	}

	/// <summary>Which way the entity faces, as (pitch, yaw, roll) in degrees. Change it with <see cref="SetRotation"/>.</summary>
	public Vector3 Rotation => BodyComponent?.SceneNode?.AbsRotation ?? Vector3.Zero;

	/// <summary>
	/// Turns the entity to face <paramref name="rotation"/> (pitch, yaw, roll in degrees) without moving it.
	/// On a hero this doesn't turn the player's view; use <c>CCitadelPlayerController.SetCameraAngles</c> for that.
	/// </summary>
	public void SetRotation(Vector3 rotation) => Teleport(angles: rotation);

	private static readonly SchemaAccessor<nint> _modifierProp = new("CBaseEntity"u8, "m_pModifierProp"u8);
	public CModifierProperty? ModifierProp {
		get {
			nint ptr = _modifierProp.Get(Handle);
			return ptr != 0 ? new CModifierProperty(ptr) : null;
		}
	}

	// m_pSubclassVData is at m_nSubclassID + 4 (CUtlStringToken is 4 bytes)
	private static readonly SchemaAccessor<byte> _subclassID = new("CBaseEntity"u8, "m_nSubclassID"u8);
	public CEntitySubclassVDataBase? SubclassVData {
		get {
			nint pVData = *(nint*)((byte*)_subclassID.GetAddress(Handle) + 4);
			return pVData != 0 ? new CEntitySubclassVDataBase(pVData) : null;
		}
	}

	/// <summary>Applies damage to this entity (convenience wrapper around <see cref="TakeDamage"/>).</summary>
	public void Hurt(float damage, CBaseEntity? attacker = null, CBaseEntity? inflictor = null, CBaseEntity? ability = null, int damageType = 0) {
		using var info = new CTakeDamageInfo(damage, attacker ?? inflictor ?? this, inflictor ?? this, ability, damageType);
		info.DamageFlags |= TakeDamageFlags.AllowSuicide;
		TakeDamage(info);
	}

	/// <summary>
	/// Deals damage to this entity the way the game does, so the target's resistances and other damage modifiers
	/// apply. <see cref="IDeadworksPlugin.OnTakeDamage"/> is called for it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Set <see cref="CTakeDamageInfo.CitadelDamageType"/> to the kind of damage you are dealing (for example
	/// <see cref="ECitadelDamageType.Bullet"/> or <see cref="ECitadelDamageType.Ability"/>), since it decides which
	/// modifiers apply. It starts out as <see cref="ECitadelDamageType.None"/>.
	/// </para>
	/// <para>
	/// The final amount is written back into <paramref name="info"/>, so use a new <see cref="CTakeDamageInfo"/> for
	/// each call. Reusing one, for example for every target of an area attack, applies the modifiers again each time.
	/// </para>
	/// <para>
	/// Calling this from inside an <see cref="IDeadworksPlugin.OnTakeDamage"/> handler triggers that handler again,
	/// so guard against loops (for example when reflecting damage back at the attacker).
	/// </para>
	/// </remarks>
	public void ApplyDamage(CTakeDamageInfo info) {
		NativeInterop.ApplyDamage((void*)Handle, (void*)info.Handle);
	}

	/// <summary>
	/// Deals exactly the damage in <paramref name="info"/>, ignoring the target's resistances and other damage
	/// modifiers. <see cref="IDeadworksPlugin.OnTakeDamage"/> is not called. Use <see cref="ApplyDamage"/> for
	/// damage that should behave like the game's own.
	/// </summary>
	public void TakeDamage(CTakeDamageInfo info) {
		NativeInterop.TakeDamage((void*)Handle, (void*)info.Handle);
	}

	/// <summary>Sets the model for this entity (e.g. "models/heroes_wip/werewolf/werewolf.vmdl").</summary>
	public void SetModel(string modelName) {
		Span<byte> utf8 = Utf8.Encode(modelName, stackalloc byte[Utf8.Size(modelName)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.SetModel((void*)Handle, ptr);
		}
	}

	/// <summary>The current model path for this entity (e.g. "models/heroes_wip/werewolf/werewolf.vmdl"), or empty if unset.</summary>
	public string ModelName {
		get {
			byte* ptr = NativeInterop.GetModelName((void*)Handle);
			return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "";
		}
	}

	/// <summary>Sets this entity's model scale (1.0 = default).</summary>
	public void SetScale(float scale) {
		NativeInterop.SetScale((void*)Handle, scale);
	}

	/// <summary>Read any schema field by class and field name. For repeated access, prefer a static <see cref="SchemaAccessor{T}"/> instead.</summary>
	public T GetField<T>(ReadOnlySpan<byte> className, ReadOnlySpan<byte> fieldName) where T : unmanaged
		=> new SchemaAccessor<T>(className, fieldName).Get(Handle);

	/// <summary>Write any schema field by class and field name. For repeated access, prefer a static <see cref="SchemaAccessor{T}"/> instead.</summary>
	public void SetField<T>(ReadOnlySpan<byte> className, ReadOnlySpan<byte> fieldName, T value) where T : unmanaged
		=> new SchemaAccessor<T>(className, fieldName).Set(Handle, value);
}
