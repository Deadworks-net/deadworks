using Stopwatch = System.Diagnostics.Stopwatch;
using System.Drawing;
using System.Numerics;
using DeadworksManaged.Api;

namespace NpcDemoPlugin;

/// <summary>
/// Chat commands that show off the NPC, navigation and bot API. Most act on the point under your crosshair.
/// Type <c>/npcdemo</c> in game for the list.
/// </summary>
public class NpcDemoPlugin : DeadworksPluginBase {
	public override string Name => "NPC Demo";

	private const float NearbyRadius = 3000f;
	private const int CampTeam = 4; // the map's neutral camps

	private readonly List<CAI_BaseNPC> _spawned = [];
	private readonly List<CBaseEntity> _blockers = [];
	private readonly List<CBeam> _beams = [];
	private readonly List<CBeam> _boxBeams = [];
	private readonly List<int> _botSlots = [];

	private Chase? _horde;
	private readonly List<CAI_BaseNPC> _hordeMembers = [];
	private IHandle? _hordeTimer;
	private Chase? _chase;
	private IHandle? _chaseTimer;
	private Squad? _squad;
	private IHandle? _squadTimer;
	private Walk? _walk;
	private IHandle? _walkTimer;
	private bool _hold;

	// Bots walked by this plugin: slot -> path follower (botcome) or null (botgo, the brain's own path).
	private readonly Dictionary<int, NavPathFollower?> _drivenBots = [];
	// botgo: usercmds in a row the brain has had nowhere to go, per slot.
	private readonly Dictionary<int, int> _botGoIdle = [];
	private const int BotGoIdleLimit = 32;
	private (CCitadelPlayerController Caller, Vector3 Goal, int Total, int Arrived)? _botGo;
	private IHandle? _botComeTimer;

	public override void OnLoad(bool isReload) { }

	// A hot reload unloads off the game thread, so only cancel timers here; /cleanup removes what the demo made.
	public override void OnUnload() {
		foreach (var handle in new[] { _hordeTimer, _chaseTimer, _squadTimer, _walkTimer, _botComeTimer })
			handle?.Cancel();
	}

	public override void OnStartupServer() {
		// Entities from the last map are gone; forget them.
		StopAll();
		_spawned.Clear();
		_blockers.Clear();
		_beams.Clear();
		_boxBeams.Clear();
		_botSlots.Clear();
		_hordeMembers.Clear();
	}

	[Command("npcdemo", Description = "List the NPC demo commands")]
	public void CmdHelp(CCitadelPlayerController caller) {
		string[] lines = [
			"NPC demo. Most commands act on the point under your crosshair.",
			"Spawning: /spawn [normal|medic|melee] [count] [enemy|ally]  /npc <subclass> [enemy|ally|neutral]",
			"  subclasses: neutral_trooper_weak, neutral_trooper_strong, npc_boss_tier1, trooper_medic ...",
			"Orders: /goto  /attack (what you aim at)  /stop  /hold (toggle: troopers stand still)",
			"Groups: /horde [count] (enemies spawn and chase you)  /chaseme (nearby troopers)  /squad [count] (minions)",
			"Info: /info (NPC you aim at)  /npcs",
			"Nav: /path  /nav  /randompoints  /block  /unblock",
			"Bots: /bot [hero] [enemy|ally]  /botai [on|off]  /botcome (bots walk to you)  /botgo (bots walk to the crosshair)",
			"/cleanup removes everything the demo made.",
		];
		foreach (var line in lines)
			Reply(caller, line);
	}

	// --- Spawning ---

	[Command("spawn", Description = "Spawn lane troopers at your crosshair: /spawn [normal|medic|melee] [count] [enemy|ally]")]
	public void CmdSpawn(CCitadelPlayerController caller, string type = "normal", int count = 3, string side = "enemy") {
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		if (!TryParseTrooper(type, out var trooperType)) {
			Reply(caller, "Trooper type is normal, medic or melee.");
			return;
		}
		int team = Team(pawn, side);
		count = Math.Clamp(count, 1, 30);
		var sw = Stopwatch.StartNew();
		int made = 0;
		foreach (var point in PointsAround(aim, count, 1)) {
			if (CNPC_Trooper.Create(point + new Vector3(0, 0, 40), team, trooperType) is { } trooper) {
				_spawned.Add(trooper);
				made++;
			}
		}
		Reply(caller, $"Spawned {made} {trooperType} trooper(s) on team {team} in {sw.Elapsed.TotalMilliseconds:F1} ms. They join the nearest lane.");
	}

