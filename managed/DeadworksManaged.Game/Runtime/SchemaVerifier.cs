using System.Reflection;

namespace DeadworksManaged.Game;

/// <summary>
/// Checks every generated schema field against the running game. Run it after a game
/// update (<c>dw_schema_verify</c>) to see what the update removed or renamed before a
/// plugin finds out by throwing.
/// </summary>
public static class SchemaVerifier {
	/// <param name="Classes">Generated classes checked.</param>
	/// <param name="Fields">Generated fields checked.</param>
	/// <param name="Missing">Fields the running game does not have, as <c>Class.m_field</c>.</param>
	public sealed record Report(int Classes, int Fields, IReadOnlyList<string> Missing);

	/// <summary>Looks every generated field up in the running game's schema. Slow; meant for a console command.</summary>
	public static Report Run() {
		int classes = 0, fields = 0;
		var missing = new List<string>();
		foreach (var type in typeof(Schema).GetNestedTypes(BindingFlags.Public)) {
			if (!typeof(SchemaObject).IsAssignableFrom(type)) continue;
			classes++;
			foreach (var member in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
				if (member.GetValue(null) is not SchemaField field) continue;
				fields++;
				if (!field.TryResolve()) missing.Add(field.ToString());
			}
		}
		missing.Sort(StringComparer.Ordinal);
		return new Report(classes, fields, missing);
	}
}
