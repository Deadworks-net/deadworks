namespace DeadworksManaged.Api;

/// <summary>
/// Bot heroes: fake clients with a hero, a bot object and a brain (<see cref="CCitadelPlayerBotNPCBrain"/>).
/// </summary>
/// <remarks>
/// A bot hero comes from <see cref="Server.CreateFakeClient"/> plus a team and a hero. The game attaches the
/// bot AI to any fake client's hero by itself, but only heroes whose VData allows bots can be given to one.
/// </remarks>
public static unsafe class Bots {
	/// <summary>
	/// Whether bot heroes play on their own: walk their lane, fight, cast, buy. Off by default on a server
	/// with no matchmaking lobby, where the game treats bots as inert dummies and sends them blank input.
	/// </summary>
	/// <remarks>
	/// This is the game rules' "bot match" flag, the same thing the <c>citadel_solo_bot_match</c> convar turns
	/// on, without touching a replicated convar. Bots also need the match to be in progress
	/// (<see cref="EGameState.GameInProgress"/>): in pre-game they only turn on the spot.
	/// </remarks>
	public static bool AiEnabled {
		get {
			byte* flag = AiFlag;
			return flag != null && *flag != 0;
		}
	}

	/// <summary>
	/// Turns <see cref="AiEnabled"/> on or off for every bot. Returns false when there are no game rules yet
	/// (no map loaded) or the flag could not be located in this game build.
	/// </summary>
	/// <remarks>
	/// The flag belongs to the map's game rules, so it resets on a map change. To take one bot over while the
	/// rest keep playing, leave this on and replace what that bot's brain picks in
	/// <see cref="IDeadworksPlugin.OnNpcSelectSchedule"/>, or rewrite its input in
	/// <see cref="IDeadworksPlugin.OnProcessUsercmds"/>.
	/// </remarks>
	public static bool SetAiEnabled(bool enabled) {
		byte* flag = AiFlag;
		if (flag == null)
			return false;
		*flag = enabled ? (byte)1 : (byte)0;
		return true;
	}

	/// <summary>Every bot hero's brain on the map.</summary>
	public static IEnumerable<CCitadelPlayerBotNPCBrain> Brains => Entities.ByClass<CCitadelPlayerBotNPCBrain>();

	private static byte* AiFlag {
		get {
			nint rules = GameRules.Pointer;
			int offset = NativeInterop.BotGetMatchFlagOffset();
			return rules != 0 && offset > 0 ? (byte*)rules + offset : null;
		}
	}
}
