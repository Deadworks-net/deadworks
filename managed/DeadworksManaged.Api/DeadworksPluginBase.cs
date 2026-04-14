using Microsoft.Extensions.Logging;

namespace DeadworksManaged.Api;

/// <summary>
/// Optional base class for plugins. Provides direct access to <see cref="ITimer"/>
/// via a <c>Timer</c> property without needing interface casts or using aliases.
/// </summary>
public abstract class DeadworksPluginBase : IDeadworksPlugin {
	public abstract string Name { get; }
	public abstract void OnLoad(bool isReload);
	public abstract void OnUnload();

	/// <summary>Per-plugin timer service.</summary>
	protected ITimer Timer => TimerResolver.Get(this);

	/// <summary>Per-plugin logger. Uses the plugin's <see cref="Name"/> as the log category.</summary>
	protected ILogger Logger => LogResolver.Get(this);

	public virtual void OnPrecacheResources() { }
	public virtual void OnStartupServer() { }
	public virtual void OnGameFrame(bool simulating, bool firstTick, bool lastTick) { }
	public virtual HookResult OnTakeDamage(TakeDamageEvent args) => HookResult.Continue;
	public virtual HookResult OnModifyCurrency(ModifyCurrencyEvent args) => HookResult.Continue;
	public virtual HookResult OnChatMessage(ChatMessage message) => HookResult.Continue;
	public virtual HookResult OnClientConCommand(ClientConCommandEvent args) => HookResult.Continue;
	public virtual bool OnClientConnect(ClientConnectEvent args) => true;
	public virtual void OnClientPutInServer(ClientPutInServerEvent args) { }
	public virtual void OnClientFullConnect(ClientFullConnectEvent args) { }
	public virtual void OnClientDisconnect(ClientDisconnectedEvent args) { }
	public virtual void OnEntityCreated(EntityCreatedEvent args) { }
	public virtual void OnEntitySpawned(EntitySpawnedEvent args) { }
	public virtual void OnEntityDeleted(EntityDeletedEvent args) { }
	public virtual void OnEntityStartTouch(EntityTouchEvent args) { }
	public virtual void OnEntityEndTouch(EntityTouchEvent args) { }
	public virtual void OnAbilityAttempt(AbilityAttemptEvent args) { }
	public virtual void OnProcessUsercmds(ProcessUsercmdsEvent args) { }
	public virtual HookResult OnAddModifier(AddModifierEvent args) => HookResult.Continue;
	public virtual void OnConfigReloaded() { }
	public virtual void OnCheckTransmit(CheckTransmitEvent args) { }
}
