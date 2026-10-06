namespace DeadworksManaged.Api;

/// <summary>The shape an entity's physics body is built from. See <see cref="CCollisionProperty.SolidType"/>.</summary>
public enum SolidType : byte {
	/// <summary>No physics body.</summary>
	None = 0,
	/// <summary>The entity's brush geometry.</summary>
	Bsp = 1,
	/// <summary>An axis-aligned box from the entity's bounds.</summary>
	BBox = 2,
	/// <summary>A box from the entity's bounds that rotates with it.</summary>
	Obb = 3,
	/// <summary>A sphere around the entity's bounds.</summary>
	Sphere = 4,
	/// <summary>A single point, with no physics body.</summary>
	Point = 5,
	/// <summary>The collision mesh from the entity's model.</summary>
	VPhysics = 6,
	/// <summary>A capsule.</summary>
	Capsule = 7,
	/// <summary>A cylinder.</summary>
	Cylinder = 8,
}
