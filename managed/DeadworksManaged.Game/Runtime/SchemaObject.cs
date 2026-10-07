using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>Implemented by every generated schema class, so the runtime can create one without reflection.</summary>
public interface ISchemaClass<TSelf> where TSelf : SchemaObject, ISchemaClass<TSelf> {
	/// <summary>Creates an unbound instance. The runtime binds it to its object.</summary>
	static abstract TSelf New();

	/// <summary>The class's name in the game's schema, e.g. <c>CCitadelPlayerPawn</c>.</summary>
	static abstract string NativeName { get; }
}

/// <summary>
/// Base of every generated schema class: a typed view of one native object. An entity is
/// found again through its handle on every access, and a struct inside it through its
/// owner, so a view never points at freed memory: once the entity is gone,
/// <see cref="IsValid"/> is false and field access throws instead of crashing the server.
/// </summary>
public abstract unsafe class SchemaObject : IEquatable<SchemaObject> {
	private enum Mode : byte { Unbound, Entity, Embedded, Pointer, Raw }

	private Mode _mode;
	private uint _entityHandle = CBaseEntity.InvalidEntityHandle;
	private SchemaObject? _owner;
	private SchemaField? _via;
	private int _extraOffset;
	private nint _pointer;

	/// <summary>Address of the native object, or 0 if it no longer exists.</summary>
	public nint Handle {
		get {
			switch (_mode) {
				case Mode.Entity:
					return _entityHandle == CBaseEntity.InvalidEntityHandle ? 0 : (nint)NativeInterop.GetEntityFromHandle(_entityHandle);
				case Mode.Embedded: {
					nint owner = _owner!.Handle;
					return owner == 0 ? 0 : owner + (_via?.Offset ?? 0) + _extraOffset;
				}
				case Mode.Pointer: {
					nint owner = _owner!.Handle;
					return owner == 0 ? 0 : *(nint*)(owner + (_via?.Offset ?? 0) + _extraOffset);
				}
				case Mode.Raw:
					return _pointer;
				default:
					return 0;
			}
		}
	}

	/// <summary>True while the native object exists.</summary>
	public bool IsValid => Handle != 0;

	/// <summary>The packed entity handle if this is an entity, otherwise <see cref="CBaseEntity.InvalidEntityHandle"/>.</summary>
	internal uint BoundEntityHandle => _mode == Mode.Entity ? _entityHandle : CBaseEntity.InvalidEntityHandle;

	/// <summary>
	/// A view of the object at <paramref name="pointer"/>. Nothing checks that the pointer
	/// is a <typeparamref name="T"/> or stays alive; use it for pointers the game hands to a hook.
	/// </summary>
	public static T At<T>(nint pointer) where T : SchemaObject, ISchemaClass<T> {
		var view = T.New();
		view._mode = Mode.Raw;
		view._pointer = pointer;
		return view;
	}

	/// <summary>
	/// This same object, viewed as a <typeparamref name="T"/>. Nothing checks that it is one: use
	/// it where the game types a member as a base class (a scene node that is a skeleton instance,
	/// VData that is an ability's). For entities, <c>As&lt;T&gt;()</c> checks.
	/// </summary>
	public T Cast<T>() where T : SchemaObject, ISchemaClass<T> {
		var view = T.New();
		view._mode = _mode;
		view._entityHandle = _entityHandle;
		view._owner = _owner;
		view._via = _via;
		view._extraOffset = _extraOffset;
		view._pointer = _pointer;
		return view;
	}

	internal static T ForEntityHandle<T>(uint entityHandle) where T : SchemaObject, ISchemaClass<T> {
		var view = T.New();
		view.BindEntity(entityHandle);
		return view;
	}

	internal void BindEntity(uint entityHandle) {
		_mode = Mode.Entity;
		_entityHandle = entityHandle;
	}

	internal T ElementAt<T>(SchemaField? via, int extraOffset) where T : SchemaObject, ISchemaClass<T> {
		var view = T.New();
		view._mode = Mode.Embedded;
		view._owner = this;
		view._via = via;
		view._extraOffset = extraOffset;
		return view;
	}

