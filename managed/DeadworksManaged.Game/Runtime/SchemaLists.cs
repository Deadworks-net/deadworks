using System.Collections;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>Where a list field keeps its elements: inline for a fixed array, on the heap for a <c>CUtlVector</c>.</summary>
internal readonly unsafe struct ListStorage {
	public readonly SchemaObject Owner;
	public readonly SchemaField Field;
	private readonly int _fixedCount;

	/// <summary>A <c>CUtlVector</c>, which can grow.</summary>
	public const int Vector = -1;

	/// <summary>A vector of another layout (<c>CUtlLeanVector</c>): read in place, never resized.</summary>
	public const int UnresizableVector = -2;

	/// <summary><paramref name="fixedCount"/> is the length of a fixed array, or <see cref="Vector"/> or <see cref="UnresizableVector"/>.</summary>
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

	/// <summary>Sets the vector's length, growing its memory with the game's allocator when it has to.</summary>
	private nint Resize(int count, int stride) {
		if (_fixedCount != Vector)
			throw new NotSupportedException($"{Field} is {(IsFixed ? "a fixed array" : "not a CUtlVector")}, so its length cannot change.");
		nint vector = Owner.AddressOf(Field);
		if (NativeInterop.GrowUtlVector((void*)vector, stride, count) == 0)
			throw new NotSupportedException($"{Field} does not own its memory, so it cannot grow.");
		*(int*)vector = count;
		return (nint)NativeInterop.GetUtlVectorData((void*)vector);
	}

	/// <summary>Opens a slot at <paramref name="index"/>, moving what follows up by one, and returns its address.</summary>
	public nint Insert(int index, int stride) {
		int count = Count;
		if ((uint)index > (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
		nint data = Resize(count + 1, stride);
		nint slot = data + index * stride;
		Buffer.MemoryCopy((void*)slot, (void*)(slot + stride), (long)(count - index) * stride, (long)(count - index) * stride);
		return slot;
	}

	/// <summary>Closes the slot at <paramref name="index"/>, moving what follows down by one.</summary>
	public void RemoveAt(int index, int stride) {
		int count = Count;
		if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
		nint slot = Data + index * stride;
		Buffer.MemoryCopy((void*)(slot + stride), (void*)slot, (long)(count - index - 1) * stride, (long)(count - index - 1) * stride);
		Resize(count - 1, stride);
	}

	public void Clear(int stride) => Resize(0, stride);

	public void Changed() => Owner.NotifyChanged(Field);
}

/// <summary><c>sizeof</c> and <c>alignof</c> a schema class in the running game, looked up once.</summary>
internal static unsafe class SchemaClassSize<T> where T : SchemaObject, ISchemaClass<T> {
	public static readonly int Value = Lookup(alignment: false);
	public static readonly int Alignment = Lookup(alignment: true);

	private static int Lookup(bool alignment) {
		string name = T.NativeName;
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		int result;
		fixed (byte* p = utf8)
			result = alignment ? NativeInterop.GetSchemaClassAlignment(p) : NativeInterop.GetSchemaClassSize(p);
		if (result <= 0)
			throw new NotSupportedException($"The running game's schema has no class {name}, so a collection of them cannot be walked.");
		return result;
	}
}

/// <summary>
/// A fixed array or <c>CUtlVector</c> of plain values, read and written in place. A vector can
/// also grow and shrink; its memory comes from the game's allocator, as it does when the game
/// adds to it.
/// </summary>
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

	/// <summary>Appends <paramref name="value"/>. Throws <see cref="NotSupportedException"/> for a fixed array.</summary>
	public void Add(T value) => Insert(Count, value);

	/// <summary>Inserts <paramref name="value"/> at <paramref name="index"/>.</summary>
	public void Insert(int index, T value) {
		*(T*)_storage.Insert(index, sizeof(T)) = value;
		_storage.Changed();
	}

	/// <summary>Removes the element at <paramref name="index"/>.</summary>
	public void RemoveAt(int index) {
		_storage.RemoveAt(index, sizeof(T));
		_storage.Changed();
	}

	/// <summary>Removes every element. The vector keeps its memory.</summary>
	public void Clear() {
		_storage.Clear(sizeof(T));
		_storage.Changed();
	}

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
	private readonly int _stride, _offset;

	/// <summary>
	/// <paramref name="stride"/> is the size of one element where an element is more than the
	/// pointer, and <paramref name="offset"/> where the pointer sits in it.
	/// </summary>
	internal SchemaPointerList(SchemaObject owner, SchemaField field, int fixedCount, int stride = 0, int offset = 0) {
		_storage = new(owner, field, fixedCount);
		_stride = stride;
		_offset = offset;
	}

	public int Count => _storage.Count;

	/// <summary>The object the pointer at <paramref name="index"/> refers to, or null if the pointer is null.</summary>
	public T? this[int index] {
		get {
			nint pointer = *(nint*)(_storage.Element(index, _stride == 0 ? sizeof(nint) : _stride) + _offset);
			return pointer == 0 ? null : SchemaObject.At<T>(pointer);
		}
	}

	public IEnumerator<T?> GetEnumerator() {
		for (int i = 0, count = Count; i < count; i++) yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A fixed array or <c>CUtlVector</c> of entity handles. A vector can also grow and shrink.</summary>
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

	/// <summary>Appends a handle to <paramref name="entity"/>. Throws <see cref="NotSupportedException"/> for a fixed array.</summary>
	public void Add(T? entity) => Insert(Count, entity);

	/// <summary>Inserts a handle to <paramref name="entity"/> at <paramref name="index"/>.</summary>
	public void Insert(int index, T? entity) {
		*(uint*)_storage.Insert(index, sizeof(uint)) = entity?.EntityHandle ?? CBaseEntity.InvalidEntityHandle;
		_storage.Changed();
	}

	/// <summary>Removes the first handle to <paramref name="entity"/>. False if the list has none.</summary>
	public bool Remove(T entity) {
		uint handle = entity.EntityHandle;
		for (int i = 0, count = Count; i < count; i++) {
			if (RawHandle(i) != handle) continue;
			RemoveAt(i);
			return true;
		}
		return false;
	}

	/// <summary>Removes the handle at <paramref name="index"/>.</summary>
	public void RemoveAt(int index) {
		_storage.RemoveAt(index, sizeof(uint));
		_storage.Changed();
	}

	/// <summary>Removes every handle. The vector keeps its memory.</summary>
	public void Clear() {
		_storage.Clear(sizeof(uint));
		_storage.Changed();
	}

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
