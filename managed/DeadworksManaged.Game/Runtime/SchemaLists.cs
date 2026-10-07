using System.Collections;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>Where a list field keeps its elements: inline for a fixed array, on the heap for a <c>CUtlVector</c>.</summary>
internal readonly unsafe struct ListStorage {
	public readonly SchemaObject Owner;
	public readonly SchemaField Field;
	private readonly int _fixedCount;

	/// <summary><paramref name="fixedCount"/> is the length of a fixed array, or -1 for a <c>CUtlVector</c>.</summary>
	public ListStorage(SchemaObject owner, SchemaField field, int fixedCount) {
		Owner = owner;
		Field = field;
		_fixedCount = fixedCount;
	}

	public bool IsFixed => _fixedCount >= 0;

	public int Count => IsFixed ? _fixedCount : NativeInterop.GetUtlVectorSize((void*)Owner.AddressOf(Field));

	public nint Data => IsFixed ? Owner.AddressOf(Field) : (nint)NativeInterop.GetUtlVectorData((void*)Owner.AddressOf(Field));

	public nint Element(int index, int stride) {
		if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
		return Data + index * stride;
	}
}

/// <summary><c>sizeof</c> a schema class in the running game, looked up once.</summary>
internal static unsafe class SchemaClassSize<T> where T : SchemaObject, ISchemaClass<T> {
	public static readonly int Value = Lookup();

	private static int Lookup() {
		string name = T.NativeName;
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		int size;
		fixed (byte* p = utf8)
			size = NativeInterop.GetSchemaClassSize(p);
		if (size <= 0)
			throw new NotSupportedException($"The running game's schema has no class {name}, so a list of them cannot be walked.");
		return size;
	}
}

/// <summary>A fixed array or <c>CUtlVector</c> of plain values, read and written in place.</summary>
public readonly unsafe struct SchemaValueList<T> : IReadOnlyList<T> where T : unmanaged {
	private readonly ListStorage _storage;

	internal SchemaValueList(SchemaObject owner, SchemaField field, int fixedCount) => _storage = new(owner, field, fixedCount);

	public int Count => _storage.Count;

	public T this[int index] {
		get => *(T*)_storage.Element(index, sizeof(T));
		set {
			*(T*)_storage.Element(index, sizeof(T)) = value;
			_storage.Owner.NotifyChanged(_storage.Field, _storage.IsFixed ? index * sizeof(T) : 0);
		}
	}

	/// <summary>The elements as they are now. A vector's memory moves when the game grows it, so do not keep the span.</summary>
	public ReadOnlySpan<T> AsSpan() => new((void*)_storage.Data, _storage.Count);

	public IEnumerator<T> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A fixed array or <c>CUtlVector</c> of structs stored one after another.</summary>
public readonly struct SchemaObjectList<T> : IReadOnlyList<T> where T : SchemaObject, ISchemaClass<T> {
	private readonly ListStorage _storage;

	internal SchemaObjectList(SchemaObject owner, SchemaField field, int fixedCount) => _storage = new(owner, field, fixedCount);

	public int Count => _storage.Count;

	/// <summary>
	/// The element at <paramref name="index"/>. An element of a vector is a view of where it is
	/// now; the game moves a vector's memory when it grows, so read what you need and let go.
	/// </summary>
	public T this[int index] {
		get {
			int stride = SchemaClassSize<T>.Value;
			if (!_storage.IsFixed) return SchemaObject.At<T>(_storage.Element(index, stride));
			if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
			return _storage.Owner.ElementAt<T>(_storage.Field, index * stride);
		}
	}

	public IEnumerator<T> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A fixed array or <c>CUtlVector</c> of pointers to objects.</summary>
public readonly unsafe struct SchemaPointerList<T> : IReadOnlyList<T?> where T : SchemaObject, ISchemaClass<T> {
	private readonly ListStorage _storage;

	internal SchemaPointerList(SchemaObject owner, SchemaField field, int fixedCount) => _storage = new(owner, field, fixedCount);

	public int Count => _storage.Count;

	/// <summary>The object the pointer at <paramref name="index"/> refers to, or null if the pointer is null.</summary>
	public T? this[int index] {
		get {
			nint pointer = *(nint*)_storage.Element(index, sizeof(nint));
			return pointer == 0 ? null : SchemaObject.At<T>(pointer);
		}
	}

	public IEnumerator<T?> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A fixed array or <c>CUtlVector</c> of entity handles.</summary>
public readonly unsafe struct SchemaHandleList<T> : IReadOnlyList<T?> where T : Schema.CEntityInstance, ISchemaClass<T> {
	private readonly ListStorage _storage;

	internal SchemaHandleList(SchemaObject owner, SchemaField field, int fixedCount) => _storage = new(owner, field, fixedCount);

	public int Count => _storage.Count;

	/// <summary>The entity at <paramref name="index"/>, or null if the handle is unset, the entity is gone, or it is not a <typeparamref name="T"/>.</summary>
	public T? this[int index] {
		get => SchemaRegistry.Resolve<T>(*(uint*)_storage.Element(index, sizeof(uint)));
		set {
			*(uint*)_storage.Element(index, sizeof(uint)) = value?.EntityHandle ?? CBaseEntity.InvalidEntityHandle;
			_storage.Owner.NotifyChanged(_storage.Field, _storage.IsFixed ? index * sizeof(uint) : 0);
		}
	}

	/// <summary>The packed handle at <paramref name="index"/>, without looking the entity up.</summary>
	public uint RawHandle(int index) => *(uint*)_storage.Element(index, sizeof(uint));

	public IEnumerator<T?> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A fixed array or <c>CUtlVector</c> of strings (<c>CUtlSymbolLarge</c>, <c>CUtlString</c>). Read-only.</summary>
public readonly unsafe struct SchemaStringList : IReadOnlyList<string> {
	private readonly ListStorage _storage;

	internal SchemaStringList(SchemaObject owner, SchemaField field, int fixedCount) => _storage = new(owner, field, fixedCount);

	public int Count => _storage.Count;

	public string this[int index] => SchemaObject.ReadCString(*(nint*)_storage.Element(index, sizeof(nint)));

	public IEnumerator<string> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
