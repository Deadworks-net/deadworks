using System.Collections;
using System.Runtime.CompilerServices;

namespace DeadworksManaged.Game;

/// <summary>
/// The game's <c>CUtlDict</c> and <c>CUtlOrderedMap</c>: a red-black tree whose nodes sit in one
/// array (tier1/utlrbtree.h, utlmap.h). The map is 40 bytes: a comparison object, then the tree's
/// node vector (count, capacity, pointer) and its root, element count and free list. A node is
/// four 32-bit links, then the key, then the value.
/// </summary>
internal readonly unsafe struct TreeStorage {
	private readonly SchemaObject _owner;
	private readonly SchemaField _field;
	private readonly int _keyOffset, _valueOffset, _stride;

	private const int Links = 16;   // left, right, parent, tag

	public TreeStorage(SchemaObject owner, SchemaField field, int keySize, int keyAlignment, int valueSize, int valueAlignment) {
		_owner = owner;
		_field = field;
		_keyOffset = Align(Links, keyAlignment);
		_valueOffset = Align(_keyOffset + keySize, valueAlignment);
		_stride = Align(_valueOffset + valueSize, Math.Max(4, Math.Max(keyAlignment, valueAlignment)));
	}

	private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

	private nint Tree => _owner.AddressOf(_field) + 8;

	/// <summary>Reads a value at an address. Iterators cannot hold pointers, so they read through this.</summary>
	public static T Read<T>(nint address) where T : unmanaged => *(T*)address;

	/// <summary>How many elements the map holds.</summary>
	public int Count => Read<int>(Tree + 20);

	/// <summary>The nodes in use, in the order they were added.</summary>
	public IEnumerable<nint> Nodes() {
		// Read once: the game does not change a map under a plugin mid-call, and this keeps the loop on one array.
		nint tree = Tree;
		int allocated = Read<int>(tree);   // nodes ever handed out, in use or back on the free list
		nint nodes = Read<nint>(tree + 8);
		if (nodes == 0) yield break;
		for (int i = 0; i < allocated; i++) {
			nint node = nodes + (nint)i * _stride;
			// A free node's left link points at itself.
			if (Read<int>(node) != i) yield return node;
		}
	}

	public nint KeyOf(nint node) => node + _keyOffset;

	public nint ValueOf(nint node) => node + _valueOffset;

	public string StringKeyOf(nint node) => SchemaObject.ReadCString(Read<nint>(KeyOf(node)));
}

/// <summary>Alignment of the plain values the generator maps: the size of a number, 4 for a vector of floats.</summary>
internal static class ValueLayout<T> where T : unmanaged {
	public static readonly int Size = Unsafe.SizeOf<T>();
	public static readonly int Alignment = Compute();

	private static int Compute() {
		var type = typeof(T).IsEnum ? Enum.GetUnderlyingType(typeof(T)) : typeof(T);
		if (type.IsPrimitive) return Unsafe.SizeOf<T>();
		if (type == typeof(Color32)) return 1;
		return 4;   // Vector2, Vector3, Vector4, Quaternion
	}
}

/// <summary>
/// A <c>CUtlDict</c>, or a <c>CUtlOrderedMap</c> keyed by a string, whose values are structs:
/// an ability's properties by name. Read-only; the values themselves can be written through.
/// </summary>
public readonly unsafe struct SchemaDict<T> : IReadOnlyCollection<KeyValuePair<string, T>> where T : SchemaObject, ISchemaClass<T> {
	private readonly TreeStorage _tree;

	internal SchemaDict(SchemaObject owner, SchemaField field)
		=> _tree = new(owner, field, sizeof(nint), sizeof(nint), SchemaClassSize<T>.Value, SchemaClassSize<T>.Alignment);

	public int Count => _tree.Count;

	/// <summary>The keys, in the order the game added them (the order of the data file).</summary>
	public IEnumerable<string> Keys => this.Select(pair => pair.Key);

	/// <summary>The value under <paramref name="key"/>, or null. Keys compare without regard to case, as the game's do.</summary>
	public T? this[string key] {
		get {
			foreach (var pair in this)
				if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
			return null;
		}
	}

	public bool ContainsKey(string key) => this[key] != null;

	public IEnumerator<KeyValuePair<string, T>> GetEnumerator() {
		foreach (nint node in _tree.Nodes())
			yield return new(_tree.StringKeyOf(node), SchemaObject.At<T>(_tree.ValueOf(node)));
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A <c>CUtlDict</c>, or a <c>CUtlOrderedMap</c> keyed by a string, of plain values. Read-only.</summary>
public readonly unsafe struct SchemaValueDict<T> : IReadOnlyCollection<KeyValuePair<string, T>> where T : unmanaged {
	private readonly TreeStorage _tree;

	internal SchemaValueDict(SchemaObject owner, SchemaField field)
		=> _tree = new(owner, field, sizeof(nint), sizeof(nint), ValueLayout<T>.Size, ValueLayout<T>.Alignment);

	public int Count => _tree.Count;

	/// <summary>The value under <paramref name="key"/>, or null. Keys compare without regard to case, as the game's do.</summary>
	public T? this[string key] {
		get {
			foreach (var pair in this)
				if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
			return null;
		}
	}

	public IEnumerator<KeyValuePair<string, T>> GetEnumerator() {
		foreach (nint node in _tree.Nodes())
			yield return new(_tree.StringKeyOf(node), TreeStorage.Read<T>(_tree.ValueOf(node)));
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A <c>CUtlOrderedMap</c> from a plain key (an enum, a number) to a struct. Read-only; the values can be written through.</summary>
public readonly unsafe struct SchemaMap<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
	where TKey : unmanaged where TValue : SchemaObject, ISchemaClass<TValue> {
	private readonly TreeStorage _tree;

	internal SchemaMap(SchemaObject owner, SchemaField field)
		=> _tree = new(owner, field, ValueLayout<TKey>.Size, ValueLayout<TKey>.Alignment, SchemaClassSize<TValue>.Value, SchemaClassSize<TValue>.Alignment);

	public int Count => _tree.Count;

	/// <summary>The value under <paramref name="key"/>, or null.</summary>
	public TValue? this[TKey key] {
		get {
			foreach (var pair in this)
				if (EqualityComparer<TKey>.Default.Equals(pair.Key, key)) return pair.Value;
			return null;
		}
	}

	public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
		foreach (nint node in _tree.Nodes())
			yield return new(TreeStorage.Read<TKey>(_tree.KeyOf(node)), SchemaObject.At<TValue>(_tree.ValueOf(node)));
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A <c>CUtlOrderedMap</c> from a plain key to a plain value. Read-only.</summary>
public readonly unsafe struct SchemaValueMap<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
	where TKey : unmanaged where TValue : unmanaged {
	private readonly TreeStorage _tree;

	internal SchemaValueMap(SchemaObject owner, SchemaField field)
		=> _tree = new(owner, field, ValueLayout<TKey>.Size, ValueLayout<TKey>.Alignment, ValueLayout<TValue>.Size, ValueLayout<TValue>.Alignment);

	public int Count => _tree.Count;

	/// <summary>The value under <paramref name="key"/>, or null.</summary>
	public TValue? this[TKey key] {
		get {
			foreach (var pair in this)
				if (EqualityComparer<TKey>.Default.Equals(pair.Key, key)) return pair.Value;
			return null;
		}
	}

	public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
		foreach (nint node in _tree.Nodes())
			yield return new(TreeStorage.Read<TKey>(_tree.KeyOf(node)), TreeStorage.Read<TValue>(_tree.ValueOf(node)));
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
