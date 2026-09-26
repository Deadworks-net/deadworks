using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace TagPlugin;

public class SpawnPoint {
	[JsonPropertyName("pos")]
	public float[] Pos { get; set; } = [0, 0, 0];

	[JsonPropertyName("ang")]
	public float[] Ang { get; set; } = [0, 0, 0];
}

public class TagConfig {
	public Dictionary<string, List<SpawnPoint>> SpawnPoints { get; set; } = new();
}

/// <summary>
/// Tag / Hide-and-Seek game mode plugin.
///
/// Team 2 = Seekers (melee only, must tag hiders)
/// Team 3 = Hiders  (cannot deal damage, silent footsteps)
///
/// When a Seeker melees a Hider, the Hider becomes a Seeker and the Seeker becomes a Hider.
/// No freeze time, respawns enabled, no map objectives, no item buying, no abilities.
/// </summary>
public class TagPlugin : DeadworksPluginBase {
	public override string Name => "Tag";

	private const int SeekerTeam = 2;
	private const int HiderTeam = 3;
	private const int RespawnDelaySeconds = 3;

	// Designer names of map entities to remove (objectives, NPCs, etc.)
	private static readonly HashSet<string> EntitiesToRemove = new() {
		"npc_boss_tier3",
		"npc_boss_tier2",
        "npc_boss_tier1",
        "npc_barrack_boss",
        "npc_base_defense_sentry",
        "npc_trooper_boss",
		"npc_trooper",
	};

	[PluginConfig]
	public TagConfig Config { get; set; } = new();

	// Track player names for chat messages
	private readonly Dictionary<int, string> _playerNames = new();
	private bool _rebroadcasting;

	// Score timer: hiders earn 1 gold per second
	private IHandle? _scoreTimer;

	// Track recently used spawn indices to avoid reuse
	private readonly List<int> _recentSpawns = new();


	public override void OnLoad(bool isReload) {
		Console.WriteLine(isReload ? "[Tag] Reloaded!" : "[Tag] Loaded!");
	}

	public override void OnUnload() {
		Console.WriteLine("[Tag] Unloaded!");
	}

	public override void OnStartupServer() {
		EnsureConVars();

		// Score timer: hiders earn 1 gold per second
		_scoreTimer?.Cancel();
		_scoreTimer = Timer.Every(1.Seconds(), () => {
			foreach (var pawn in Players.GetAllPawns()) {
				if (pawn.TeamNum == HiderTeam && pawn.Health > 0)
					pawn.ModifyCurrency(ECurrencyType.EGold, 1, ECurrencySource.ECheats);
			}
		});
	}

	private void EnsureConVars() {
		ConVar.Find("citadel_trooper_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_npc_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_start_players_on_zipline")?.SetInt(0);
		ConVar.Find("citadel_allow_duplicate_heroes")?.SetInt(1);
		ConVar.Find("citadel_voice_all_talk")?.SetInt(1);
		ConVar.Find("citadel_player_starting_gold")?.SetInt(0);
		ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetInt(RespawnDelaySeconds);
		ConVar.Find("citadel_active_lane")?.SetInt(255);
		ConVar.Find("citadel_rapid_stamina_regen")?.SetInt(1);
	}

	// --- Team Assignment ---

	public override void OnClientPutInServer(ClientPutInServerEvent args) {
		_playerNames[args.Slot] = args.Name;
	}

	public override void OnClientFullConnect(ClientFullConnectEvent args) {
		EnsureConVars();

		var controller = args.Controller;
		if (controller == null) return;

		// Assign to team with fewer players, defaulting to hider
		int seekers = 0, hiders = 0;
		foreach (var p in Players.GetAll()) {
			if (p.EntityIndex == controller.EntityIndex) continue;
			var pawn = p.GetHeroPawn();
			if (pawn == null) continue;
			if (pawn.TeamNum == SeekerTeam) seekers++;
			else if (pawn.TeamNum == HiderTeam) hiders++;
		}

		// Start most players as hiders; need at least 1 seeker
		int team;
		if (seekers == 0 && hiders == 0)
			team = SeekerTeam; // First player is seeker
		else if (seekers == 0)
			team = SeekerTeam; // Need at least one seeker
		else
			team = HiderTeam;

		Console.WriteLine($"[Tag] Assigning slot {args.Slot} to {(team == SeekerTeam ? "Seeker" : "Hider")}");
		controller.ChangeTeam(team);

		// Seekers = Warden, Hiders = Astro (Holiday)
		controller.SelectHero(team == SeekerTeam ? Heroes.Warden : Heroes.Astro);
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) {
		_playerNames.Remove(args.Slot);

		var controller = args.Controller;
		if (controller == null) return;
		controller.GetHeroPawn()?.Remove();
	}

