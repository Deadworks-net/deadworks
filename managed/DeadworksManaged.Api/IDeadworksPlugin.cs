using Microsoft.Extensions.Logging;

namespace DeadworksManaged.Api;

/// <summary>
/// Core plugin interface. Implement this (or extend <see cref="DeadworksPluginBase"/>) to create a Deadworks plugin.
/// Methods have default no-op implementations so you only need to override what you use.
/// </summary>
public interface IDeadworksPlugin {
	/// <summary>Display name of the plugin.</summary>
	string Name { get; }
	/// <summary>Called when the plugin is loaded or hot-reloaded.</summary>
	void OnLoad(bool isReload);
	/// <summary>Called when the plugin is unloaded. Clean up hooks and timers here.</summary>
	void OnUnload();

	/// <summary>
	/// Per-plugin timer service. Use to schedule delayed or repeating actions.
	/// </summary>
	ITimer Timer => TimerResolver.Get(this);

	/// <summary>
	/// Per-plugin logger instance. Uses the plugin's <see cref="Name"/> as the log category.
	/// </summary>
	ILogger Logger => LogResolver.Get(this);

	/// <summary>
	/// Called during map load to precache resources (particles, models, etc).
	/// Use <see cref="Precache.AddResource"/> to register resources.
	/// </summary>
	void OnPrecacheResources() { }

	/// <summary>
	/// Called when the server starts up (new map load).
	/// </summary>
	void OnStartupServer() { }

	/// <summary>
	/// Called every server frame.
	/// </summary>
	void OnGameFrame(bool simulating, bool firstTick, bool lastTick) { }

	/// <summary>
	/// Called when an entity takes damage.
	/// Return Stop to block the damage from being applied.
	/// </summary>
	HookResult OnTakeDamage(TakeDamageEvent args) => HookResult.Continue;

	/// <summary>
	/// Called when a player's currency is about to be modified.
	/// Return Stop to block the currency change.
	/// </summary>
	HookResult OnModifyCurrency(ModifyCurrencyEvent args) => HookResult.Continue;

	/// <summary>
	/// Called when a player sends a chat message.
	/// Return Stop to block the message from being processed further.
	/// </summary>
	HookResult OnChatMessage(ChatMessage message) => HookResult.Continue;

	/// <summary>
	/// Called when a client sends a console command (e.g. selecthero, changeteam, respawn).
	/// Return Stop to block the command from being processed by the engine.
	/// </summary>
	HookResult OnClientConCommand(ClientConCommandEvent args) => HookResult.Continue;

	/// <summary>
	/// Called when a client is connecting. Return false to reject the connection.
	/// All plugins see the event regardless of any individual result.
	/// </summary>
	bool OnClientConnect(ClientConnectEvent args) => true;

	/// <summary>
	/// Called when a client is put into the server (initial connection).
	/// </summary>
	void OnClientPutInServer(ClientPutInServerEvent args) { }

	/// <summary>
	/// Called when a client has fully connected and is in-game.
	/// </summary>
	void OnClientFullConnect(ClientFullConnectEvent args) { }

	/// <summary>
	/// Called when a client disconnects from the server.
	/// </summary>
	void OnClientDisconnect(ClientDisconnectedEvent args) { }

	/// <summary>
	/// Called when an entity is created.
	/// </summary>
	void OnEntityCreated(EntityCreatedEvent args) { }

	/// <summary>
	/// Called when an entity has been fully spawned.
	/// </summary>
	void OnEntitySpawned(EntitySpawnedEvent args) { }

	/// <summary>
	/// Called when an entity is deleted.
	/// </summary>
	void OnEntityDeleted(EntityDeletedEvent args) { }

	/// <summary>Called when an entity starts touching another entity (trigger zone entry, collision).</summary>
	void OnEntityStartTouch(EntityTouchEvent args) { }

	/// <summary>Called when an entity stops touching another entity.</summary>
	void OnEntityEndTouch(EntityTouchEvent args) { }

	/// <summary>
	/// Called each think tick before ability execution.
	/// Set <see cref="AbilityAttemptEvent.BlockedButtons"/> to prevent specific abilities/items from being cast.
	/// </summary>
	void OnAbilityAttempt(AbilityAttemptEvent args) { }

	/// <summary>Called when a player's usercmds are being processed.</summary>
	void OnProcessUsercmds(ProcessUsercmdsEvent args) { }

	/// <summary>Called after the plugin's config has been reloaded via <c>dw_reloadconfig</c>.</summary>
	void OnConfigReloaded() { }

	/// <summary>
	/// Called when a modifier is about to be applied to an entity.
	/// Return Stop to block the modifier from being applied.
	/// </summary>
	HookResult OnAddModifier(AddModifierEvent args) => HookResult.Continue;

	/// <summary>
	/// Called per-player each tick after the engine builds the default transmit list.
	/// Use <see cref="CheckTransmitEvent.Hide"/> to prevent entities from being networked to this player.
	/// </summary>
	void OnCheckTransmit(CheckTransmitEvent args) { }

	/// <summary>
	/// Called whenever a pawn's hero abilities and modifiers have just been (re)populated server-side.
	/// </summary>
	void OnPawnHeroInitialized(CCitadelPlayerPawn pawn) { }
}
