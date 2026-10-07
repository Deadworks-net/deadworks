using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeadworksManaged.Api;
using DeadworksManaged.Game;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// A stand-in for the game's schema system and entity list, so the generated classes can be
/// exercised against plain memory. Field offsets are whatever a test declares; an
/// undeclared field is reported missing, as the native core reports one the game lacks.
/// A field's lookup is cached for the life of the process, as it is on a server, so tests
/// that need a field to behave differently each use a field of their own.
/// </summary>
internal sealed unsafe class FakeGame : IDisposable {
	private static FakeGame? _current;

	private readonly Dictionary<(string Class, string Field), (int Offset, bool Networked)> _fields = new();
	private readonly Dictionary<string, short> _chains = new();
	private readonly Dictionary<string, int> _sizes = new();
	private readonly Dictionary<uint, nint> _entities = new();
	private readonly Dictionary<nint, (nint ClassName, nint DesignerName)> _names = new();
	private readonly Dictionary<string, nint> _strings = new();
	private readonly List<nint> _allocations = [];

	/// <summary>Every state-change notification, as (object, field offset, chain offset).</summary>
	public List<(nint Object, int Offset, short Chain)> Notifications { get; } = [];

	public FakeGame() {
		_current = this;
		var callbacks = default(NativeCallbacks);
		callbacks.GetSchemaField = (nint)(delegate* unmanaged[Cdecl]<byte*, byte*, SchemaFieldResult*, void>)&GetSchemaField;
		callbacks.NotifyStateChanged = (nint)(delegate* unmanaged[Cdecl]<void*, int, short, int, void>)&NotifyStateChanged;
		callbacks.GetEntityFromHandle = (nint)(delegate* unmanaged[Cdecl]<uint, void*>)&GetEntityFromHandle;
		callbacks.GetEntityHandle = (nint)(delegate* unmanaged[Cdecl]<void*, uint>)&GetEntityHandle;
		callbacks.GetEntityClassname = (nint)(delegate* unmanaged[Cdecl]<void*, byte*>)&GetEntityClassname;
		callbacks.GetEntityDesignerName = (nint)(delegate* unmanaged[Cdecl]<void*, byte*>)&GetEntityDesignerName;
		callbacks.EntityDerivesFrom = (nint)(delegate* unmanaged[Cdecl]<void*, byte*, byte>)&EntityDerivesFrom;
		callbacks.GetUtlVectorSize = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&GetUtlVectorSize;
		callbacks.GetUtlVectorData = (nint)(delegate* unmanaged[Cdecl]<void*, void*>)&GetUtlVectorData;
		callbacks.GetSchemaClassSize = (nint)(delegate* unmanaged[Cdecl]<byte*, int>)&GetSchemaClassSize;
		NativeInterop.Bind(&callbacks);
	}

	public void Dispose() {
		var none = default(NativeCallbacks);
		NativeInterop.Bind(&none);
		foreach (var allocation in _allocations) NativeMemory.Free((void*)allocation);
		_current = null;
	}

	public FakeGame Field(string className, string field, int offset, bool networked = false) {
		_fields[(className, field)] = (offset, networked);
		return this;
	}

	public FakeGame Chain(string className, short offset) { _chains[className] = offset; return this; }

	public FakeGame Size(string className, int size) { _sizes[className] = size; return this; }

	public nint Allocate(int bytes) {
		nint memory = (nint)NativeMemory.AllocZeroed((nuint)bytes);
		_allocations.Add(memory);
		return memory;
	}

	private nint Text(string text) {
		if (_strings.TryGetValue(text, out var existing)) return existing;
		nint memory = Marshal.StringToCoTaskMemUTF8(text);
		return _strings[text] = memory;
	}

	/// <summary>Adds an entity of <paramref name="className"/> and returns its packed handle.</summary>
	public uint Entity(string className, string designerName, out nint memory, int bytes = 4096) {
		memory = Allocate(bytes);
		uint handle = (uint)(_entities.Count + 1) | 0x10000;
		_entities[handle] = memory;
		_names[memory] = (Text(className), Text(designerName));
		return handle;
	}

	public void Kill(uint handle) => _entities.Remove(handle);

