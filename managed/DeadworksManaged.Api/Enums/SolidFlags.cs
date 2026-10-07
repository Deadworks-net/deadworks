namespace DeadworksManaged.Api;

/// <summary>Solid flags on an entity. See <see cref="CCollisionProperty.SolidFlags"/>.</summary>
/// <remarks>
/// None of these change what collides with the entity. Use <see cref="CCollisionProperty.AddInteractsExclude"/> or
/// <see cref="CCollisionProperty.DisableCollision"/> for that.
/// </remarks>
[Flags]
public enum SolidFlags : byte {
	/// <summary>No flags.</summary>
	None = 0,
	/// <summary>The entity can't be stood on.</summary>
	NotStandable = 1 << 0,
	/// <summary>Grows the area that detects touches by the entity's trigger bloat, outwards and upwards.</summary>
	UseTriggerBounds = 1 << 1,
	/// <summary>Doesn't make the entity non-solid. It only stops the entity ignoring its owner, so the two can collide.</summary>
	NotSolid = 1 << 2,
}