	// --- Block Normal Team Choosing ---

	public override HookResult OnClientConCommand(ClientConCommandEvent e) {
		// Block hero selection entirely - heroes are assigned by role
		if (e.Command == "selecthero") {
			SendChatToPlayer(e.Controller!, "[Tag] Heroes are assigned by role (Warden/Holiday).");
			return HookResult.Stop;
		}
		// Block team change commands
		if (e.Command == "changeteam" || e.Command == "jointeam") {
			SendChatToPlayer(e.Controller!, "[Tag] Teams are managed by the Tag plugin.");
			return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	// --- Spawning ---

	[GameEventHandler("player_respawned")]
	public HookResult OnPlayerRespawned(PlayerRespawnedEvent args) {
		var pawn = args.Userid?.As<CCitadelPlayerPawn>();
		if (pawn == null) return HookResult.Continue;

		var mapName = Server.MapName;
		if (!Config.SpawnPoints.TryGetValue(mapName, out var spawns) || spawns.Count == 0)
			return HookResult.Continue;

		// Pick a random spawn, avoiding the last 3 used
		int index;
		var available = Enumerable.Range(0, spawns.Count)
			.Where(i => !_recentSpawns.Contains(i))
			.ToList();

		if (available.Count == 0) {
			// All spawns are recent, just avoid the very last one
			available = Enumerable.Range(0, spawns.Count)
				.Where(i => spawns.Count == 1 || i != _recentSpawns[^1])
				.ToList();
		}

		index = available[Random.Shared.Next(available.Count)];

		_recentSpawns.Add(index);
		if (_recentSpawns.Count > 3)
			_recentSpawns.RemoveAt(0);

		var spawn = spawns[index];
		var pos = new Vector3(spawn.Pos[0], spawn.Pos[1], spawn.Pos[2]);
		var ang = new Vector3(spawn.Ang[0], spawn.Ang[1], spawn.Ang[2]);
		pawn.Teleport(position: pos, angles: ang);

		return HookResult.Continue;
	}

	[Command("addspawn", Description = "Record your current position as a spawn point", Permission = "tag.spawns.edit")]
	public void CmdAddSpawn(CCitadelPlayerController caller) {
		var pawn = caller.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) return;

		var mapName = Server.MapName;
		if (!Config.SpawnPoints.ContainsKey(mapName))
			Config.SpawnPoints[mapName] = new List<SpawnPoint>();

		var pos = pawn.Position;
		var ang = pawn.CameraAngles;
		Config.SpawnPoints[mapName].Add(new SpawnPoint {
			Pos = [pos.X, pos.Y, pos.Z],
			Ang = [ang.X, ang.Y, ang.Z]
		});

		SaveConfig();

		var count = Config.SpawnPoints[mapName].Count;
		SendChatToPlayer(caller, $"[Tag] Spawn #{count} added at ({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})");
		Console.WriteLine($"[Tag] Spawn #{count} added on {mapName} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
	}

	[Command("removespawn", Description = "Remove the last spawn point on this map", Permission = "tag.spawns.edit")]
	public void CmdRemoveSpawn(CCitadelPlayerController caller) {
		var mapName = Server.MapName;
		if (!Config.SpawnPoints.TryGetValue(mapName, out var spawns) || spawns.Count == 0) {
			SendChatToPlayer(caller, "[Tag] No spawns to remove on this map.");
			return;
		}

		spawns.RemoveAt(spawns.Count - 1);
		_recentSpawns.Clear();
		SaveConfig();

		SendChatToPlayer(caller, $"[Tag] Removed last spawn. {spawns.Count} remaining.");
	}

	[Command("listspawns", Description = "List all configured spawn points on this map")]
	public void CmdListSpawns(CCitadelPlayerController caller) {
		var mapName = Server.MapName;
		if (!Config.SpawnPoints.TryGetValue(mapName, out var spawns) || spawns.Count == 0) {
			SendChatToPlayer(caller, "[Tag] No spawns on this map.");
			return;
		}

		SendChatToPlayer(caller, $"[Tag] {spawns.Count} spawn(s) on {mapName}:");
		for (int i = 0; i < spawns.Count; i++) {
			var s = spawns[i];
			SendChatToPlayer(caller, $"  #{i + 1}: ({s.Pos[0]:F0}, {s.Pos[1]:F0}, {s.Pos[2]:F0})");
		}
	}

	private void SaveConfig() {
		var configPath = this.GetConfigPath();
		if (configPath == null) {
			// Config file doesn't exist yet - derive path from convention
			var dir = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "..", "configs", "TagPlugin");
			Directory.CreateDirectory(dir);
			configPath = Path.Combine(dir, "TagPlugin.jsonc");
		}

		var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(configPath, $"// Configuration for Tag\n{json}\n");
		Console.WriteLine($"[Tag] Config saved to {configPath}");
	}

	// --- Block Abilities ---

	public override void OnAbilityAttempt(AbilityAttemptEvent args) {
		// Block all abilities and items for both teams
		args.Block(InputButton.AllAbilities);
		args.Block(InputButton.AllItems);
	}

	// --- Damage & Tagging ---

	public override HookResult OnTakeDamage(TakeDamageEvent args) {
		var victim = args.Entity;

		// Block damage to map objectives/NPCs
		if (EntitiesToRemove.Contains(victim.DesignerName))
			return HookResult.Stop;

		var attacker = args.Info.Attacker;
		if (attacker == null) return HookResult.Continue;

		var attackerPawn = attacker.As<CCitadelPlayerPawn>();
		var victimPawn = victim.As<CCitadelPlayerPawn>();
		if (attackerPawn == null || victimPawn == null)
			return HookResult.Continue;

		// Hiders deal no damage
		if (attackerPawn.TeamNum == HiderTeam)
			return HookResult.Stop;

		// Seeker melees hider = tag! Only count light or heavy melee hits
		var flags = args.Info.DamageFlags;
		bool isMelee = (flags & TakeDamageFlags.LightMelee) != 0 || (flags & TakeDamageFlags.HeavyMelee) != 0;

		if (attackerPawn.TeamNum == SeekerTeam && victimPawn.TeamNum == HiderTeam && isMelee) {
			// Block the actual damage (no kill)
			// Defer the swap to next tick to avoid issues during damage processing
			Timer.NextTick(() => SwapRoles(attackerPawn, victimPawn));
			return HookResult.Stop;
		}

		// Seeker vs seeker or hider vs hider: no damage
		return HookResult.Stop;
	}

	private void SwapRoles(CCitadelPlayerPawn tagger, CCitadelPlayerPawn tagged) {
		// Find controllers for both players
		CCitadelPlayerController? taggerController = null;
		CCitadelPlayerController? taggedController = null;

		foreach (var controller in Players.GetAll()) {
			var pawn = controller.GetHeroPawn();
			if (pawn == null) continue;
			if (pawn.EntityIndex == tagger.EntityIndex) taggerController = controller;
			if (pawn.EntityIndex == tagged.EntityIndex) taggedController = controller;
		}

		if (taggerController == null || taggedController == null) return;

		var taggerName = taggerController.PlayerName ?? "???";
		var taggedName = taggedController.PlayerName ?? "???";

		const float KnockdownDuration = 3.0f;

		// --- Immediate knockdown + VFX/SFX ---
		tagged.EmitSound("Mystical.Piano.AOE.Warning");

		using var kv = new KeyValues3();
		kv.SetFloat("duration", KnockdownDuration);
		tagged.AddModifier("modifier_citadel_knockdown", kv);

		var piano = CParticleSystem.Create("particles/upgrades/mystical_piano_hit.vpcf")
			.AtPosition(tagged.Position + Vector3.UnitZ * 100)
			.StartActive(true)
			.Spawn();

		tagged.EmitSound("Mystical.Piano.AOE.Explode");

		if (piano != null)
			Timer.Once(3.Seconds(), () => piano.Destroy());

		// HUD announcement to all players
		var announcement = new CCitadelUserMsg_HudGameAnnouncement {
			TitleLocstring = "TAGGED!",
			DescriptionLocstring = $"{taggerName} caught {taggedName}!"
		};
		NetMessages.Send(announcement, RecipientFilter.All);
		BroadcastChat($"[Tag] {taggerName} tagged {taggedName}!");
		Console.WriteLine($"[Tag] {taggerName} tagged {taggedName}");

		// Swap roles after knockdown finishes
		Timer.Once(((int)(KnockdownDuration * 1000)).Milliseconds(), () => {
			taggerController.ChangeTeam(HiderTeam);
			taggerController.SelectHero(Heroes.Astro);

			taggedController.ChangeTeam(SeekerTeam);
			taggedController.SelectHero(Heroes.Warden);

			BroadcastChat($"[Tag] Roles swapped!");
		});
	}

	// --- Block Economy ---

	public override HookResult OnModifyCurrency(ModifyCurrencyEvent args) {
		// Allow score gold (awarded via ECheats source)
		if (args.CurrencyType == ECurrencyType.EGold && args.Source == ECurrencySource.ECheats)
			return HookResult.Continue;
		// Block all other gold gain
		if (args.CurrencyType == ECurrencyType.EGold)
			return HookResult.Stop;
		// Block ability points
		if (args.CurrencyType == ECurrencyType.EAbilityPoints)
			return HookResult.Stop;
		return HookResult.Continue;
	}

	// --- Remove Map Objectives ---

	public override void OnEntitySpawned(EntitySpawnedEvent e) {
		if (EntitiesToRemove.Contains(e.Entity.DesignerName))
			e.Entity.Remove();
	}


	// --- Chat Helpers ---

	/// <summary>
	/// Rebroadcasts chat messages so each recipient sees the message as coming from
	/// their own player slot (works around Deadlock's 12-slot portrait limit).
	/// </summary>
	[NetMessageHandler]
	public HookResult OnChatMsgOutgoing(OutgoingMessageContext<CCitadelUserMsg_ChatMsg> ctx) {
		if (_rebroadcasting) return HookResult.Continue;
		var senderSlot = ctx.Message.PlayerSlot;
		if (senderSlot < 0) return HookResult.Continue;

		var text = ctx.Message.Text;
		var allChat = ctx.Message.AllChat;
		var laneColor = ctx.Message.LaneColor;
		var originalMask = ctx.Recipients.Mask;
		var senderName = _playerNames.GetValueOrDefault(senderSlot, $"Player {senderSlot}");

		_rebroadcasting = true;
		try {
			for (int slot = 0; slot < 64; slot++) {
				if ((originalMask & (1UL << slot)) == 0) continue;
				var msg = new CCitadelUserMsg_ChatMsg {
					PlayerSlot = slot,
					Text = slot == senderSlot ? text : $"[{senderName}]: {text}",
					AllChat = allChat,
					LaneColor = laneColor
				};
				NetMessages.Send(msg, RecipientFilter.Single(slot));
			}
		} finally {
			_rebroadcasting = false;
		}
		return HookResult.Stop;
	}

	private void BroadcastChat(string text) {
		var msg = new CCitadelUserMsg_ChatMsg {
			PlayerSlot = -1,
			Text = text,
			AllChat = true,
		};
		NetMessages.Send(msg, RecipientFilter.All);
	}

	private void SendChatToPlayer(CCitadelPlayerController controller, string text) {
		int slot = controller.EntityIndex - 1;
		SendChatToSlot(slot, text);
	}

	private void SendChatToSlot(int slot, string text) {
		var msg = new CCitadelUserMsg_ChatMsg {
			PlayerSlot = slot,
			Text = text,
			AllChat = true,
		};
		NetMessages.Send(msg, RecipientFilter.Single(slot));
	}

	public override void OnPrecacheResources() {
		Precache.AddHero(Heroes.Warden);
		Precache.AddHero(Heroes.Astro);
		Precache.AddResource("particles/upgrades/mystical_piano_hit.vpcf");
	}
}
