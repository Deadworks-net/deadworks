using System.Globalization;
using System.Numerics;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>
/// The key values an entity is spawned with, as a map would give them. Generated classes
/// in <see cref="Keys"/> add a typed property per key the entity reads; <see cref="With(string, string)"/>
/// and its overloads set any other key by name.
/// </summary>
/// <example><code>Spawn.point_worldtext(new() { origin = position, message = "hello", targetname = "sign" });</code></example>
public abstract class EntityKeys {
	private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The entity's name, which <c>Entities.FirstByName</c> and map logic find it by. The entity
	/// system reads this key itself, so no datamap declares it.
	/// </summary>
	public string? targetname { get => GetText("targetname"); set => SetKey("targetname", value); }

	/// <summary>Sets a key by name, for one the game added since this assembly was generated.</summary>
	public EntityKeys With(string key, string value) { _values[key] = value; return this; }
	/// <inheritdoc cref="With(string, string)"/>
	public EntityKeys With(string key, bool value) { _values[key] = value; return this; }
	/// <inheritdoc cref="With(string, string)"/>
	public EntityKeys With(string key, int value) { _values[key] = value; return this; }
	/// <inheritdoc cref="With(string, string)"/>
	public EntityKeys With(string key, float value) { _values[key] = value; return this; }
	/// <inheritdoc cref="With(string, string)"/>
	public EntityKeys With(string key, Vector3 value) { _values[key] = value; return this; }
	/// <inheritdoc cref="With(string, string)"/>
	public EntityKeys With(string key, Color32 value) { _values[key] = value; return this; }

	/// <summary>A string token key (<c>FIELD_UTLSTRINGTOKEN</c>), which the game stores hashed.</summary>
	private sealed record Token(string Text);

	protected void SetKey(string key, object? value) {
		if (value is null) _values.Remove(key);
		else _values[key] = value;
	}

	protected void SetToken(string key, string? value) => SetKey(key, value is null ? null : new Token(value));

	protected T? GetValue<T>(string key) where T : struct => _values.TryGetValue(key, out var value) && value is T typed ? typed : null;

	protected string? GetText(string key) => _values.TryGetValue(key, out var value)
		? value switch { string s => s, Token t => t.Text, _ => null }
		: null;

	internal CEntityKeyValues Build() {
		var keyValues = new CEntityKeyValues();
		foreach (var (key, value) in _values) {
			switch (value) {
				case string s: keyValues.SetString(key, s); break;
				case bool b: keyValues.SetBool(key, b); break;
				case int i: keyValues.SetInt(key, i); break;
				case float f: keyValues.SetFloat(key, f); break;
				case Vector3 v: keyValues.SetVector(key, v); break;
				case Color32 c: keyValues.SetColor(key, c.R, c.G, c.B, c.A); break;
				case Token t: keyValues.SetStringToken(key, t.Text); break;
				case Enum e: keyValues.SetInt(key, Convert.ToInt32(e, CultureInfo.InvariantCulture)); break;
				default: keyValues.SetString(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
			}
		}
		return keyValues;
	}
}

/// <summary>
/// The spawn key values of each kind of entity, as typed properties. A class here is the
/// parameter of a <see cref="Spawn"/> function, so it is usually written as <c>new() { … }</c>.
/// </summary>
public static partial class Keys;

/// <summary>Creates entities for the generated <see cref="Spawn"/> methods.</summary>
public static class Spawner {
	/// <summary>
	/// Creates the entity the game calls <paramref name="designerName"/>, spawns it with
	/// <paramref name="keys"/>, and returns it as a <typeparamref name="T"/>. Null if the game
	/// could not create it.
	/// </summary>
	/// <param name="designerName">
	/// The entity's name as a map names it (<c>prop_dynamic</c>), or the name of a data entry
	/// it is created from (<c>npc_boss_tier1</c>), which gives the entity that entry's data.
	/// </param>
	/// <param name="keys">The key values a map would give the entity.</param>
	/// <param name="beforeSpawn">
	/// Runs after the entity exists and before it spawns, for fields the game only reads while
	/// spawning (a unit's <c>m_iInitialTeamNum</c>).
	/// </param>
	public static T? Create<T>(string designerName, EntityKeys? keys = null, Action<T>? beforeSpawn = null) where T : Schema.CEntityInstance, ISchemaClass<T> {
		var entity = CBaseEntity.CreateByDesignerName(designerName);
		if (entity == null) return null;
		beforeSpawn?.Invoke(SchemaRegistry.Bridge<T>(entity.EntityHandle));
		if (keys != null) entity.Spawn(keys.Build());
		else entity.Spawn();
		return entity.IsValid ? SchemaRegistry.Bridge<T>(entity.EntityHandle) : null;
	}

	/// <summary>Text for an input's parameter: the game reads numbers with a dot and booleans as 0 or 1.</summary>
	public static string InputText(float value) => value.ToString(CultureInfo.InvariantCulture);
	/// <inheritdoc cref="InputText(float)"/>
	public static string InputText(int value) => value.ToString(CultureInfo.InvariantCulture);
	/// <inheritdoc cref="InputText(float)"/>
	public static string InputText(bool value) => value ? "1" : "0";
	/// <inheritdoc cref="InputText(float)"/>
	public static string InputText(Vector3 value) => string.Create(CultureInfo.InvariantCulture, $"{value.X} {value.Y} {value.Z}");
	/// <inheritdoc cref="InputText(float)"/>
	public static string InputText(Color32 value) => $"{value.R} {value.G} {value.B}";
}
