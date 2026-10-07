using System.Text.Json;

namespace DeadworksManaged.GameGen;

/// <summary>An entity the game can create by name: <c>prop_dynamic</c> is <c>CDynamicProp</c>.</summary>
sealed record EntityClass(string Name, string DataMap, string CppClass);

sealed record DataMapField(string Name, string Type, int Size, HashSet<string> Flags, string? ExternalName, string? Embedded, string? Enum);

sealed record DataMap(string Name, string? Base, List<DataMapField> Fields);

sealed record IoParam(string Name, string Type);

/// <summary>An entity input or output: since build 6711 a Pulse binding of a C++ class.</summary>
sealed record IoBinding(string Name, List<IoParam> Params, bool Returns, string Description);

sealed record IoClass(string Name, List<IoBinding> Inputs, List<IoBinding> Outputs);

/// <summary>
/// A data entry an entity is created from: <c>npc_boss_tier1</c> in <c>scripts/npc_units.vdata</c>
/// is an <c>npc_trooper_boss</c> with a Guardian's health, model and weapons.
/// </summary>
sealed record SubclassEntry(string Name, string EntityName, string File);

/// <summary>A spawn key value an entity reads, and the datamap field it lands in.</summary>
sealed record KeyDef(string Name, string Type, int Size, string? Enum, string DataMap, string Field, bool Procedural);

/// <summary>The dump's entity classes, their datamaps (spawn key values) and their inputs and outputs.</summary>
sealed class EntityModel {
	public SortedDictionary<string, EntityClass> Entities { get; } = new(StringComparer.Ordinal);
	public SortedDictionary<string, DataMap> DataMaps { get; } = new(StringComparer.Ordinal);
	public SortedDictionary<string, IoClass> Io { get; } = new(StringComparer.Ordinal);

	/// <summary>Every data entry that names the entity it makes, by entry name.</summary>
	public SortedDictionary<string, SubclassEntry> Subclasses { get; } = new(StringComparer.Ordinal);

	/// <summary>
	/// Adds the entries of one VData file of entity subclasses, as the modding database serves
	/// it: each entry's <c>_class</c> is the entity it is created as. An entry without one is a
	/// base for other entries, not something to spawn.
	/// </summary>
	public void AddSubclasses(JsonElement file) {
		string path = Text(file, "path") ?? "";
		if (!file.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object) return;
		foreach (var entry in entries.EnumerateObject())
			if (entry.Value.ValueKind == JsonValueKind.Object && Text(entry.Value, "_class") is { Length: > 0 } entityName)
				Subclasses.TryAdd(entry.Name, new(entry.Name, entityName, path));
	}

	public static EntityModel Load(JsonElement root) {
		var model = new EntityModel();
		foreach (var c in root.GetProperty("classes").EnumerateArray()) {
			string name = c.GetProperty("name").GetString()!;
			model.Entities[name] = new(name, Text(c, "datamap") ?? "", Text(c, "cpp_class") ?? "");
		}
		foreach (var d in root.GetProperty("datamaps").EnumerateArray()) {
			string name = d.GetProperty("name").GetString()!;
			var fields = d.TryGetProperty("fields", out var list)
				? list.EnumerateArray().Select(f => new DataMapField(
					f.GetProperty("name").GetString()!,
					Text(f, "type") ?? "",
					f.TryGetProperty("size", out var size) ? size.GetInt32() : 1,
					f.TryGetProperty("flags", out var flags) ? flags.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [],
					Text(f, "external_name"), Text(f, "embedded"), Text(f, "enum"))).ToList()
				: [];
			model.DataMaps[name] = new(name, Text(d, "base"), fields);
		}
		if (root.TryGetProperty("io", out var io)) {
			foreach (var c in io.EnumerateArray()) {
				string name = c.GetProperty("name").GetString()!;
				model.Io[name] = new(name, Bindings(c, "inputs"), Bindings(c, "outputs"));
			}
		}
		return model;
	}

	private static string? Text(JsonElement e, string property)
		=> e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static List<IoBinding> Bindings(JsonElement owner, string property) {
		if (!owner.TryGetProperty(property, out var list)) return [];
		return list.EnumerateArray().Select(b => new IoBinding(
			b.GetProperty("name").GetString()!,
			b.TryGetProperty("params", out var ps) ? ps.EnumerateArray().Select(p => new IoParam(Text(p, "name") ?? "param", Text(p, "type") ?? "")).ToList() : [],
			b.TryGetProperty("returns", out var returns) && returns.ValueKind == JsonValueKind.Array && returns.GetArrayLength() > 0,
			Text(b, "description") ?? "")).ToList();
	}

	/// <summary><c>PVAL_FLOAT</c> is (<c>FLOAT</c>, null); <c>PVAL_ENUM:NPC_STATE</c> is (<c>ENUM</c>, <c>NPC_STATE</c>).</summary>
	public static (string Kind, string? Of) SplitPulseType(string type) {
		string bare = type.StartsWith("PVAL_") ? type[5..] : type;
		int colon = bare.IndexOf(':');
		return colon < 0 ? (bare, null) : (bare[..colon], bare[(colon + 1)..]);
	}

