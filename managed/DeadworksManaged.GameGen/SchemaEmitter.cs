using System.Numerics;

namespace DeadworksManaged.GameGen;

/// <summary>Counts for the run's report.</summary>
sealed class SchemaStats {
	public int Classes, EntityClasses, Enums, Fields, RawFields, InputMethods, Bridges, CuratedSetterWarnings, DataProperties;
	public SortedDictionary<string, int> RawTypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>Writes <c>Schema.*</c>: a class per schema class, an enum per schema enum, the entity registry and the bridge from curated wrappers.</summary>
sealed class SchemaEmitter(SchemaModel model, EntityModel entities, ApiScanner api, Dictionary<string, string> setterWarnings) {
	public SchemaStats Stats { get; } = new();

	/// <summary>The C# identifier of each class and enum inside <c>Schema</c>.</summary>
	public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

	private static readonly Dictionary<string, string> Builtins = new() {
		["bool"] = "bool", ["char"] = "byte", ["int8"] = "sbyte", ["uint8"] = "byte", ["int16"] = "short", ["uint16"] = "ushort",
		["int32"] = "int", ["uint32"] = "uint", ["int64"] = "long", ["uint64"] = "ulong", ["float32"] = "float", ["float64"] = "double",
	};

	// Atomics that are one plain value of a known layout.
	private static readonly Dictionary<string, string> ValueAtomics = new() {
		["Vector"] = "Vector3", ["VectorWS"] = "Vector3", ["QAngle"] = "Vector3", ["RotationVector"] = "Vector3",
		["Vector2D"] = "Vector2", ["Vector4D"] = "Vector4", ["Quaternion"] = "Quaternion", ["Color"] = "Color32",
		["CUtlStringToken"] = "uint", ["WorldGroupId_t"] = "uint",
		["CPlayerSlot"] = "int", ["CEntityIndex"] = "int", ["CSplitScreenSlot"] = "int",
		["CNetworkedQuantizedFloat"] = "float",
	};

	// All begin with a 32-bit count and keep the element pointer at +8 (tier1/utlvector.h, utlleanvector.h).
	private static readonly HashSet<string> Vectors = ["CUtlVector", "CNetworkUtlVectorBase", "C_NetworkUtlVectorBase", "CUtlVectorEmbeddedNetworkVar", "CUtlLeanVector"];
	private static readonly HashSet<string> Handles = ["CHandle", "CEntityHandle"];
	private static readonly HashSet<string> Strings = ["CUtlSymbolLarge", "CGlobalSymbol", "CUtlString"];
	// Atomics that are a CBufferString with the text inside the field (tier0/bufferstring.h).
	private static readonly HashSet<string> BufferStrings = ["CSoundEventName", "CPanoramaImageName", "CResourceNameTyped"];

	// Names a generated member must not take: the runtime's own members on SchemaObject and Schema.CEntityInstance.
	private static readonly string[] Reserved = [
		"Handle", "IsValid", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize", "At", "Get", "Set",
		"Embedded", "Pointer", "EntityPointer", "EmbeddedSubclass", "GetHandle", "SetHandle", "GetString", "SetString", "GetBufferString", "GetChars", "Raw",
		"EntityHandle", "EntityIndex", "Entity", "As", "Is", "FireInput", "Remove", "New", "NativeName", "Cast", "SubclassVData", "VData", "EntryName", "ModifierData", "PointerAtOffset", "StringAtOffset",
	];

	// Types generated code names without qualification; a schema type of the same name would hide them.
	private static readonly string[] RuntimeTypes = [
		"Vector2", "Vector3", "Vector4", "Quaternion", "Color32", "SchemaField", "SchemaObject", "RawField", "ISchemaClass",
		"SchemaValueList", "SchemaObjectList", "SchemaPointerList", "SchemaHandleList", "SchemaStringList", "Spawner", "Obsolete",
		"SchemaRegistry", "Flags", "SchemaDict", "SchemaValueDict", "SchemaMap", "SchemaValueMap",
	];

	private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);

	/// <summary>Each curated wrapper that is a view of one schema class, by that class's name.</summary>
	private readonly SortedDictionary<string, List<(ApiWrapper Wrapper, bool IsEntity)>> _bridges = new(StringComparer.Ordinal);