	[Command("npc", Description = "Spawn any NPC by VData subclass name: /npc <subclass> [enemy|ally|neutral]")]
	public void CmdNpc(CCitadelPlayerController caller, string subclass = "", string side = "neutral") {
		if (subclass == "") {
			Reply(caller, "Usage: /npc <subclass> [enemy|ally|neutral], e.g. /npc npc_boss_tier1 enemy");
			return;
		}
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		var spot = Nav.GetNearestPoint(aim, 600f) ?? aim;
		var npc = CAI_BaseNPC.Create(subclass, spot + new Vector3(0, 0, 20), side == "neutral" ? 0 : Team(pawn, side));
		if (npc == null) {
			Reply(caller, $"'{subclass}' is not an NPC subclass.");
			return;
		}
		_spawned.Add(npc);
		Reply(caller, $"Spawned {npc.Classname} ({subclass}) #{npc.EntityIndex}, team {npc.TeamNum}, {npc.Health} HP.");
	}

	// --- Orders ---

	[Command("goto", Description = "Nearby NPCs walk to your crosshair")]
	public void CmdGoto(CCitadelPlayerController caller) {
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		StopGroups();
		var goal = Nav.GetNearestPoint(aim, 600f, 1) ?? aim;
		var walk = new Walk(goal);
		int n = 0, noRoute = 0, camp = 0;
		foreach (var npc in Nearby(pawn)) {
			// The map's neutral creeps keep to their camps; ones made with /npc follow orders.
			if (npc.TeamNum == CampTeam && !_spawned.Any(s => s.EntityHandle == npc.EntityHandle)) {
				camp++;
				continue;
			}
			if (walk.Add(npc))
				n++;
			else
				noRoute++;
		}
		if (n == 0) {
			Reply(caller, noRoute > 0 ? $"None of the {noRoute} nearby NPC(s) has a route there." : "No NPCs nearby.");
			return;
		}
		_walk = walk;
		_walkTimer = Timer.Every(TimeSpan.FromSeconds(Walk.Interval), () => {
			walk.Update();
			if (!walk.IsDone)
				return;
			EndWalk();
			if (caller.IsValid)
				Reply(caller, $"/goto: {walk.Arrived} of {n} NPC(s) reached the crosshair in {walk.Elapsed.TotalSeconds:F0} s" +
				              (walk.Died > 0 ? $", {walk.Died} died on the way" : "") +
				              (walk.GaveUp.Count > 0 ? $"; gave up: {string.Join(", ", walk.GaveUp.Select(g => $"{g.Key} {g.Value}"))}." : "."));
		}).CancelOnMapChange();
		Reply(caller, $"{n} NPC(s) heading to the crosshair along a navmesh route" + (noRoute > 0 ? $" ({noRoute} have no route there)." : ".") +
		              (camp > 0 ? $" {camp} neutral camp NPC(s) stay at their camps." : ""));
	}

	[Command("attack", Description = "Nearby NPCs attack what you aim at")]
	public void CmdAttack(CCitadelPlayerController caller) {
		if (!TryAim(caller, out var pawn, out _, out var hit))
			return;
		if (hit == null || hit.Health <= 0) {
			Reply(caller, "Aim at something with health.");
			return;
		}
		int n = 0;
		foreach (var npc in Nearby(pawn)) {
			if (npc.TeamNum == hit.TeamNum || npc.EntityHandle == hit.EntityHandle)
				continue;
			npc.Stop();
			npc.SetEnemy(hit);
			n++;
		}
		Reply(caller, $"{n} NPC(s) told to attack {hit.Classname} #{hit.EntityIndex}. Their AI may switch to a closer target.");
	}

	[Command("stop", Description = "Nearby NPCs drop their orders; stops /horde, /chaseme and bot walking")]
	public void CmdStop(CCitadelPlayerController caller) {
		var pawn = caller.GetHeroPawn();
		StopAll();
		int n = 0;
		if (pawn != null)
			foreach (var npc in Nearby(pawn)) {
				npc.Stop(instant: true);
				n++;
			}
		Reply(caller, $"Stopped {n} NPC(s).");
	}

