using System.Globalization;
using DeadworksManaged.Game;

// In the API's namespace so the members appear without another using directive.
namespace DeadworksManaged.Api;

/// <summary>An ability's properties as numbers. The names are in <see cref="AbilityProperties"/>.</summary>
public static class AbilityPropertyExtensions {
	/// <summary>Hammer units in a metre: the game converts a distance its data writes in metres by this.</summary>
	public const float UnitsPerMetre = 39.37f;

	extension(Schema.CitadelAbilityProperty_t self) {
		/// <summary>True if the data writes the value in metres (<c>"25m"</c>).</summary>
		public bool InMetres => Split(self.m_strValue).Metres;

		/// <summary>
		/// The number the data gives, as written: 25 for <c>"25m"</c>. NaN if the value is not one
		/// number. This is the base value, before upgrades, items and scaling.
		/// </summary>
		public float Number => Split(self.m_strValue).Number;

		/// <summary><see cref="Number"/>, with a distance written in metres converted to Hammer units.</summary>
		public float InGameUnits {
			get {
				var (number, metres) = Split(self.m_strValue);
				return metres ? number * UnitsPerMetre : number;
			}
		}
	}

	extension(Schema.CitadelAbilityVData self) {
		/// <summary>
		/// The base value of the property <paramref name="name"/> (one of <see cref="AbilityProperties"/>),
		/// as the data writes it, or null if the ability has no such property or it is not one number.
		/// </summary>
		public float? Property(string name) {
			var property = self.m_mapAbilityProperties[name];
			if (property == null) return null;
			float number = Split(property.m_strValue).Number;
			return float.IsNaN(number) ? null : number;
		}
	}

	private static (float Number, bool Metres) Split(string text) {
		ReadOnlySpan<char> value = text.AsSpan().Trim();
		bool metres = value.EndsWith("m", StringComparison.Ordinal);
		if (metres) value = value[..^1];
		return (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number : float.NaN, metres);
	}
}
