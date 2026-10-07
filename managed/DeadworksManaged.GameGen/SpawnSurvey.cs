using System.Text.Json;

namespace DeadworksManaged.GameGen;

/// <summary>What happened when one name was created and spawned alone on a dedicated server.</summary>
/// <param name="Result">
/// <c>lived</c>, <c>vanished</c> (gone within half a second), <c>refused</c> (the game created
/// nothing), <c>threw</c>, <c>crashed</c> or <c>hung</c> (twice, the second time first thing on a
/// fresh server).
/// </param>
/// <param name="Model">A model the engine reported as not loaded when the entity spawned, or null.</param>
sealed record SurveyResult(string Result, string? Model) {
	/// <summary>True if spawning this takes the server down or does nothing, so it gets no function.</summary>
	public bool Unusable => Result is "crashed" or "hung" or "refused" or "threw";
}

/// <summary>
/// The spawn survey (<c>spawn-survey.json</c>, written by <c>tools/spawn-survey</c>): every name
/// <c>Spawn</c> could have a function for, tried on a real server. The registries say what the
/// game can create; only trying says what survives being created. It also lists the console
/// variables the game declares that a dedicated server never registers.
/// </summary>
sealed class SpawnSurvey {
	public int Build { get; private init; }
	public string Map { get; private init; } = "";
	public Dictionary<string, SurveyResult> Results { get; } = new(StringComparer.Ordinal);
	public HashSet<string> AbsentConVars { get; } = new(StringComparer.Ordinal);

	public static SpawnSurvey Load(JsonElement root) {
		var survey = new SpawnSurvey {
			Build = root.TryGetProperty("build", out var build) ? build.GetInt32() : 0,
			Map = root.TryGetProperty("map", out var map) ? map.GetString() ?? "" : "",
		};
		if (root.TryGetProperty("results", out var results))
			foreach (var entry in results.EnumerateObject())
				survey.Results[entry.Name] = new(
					entry.Value.GetProperty("result").GetString() ?? "",
					entry.Value.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null);
		if (root.TryGetProperty("absentConVars", out var absent))
			foreach (var name in absent.EnumerateArray()) survey.AbsentConVars.Add(name.GetString() ?? "");
		return survey;
	}
}
