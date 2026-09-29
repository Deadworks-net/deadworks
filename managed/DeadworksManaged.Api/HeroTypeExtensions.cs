namespace DeadworksManaged.Api;

/// <summary>Extension methods for converting <see cref="Heroes"/> enum values to/from hero name strings and fetching VData.</summary>
public static class HeroTypeExtensions {
	private static readonly Dictionary<Heroes, string> _toName;
	private static readonly Dictionary<string, Heroes> _fromName;

	static HeroTypeExtensions() {
		_toName = new Dictionary<Heroes, string>();
		_fromName = new Dictionary<string, Heroes>(StringComparer.OrdinalIgnoreCase);
		foreach (var value in Enum.GetValues<Heroes>()) {
			var name = "hero_" + value.ToString().ToLowerInvariant();
			_toName[value] = name;
			_fromName[name] = value;
		}
	}

	/// <summary>Converts a <see cref="Heroes"/> value to its internal hero name string (e.g. "hero_inferno").</summary>
	public static string ToHeroName(this Heroes hero) => _toName[hero];

	private static readonly Dictionary<Heroes, string> _displayNames = new() {
		[Heroes.Inferno] = "Infernus",
		[Heroes.Gigawatt] = "Seven",
		[Heroes.Hornet] = "Vindicta",
		[Heroes.Ghost] = "Lady Geist",
		[Heroes.Atlas] = "Abrams",
		[Heroes.Wraith] = "Wraith",
		[Heroes.Forge] = "McGinnis",
		[Heroes.Chrono] = "Paradox",
		[Heroes.Dynamo] = "Dynamo",
		[Heroes.Kelvin] = "Kelvin",
		[Heroes.Haze] = "Haze",
		[Heroes.Astro] = "Holliday",
		[Heroes.Bebop] = "Bebop",
		[Heroes.Nano] = "Calico",
		[Heroes.Orion] = "Grey Talon",
		[Heroes.Krill] = "Mo & Krill",
		[Heroes.Shiv] = "Shiv",
		[Heroes.Tengu] = "Ivy",
		[Heroes.Kali] = "Kali",
		[Heroes.Warden] = "Warden",
		[Heroes.Yamato] = "Yamato",
		[Heroes.Lash] = "Lash",
		[Heroes.Viscous] = "Viscous",
		[Heroes.Gunslinger] = "Gunslinger",
		[Heroes.Yakuza] = "The Boss",
		[Heroes.Tokamak] = "Tokamak",
		[Heroes.Wrecker] = "Wrecker",
		[Heroes.Rutger] = "Rutger",
		[Heroes.Synth] = "Pocket",
		[Heroes.Thumper] = "Thumper",
		[Heroes.Mirage] = "Mirage",
		[Heroes.Slork] = "Fathom",
		[Heroes.Cadence] = "Cadence",
		[Heroes.Bomber] = "Bomber",
		[Heroes.ShieldGuy] = "Shield Guy",
		[Heroes.Viper] = "Vyper",
		[Heroes.Vandal] = "Vandal",
		[Heroes.Magician] = "Sinclair",
		[Heroes.Trapper] = "Trapper",
		[Heroes.Operative] = "Raven",
		[Heroes.VampireBat] = "Mina",
		[Heroes.Drifter] = "Drifter",
		[Heroes.Priest] = "Venator",
		[Heroes.Frank] = "Victor",
		[Heroes.Bookworm] = "Paige",
		[Heroes.Boho] = "Boho",
		[Heroes.Doorman] = "The Doorman",
		[Heroes.Skyrunner] = "Skyrunner",
		[Heroes.Swan] = "Swan",
		[Heroes.PunkGoat] = "Billy",
		[Heroes.Druid] = "Druid",
		[Heroes.Graf] = "Graf",
		[Heroes.Fortuna] = "Fortuna",
		[Heroes.Necro] = "Graves",
		[Heroes.Fencer] = "Apollo",
		[Heroes.Airheart] = "Airheart",
		[Heroes.Familiar] = "Rem",
		[Heroes.Werewolf] = "Silver",
		[Heroes.Unicorn] = "Celeste",
		[Heroes.Opera] = "Opera",
		[Heroes.GenericPerson] = "Generic Person",
		[Heroes.TargetDummy] = "TargetDummy",
		[Heroes.Ratking] = "Rat King",
		[Heroes.Chessmaster] = "Solomon",
		[Heroes.Artist] = "Violet",
		[Heroes.Nurse] = "Nurse Harrow",
		[Heroes.Baba] = "Baba",
	};

	/// <summary>Returns the localized English display name (e.g. "Grey Talon" for Orion).</summary>
	public static string ToDisplayName(this Heroes hero) =>
		_displayNames.TryGetValue(hero, out var name) ? name : hero.ToString();

	/// <summary>Tries to parse a hero name string (e.g. "hero_inferno") back to a <see cref="Heroes"/> enum value.</summary>
	public static bool TryParse(string heroName, out Heroes hero) => _fromName.TryGetValue(heroName, out hero);

	/// <summary>Get the native CitadelHeroData_t VData for this hero type. Returns null if not found.</summary>
	public static unsafe CitadelHeroData? GetHeroData(this Heroes hero) {
		var name = hero.ToHeroName();
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* ptr = utf8) {
			var result = NativeInterop.GetHeroData(ptr);
			return result == null ? null : new CitadelHeroData((nint)result);
		}
	}
}
