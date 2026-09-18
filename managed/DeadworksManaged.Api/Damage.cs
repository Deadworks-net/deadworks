namespace DeadworksManaged.Api;

/// <summary>
/// Wraps the native CTakeDamageInfo damage descriptor. Can be owned (created via constructor, must be Disposed)
/// or non-owning (obtained from an OnTakeDamage hook). Exposes attacker, inflictor, ability, damage amount, type, and flags.
/// </summary>
public sealed unsafe class CTakeDamageInfo : IDisposable {
	public nint Handle { get; private set; }
	private readonly bool _owned;

	private CTakeDamageInfo(nint handle, bool owned) {
		Handle = handle;
		_owned = owned;
	}

	/// <summary>Wraps an existing native CTakeDamageInfo pointer (non-owning, e.g. from OnTakeDamage hook).</summary>
	internal static CTakeDamageInfo FromExisting(nint handle) => new(handle, owned: false);

	/// <summary>Creates a new native CTakeDamageInfo. Must be disposed after use.</summary>
	public CTakeDamageInfo(float damage, CBaseEntity? attacker = null, CBaseEntity? inflictor = null, CBaseEntity? ability = null, int damageType = 0) {
		Handle = (nint)NativeInterop.CreateDamageInfo(
			inflictor != null ? (void*)inflictor.Handle : null,
			attacker != null ? (void*)attacker.Handle : null,
			ability != null ? (void*)ability.Handle : null,
			damage, damageType);
		_owned = true;
	}

	public void Dispose() {
		if (_owned && Handle != 0) {
			NativeInterop.DestroyDamageInfo((void*)Handle);
			Handle = 0;
		}
	}

	private static readonly SchemaAccessor<uint> _hInflictor = new("CTakeDamageInfo"u8, "m_hInflictor"u8);
	public CBaseEntity? Inflictor {
		get {
			uint raw = _hInflictor.Get(Handle);
			void* ptr = NativeInterop.GetEntityFromHandle(raw);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<uint> _hAttacker = new("CTakeDamageInfo"u8, "m_hAttacker"u8);
	public CBaseEntity? Attacker {
		get {
			uint raw = _hAttacker.Get(Handle);
			void* ptr = NativeInterop.GetEntityFromHandle(raw);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<uint> _hAbility = new("CTakeDamageInfo"u8, "m_hAbility"u8);
	public CBaseEntity? Ability {
		get {
			uint raw = _hAbility.Get(Handle);
			void* ptr = NativeInterop.GetEntityFromHandle(raw);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<uint> _hOriginator = new("CTakeDamageInfo"u8, "m_hOriginator"u8);
	public CBaseEntity? Originator {
		get {
			uint raw = _hOriginator.Get(Handle);
			void* ptr = NativeInterop.GetEntityFromHandle(raw);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<float> _damage = new("CTakeDamageInfo"u8, "m_flDamage"u8);
	public float Damage { get => _damage.Get(Handle); set => _damage.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _totalledDamage = new("CTakeDamageInfo"u8, "m_flTotalledDamage"u8);
	public float TotalledDamage { get => _totalledDamage.Get(Handle); set => _totalledDamage.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _damageType = new("CTakeDamageInfo"u8, "m_bitsDamageType"u8);
	public int DamageType { get => _damageType.Get(Handle); set => _damageType.Set(Handle, value); }

	private static readonly SchemaAccessor<ulong> _damageFlags = new("CTakeDamageInfo"u8, "m_nDamageFlags"u8);
	public TakeDamageFlags DamageFlags { get => (TakeDamageFlags)_damageFlags.Get(Handle); set => _damageFlags.Set(Handle, (ulong)value); }

	private static readonly SchemaAccessor<int> _eCitadelDamageType = new("CTakeDamageInfo"u8, "m_eCitadelDamageType"u8);
	public ECitadelDamageType CitadelDamageType { get => (ECitadelDamageType)_eCitadelDamageType.Get(Handle); set => _eCitadelDamageType.Set(Handle, (int)value); }
}
