using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace DeathmatchPlugin;

public class SpawnPoint {
	[JsonPropertyName("pos")]
	public float[] Pos { get; set; } = [0, 0, 0];

	[JsonPropertyName("ang")]
	public float[] Ang { get; set; } = [0, 0, 0];
}

public class HeroItemSet {
	[JsonPropertyName("hero_id")]
	public int HeroId { get; set; }

	[JsonPropertyName("hero_name")]
	public string HeroName { get; set; } = "";

	[JsonPropertyName("items")]
	public List<string> Items { get; set; } = new();

	[JsonPropertyName("gold_remaining")]
	public int GoldRemaining { get; set; }
}

public class ItemSetConfig {
	[JsonPropertyName("hero_item_sets")]
	public Dictionary<string, HeroItemSet> HeroItemSets { get; set; } = new();
}

public class DeathmatchConfig {
	public Dictionary<string, Dictionary<string, SpawnPoint[]>> SpawnPoints { get; set; } = new();
	public int HeroSwapIntervalSeconds { get; set; } = 60;
	public Dictionary<string, HeroItemSet> HeroItemSets { get; set; } = new();
}

public record SwapState(List<string> Items, Dictionary<EAbilitySlot, (float Start, float End)> Cooldowns, int Gold);

public class DeathmatchPlugin : DeadworksPluginBase {
	public override string Name => "Deathmatch";

	[PluginConfig]
	public DeathmatchConfig Config { get; set; } = new();

	private Heroes[] _availableHeroes = [];
	private Heroes _team2Hero;
	private Heroes _team3Hero;
	private IHandle? _swapTimer;
	private readonly EntityData<SwapState> _pendingSwap = new();
	private readonly Queue<Heroes> _team2History = new();
	private readonly Queue<Heroes> _team3History = new();
	private int _team2Kills;
	private int _team3Kills;
	private readonly Dictionary<int, int> _playerKills = new(); // entity index -> kills this round

	public override void OnLoad(bool isReload) {
		LoadBundledItemSets();
		Console.WriteLine(isReload ? "Deathmatch reloaded!" : "Deathmatch loaded!");
	}

	private void LoadBundledItemSets() {
		// Load embedded item sets as defaults - config file entries take priority
		var asm = Assembly.GetExecutingAssembly();
		var resourceName = asm.GetManifestResourceNames()
			.FirstOrDefault(n => n.EndsWith("HeroItemSets.jsonc"));
		if (resourceName == null) return;

		using var stream = asm.GetManifestResourceStream(resourceName);
		if (stream == null) return;

		var options = new JsonSerializerOptions {
			ReadCommentHandling = JsonCommentHandling.Skip,
			PropertyNameCaseInsensitive = true
		};
		var bundled = JsonSerializer.Deserialize<ItemSetConfig>(stream, options);
		if (bundled == null) return;

		int count = 0;
		foreach (var (key, itemSet) in bundled.HeroItemSets) {
			// Only add if not already defined in the config file
			if (!Config.HeroItemSets.ContainsKey(key)) {
				Config.HeroItemSets[key] = itemSet;
				count++;
			}
		}
		Console.WriteLine($"[DM] Loaded {count} bundled item sets ({bundled.HeroItemSets.Count} total, {Config.HeroItemSets.Count - count} from config)");
	}

	public override void OnConfigReloaded() => RestartSwapTimer();

	private void RestartSwapTimer() {
		_swapTimer?.Cancel();
		var interval = Config.HeroSwapIntervalSeconds;
		if (interval > 0) {
			_swapTimer = Timer.Every(interval.Seconds(), SwapHeroes);
			Console.WriteLine($"[DM] Hero swap every {interval}s");
		} else {
			Console.WriteLine("[DM] Hero swap disabled");
		}
	}

