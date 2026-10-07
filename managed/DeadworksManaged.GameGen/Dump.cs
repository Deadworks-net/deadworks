using System.Numerics;
using System.Text.Json;

namespace DeadworksManaged.GameGen;

/// <summary>A schema type as the dump describes it: a builtin, a named atomic with up to two inner types, a class, an enum, a pointer, a fixed array or a bitfield.</summary>
sealed record TypeRef(string Category, string? Name, TypeRef? Inner, TypeRef? Inner2, int Count) {
	public static TypeRef Parse(JsonElement e) => new(
		e.GetProperty("category").GetString()!,
		e.TryGetProperty("name", out var name) ? name.GetString() : null,
		e.TryGetProperty("inner", out var inner) ? Parse(inner) : null,
		e.TryGetProperty("inner2", out var inner2) ? Parse(inner2) : null,
		e.TryGetProperty("count", out var count) ? count.GetInt32() : 0);

	/// <summary>The type as the game would spell it, for documentation.</summary>
	public string Display => Category switch {
		"ptr" => $"{Inner!.Display}*",
		"fixed_array" => $"{Inner!.Display}[{Count}]",
		"bitfield" => $"bitfield:{Count}",
		"atomic" when Inner != null && Inner2 != null => $"{Name}<{Inner.Display}, {Inner2.Display}>",
		"atomic" when Inner != null => $"{Name}<{Inner.Display}>",
		_ => Name ?? Category,
	};
}

sealed class FieldDef {
	public required string Name { get; init; }
	public required TypeRef Type { get; init; }
	public required int Offset { get; init; }
	public required Dictionary<string, string?> Metadata { get; init; }
}

sealed class ClassDef {
	public required string Name { get; init; }
	public required string Module { get; init; }
	public required List<string> Parents { get; init; }
	public required List<FieldDef> Fields { get; init; }
	public required Dictionary<string, string?> Metadata { get; init; }
}

sealed class EnumDef {
	public required string Name { get; init; }
	public required string Module { get; init; }
	public required string Alignment { get; init; }
	public required List<(string Name, BigInteger Value)> Members { get; init; }
}

/// <summary>Every class and enum the server's schema uses, by name.</summary>
sealed class SchemaModel {
	/// <summary>The server module's classes and every class they reach through a field or a parent.</summary>
	public SortedDictionary<string, ClassDef> Classes { get; } = new(StringComparer.Ordinal);
	public SortedDictionary<string, EnumDef> Enums { get; } = new(StringComparer.Ordinal);

	/// <summary>Every class in the dump, whichever module declares it.</summary>
	public Dictionary<string, ClassDef> AllClasses { get; private init; } = new(StringComparer.Ordinal);

	public const string EntityRoot = "CEntityInstance";

	public static SchemaModel Load(JsonElement root) {
		// The dump files a class shared by several modules under each of them; the server's copy is the one a server reads.
		var all = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
		var serverNames = new List<string>();
		foreach (var c in root.GetProperty("classes").EnumerateArray()) {
			var def = ParseClass(c);
			if (def.Module == "server") serverNames.Add(def.Name);
			if (!all.ContainsKey(def.Name) || def.Module == "server") all[def.Name] = def;
		}

		// A struct both modules share is sometimes only in the dump as the client declares it,
		// naming client classes (CHandle<C_BaseEntity>). On a server those are the server's classes.
		var server = serverNames.ToHashSet(StringComparer.Ordinal);
		string ServerName(string name)
			=> name.StartsWith("C_") && !server.Contains(name) && server.Contains("C" + name[2..]) ? "C" + name[2..] : name;
		TypeRef? ServerType(TypeRef? type) => type == null ? null : type with {
			Name = type.Category == "declared_class" && type.Name != null ? ServerName(type.Name) : type.Name,
			Inner = ServerType(type.Inner),
			Inner2 = ServerType(type.Inner2),
		};

		var model = new SchemaModel { AllClasses = all };
		var pending = new Stack<string>(serverNames);
		while (pending.TryPop(out var name)) {
			if (model.Classes.ContainsKey(name) || !all.TryGetValue(name, out var def)) continue;
			if (def.Module != "server") {
				def = new ClassDef {
					Name = def.Name, Module = def.Module, Metadata = def.Metadata,
					Parents = [.. def.Parents.Select(ServerName)],
					Fields = [.. def.Fields.Select(f => new FieldDef { Name = f.Name, Offset = f.Offset, Metadata = f.Metadata, Type = ServerType(f.Type)! })],
				};
			}
			model.Classes[name] = def;
			foreach (var parent in def.Parents) pending.Push(parent);
			foreach (var field in def.Fields)
				foreach (var referenced in ClassNames(field.Type)) pending.Push(referenced);
		}

		foreach (var e in root.GetProperty("enums").EnumerateArray()) {
			var def = ParseEnum(e);
			if (!model.Enums.ContainsKey(def.Name) || def.Module == "server") model.Enums[def.Name] = def;
		}
		return model;
	}

