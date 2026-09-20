using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Deadlock-specific player controller. Provides access to player data, hero selection, team changes, and console messaging.</summary>
[NativeClass("CCitadelPlayerController")]
public sealed unsafe class CCitadelPlayerController : CBasePlayerController {
	internal CCitadelPlayerController(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _playerDataGlobal = new("CCitadelPlayerController"u8, "m_PlayerDataGlobal"u8);
	public PlayerDataGlobal PlayerDataGlobal => new(_playerDataGlobal.GetAddress(Handle));

	/// <summary>Returns the player's current hero pawn, or null if they have none.</summary>
	public CCitadelPlayerPawn? GetHeroPawn() {
		var ptr = NativeInterop.GetHeroPawn((void*)Handle);
		return ptr != null ? new CCitadelPlayerPawn((nint)ptr) : null;
	}

	/// <summary>
	/// Moves this player to the specified team, keeping the pawn's hero, abilities and items.
	/// Pass <paramref name="keepHero"/> false for the server's own behaviour, which destroys
	/// all three on a real team change and leaves the pawn alive but inert.
	/// </summary>
	public void ChangeTeam(int teamNum, bool keepHero = true) {
		NativeInterop.ChangeTeam((void*)Handle, teamNum, keepHero ? (byte)1 : (byte)0);
	}

	/// <summary>
	/// Forcibly removes the player's current pawn, spawns an observer pawn, and attaches it.
	/// </summary>
	public void MakeObserver() {
		Pawn?.Remove();
		SetPawn(null, retainOldPawnTeam: true);
		NativeInterop.SpawnObserverPawn((void*)Handle);
	}

	/// <summary>Forces the player to select the specified hero.</summary>
	public void SelectHero(Heroes hero) {
		var name = hero.ToHeroName();
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.SelectHero((void*)Handle, ptr);
		}
	}

	/// <summary>Sends a message to this player's console via "echo" client command.</summary>
	public void PrintToConsole(string message) {
		Server.ClientCommand(Slot, $"echo {message}");
	}

	/// <summary>Displays a HUD game announcement banner to this player with the given title and description.</summary>
	public void HudAnnounce(string title = "", string description = "") {
		NetMessages.Send(new CCitadelUserMsg_HudGameAnnouncement {
			TitleLocstring = title,
			DescriptionLocstring = description
		}, Recipients);
	}

	/// <summary>
	/// Forces this player's camera to face <paramref name="angles"/> (pitch, yaw, roll in degrees).
	/// </summary>
	/// <remarks>
	/// The <c>angles</c> argument of <see cref="CBaseEntity.Teleport"/> has no effect on a player pawn's
	/// view because the client owns its own view angles. The engine only honours a server-driven
	/// change when it arrives as a <c>CCitadelUserMsg_SetClientCameraAngles</c> message, which is
	/// what this method sends. Call it after teleporting a player to a checkpoint, spawn point or
	/// saved location so they end up looking the intended way.
	/// </remarks>
	public void SetCameraAngles(Vector3 angles) {
		NetMessages.Send(new CCitadelUserMsg_SetClientCameraAngles {
			PlayerSlot = Slot,
			CameraAngles = new CMsgQAngle { X = angles.X, Y = angles.Y, Z = angles.Z }
		}, Recipients);
	}

	/// <inheritdoc cref="SetCameraAngles(Vector3)"/>
	public void SetCameraAngles(float pitch, float yaw, float roll = 0f) => SetCameraAngles(new Vector3(pitch, yaw, roll));

	/// <summary>
	/// Teleports this player's hero pawn to <paramref name="position"/> and points their camera at
	/// <paramref name="angles"/> in one call. Velocity is zeroed so the player does not carry momentum
	/// through the teleport. Does nothing if the player has no hero pawn.
	/// </summary>
	public void TeleportWithView(Vector3 position, Vector3 angles) {
		var pawn = GetHeroPawn();
		if (pawn == null || !pawn.IsValid) return;
		pawn.Teleport(position, velocity: Vector3.Zero);
		SetCameraAngles(angles);
	}

	/// <summary>Sends a message to all connected players' consoles.</summary>
	public static void PrintToConsoleAll(string message) {
		foreach (var player in Players.GetAll())
			player.PrintToConsole(message);
	}
}