	[Command("hold", Description = "Toggle: troopers stand still instead of walking their lane")]
	public void CmdHold(CCitadelPlayerController caller) {
		_hold = !_hold;
		var pawn = caller.GetHeroPawn();
		int n = 0;
		if (pawn != null)
			foreach (var trooper in NearbyTroopers(pawn)) {
				trooper.SetSchedule(_hold ? "SCHED_IDLE_STAND" : "SCHED_TROOPER_RUN_DOWN_LANE");
				n++;
			}
		Reply(caller, _hold
			? $"Hold on: troopers stand still instead of walking their lanes ({n} nearby stopped now). They still fight."
			: $"Hold off: troopers walk their lanes again ({n} nearby sent).");
	}

	public override void OnNpcSelectSchedule(NpcSelectScheduleEvent e) {
		_squad?.OnSelectSchedule(e);
		if (e.Override == null && _hold && e.Npc.As<CNPC_Trooper>() != null
		    && e.Schedule is "SCHED_TROOPER_RUN_DOWN_LANE" or "SCHED_TROOPER_RUN_TO_ENEMY_BASE_OBJECTIVE")
			e.Override = "SCHED_IDLE_STAND";
	}

	// --- Groups ---

	[Command("horde", Description = "Enemy melee troopers spawn around you and chase you; again to end")]
	public void CmdHorde(CCitadelPlayerController caller, int count = 16) {
		if (_hordeTimer != null) {
			EndHorde(remove: true);
			Reply(caller, "Horde removed.");
			return;
		}
		var pawn = caller.GetHeroPawn();
		if (pawn == null)
			return;
		count = Math.Clamp(count, 1, 60);
		var sw = Stopwatch.StartNew();
		var feet = Feet(pawn);
		for (int i = 0; i < count * 3 && _hordeMembers.Count < count; i++) {
			if (Nav.GetRandomPoint(feet, 700f, 1400f, 1) is not { } point)
				continue;
			if (CNPC_Trooper.Create(point + new Vector3(0, 0, 40), Team(pawn, "enemy"), TrooperType.Melee) is { } trooper)
				_hordeMembers.Add(trooper);
		}
		double spawnMs = sw.Elapsed.TotalMilliseconds;
		_horde = new Chase(() => _hordeMembers);
		_hordeTimer = Timer.Every(TimeSpan.FromSeconds(Chase.Interval), () => {
			if (!pawn.IsValid || pawn.Health <= 0)
				return;
			_horde?.Update(Feet(pawn));
		}).CancelOnMapChange();
		Reply(caller, $"{_hordeMembers.Count} enemies spawned in {spawnMs:F1} ms, 700-1400 units away. /horde again to end it.");
	}

	[Command("chaseme", Description = "Nearby lane troopers chase you; again to stop")]
	public void CmdChaseMe(CCitadelPlayerController caller) {
		if (_chaseTimer != null) {
			EndChase();
			Reply(caller, "Chase over; troopers go back to their lanes.");
			return;
		}
		var pawn = caller.GetHeroPawn();
		if (pawn == null)
			return;
		_chase = new Chase(() => NearbyTroopers(pawn).Where(t => _squad?.Contains(t) != true));
		_chaseTimer = Timer.Every(TimeSpan.FromSeconds(Chase.Interval), () => {
			if (pawn.IsValid && pawn.Health > 0)
				_chase?.Update(Feet(pawn));
		}).CancelOnMapChange();
		Reply(caller, $"Troopers within {NearbyRadius:F0} units chase you. /chaseme again to stop.");
	}

	[Command("squad", Description = "Spawn allied troopers that follow you as minions; again to dismiss")]
	public void CmdSquad(CCitadelPlayerController caller, int count = 4) {
		if (_squad != null) {
			EndSquad();
			Reply(caller, "Squad dismissed.");
			return;
		}
		var pawn = caller.GetHeroPawn();
		if (pawn == null)
			return;
		_squad = new Squad(pawn);
		count = Math.Clamp(count, 1, Squad.MaxSize);
		foreach (var point in PointsAround(Feet(pawn), count, 1))
			if (CNPC_Trooper.Create(point + new Vector3(0, 0, 40), pawn.TeamNum, TrooperType.Normal) is { } trooper)
				_squad.Add(trooper);
		_squadTimer = Timer.Every(TimeSpan.FromSeconds(Squad.Interval), () => {
			if (pawn.IsValid)
				_squad?.Update(Feet(pawn));
		}).CancelOnMapChange();
		Reply(caller, $"{_squad.Count} minion(s) follow you; their kills count for you. /squad again to dismiss.");
	}

