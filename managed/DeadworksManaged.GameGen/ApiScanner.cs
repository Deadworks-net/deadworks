using System.Text.RegularExpressions;

namespace DeadworksManaged.GameGen;

/// <summary>A curated wrapper class in DeadworksManaged.Api.</summary>
sealed record ApiWrapper(string Name, string? Base, string[] NativeNames);

/// <summary>A handwritten <c>SchemaAccessor</c> and the public members of its wrapper that use it.</summary>
sealed record ApiAccessor(string SchemaClass, string Field, string Wrapper, string[] Members, string File, int Line);

/// <summary>
/// What DeadworksManaged.Api already wraps, read from its source: the wrapper classes, and
/// which schema field each public member reads. The generator points a raw field at its
/// curated member, bridges each wrapper to its generated class, and reports accessors whose
/// field the game no longer has. Mirrors the modding database's parser
/// (deadworks-web <c>app/moddb/deadworks.server.js</c>).
/// </summary>
sealed partial class ApiScanner {
	public List<ApiWrapper> Wrappers { get; } = [];
	public List<ApiAccessor> Accessors { get; } = [];

	[GeneratedRegex(@"^\s*public\s+(?:(?:static|override|new|virtual|unsafe|readonly|sealed|abstract|partial)\s+)*[\w<>\[\]?,.\s]+?\s(\w+)\s*(\(|\{|=>|=|;)")]
	private static partial Regex Member();

	[GeneratedRegex(@"static\s+readonly\s+Schema(?:Array|String)?Accessor(?:<[^>]+>)?\s+(\w+)\s*=\s*new\s*\(\s*(?:""([^""]+)""u8|(Class))\s*,\s*""([^""]+)""u8")]
	private static partial Regex Accessor();

	[GeneratedRegex(@"^\s*\[NativeClass\(([^)]*)\)\]")]
	private static partial Regex NativeClass();

	[GeneratedRegex(@"^\s*public\s+(?:(?:sealed|unsafe|abstract|partial|readonly|ref)\s+)*(?:class|struct)\s+(\w+)(?:\s*:\s*(\w+))?")]
	private static partial Regex Class();

	[GeneratedRegex(@"ReadOnlySpan<byte>\s+Class\s*=>\s*""([^""]+)""u8")]
	private static partial Regex ClassShorthand();

	[GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
	private static partial Regex StringLiteral();

	[GeneratedRegex(@"//.*$")]
	private static partial Regex LineComment();

	public static ApiScanner Scan(string apiDirectory) {
		var scanner = new ApiScanner();
		foreach (var path in Directory.EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
			string relative = Path.GetRelativePath(apiDirectory, path).Replace('\\', '/');
			if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || relative.Contains("/Generated/")) continue;
			scanner.ScanFile(relative, File.ReadAllText(path));
		}
		return scanner;
	}

	// Source with string contents and comments blanked, so braces and names inside them do not count.
	private static string Code(string line)
		=> LineComment().Replace(StringLiteral().Replace(line, m => "\"" + new string(' ', m.Length - 2) + "\""), "");

	internal void ScanFile(string file, string text) {
		string? shorthand = ClassShorthand().Match(text) is { Success: true } s ? s.Groups[1].Value : null;
		var accessors = new List<(string Variable, string SchemaClass, string Field, string Wrapper, int Line)>();
		var members = new List<(string Name, string Wrapper, string Body)>();
		int depth = 0;
		string[]? pendingNative = null;
		string? currentClass = null;
		(string Name, string Wrapper, string Body)? member = null;

		string[] lines = text.Split('\n');
		for (int i = 0; i < lines.Length; i++) {
			string raw = lines[i].TrimEnd('\r'), line = Code(raw);

			if (NativeClass().Match(raw) is { Success: true } native)
				pendingNative = [.. StringLiteral().Matches(native.Groups[1].Value).Select(m => m.Value[1..^1])];

			if (depth == 0 && Class().Match(line) is { Success: true } cls) {
				currentClass = cls.Groups[1].Value;
				Wrappers.Add(new(currentClass, cls.Groups[2].Success ? cls.Groups[2].Value : null, pendingNative ?? []));
				pendingNative = null;
			}

			if (currentClass != null && Accessor().Match(raw) is { Success: true } accessor) {
				string? schemaClass = accessor.Groups[2].Success ? accessor.Groups[2].Value : shorthand;
				if (schemaClass != null)
					accessors.Add((accessor.Groups[1].Value, schemaClass, accessor.Groups[4].Value, currentClass, i + 1));
			}

			// A public member starts at class-body depth and runs until depth returns there.
			if (depth == 1 && member == null && currentClass != null && !line.Contains(" class ")
				&& Member().Match(line) is { Success: true } m)
				member = (m.Groups[1].Value, currentClass, "");
			if (member is { } open) member = open with { Body = open.Body + line + "\n" };

			foreach (char c in line) depth += c == '{' ? 1 : c == '}' ? -1 : 0;
			if (depth == 0) currentClass = null;

			string trimmed = line.Trim();
			if (member is { } done && depth == 1 && (trimmed.EndsWith('}') || trimmed.EndsWith(';'))) {
				members.Add(done);
				member = null;
			}
		}

		foreach (var a in accessors) {
			var uses = new Regex($@"\b{Regex.Escape(a.Variable)}\b");
			string[] users = [.. members.Where(m => m.Wrapper == a.Wrapper && uses.IsMatch(m.Body)).Select(m => m.Name).Distinct()];
			Accessors.Add(new(a.SchemaClass, a.Field, a.Wrapper, users, file, a.Line));
		}
	}

	/// <summary>True if the wrapper derives from <paramref name="baseName"/> in the scanned source.</summary>
	public bool DerivesFrom(ApiWrapper wrapper, string baseName) {
		var byName = Wrappers.GroupBy(w => w.Name).ToDictionary(g => g.Key, g => g.First());
		for (ApiWrapper? w = wrapper; w != null; w = w.Base != null ? byName.GetValueOrDefault(w.Base) : null)
			if (w.Name == baseName) return true;
		return false;
	}
}
