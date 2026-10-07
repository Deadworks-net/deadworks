using System.Text.Json;
using DeadworksManaged.GameGen;
using Xunit;

namespace DeadworksManaged.Tests;

public class GameGenTests {
	// A dump small enough to read: an entity hierarchy, a strong typedef, a struct the server
	// only reaches through a field of a class another module declares, and an enum with a negative value.
	private const string Schemas = """
		{ "version": 1, "classes": [
		  { "name": "CEntityInstance", "module": "entity2", "fields": [] },
		  { "name": "CBaseEntity", "module": "server", "parents": [{ "name": "CEntityInstance", "module": "entity2" }], "fields": [
		    { "name": "m_iHealth", "offset": 8, "type": { "category": "builtin", "name": "int32" } },
		    { "name": "m_flSpawnTime", "offset": 12, "type": { "category": "declared_class", "name": "GameTime_t" } },
		    { "name": "m_hOwner", "offset": 16, "type": { "category": "atomic", "name": "CHandle", "inner": { "category": "declared_class", "name": "CBaseEntity" } } },
		    { "name": "m_Glow", "offset": 24, "type": { "category": "declared_class", "name": "CGlowProperty" } },
		    { "name": "m_sSound", "offset": 64, "type": { "category": "atomic", "name": "CSoundEventName" } },
		    { "name": "m_Curve", "offset": 148, "type": { "category": "atomic", "name": "CPiecewiseCurve" } },
		    { "name": "m_nState", "offset": 80, "type": { "category": "declared_enum", "name": "State_t" } },
		    { "name": "m_vecChildren", "offset": 88, "type": { "category": "atomic", "name": "CUtlVector", "inner": { "category": "atomic", "name": "CHandle", "inner": { "category": "declared_class", "name": "CBaseEntity" } } } },
		    { "name": "m_szName", "offset": 112, "type": { "category": "fixed_array", "count": 32, "inner": { "category": "builtin", "name": "char" } } },
		    { "name": "class", "offset": 144, "type": { "category": "builtin", "name": "bool" } }
		  ] },
		  { "name": "CProp", "module": "server", "parents": [{ "name": "CBaseEntity", "module": "server" }], "fields": [
		    { "name": "m_iHealth", "offset": 200, "type": { "category": "builtin", "name": "float32" }, "metadata": [{ "name": "MPropertyDescription", "value": "\"Shadows the base's <health>.\"" }] }
		  ] },
		  { "name": "GameTime_t", "module": "server", "fields": [{ "name": "m_Value", "offset": 0, "type": { "category": "builtin", "name": "float32" } }] },
		  { "name": "CGlowProperty", "module": "client", "fields": [
		    { "name": "m_bGlowing", "offset": 0, "type": { "category": "builtin", "name": "bool" } },
		    { "name": "m_hTarget", "offset": 4, "type": { "category": "atomic", "name": "CHandle", "inner": { "category": "declared_class", "name": "C_BaseEntity" } } }
		  ] },
		  { "name": "C_BaseEntity", "module": "client", "fields": [{ "name": "m_bClientOnly", "offset": 0, "type": { "category": "builtin", "name": "bool" } }] },
		  { "name": "CEntitySubclassVDataBase", "module": "server", "fields": [] },
		  { "name": "CPropVData", "module": "server", "parents": [{ "name": "CEntitySubclassVDataBase", "module": "server" }], "fields": [
		    { "name": "m_mapProperties", "offset": 40, "type": { "category": "atomic", "name": "CUtlDict", "inner": { "category": "declared_class", "name": "CGlowProperty" } } },
		    { "name": "m_mapScales", "offset": 80, "type": { "category": "atomic", "name": "CUtlOrderedMap", "inner": { "category": "declared_enum", "name": "State_t" }, "inner2": { "category": "builtin", "name": "float32" } } },
		    { "name": "m_mapByName", "offset": 120, "type": { "category": "atomic", "name": "CUtlOrderedMap", "inner": { "category": "atomic", "name": "CGlobalSymbol" }, "inner2": { "category": "declared_class", "name": "CGlowProperty" } } },
		    { "name": "m_mapSounds", "offset": 160, "type": { "category": "atomic", "name": "CUtlOrderedMap", "inner": { "category": "declared_enum", "name": "State_t" }, "inner2": { "category": "atomic", "name": "CSoundEventName" } } },
		    { "name": "m_BreakModifier", "offset": 200, "type": { "category": "atomic", "name": "CEmbeddedSubclass", "inner": { "category": "declared_class", "name": "CPropModifier" } } },
		    { "name": "m_vecModifiers", "offset": 216, "type": { "category": "atomic", "name": "CUtlVector", "inner": { "category": "atomic", "name": "CEmbeddedSubclass", "inner": { "category": "declared_class", "name": "CBaseModifier" } } } },
		    { "name": "m_Scale", "offset": 240, "type": { "category": "atomic", "name": "CEmbeddedSubclass", "inner": { "category": "declared_class", "name": "CGlowProperty" } } }
		  ] },
		  { "name": "CBaseModifier", "module": "server", "fields": [] },
		  { "name": "CModifierVData", "module": "server", "parents": [{ "name": "CEntitySubclassVDataBase", "module": "server" }], "fields": [] },
		  { "name": "CPropModifier", "module": "server", "parents": [{ "name": "CBaseModifier", "module": "server" }], "fields": [] },
		  { "name": "CPropModifierVData", "module": "server", "parents": [{ "name": "CModifierVData", "module": "server" }], "fields": [] },
		  { "name": "CClientOnly", "module": "client", "fields": [] },
		  { "name": "Outer::Inner_t", "module": "server", "fields": [] }
		], "enums": [
		  { "name": "State_t", "module": "server", "alignment": "uint8_t", "members": [{ "name": "STATE_NONE", "value": -1 }, { "name": "State_t", "value": 0 }] },
		  { "name": "Big_t", "module": "server", "alignment": "uint64_t", "members": [{ "name": "BIG", "value": "18446744073709551615" }] },
		  { "name": "Bits_t", "module": "server", "alignment": "uint32_t", "members": [
		    { "name": "BITS_NONE", "value": 0 }, { "name": "BITS_A", "value": 1 }, { "name": "BITS_B", "value": 2 }, { "name": "BITS_C", "value": 4 }] },
		  { "name": "Steps_t", "module": "server", "alignment": "uint32_t", "members": [
		    { "name": "STEP_0", "value": 0 }, { "name": "STEP_1", "value": 1 }, { "name": "STEP_2", "value": 2 }, { "name": "STEP_3", "value": 3 }, { "name": "STEP_4", "value": 4 }] }
		] }
		""";

