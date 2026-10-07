using System.Text.Json;

namespace DeadworksManaged.GameGen;

sealed class CatalogStats {
	public int ConVars, Commands, Heroes, Abilities, Items, Modifiers;
}

/// <summary>Writes the typed console variables and commands, and the name constants for heroes, abilities, items and modifiers.</summary>
sealed class CatalogEmitter {
	public CatalogStats Stats { get; } = new();

	private static readonly string[] ObjectMembers = ["Equals", "ReferenceEquals", "GetHashCode", "ToString", "GetType", "MemberwiseClone"];

	private static readonly Dictionary<string, string> ConVarTypes = new() {
		["bool"] = "bool", ["int16"] = "int", ["int32"] = "int", ["uint16"] = "ushort", ["uint32"] = "uint", ["int64"] = "long", ["uint64"] = "ulong",
		["float32"] = "float", ["float64"] = "double", ["string"] = "string",
		["vector2"] = "Vector2", ["vector3"] = "Vector3", ["vector4"] = "Vector4", ["color"] = "Color32",
	};

	private static string[] Flags(JsonElement e)
		=> e.TryGetProperty("flags", out var flags) ? [.. flags.EnumerateArray().Select(f => f.GetString()!)] : [];

	// A dedicated server has no client DLL, so a variable only it registers does not exist there.
	private static bool OnServer(string[] flags) => !flags.Contains("clientdll") || flags.Contains("gamedll");

	private static string Scalar(JsonElement e, string property)
		=> e.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";

