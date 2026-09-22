namespace DeadworksManaged.Api;

/// <summary>The kind of damage being dealt (bullet, spirit, melee, ...). See <see cref="CTakeDamageInfo.CitadelDamageType"/>.</summary>
public enum ECitadelDamageType : uint {
	None = 0x0,
	Bullet = 0x1,
	/// <summary>Ability damage, shown in-game as Spirit damage.</summary>
	Ability = 0x2,
	Melee = 0x3,
	Environmental = 0x4,
	Poison = 0x5,
	WeakpointBonus = 0x6,
	Pure = 0x7,
}