	// --- Info ---

	[Command("info", Description = "Describe the NPC you aim at, or the nearest one")]
	public void CmdInfo(CCitadelPlayerController caller) {
		if (!TryAim(caller, out var pawn, out var aim, out var hit))
			return;
		var npc = hit?.As<CAI_BaseNPC>()
		          ?? Entities.ByClass<CAI_BaseNPC>().Where(n => !n.IsDormant && n.Health > 0 && !n.Is<CCitadelPlayerBotNPCBrain>())
			          .MinBy(n => Vector3.DistanceSquared(n.Position, aim));
		if (npc == null) {
			Reply(caller, "No NPC found.");
			return;
		}
		var citadel = npc.As<CAI_CitadelNPC>();
		var trooper = npc.As<CNPC_Trooper>();
		Reply(caller, $"#{npc.EntityIndex} {npc.Classname} ({npc.SubclassVData?.Name ?? "?"}), team {npc.TeamNum}, {npc.Health}/{npc.MaxHealth} HP, " +
		              $"{Vector3.Distance(npc.Position, pawn.Position):F0} units away");
		Reply(caller, $"  state {npc.State}, schedule {npc.Schedule ?? "none"}, enemy {Describe(npc.Enemy)}, hull {Nav.GetHull(npc)}");
		if (citadel != null)
			Reply(caller, $"  minion {citadel.IsMinion}, summoner {Describe(citadel.Summoner)}");
		if (trooper != null)
			Reply(caller, $"  lane {trooper.Lane}, boss weapon {trooper.UsingBossWeapon}");
	}

	[Command("npcs", Description = "Count the NPCs on the map")]
	public void CmdNpcs(CCitadelPlayerController caller) {
		var npcs = Entities.ByClass<CAI_BaseNPC>().ToList();
		Reply(caller, $"{npcs.Count} NPCs, {npcs.Count(n => n.IsDormant)} of them hidden in the trooper pool.");
		foreach (var group in npcs.Where(n => !n.IsDormant).GroupBy(n => n.Classname).OrderByDescending(g => g.Count()).Take(8))
			Reply(caller, $"  {group.Key}: {group.Count()}");
	}

	// --- Navigation ---

	[Command("path", Description = "Draw the navmesh path from you to your crosshair")]
	public void CmdPath(CCitadelPlayerController caller) {
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		var start = Feet(pawn);
		var goal = Nav.GetNearestPoint(aim, pawn, 600f);
		if (goal == null) {
			Reply(caller, "The crosshair is off the navmesh.");
			return;
		}
		var sw = Stopwatch.StartNew();
		var path = Nav.FindPath(start, goal.Value, pawn);
		double ms = sw.Elapsed.TotalMilliseconds;
		if (path == null) {
			Reply(caller, $"No path ({ms:F2} ms).");
			return;
		}
		var points = path.Waypoints;
		for (int i = 1; i < points.Count; i++)
			DrawLine(points[i - 1].Position, points[i].Position, TraverseColor(points[i].Traverse), seconds: 15);
		var kinds = string.Join(", ", points.Skip(1).GroupBy(w => w.Traverse).Select(g => $"{g.Key} {g.Count()}"));
		Reply(caller, $"Path: {points.Count} waypoints, {path.Length:F0} units walked ({Vector3.Distance(start, goal.Value):F0} straight), {ms:F2} ms. {kinds}.");
		Reply(caller, "  Green walk, yellow jump, orange climb, cyan ladder or link. Shown for 15 s.");
	}

