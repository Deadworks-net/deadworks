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

	/// <summary>Sends a message to all connected players' consoles.</summary>
	public static void PrintToConsoleAll(string message) {
		foreach (var player in Players.GetAll())
			player.PrintToConsole(message);
	}
}
