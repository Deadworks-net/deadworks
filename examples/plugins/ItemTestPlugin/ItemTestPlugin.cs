using DeadworksManaged.Api;

namespace ItemTestPlugin;

public class ItemTestPlugin : DeadworksPluginBase
{
	public override string Name => "Item Test";

	public override void OnLoad(bool isReload) { }
	public override void OnUnload() { }

	[Command("additem", Description = "Give an item directly (no cost). Set enhanced=true for the upgraded version.")]
	public void CmdAddItem(CCitadelPlayerController caller, string itemName, bool enhanced = false)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var item = pawn.AddItem(itemName, enhanced);
		Reply(caller, item != null
			? $"Added '{itemName}' (enhanced={enhanced}) -> entity #{item.EntityIndex}"
			: $"Failed to add '{itemName}' (enhanced={enhanced})");
	}

	[Command("giveimbued", Description = "Give an imbuable item imbued into an ability slot (0-3), like 'giveitem <item> <index>'.")]
	public void CmdGiveImbued(CCitadelPlayerController caller, string itemName, EAbilitySlot imbueSlot, bool enhanced = false)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var result = pawn.TryAddItem(itemName, imbueSlot, out var item, enhanced);
		Reply(caller, result == ImbueResult.Success
			? $"Added '{itemName}' (enhanced={enhanced}) imbued into {string.Join(", ", item!.ImbuedAbilities)} -> entity #{item.EntityIndex}"
			: $"Failed to add '{itemName}' imbued into {imbueSlot}: {result}");
	}

	[Command("imbue", Description = "Imbue an item you already own into an ability slot (0-3)")]
	public void CmdImbue(CCitadelPlayerController caller, string itemName, EAbilitySlot slot)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var result = pawn.ImbueItem(itemName, slot);
		Reply(caller, result == ImbueResult.Success
			? $"Imbued '{itemName}' into {slot}"
			: $"Failed to imbue '{itemName}' into {slot}: {result}");
	}

	[Command("iteminfo", Description = "Show whether an item can be imbued, and which of your abilities accept it")]
	public void CmdItemInfo(CCitadelPlayerController caller, string itemName)
	{
		if (!ItemInfo.Exists(itemName))
		{
			Reply(caller, $"No item named '{itemName}'");
			return;
		}

		var effects = ItemInfo.GetImbueEffects(itemName);
		if (effects == ImbueEffects.None)
		{
			Reply(caller, $"'{itemName}' cannot be imbued");
			return;
		}

		Reply(caller, $"'{itemName}' imbue effects: {effects}");

		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		for (var slot = EAbilitySlot.Signature1; slot <= EAbilitySlot.Signature4; slot++)
		{
			var ability = pawn.AbilityComponent.GetAbilityBySlot(slot);
			string name = ability?.AbilityName ?? "<empty>";
			Reply(caller, $"  {(int)slot} {name}: {(pawn.CanImbue(itemName, slot) ? "ok" : "rejected")}");
		}
	}

	[Command("sellitem", Description = "Sell an item. fullRefund=true refunds the full price.")]
	public void CmdSellItem(CCitadelPlayerController caller, string itemName, bool fullRefund = false)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		bool ok = pawn.SellItem(itemName, fullRefund);
		Reply(caller, ok
			? $"Sold '{itemName}' (fullRefund={fullRefund})"
			: $"Failed to sell '{itemName}'");
	}

	[Command("removeitem", Description = "Remove an item from your inventory (no refund)")]
	public void CmdRemoveItem(CCitadelPlayerController caller, string itemName)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		bool ok = pawn.RemoveItem(itemName);
		Reply(caller, ok
			? $"Removed '{itemName}'"
			: $"Failed to remove '{itemName}'");
	}

	[Command("givegold", Description = "Give yourself gold (default 50000)")]
	public void CmdGiveGold(CCitadelPlayerController caller, int amount = 50000)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		pawn.ModifyCurrency(ECurrencyType.EGold, amount, ECurrencySource.ECheats, silent: true, forceGain: true);
		Reply(caller, $"Gave {amount} gold");
	}

	[Command("listitems", Description = "List all abilities/items currently on your pawn")]
	public void CmdListItems(CCitadelPlayerController caller)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var abilities = pawn.AbilityComponent.Abilities;
		Reply(caller, $"Pawn has {abilities.Count} abilities/items:");
		foreach (var ent in abilities)
		{
			string imbued = ent.IsImbued ? $" [imbued into {string.Join(", ", ent.ImbuedAbilities)}]" : "";
			Reply(caller, $"  #{ent.EntityIndex}: {ent.DesignerName} ({ent.Classname}){imbued}");
		}
	}

	[Command("rcon", Description = "Execute a server console command", SuppressChat = true)]
	public void CmdRcon(CCitadelPlayerController? caller, params string[] commandParts)
	{
		if (commandParts.Length == 0)
			throw new CommandException("Nothing to execute.");

		string command = string.Join(' ', commandParts);
		Server.ExecuteCommand(command);
		Reply(caller, $"Executed: {command}");
	}

	private static void Reply(CCitadelPlayerController? to, string message)
	{
		if (to != null) to.PrintToConsole(message);
		else Console.WriteLine(message);
	}
}
