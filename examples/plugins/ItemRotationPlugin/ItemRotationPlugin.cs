using DeadworksManaged.Api;

namespace ItemRotationPlugin;

public class ItemRotationConfig : IConfig
{
	public int SwapIntervalSeconds { get; set; } = 10;
	public string SelectionMode { get; set; } = "sequential"; // "sequential" or "random"
	public bool AllowDuplicateSets { get; set; } = true;
	public bool ShowRotationAnnouncement { get; set; } = true;
	public string AnnouncementTitle { get; set; } = "Items Changed!";
	public string AnnouncementDescription { get; set; } = "<item_set_name>";
	public bool PlayRotationSound { get; set; } = true;
	public string RotationSound { get; set; } = "Mystical.Piano.AOE.Warning";

	public List<ItemSet> ItemSets { get; set; } = new()
	{
		new ItemSet { Name = "Speed Demons", Items = new List<string> { "upgrade_sprint_booster", "upgrade_kinetic_sash", "upgrade_improved_stamina" } },
		new ItemSet { Name = "Cardio Kings", Items = new List<string> { "upgrade_fleetfoot_boots", "upgrade_cardio_calibrator", "upgrade_improved_stamina" } },
		new ItemSet { Name = "Warp Zone", Items = new List<string> { "upgrade_warp_stone", "upgrade_sprint_booster", "upgrade_superior_stamina" } },
		new ItemSet { Name = "Rocket Riders", Items = new List<string> { "upgrade_rocket_booster", "upgrade_magic_carpet", "upgrade_superior_stamina" } },
		new ItemSet { Name = "Bubble Fleet", Items = new List<string> { "upgrade_self_bubble", "upgrade_fleetfoot_boots", "upgrade_arcane_surge" } },
		new ItemSet { Name = "Bullet Storm", Items = new List<string> { "upgrade_blitz_bullets", "upgrade_rechargingbullets", "upgrade_kinetic_sash" } }
	};

	public void Validate()
	{
		if (SwapIntervalSeconds < 1) SwapIntervalSeconds = 1;
	}
}

public class ItemSet
{
	public string Name { get; set; } = "";
	public List<string> Items { get; set; } = new();
}

public class ItemRotationPlugin : DeadworksPluginBase
{
	public override string Name => "Item Rotation";

	#region Fields

	[PluginConfig]
	public ItemRotationConfig Config { get; set; } = new();

	private bool _running;
	private IHandle? _swapTimer;
	private readonly Dictionary<int, int> _playerSetIndex = new(); // slot -> current set index
	private readonly HashSet<int> _activePlayers = new();
	private readonly Random _rng = new();

	#endregion

	#region Plugin Lifecycle

	public override void OnLoad(bool isReload)
	{
		Console.WriteLine($"[ItemRotation] {(isReload ? "Reloaded" : "Loaded")} with {Config.ItemSets.Count} item sets");
	}

	public override void OnUnload()
	{
		StopGame();
		Console.WriteLine("[ItemRotation] Unloaded!");
	}

	#endregion

	#region Commands

	[Command("ir_sets", Description = "Show the configured item sets and rotation options")]
	public void CmdSets(CCitadelPlayerController? caller)
	{
		Reply(caller, $"[ItemRotation] Swap Interval: {Config.SwapIntervalSeconds}s (time between rotations)");
		Reply(caller, $"[ItemRotation] Selection Mode: {Config.SelectionMode} (sequential = 1->2->3, random = random each rotation)");
		Reply(caller, $"[ItemRotation] Allow Duplicates: {Config.AllowDuplicateSets} (can multiple players share a set)");

		if (Config.ItemSets.Count == 0)
		{
			Reply(caller, "[ItemRotation] No item sets configured.");
			return;
		}

		for (int i = 0; i < Config.ItemSets.Count; i++)
		{
			var set = Config.ItemSets[i];
			var label = string.IsNullOrEmpty(set.Name) ? $"Set {i + 1}" : set.Name;
			Reply(caller, $"{label}: {string.Join(", ", set.Items)}");
		}
	}