	public void EmitConsole(OutputSet output, JsonElement root) {
		var w = new CodeWriter("System.Numerics");
		w.Summary("Every console variable a dedicated server has, with the type the game declares for it. "
			+ "<c>ConVars.sv_cheats.Value = true</c> sets it directly, development-only and cheat-protected variables included.");
		w.Open("public static class ConVars");
		var taken = new HashSet<string>(ObjectMembers, StringComparer.Ordinal) { "ConVars" };
		foreach (var conVar in root.GetProperty("convars").EnumerateArray().OrderBy(c => c.GetProperty("name").GetString(), StringComparer.Ordinal)) {
			string name = conVar.GetProperty("name").GetString()!;
			string[] flags = Flags(conVar);
			if (!OnServer(flags) || !Naming.IsIdentifier(name)) continue;
			string type = ConVarTypes.GetValueOrDefault(Scalar(conVar, "type"), "string");

			string summary = Naming.Doc(Scalar(conVar, "description").TrimEnd('.'));
			if (summary.Length > 0) summary += ". ";
			string defaultValue = Scalar(conVar, "default");
			summary += $"Default <c>{(defaultValue.Length > 0 ? Naming.Doc(defaultValue) : "\"\"")}</c>";
			string min = Scalar(conVar, "min"), max = Scalar(conVar, "max");
			if (min.Length > 0 || max.Length > 0) summary += $", range {(min.Length > 0 ? min : "…")} to {(max.Length > 0 ? max : "…")}";
			summary += ".";
			if (flags.Length > 0) summary += $" Flags: {string.Join(", ", flags)}.";
			w.Summary(summary);
			w.Line($"public static readonly GameConVar<{type}> {Naming.Unique(Naming.Identifier(name), taken)} = new({Naming.Literal(name)});");
			Stats.ConVars++;
		}
		w.Close();
		output.Add("Console/ConVars.g.cs", w.ToString());

		w = new CodeWriter();
		w.Summary("Every console command a dedicated server has. Each runs one line at the server console: <c>ConCommands.changelevel(\"dl_midtown\")</c>.");
		w.Open("public static class ConCommands");
		w.Line("private static void Run(string command, string arguments)");
		w.Line("\t=> global::DeadworksManaged.Api.Server.ExecuteCommand(arguments.Length == 0 ? command : command + \" \" + arguments);");
		taken = new HashSet<string>(ObjectMembers, StringComparer.Ordinal) { "ConCommands", "Run" };
		foreach (var command in root.GetProperty("commands").EnumerateArray().OrderBy(c => c.GetProperty("name").GetString(), StringComparer.Ordinal)) {
			string name = command.GetProperty("name").GetString()!;
			string[] flags = Flags(command);
			if (!OnServer(flags) || !Naming.IsIdentifier(name)) continue;
			string summary = Naming.Doc(Scalar(command, "description").TrimEnd('.'));
			if (summary.Length > 0) summary += ".";
			if (flags.Length > 0) summary = (summary + $" Flags: {string.Join(", ", flags)}.").Trim();
			w.Line();
			w.Summary(summary);
			w.Line($"public static void {Naming.Unique(Naming.Identifier(name), taken)}(string arguments = \"\") => Run({Naming.Literal(name)}, arguments);");
			Stats.Commands++;
		}
		w.Close();
		output.Add("Console/ConCommands.g.cs", w.ToString());
	}

	public void EmitNames(OutputSet output, JsonElement root) {
		output.Add("Names/HeroNames.g.cs", Constants("HeroNames",
			"The name of every hero, as <c>SelectHero</c> and the hero data take it.",
			root.GetProperty("heroes").EnumerateArray().Select(h => {
				string id = h.GetProperty("vdata").TryGetProperty("m_HeroID", out var heroId) ? $" Hero ID {heroId}." : "";
				return (Name(h), $"{Display(h)}{id}".Trim());
			}), n => Stats.Heroes = n));

		var abilities = root.GetProperty("abilities").EnumerateArray().ToList();
		static string AbilityType(JsonElement a) => a.GetProperty("vdata").TryGetProperty("m_eAbilityType", out var type) ? type.ToString() : "";
		static string AbilityDoc(JsonElement a) {
			string type = AbilityType(a);
			type = type.StartsWith("EAbilityType_") ? type[13..] : type;
			return $"{Display(a)}{(type.Length > 0 ? $" {type}." : "")}".Trim();
		}
		output.Add("Names/ItemNames.g.cs", Constants("ItemNames",
			"The name of every shop item, as <c>AddItem</c> takes it.",
			abilities.Where(a => AbilityType(a) == "EAbilityType_Item").Select(a => (Name(a), AbilityDoc(a))), n => Stats.Items = n));
		output.Add("Names/AbilityNames.g.cs", Constants("AbilityNames",
			"The name of every ability that is not a shop item, as <c>AddAbility</c> takes it.",
			abilities.Where(a => AbilityType(a) != "EAbilityType_Item").Select(a => (Name(a), AbilityDoc(a))), n => Stats.Abilities = n));

		output.Add("Names/ModifierNames.g.cs", Constants("ModifierNames",
			"The name of every modifier, as <c>AddModifier</c> takes it. A modifier an ability defines inside itself is named "
			+ "<c>ability/modifier</c>; its constant joins the two with <c>__</c>.",
			root.GetProperty("modifiers").EnumerateArray().Select(m => {
				string cls = m.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
				return (Name(m), cls.Length > 0 ? $"Class <c>{Naming.Doc(cls)}</c>." : "");
			}), n => Stats.Modifiers = n));
	}

	private static string Name(JsonElement e) => e.GetProperty("name").GetString()!;

	private static string Display(JsonElement e)
		=> e.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String && display.GetString() is { Length: > 0 } text
			? Naming.Doc(text.TrimEnd('.')) + "."
			: "";

	private static string Constants(string className, string summary, IEnumerable<(string Name, string Doc)> entries, Action<int> count) {
		var w = new CodeWriter();
		w.Summary(summary);
		w.Open($"public static class {className}");
		var taken = new HashSet<string>(ObjectMembers, StringComparer.Ordinal) { className };
		int n = 0;
		foreach (var (name, doc) in entries.DistinctBy(e => e.Name).OrderBy(e => e.Name, StringComparer.Ordinal)) {
			w.Summary(doc);
			w.Line($"public const string {Naming.Unique(Naming.Identifier(name.Replace("/", "__")), taken)} = {Naming.Literal(name)};");
			n++;
		}
		w.Close();
		count(n);
		return w.ToString();
	}

	public static string EmitBuild(int version, string date) {
		var w = new CodeWriter();
		w.Summary("The Deadlock build this assembly was generated from. Field offsets are still looked up in the running game, "
			+ "so a newer build only breaks what it renamed or removed.");
		w.Open("public static class GameBuild");
		w.Summary("The build's <c>ClientVersion</c>.");
		w.Line($"public const int Version = {version};");
		w.Summary("The build's date, as the game reports it.");
		w.Line($"public const string Date = {Naming.Literal(date)};");
		w.Close();
		return w.ToString();
	}
}
