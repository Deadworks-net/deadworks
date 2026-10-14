using System.Numerics;
using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>
/// The game's <c>GameTime_t</c>: a moment on the server's clock, in seconds. It converts to and
/// from <c>float</c>, so it reads and writes like one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GameTime(float Value) : IComparable<GameTime> {
	/// <summary>The server's clock right now.</summary>
	public static GameTime Now => new(GlobalVars.CurTime);

	/// <summary>The moment <paramref name="seconds"/> from now.</summary>
	public static GameTime FromNow(float seconds) => new(GlobalVars.CurTime + seconds);

	/// <summary>Seconds until this moment, or 0 once it has passed.</summary>
	public float Remaining => MathF.Max(0f, Value - GlobalVars.CurTime);

	/// <summary>Seconds since this moment; negative while it is still ahead.</summary>
	public float Elapsed => GlobalVars.CurTime - Value;

	/// <summary>True once the server's clock has reached this moment.</summary>
	public bool HasPassed => GlobalVars.CurTime >= Value;

	public static implicit operator float(GameTime time) => time.Value;
	public static implicit operator GameTime(float seconds) => new(seconds);

	public int CompareTo(GameTime other) => Value.CompareTo(other.Value);

	public override string ToString() => Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The game's <c>CTransform</c>: a position and a rotation, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Transform {
	public Vector3 Position;
	// The position is stored as four floats; the game keeps 1 in the fourth.
	private float _positionW;
	public Quaternion Rotation;

	public Transform(Vector3 position, Quaternion rotation) {
		Position = position;
		_positionW = 1f;
		Rotation = rotation;
	}

	public readonly override string ToString() => $"{Position} {Rotation}";
}

/// <summary>The game's <c>matrix3x4_t</c>: three rows of a rotation with the translation in the fourth column.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Matrix3x4 {
	public float M11, M12, M13, M14;
	public float M21, M22, M23, M24;
	public float M31, M32, M33, M34;

	/// <summary>The translation: the fourth column.</summary>
	public Vector3 Translation {
		readonly get => new(M14, M24, M34);
		set => (M14, M24, M34) = (value.X, value.Y, value.Z);
	}

	/// <summary>This transform as a <see cref="Matrix4x4"/> in .NET's row-vector convention.</summary>
	public readonly Matrix4x4 ToMatrix4x4() => new(
		M11, M21, M31, 0f,
		M12, M22, M32, 0f,
		M13, M23, M33, 0f,
		M14, M24, M34, 1f);
}

/// <summary>
/// The game's <c>CStrongHandle</c>: a reference to a loaded resource (a model, a material, a
/// particle system). It is one pointer to the resource system's record of it. Copy it from one
/// field to another; there is nothing here to make one from a file name.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ResourceHandle(nint Binding) {
	/// <summary>True if the field refers to a resource.</summary>
	public bool IsSet => Binding != 0;

	public override string ToString() => IsSet ? $"resource [0x{Binding:X}]" : "resource [none]";
}
