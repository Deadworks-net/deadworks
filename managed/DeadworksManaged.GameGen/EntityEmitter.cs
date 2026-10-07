namespace DeadworksManaged.GameGen;

sealed class EntityStats {
	public int Spawnable, NotSpawnable, KeyClasses, Keys, Inputs, Outputs;
}

/// <summary>Writes the typed spawn functions, their key value classes, and the name constants for entities, inputs and outputs.</summary>
sealed class EntityEmitter(EntityModel entities, SchemaModel schema, Dictionary<string, string> schemaIds) {
	public EntityStats Stats { get; } = new();

	// Entities only the engine or a hero may create: making one alone takes the server down.
	private static readonly string[] NotSpawnable = ["CCitadelBaseAbility", "CBasePlayerController", "CBasePlayerPawn"];

	private static readonly string[] KeysReserved = [
		"With", "Build", "SetKey", "SetToken", "GetValue", "GetText", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize",
	];

	private static readonly HashSet<string> IntTypes = [
		"FIELD_INT16", "FIELD_INT32", "FIELD_INT64", "FIELD_UINT8", "FIELD_UINT16", "FIELD_UINT32", "FIELD_UINT64",
		"FIELD_TICK", "FIELD_ENGINE_TICK", "FIELD_POSITIVEINTEGER_OR_NULL",
	];
	private static readonly HashSet<string> FloatTypes = ["FIELD_FLOAT32", "FIELD_FLOAT64", "FIELD_TIME", "FIELD_ENGINE_TIME", "FIELD_SCALE32"];
	private static readonly HashSet<string> VectorTypes = [
		"FIELD_VECTOR", "FIELD_POSITION_VECTOR", "FIELD_QANGLE", "FIELD_QANGLE_WORLDSPACE", "FIELD_DIRECTION_VECTOR_WORLDSPACE",
		"FIELD_ROTATION_VECTOR", "FIELD_ROTATION_VECTOR_WORLDSPACE", "FIELD_NETWORK_QUANTIZED_VECTOR",
		"FIELD_NETWORK_ORIGIN_CELL_QUANTIZED_VECTOR", "FIELD_NETWORK_ORIGIN_CELL_QUANTIZED_POSITION_VECTOR",
	];
	private static readonly HashSet<string> TokenTypes = ["FIELD_UTLSTRINGTOKEN", "FIELD_STRING_AND_TOKEN"];

	private readonly Dictionary<string, string> _keyIds = new(StringComparer.Ordinal);

	public void Emit(OutputSet output) {
		var spawnable = entities.Entities.Values
			.Select(e => (Entity: e, Class: entities.SchemaClassOf(e, schema)))
			.Where(x => x.Class != null && schema.IsEntity(x.Class.Name) && !NotSpawnable.Any(n => schema.DerivesFrom(x.Class, n)))
			.ToList();
		Stats.Spawnable = spawnable.Count;
		Stats.NotSpawnable = entities.Entities.Count - spawnable.Count;

		// A key class per datamap a spawnable entity uses, and per datamap those derive from.
		var needed = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var (entity, _) in spawnable)
			for (var map = entities.DataMaps.GetValueOrDefault(entity.DataMap); map != null && needed.Add(map.Name); map = map.Base != null ? entities.DataMaps.GetValueOrDefault(map.Base) : null) { }
		var taken = new HashSet<string>(StringComparer.Ordinal);
		foreach (var name in needed) _keyIds[name] = Naming.Unique(Naming.Identifier(name), taken);

		var files = new HashSet<string>();
		foreach (var name in needed) {
			output.Add($"Entities/Keys/{OutputSet.FileName(name, files)}.cs", EmitKeys(entities.DataMaps[name]));
			Stats.KeyClasses++;
		}

