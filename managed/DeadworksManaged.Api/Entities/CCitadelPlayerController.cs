using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Deadlock-specific player controller. Provides access to player data, hero selection, team changes, and console messaging.</summary>
[NativeClass("CCitadelPlayerController")]
public sealed unsafe class CCitadelPlayerController : CBasePlayerController {
	internal CCitadelPlayerController(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _playerDataGlobal = new("CCitadelPlayerController"u8, "m_PlayerDataGlobal"u8);
	public PlayerDataGlobal PlayerDataGlobal => new(_playerDataGlobal.GetAddress(Handle), Handle);

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

	private static readonly SchemaAccessor<uint> _hHeroPawn = new("CCitadelPlayerController"u8, "m_hHeroPawn"u8);

	/// <summary>
	/// Gives this player <paramref name="from"/>'s hero, as it is: level, souls, items, health and position carry over,
	/// along with its team and lane. Use it to hand a leaver's hero to someone else. This player's old pawn (usually a
	/// spectator's) is removed, and <paramref name="from"/> is left without one.
	/// </summary>
	/// <returns>False if <paramref name="from"/> has no hero.</returns>
	public bool TakeOverHero(CCitadelPlayerController from) {
		if (from == this || from.GetHeroPawn() is not { } hero)
			return false;

		var previous = Pawn;
		ChangeTeam(hero.TeamNum);
		SetPawn(hero);
		_hHeroPawn.Set(Handle, hero.EntityHandle);
		if (previous != null && previous.EntityHandle != hero.EntityHandle)
			previous.Remove();

		// Raw writes on purpose: SetPawn(null) on the old owner would reach into the pawn it just lost.
		from.ClearPawnHandle();
		_hHeroPawn.Set(from.Handle, CBaseEntity.InvalidEntityHandle);

		PlayerDataGlobal.HeroID = from.PlayerDataGlobal.HeroID;
		AssignedLane = from.AssignedLane;
		OriginalLaneAssignment = from.OriginalLaneAssignment;
		return true;
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

	/// <summary>Prints a message in this player's console, one <c>echo</c> per line.</summary>
	public void PrintToConsole(string message) {
		foreach (var line in message.ReplaceLineEndings("\n").Split('\n'))
			Server.ClientCommand(Slot, EchoCommand(line));
	}

	/// <summary>
	/// Builds the <c>echo</c> for one line. A <c>;</c> in a player's name or a kick reason would end the echo and run
	/// the rest on the client as a new command, so it becomes a full-width semicolon, which looks the same. Deadlock's
	/// echo prints quotes literally, so quoting isn't an option; the console's own UM_TextMsg isn't shown by the client.
	/// </summary>
	internal static string EchoCommand(string line) => $"echo {line.Replace(';', '；')}";

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
