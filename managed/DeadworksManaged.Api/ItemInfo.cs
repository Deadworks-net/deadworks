namespace DeadworksManaged.Api;

/// <summary>
/// Lookups against item definitions by internal name (e.g. <c>"upgrade_echo_shard"</c>).
/// These read the item's VData, so they work before the item has ever been granted.
/// </summary>
public static unsafe class ItemInfo {
	// -1 = no definition by that name, otherwise the raw ECitadelTargetAbilityEffects value.
	private static int RawImbueEffects(string itemName) {
		Span<byte> utf8 = Utf8.Encode(itemName, stackalloc byte[Utf8.Size(itemName)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.GetItemImbueEffects(ptr);
		}
	}

	/// <summary>True when an item definition with this internal name exists.</summary>
	public static bool Exists(string itemName) => RawImbueEffects(itemName) >= 0;

	/// <summary>
	/// The imbue behaviour this item declares, or <see cref="ImbueEffects.None"/> when the item
	/// cannot be imbued or does not exist.
	/// </summary>
	public static ImbueEffects GetImbueEffects(string itemName) {
		int raw = RawImbueEffects(itemName);
		return raw > 0 ? (ImbueEffects)raw : ImbueEffects.None;
	}

	/// <summary>
	/// True when this item has to be imbued into one of the hero's abilities to do anything -
	/// Echo Shard, Mystic Reverb, Compress Cooldown and friends.
	/// </summary>
	public static bool CanBeImbued(string itemName) => RawImbueEffects(itemName) > 0;
}
