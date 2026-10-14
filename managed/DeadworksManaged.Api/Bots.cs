using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Bot heroes: fake clients with a hero, a bot object and a brain (<see cref="CCitadelPlayerBotNPCBrain"/>).
/// </summary>
/// <remarks>
/// Add one with <see cref="Add"/>, which goes through the game's own bot creation: the hero is loaded, the bot
/// AI attached and a lane picked the way practice bots get theirs. A fake client from
/// <see cref="Server.CreateFakeClient"/> also gets bot AI attached, but no hero until a plugin gives it one.
/// </remarks>
public static unsafe class Bots {
	/// <summary>
	/// Adds a bot hero, the way <c>citadel_create_unit</c> does. Returns its pawn, or null when the hero is not
	/// allowed for bots in this build, the server is full, or no map is loaded (the game logs which).
	/// </summary>
	/// <remarks>
	/// <para>
	/// The bot is a fake client named <c>BotN</c>. It joins <paramref name="team"/> at
	/// <paramref name="position"/>; the hero finishes loading a moment later, so wait before reading its
	/// abilities. Remove it with <see cref="Server.Kick(int, ENetworkDisconnectionReason)"/>.
	/// </para>
	/// <para>
	/// Whether it then plays on its own depends on <see cref="AiEnabled"/>, off by default on a community
	/// server.
	/// </para>
	/// </remarks>
	/// <param name="hero">The hero to play, or null for a random one bots are allowed to play.</param>
	/// <param name="team">The team number (2 Amber, 3 Sapphire).</param>
	/// <param name="position">Where the hero appears. Use a point on the navmesh, such as one from
	/// <see cref="Nav.GetNearestPoint(Vector3, float, int)"/>, and not in the enemy base, which kills it within
	/// seconds.</param>
	/// <param name="difficulty">How well it plays. Null uses <c>citadel_default_bot_difficulty</c>.</param>
	/// <param name="assignLane">Let the game pick its lane, as for practice bots. Set one yourself with
	/// <see cref="CCitadelPlayerPawn.SetBotLane"/>.</param>
	public static CCitadelPlayerPawn? Add(Heroes? hero, int team, Vector3 position, BotDifficulty? difficulty = null, bool assignLane = true) {
		nint gameRules = GameRules.Pointer;
		if (gameRules == 0)
			return null;
		string name = hero?.ToHeroName() ?? "none";

		// The bot reads its difficulty from this convar when the game attaches it, inside the create call.
		ConVar? difficultyVar = difficulty != null ? ConVar.Find("citadel_default_bot_difficulty") : null;
		int previous = difficultyVar?.GetInt() ?? 0;
		difficultyVar?.SetInt((int)difficulty!.Value);
		try {
			Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
			float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
			fixed (byte* namePtr = utf8) {
				void* pawn = NativeInterop.BotCreate((void*)gameRules, namePtr, team, pos, assignLane ? (byte)1 : (byte)0);
				return pawn != null ? new CCitadelPlayerPawn((nint)pawn) : null;
			}
		} finally {
			difficultyVar?.SetInt(previous);
		}
	}

	/// <summary>
	/// Fills in a usercmd so a bot hero moves towards <paramref name="moveTo"/>: forwardmove and leftmove,
	/// view angles and the matching movement keys. Use it from <see cref="IDeadworksPlugin.OnProcessUsercmds"/>
	/// to drive a bot with your own steering, for example along a <see cref="NavPathFollower"/>, while
	/// <see cref="AiEnabled"/> is off. To follow the brain's own path instead, use
	/// <see cref="CCitadelPlayerBotNPCBrain.ApplyMovement"/>.
	/// </summary>
	/// <param name="usercmd">The bot's usercmd.</param>
	/// <param name="pawn">The bot hero.</param>
	/// <param name="moveTo">The point to move towards. Height is ignored.</param>
	/// <param name="lookAt">A point to keep looking at while moving (it strafes or backpedals). Null looks
	/// where it is going.</param>
	/// <param name="jump">Press jump this command, for example when <see cref="NavPathFollower.ShouldJump"/>.</param>
	public static void SetMoveInput(CCitadelUserCmdPB usercmd, CCitadelPlayerPawn pawn, Vector3 moveTo, Vector3? lookAt = null, bool jump = false) {
		var from = pawn.Position;
		var move = new Vector2(moveTo.X - from.X, moveTo.Y - from.Y);
		float moveYaw = MathF.Atan2(move.Y, move.X);
		float viewYaw = moveYaw, viewPitch = 0f;
		if (lookAt is { } target) {
			var toTarget = target - pawn.EyePosition;
			viewYaw = MathF.Atan2(toTarget.Y, toTarget.X);
			viewPitch = -MathF.Atan2(toTarget.Z, MathF.Sqrt(toTarget.X * toTarget.X + toTarget.Y * toTarget.Y));
		}
		float forward = 0f, side = 0f;
		if (move.LengthSquared() > 1f) {
			forward = MathF.Cos(moveYaw - viewYaw);
			side = MathF.Sin(moveYaw - viewYaw);
		}
		WriteMove(usercmd, forward, side, viewPitch * (180f / MathF.PI), viewYaw * (180f / MathF.PI), jump ? (ulong)InputButton.Jump : 0);
	}

	/// <summary>
	/// Writes movement into a usercmd the way the game builds a bot's: analog move, view angles, and the
	/// direction keys that go with the move. A bot's usercmd starts blank each tick, so everything held counts
	/// as changed too.
	/// </summary>
	internal static void WriteMove(CCitadelUserCmdPB usercmd, float forward, float side, float pitch, float yaw, ulong buttons) {
		const float deadZone = 0.01f;
		if (MathF.Abs(forward) < deadZone) forward = 0f;
		if (MathF.Abs(side) < deadZone) side = 0f;
		var cmd = usercmd.Base ??= new CBaseUserCmdPB();
		cmd.Forwardmove = forward;
		cmd.Leftmove = side;
		cmd.Viewangles = new CMsgQAngle { X = pitch, Y = yaw, Z = 0f };
		if (forward != 0f) buttons |= (ulong)(forward > 0f ? InputButton.Forward : InputButton.Back);
		if (side != 0f) buttons |= (ulong)(side > 0f ? InputButton.MoveLeft : InputButton.MoveRight);
		var state = cmd.ButtonsPb ??= new CInButtonStatePB();
		state.Buttonstate1 |= buttons;
		state.Buttonstate2 |= buttons;
	}

	/// <summary>
	/// Whether bot heroes play on their own: take ziplines, push their lane, fight. Off by default on a server
	/// with no matchmaking lobby, where the game treats bots as inert dummies and sends them blank input.
	/// </summary>
	/// <remarks>
	/// This is the game rules' "bot match" flag, the same thing the <c>citadel_solo_bot_match</c> convar turns
	/// on, without touching a replicated convar.
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

/// <summary>How well a bot hero plays (<c>scripts/bots/bot_difficulty.vdata</c>).</summary>
public enum BotDifficulty {
	/// <summary>The <c>easy</c> entry in bot_difficulty.vdata.</summary>
	Easy = 1,
	/// <summary>The <c>medium</c> entry.</summary>
	Medium = 2,
	/// <summary>The <c>hard</c> entry.</summary>
	Hard = 3,
}