	private nint Ptr {
		get {
			nint handle = Handle;
			if (handle == 0) ThrowGone();
			return handle;
		}
	}

	private void ThrowGone() => throw new InvalidOperationException(
		_mode == Mode.Unbound
			? $"This {GetType().Name} is not bound to a native object."
			: $"The native object behind this {GetType().Name} no longer exists. Check IsValid before using a view kept across frames.");

	/// <summary>Address of <paramref name="field"/> in this object.</summary>
	internal nint AddressOf(SchemaField field) => Ptr + field.Offset;

	// ---- Used by generated members -------------------------------------------------

	protected T Get<T>(SchemaField field) where T : unmanaged => *(T*)(Ptr + field.Offset);

	protected void Set<T>(SchemaField field, T value) where T : unmanaged {
		*(T*)(Ptr + field.Offset) = value;
		NotifyChanged(field);
	}

	/// <summary>A struct the game stores inside this object.</summary>
	protected T Embedded<T>(SchemaField field) where T : SchemaObject, ISchemaClass<T> => ElementAt<T>(field, 0);

	/// <summary>An object this one points at, or null while the pointer is null.</summary>
	protected T? Pointer<T>(SchemaField field) where T : SchemaObject, ISchemaClass<T> {
		if (*(nint*)(Ptr + field.Offset) == 0) return null;
		var view = T.New();
		view._mode = Mode.Pointer;
		view._owner = this;
		view._via = field;
		return view;
	}

	/// <summary>An entity this object points at directly, or null while the pointer is null.</summary>
	protected T? EntityPointer<T>(SchemaField field) where T : Schema.CEntityInstance, ISchemaClass<T> {
		nint entity = *(nint*)(Ptr + field.Offset);
		return entity == 0 ? null : SchemaRegistry.Resolve<T>(NativeInterop.GetEntityHandle((void*)entity));
	}

	/// <summary>The entity a <c>CHandle</c> field refers to, or null if it is unset, gone, or not a <typeparamref name="T"/>.</summary>
	protected T? GetHandle<T>(SchemaField field) where T : Schema.CEntityInstance, ISchemaClass<T>
		=> SchemaRegistry.Resolve<T>(*(uint*)(Ptr + field.Offset));

	protected void SetHandle(SchemaField field, Schema.CEntityInstance? entity)
		=> Set(field, entity?.EntityHandle ?? CBaseEntity.InvalidEntityHandle);

	/// <summary>Reads a field that holds one <c>const char*</c>: <c>CUtlSymbolLarge</c>, <c>CGlobalSymbol</c>, <c>CUtlString</c>.</summary>
	protected string GetString(SchemaField field) => ReadCString(*(nint*)(Ptr + field.Offset));

	/// <summary>Points a <c>CUtlSymbolLarge</c> field at a copy of <paramref name="value"/>. The copy is never freed.</summary>
	protected void SetString(SchemaField field, string? value) {
		nint address = Ptr + field.Offset;
		*(nint*)address = Marshal.StringToCoTaskMemUTF8(value ?? "");
		NotifyChanged(field);
	}

	/// <summary>
	/// Reads a field that is a <c>CBufferString</c>: <c>CSoundEventName</c>, <c>CPanoramaImageName</c>,
	/// <c>CResourceNameTyped</c>. The text sits in the field itself or, once it outgrows that, on the heap.
	/// </summary>
	protected string GetBufferString(SchemaField field) {
		byte* buffer = (byte*)(Ptr + field.Offset);
		int length = (int)(*(uint*)buffer & 0x3FFFFFFF);
		uint allocation = *(uint*)(buffer + 4);
		if (length == 0) return "";
		const uint stackAllocated = 1u << 30;
		byte* text = (allocation & stackAllocated) != 0 ? buffer + 8 : (allocation & 0x3FFFFFFF) == 0 ? null : *(byte**)(buffer + 8);
		return text == null ? "" : System.Text.Encoding.UTF8.GetString(text, length);
	}

	/// <summary>Reads a <c>char[capacity]</c> field.</summary>
	protected string GetChars(SchemaField field, int capacity) {
		var bytes = new ReadOnlySpan<byte>((void*)(Ptr + field.Offset), capacity);
		int end = bytes.IndexOf((byte)0);
		return System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
	}

