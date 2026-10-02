namespace DeadworksManaged.Api;

/// <summary>Wraps a Source 2 console variable (cvar). Use <see cref="Find"/> to look up existing cvars or <see cref="Create"/> to register new ones.</summary>
/// <remarks>
/// Unlike typing a value in the console, the Set methods here also work on cheat-protected cvars while
/// <c>sv_cheats</c> is off.
/// </remarks>
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
	/// <summary>Gets the cvar's value as true or false. Any non-zero number counts as true.</summary>
	public bool GetBool() => NativeInterop.GetConVarBool(_handle) != 0;
	/// <summary>Gets the cvar's value as a string.</summary>
	public string GetString() {
		byte* ptr = NativeInterop.GetConVarString(_handle);
		return ptr != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "" : "";
	}

	/// <summary>Sets the cvar's value as an integer.</summary>
	public void SetInt(int value) => NativeInterop.SetConVarInt(_handle, value);
	/// <summary>Sets the cvar's value as a float.</summary>
	public void SetFloat(float value) => NativeInterop.SetConVarFloat(_handle, value);
	/// <summary>Sets the cvar's value to true or false (1 or 0 on a number cvar).</summary>
	public void SetBool(bool value) => NativeInterop.SetConVarInt(_handle, value ? 1 : 0);

	/// <summary>
	/// Sets the cvar from text. The text is converted to whatever type the cvar holds, so this works for
	/// number, bool and text cvars alike. Returns false if the text isn't a valid value for this cvar,
	/// like "abc" for a number cvar.
	/// </summary>
	/// <remarks>A value outside the cvar's allowed range is clamped to that range.</remarks>
	public bool SetString(string value) {
		Span<byte> utf8 = Utf8.Encode(value, stackalloc byte[Utf8.Size(value)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.SetConVarString(_handle, ptr) != 0;
		}
	}

	/// <summary>
	/// Sets the cvar from a value of any type, the same way <see cref="SetString"/> does: numbers are
	/// written out as text and bools as 1 or 0. Returns false if the value doesn't fit this cvar's type.
	/// Handy for applying a dictionary of settings in one loop.
	/// </summary>
	/// <example>
	/// <code>
	/// var settings = new Dictionary&lt;string, object&gt; { ["sv_cheats"] = true, ["sv_gravity"] = 600, ["hostname"] = "My server" };
	/// foreach (var (name, value) in settings) {
	///     if (ConVar.Find(name)?.SetValue(value) != true)
	///         Console.WriteLine($"Couldn't set {name} to {value}");
	/// }
	/// </code>
	/// </example>
	public bool SetValue(object value) => SetString(value switch {
		bool b => b ? "1" : "0",
		IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
		_ => value?.ToString() ?? "",
	});
}