    public override void OnStartupServer()
    {
		ConVar.Find("citadel_active_lane")?.SetInt(4);
		ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetInt(5);
		ConVar.Find("citadel_allow_purchasing_anywhere")?.SetInt(1);
		ConVar.Find("citadel_item_sell_price_ratio")?.SetFloat(1.0f);
		ConVar.Find("citadel_voice_all_talk")?.SetInt(1);
		ConVar.Find("sv_alltalk")?.SetInt(1);
		ConVar.Find("citadel_player_starting_gold")?.SetInt(0);
		ConVar.Find("citadel_trooper_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_npc_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_start_players_on_zipline")?.SetInt(0);
		ConVar.Find("citadel_allow_duplicate_heroes")?.SetInt(1);

		Server.ExecuteCommand("sv_cheats 1");
		Server.ExecuteCommand("citadel_unlock_flex_slots");
		Server.ExecuteCommand("sv_cheats 0");

		Timer.NextTick(() => {
			var (hero1, hero2) = PickTwoRandomHeroes();
			_team2Hero = hero1;
			_team3Hero = hero2;
			Console.WriteLine($"[DM] Team 2: {_team2Hero.ToHeroName()}, Team 3: {_team3Hero.ToHeroName()}");
		});

		RestartSwapTimer();

		Timer.Once(3.Seconds(), () => {
			var sign1 = CPointWorldText.Create("DEADWORKS.net", new Vector3(0, 256, 542), fontSize: 100f, r: 127, g: 0, b: 127, fontName: "Reaver");
			sign1?.Teleport(angles: new Vector3(185f, 0f, 270f));
			sign1?.WorldUnitsPerPx = 0.50f;
			sign1?.JustifyHorizontal = HorizontalJustify.Center;
			sign1?.JustifyVertical = VerticalJustify.Center;
			var sign2 = CPointWorldText.Create("DEADWORKS.net", new Vector3(0, -256, 542), fontSize: 100f, r: 127, g: 0, b: 127, fontName: "Reaver");
			sign2?.Teleport(angles: new Vector3(185f, 180f, 270f));
			sign2?.WorldUnitsPerPx = 0.50f;
			sign2?.JustifyHorizontal = HorizontalJustify.Center;
			sign2?.JustifyVertical = VerticalJustify.Center;

			var rulesText = "Every minute, each team\nis assigned a random hero!\nThe team with the most kills wins!";

			var rules1 = CPointWorldText.Create(rulesText, new Vector3(0, -966, 443), fontSize: 90f, r: 200, g: 200, b: 200, fontName: "Reaver");
			rules1?.Teleport(angles: new Vector3(180f, 180f, 270f));
			rules1?.WorldUnitsPerPx = 0.10f;
			rules1?.JustifyHorizontal = HorizontalJustify.Center;
			rules1?.JustifyVertical = VerticalJustify.Center;

			var rules2 = CPointWorldText.Create(rulesText, new Vector3(0, 978, 443), fontSize: 90f, r: 200, g: 200, b: 200, fontName: "Reaver");
			rules2?.Teleport(angles: new Vector3(180f, 0f, 270f));
			rules2?.WorldUnitsPerPx = 0.10f;
			rules2?.JustifyHorizontal = HorizontalJustify.Center;
			rules2?.JustifyVertical = VerticalJustify.Center;

			var discord1 = CPointWorldText.Create("deadworks.net/discord", new Vector3(-513.4f, -800f, 452.8f), fontSize: 90f, r: 200, g: 50, b: 50, fontName: "Radiance");
			discord1?.Teleport(angles: new Vector3(180f, 180f, 270f));
			discord1?.WorldUnitsPerPx = 0.20f;
			discord1?.JustifyHorizontal = HorizontalJustify.Center;
			discord1?.JustifyVertical = VerticalJustify.Center;

			var discord2 = CPointWorldText.Create("deadworks.net/discord", new Vector3(513.4f, 800f, 452.8f), fontSize: 90f, r: 200, g: 50, b: 50, fontName: "Radiance");
			discord2?.Teleport(angles: new Vector3(180f, 0f, 270f));
			discord2?.WorldUnitsPerPx = 0.20f;
			discord2?.JustifyHorizontal = HorizontalJustify.Center;
			discord2?.JustifyVertical = VerticalJustify.Center;
		});
	}

