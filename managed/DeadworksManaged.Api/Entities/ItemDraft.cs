namespace DeadworksManaged.Api;

/// <summary>
/// Wraps ItemDraftRoundState_t - a player's street brawl item draft, found on
/// <see cref="CCitadelPlayerPawn.ItemDraft"/>. Holds the options currently offered and how many
/// picks and rounds are left. Every setter is networked to the player.
/// </summary>
public sealed unsafe class ItemDraftRoundState : NativeEntity {
	private static ReadOnlySpan<byte> Class => "ItemDraftRoundState_t"u8;

	internal ItemDraftRoundState(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _vecOptions = new(Class, "m_vecOptions"u8);

	/// <summary>
	/// The options offered this round, in the order the player sees them. Empty when no round has
	/// been rolled. Change what is offered by editing the options in place.
	/// </summary>
	public IReadOnlyList<ItemDraftOption> Options {
		get {
			int stride = ItemDraftOption.Size;
			nint vecAddr = _vecOptions.GetAddress(Handle);
			int count = NativeInterop.GetUtlVectorSize((void*)vecAddr);
			byte* data = (byte*)NativeInterop.GetUtlVectorData((void*)vecAddr);
			if (data == null || count <= 0 || stride <= 0) return Array.Empty<ItemDraftOption>();

			var result = new ItemDraftOption[count];
			for (int i = 0; i < count; i++)
				result[i] = new ItemDraftOption((nint)(data + i * stride), i);
			return result;
		}
	}

	// Networked through the struct's own NetworkStateChanged (vtable index 1), which chains up to the pawn.
	private static readonly SchemaAccessor<uint> _nID = new(Class, "m_nID"u8, 1);
	/// <summary>Increases every time a round is rolled, including rerolls.</summary>
	public uint RoundId => _nID.Get(Handle);

	private static readonly SchemaAccessor<int> _nDraftsRemaining = new(Class, "m_nDraftsRemaining"u8, 1);
	/// <summary>How many more options the player may pick this round.</summary>
	public int DraftsRemaining { get => _nDraftsRemaining.Get(Handle); set => _nDraftsRemaining.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _nDraftsTotal = new(Class, "m_nDraftsTotal"u8, 1);
	/// <summary>How many options the player may pick in total this round.</summary>
	public int DraftsTotal { get => _nDraftsTotal.Get(Handle); set => _nDraftsTotal.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _nRoundsRemaining = new(Class, "m_nRoundsRemaining"u8, 1);
	/// <summary>How many draft rounds are left after this one in the current game round.</summary>
	public int RoundsRemaining { get => _nRoundsRemaining.Get(Handle); set => _nRoundsRemaining.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _nRoundsTotal = new(Class, "m_nRoundsTotal"u8, 1);
	/// <summary>How many draft rounds the current game round has.</summary>
	public int RoundsTotal { get => _nRoundsTotal.Get(Handle); set => _nRoundsTotal.Set(Handle, value); }
}

/// <summary>
/// Wraps ItemDraftOption_t - one card in a street brawl item draft. Picking it gives the player
/// <see cref="Item"/> plus both bonus items. Every setter is networked to the player.
/// </summary>
public sealed unsafe class ItemDraftOption : NativeEntity {
	private static ReadOnlySpan<byte> Class => "ItemDraftOption_t"u8;

	private static int _size = -1;
	internal static int Size {
		get {
			if (_size < 0) {
				fixed (byte* cls = "ItemDraftOption_t\0"u8)
					_size = NativeInterop.GetSchemaClassSize(cls);
			}
			return _size;
		}
	}

	internal ItemDraftOption(nint handle, int index) : base(handle) => Index = index;

	/// <summary>Position of this option in <see cref="ItemDraftRoundState.Options"/>.</summary>
	public int Index { get; }

	private static readonly SchemaAccessor<byte> _item = new(Class, "m_Item"u8);
	/// <summary>The item this option offers. The player picks the option by buying this item.</summary>
	public ItemDraftItem Item => new(_item.GetAddress(Handle));

	private static readonly SchemaAccessor<byte> _bonusItem1 = new(Class, "m_BonusItem1"u8);
	/// <summary>An extra item given along with <see cref="Item"/>. Empty on most options.</summary>
	public ItemDraftItem BonusItem1 => new(_bonusItem1.GetAddress(Handle));

	private static readonly SchemaAccessor<byte> _bonusItem2 = new(Class, "m_BonusItem2"u8);
	/// <summary>A second extra item given along with <see cref="Item"/>. Empty on most options.</summary>
	public ItemDraftItem BonusItem2 => new(_bonusItem2.GetAddress(Handle));

	private static readonly SchemaAccessor<bool> _bHasBeenDrafted = new(Class, "m_bHasBeenDrafted"u8, 1);
	/// <summary>True once the player has picked this option. Picked options can't be picked again.</summary>
	public bool HasBeenDrafted { get => _bHasBeenDrafted.Get(Handle); set => _bHasBeenDrafted.Set(Handle, value); }

	private static readonly SchemaAccessor<bool> _bRare = new(Class, "m_bRare"u8, 1);
	/// <summary>Shows the option as a rare roll.</summary>
	public bool IsRare { get => _bRare.Get(Handle); set => _bRare.Set(Handle, value); }
}

/// <summary>
/// Wraps ItemDraftItem_t - an item slot on an <see cref="ItemDraftOption"/>. Every setter is
/// networked to the player.
/// </summary>
public sealed unsafe class ItemDraftItem : NativeEntity {
	private static ReadOnlySpan<byte> Class => "ItemDraftItem_t"u8;

	// CUtlStringToken seed - item IDs are the token of the item's internal name.
	private const uint StringTokenSeed = 0x31415926u;

	internal ItemDraftItem(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<uint> _unItemID = new(Class, "m_unItemID"u8, 1);
	/// <summary>Item ID (the string token of the item's internal name), 0 when the slot is empty.</summary>
	public uint ItemId { get => _unItemID.Get(Handle); set => _unItemID.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _nUpgradeBits = new(Class, "m_nUpgradeBits"u8, 1);
	/// <summary>Upgrade flags the item is given with when picked, e.g. <see cref="UpgradeFlags.Enhanced"/>.</summary>
	public UpgradeFlags UpgradeBits { get => (UpgradeFlags)_nUpgradeBits.Get(Handle); set => _nUpgradeBits.Set(Handle, (int)value); }

	/// <summary>True when the slot holds no item.</summary>
	public bool IsEmpty => ItemId == 0;

	/// <summary>Whether the item is given enhanced.</summary>
	public bool Enhanced {
		get => (UpgradeBits & UpgradeFlags.Enhanced) != 0;
		set => UpgradeBits = value ? UpgradeBits | UpgradeFlags.Enhanced : UpgradeBits & ~UpgradeFlags.Enhanced;
	}

	/// <summary>Internal item name (e.g. "upgrade_sprint_booster"), or "" when empty or unknown.</summary>
	public string DesignerName {
		get {
			uint id = ItemId;
			if (id == 0) return "";
			// 4 == SUBCLASS_SCOPE_ABILITIES
			void* vdata = NativeInterop.LookupVDataByHash(4, id);
			return vdata != null ? new CEntitySubclassVDataBase((nint)vdata).Name : "";
		}
	}

	/// <summary>
	/// Puts <paramref name="itemName"/> (e.g. "upgrade_sprint_booster") in this slot.
	/// Throws <see cref="ArgumentException"/> if no item has that name.
	/// </summary>
	public void Set(string itemName, bool enhanced = false) {
		if (!ItemInfo.Exists(itemName))
			throw new ArgumentException($"Unknown item '{itemName}'", nameof(itemName));
		ItemId = GetItemId(itemName);
		UpgradeBits = enhanced ? UpgradeFlags.Enhanced : 0;
	}

	/// <summary>Empties this slot.</summary>
	public void Clear() {
		ItemId = 0;
		UpgradeBits = 0;
	}

	/// <summary>The item ID the game uses for <paramref name="itemName"/>.</summary>
	public static uint GetItemId(string itemName) => MurmurHash2.HashLowerCase(itemName, StringTokenSeed);
}