	private void FindBridges() {
		foreach (var wrapper in api.Wrappers.GroupBy(x => x.Name).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.Ordinal)) {
			if (!api.DerivesFrom(wrapper, "NativeEntity")) continue;
			string? schemaClass = new[] { wrapper.Name }.Concat(wrapper.NativeNames).FirstOrDefault(model.Classes.ContainsKey);
			if (schemaClass == null) continue;
			bool wrapperIsEntity = api.DerivesFrom(wrapper, "CBaseEntity");
			if (wrapperIsEntity != model.IsEntity(schemaClass)) continue;
			if (!_bridges.TryGetValue(schemaClass, out var list)) _bridges[schemaClass] = list = [];
			list.Add((wrapper, wrapperIsEntity));
		}
	}

	public void Emit(OutputSet output) {
		var taken = new HashSet<string>(RuntimeTypes, StringComparer.Ordinal);
		foreach (var name in model.Classes.Keys) Ids[name] = Naming.Unique(Naming.Identifier(name), taken);
		foreach (var name in model.Enums.Keys) if (!Ids.ContainsKey(name)) Ids[name] = Naming.Unique(Naming.Identifier(name), taken);

		FindBridges();
		var files = new HashSet<string>();
		foreach (var c in ParentsFirst()) {
			output.Add($"Schema/Classes/{OutputSet.FileName(c.Name, files)}.cs", EmitClass(c));
			Stats.Classes++;
			if (model.IsEntity(c.Name)) Stats.EntityClasses++;
		}

		files.Clear();
		foreach (var e in model.Enums.Values) {
			output.Add($"Schema/Enums/{OutputSet.FileName(e.Name, files)}.cs", EmitEnum(e));
			Stats.Enums++;
		}

		output.Add("Schema/SchemaRegistry.g.cs", EmitRegistry());
		output.Add("Schema/SchemaBridge.g.cs", EmitBridge());

		foreach (var key in setterWarnings.Keys.Where(k => !_warned.Contains(k)))
			Console.Error.WriteLine($"warning: overrides.json names {key}, which the schema does not have or is not a plain value");
	}

	private IEnumerable<ClassDef> ParentsFirst() {
		var done = new HashSet<string>(StringComparer.Ordinal);
		var order = new List<ClassDef>();
		void Visit(ClassDef c) {
			if (!done.Add(c.Name)) return;
			if (model.ParentOf(c) is { } parent) Visit(parent);
			order.Add(c);
		}
		foreach (var c in model.Classes.Values) Visit(c);
		return order;
	}

	// ---- Types -------------------------------------------------------------------

	/// <summary>The C# type that is laid out exactly as <paramref name="type"/>, or null if it is not one plain value.</summary>
	private string? ValueType(TypeRef type) => type.Category switch {
		"builtin" => Builtins.GetValueOrDefault(type.Name!),
		"declared_enum" => model.Enums.ContainsKey(type.Name!) ? Ids[type.Name!] : null,
		"declared_class" => model.BoxedScalar(type.Name!) is { } builtin ? Builtins.GetValueOrDefault(builtin) : null,
		"atomic" when type.Inner == null => ValueAtomics.GetValueOrDefault(type.Name!),
		_ => null,
	};

	private string? ClassId(TypeRef? type) => type is { Category: "declared_class" } && model.Classes.ContainsKey(type.Name!) ? Ids[type.Name!] : null;

	/// <summary>The entity class a handle refers to: its declared target when that is an entity class, otherwise any entity.</summary>
	private string HandleTarget(TypeRef handle) => handle.Inner is { Category: "declared_class" } inner && model.IsEntity(inner.Name!)
		? Ids[inner.Name!]
		: Ids[SchemaModel.EntityRoot];

	// The data class of what an entity or modifier class is created from. A class that has data
	// of its own names it after itself (CNPC_TrooperBoss, CNPC_TrooperBossVData); these two roots
	// do not follow that.
	private static readonly Dictionary<string, string> DataRoots = new() {
		["CCitadelBaseAbility"] = "CitadelAbilityVData",
		["CBaseModifier"] = "CModifierVData",
	};

	private const string DataBase = "CEntitySubclassVDataBase";

	/// <summary>The schema class of the data an instance of <paramref name="className"/> is created from, or null if it has none.</summary>
	private string? DataClassOf(string className) {
		if (!model.Classes.TryGetValue(className, out var c)) return null;
		foreach (var candidate in new[] { c }.Concat(model.Ancestors(c))) {
			string own = candidate.Name + "VData";
			if (model.Classes.TryGetValue(own, out var data) && model.DerivesFrom(data, DataBase)) return own;
			if (DataRoots.TryGetValue(candidate.Name, out var declared) && model.Classes.ContainsKey(declared)) return declared;
		}
		return null;
	}

	private bool IsHandle(TypeRef type) => type.Category == "atomic" && Handles.Contains(type.Name!);

	/// <summary>The data class behind a <c>CEmbeddedSubclass&lt;T&gt;</c>: T's, where T is a kind of modifier.</summary>
	private string? EmbeddedData(TypeRef type) => type is { Category: "atomic", Name: "CEmbeddedSubclass", Inner: { Category: "declared_class" } inner }
		&& model.Classes.TryGetValue(inner.Name!, out var instance) && model.DerivesFrom(instance, "CBaseModifier") && DataClassOf(inner.Name!) is { } data
		? Ids[data]
		: null;

	private bool IsStringKey(TypeRef? type) => type != null && IsString(type);

	/// <summary>A <c>CUtlDict</c> or <c>CUtlOrderedMap</c> whose key and value the runtime can walk, or null.</summary>
	private (string Type, string Body)? Map_(TypeRef type, string field) {
		// A CUtlDict is keyed by a string and has only its value as a template argument.
		bool dict = type.Name == "CUtlDict";
		TypeRef? key = dict ? null : type.Inner, value = dict ? type.Inner : type.Inner2;
		if (value == null || (!dict && key == null)) return null;
		bool stringKey = dict || IsStringKey(key);
		string? keyType = stringKey ? null : ValueType(key!);
		if (!stringKey && keyType == null) return null;

		string? collection = (stringKey, ValueType(value), ClassId(value)) switch {
			(true, { } plain, _) => $"SchemaValueDict<{plain}>",
			(true, null, { } structure) => $"SchemaDict<{structure}>",
			(false, { } plain, _) => $"SchemaValueMap<{keyType}, {plain}>",
			(false, null, { } structure) => $"SchemaMap<{keyType}, {structure}>",
			_ => null,
		};
		return collection == null ? null : (collection, $"=> new(this, {field});");
	}
	private bool IsString(TypeRef type) => type.Category == "atomic" && Strings.Contains(type.Name!);

	/// <summary>A member's C# type and the text after its name. Null when the field has no typed mapping.</summary>
	private (string Type, string Body)? Map(TypeRef type, string field, string? setterWarning) {
		if (ValueType(type) is { } value) {
			string set = setterWarning == null ? "set" : $"[Obsolete({Naming.Literal(setterWarning)})] set";
			return (value, $"{{ get => Get<{value}>({field}); {set} => Set({field}, value); }}");
		}
		switch (type.Category) {
			case "atomic" when IsHandle(type):
				string target = HandleTarget(type);
				return ($"{target}?", $"{{ get => GetHandle<{target}>({field}); set => SetHandle({field}, value); }}");
			case "atomic" when type.Name == "CUtlSymbolLarge":
				return ("string", $"{{ get => GetString({field}); set => SetString({field}, value); }}");
			case "atomic" when IsString(type):
				return ("string", $"=> GetString({field});");
			case "atomic" when BufferStrings.Contains(type.Name!):
				return ("string", $"=> GetBufferString({field});");
			case "atomic" when EmbeddedData(type) is { } data:
				return ($"{data}?", $"=> EmbeddedSubclass<{data}>({field});");
			case "atomic" when type.Name is "CUtlDict" or "CUtlOrderedMap":
				return Map_(type, field);
			case "atomic" when Vectors.Contains(type.Name!) && type.Inner != null:
				return List(type.Inner, field, -1);
			case "declared_class" when ClassId(type) is { } embedded:
				return (embedded, $"=> Embedded<{embedded}>({field});");
			case "ptr" when ClassId(type.Inner) is { } pointee:
				return ($"{pointee}?", model.IsEntity(type.Inner!.Name!) ? $"=> EntityPointer<{pointee}>({field});" : $"=> Pointer<{pointee}>({field});");
			case "fixed_array" when type.Inner is { Category: "builtin", Name: "char" }:
				return ("string", $"=> GetChars({field}, {type.Count});");
			case "fixed_array" when type.Inner != null:
				return List(type.Inner, field, type.Count);
			default:
				return null;
		}
	}

	private (string Type, string Body)? List(TypeRef element, string field, int fixedCount) {
		// An embedded subclass is 16 bytes with its data pointer in the second half.
		if (EmbeddedData(element) is { } data)
			return ($"SchemaPointerList<{data}>", $"=> new(this, {field}, {fixedCount}, 16, 8);");
		string? list =
			ValueType(element) is { } value ? $"SchemaValueList<{value}>"
			: IsHandle(element) ? $"SchemaHandleList<{HandleTarget(element)}>"
			: IsString(element) ? "SchemaStringList"
			: ClassId(element) is { } embedded ? $"SchemaObjectList<{embedded}>"
			: element.Category == "ptr" && ClassId(element.Inner) is { } pointee && !model.IsEntity(element.Inner!.Name!) ? $"SchemaPointerList<{pointee}>"
			: null;
		return list == null ? null : (list, $"=> new(this, {field}, {fixedCount});");
	}

	// ---- Classes -----------------------------------------------------------------

	private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

	private string EmitClass(ClassDef c) {
		string id = Ids[c.Name];
		var parent = model.ParentOf(c);
		bool isEntity = model.IsEntity(c.Name);

		var inherited = new HashSet<string>(StringComparer.Ordinal);
		foreach (var ancestor in model.Ancestors(c)) inherited.UnionWith(_members[ancestor.Name]);
		var own = new HashSet<string>(Reserved, StringComparer.Ordinal) { id };
		var declared = new HashSet<string>(StringComparer.Ordinal);

		var w = new CodeWriter("System.Numerics");
		w.Open("public static partial class Schema");

		string description = Naming.Unquote(c.Metadata.GetValueOrDefault("MPropertyDescription") ?? c.Metadata.GetValueOrDefault("MPropertyFriendlyName"));
		string summary = $"Schema class <c>{Naming.Doc(c.Name)}</c>.";
		if (description.Length > 0) summary += " " + Naming.Doc(description);
		summary += $" <see href=\"https://deadworks.net/db/schema/{c.Module}/{Uri.EscapeDataString(c.Name)}\">Modding database</see>.";
		w.Summary(summary);
		if (c.Parents.Count > 1)
			w.Line($"/// <remarks>The game also derives this class from {string.Join(", ", c.Parents.Skip(1).Select(p => $"<c>{Naming.Doc(p)}</c>"))}, whose fields are not on this view.</remarks>");

		w.Open($"public partial class {id} : {(parent != null ? Ids[parent.Name] : "SchemaObject")}, ISchemaClass<{id}>");
		w.Line($"internal {id}() {{ }}");
		w.Line($"static {id} ISchemaClass<{id}>.New() => new();");
		w.Line($"static string ISchemaClass<{id}>.NativeName => {Naming.Literal(c.Name)};");

		foreach (var field in c.Fields.OrderBy(f => f.Name, StringComparer.Ordinal)) {
			string property = Naming.Unique(Naming.Identifier(field.Name), own);
			declared.Add(property);
			string descriptor = "__" + property.TrimStart('@');
			string key = $"{c.Name}.{field.Name}";
			if (!setterWarnings.TryGetValue(key, out var warning)
				&& api.Accessors.FirstOrDefault(x => x.SchemaClass == c.Name && x.Field == field.Name && x.SetMethod != null) is { } curated) {
				warning = $"A raw write skips what {curated.Wrapper}.{curated.SetMethod} does; call that on the curated wrapper instead.";
				Stats.CuratedSetterWarnings++;
			}

			var mapped = Map(field.Type, descriptor, warning);
			if (warning != null && mapped is { } m && m.Body.Contains("[Obsolete(")) _warned.Add(key);
			Stats.Fields++;
			if (mapped == null) {
				Stats.RawFields++;
				string rawType = field.Type.Category == "atomic" ? field.Type.Name! : field.Type.Category;
				Stats.RawTypes[rawType] = Stats.RawTypes.GetValueOrDefault(rawType) + 1;
			}

			w.Line();
			w.Line($"private static readonly SchemaField {descriptor} = new({Naming.Literal(c.Name)}, {Naming.Literal(field.Name)});");
			w.Summary(FieldSummary(c, field, mapped == null));
			string modifier = inherited.Contains(property) ? "public new" : "public";
			if (mapped is { } typed) w.Line($"{modifier} {typed.Type} {property} {typed.Body}");
			else w.Line($"{modifier} RawField {property} => Raw({descriptor}, {Naming.Literal(field.Type.Display)});");
		}

		// The way back to the curated wrapper, typed, where one wraps exactly this class.
		if (isEntity && c.Name != "CBaseEntity" && _bridges.TryGetValue(c.Name, out var bridged) && bridged.Any(b => b.Wrapper.Name == c.Name)) {
			string type = $"global::DeadworksManaged.Api.{c.Name}";
			w.Line();
			w.Summary($"The curated <see cref=\"{type}\"/> for this entity, or null if it is gone.");
			w.Line($"public new {type}? Entity => SchemaRegistry.Wrapper<{type}>(EntityHandle);");
		}

		// The data this is created from, typed, wherever the type is more specific than the base class's.
		if (DataClassOf(c.Name) is { } dataClass && (parent == null || DataClassOf(parent.Name) != dataClass) && (isEntity || c.Name == "CBaseModifier" || model.DerivesFrom(c, "CBaseModifier"))) {
			string dataType = Ids[dataClass];
			string hides = parent != null && DataClassOf(parent.Name) != null ? "new " : "";
			w.Line();
			w.Summary($"The data entry this was created from, as <see cref=\"{dataType}\"/>, or null if it has none.");
			w.Line(isEntity
				? $"public {hides}{dataType}? VData => SubclassVData<{dataType}>();"
				: $"public {hides}{dataType}? VData => ModifierData<{dataType}>();");
			Stats.DataProperties++;
		}

		if (isEntity) EmitInputs(w, c, own, inherited, declared);

		w.Close().Close();
		_members[c.Name] = declared;
		return w.ToString();
	}

	private string FieldSummary(ClassDef c, FieldDef field, bool raw) {
		string summary = $"<c>{Naming.Doc(field.Type.Display)}</c>.";
		string description = Naming.Unquote(field.Metadata.GetValueOrDefault("MPropertyDescription") ?? field.Metadata.GetValueOrDefault("MPropertyFriendlyName"));
		if (description.Length > 0) summary += " " + Naming.Doc(description.TrimEnd('.')) + ".";
		var curated = api.Accessors
			.Where(a => a.SchemaClass == c.Name && a.Field == field.Name)
			.SelectMany(a => a.Members.Select(member => $"<c>{a.Wrapper}.{member}</c>"))
			.Distinct().ToList();
		if (curated.Count > 0) summary += $" Wrapped by {string.Join(", ", curated)} in DeadworksManaged.Api.";
		if (raw) summary += " No typed mapping yet: read it through its address.";
		return summary;
	}

	private void EmitInputs(CodeWriter w, ClassDef c, HashSet<string> own, HashSet<string> inherited, HashSet<string> declared) {
		foreach (var input in entities.InputsOf(c, model).OrderBy(i => i.Name, StringComparer.Ordinal)) {
			if (input.Returns || input.Params.Count > 1) continue;
			string method = "Input" + Naming.Identifier(input.Name).TrimStart('@');
			if (inherited.Contains(method) || !own.Add(method)) continue;

			string? parameter = null, argument = null;
			if (input.Params.Count == 1) {
				(string kind, string? of) = EntityModel.SplitPulseType(input.Params[0].Type);
				(parameter, argument) = kind switch {
					"FLOAT" => ("float value", "Spawner.InputText(value)"),
					"INT" => ("int value", "Spawner.InputText(value)"),
					"BOOL" => ("bool value", "Spawner.InputText(value)"),
					"STRING" or "ENTITY_NAME" => ("string value", "value"),
					"VEC3" or "VEC3_WORLDSPACE" or "QANGLE" => ("Vector3 value", "Spawner.InputText(value)"),
					"COLOR_RGB" => ("Color32 value", "Spawner.InputText(value)"),
					"ENUM" or "SCHEMA_ENUM" when of != null && model.Enums.ContainsKey(of) => ($"{Ids[of]} value", "Spawner.InputText(Convert.ToInt32(value))"),
					_ => ((string?)null, (string?)null),
				};
				if (parameter == null) continue;
			}

			declared.Add(method);
			Stats.InputMethods++;
			string summary = $"Fires the <c>{Naming.Doc(input.Name)}</c> input.";
			if (input.Description.Length > 0) summary += " " + Naming.Doc(input.Description.TrimEnd('.')) + ".";
			w.Line();
			w.Summary(summary);
			w.Line(parameter == null
				? $"public void {method}() => FireInput({Naming.Literal(input.Name)});"
				: $"public void {method}({parameter}) => FireInput({Naming.Literal(input.Name)}, {argument});");
		}
	}

	// ---- Enums -------------------------------------------------------------------

	private string EmitEnum(EnumDef e) {
		bool signed = e.Members.Any(m => m.Value < 0);
		(string type, BigInteger min, BigInteger max) = (e.Alignment, signed) switch {
			("uint8_t", false) => ("byte", (BigInteger)byte.MinValue, (BigInteger)byte.MaxValue),
			("uint8_t", true) => ("sbyte", sbyte.MinValue, sbyte.MaxValue),
			("uint16_t", false) => ("ushort", ushort.MinValue, ushort.MaxValue),
			("uint16_t", true) => ("short", short.MinValue, short.MaxValue),
			("uint64_t", false) => ("ulong", ulong.MinValue, ulong.MaxValue),
			("uint64_t", true) => ("long", long.MinValue, long.MaxValue),
			(_, true) => ("int", int.MinValue, int.MaxValue),
			_ => ("uint", uint.MinValue, uint.MaxValue),
		};

		string id = Ids[e.Name];
		var w = new CodeWriter();
		w.Open("public static partial class Schema");
		w.Summary($"Schema enum <c>{Naming.Doc(e.Name)}</c>. <see href=\"https://deadworks.net/db/schema/{e.Module}/{Uri.EscapeDataString(e.Name)}\">Modding database</see>.");
		// Bit flags print as their names combined. A sequential enum that reaches 4 has a 3, so it is not mistaken for one.
		var bits = e.Members.Select(m => m.Value).Where(v => v != 0).Distinct().ToList();
		if (bits.Count >= 3 && bits.All(v => v > 0 && v.IsPowerOfTwo) && bits.Any(v => v >= 4)) w.Line("[Flags]");
		w.Open($"public enum {id} : {type}");
		var taken = new HashSet<string>(StringComparer.Ordinal) { id };
		foreach (var (name, value) in e.Members) {
			// A value outside the storage type keeps its bits, as it does in the game.
			string literal = value >= min && value <= max ? value.ToString()
				: $"unchecked(({type}){(value < 0 ? value + "L" : value + "UL")})";
			w.Line($"{Naming.Unique(Naming.Identifier(name), taken)} = {literal},");
		}
		w.Close().Close();
		return w.ToString();
	}

	// ---- Registry and bridge -----------------------------------------------------

	private string EmitRegistry() {
		string root = Ids[SchemaModel.EntityRoot];
		var w = new CodeWriter();
		w.Open("public static partial class SchemaRegistry");
		w.Open($"static partial void AddEntityClasses(Dictionary<string, Func<Schema.{root}>> classes)");
		foreach (var c in model.Classes.Values.Where(c => model.IsEntity(c.Name)))
			w.Line($"classes[{Naming.Literal(c.Name)}] = static () => new Schema.{Ids[c.Name]}();");
		w.Close();
		w.Line();
		w.Open($"static partial void AddDesignerNames(Dictionary<string, Func<Schema.{root}>> designerNames)");
		foreach (var entity in entities.Entities.Values)
			if (entities.SchemaClassOf(entity, model) is { } c && model.IsEntity(c.Name))
				w.Line($"designerNames[{Naming.Literal(entity.Name)}] = static () => new Schema.{Ids[c.Name]}();");
		w.Close().Close();
		return w.ToString();
	}

	private string EmitBridge() {
		// In the API's namespace, so .Schema is there for anyone who references this assembly.
		var w = new CodeWriter("DeadworksManaged.Api", ["DeadworksManaged.Game"]);
		w.Summary("Adds <c>.Schema</c> to the curated wrappers: every schema field of the object, typed as its generated class.");
		w.Open("public static class SchemaBridge");
		foreach (var (schemaClass, wrappers) in _bridges) {
			foreach (var (wrapper, wrapperIsEntity) in wrappers) {
				string type = $"Schema.{Ids[schemaClass]}";
				w.Open($"extension({wrapper.Name} self)");
				w.Summary($"This object as <see cref=\"{type}\"/>: every schema field the game declares for it, under the game's own names. "
					+ "Each use looks the object up again, so keep the view in a local when you read several fields.");
				w.Line(wrapperIsEntity
					? $"public {type} Schema => SchemaRegistry.Bridge<{type}>(self.EntityHandle);"
					: $"public {type} Schema => SchemaObject.At<{type}>(self.Handle);");
				w.Close();
				Stats.Bridges++;
			}
		}
		w.Close();
		return w.ToString();
	}
}
