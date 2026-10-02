namespace DeadworksManaged.Api;

// The values are the game's CameraParam values (citadel_usermessages.proto), so PlayerCamera converts with a cast.

/// <summary>A camera setting that <see cref="PlayerCamera"/> can move or hold.</summary>
public enum CameraSetting {
	/// <summary>How far the camera sits behind what it looks at, normally 150 units.</summary>
	Distance = 2,
	/// <summary>Field of view in degrees. Each player picks their own, 90 by default.</summary>
	Fov = 3,
	/// <summary>The point in the world the camera looks at, normally the hero. The only setting that takes a position.</summary>
	Target = 4,
	/// <summary>Shifts the camera up or down.</summary>
	VerticalOffset = 5,
	/// <summary>Shifts the camera sideways.</summary>
	HorizontalOffset = 6,
}
