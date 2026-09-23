namespace DeadworksManaged.Api;

/// <summary>Base player pawn entity. Provides access to the owning controller.</summary>
[NativeClass("CBasePlayerPawn")]
public unsafe class CBasePlayerPawn : CBaseEntity {
	internal CBasePlayerPawn(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<uint> _hController = new("CBasePlayerPawn"u8, "m_hController"u8);
	public CBasePlayerController? Controller {
		get {
			uint handle = _hController.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CBasePlayerController((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<nint> _pObserverServices = new("CBasePlayerPawn"u8, "m_pObserverServices"u8);
	/// <summary>The observer/spectator state component on this pawn, or null if not allocated.</summary>
	public CPlayer_ObserverServices? ObserverServices {
		get {
			nint ptr = _pObserverServices.Get(Handle);
			return ptr != 0 ? new CPlayer_ObserverServices(ptr) : null;
		}
	}

	/// <summary>Current observer mode, or <see cref="ObserverMode_t.None"/> if no services are attached.</summary>
	public ObserverMode_t ObserverMode => ObserverServices?.ObserverMode ?? ObserverMode_t.None;

	/// <summary>Currently observed entity, or null if no target / no services.</summary>
	public CBaseEntity? ObserverTarget => ObserverServices?.ObserverTarget;

	/// <inheritdoc cref="CPlayer_ObserverServices.SetObserverMode"/>
	public void SetObserverMode(ObserverMode_t mode) => ObserverServices?.SetObserverMode(mode);

	/// <inheritdoc cref="CPlayer_ObserverServices.SetObserverTarget"/>
	public bool SetObserverTarget(CBaseEntity? target) => ObserverServices?.SetObserverTarget(target) ?? false;

	/// <inheritdoc cref="CPlayer_ObserverServices.IsValidObserverTarget"/>
	public bool IsValidObserverTarget(CBaseEntity? target) => ObserverServices?.IsValidObserverTarget(target) ?? false;

	/// <summary>
	/// Respawns this pawn immediately, the same way the <c>respawn</c> cheat command does.
	/// It behaves like a normal respawn, including firing the <c>player_respawned</c> event.
	/// </summary>
	/// <param name="releaseButtons">Release any buttons the player is holding, so they don't keep moving or
	/// shooting after the respawn.</param>
	public void ForceRespawn(bool releaseButtons = true) {
		if (NativeInterop.ForceRespawn != null)
			NativeInterop.ForceRespawn((void*)Handle, releaseButtons ? (byte)1 : (byte)0);
	}
}