	[Command("ir_start", Description = "Start the item-rotation game", Permission = "itemrotation.manage")]
	public void CmdStart(CCitadelPlayerController? caller)
	{
		if (_running)
			throw new CommandException("[ItemRotation] Game is already running! Use /ir_reset to stop.");

		var players = GetConnectedPlayers();
		if (players.Count == 0)
			throw new CommandException("[ItemRotation] No players found.");

		if (Config.ItemSets.Count == 0)
			throw new CommandException("[ItemRotation] No item sets configured.");

		if (!Config.AllowDuplicateSets && players.Count > Config.ItemSets.Count)
			throw new CommandException(
				$"[ItemRotation] Not enough item sets ({Config.ItemSets.Count}) for {players.Count} players. " +
				"Enable allowDuplicateSets in config or add more sets.");

		_running = true;
		_activePlayers.Clear();
		_playerSetIndex.Clear();

		foreach (var slot in players)
		{
			_activePlayers.Add(slot);
		}

		AssignInitialSets(players);
		ApplyAllPlayerSets(null);

		_swapTimer = Timer.Every((Config.SwapIntervalSeconds * 1000).Milliseconds(), OnSwapTick);

		SendChatAll($"[ItemRotation] Game started! Sets rotate every {Config.SwapIntervalSeconds}s. Mode: {Config.SelectionMode}.");
	}

	[Command("ir_swap", Description = "Force an immediate item-set rotation", Permission = "itemrotation.manage")]
	public void CmdSwap(CCitadelPlayerController? caller)
	{
		if (!_running)
			throw new CommandException("[ItemRotation] No game is running.");

		OnSwapTick();
		Reply(caller, "[ItemRotation] Forced a swap.");
	}

	[Command("ir_reset", Description = "Stop the item-rotation game and clear all items", Permission = "itemrotation.manage")]
	public void CmdReset(CCitadelPlayerController? caller)
	{
		if (!_running)
			throw new CommandException("[ItemRotation] No game is running.");

		StopGame();
		ClearAllPlayerItems();
		SendChatAll("[ItemRotation] Game stopped. All items cleared.");
	}

	private static void Reply(CCitadelPlayerController? to, string text)
	{
		if (to != null) SendChat(to.EntityIndex - 1, text);
		else Console.WriteLine(text);
	}

	#endregion

	#region Game Logic

	private void StopGame()
	{
		_running = false;
		_swapTimer?.Cancel();
		_swapTimer = null;
	}

	private void OnSwapTick()
	{
		if (!_running) return;

		// Capture old assignments before rotating
		var previousSets = new Dictionary<int, int>(_playerSetIndex);

		RotateSets();
		ApplyAllPlayerSets(previousSets);
	}

	private void AssignInitialSets(List<int> players)
	{
		if (Config.SelectionMode == "random")
		{
			AssignRandomSets(players);
		}
		else
		{
			for (int i = 0; i < players.Count; i++)
			{
				int setIndex = Config.AllowDuplicateSets
					? i % Config.ItemSets.Count
					: i;
				_playerSetIndex[players[i]] = setIndex;
			}
		}
	}

	private void RotateSets()
	{
		var players = _activePlayers.Where(s => GetPawn(s) != null).ToList();
		if (players.Count == 0) return;

		if (Config.SelectionMode == "random")
		{
			AssignRandomSets(players);
		}
		else
		{
			foreach (var slot in players)
			{
				if (_playerSetIndex.TryGetValue(slot, out int current))
					_playerSetIndex[slot] = (current + 1) % Config.ItemSets.Count;
			}
		}
	}