	[Command("nav", Description = "Navmesh facts at your crosshair, and a straight-line walk test from you")]
	public void CmdNav(CCitadelPlayerController caller) {
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		int hull = Nav.GetHull(pawn);
		var area = Nav.GetArea(aim, hull: hull);
		if (area == null) {
			Reply(caller, "No navmesh near the crosshair.");
			return;
		}
		Reply(caller, $"Area {area.Value.Id} (hull {hull}): centre {Fmt(area.Value.Center)}, attributes [{area.Value.Attributes}], flags [{area.Value.Flags}], blocked {area.Value.IsBlocked}");
		Reply(caller, $"  ground height {Nav.GetGroundHeight(aim, hull: hull):F1} at the crosshair (z {aim.Z:F1})");
		var feet = Feet(pawn);
		bool clear = Nav.Raycast(feet, aim, out var stoppedAt, hull);
		DrawLine(feet, clear ? aim : stoppedAt, clear ? Color.Lime : Color.Red, seconds: 10);
		Reply(caller, clear ? "  You can walk there in a straight line (green)." : $"  A straight walk leaves the navmesh after {Vector3.Distance(feet, stoppedAt):F0} units (red).");
	}

	[Command("randompoints", Description = "Show 10 random points you can walk to, 300-1500 units away")]
	public void CmdRandomPoints(CCitadelPlayerController caller) {
		var pawn = caller.GetHeroPawn();
		if (pawn == null)
			return;
		var sw = Stopwatch.StartNew();
		var feet = Feet(pawn);
		int n = 0;
		for (int i = 0; i < 10; i++) {
			if (Nav.GetRandomPoint(feet, 300f, 1500f, Nav.GetHull(pawn)) is not { } p)
				continue;
			DrawLine(p, p + new Vector3(0, 0, 300), Color.Magenta, seconds: 15);
			n++;
		}
		Reply(caller, $"{n} reachable points (magenta pillars) in {sw.Elapsed.TotalMilliseconds:F1} ms. Shown for 15 s.");
	}

	[Command("block", Description = "Block a 300x300 box of the navmesh at your crosshair")]
	public void CmdBlock(CCitadelPlayerController caller) {
		if (!TryAim(caller, out _, out var aim, out _))
			return;
		var half = new Vector3(150, 150, 100);
		var blocker = Nav.CreateBlocker(aim, half);
		if (blocker == null) {
			Reply(caller, "Couldn't create the blocker.");
			return;
		}
		_blockers.Add(blocker);
		var z = new Vector3(0, 0, 10);
		Vector3[] corners = [aim + new Vector3(-150, -150, 0), aim + new Vector3(150, -150, 0), aim + new Vector3(150, 150, 0), aim + new Vector3(-150, 150, 0)];
		for (int i = 0; i < 4; i++)
			DrawLine(corners[i] + z, corners[(i + 1) % 4] + z, Color.Red, seconds: 0);
		Reply(caller, "Blocked (red box). NPCs and /path route around it. /unblock to remove.");
	}

	[Command("unblock", Description = "Remove every navmesh block made with /block")]
	public void CmdUnblock(CCitadelPlayerController caller) {
		int n = RemoveAll(_blockers);
		RemoveAll(_boxBeams);
		Reply(caller, $"Removed {n} block(s).");
	}

	// --- Bots ---

	[Command("bot", Description = "Add a bot hero at your crosshair: /bot [hero] [enemy|ally]")]
	public void CmdBot(CCitadelPlayerController caller, string hero = "random", string side = "enemy") {
		if (!TryAim(caller, out var pawn, out var aim, out _))
			return;
		Heroes? heroId = null;
		if (hero != "random") {
			if (!Enum.TryParse<Heroes>(hero, ignoreCase: true, out var parsed) && !HeroTypeExtensions.TryParse(hero, out parsed)) {
				Reply(caller, $"Unknown hero '{hero}'.");
				return;
			}
			heroId = parsed;
		}
		var spot = Nav.GetNearestPoint(aim, 600f) ?? aim;
		var bot = Bots.Add(heroId, Team(pawn, side), spot, BotDifficulty.Medium);
		if (bot?.Controller is not { } controller) {
			Reply(caller, "Couldn't add the bot (hero not allowed for bots, or the server is full).");
			return;
		}
		_botSlots.Add(controller.Slot);
		Reply(caller, $"Added {controller.PlayerName} on team {bot.TeamNum}. Bot AI is {(Bots.AiEnabled ? "on" : "off")}: /botai to change it.");
	}

	[Command("botai", Description = "Turn the bot AI on or off: /botai [on|off]")]
	public void CmdBotAi(CCitadelPlayerController caller, string mode = "") {
		bool enable = mode switch { "on" => true, "off" => false, _ => !Bots.AiEnabled };
		if (enable)
			StopBotWalking();
		Bots.SetAiEnabled(enable);
		Reply(caller, enable ? "Bot AI on." : "Bot AI off: bots only move when a plugin moves them.");
	}

