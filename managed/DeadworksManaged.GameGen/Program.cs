using System.Net;
using System.Text;
using System.Text.Json;
using DeadworksManaged.GameGen;

// Generates managed/DeadworksManaged.Game/Generated from one build of the Deadworks modding
// database (https://deadworks.net/db): schema classes and enums, entity spawn functions, key
// values, inputs and outputs, console variables and commands, and hero, ability, item and
// modifier names.
//
//   dotnet run --project managed/DeadworksManaged.GameGen -- [--build N|latest] [--input DIR]
//       [--out DIR] [--api DIR] [--report FILE] [--check-api]
//
// --input reads schemas.json, entities.json, concommands.json and vdata.json from DIR instead
// of downloading them. --check-api exits with 3 when a handwritten SchemaAccessor in
// DeadworksManaged.Api names a field the build does not have.

const string Endpoint = "https://deadworks.net/api/moddb/builds";

string build = "latest";
string? input = null, output = null, apiDirectory = null, report = null;
bool checkApi = false;
for (int i = 0; i < args.Length; i++) {
	string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
	switch (args[i]) {
		case "--build": build = Next(); break;
		case "--input": input = Next(); break;
		case "--out": output = Next(); break;
		case "--api": apiDirectory = Next(); break;
		case "--report": report = Next(); break;
		case "--check-api": checkApi = true; break;
		default: throw new ArgumentException($"Unknown argument {args[i]}");
	}
}

string? repo = Directory.GetCurrentDirectory();
while (repo != null && !File.Exists(Path.Combine(repo, "deadworks.slnx"))) repo = Path.GetDirectoryName(repo);
output ??= repo != null ? Path.Combine(repo, "managed", "DeadworksManaged.Game", "Generated") : throw new ArgumentException("Run inside the repository or pass --out");
apiDirectory ??= repo != null ? Path.Combine(repo, "managed", "DeadworksManaged.Api") : throw new ArgumentException("Run inside the repository or pass --api");

using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(5) };

async Task<JsonDocument> Load(string name) {
	if (input != null) return JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(input, name)));
	Console.WriteLine($"Fetching {Endpoint}/{build}/{name}");
	return JsonDocument.Parse(await http.GetByteArrayAsync($"{Endpoint}/{build}/{name}"));
}

using var schemasJson = await Load("schemas.json");
using var entitiesJson = await Load("entities.json");
using var consoleJson = await Load("concommands.json");
using var vdataJson = await Load("vdata.json");

int version = schemasJson.RootElement.GetProperty("version").GetInt32();
foreach (var (name, document) in new[] { ("entities.json", entitiesJson), ("concommands.json", consoleJson), ("vdata.json", vdataJson) })
	if (document.RootElement.GetProperty("version").GetInt32() != version)
		throw new InvalidDataException($"{name} is from build {document.RootElement.GetProperty("version")}, schemas.json from {version}");

// The build's date is only in the build list; a local dump goes without it.
string date = "";
if (input == null) {
	using var builds = JsonDocument.Parse(await http.GetByteArrayAsync(Endpoint));
	foreach (var b in builds.RootElement.GetProperty("builds").EnumerateArray())
		if (b.GetProperty("version").GetInt32() == version && b.TryGetProperty("version_date", out var d))
			date = d.GetString() ?? "";
}

var schema = SchemaModel.Load(schemasJson.RootElement);
var entities = EntityModel.Load(entitiesJson.RootElement);
var api = ApiScanner.Scan(apiDirectory);

var setterWarnings = new Dictionary<string, string>(StringComparer.Ordinal);
string overridesPath = Path.Combine(AppContext.BaseDirectory, "overrides.json");
if (File.Exists(overridesPath)) {
	using var overrides = JsonDocument.Parse(File.ReadAllText(overridesPath));
	if (overrides.RootElement.TryGetProperty("setterWarnings", out var warnings))
		foreach (var warning in warnings.EnumerateObject()) setterWarnings[warning.Name] = warning.Value.GetString() ?? "";
}

var files = new OutputSet(output);
var schemaEmitter = new SchemaEmitter(schema, entities, api, setterWarnings);
schemaEmitter.Emit(files);
var entityEmitter = new EntityEmitter(entities, schema, schemaEmitter.Ids);
entityEmitter.Emit(files);
var catalog = new CatalogEmitter();
catalog.EmitConsole(files, consoleJson.RootElement);
catalog.EmitNames(files, vdataJson.RootElement);
files.Add("GameBuild.g.cs", CatalogEmitter.EmitBuild(version, date));

var (written, deleted) = files.Flush();

// A handwritten accessor whose field the build no longer has reads offset 0 at run time.
var stale = api.Accessors
	.Where(a => !schema.AllClasses.TryGetValue(a.SchemaClass, out var c) || c.Fields.All(f => f.Name != a.Field))
	.OrderBy(a => a.File, StringComparer.Ordinal).ThenBy(a => a.Line)
	.ToList();

var s = schemaEmitter.Stats;
var e = entityEmitter.Stats;
var k = catalog.Stats;
var summary = new StringBuilder();
summary.AppendLine($"Generated `DeadworksManaged.Game` from Deadlock build **{version}**{(date.Length > 0 ? $" ({date})" : "")}.");
summary.AppendLine();
summary.AppendLine("| | |");
summary.AppendLine("|---|---|");
summary.AppendLine($"| Schema classes | {s.Classes} ({s.EntityClasses} entities) |");
summary.AppendLine($"| Schema fields | {s.Fields} ({s.Fields - s.RawFields} typed, {s.RawFields} by address) |");
summary.AppendLine($"| Schema enums | {s.Enums} |");
summary.AppendLine($"| Curated wrappers bridged | {s.Bridges} |");
summary.AppendLine($"| Spawn functions | {e.Spawnable} ({e.NotSpawnable} entity names left out: abilities, items, players) |");
summary.AppendLine($"| Spawn key values | {e.Keys} in {e.KeyClasses} classes |");
summary.AppendLine($"| Inputs, outputs | {e.Inputs}, {e.Outputs} ({s.InputMethods} inputs as methods) |");
summary.AppendLine($"| Console variables, commands | {k.ConVars}, {k.Commands} |");
summary.AppendLine($"| Heroes, abilities, items, modifiers | {k.Heroes}, {k.Abilities}, {k.Items}, {k.Modifiers} |");
summary.AppendLine();
summary.AppendLine($"{files.Count} files: {written} written, {deleted} deleted.");
if (stale.Count > 0) {
	summary.AppendLine();
	summary.AppendLine($"### Handwritten accessors build {version} does not have");
	summary.AppendLine();
	summary.AppendLine("These `SchemaAccessor` fields in `DeadworksManaged.Api` name a class or field that is not in this build's schema, so they read offset 0:");
	summary.AppendLine();
	foreach (var a in stale)
		summary.AppendLine($"- `{a.SchemaClass}.{a.Field}` in `{a.File}:{a.Line}`{(a.Members.Length > 0 ? $" (`{a.Wrapper}.{string.Join("`, `", a.Members)}`)" : "")}");
}

Console.WriteLine();
Console.WriteLine(summary);
if (s.RawTypes.Count > 0)
	Console.WriteLine("Fields by address, by type: " + string.Join(", ", s.RawTypes.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}")));
if (report != null) File.WriteAllText(report, summary.ToString());

return checkApi && stale.Count > 0 ? 3 : 0;