	private void AssignRandomSets(List<int> players)
	{
		if (Config.AllowDuplicateSets)
		{
			foreach (var slot in players)
				_playerSetIndex[slot] = _rng.Next(Config.ItemSets.Count);
		}
		else
		{
			var available = Enumerable.Range(0, Config.ItemSets.Count).ToList();
			Shuffle(available);
			for (int i = 0; i < players.Count; i++)
				_playerSetIndex[players[i]] = available[i];
		}
	}

	private void Shuffle<T>(List<T> list)
	{
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
	}

	/// <summary>
	/// Removes old items and gives new items for all active players.
	/// If previousSets is null (initial assignment), no removal is done.
	/// </summary>
	private void ApplyAllPlayerSets(Dictionary<int, int>? previousSets)
	{
		bool isRotation = previousSets != null;

		foreach (var slot in _activePlayers)
		{
			if (!_playerSetIndex.TryGetValue(slot, out int setIndex)) continue;
			var pawn = GetPawn(slot);
			if (pawn == null) continue;

			// Remove old items via pawn API
			if (previousSets != null && previousSets.TryGetValue(slot, out int oldIndex))
			{
				foreach (var item in Config.ItemSets[oldIndex].Items)
					pawn.RemoveItem(item);
			}

			// Give new items via pawn API
			var itemSet = Config.ItemSets[setIndex];
			foreach (var item in itemSet.Items)
				pawn.AddItem(item);

			var setLabel = string.IsNullOrEmpty(itemSet.Name) ? $"Set {setIndex + 1}" : itemSet.Name;
			SendChat(slot, $"[ItemRotation] You received {setLabel}");

			if (isRotation)
			{
				if (Config.ShowRotationAnnouncement)
				{
					var title = Config.AnnouncementTitle.Replace("<item_set_name>", setLabel);
					var desc = Config.AnnouncementDescription.Replace("<item_set_name>", setLabel);
					var msg = new CCitadelUserMsg_HudGameAnnouncement
					{
						TitleLocstring = title,
						DescriptionLocstring = desc
					};
					NetMessages.Send(msg, RecipientFilter.Single(slot));
				}

				if (Config.PlayRotationSound && !string.IsNullOrEmpty(Config.RotationSound))
				{
					pawn.EmitSound(Config.RotationSound);
				}
			}
		}
	}

	private void ClearAllPlayerItems()
	{
		foreach (var slot in _activePlayers)
		{
			if (!_playerSetIndex.TryGetValue(slot, out int setIndex)) continue;
			var pawn = GetPawn(slot);
			if (pawn == null) continue;

			foreach (var item in Config.ItemSets[setIndex].Items)
				pawn.RemoveItem(item);
		}

		_activePlayers.Clear();
		_playerSetIndex.Clear();
	}

	#endregion

	#region Helpers

	private static List<int> GetConnectedPlayers()
	{
		var players = new List<int>();
		for (int i = 0; i < Players.MaxSlot; i++)
		{
			var ctrl = Players.FromSlot(i);
			if (ctrl?.GetHeroPawn() != null)
				players.Add(i);
		}
		return players;
	}

	private static CCitadelPlayerPawn? GetPawn(int slot)
	{
		return Players.FromSlot(slot)?.GetHeroPawn();
	}

	private static void SendChat(int slot, string text)
	{
		var msg = new CCitadelUserMsg_ChatMsg
		{
			PlayerSlot = -1,
			Text = text,
			AllChat = true
		};
		NetMessages.Send(msg, RecipientFilter.Single(slot));
	}

	private static void SendChatAll(string text)
	{
		var msg = new CCitadelUserMsg_ChatMsg
		{
			PlayerSlot = -1,
			Text = text,
			AllChat = true
		};
		NetMessages.Send(msg, RecipientFilter.All);
	}

	#endregion

	#region Events

	public override void OnClientDisconnect(ClientDisconnectedEvent args)
	{
		_activePlayers.Remove(args.Slot);
		_playerSetIndex.Remove(args.Slot);
	}

	#endregion
}