	private const string Entities = """
		{ "version": 1, "classes": [
		  { "name": "prop_thing", "datamap": "CProp", "cpp_class": "CProp" },
		  { "name": "info_base", "datamap": "CBaseEntity", "cpp_class": "CBaseEntity" }
		], "datamaps": [
		  { "name": "CBaseEntity", "fields": [
		    { "name": "m_iHealth", "type": "FIELD_INT32", "flags": ["key"], "external_name": "health" },
		    { "name": "m_old", "type": "FIELD_STRING", "flags": ["key", "removed_keyfield"], "external_name": "old" },
		    { "name": "m_OnUser", "type": "FIELD_CUSTOM", "flags": ["key", "output"], "external_name": "OnUser" }
		  ] },
		  { "name": "CProp", "base": "CBaseEntity", "fields": [
		    { "name": "m_clr", "type": "FIELD_COLOR32", "flags": ["key"], "external_name": "rendercolor" },
		    { "name": "m_nState", "type": "FIELD_INT32", "flags": ["key", "enum"], "enum": "State_t", "external_name": "state" },
		    { "name": "m_dup", "type": "FIELD_INT32", "flags": ["key"], "external_name": "Health" }
		  ] }
		], "io": [
		  { "name": "CBaseEntity", "inputs": [
		    { "name": "Kill", "params": [] },
		    { "name": "SetHealth", "params": [{ "name": "param", "type": "PVAL_INT" }], "description": "Sets health" },
		    { "name": "GetOrigin", "params": [], "returns": [{ "name": "retval", "type": "PVAL_VEC3" }] },
		    { "name": "SetParent", "params": [{ "name": "param", "type": "PVAL_EHANDLE" }] }
		  ], "outputs": [{ "name": "OnUser", "params": [] }] },
		  { "name": "CProp", "inputs": [{ "name": "Kill", "params": [] }, { "name": "Break", "params": [] }], "outputs": [] }
		] }
		""";

	// One file of entity subclasses, as the modding database serves it.
	private const string Units = """
		{ "version": 1, "path": "scripts/units.vdata", "entries": {
		  "thing_base": { "m_iHealth": 1 },
		  "thing_small": { "_class": "prop_thing", "_base": "thing_base" },
		  "info_base": { "_class": "info_base" }
		} }
		""";

