using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>
/// Turns an entity into the generated class of what it actually is, so
/// <c>entity.Schema is Schema.CNPC_Trooper trooper</c> works on any entity.
/// </summary>
public static unsafe partial class SchemaRegistry {
	private static readonly Dictionary<string, Func<Schema.CEntityInstance>> _byClassName = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, Func<Schema.CEntityInstance>> _byDesignerName = new(StringComparer.Ordinal);

	// The game keeps one class-name string per class, so its address identifies the class
	// without decoding it on every lookup. A null factory means "not a class we generated".
	private static readonly ConcurrentDictionary<nint, Func<Schema.CEntityInstance>?> _byClassNamePointer = new();

	static SchemaRegistry() {
		AddEntityClasses(_byClassName);
		AddDesignerNames(_byDesignerName);
	}

	/// <summary>Generated: every entity class, by its schema name.</summary>
	static partial void AddEntityClasses(Dictionary<string, Func<Schema.CEntityInstance>> classes);

	/// <summary>Generated: every entity the game can create by name (<c>prop_dynamic</c>), as its class.</summary>
	static partial void AddDesignerNames(Dictionary<string, Func<Schema.CEntityInstance>> designerNames);

	/// <summary>The entity behind a packed handle as its most derived generated class, or null if it is gone.</summary>
	public static Schema.CEntityInstance? Resolve(uint entityHandle) => Resolve(entityHandle, out _);

	/// <summary>The entity behind a packed handle as a <typeparamref name="T"/>, or null if it is gone or is not one.</summary>
	public static T? Resolve<T>(uint entityHandle) where T : Schema.CEntityInstance, ISchemaClass<T> {
		var entity = Resolve(entityHandle, out bool known);
		if (entity is T typed) return typed;
		if (entity is null || known) return null;
		// A class newer than this assembly: ask the game whether it derives from T.
		return DerivesFrom(entity.Handle, T.NativeName) ? SchemaObject.ForEntityHandle<T>(entityHandle) : null;
	}

	/// <summary>
	/// A <typeparamref name="T"/> view for a curated wrapper, which has already established the
	/// entity's class. Never null: a dead entity gives a view whose <see cref="SchemaObject.IsValid"/> is false.
	/// </summary>
	internal static T Bridge<T>(uint entityHandle) where T : Schema.CEntityInstance, ISchemaClass<T>
		=> Resolve(entityHandle, out _) as T ?? SchemaObject.ForEntityHandle<T>(entityHandle);

	private static Schema.CEntityInstance? Resolve(uint entityHandle, out bool known) {
		known = false;
		if (entityHandle == CBaseEntity.InvalidEntityHandle) return null;
		void* pointer = NativeInterop.GetEntityFromHandle(entityHandle);
		if (pointer == null) return null;

		byte* className = NativeInterop.GetEntityClassname(pointer);
		var factory = _byClassNamePointer.GetOrAdd((nint)className, static p => {
			string name = Marshal.PtrToStringUTF8(p) ?? "";
			return _byClassName.GetValueOrDefault(name);
		});

		// A class the server never networks has no class name here; its designer name still tells.
		if (factory == null) {
			string designerName = Marshal.PtrToStringUTF8((nint)NativeInterop.GetEntityDesignerName(pointer)) ?? "";
			_byDesignerName.TryGetValue(designerName, out factory);
		}

		known = factory != null;
		Schema.CEntityInstance entity = factory != null ? factory() : new Schema.CBaseEntity();
		entity.BindEntity(entityHandle);
		return entity;
	}

	private static bool DerivesFrom(nint entity, string className) {
		Span<byte> utf8 = Utf8.Encode(className, stackalloc byte[Utf8.Size(className)]);
		fixed (byte* p = utf8)
			return NativeInterop.EntityDerivesFrom((void*)entity, p) != 0;
	}
}

/// <summary>Queries over the server's entities, as generated schema classes.</summary>
public static unsafe class GameEntities {
	// MAX_ENTITY_LISTS (64) * MAX_ENTITIES_IN_LIST (512)
	private const int MaxEntities = 32768;

	/// <summary>Every entity on the server, each as its most derived generated class.</summary>
	public static IEnumerable<Schema.CEntityInstance> All() {
		var list = new List<Schema.CEntityInstance>();
		for (int i = 0; i < MaxEntities; i++) {
			void* pointer = NativeInterop.GetEntityByIndex(i);
			if (pointer == null) continue;
			if (SchemaRegistry.Resolve(NativeInterop.GetEntityHandle(pointer)) is { } entity)
				list.Add(entity);
		}
		return list;
	}

	/// <summary>Every entity that is a <typeparamref name="T"/> or derives from it.</summary>
	public static IEnumerable<T> OfType<T>() where T : Schema.CEntityInstance, ISchemaClass<T> {
		var list = new List<T>();
		for (int i = 0; i < MaxEntities; i++) {
			void* pointer = NativeInterop.GetEntityByIndex(i);
			if (pointer == null) continue;
			if (SchemaRegistry.Resolve<T>(NativeInterop.GetEntityHandle(pointer)) is { } entity)
				list.Add(entity);
		}
		return list;
	}

	/// <summary>The entity with this index as its most derived generated class, or null.</summary>
	public static Schema.CEntityInstance? FromIndex(int index) {
		void* pointer = NativeInterop.GetEntityByIndex(index);
		return pointer == null ? null : SchemaRegistry.Resolve(NativeInterop.GetEntityHandle(pointer));
	}

	/// <summary>The entity behind a packed handle as its most derived generated class, or null.</summary>
	public static Schema.CEntityInstance? FromHandle(uint entityHandle) => SchemaRegistry.Resolve(entityHandle);
}

public static partial class Schema {
	public partial class CEntityInstance {
		/// <summary>Packed entity handle (serial and index): this entity's identity across frames.</summary>
		public uint EntityHandle => BoundEntityHandle;

		/// <summary>Entity index, or -1 if this view is not of an entity.</summary>
		public int EntityIndex => EntityHandle == Api.CBaseEntity.InvalidEntityHandle ? -1 : (int)(EntityHandle & 0x3FFF);

		/// <summary>The curated wrapper for this entity, or null if it is gone.</summary>
		public Api.CBaseEntity? Entity => Api.CBaseEntity.FromHandle(EntityHandle);

		/// <summary>This entity as a <typeparamref name="T"/>, or null if it is gone or is not one.</summary>
		public T? As<T>() where T : CEntityInstance, ISchemaClass<T> => this as T ?? SchemaRegistry.Resolve<T>(EntityHandle);

		/// <summary>True if this entity is a <typeparamref name="T"/> or derives from it.</summary>
		public bool Is<T>() where T : CEntityInstance, ISchemaClass<T> => As<T>() != null;

		/// <summary>
		/// Sends this entity an input, as a map's output would. <paramref name="value"/> is the
		/// input's parameter as text. Generated <c>Input…</c> methods call this with the right name.
		/// </summary>
		public void FireInput(string input, string? value = null, CEntityInstance? activator = null, CEntityInstance? caller = null)
			=> (Entity ?? throw new InvalidOperationException($"This {GetType().Name} no longer exists."))
				.AcceptInput(input, activator?.Entity, caller?.Entity, value);

		/// <summary>Marks this entity for removal at the end of the frame.</summary>
		public void Remove() => Entity?.Remove();
	}
}