	private Heroes PickRandomHero(Queue<Heroes> history) {
		_availableHeroes = Enum.GetValues<Heroes>()
			.Where(h => h.GetHeroData()?.AvailableInGame == true)
			.ToArray();

		var candidates = _availableHeroes.Where(h => !history.Contains(h)).ToArray();
		if (candidates.Length == 0)
			candidates = _availableHeroes;

		var pick = candidates[Random.Shared.Next(candidates.Length)];
		history.Enqueue(pick);
		if (history.Count > 10)
			history.Dequeue();
		return pick;
	}

	private (Heroes, Heroes) PickTwoRandomHeroes() {
		var first = PickRandomHero(_team2History);
		Heroes second;
		do {
			second = PickRandomHero(_team3History);
		} while (second == first);
		return (first, second);
	}

	private void SwapHeroes() {
		if (_team2Kills > 0 || _team3Kills > 0)
			AnnounceRoundResults();

		_team2Kills = 0;
		_team3Kills = 0;
		_playerKills.Clear();

		(_team2Hero, _team3Hero) = PickTwoRandomHeroes();
		Console.WriteLine($"[DM] New heroes! Team 2: {_team2Hero.ToHeroName()}, Team 3: {_team3Hero.ToHeroName()}");

		foreach (var controller in Players.GetAll()) {
			var pawn = controller.GetHeroPawn()?.As<CCitadelPlayerPawn>();
			if (pawn == null) continue;

			var newHero = pawn.TeamNum == 2 ? _team2Hero : _team3Hero;
			var newHeroData = newHero.GetHeroData();
			int newHeroId = newHeroData?.HeroID ?? 0;

			// Look up the item set for the new hero
			Config.HeroItemSets.TryGetValue(newHeroId.ToString(), out var itemSet);

			int gold = itemSet?.GoldRemaining ?? 50_000;
			_pendingSwap[controller] = new SwapState(itemSet?.Items ?? new(), new(), gold);
			controller.SelectHero(newHero);
		}

		// Hero loading is async — restore after it completes.
		Timer.Once(1.Seconds(), () => {
			foreach (var controller in Players.GetAll()) {
				if (!_pendingSwap.TryGet(controller, out var state)) {
					continue;
				}

				var p = controller.GetHeroPawn()?.As<CCitadelPlayerPawn>();
				if (p == null) { Console.WriteLine("[DM] Pawn is null in timer"); continue; }

				// ResetHero triggers EStartingAmount — _pendingSwap must still
				// exist so OnModifyCurrency restores saved gold instead of 15000.
				p.ResetHero();
				_pendingSwap.Remove(controller);

				p.Heal(p.GetMaxHealth());
				MaxUpgradeSignatureAbilities(p);

				foreach (var item in state.Items) {
					var result = p.AddItem(item);
					Console.WriteLine($"[DM]   AddItem({item}) => {(result != null ? result.ToString() : "NULL")}");
				}

				Console.WriteLine($"[DM] Restored {state.Items.Count} items, {state.Gold}g for {controller.PlayerName}");
			}
		});
	}