	private static IEnumerable<string> ClassNames(TypeRef? type) {
		if (type == null) yield break;
		if (type.Category == "declared_class" && type.Name != null) yield return type.Name;
		foreach (var name in ClassNames(type.Inner)) yield return name;
		foreach (var name in ClassNames(type.Inner2)) yield return name;
	}

	private static Dictionary<string, string?> ParseMetadata(JsonElement owner) {
		var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
		if (!owner.TryGetProperty("metadata", out var list)) return metadata;
		foreach (var entry in list.EnumerateArray())
			metadata[entry.GetProperty("name").GetString()!] = entry.TryGetProperty("value", out var value) ? value.ToString() : null;
		return metadata;
	}

	private static ClassDef ParseClass(JsonElement c) => new() {
		Name = c.GetProperty("name").GetString()!,
		Module = c.GetProperty("module").GetString()!,
		Parents = c.TryGetProperty("parents", out var parents)
			? parents.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList()
			: [],
		Fields = c.TryGetProperty("fields", out var fields)
			? fields.EnumerateArray().Select(f => new FieldDef {
				Name = f.GetProperty("name").GetString()!,
				Type = TypeRef.Parse(f.GetProperty("type")),
				Offset = f.GetProperty("offset").GetInt32(),
				Metadata = ParseMetadata(f),
			}).ToList()
			: [],
		Metadata = ParseMetadata(c),
	};

	private static EnumDef ParseEnum(JsonElement e) => new() {
		Name = e.GetProperty("name").GetString()!,
		Module = e.GetProperty("module").GetString()!,
		Alignment = e.TryGetProperty("alignment", out var alignment) ? alignment.GetString() ?? "uint32_t" : "uint32_t",
		// 64-bit values arrive as strings so JSON readers do not round them.
		Members = e.GetProperty("members").EnumerateArray()
			.Select(m => (m.GetProperty("name").GetString()!, BigInteger.Parse(m.GetProperty("value").ToString())))
			.ToList(),
	};

	/// <summary>The class a generated class derives from: its first parent, when that is in the model.</summary>
	public ClassDef? ParentOf(ClassDef c) => c.Parents.Count > 0 && Classes.TryGetValue(c.Parents[0], out var parent) ? parent : null;

	/// <summary>The class's base classes, nearest first.</summary>
	public IEnumerable<ClassDef> Ancestors(ClassDef c) {
		for (var parent = ParentOf(c); parent != null; parent = ParentOf(parent)) yield return parent;
	}

	public bool DerivesFrom(ClassDef c, string baseName) => c.Name == baseName || Ancestors(c).Any(a => a.Name == baseName);

	public bool IsEntity(string className) => Classes.TryGetValue(className, out var c) && DerivesFrom(c, EntityRoot);

	/// <summary>
	/// The builtin behind a strong-typedef class (<c>GameTime_t</c> is one <c>float32 m_Value</c>),
	/// or null. Such a class is exposed as its value rather than as a view with one member.
	/// </summary>
	public string? BoxedScalar(string className) {
		if (!Classes.TryGetValue(className, out var c) || c.Parents.Count > 0 || c.Fields.Count != 1) return null;
		var field = c.Fields[0];
		return field is { Name: "m_Value", Offset: 0, Type.Category: "builtin" } ? field.Type.Name : null;
	}
}
