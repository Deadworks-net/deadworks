namespace DeadworksManaged.Api;

/// <summary>Named sets of <see cref="MaskTrace"/> layers.</summary>
public static class CollisionLayers {
	/// <summary>
	/// Gunfire and bullet-like abilities. Add to <see cref="CCollisionProperty.InteractsExclude"/> to let shots pass
	/// through an entity that players still collide with.
	/// </summary>
	public const MaskTrace BulletsAndAbilities = MaskTrace.CitadelBullet | MaskTrace.CitadelAbility;
}
