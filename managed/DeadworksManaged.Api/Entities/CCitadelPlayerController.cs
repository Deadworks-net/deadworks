using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Deadlock-specific player controller. Provides access to player data, hero selection, team changes, and console messaging.</summary>
[NativeClass("CCitadelPlayerController")]
public sealed unsafe class CCitadelPlayerController : CBasePlayerController {
	internal CCitadelPlayerController(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _playerDataGlobal = new("CCitadelPlayerController"u8, "m_PlayerDataGlobal"u8);
	public PlayerDataGlobal PlayerDataGlobal => new(_playerDataGlobal.GetAddress(Handle));

	private static readonly SchemaAccessor<sbyte> _assignedLane = new("CCitadelPlayerController"u8, "m_nAssignedLane"u8);
	/// <summary>
	/// Which lane's zipline this player rides if the match starts with the zipline launch. Setting it doesn't cause a
	/// launch, it only picks the lane. <see cref="LaneColor.Invalid"/>, the default, leaves the choice to the game. Not
	/// every map has every lane; dl_midtown has Yellow, Blue and Purple.
	/// </summary>
	public LaneColor AssignedLane { get => (LaneColor)_assignedLane.Get(Handle); set => _assignedLane.Set(Handle, (sbyte)value); }

	private static readonly SchemaAccessor<sbyte> _originalLaneAssignment = new("CCitadelPlayerController"u8, "m_nOriginalLaneAssignment"u8);
	/// <summary>The lane this player was first assigned, before any lane swap.</summary>
	public LaneColor OriginalLaneAssignment {
		get => (LaneColor)_originalLaneAssignment.Get(Handle);
		set => _originalLaneAssignment.Set(Handle, (sbyte)value);
	}

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
	/// Moves this player's camera without moving their hero: pull it back, zoom, point it somewhere else and hold it
	/// there. See <see cref="PlayerCamera"/>.
	/// </summary>
	public PlayerCamera Camera => new(this);

	/// <summary>
	/// Turns this player's camera to face <paramref name="angles"/> (pitch, yaw, roll in degrees).
	/// </summary>
	/// <remarks>
	/// <see cref="CBaseEntity.Teleport"/> can't turn a player's camera (its <c>angles</c> are ignored for players),
	/// so call this after moving someone to control which way they end up facing, or use
	/// <see cref="CCitadelPlayerPawn.TeleportWithView"/> to do both at once. To restore a view you saved
	/// earlier, save <see cref="CCitadelPlayerPawn.CameraAngles"/>. <see cref="CCitadelPlayerPawn.EyeAngles"/>
	/// is where the crosshair aims, which can be far off from where the camera points.
	/// </remarks>
	public void SetCameraAngles(Vector3 angles) {
		NetMessages.Send(new CCitadelUserMsg_SetClientCameraAngles {
			PlayerSlot = Slot,
			CameraAngles = new CMsgQAngle { X = angles.X, Y = angles.Y, Z = angles.Z }
		}, Recipients);
	}

	/// <inheritdoc cref="SetCameraAngles(Vector3)"/>
	public void SetCameraAngles(float pitch, float yaw, float roll = 0f) => SetCameraAngles(new Vector3(pitch, yaw, roll));

	/// <summary>Sends a message to all connected players' consoles.</summary>
	public static void PrintToConsoleAll(string message) {
		foreach (var player in Players.GetAll())
			player.PrintToConsole(message);
	}
}
