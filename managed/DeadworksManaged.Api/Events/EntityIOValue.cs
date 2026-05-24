using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Typed accessor for the variant value carried by an entity I/O input or output hook.</summary>
/// <remarks>
/// Holds a pointer to the native <c>variant_t</c> for the duration of the hook callback.
/// Do not retain an <see cref="EntityIOValue"/> past the callback's return — the native pointer becomes invalid.
/// If you need to retain the value, call the typed accessor (e.g. <see cref="AsString"/>) immediately and store the result.
/// </remarks>
public readonly unsafe struct EntityIOValue {
	private readonly nint _ptr;

	internal EntityIOValue(nint variantPtr) {
		_ptr = variantPtr;
	}

	/// <summary>The Source 2 field type of the underlying variant. <see cref="FieldType.Void"/> for null variants.</summary>
	public FieldType Type => _ptr == 0 ? FieldType.Void : (FieldType)NativeInterop.VariantGetType((void*)_ptr);

	/// <summary>True if the variant is null or holds no value.</summary>
	public bool IsNull => _ptr == 0 || Type == FieldType.Void;

	/// <summary>Returns the value as a string using Source 2's built-in formatter. Works for any variant type.</summary>
	public string AsString() {
		if (_ptr == 0) return "";
		byte* p = NativeInterop.VariantToCString((void*)_ptr);
		return p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
	}

	/// <summary>Converts the variant to a 32-bit signed integer. Returns 0 if the conversion is not supported.</summary>
	public int AsInt() => (int)AsInt64();

	/// <summary>Converts the variant to a 64-bit signed integer. Returns 0 if the conversion is not supported.</summary>
	public long AsInt64() => _ptr == 0 ? 0 : NativeInterop.VariantToInt64((void*)_ptr);

	/// <summary>Converts the variant to a 32-bit unsigned integer.</summary>
	public uint AsUInt() => (uint)AsInt64();

	/// <summary>Converts the variant to a 32-bit float. Returns 0 if the conversion is not supported.</summary>
	public float AsFloat() => (float)AsDouble();

	/// <summary>Converts the variant to a 64-bit float. Returns 0 if the conversion is not supported.</summary>
	public double AsDouble() => _ptr == 0 ? 0.0 : NativeInterop.VariantToFloat64((void*)_ptr);

	/// <summary>Converts the variant to a boolean.</summary>
	public bool AsBool() => _ptr != 0 && NativeInterop.VariantToBool((void*)_ptr) != 0;

	/// <summary>Reads the variant as a 3D vector (Vector, QAngle, PositionVector). Returns zero for other types.</summary>
	public Vector3 AsVector3() {
		if (_ptr == 0) return Vector3.Zero;
		float* buf = stackalloc float[4];
		NativeInterop.VariantToVector((void*)_ptr, buf);
		return new Vector3(buf[0], buf[1], buf[2]);
	}

	/// <summary>Reads the variant as a 2D vector. Returns zero for other types.</summary>
	public Vector2 AsVector2() {
		if (_ptr == 0) return Vector2.Zero;
		float* buf = stackalloc float[4];
		NativeInterop.VariantToVector((void*)_ptr, buf);
		return new Vector2(buf[0], buf[1]);
	}

	/// <summary>Reads the variant as a 4D vector or quaternion. Returns zero for other types.</summary>
	public Vector4 AsVector4() {
		if (_ptr == 0) return Vector4.Zero;
		float* buf = stackalloc float[4];
		NativeInterop.VariantToVector((void*)_ptr, buf);
		return new Vector4(buf[0], buf[1], buf[2], buf[3]);
	}

	/// <summary>Reads the variant as an RGBA color. Returns transparent black for non-color variants.</summary>
	public Color AsColor() {
		if (_ptr == 0) return Color.FromArgb(0, 0, 0, 0);
		uint packed = NativeInterop.VariantToColor((void*)_ptr);
		return Color.FromArgb(
			(int)((packed >> 24) & 0xFF),
			(int)(packed & 0xFF),
			(int)((packed >> 8) & 0xFF),
			(int)((packed >> 16) & 0xFF));
	}

	/// <summary>Resolves the variant as a <see cref="CBaseEntity"/>. Returns null for non-ehandle variants or invalid handles.</summary>
	public CBaseEntity? AsEntity() {
		if (_ptr == 0) return null;
		uint handle = NativeInterop.VariantToEHandle((void*)_ptr);
		return handle == CBaseEntity.InvalidEntityHandle ? null : new CBaseEntity(handle);
	}

	/// <summary>Empty/null variant value (no underlying native pointer).</summary>
	public static EntityIOValue Empty => new(0);
}