	[Command("botcome", Description = "Bots walk to you along a navmesh path (bot AI off); again to stop")]
	public void CmdBotCome(CCitadelPlayerController caller) {
		if (_botComeTimer != null) {
			StopBotWalking();
			Reply(caller, "Bots stopped.");
			return;
		}
		var pawn = caller.GetHeroPawn();
		if (pawn == null)
			return;
		Bots.SetAiEnabled(false);
		int callerSlot = caller.Slot;
		var bots = BotHeroes(except: callerSlot).ToList();
		// Re-path every second towards where you are now.
		_botComeTimer = Timer.Every(TimeSpan.FromSeconds(1), () => {
			if (!pawn.IsValid)
				return;
			var goal = Feet(pawn);
			foreach (var (slot, hero) in BotHeroes(except: callerSlot)) {
				var current = _drivenBots.GetValueOrDefault(slot);
				if (current != null && !current.IsFinished && Vector3.Distance(current.Path.End, goal) < 300f)
					continue;
				var start = Feet(hero);
				var path = Nav.FindPath(start, goal, hero);
				_drivenBots[slot] = path != null ? new NavPathFollower(path) : null;
			}
		}).CancelOnMapChange();
		Reply(caller, $"{bots.Count} bot(s) walking to you with Nav.FindPath + NavPathFollower (bot AI turned off). /botcome again to stop.");
	}

	[Command("botgo", Description = "Bots walk to your crosshair on their brain's own path (bot AI off)")]
	public void CmdBotGo(CCitadelPlayerController caller) {
		if (!TryAim(caller, out _, out var aim, out _))
			return;
		StopBotWalking();
		Bots.SetAiEnabled(false);
		var goal = Nav.GetNearestPoint(aim, 600f) ?? aim;
		int n = 0;
		foreach (var (slot, hero) in BotHeroes(except: caller.Slot)) {
			if (hero.BotBrain is not { } brain)
				continue;
			brain.MoveTo(goal, run: true);
			_drivenBots[slot] = null;
			_botGoIdle[slot] = 0;
			n++;
		}
		_botGo = (caller, goal, n, 0);
		Reply(caller, $"{n} bot(s) heading to the crosshair: brain MoveTo + ApplyMovement (bot AI turned off).");
	}

	public override void OnProcessUsercmds(ProcessUsercmdsEvent e) {
		if (!_drivenBots.TryGetValue(e.PlayerSlot, out var follower))
			return;
		var hero = e.Controller?.GetHeroPawn();
		if (hero == null)
			return;
		if (follower == null) {
			// botgo: ApplyMovement walks the bot along the brain's path. It also returns false for the first usercmd
			// after MoveTo, so only a run of falses counts as done.
			var brain = hero.BotBrain;
			if (brain == null || _botComeTimer != null)
				return;
			foreach (var cmd in e.Usercmds) {
				if (brain.ApplyMovement(cmd)) {
					_botGoIdle[e.PlayerSlot] = 0;
				} else if ((_botGoIdle[e.PlayerSlot] = _botGoIdle.GetValueOrDefault(e.PlayerSlot) + 1) > BotGoIdleLimit) {
					BotGoDone(e.PlayerSlot, hero);
					return;
				}
			}
			return;
		}
		if (follower.Update(hero.Position) is not { } next)
			return;
		bool jump = follower.ShouldJump(hero.Position);
		foreach (var cmd in e.Usercmds)
			Bots.SetMoveInput(cmd, hero, next, jump: jump);
	}

	private void BotGoDone(int slot, CCitadelPlayerPawn hero) {
		_drivenBots.Remove(slot);
		_botGoIdle.Remove(slot);
		if (_botGo is not { } go)
			return;
		var flat = hero.Position - go.Goal;
		if (new Vector2(flat.X, flat.Y).Length() <= 150f)
			go.Arrived++;
		_botGo = go;
		if (_drivenBots.ContainsValue(null))
			return;
		_botGo = null;
		if (go.Caller.IsValid)
			Reply(go.Caller, $"/botgo: {go.Arrived} of {go.Total} bot(s) reached the crosshair.");
	}

	// --- Cleanup ---

