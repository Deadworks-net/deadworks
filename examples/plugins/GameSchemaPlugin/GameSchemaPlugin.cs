using DeadworksManaged.Api;
using DeadworksManaged.Game;

namespace GameSchemaPlugin;

/// <summary>
/// DeadworksManaged.Game in use: every schema class, entity, console variable and name the
/// game has, generated from the game itself. Reach for it when the curated API in
/// DeadworksManaged.Api does not wrap what you need.
/// </summary>
public class GameSchemaPlugin : DeadworksPluginBase
{
	public override string Name => "Game Schema Example";

	private const string StatueModel = "models/heroes_wip/werewolf/werewolf.vmdl";

	public override void OnLoad(bool isReload) { }
	public override void OnUnload() { }

	public override void OnPrecacheResources() => Precache.AddResource(StatueModel);

	// Any curated wrapper has .Schema: every field the game declares for it, under the game's names.
	[Command("stamina", Description = "Show your stamina, read straight from the schema")]
	public void CmdStamina(CCitadelPlayerController caller)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var stamina = pawn.Schema.m_CCitadelAbilityComponent.m_ResourceStamina;
		caller.PrintToConsole($"Stamina {stamina.m_flCurrentValue:0.0} of {stamina.m_flMaxValue:0.0}");
	}

	// An entity is the generated class of what it is, so pattern matching and LINQ work on any of them.
	[Command("troopers", Description = "Count the troopers in each lane")]
	public void CmdTroopers(CCitadelPlayerController caller)
	{
		foreach (var lane in GameEntities.OfType<Schema.CNPC_Trooper>().GroupBy(trooper => trooper.m_iLane).OrderBy(lane => lane.Key))
			caller.PrintToConsole($"Lane {lane.Key}: {lane.Count()} troopers, {lane.Sum(trooper => trooper.m_iHealth)} health in total");
	}

	// Spawn has a function per entity the game can create, with the key values that entity reads.
	[Command("statue", Description = "Stand a red werewolf where you are")]
	public void CmdStatue(CCitadelPlayerController caller)
	{
		var pawn = caller.GetHeroPawn();
		if (pawn == null) return;

		var statue = Spawn.prop_dynamic(new() { model = StatueModel, origin = pawn.Position });
		statue?.InputColor(new Color32(255, 0, 0));
	}

	// ConVars has the game's console variables with their types; ItemNames, AbilityNames,
	// HeroNames and ModifierNames have every name the curated API takes as a string.
	[Command("richtroopers", Description = "Double what a trooper is worth and take a pair of boots")]
	public void CmdRichTroopers(CCitadelPlayerController caller)
	{
		ConVars.citadel_trooper_gold_reward.Value *= 2;
		caller.GetHeroPawn()?.AddItem(ItemNames.upgrade_fleetfoot_boots);
	}

	// Inputs and Outputs name every entity input and output, for hooks.
	[EntityOutputHook(EntityNames.trigger_multiple, Outputs.CBaseTrigger.OnStartTouch)]
	public HookResult OnTriggerTouched(EntityOutputEvent e) => HookResult.Continue;
}
