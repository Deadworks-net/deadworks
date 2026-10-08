namespace DeadworksManaged.Api;

/// <summary>
/// Reads and writes a single networked schema field of type <typeparamref name="T"/>.
/// Resolves the field offset once on first access and caches it.
/// Use UTF-8 string literals (<c>"ClassName"u8</c>) for <paramref name="className"/> and <paramref name="fieldName"/>.
/// </summary>
/// <remarks>
/// DeadworksManaged.Game has a generated, typed member for every schema field of every class,
/// so most plugins do not need to declare one of these: <c>pawn.Schema.m_flRespawnTime</c>.
/// </remarks>
public sealed unsafe class SchemaAccessor<T> where T : unmanaged {
	private volatile int _offset = -1;
	private short _chainOffset;
	private bool _networked;
	private bool _networkingKnown;
	private readonly byte[] _className;
	private readonly byte[] _fieldName;
	private readonly int _networkStateChangedOffset;

	/// <param name="className">UTF-8 null-terminated class name (e.g. <c>"CBaseEntity"u8</c>).</param>
	/// <param name="fieldName">UTF-8 null-terminated field name (e.g. <c>"m_iHealth"u8</c>).</param>
	/// <param name="networkStateChangedOffset">Optional offset passed to NotifyStateChanged for custom chain offsets.</param>
	public SchemaAccessor(ReadOnlySpan<byte> className, ReadOnlySpan<byte> fieldName, int networkStateChangedOffset = 0) {
		// Copy and null-terminate for C interop
		_className = new byte[className.Length + 1];
		className.CopyTo(_className);
		_fieldName = new byte[fieldName.Length + 1];
		fieldName.CopyTo(_fieldName);
		_networkStateChangedOffset = networkStateChangedOffset;
	}

	private void Resolve() {
		fixed (byte* cls = _className, fld = _fieldName) {
			SchemaFieldResult r;
			NativeInterop.GetSchemaField(cls, fld, &r);
			_chainOffset = r.ChainOffset;
			_networked = r.Networked != 0;
			_networkingKnown = r.Networked != SchemaFieldResult.NetworkingUnknown;
			_offset = r.Offset; // volatile write last - publishes all fields
		}
	}

	/// <summary>Resolved byte offset of this field. Forces resolution if not yet resolved.</summary>
	internal int Offset { get { if (_offset < 0) Resolve(); return _offset; } }

	/// <summary>Chain offset for network state change notifications.</summary>
	internal short ChainOffset { get { if (_offset < 0) Resolve(); return _chainOffset; } }

	/// <summary>Returns the raw pointer to the field on <paramref name="entity"/>.</summary>
	public nint GetAddress(nint entity) {
		if (_offset < 0) Resolve();
		return (nint)((byte*)entity + _offset);
	}

	/// <summary>Reads the field value from <paramref name="entity"/>.</summary>
	public T Get(nint entity) {
		if (_offset < 0) Resolve();
		return *(T*)((byte*)entity + _offset);
	}

	/// <summary>Writes the field value to <paramref name="entity"/>, notifying the network state if the field is networked.</summary>
	public void Set(nint entity, T value) {
		if (_offset < 0 || !_networkingKnown) Resolve();
		*(T*)((byte*)entity + _offset) = value;
		if (_networked)
			NativeInterop.NotifyStateChanged((void*)entity, _offset, _chainOffset, _networkStateChangedOffset);
	}
}