	/// <summary>A field whose type the generator does not map yet: its address and the game's name for its type.</summary>
	protected RawField Raw(SchemaField field, string typeName) => new(this, field, typeName);

	internal static string ReadCString(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";

	/// <summary>
	/// Tells the game a networked field changed so it is sent to clients. A struct with its
	/// own network chain reports through it; one stored inside an entity reports to that
	/// entity at its offset there; anything else is not networked from here.
	/// </summary>
	internal void NotifyChanged(SchemaField field, int extraOffset = 0) {
		if (!field.Networked) return;
		nint self = Handle;
		if (self == 0) return;
		int offset = field.Offset + extraOffset;
		short chain = field.ChainOffset;
		if (chain != 0) {
			NativeInterop.NotifyStateChanged((void*)self, offset, chain, 0);
			return;
		}
		if (TryGetEntityBase(out nint entity, out int baseOffset))
			NativeInterop.NotifyStateChanged((void*)entity, baseOffset + offset, 0, 0);
	}

	private bool TryGetEntityBase(out nint entity, out int offset) {
		if (_mode == Mode.Entity) {
			entity = Handle;
			offset = 0;
			return entity != 0;
		}
		if (_mode == Mode.Embedded && _owner!.TryGetEntityBase(out entity, out int ownerOffset)) {
			offset = ownerOffset + (_via?.Offset ?? 0) + _extraOffset;
			return true;
		}
		entity = 0;
		offset = 0;
		return false;
	}

	// ---- Identity ------------------------------------------------------------------

	/// <summary>Two views are equal when they are of the same native object.</summary>
	public bool Equals(SchemaObject? other) {
		if (other is null) return false;
		if (ReferenceEquals(this, other)) return true;
		if (_mode == Mode.Entity && other._mode == Mode.Entity) return _entityHandle == other._entityHandle;
		nint handle = Handle;
		return handle != 0 && handle == other.Handle;
	}

	public override bool Equals(object? obj) => obj is SchemaObject other && Equals(other);

	public static bool operator ==(SchemaObject? a, SchemaObject? b) => a is null ? b is null : a.Equals(b);

	public static bool operator !=(SchemaObject? a, SchemaObject? b) => !(a == b);

	public override int GetHashCode() => _mode == Mode.Entity ? _entityHandle.GetHashCode() : Handle.GetHashCode();

	public override string ToString() {
		nint handle = Handle;
		return handle != 0 ? $"{GetType().Name} [0x{handle:X}]" : $"{GetType().Name} [gone]";
	}
}

/// <summary>
/// A schema field the generator has no typed mapping for. It still appears on its class,
/// so nothing is missing; read it yourself once you know its layout.
/// </summary>
public readonly unsafe struct RawField {
	private readonly SchemaObject _owner;
	private readonly SchemaField _field;

	/// <summary>The field's type as the game spells it, e.g. <c>CSoundEventName</c>.</summary>
	public string TypeName { get; }

	internal RawField(SchemaObject owner, SchemaField field, string typeName) {
		_owner = owner;
		_field = field;
		TypeName = typeName;
	}

	/// <summary>Address of the field in the running game.</summary>
	public nint Address => _owner.AddressOf(_field);

	/// <summary>Reads a <typeparamref name="T"/> at <paramref name="offset"/> bytes into the field.</summary>
	public T Read<T>(int offset = 0) where T : unmanaged => *(T*)(Address + offset);

	/// <summary>Writes a <typeparamref name="T"/> at <paramref name="offset"/> bytes into the field. Clients are not told.</summary>
	public void Write<T>(T value, int offset = 0) where T : unmanaged => *(T*)(Address + offset) = value;

	public override string ToString() => $"{_field} ({TypeName})";
}

/// <summary>The game's 4-byte <c>Color</c>: red, green, blue, alpha.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Color32(byte R, byte G, byte B, byte A = 255) {
	public static implicit operator Color32(System.Drawing.Color color) => new(color.R, color.G, color.B, color.A);
	public static implicit operator System.Drawing.Color(Color32 color) => System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

	public override string ToString() => $"{R} {G} {B} {A}";
}
