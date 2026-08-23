namespace DeadworksManaged.Api;

/// <summary>
/// What an imbuable item does to the ability it is imbued into
/// (<c>ECitadelTargetAbilityEffects</c> in the game SDK).
/// <see cref="None"/> means the item declares no imbue behaviour and cannot be imbued at all.
/// </summary>
[Flags]
public enum ImbueEffects : uint {
	/// <summary>Not an imbuable item.</summary>
	None = 0,

	/// <summary>Boosts the imbued ability's values (Compress Cooldown, Mystic Expansion, ...).</summary>
	ModifierValue = 1 << 0,

	/// <summary>Triggers off the imbued ability being cast (Echo Shard, Surge of Power, ...).</summary>
	Active = 1 << 1,

	/// <summary>Like <see cref="Active"/>, but the imbued ability may not be the hero's ultimate.</summary>
	ActiveNonUltimate = 1 << 2,
}