	[Command("cleanup", Description = "Remove everything the NPC demo made")]
	public void CmdCleanup(CCitadelPlayerController caller) {
		int npcs = _spawned.Count(n => n.IsValid) + _hordeMembers.Count(n => n.IsValid) + (_squad?.Count ?? 0);
		int bots = _botSlots.Count;
		Cleanup(kickBots: true);
		Reply(caller, $"Removed {npcs} NPC(s), kicked {bots} bot(s), cleared blocks and drawings.");
	}

	private void Cleanup(bool kickBots) {
		StopAll();
		EndHorde(remove: true);
		EndSquad();
		RemoveAll(_spawned);
		RemoveAll(_blockers);
		RemoveAll(_beams);
		RemoveAll(_boxBeams);
		_hold = false;
		if (kickBots)
			foreach (int slot in _botSlots)
				Server.Kick(slot);
		_botSlots.Clear();
	}

	private void StopAll() {
		StopGroups();
		StopBotWalking();
	}

	/// <summary>Ends /chaseme and the horde's chase; the horde itself stays.</summary>
	private void StopGroups() {
		EndChase();
		EndWalk();
		_hordeTimer?.Cancel();
		_hordeTimer = null;
		_horde?.Release();
		_horde = null;
	}

	private void EndHorde(bool remove) {
		_hordeTimer?.Cancel();
		_hordeTimer = null;
		_horde?.Release();
		_horde = null;
		if (remove)
			RemoveAll(_hordeMembers);
		_hordeMembers.Clear();
	}

	private void EndChase() {
		_chaseTimer?.Cancel();
		_chaseTimer = null;
		_chase?.Release();
		_chase = null;
	}

	private void EndWalk() {
		_walkTimer?.Cancel();
		_walkTimer = null;
		_walk?.Release();
		_walk = null;
	}

	private void EndSquad() {
		_squadTimer?.Cancel();
		_squadTimer = null;
		_squad?.Disband();
		_squad = null;
	}

	private void StopBotWalking() {
		_botComeTimer?.Cancel();
		_botComeTimer = null;
		_drivenBots.Clear();
		_botGoIdle.Clear();
		_botGo = null;
	}

	// --- Helpers ---

	/// <summary>The caller's hero, and what is under the crosshair: a point and the entity there, if any.</summary>
	private static bool TryAim(CCitadelPlayerController caller, out CCitadelPlayerPawn pawn, out Vector3 point, out CBaseEntity? hit) {
		pawn = caller.GetHeroPawn()!;
		point = default;
		hit = null;
		if (pawn == null) {
			Reply(caller, "You need a hero for that.");
			return false;
		}
		float pitch = pawn.EyeAngles.X * MathF.PI / 180f, yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
		var forward = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
		var start = pawn.EyePosition;
		var trace = TraceLine(start, start + forward * 8000f, RnQueryObjectSet.All, pawn);
		if (!trace.DidHit) {
			Reply(caller, "Aim at the ground or something nearer.");
			return false;
		}
		point = start + forward * 8000f * trace.Fraction;
		hit = trace.HitEntity;
		if (hit != null && hit.Health <= 0)
			hit = null;
		return true;
	}

	/// <summary>The navmesh point under the pawn, even while it is in the air, where nav queries from its position fail.</summary>
	private static Vector3 Feet(CCitadelPlayerPawn pawn) {
		var from = pawn.Position + new Vector3(0, 0, 8);
		var down = new Vector3(0, 0, -4000f);
		var trace = TraceLine(from, from + down, RnQueryObjectSet.Static | RnQueryObjectSet.Keyframed, pawn);
		var ground = trace.DidHit ? from + down * trace.Fraction : pawn.Position;
		// Standing on something the navmesh leaves out, such as a roof: the nearest walkable point instead.
		return Nav.GetNearestPoint(ground, pawn, 400f) ?? Nav.GetNearestPoint(ground, pawn, 1500f) ?? ground;
	}

	private static CGameTrace TraceLine(Vector3 start, Vector3 end, RnQueryObjectSet objects, CCitadelPlayerPawn pawn) {
		// Skip trigger and post-processing volumes: a map-wide one stops the trace at once. A bot's brain sits inside
		// its hero, so ignore it too.
		var trace = CGameTrace.Create();
		Trace.SimpleTrace(start, end, RayType_t.Line, objects, InteractionLayer.Solid | InteractionLayer.Hitbox,
			InteractionLayer.Trigger | InteractionLayer.PostProcessingVolume, InteractionLayer.None, CollisionGroup.Always,
			ref trace, pawn, pawn.BotBrain);
		return trace;
	}

