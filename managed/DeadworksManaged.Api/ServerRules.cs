namespace DeadworksManaged.Api;

/// <summary>
/// The Steam gameserver rules store, which Steam serves to clients as A2S_RULES.
/// <para>
/// Deadlock's engine never writes this store. The framework owns it outright and rewrites it
/// wholesale - <see cref="Clear"/>, then every key - whenever the content it advertises changes,
/// which is also the only way to remove a key: Steam has no call to delete a single one. That
/// makes the store unsafe to share, so this stays internal; a plugin-facing rules API would need
/// one store that every writer goes through.
/// </para>
/// </summary>
internal static unsafe class ServerRules {
	/// <summary>
	/// Removes every rule. Returns false if the Steam gameserver interface could not be resolved,
	/// which is how the caller learns Steam is not up yet.
	/// </summary>
	public static bool Clear() => NativeInterop.ClearServerKeyValues() != 0;

	/// <summary>
	/// Adds or updates one rule. Returns false if Steam could not be reached or the key was empty.
	/// Sent verbatim, so keep keys and values short - the whole reply has to fit in a datagram.
	/// </summary>
	public static bool Set(string key, string value) {
		Span<byte> k = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		Span<byte> v = Utf8.Encode(value ?? "", stackalloc byte[Utf8.Size(value ?? "")]);
		fixed (byte* kp = k, vp = v) {
			return NativeInterop.SetServerKeyValue(kp, vp) != 0;
		}
	}
}