	private static (Dictionary<string, string> Files, SchemaEmitter Schema, SchemaModel Model) Generate(
		Dictionary<string, string>? warnings = null, ApiScanner? api = null, bool units = false, string? survey = null) {
		using var schemas = JsonDocument.Parse(Schemas);
		using var entities = JsonDocument.Parse(Entities);
		var model = SchemaModel.Load(schemas.RootElement);
		var entityModel = EntityModel.Load(entities.RootElement);
		if (units) {
			using var file = JsonDocument.Parse(Units);
			entityModel.AddSubclasses(file.RootElement);
		}
		string directory = Path.Combine(Path.GetTempPath(), "gamegen-" + Guid.NewGuid().ToString("N"));
		try {
			var output = new OutputSet(directory);
			var emitter = new SchemaEmitter(model, entityModel, api ?? new ApiScanner(), warnings ?? []);
			emitter.Emit(output);
			SpawnSurvey? tried = null;
			if (survey != null) {
				using var surveyJson = JsonDocument.Parse(survey);
				tried = SpawnSurvey.Load(surveyJson.RootElement);
			}
			new EntityEmitter(entityModel, model, emitter.Ids, tried).Emit(output);
			output.Flush();
			var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
				.ToDictionary(f => Path.GetRelativePath(directory, f).Replace('\\', '/'), File.ReadAllText);
			return (files, emitter, model);
		}
		finally {
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void The_model_is_what_the_server_reaches_whichever_module_declares_it() {
		var (_, _, model) = Generate();

		Assert.Contains("CGlowProperty", model.Classes.Keys);   // reached through CBaseEntity.m_Glow
		Assert.Contains("CEntityInstance", model.Classes.Keys); // reached as a parent
		Assert.DoesNotContain("CClientOnly", model.Classes.Keys);
		Assert.DoesNotContain("C_BaseEntity", model.Classes.Keys); // a shared struct's client handle is the server class here
		Assert.Equal("CBaseEntity", model.Classes["CGlowProperty"].Fields.Single(f => f.Name == "m_hTarget").Type.Inner!.Name);
		Assert.True(model.IsEntity("CProp"));
		Assert.False(model.IsEntity("CGlowProperty"));
		Assert.Equal("float32", model.BoxedScalar("GameTime_t"));
	}

	[Fact]
	public void Fields_get_the_type_their_layout_allows() {
		string code = Generate().Files["Schema/Classes/CBaseEntity.cs"];

		Assert.Contains("public partial class CBaseEntity : CEntityInstance, ISchemaClass<CBaseEntity>", code);
		Assert.Contains("public int m_iHealth { get => Get<int>(__m_iHealth); set => Set(__m_iHealth, value); }", code);
		Assert.Contains("public float m_flSpawnTime {", code);                       // a strong typedef is its value
		Assert.Contains("public CBaseEntity? m_hOwner { get => GetHandle<CBaseEntity>(__m_hOwner);", code);
		Assert.Contains("public CGlowProperty m_Glow => Embedded<CGlowProperty>(__m_Glow);", code);
		Assert.Contains("public State_t m_nState {", code);
		Assert.Contains("public SchemaHandleList<CBaseEntity> m_vecChildren => new(this, __m_vecChildren, -1);", code);
		Assert.Contains("public string m_szName => GetChars(__m_szName, 32);", code);
		Assert.Contains("public string m_sSound => GetBufferString(__m_sSound);", code);
		Assert.Contains("public RawField m_Curve => Raw(__m_Curve, \"CPiecewiseCurve\");", code); // unmapped, but present
		Assert.Contains("public bool @class {", code);                               // a keyword is escaped, not renamed
		Assert.Contains("new(\"CBaseEntity\", \"class\")", code);
	}

	[Fact]
	public void A_field_that_shadows_a_base_field_hides_it_and_documentation_is_escaped() {
		string code = Generate().Files["Schema/Classes/CProp.cs"];

		Assert.Contains("public new float m_iHealth {", code);
		Assert.Contains("Shadows the base's &lt;health&gt;.", code);
	}

	[Fact]
	public void Inputs_become_methods_once_and_only_where_the_parameter_can_be_passed() {
		var files = Generate().Files;
		string entity = files["Schema/Classes/CBaseEntity.cs"], prop = files["Schema/Classes/CProp.cs"];

		Assert.Contains("public void InputKill() => FireInput(\"Kill\");", entity);
		Assert.Contains("public void InputSetHealth(int value) => FireInput(\"SetHealth\", Spawner.InputText(value));", entity);
		Assert.DoesNotContain("InputGetOrigin", entity);  // a query returns a value; an input cannot
		Assert.DoesNotContain("InputSetParent", entity);  // an entity handle has no text form
		Assert.Contains("public void InputBreak()", prop);
		Assert.DoesNotContain("InputKill", prop);         // inherited
	}

	[Fact]
	public void Enums_keep_their_storage_size_and_values() {
		var files = Generate().Files;

		Assert.Contains("public enum State_t : sbyte", files["Schema/Enums/State_t.cs"]); // one byte, signed for the -1
		Assert.Contains("STATE_NONE = -1,", files["Schema/Enums/State_t.cs"]);
		Assert.Contains("State_t_ = 0,", files["Schema/Enums/State_t.cs"]);               // a member cannot share the enum's name
		Assert.Contains("BIG = 18446744073709551615,", files["Schema/Enums/Big_t.cs"]);
	}

	[Fact]
	public void An_enum_of_single_bits_is_a_flags_enum_and_a_sequence_is_not() {
		var files = Generate().Files;

		Assert.Contains("[Flags]", files["Schema/Enums/Bits_t.cs"]);
		Assert.DoesNotContain("[Flags]", files["Schema/Enums/Steps_t.cs"]); // 1, 2 and 4 are there, but so is 3
		Assert.DoesNotContain("[Flags]", files["Schema/Enums/State_t.cs"]);
	}

	[Fact]
	public void A_curated_wrapper_and_its_schema_class_lead_to_each_other() {
		var api = new ApiScanner();
		api.ScanFile("NativeEntity.cs", "public abstract class NativeEntity { }");
		api.ScanFile("CBaseEntity.cs", "public unsafe class CBaseEntity : NativeEntity { }");
		api.ScanFile("CProp.cs", "public sealed class CProp : CBaseEntity { }");
		api.ScanFile("CGlowProperty.cs", "public sealed class CGlowProperty : NativeEntity { }");
		api.ScanFile("CNotSchema.cs", "public sealed class CNotSchema : NativeEntity { }");
		var files = Generate(api: api).Files;
		string bridge = files["Schema/SchemaBridge.g.cs"];

		// In the API's namespace, so .Schema needs no using directive of its own.
		Assert.Contains("namespace DeadworksManaged.Api;", bridge);
		Assert.Contains("extension(CProp self)", bridge);
		Assert.Contains("public Schema.CProp Schema => SchemaRegistry.Bridge<Schema.CProp>(self.EntityHandle);", bridge);
		Assert.Contains("public Schema.CGlowProperty Schema => SchemaObject.At<Schema.CGlowProperty>(self.Handle);", bridge); // not an entity: by pointer
		Assert.DoesNotContain("CNotSchema", bridge);

		// And back, typed. CBaseEntity's is the runtime's own.
		Assert.Contains("public new global::DeadworksManaged.Api.CProp? Entity => SchemaRegistry.Wrapper<global::DeadworksManaged.Api.CProp>(EntityHandle);", files["Schema/Classes/CProp.cs"]);
		Assert.DoesNotContain("public new", files["Schema/Classes/CBaseEntity.cs"]);
	}

	[Fact]
	public void Names_that_are_not_identifiers_are_adapted_and_the_games_name_kept() {
		var (files, emitter, _) = Generate();

		Assert.Equal("Outer__Inner_t", emitter.Ids["Outer::Inner_t"]);
		Assert.Contains("NativeName => \"Outer::Inner_t\";", files["Schema/Classes/Outer__Inner_t.cs"]);
	}

	[Fact]
	public void A_setter_warning_marks_the_setter_obsolete() {
		string code = Generate(new() { ["CBaseEntity.m_iHealth"] = "Use Hurt." }).Files["Schema/Classes/CBaseEntity.cs"];

		Assert.Contains("public int m_iHealth { get => Get<int>(__m_iHealth); [Obsolete(\"Use Hurt.\")] set => Set(__m_iHealth, value); }", code);
	}

	[Fact]
	public void Key_classes_follow_the_datamap_chain_and_declare_each_key_once() {
		var files = Generate().Files;
		string baseKeys = files["Entities/Keys/CBaseEntity.cs"], propKeys = files["Entities/Keys/CProp.cs"];

		Assert.Contains("public class CBaseEntity : EntityKeys", baseKeys);
		Assert.Contains("public int? health { get => GetValue<int>(\"health\"); set => SetKey(\"health\", value); }", baseKeys);
		Assert.DoesNotContain("\"old\"", baseKeys);     // removed by the game
		Assert.DoesNotContain("OnUser", baseKeys);      // an output, not a key
		Assert.Contains("public class CProp : CBaseEntity", propKeys);
		Assert.Contains("public Color32? rendercolor {", propKeys);
		Assert.Contains("public Schema.State_t? state {", propKeys);
		Assert.DoesNotContain("Health", propKeys);      // keys are case-insensitive: the base already has it
	}

	[Fact]
	public void Every_entity_gets_a_spawn_function_typed_as_its_class() {
		var files = Generate().Files;

		Assert.Contains("public static Schema.CProp? prop_thing(Keys.CProp? keys = null, Action<Schema.CProp>? beforeSpawn = null) => Spawner.Create(\"prop_thing\", keys, beforeSpawn);", files["Entities/Spawn.g.cs"]);
		Assert.Contains("classes[\"CProp\"] = static () => new Schema.CProp();", files["Schema/SchemaRegistry.g.cs"]);
		Assert.Contains("designerNames[\"prop_thing\"] = static () => new Schema.CProp();", files["Schema/SchemaRegistry.g.cs"]);
		Assert.DoesNotContain("CGlowProperty", files["Schema/SchemaRegistry.g.cs"]); // not an entity
		Assert.Contains("public abstract class CProp : CBaseEntity", files["Entities/Inputs.g.cs"]);
		Assert.Contains("public const string OnUser = \"OnUser\";", files["Entities/Outputs.g.cs"]);
	}

	[Fact]
	public void An_entity_made_from_data_entries_is_spawned_through_them() {
		var files = Generate(units: true).Files;
		string spawn = files["Entities/Spawn.g.cs"];

		// The entry, as the class and with the keys of the entity it makes.
		Assert.Contains("public static Schema.CProp? thing_small(Keys.CProp? keys = null, Action<Schema.CProp>? beforeSpawn = null) => Spawner.Create(\"thing_small\", keys, beforeSpawn);", spawn);
		// Not by its own name: without an entry the game has nothing to build it from.
		Assert.DoesNotContain(" prop_thing(", spawn);
		// An entry that only other entries build on is not something to spawn.
		Assert.DoesNotContain("thing_base", spawn);
		// An entry named after its entity is one function, not two.
		Assert.Single(spawn.Split('\n'), line => line.Contains(" info_base("));
		Assert.Contains("public const string thing_small = \"thing_small\";", files["Entities/SubclassNames.g.cs"]);
	}

	[Fact]
	public void Dictionaries_and_maps_are_typed_by_what_their_keys_and_values_are() {
		string code = Generate().Files["Schema/Classes/CPropVData.cs"];

		Assert.Contains("public SchemaDict<CGlowProperty> m_mapProperties => new(this, __m_mapProperties);", code);       // a CUtlDict is keyed by a string
		Assert.Contains("public SchemaValueMap<State_t, float> m_mapScales => new(this, __m_mapScales);", code);
		Assert.Contains("public SchemaDict<CGlowProperty> m_mapByName => new(this, __m_mapByName);", code);               // so is a map keyed by a symbol
		Assert.Contains("public RawField m_mapSounds => Raw(", code);                                                     // a value the runtime cannot walk yet
	}

	[Fact]
	public void An_embedded_subclass_of_a_modifier_is_that_modifiers_data() {
		string code = Generate().Files["Schema/Classes/CPropVData.cs"];

		// A class that has data of its own names it after itself; one that does not uses its base's.
		Assert.Contains("public CPropModifierVData? m_BreakModifier => EmbeddedSubclass<CPropModifierVData>(__m_BreakModifier);", code);
		Assert.Contains("public SchemaPointerList<CModifierVData> m_vecModifiers => new(this, __m_vecModifiers, -1, 16, 8);", code);
		Assert.Contains("public RawField m_Scale => Raw(", code);   // not a modifier: what it points at is not established
	}

	[Fact]
	public void A_class_created_from_data_has_that_data_typed() {
		var files = Generate().Files;

		Assert.Contains("public CPropVData? VData => SubclassVData<CPropVData>();", files["Schema/Classes/CProp.cs"]);
		Assert.DoesNotContain(" VData =>", files["Schema/Classes/CBaseEntity.cs"]);                    // no data class of its own
		Assert.Contains("public CModifierVData? VData => ModifierData<CModifierVData>();", files["Schema/Classes/CBaseModifier.cs"]);
		Assert.Contains("public new CPropModifierVData? VData => ModifierData<CPropModifierVData>();", files["Schema/Classes/CPropModifier.cs"]);
	}

	[Fact]
	public void A_field_the_curated_api_only_sets_through_a_method_warns_on_a_raw_write() {
		var api = new ApiScanner();
		api.ScanFile("CBaseEntity.cs", """
			public unsafe class CBaseEntity : NativeEntity {
				private static readonly SchemaAccessor<int> _health = new("CBaseEntity"u8, "m_iHealth"u8);
				private static readonly SchemaAccessor<byte> _state = new("CBaseEntity"u8, "m_nState"u8);
				public int Health { get => _health.Get(Handle); set => _health.Set(Handle, value); }
				public byte State => _state.Get(Handle);
				public void SetState(byte state) => Native.SetState(Handle, state);
			}
			""");
		string code = Generate(api: api).Files["Schema/Classes/CBaseEntity.cs"];

		Assert.Contains("[Obsolete(\"A raw write skips what CBaseEntity.SetState does; call that on the curated wrapper instead.\")] set => Set(__m_nState, value);", code);
		Assert.Contains("public int m_iHealth { get => Get<int>(__m_iHealth); set => Set(__m_iHealth, value); }", code);   // the curated setter is a plain write too
	}

	[Fact]
	public void The_spawn_survey_decides_what_gets_a_function() {
		const string survey = """
			{ "build": 7, "map": "dl_test", "results": {
			  "prop_thing": { "result": "lived", "class": "CProp" },
			  "thing_small": { "result": "crashed", "at": "server.dll+0x10" },
			  "info_base": { "result": "vanished", "model": "models/a.vmdl" }
			}, "absentConVars": ["r_only_on_clients"] }
			""";
		string spawn = Generate(units: true, survey: survey).Files["Entities/Spawn.g.cs"];

		Assert.DoesNotContain(" thing_small(", spawn);                 // takes the server down
		Assert.Contains(" prop_thing(", spawn);                        // an entity with data entries that the survey saw live under its own name
		Assert.Contains("Lived when spawned alone on dl_test in build 7.", spawn);
		Assert.Contains("it removed itself at once", spawn);
		Assert.Contains("Its model <c>models/a.vmdl</c> is not loaded on dl_test", spawn);
	}

	[Fact]
	public void The_output_directory_matches_the_dump_after_every_run() {
		string directory = Path.Combine(Path.GetTempPath(), "gamegen-" + Guid.NewGuid().ToString("N"));
		try {
			var first = new OutputSet(directory);
			first.Add("A.cs", "a\n");
			first.Add("Gone/B.cs", "b\n");
			Assert.Equal((2, 0), first.Flush());

			var second = new OutputSet(directory);
			second.Add("A.cs", "a\n");
			Assert.Equal((0, 1), second.Flush()); // unchanged file untouched, stale file removed
			Assert.False(File.Exists(Path.Combine(directory, "Gone", "B.cs")));
		}
		finally {
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void The_api_scanner_ties_an_accessor_to_the_members_that_use_it() {
		var scanner = new ApiScanner();
		scanner.ScanFile("Entities/CThing.cs", """
			namespace DeadworksManaged.Api;

			[NativeClass("CThing", "CThingAlias")]
			public unsafe class CThing : CBaseEntity {
				private static readonly SchemaAccessor<int> _level = new("CThing"u8, "m_nLevel"u8);
				/// <summary>Not a member: { braces } in a comment.</summary>
				public int Level => _level.Get(Handle);
				public void Reset() {
					_level.Set(Handle, 0);
				}
				public int Other => 1;
			}
			""");

		var wrapper = Assert.Single(scanner.Wrappers);
		Assert.Equal(("CThing", "CBaseEntity"), (wrapper.Name, wrapper.Base));
		Assert.Equal(["CThing", "CThingAlias"], wrapper.NativeNames);
		var accessor = Assert.Single(scanner.Accessors);
		Assert.Equal(("CThing", "m_nLevel"), (accessor.SchemaClass, accessor.Field));
		Assert.Equal(["Level", "Reset"], accessor.Members);
	}
}