	/// <summary>Live NPCs within <see cref="NearbyRadius"/> of the pawn, bot brains left out.</summary>
	private static IEnumerable<CAI_BaseNPC> Nearby(CCitadelPlayerPawn pawn) {
		var origin = Feet(pawn);
		return Entities.ByClass<CAI_BaseNPC>().Where(n => !n.IsDormant && n.Health > 0 && !n.Is<CCitadelPlayerBotNPCBrain>()
		                                                  && Vector3.DistanceSquared(n.Position, origin) < NearbyRadius * NearbyRadius);
	}

	private static IEnumerable<CNPC_Trooper> NearbyTroopers(CCitadelPlayerPawn pawn) =>
		Nearby(pawn).Select(n => n.As<CNPC_Trooper>()).OfType<CNPC_Trooper>();

	/// <summary>The bot heroes on the server, by slot, leaving out the one in slot <paramref name="except"/>.</summary>
	private static IEnumerable<(int Slot, CCitadelPlayerPawn Hero)> BotHeroes(int except = -1) {
		foreach (var controller in Players.GetAll())
			if (controller.IsBot && controller.Slot != except && controller.GetHeroPawn() is { IsValid: true } hero && hero.BotBrain != null)
				yield return (controller.Slot, hero);
	}

	/// <summary><paramref name="count"/> navmesh points: the first at <paramref name="centre"/>, the rest near it.</summary>
	private static IEnumerable<Vector3> PointsAround(Vector3 centre, int count, int hull) {
		var first = Nav.GetNearestPoint(centre, 600f, hull) ?? centre;
		yield return first;
		int made = 1;
		for (int i = 0; i < count * 4 && made < count; i++) {
			if (Nav.GetRandomPoint(first, 60f, 350f, hull) is not { } p)
				continue;
			made++;
			yield return p;
		}
	}

	private static bool TryParseTrooper(string text, out TrooperType type) {
		type = text.ToLowerInvariant() switch {
			"normal" or "ranged" => TrooperType.Normal,
			"medic" => TrooperType.Medic,
			"melee" => TrooperType.Melee,
			_ => 0,
		};
		return type != 0;
	}

	/// <summary>2 Amber and 3 Sapphire: "ally" is the pawn's team, anything else the other one.</summary>
	private static int Team(CCitadelPlayerPawn pawn, string side) {
		int own = pawn.TeamNum is 2 or 3 ? pawn.TeamNum : 2;
		return side.Equals("ally", StringComparison.OrdinalIgnoreCase) ? own : 5 - own;
	}

	private void DrawLine(Vector3 from, Vector3 to, Color color, int seconds) {
		var lift = new Vector3(0, 0, 16);
		if (CBeam.Create(from + lift, to + lift, width: 4f, color: color) is not { } beam)
			return;
		if (seconds <= 0) {
			_boxBeams.Add(beam);
			return;
		}
		_beams.Add(beam);
		Timer.Once(TimeSpan.FromSeconds(seconds), () => {
			if (beam.IsValid)
				beam.Remove();
			_beams.Remove(beam);
		});
	}

	private static Color TraverseColor(NavTraverse traverse) => traverse switch {
		NavTraverse.Ground => Color.Lime,
		NavTraverse.Jump => Color.Yellow,
		NavTraverse.Climb => Color.Orange,
		_ => Color.Cyan,
	};

	private static int RemoveAll<T>(List<T> entities) where T : CBaseEntity {
		int n = 0;
		foreach (var entity in entities)
			if (entity.IsValid) {
				entity.Remove();
				n++;
			}
		entities.Clear();
		return n;
	}

	private static string Describe(CBaseEntity? entity) => entity == null ? "none" : $"{entity.Classname} #{entity.EntityIndex}";

	private static string Fmt(Vector3 v) => $"({v.X:F0}, {v.Y:F0}, {v.Z:F0})";

	private static void Reply(CCitadelPlayerController caller, string message) {
		Chat.PrintToChat(caller, message);
		caller.PrintToConsole(message);
		Console.WriteLine($"[NpcDemo] {caller.PlayerName}: {message}");
	}
}
