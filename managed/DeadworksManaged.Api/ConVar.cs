namespace DeadworksManaged.Api;

/// <summary>Wraps a Source 2 console variable (cvar). Use <see cref="Find"/> to look up existing cvars or <see cref="Create"/> to register new ones.</summary>
public sealed unsafe class ConVar {
	private readonly ulong _handle;

	private ConVar(ulong handle) => _handle = handle;

	public bool IsValid => _handle != 0;

	/// <summary>Looks up an existing ConVar by name. Returns null if not found.</summary>
	public static ConVar? Find(string name) {
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* ptr = utf8) {
			ulong handle = NativeInterop.FindConVar(ptr);
			return handle != 0 ? new ConVar(handle) : null;
		}
	}

	/// <summary>Creates a new ConVar registered with the engine. Returns null if creation fails.</summary>
	public static ConVar? Create(string name, string defaultValue, string description = "", bool serverOnly = false) {
		Span<byte> nameUtf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		Span<byte> defUtf8 = Utf8.Encode(defaultValue, stackalloc byte[Utf8.Size(defaultValue)]);
		Span<byte> descUtf8 = Utf8.Encode(description, stackalloc byte[Utf8.Size(description)]);

		FCVar flags = serverOnly ? FCVar.UserInfo : FCVar.None;

		fixed (byte* namePtr = nameUtf8, defPtr = defUtf8, descPtr = descUtf8) {
			ulong handle = NativeInterop.CreateConVar(namePtr, defPtr, descPtr, (ulong)flags);
			return handle != 0 ? new ConVar(handle) : null;
		}
	}

	/// <summary>Gets the cvar's value as an integer.</summary>
	public int GetInt() => NativeInterop.GetConVarInt(_handle);
	/// <summary>Gets the cvar's value as a float.</summary>
	public float GetFloat() => NativeInterop.GetConVarFloat(_handle);
	/// <summary>Gets the cvar's value as a string.</summary>
	public string GetString() {
		byte* ptr = NativeInterop.GetConVarString(_handle);
		return ptr != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "" : "";
	}

	/// <summary>Sets the cvar's value as an integer.</summary>
	public void SetInt(int value) => NativeInterop.SetConVarInt(_handle, value);
	/// <summary>Sets the cvar's value as a float.</summary>
	public void SetFloat(float value) => NativeInterop.SetConVarFloat(_handle, value);
	/// <summary>Sets the cvar's value as a bool (1 or 0).</summary>
	public void SetBool(bool value) => NativeInterop.SetConVarInt(_handle, value ? 1 : 0);

	/// <summary>Gets the cvar's value as a bool (non-zero is true).</summary>
	public bool GetBool() => NativeInterop.GetConVarInt(_handle) != 0;

	/// <summary>
	/// Sets the cvar's value from a string. The engine parses the string into the cvar's own type,
	/// so this works for string cvars as well as numeric ones. Returns false if the value could not be
	/// parsed for that cvar's type.
	/// </summary>
	public bool SetString(string value) {
		Span<byte> utf8 = Utf8.Encode(value, stackalloc byte[Utf8.Size(value)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.SetConVarString(_handle, ptr) != 0;
		}
	}

	/// <summary>
	/// Sets the cvar from any boxed value, choosing <see cref="SetInt"/>, <see cref="SetFloat"/>,
	/// <see cref="SetBool"/> or <see cref="SetString"/> by runtime type. Convenient for applying a
	/// dictionary of startup cvars in one loop.
	/// </summary>
	public bool SetValue(object value) {
		switch (value) {
			case bool b: SetBool(b); return true;
			case int i: SetInt(i); return true;
			case float f: SetFloat(f); return true;
			case double d: SetFloat((float)d); return true;
			case string str: return SetString(str);
			default: return SetString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
		}
	}
}