	private static string Read(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? "";

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void GetSchemaField(byte* className, byte* fieldName, SchemaFieldResult* result) {
		string cls = Read(className);
		bool found = _current!._fields.TryGetValue((cls, Read(fieldName)), out var field);
		result->Offset = field.Offset;
		result->ChainOffset = _current._chains.GetValueOrDefault(cls);
		result->Networked = (byte)(field.Networked ? 1 : 0);
		result->Found = found ? SchemaFieldResult.Present : SchemaFieldResult.Missing;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void NotifyStateChanged(void* entity, int offset, short chain, int networkStateChangedOffset)
		=> _current!.Notifications.Add(((nint)entity, offset, chain));

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void* GetEntityFromHandle(uint handle) => (void*)_current!._entities.GetValueOrDefault(handle);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static uint GetEntityHandle(void* entity) {
		foreach (var (handle, memory) in _current!._entities)
			if (memory == (nint)entity) return handle;
		return CBaseEntity.InvalidEntityHandle;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static byte* GetEntityClassname(void* entity) => (byte*)_current!._names[(nint)entity].ClassName;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static byte* GetEntityDesignerName(void* entity) => (byte*)_current!._names[(nint)entity].DesignerName;

	// The fake has no class hierarchy: a class "derives" from a name it starts with.
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static byte EntityDerivesFrom(void* entity, byte* baseClassName)
		=> (byte)(Read((byte*)_current!._names[(nint)entity].ClassName).StartsWith(Read(baseClassName)) ? 1 : 0);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int GetUtlVectorSize(void* vector) => *(int*)vector;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void* GetUtlVectorData(void* vector) => *(void**)((byte*)vector + 8);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int GetSchemaClassSize(byte* className) => _current!._sizes.GetValueOrDefault(Read(className));
}

public unsafe class GameSchemaTests {
	[Fact]
	public void Value_fields_read_and_write_at_the_offset_the_game_reports() {
		using var game = new FakeGame().Field("CBaseEntity", "m_iHealth", 0x354).Field("CBaseEntity", "m_vecAbsVelocity", 0x400);
		uint handle = game.Entity("CBaseEntity", "info_target", out nint memory);
		var entity = SchemaRegistry.Resolve<Schema.CBaseEntity>(handle)!;

		*(int*)(memory + 0x354) = 750;
		Assert.Equal(750, entity.m_iHealth);

		entity.m_iHealth = 125;
		entity.m_vecAbsVelocity = new Vector3(1, 2, 3);
		Assert.Equal(125, *(int*)(memory + 0x354));
		Assert.Equal(new Vector3(1, 2, 3), *(Vector3*)(memory + 0x400));
		Assert.Empty(game.Notifications);
	}

	[Fact]
	public void Writing_a_networked_entity_field_notifies_the_entity() {
		using var game = new FakeGame().Field("CBaseEntity", "m_iMaxHealth", 0x358, networked: true);
		uint handle = game.Entity("CBaseEntity", "info_target", out nint memory);

		SchemaRegistry.Resolve<Schema.CBaseEntity>(handle)!.m_iMaxHealth = 1;

		Assert.Equal([(memory, 0x358, (short)0)], game.Notifications);
	}

	[Fact]
	public void A_struct_inside_an_entity_notifies_the_entity_at_its_offset_there() {
		using var game = new FakeGame()
			.Field("CBaseEntity", "m_pCollision", 0x300)
			.Field("CCitadelPlayerPawn", "m_CCitadelAbilityComponent", 0x1000)
			.Field("CCitadelAbilityComponent", "m_flTimeScale", 0x40, networked: true);
		uint handle = game.Entity("CCitadelPlayerPawn", "player", out nint memory, bytes: 0x2000);
		var pawn = SchemaRegistry.Resolve<Schema.CCitadelPlayerPawn>(handle)!;

		pawn.m_CCitadelAbilityComponent.m_flTimeScale = 3f;

		Assert.Equal(3f, *(float*)(memory + 0x1040));
		Assert.Equal([(memory, 0x1040, (short)0)], game.Notifications);
	}

	[Fact]
	public void A_struct_with_its_own_network_chain_notifies_through_it() {
		using var game = new FakeGame()
			.Field("CCitadelPlayerPawn", "m_CCitadelAbilityComponent", 0x1000)
			.Field("CCitadelAbilityComponent", "m_flParticleTimeScale", 0x44, networked: true)
			.Chain("CCitadelAbilityComponent", 0x18);
		uint handle = game.Entity("CCitadelPlayerPawn", "player", out nint memory, bytes: 0x2000);

		SchemaRegistry.Resolve<Schema.CCitadelPlayerPawn>(handle)!.m_CCitadelAbilityComponent.m_flParticleTimeScale = 3f;

		Assert.Equal([(memory + 0x1000, 0x44, (short)0x18)], game.Notifications);
	}

	[Fact]
	public void A_field_the_game_lacks_throws_with_its_name() {
		using var game = new FakeGame();
		uint handle = game.Entity("CBaseEntity", "info_target", out _);
		var entity = SchemaRegistry.Resolve<Schema.CBaseEntity>(handle)!;

		var error = Assert.Throws<SchemaFieldMissingException>(() => entity.m_iTeamNum);

		Assert.Equal("CBaseEntity", error.ClassName);
		Assert.Equal("m_iTeamNum", error.FieldName);
	}

	[Fact]
	public void A_view_of_a_removed_entity_throws_instead_of_reading_freed_memory() {
		using var game = new FakeGame().Field("CBaseEntity", "m_iHealth", 0x354).Field("CBaseEntity", "m_pCollision", 0x300);
		uint handle = game.Entity("CBaseEntity", "info_target", out _);
		var entity = SchemaRegistry.Resolve<Schema.CBaseEntity>(handle)!;
		Assert.True(entity.IsValid);

		game.Kill(handle);

		Assert.False(entity.IsValid);
		Assert.Throws<InvalidOperationException>(() => entity.m_iHealth);
	}

	[Fact]
	public void An_entity_resolves_to_its_most_derived_generated_class() {
		using var game = new FakeGame();
		uint trooper = game.Entity("CNPC_Trooper", "npc_trooper", out _);

		var entity = SchemaRegistry.Resolve(trooper);

		Assert.IsType<Schema.CNPC_Trooper>(entity);
		Assert.True(entity is Schema.CAI_BaseNPC);
		Assert.NotNull(SchemaRegistry.Resolve<Schema.CBaseEntity>(trooper));
		Assert.Null(SchemaRegistry.Resolve<Schema.CCitadelPlayerPawn>(trooper));
	}

	[Fact]
	public void An_entity_the_server_does_not_network_resolves_by_its_designer_name() {
		using var game = new FakeGame();
		uint relay = game.Entity("", "logic_relay", out _);

		Assert.IsType<Schema.CLogicRelay>(SchemaRegistry.Resolve(relay));
	}

	[Fact]
	public void A_class_newer_than_the_assembly_is_asked_of_the_game() {
		using var game = new FakeGame();
		uint handle = game.Entity("CBaseEntity_AddedNextPatch", "something_new", out _);

		Assert.IsType<Schema.CBaseEntity>(SchemaRegistry.Resolve(handle));
		Assert.NotNull(SchemaRegistry.Resolve<Schema.CBaseEntity>(handle));
		Assert.Null(SchemaRegistry.Resolve<Schema.CNPC_Trooper>(handle));
	}

	[Fact]
	public void Handles_resolve_to_entities_and_take_them_back() {
		using var game = new FakeGame().Field("CBaseEntity", "m_hOwnerEntity", 0x500);
		uint owner = game.Entity("CNPC_Trooper", "npc_trooper", out _);
		uint child = game.Entity("CBaseEntity", "info_target", out nint memory);
		var entity = SchemaRegistry.Resolve<Schema.CBaseEntity>(child)!;

		*(uint*)(memory + 0x500) = CBaseEntity.InvalidEntityHandle;
		Assert.Null(entity.m_hOwnerEntity);

		entity.m_hOwnerEntity = SchemaRegistry.Resolve<Schema.CBaseEntity>(owner);
		Assert.Equal(owner, *(uint*)(memory + 0x500));
		Assert.IsType<Schema.CNPC_Trooper>(entity.m_hOwnerEntity);

		entity.m_hOwnerEntity = null;
		Assert.Equal(CBaseEntity.InvalidEntityHandle, *(uint*)(memory + 0x500));
	}

	[Fact]
	public void Strings_read_through_the_pointer_and_fixed_arrays_in_place() {
		using var game = new FakeGame()
			.Field("CEntityInstance", "m_iszPrivateVScripts", 0x100)
			.Field("CCitadelPlayerPawn", "m_arrGoldSources", 0x800, networked: true);
		uint handle = game.Entity("CCitadelPlayerPawn", "player", out nint memory);
		var pawn = SchemaRegistry.Resolve<Schema.CCitadelPlayerPawn>(handle)!;

		Assert.Equal("", pawn.m_iszPrivateVScripts);
		pawn.m_iszPrivateVScripts = "scripts/thing.lua";
		Assert.Equal("scripts/thing.lua", pawn.m_iszPrivateVScripts);

		var gold = pawn.m_arrGoldSources;
		gold[2] = 77;
		Assert.Equal(77, *(int*)(memory + 0x800 + 2 * sizeof(int)));
		Assert.Equal(77, gold[2]);
		Assert.Equal((memory, 0x800 + 2 * sizeof(int), (short)0), game.Notifications[^1]);
		Assert.Throws<ArgumentOutOfRangeException>(() => gold[gold.Count]);
	}

	[Fact]
	public void Buffer_strings_read_from_the_field_or_from_the_heap() {
		using var game = new FakeGame()
			.Field("CitadelAbilityVData", "m_skillshotMissParticle", 0x100)
			.Field("CitadelAbilityVData", "m_strAbilityImage", 0x200)
			.Field("CitadelAbilityVData", "m_strCastSound", 0x300);
		nint memory = game.Allocate(0x400);
		var vdata = SchemaObject.At<Schema.CitadelAbilityVData>(memory);

		// In the field: length, then a size with the stack-allocated bit, then the text.
		*(uint*)(memory + 0x100) = 5;
		*(uint*)(memory + 0x104) = 0xC00000C8;
		"hello"u8.CopyTo(new Span<byte>((void*)(memory + 0x108), 5));
		Assert.Equal("hello", vdata.m_skillshotMissParticle);

		// On the heap: the size has no stack bit and a pointer follows.
		nint heap = game.Allocate(32);
		"file://{images}/a.psd"u8.CopyTo(new Span<byte>((void*)heap, 21));
		*(uint*)(memory + 0x200) = 21 | 0x80000000;
		*(uint*)(memory + 0x204) = 0x80000020;
		*(nint*)(memory + 0x208) = heap;
		Assert.Equal("file://{images}/a.psd", vdata.m_strAbilityImage);

		// Empty: no length, whatever the rest says.
		*(uint*)(memory + 0x304) = 0xC0000008;
		Assert.Equal("", vdata.m_strCastSound);
	}

	[Fact]
	public void Vectors_of_values_walk_the_games_buffer() {
		using var game = new FakeGame().Field("CBaseEntity", "m_aThinkFunctions", 0x200);
		uint handle = game.Entity("CBaseEntity", "info_target", out nint memory);
		nint data = game.Allocate(4 * sizeof(int));
		for (int i = 0; i < 4; i++) *(int*)(data + i * sizeof(int)) = i * 10;
		*(int*)(memory + 0x200) = 4;
		*(nint*)(memory + 0x208) = data;

		var list = new SchemaValueList<int>(SchemaRegistry.Resolve<Schema.CBaseEntity>(handle)!, new SchemaField("CBaseEntity", "m_aThinkFunctions"), -1);

		Assert.Equal(4, list.Count);
		Assert.Equal([0, 10, 20, 30], list.ToArray());
		Assert.Throws<ArgumentOutOfRangeException>(() => list[4]);
	}

	[Fact]
	public void Equality_is_by_native_object() {
		using var game = new FakeGame();
		uint handle = game.Entity("CNPC_Trooper", "npc_trooper", out _);

		Assert.Equal<SchemaObject>(SchemaRegistry.Resolve(handle), SchemaRegistry.Resolve<Schema.CBaseEntity>(handle));
		Assert.NotEqual<SchemaObject>(SchemaRegistry.Resolve(handle), SchemaRegistry.Resolve(game.Entity("CNPC_Trooper", "npc_trooper", out _)));
	}

	[Fact]
	public void Spawn_keys_keep_their_typed_values() {
		var keys = new Keys.CDynamicProp { origin = new Vector3(1, 2, 3), rendercolor = new Color32(255, 0, 0), model = "models/a.vmdl" };

		Assert.Equal(new Vector3(1, 2, 3), keys.origin);
		Assert.Equal(new Color32(255, 0, 0), keys.rendercolor);
		Assert.Equal("models/a.vmdl", keys.model);

		keys.model = null;
		Assert.Null(keys.model);
	}

	[Fact]
	public void Generated_names_are_the_games_names() {
		Assert.Equal("prop_dynamic", EntityNames.prop_dynamic);
		Assert.Equal("Kill", Inputs.CBaseEntity.Kill);
		Assert.Equal("Kill", Inputs.CBaseModelEntity.Kill);
		Assert.Equal("sv_cheats", ConVars.sv_cheats.Name);
		Assert.Equal("hero_inferno", HeroNames.hero_inferno);
		Assert.True(GameBuild.Version > 6000);
	}
}
