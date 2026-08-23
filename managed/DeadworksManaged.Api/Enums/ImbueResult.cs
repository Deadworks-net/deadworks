namespace DeadworksManaged.Api;

/// <summary>
/// Outcome of an imbue attempt. Every value other than <see cref="Success"/> leaves the pawn
/// exactly as it was - a rejected imbue never grants the item.
/// </summary>
public enum ImbueResult {
	/// <summary>The item is on the pawn and imbued into the requested ability.</summary>
	Success,

	/// <summary>No item definition with that internal name exists.</summary>
	UnknownItem,

	/// <summary>
	/// The item exists but has no imbue behaviour. Grant it with the plain
	/// <c>AddItem(name, enhanced)</c> overload instead.
	/// </summary>
	ItemNotImbuable,

	/// <summary>The pawn has no ability in the requested slot.</summary>
	NoAbilityInSlot,

	/// <summary>
	/// The ability exists, but this item cannot be imbued into it - for example an
	/// <see cref="ImbueEffects.ActiveNonUltimate"/> item aimed at the hero's ultimate, or an
	/// active item aimed at a passive ability.
	/// </summary>
	AbilityRejected,

	/// <summary>The item could not be granted (already owned, disabled, or no free slot).</summary>
	GrantFailed,

	/// <summary>The pawn does not own the item being imbued.</summary>
	ItemNotOwned,
}