		output.Add("Entities/Spawn.g.cs", EmitSpawn(spawnable!));
		output.Add("Entities/EntityNames.g.cs", EmitEntityNames());
		output.Add("Entities/Inputs.g.cs", EmitIo("Inputs", "Input", c => c.Inputs, n => Stats.Inputs += n));
		output.Add("Entities/Outputs.g.cs", EmitIo("Outputs", "Output", c => c.Outputs, n => Stats.Outputs += n));
	}

	private (string Type, string Get, string Set) KeyAccess(KeyDef key) {
		string name = Naming.Literal(key.Name);
		string type =
			key.Type == "FIELD_BOOLEAN" ? "bool"
			: key.Type == "FIELD_CHARACTER" ? (key.Size > 1 ? "string" : "int")
			: IntTypes.Contains(key.Type) ? (key.Enum != null && schema.Enums.ContainsKey(key.Enum) ? $"Schema.{schemaIds[key.Enum]}" : "int")
			: FloatTypes.Contains(key.Type) ? "float"
			: VectorTypes.Contains(key.Type) ? "Vector3"
			: key.Type == "FIELD_COLOR32" ? "Color32"
			: "string";
		if (type == "string")
			return ("string?", $"GetText({name})", TokenTypes.Contains(key.Type) ? $"SetToken({name}, value)" : $"SetKey({name}, value)");
		return ($"{type}?", $"GetValue<{type}>({name})", $"SetKey({name}, value)");
	}

	private string EmitKeys(DataMap map) {
		string id = _keyIds[map.Name];
		string? baseId = map.Base != null ? _keyIds.GetValueOrDefault(map.Base) : null;
		var inherited = map.Base != null && baseId != null
			? entities.KeysOf(map.Base, schema).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		inherited.Add("targetname"); // EntityKeys declares it: the entity system reads it, not a datamap

		var w = new CodeWriter("System.Numerics");
		w.Open("public static partial class Keys");
		w.Summary($"Spawn key values of entities with the <c>{Naming.Doc(map.Name)}</c> datamap.");
		w.Open($"public class {id} : {baseId ?? "EntityKeys"}");
		var taken = new HashSet<string>(KeysReserved, StringComparer.Ordinal) { id };
		foreach (var key in entities.KeysOf(map.Name, schema).Where(k => !inherited.Contains(k.Name)).OrderBy(k => k.Name, StringComparer.Ordinal)) {
			(string type, string get, string set) = KeyAccess(key);
			string label = key.Type.StartsWith("FIELD_") ? key.Type[6..].ToLowerInvariant() : key.Type.ToLowerInvariant();
			string summary = $"<c>{Naming.Doc(label)}</c> key <c>{Naming.Doc(key.Name)}</c>, read into <c>{Naming.Doc(key.DataMap)}.{Naming.Doc(key.Field)}</c>.";
			if (key.Procedural) summary += " The game computes what this key does while it spawns the entity.";
			w.Summary(summary);
			w.Line($"public {type} {Naming.Unique(Naming.Identifier(key.Name), taken)} {{ get => {get}; set => {set}; }}");
			Stats.Keys++;
		}
		w.Close().Close();
		return w.ToString();
	}

	private string EmitSpawn(List<(EntityClass Entity, ClassDef Class)> spawnable) {
		var w = new CodeWriter();
		w.Summary("Creates and spawns any entity the game can create by name, with the key values a map would give it. "
			+ "A function exists for every entity class in the game; that does not promise the entity works alone on a dedicated server.");
		w.Open("public static partial class Spawn");
		var taken = new HashSet<string>(["Equals", "ReferenceEquals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Spawn"], StringComparer.Ordinal);
		foreach (var (entity, schemaClass) in spawnable) {
			string type = $"Schema.{schemaIds[schemaClass.Name]}";
			string keys = _keyIds.TryGetValue(entity.DataMap, out var keyId) ? $"Keys.{keyId}" : "EntityKeys";
			w.Summary($"Creates and spawns a <c>{Naming.Doc(entity.Name)}</c> as a <see cref=\"{type}\"/>, or returns null if the game refuses. "
				+ $"<see href=\"https://deadworks.net/db/entities/{Uri.EscapeDataString(entity.Name)}\">Modding database</see>.");
			w.Line($"public static {type}? {Naming.Unique(Naming.Identifier(entity.Name), taken)}({keys}? keys = null) => Spawner.Create<{type}>({Naming.Literal(entity.Name)}, keys);");
		}
		w.Close();
		return w.ToString();
	}

	private string EmitEntityNames() {
		var w = new CodeWriter();
		w.Summary("The name of every entity the game can create, as a map names it (its designer name).");
		w.Open("public static class EntityNames");
		var taken = new HashSet<string>(["Equals", "ReferenceEquals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "EntityNames"], StringComparer.Ordinal);
		foreach (var entity in entities.Entities.Values) {
			w.Summary($"<c>{Naming.Doc(entity.CppClass.Length > 0 ? entity.CppClass : entity.DataMap)}</c>.");
			w.Line($"public const string {Naming.Unique(Naming.Identifier(entity.Name), taken)} = {Naming.Literal(entity.Name)};");
		}
		w.Close();
		return w.ToString();
	}

	private string EmitIo(string container, string noun, Func<IoClass, List<IoBinding>> select, Action<int> count) {
		var emitted = entities.Io.Values.Where(c => select(c).Count > 0).ToDictionary(c => c.Name, StringComparer.Ordinal);
		var ids = new Dictionary<string, string>(StringComparer.Ordinal);
		var taken = new HashSet<string>(StringComparer.Ordinal) { container };
		foreach (var name in emitted.Keys.Order(StringComparer.Ordinal)) ids[name] = Naming.Unique(Naming.Identifier(name), taken);

		// The nearest base class that has bindings of its own, so a class's constants include what it inherits.
		string? BaseOf(string name) => schema.Classes.TryGetValue(name, out var c)
			? schema.Ancestors(c).Select(a => a.Name).FirstOrDefault(emitted.ContainsKey)
			: null;

		// Bases first, so each class knows every constant it inherits before it declares its own.
		var order = new List<string>();
		var placed = new HashSet<string>(StringComparer.Ordinal);
		void Place(string name) {
			if (!placed.Add(name)) return;
			if (BaseOf(name) is { } parent) Place(parent);
			order.Add(name);
		}
		foreach (var name in emitted.Keys.Order(StringComparer.Ordinal)) Place(name);
		var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

		var w = new CodeWriter();
		w.Summary($"The name of every entity {noun.ToLowerInvariant()}, by the class that declares it, for <c>Entity{noun}Hook</c> and <c>FireInput</c>. "
			+ "A class's constants include those of its base classes.");
		w.Open($"public static class {container}");
		foreach (var name in order) {
			string id = ids[name];
			string? parent = BaseOf(name);
			var inherited = names[name] = parent != null ? new(names[parent], StringComparer.Ordinal) : new(StringComparer.Ordinal);
			var own = new HashSet<string>(StringComparer.Ordinal) { id };
			w.Summary($"{container} of <c>{Naming.Doc(name)}</c>.");
			w.Open($"public abstract class {id}{(parent != null ? $" : {ids[parent]}" : "")}");
			w.Line($"private protected {id}() {{ }}");
			int added = 0;
			foreach (var binding in select(emitted[name]).DistinctBy(b => b.Name).OrderBy(b => b.Name, StringComparer.Ordinal)) {
				string constant = Naming.Identifier(binding.Name);
				if (inherited.Contains(constant) || !own.Add(constant)) continue;
				inherited.Add(constant);
				string parameters = EntityModel.ParamsLabel(binding);
				string summary = parameters.Length > 0 ? $"Takes <c>{Naming.Doc(parameters)}</c>." : "";
				if (binding.Description.Length > 0) summary = (summary + " " + Naming.Doc(binding.Description.TrimEnd('.')) + ".").Trim();
				w.Summary(summary);
				w.Line($"public const string {constant} = {Naming.Literal(binding.Name)};");
				added++;
			}
			count(added);
			w.Close();
		}
		w.Close();
		return w.ToString();
	}
}
