namespace DeadworksManaged.Api;

/// <summary>
/// Fired after a street brawl item draft round has been rolled for a player - when a new draft
/// round starts and when the player rerolls. The options are already on the pawn; edit them
/// through <see cref="Options"/> and the player sees the edited draft.
/// </summary>
/// <example>
/// Always offering an enhanced Extra Charge as the first option:
/// <code>
/// public override void OnItemDraftRolled(ItemDraftRolledEvent args) {
///     if (args.Options.Count > 0)
///         args.Options[0].Item.Set("upgrade_extra_charge", enhanced: true);
/// }
/// </code>
/// </example>
public sealed class ItemDraftRolledEvent {
	internal ItemDraftRolledEvent(CCitadelPlayerPawn pawn) => Pawn = pawn;

	/// <summary>The pawn the draft was rolled for.</summary>
	public CCitadelPlayerPawn Pawn { get; }

	/// <summary>The player the draft was rolled for, or null if the pawn has no controller.</summary>
	public CCitadelPlayerController? Controller => Pawn.Controller;

	/// <summary>The pawn's draft state. Same as <see cref="CCitadelPlayerPawn.ItemDraft"/>.</summary>
	public ItemDraftRoundState Draft => Pawn.ItemDraft;

	/// <summary>The options just rolled, in the order the player sees them.</summary>
	public IReadOnlyList<ItemDraftOption> Options => Draft.Options;
}