	/// <summary><c>PVAL_FLOAT</c> as <c>float</c>, <c>PVAL_EHANDLE:func_button</c> as <c>ehandle&lt;func_button&gt;</c>.</summary>
	public static string PulseTypeLabel(string type) {
		(string kind, string? of) = SplitPulseType(type);
		if (of == null) return kind.ToLowerInvariant();
		if (kind is "SCHEMA_ENUM" or "TYPESAFE_INT64") return of;
		return $"{kind.ToLowerInvariant()}<{(of.StartsWith("PVAL_") ? PulseTypeLabel(of) : of)}>";
	}

	public static string ParamsLabel(IoBinding binding)
		=> string.Join(", ", binding.Params.Select(p => p.Name == "param" ? PulseTypeLabel(p.Type) : $"{PulseTypeLabel(p.Type)} {p.Name}"));

	/// <summary>The entity's schema class: its C++ class, or the nearest datamap base the schema knows.</summary>
	public ClassDef? SchemaClassOf(EntityClass entity, SchemaModel schema) {
		if (schema.Classes.TryGetValue(entity.CppClass, out var c)) return c;
		return SchemaClassOfDataMap(entity.DataMap, schema);
	}

	private ClassDef? SchemaClassOfDataMap(string? dataMap, SchemaModel schema) {
		for (var map = dataMap != null ? DataMaps.GetValueOrDefault(dataMap) : null; map != null; map = map.Base != null ? DataMaps.GetValueOrDefault(map.Base) : null)
			if (schema.Classes.TryGetValue(map.Name, out var c)) return c;
		return null;
	}

	private static string? HeldClass(TypeRef? type) => type?.Category switch {
		"ptr" => HeldClass(type.Inner),
		"declared_class" => type.Name,
		_ => null,
	};

	/// <summary>The classes held by a class's own or inherited fields that derive from <paramref name="baseName"/>.</summary>
	private static IEnumerable<ClassDef> FieldsHolding(ClassDef c, string baseName, SchemaModel schema) {
		foreach (var owner in new[] { c }.Concat(schema.Ancestors(c)))
			foreach (var field in owner.Fields)
				if (HeldClass(field.Type) is { } held && schema.Classes.TryGetValue(held, out var heldClass) && schema.DerivesFrom(heldClass, baseName))
					yield return heldClass;
	}

	/// <summary>
	/// Components hang off an entity at run time rather than through its datamap: the schema says
	/// which (<c>CBaseEntity::m_CBodyComponent</c>), and a component's scene node carries keys such as <c>origin</c>.
	/// </summary>
	private static IEnumerable<string> ComponentDataMaps(ClassDef c, SchemaModel schema) {
		foreach (var component in FieldsHolding(c, "CEntityComponent", schema)) {
			yield return component.Name;
			foreach (var node in FieldsHolding(component, "CGameSceneNode", schema)) yield return node.Name;
		}
	}

	private readonly Dictionary<string, List<KeyDef>> _keys = new(StringComparer.Ordinal);

	/// <summary>
	/// Every key value an entity of this datamap reads: its own, those of the datamaps it derives
	/// from or embeds, and its components'. The first declaration of a name wins. Follows the
	/// modding database's <c>entityDetails</c>.
	/// </summary>
	public List<KeyDef> KeysOf(string dataMap, SchemaModel schema) {
		if (_keys.TryGetValue(dataMap, out var cached)) return cached;
		var keys = new List<KeyDef>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var visited = new HashSet<string>(StringComparer.Ordinal);

		void Visit(string? name) {
			for (var map = name != null ? DataMaps.GetValueOrDefault(name) : null; map != null && visited.Add(map.Name); map = map.Base != null ? DataMaps.GetValueOrDefault(map.Base) : null) {
				foreach (var field in map.Fields) {
					// Inputs and outputs carry the key flag too, but their names are I/O names.
					if (field.Flags.Contains("input") || field.Flags.Contains("output")) { }
					else if (field.Flags.Contains("procedural_keyfield")) {
						if (seen.Add(field.Name)) keys.Add(new(field.Name, field.Type, field.Size, field.Enum, map.Name, field.Name, true));
					}
					else if (field.Flags.Contains("key") && field.ExternalName is { Length: > 0 } external) {
						// A removed key still claims its name, so a base's key of that name does not come back.
						if (seen.Add(external) && !field.Flags.Contains("removed_keyfield"))
							keys.Add(new(external, field.Type, field.Size, field.Enum, map.Name, field.Name, false));
					}
					if (field.Embedded != null) Visit(field.Embedded);
				}
			}
		}

		Visit(dataMap);
		if (SchemaClassOfDataMap(dataMap, schema) is { } schemaClass)
			foreach (var component in ComponentDataMaps(schemaClass, schema)) Visit(component);

		return _keys[dataMap] = keys;
	}

	/// <summary>
	/// The inputs an entity class adds: its own Pulse bindings and those of the structs and
	/// components its own fields hold. Inherited ones come through the generated base class.
	/// </summary>
	public IEnumerable<IoBinding> InputsOf(ClassDef c, SchemaModel schema) {
		var names = new List<string> { c.Name };
		foreach (var field in c.Fields)
			if (HeldClass(field.Type) is { } held && schema.Classes.TryGetValue(held, out var heldClass) && !schema.IsEntity(held))
				names.AddRange(new[] { heldClass }.Concat(schema.Ancestors(heldClass)).Select(x => x.Name));
		return names.Distinct().Where(Io.ContainsKey).SelectMany(name => Io[name].Inputs).DistinctBy(i => i.Name);
	}
}