	[Command("pos", Description = "Print your current position and camera angles as JSON")]
	public void CmdPos(CCitadelPlayerController caller) {
		var pawn = caller.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) return;
		var pos = pawn.Position;
		var ang = pawn.CameraAngles;
		Console.WriteLine($@"{{ ""pos"": [{pos.X}, {pos.Y}, {pos.Z}], ""ang"": [{ang.X}, {ang.Y}, {ang.Z}] }}");
	}

	[GameEventHandler("player_death")]
	public HookResult OnPlayerDeath(PlayerDeathEvent args) {
		var attackerPawn = args.AttackerPawn;
		if (attackerPawn == null) return HookResult.Continue;

		// Don't count suicides
		var victimPawn = args.UseridPawn;
		if (victimPawn != null && attackerPawn.EntityIndex == victimPawn.EntityIndex)
			return HookResult.Continue;

		if (attackerPawn.TeamNum == 2) _team2Kills++;
		else if (attackerPawn.TeamNum == 3) _team3Kills++;

		var controller = args.AttackerController;
		if (controller != null) {
			var idx = controller.EntityIndex;
			_playerKills[idx] = _playerKills.GetValueOrDefault(idx) + 1;
		}

		return HookResult.Continue;
	}

	private void AnnounceRoundResults() {
		var winnerHero = _team2Kills >= _team3Kills ? _team2Hero : _team3Hero;
		var winnerKills = Math.Max(_team2Kills, _team3Kills);
		var loserKills = Math.Min(_team2Kills, _team3Kills);

		// Find MVP - player with the most kills this round
		int mvpIdx = -1, mvpKills = 0;
		foreach (var (idx, kills) in _playerKills) {
			if (kills > mvpKills) {
				mvpKills = kills;
				mvpIdx = idx;
			}
		}

		var mvpName = "";
		if (mvpIdx >= 0) {
			var mvpController = CBaseEntity.FromIndex<CCitadelPlayerController>(mvpIdx);
			if (mvpController != null)
				mvpName = mvpController.PlayerName;
		}

		var isTie = _team2Kills == _team3Kills;
		var title = isTie ? "Draw!" : $"{winnerHero.ToDisplayName()} wins!";
		var desc = string.IsNullOrEmpty(mvpName)
			? $"{_team2Kills} - {_team3Kills}"
			: $"{_team2Kills} - {_team3Kills}  |  MVP: {mvpName} ({mvpKills} kills)";

		var msg = new CCitadelUserMsg_HudGameAnnouncement {
			TitleLocstring = title,
			DescriptionLocstring = desc
		};
		NetMessages.Send(msg, RecipientFilter.All);
	}

	[GameEventHandler("player_respawned")]
	public HookResult OnPlayerRespawned(PlayerRespawnedEvent args) {
		var pawn = args.Userid;
		if (pawn == null) return HookResult.Continue;

		var teamKey = pawn.TeamNum.ToString();
		if (Config.SpawnPoints.TryGetValue(Server.MapName, out var teams)
			&& teams.TryGetValue(teamKey, out var spawns)
			&& spawns.Length > 0) {
			var spawn = spawns[Random.Shared.Next(spawns.Length)];
			var pos = spawn.Pos.Length >= 3 ? new Vector3(spawn.Pos[0], spawn.Pos[1], spawn.Pos[2]) : (Vector3?)null;
			var ang = spawn.Ang.Length >= 3 ? new Vector3(spawn.Ang[0], spawn.Ang[1], spawn.Ang[2]) : (Vector3?)null;
			pawn.Teleport(position: pos, angles: ang);
		}

		MaxUpgradeSignatureAbilities(pawn.As<CCitadelPlayerPawn>());
		return HookResult.Continue;
	}

	public override HookResult OnClientConCommand(ClientConCommandEvent e) {
		if (e.Command == "selecthero") {
			return HookResult.Stop;
		}
		if (e.Command == "changeteam" || e.Command == "jointeam") {
			return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	[GameEventHandler("player_hero_changed")]
	public HookResult OnPlayerHeroChanged(PlayerHeroChangedEvent args) {
		var pawn = args.Userid?.As<CCitadelPlayerPawn>();
		if (pawn == null) return HookResult.Continue;

		// Swap in progress - skip, the timer in SwapHeroes handles restoration.
		var controller = pawn.Controller;
		if (controller != null && _pendingSwap.Has(controller))
			return HookResult.Continue;

		pawn.ResetHero();
		pawn.Heal(pawn.GetMaxHealth());
		MaxUpgradeSignatureAbilities(pawn);
		RestoreItemSet(pawn);
		return HookResult.Continue;
	}

	public override void OnEntitySpawned(EntitySpawnedEvent e) {
		if (e.Entity.DesignerName == "npc_trooper_boss")
			e.Entity.Remove();
	}

	public override void OnClientFullConnect(ClientFullConnectEvent args) {
		var controller = args.Controller;
		if (controller == null) return;

		int team2 = 0, team3 = 0;
		foreach (var p in Players.GetAll()) {
			if (p.EntityIndex == controller.EntityIndex) continue;
			var pawn = p.GetHeroPawn();
			if (pawn == null) continue;
			if (pawn.TeamNum == 2) team2++;
			else if (pawn.TeamNum == 3) team3++;
		}
		int team = team2 < team3 ? 2 : team3 < team2 ? 3 : Random.Shared.Next(2) == 0 ? 2 : 3;
		controller.ChangeTeam(team);

		var hero = team == 2 ? _team2Hero : _team3Hero;
		Console.WriteLine($"[DM] Slot {args.Slot} -> team {team}, hero {hero.ToHeroName()}");
		controller.SelectHero(hero);
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) {
		var controller = args.Controller;
		if (controller == null) return;

		controller.GetHeroPawn()?.Remove();
		controller.Remove();
	}

	public override HookResult OnTakeDamage(TakeDamageEvent args) {
		if (args.Entity.DesignerName is "npc_boss_tier3" or "npc_boss_tier2" or "npc_trooper_boss")
			return HookResult.Stop;
		return HookResult.Continue;
	}

	public override HookResult OnModifyCurrency(ModifyCurrencyEvent args) {
		if (args.CurrencyType == ECurrencyType.EGold) {
			if (args.Source == ECurrencySource.EStartingAmount) {
				var controller = args.Pawn.Controller;
				int gold = 50_000;
				if (controller != null && _pendingSwap.TryGet(controller, out var state))
					gold = state.Gold;
				else if (controller != null) {
					int heroId = controller.PlayerDataGlobal.HeroID;
					if (Config.HeroItemSets.TryGetValue(heroId.ToString(), out var itemSet))
						gold = itemSet.GoldRemaining;
				}

				// Set level + gold atomically via schema writes to avoid the
				// step-by-step level-up in ModifyCurrency that triggers
				// client-side "LevelChanged" callbacks and boon UI per level.
				args.Pawn.Level = 36;
				args.Pawn.SetCurrency(ECurrencyType.EGold, gold);

				// Run a zero-amount ModifyCurrency to trigger internal stat
				// recalculation (health/damage scaling) without changing gold.
				args.Pawn.ModifyCurrency(ECurrencyType.EGold, 0, ECurrencySource.ECheats, silent: true);
				return HookResult.Stop;
			}
			if (args.Source != ECurrencySource.ECheats && args.Source != ECurrencySource.EItemPurchase && args.Source != ECurrencySource.EItemSale)
				return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	public override void OnUnload() {
		_swapTimer?.Cancel();
		Console.WriteLine("Deathmatch unloaded!");
	}

	public override void OnPrecacheResources() {
	}

	private void RestoreItemSet(CCitadelPlayerPawn? pawn) {
		if (pawn == null) return;
		var controller = pawn.Controller;
		if (controller == null) return;

		int heroId = controller.PlayerDataGlobal.HeroID;
		if (!Config.HeroItemSets.TryGetValue(heroId.ToString(), out var itemSet))
			return;

		foreach (var item in itemSet.Items) {
			pawn.AddItem(item);
		}
		Console.WriteLine($"[DM] Restored {itemSet.Items.Count} items for {itemSet.HeroName}");
	}

	private static void MaxUpgradeSignatureAbilities(CCitadelPlayerPawn? pawn) {
		if (pawn == null) return;
		foreach (var ability in pawn.AbilityComponent.Abilities) {
			if (ability.AbilitySlot < EAbilitySlot.Signature1 || ability.AbilitySlot > EAbilitySlot.Signature4) continue;
			ability.UpgradeBits = ability.UpgradeBits | 0b11111;
		}
	}

	[Command("trace", Description = "Raycast forward from the caller and print trace info")]
	public void CmdTrace(CCitadelPlayerController caller) {
		var pawn = caller.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) {
			Console.WriteLine("No pawn found for trace");
			return;
		}

		var eye = pawn.EyePosition;
		var eyeAngles = pawn.EyeAngles;
		var camAngles = pawn.CameraAngles;
		var viewAngles = pawn.ViewAngles;

		Console.WriteLine($"[trace] EyeAngles=({eyeAngles.X:F4},{eyeAngles.Y:F4},{eyeAngles.Z:F4}) [networked, quantized 11-bit]");
		Console.WriteLine($"[trace] CamAngles=({camAngles.X:F4},{camAngles.Y:F4},{camAngles.Z:F4}) [m_angClientCamera]");
		Console.WriteLine($"[trace] ViewAngles=({viewAngles.X:F4},{viewAngles.Y:F4},{viewAngles.Z:F4}) [v_angle, raw from CUserCmd]");
		Console.WriteLine($"[trace] EyePos=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) AbsOrigin=({pawn.Position.X:F1},{pawn.Position.Y:F1},{pawn.Position.Z:F1})");

		var angles = viewAngles;
		float pitch = angles.X * MathF.PI / 180f;
		float yaw = angles.Y * MathF.PI / 180f;
		var forward = new System.Numerics.Vector3(
			MathF.Cos(pitch) * MathF.Cos(yaw),
			MathF.Cos(pitch) * MathF.Sin(yaw),
			-MathF.Sin(pitch));

		var end = eye + forward * 10000f;

		Console.WriteLine($"[trace] eye=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) end=({end.X:F1},{end.Y:F1},{end.Z:F1}) pawnIdx={pawn.EntityIndex}");

		unsafe {
			var trace = CGameTrace.Create();
			var ray = new Ray_t { Type = RayType_t.Line };
			var filter = new CTraceFilter(true) {
				IterateEntities = true,
				QueryShapeAttributes = new RnQueryShapeAttr_t {
					ObjectSetMask = RnQueryObjectSet.All,
					InteractsWith = InteractionLayer.Solid,
					InteractsExclude = InteractionLayer.None,
					InteractsAs = InteractionLayer.None,
					CollisionGroup = CollisionGroup.CitadelBullet,
					HitSolid = true,
				}
			};
			filter.QueryShapeAttributes.EntityIdsToIgnore[0] = (uint)pawn.EntityIndex;

			Console.WriteLine($"[trace] sizeof Ray_t={sizeof(Ray_t)} CTraceFilter={sizeof(CTraceFilter)} CGameTrace={sizeof(CGameTrace)}");
			Console.WriteLine($"[trace] filter EntityIdsToIgnore[0]={filter.QueryShapeAttributes.EntityIdsToIgnore[0]}");

			Trace.TraceShape(eye, end, ray, filter, ref trace);

			Console.WriteLine($"[trace] frac={trace.Fraction:F6} startInSolid={trace.StartInSolid} pEntity=0x{trace.pEntity:X}");
			Console.WriteLine($"[trace] hitPoint=({trace.HitPoint.X:F1},{trace.HitPoint.Y:F1},{trace.HitPoint.Z:F1})");
			Console.WriteLine($"[trace] startPos=({trace.StartPos.X:F1},{trace.StartPos.Y:F1},{trace.StartPos.Z:F1})");
			Console.WriteLine($"[trace] endPos=({trace.EndPos.X:F1},{trace.EndPos.Y:F1},{trace.EndPos.Z:F1})");

			var hitPos = eye + (end - eye) * trace.Fraction;
			var text = trace.DidHit
				? $"Trace hit at ({hitPos.X:F1}, {hitPos.Y:F1}, {hitPos.Z:F1}) frac={trace.Fraction:F4}"
				: "Trace: no hit";

			Console.WriteLine(text);
			Chat.PrintToChat(caller, text);
		}
	}
}
