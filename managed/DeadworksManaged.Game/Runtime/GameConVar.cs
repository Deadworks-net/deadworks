using System.Globalization;
using System.Numerics;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>
/// One of the game's console variables, with the type the game declares for it. Generated
/// members of <see cref="ConVars"/> are instances of this; reading or writing <see cref="Value"/>
/// goes straight to the variable, including development-only and cheat-protected ones.
/// </summary>
public sealed class GameConVar<T> {
	private ConVar? _conVar;

	/// <summary>The variable's name, e.g. <c>citadel_trooper_gold_reward</c>.</summary>
	public string Name { get; }

	public GameConVar(string name) => Name = name;

	/// <summary>True if the running game has this variable.</summary>
	public bool Exists => Find() != null;

	/// <summary>The variable's current value.</summary>
	/// <exception cref="InvalidOperationException">The running game has no such variable.</exception>
	public T Value {
		get {
			var conVar = Require();
			if (typeof(T) == typeof(bool)) return (T)(object)conVar.GetBool();
			if (typeof(T) == typeof(int)) return (T)(object)conVar.GetInt();
			if (typeof(T) == typeof(float)) return (T)(object)conVar.GetFloat();
			return Parse(conVar.GetString());
		}
		set {
			var conVar = Require();
			if (value is bool b) conVar.SetBool(b);
			else if (value is int i) conVar.SetInt(i);
			else if (value is float f) conVar.SetFloat(f);
			else conVar.SetString(Format(value));
		}
	}

	private ConVar? Find() => _conVar ??= ConVar.Find(Name);

	private ConVar Require() => Find() ?? throw new InvalidOperationException(
		$"The running game has no console variable '{Name}'. DeadworksManaged.Game was generated from build {GameBuild.Version}.");

	private static T Parse(string text) {
		var inv = CultureInfo.InvariantCulture;
		object value;
		if (typeof(T) == typeof(uint)) value = uint.Parse(text, inv);
		else if (typeof(T) == typeof(ushort)) value = ushort.Parse(text, inv);
		else if (typeof(T) == typeof(ulong)) value = ulong.Parse(text, inv);
		else if (typeof(T) == typeof(long)) value = long.Parse(text, inv);
		else if (typeof(T) == typeof(double)) value = double.Parse(text, inv);
		else if (typeof(T) == typeof(Vector2)) { var v = Floats(text, 2); value = new Vector2(v[0], v[1]); }
		else if (typeof(T) == typeof(Vector3)) { var v = Floats(text, 3); value = new Vector3(v[0], v[1], v[2]); }
		else if (typeof(T) == typeof(Vector4)) { var v = Floats(text, 4); value = new Vector4(v[0], v[1], v[2], v[3]); }
		else if (typeof(T) == typeof(Color32)) { var v = Floats(text, 4, 255); value = new Color32((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3]); }
		else value = text;
		return (T)value;
	}

	private static float[] Floats(string text, int count, float fill = 0) {
		var values = new float[count];
		Array.Fill(values, fill);
		var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < count && i < parts.Length; i++)
			float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
		return values;
	}

	private static string Format(T value) {
		var inv = CultureInfo.InvariantCulture;
		return value switch {
			null => "",
			Vector2 v => string.Create(inv, $"{v.X} {v.Y}"),
			Vector3 v => string.Create(inv, $"{v.X} {v.Y} {v.Z}"),
			Vector4 v => string.Create(inv, $"{v.X} {v.Y} {v.Z} {v.W}"),
			Color32 c => c.ToString(),
			IFormattable f => f.ToString(null, inv),
			_ => value.ToString() ?? "",
		};
	}

	/// <summary>A variable reads as its value: <c>if (ConVars.sv_cheats) …</c>.</summary>
	public static implicit operator T(GameConVar<T> conVar) => conVar.Value;

	public override string ToString() => Name;
}
