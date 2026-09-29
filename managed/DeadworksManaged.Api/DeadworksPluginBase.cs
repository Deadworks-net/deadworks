namespace DeadworksManaged.Api;

/// <summary>
/// Optional base class for plugins. Provides direct access to <see cref="ITimer"/>
/// via a <c>Timer</c> property without needing interface casts or using aliases.
/// </summary>
public abstract class DeadworksPluginBase : IDeadworksPlugin {
	/// <inheritdoc/>
	public abstract string Name { get; }

	// Virtual, not abstract: a plugin whose work is all attributes and event
	// hooks has nothing to do here, and should not have to write an empty
	// method to say so. Name is the only member you must supply.
	/// <inheritdoc/>
	public virtual void OnLoad(bool isReload) { }
	/// <inheritdoc/>
	public virtual void OnUnload() { }

	/// <summary>Per-plugin timer service.</summary>
	protected ITimer Timer => TimerResolver.Get(this);

	/// <summary>Content addons this plugin needs connecting clients to download.</summary>
	public virtual IReadOnlyList<string> ContentAddons => [];

	/// <inheritdoc/>
	public virtual void OnPrecacheResources() { }
	/// <inheritdoc/>
	public virtual void OnStartupServer() { }
	/// <inheritdoc/>
	public virtual void OnGameFrame(bool simulating, bool firstTick, bool lastTick) { }
	/// <inheritdoc/>
	public virtual HookResult OnTakeDamage(TakeDamageEvent args) => HookResult.Continue;
	/// <inheritdoc/>
	public virtual HookResult OnModifyCurrency(ModifyCurrencyEvent args) => HookResult.Continue;
	/// <inheritdoc/>
	public virtual HookResult OnChatMessage(ChatMessage message) => HookResult.Continue;
	/// <inheritdoc/>
	public virtual HookResult OnClientConCommand(ClientConCommandEvent args) => HookResult.Continue;
	/// <inheritdoc/>
	public virtual bool OnClientConnect(ClientConnectEvent args) => true;
	/// <inheritdoc/>
	public virtual void OnClientPutInServer(ClientPutInServerEvent args) { }
	/// <inheritdoc/>
	public virtual void OnClientFullConnect(ClientFullConnectEvent args) { }
	/// <inheritdoc/>
	public virtual void OnClientDisconnecting(ClientDisconnectedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnClientDisconnect(ClientDisconnectedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnEntityCreated(EntityCreatedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnEntitySpawned(EntitySpawnedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnEntityDeleted(EntityDeletedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnEntityStartTouch(EntityTouchEvent args) { }
	/// <inheritdoc/>
	public virtual void OnEntityEndTouch(EntityTouchEvent args) { }
	/// <inheritdoc/>
	public virtual void OnModifierEvent(ModifierEvent args) { }
	/// <inheritdoc/>
	public virtual void OnAbilityAttempt(AbilityAttemptEvent args) { }
	/// <inheritdoc/>
	public virtual void OnProcessUsercmds(ProcessUsercmdsEvent args) { }
	/// <inheritdoc/>
	public virtual HookResult OnAddModifier(AddModifierEvent args) => HookResult.Continue;
	/// <inheritdoc/>
	public virtual void OnConfigReloaded() { }
	/// <inheritdoc/>
	public virtual void OnCheckTransmit(CheckTransmitEvent args) { }
	/// <inheritdoc/>
	public virtual void OnPawnHeroInitialized(CCitadelPlayerPawn pawn) { }
	/// <inheritdoc/>
	public virtual void OnGameStateChanged(EGameState newState) { }
	/// <inheritdoc/>
	public virtual bool OnGameStateChanging(EGameState currentState, EGameState newState) => true;
	/// <inheritdoc/>
	public virtual void OnClientAuthorized(ClientAuthorizedEvent args) { }
	/// <inheritdoc/>
	public virtual void OnPermissionsChanged(ulong? steamId64) { }
	/// <inheritdoc/>
	public virtual void OnPenaltyAdded(Penalty penalty) { }
	/// <inheritdoc/>
	public virtual void OnPenaltyRemoved(Penalty penalty) { }
	/// <inheritdoc/>
	public virtual void OnAdminAction(AdminLogEntry entry) { }
}
