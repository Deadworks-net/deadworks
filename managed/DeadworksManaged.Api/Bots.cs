using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Adds and controls bot heroes: fake clients with a hero and a <see cref="CCitadelPlayerBotNPCBrain"/>.</summary>
/// <remarks>A fake client from <see cref="Server.CreateFakeClient"/> also gets a brain, but no hero.</remarks>
public static unsafe class Bots {
	/// <summary>
	/// Adds a bot hero, like <c>citadel_create_unit</c>. Returns its pawn, or null if bots can't play the hero, the
	/// server is full or no map is loaded.
	/// </summary>
	/// <remarks>
	/// The bot joins as a fake client named <c>BotN</c>. Its hero finishes loading shortly after. Wait before reading
	/// its abilities. Remove it with <see cref="Server.Kick(int, ENetworkDisconnectionReason)"/>. It only plays while
	/// <see cref="AiEnabled"/> is on.
	/// </remarks>
	/// <param name="hero">Hero. Null picks a random one.</param>
	/// <param name="team">2 Amber, 3 Sapphire.</param>
	/// <param name="position">Spawn position, on the navmesh.</param>
	/// <param name="difficulty">Null uses <c>citadel_default_bot_difficulty</c>.</param>
	/// <param name="assignLane">Let the game pick a lane.</param>
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
	/// Fills in a usercmd that moves a bot hero towards a point. Call from
	/// <see cref="IDeadworksPlugin.OnProcessUsercmds"/> while <see cref="AiEnabled"/> is off.
	/// </summary>
	/// <param name="usercmd">The bot's usercmd.</param>
	/// <param name="pawn">The bot hero.</param>
	/// <param name="moveTo">Point to move towards. Height is ignored.</param>
	/// <param name="lookAt">Point to aim at while moving. Null faces the direction of travel.</param>
	/// <param name="jump">Press jump.</param>
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
	/// Whether bot heroes play on their own. Off by default on servers without a matchmaking lobby. Same as
	/// <c>citadel_solo_bot_match</c>.
	/// </summary>
	public static bool AiEnabled {
		get {
			byte* flag = AiFlag;
			return flag != null && *flag != 0;
		}
	}

	/// <summary>Turns the bot AI on or off for every bot. Returns false if no map is loaded.</summary>
	public static bool SetAiEnabled(bool enabled) {
		byte* flag = AiFlag;
		if (flag == null)
			return false;
		*flag = enabled ? (byte)1 : (byte)0;
		return true;
	}

	/// <summary>Every bot brain on the map.</summary>
	public static IEnumerable<CCitadelPlayerBotNPCBrain> Brains => Entities.ByClass<CCitadelPlayerBotNPCBrain>();

	private static byte* AiFlag {
		get {
			nint rules = GameRules.Pointer;
			int offset = NativeInterop.BotGetMatchFlagOffset();
			return rules != 0 && offset > 0 ? (byte*)rules + offset : null;
		}
	}
}

/// <summary>Bot skill level, from <c>scripts/bots/bot_difficulty.vdata</c>.</summary>
public enum BotDifficulty {
	/// <summary>The <c>easy</c> entry.</summary>
	Easy = 1,
	/// <summary>The <c>medium</c> entry.</summary>
	Medium = 2,
	/// <summary>The <c>hard</c> entry.</summary>
	Hard = 3,
}
