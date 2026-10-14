using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// The brain of a bot hero (<c>npc_player_bot_brain</c>): an invisible NPC that follows the hero around and
/// owns its navigator and schedules. Get it from <see cref="CCitadelPlayerPawn.BotBrain"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CAI_BaseNPC.MoveTo"/> paths the bot to a point with the game's own pathfinder, and
/// <see cref="CAI_BaseNPC.Schedule"/> shows what it is doing.
/// </para>
/// <para>
/// Whether the brain's decisions reach the hero depends on <see cref="Bots.AiEnabled"/>. While it is on, the
/// brain turns its navigation into the bot's input itself and also fights. It keeps making its own decisions,
/// so a <see cref="CAI_BaseNPC.MoveTo"/> order may be dropped for a zipline or a lane push a moment later.
/// While it is off (the default on a community server) the game sends the bot blank input and ignores the
/// brain: the brain still plans a <see cref="CAI_BaseNPC.MoveTo"/> path, and <see cref="ApplyMovement"/> is
/// what walks the hero along it, running at the hero's normal speed and stopping within about 50 units of the
/// goal.
/// </para>
/// </remarks>
[NativeClass("CCitadelPlayerBotNPCBrain")]
public sealed unsafe class CCitadelPlayerBotNPCBrain : CAI_CitadelNPC {
	internal CCitadelPlayerBotNPCBrain(nint handle) : base(handle) { }

	[StructLayout(LayoutKind.Sequential)]
	private struct BotMoveInput {
		public float Forward;
		public float Side;
		public ulong HeldButtons;
		public ulong PressedButtons;
		public float Pitch;
		public float Yaw;
	}

	/// <summary>The hero this brain drives.</summary>
	public CCitadelPlayerPawn? Pawn {
		get {
			void* pawn = NativeInterop.BotGetPawn((void*)Handle);
			return pawn != null ? new CCitadelPlayerPawn((nint)pawn) : null;
		}
	}

	/// <summary>
	/// Walks the bot along the path its brain is following, by filling in a usercmd with the game's own bot
	/// movement step. Call it from <see cref="IDeadworksPlugin.OnProcessUsercmds"/> for the bot's slot, on every
	/// usercmd, while <see cref="Bots.AiEnabled"/> is off. Returns false when the brain has nowhere to go, in
	/// which case the usercmd is left alone.
	/// </summary>
	/// <remarks>
	/// Give the brain somewhere to go first with <see cref="CAI_BaseNPC.MoveTo"/>; once it arrives this
	/// returns false again. The bot runs towards each waypoint.
	/// Don't call it while <see cref="Bots.AiEnabled"/> is on: the brain already sends its own input then.
	/// </remarks>
	/// <param name="usercmd">The usercmd to fill in: movement, view angles and buttons.</param>
	/// <param name="lookAt">
	/// A point for the bot to look at while it moves, so it strafes or backpedals to keep facing it. Null
	/// looks where it is going.
	/// </param>
	public bool ApplyMovement(CCitadelUserCmdPB usercmd, Vector3? lookAt = null) {
		BotMoveInput input;
		byte moving;
		if (lookAt is { } target && Pawn is { } pawn) {
			var toTarget = target - pawn.EyePosition;
			float* view = stackalloc float[2] {
				-MathF.Atan2(toTarget.Z, MathF.Sqrt(toTarget.X * toTarget.X + toTarget.Y * toTarget.Y)) * (180f / MathF.PI),
				MathF.Atan2(toTarget.Y, toTarget.X) * (180f / MathF.PI),
			};
			moving = NativeInterop.BotGetMoveInput((void*)Handle, view, &input);
		} else {
			moving = NativeInterop.BotGetMoveInput((void*)Handle, null, &input);
		}
		if (moving == 0)
			return false;

		Bots.WriteMove(usercmd, input.Forward, input.Side, input.Pitch, input.Yaw, input.HeldButtons | input.PressedButtons);
		return true;
	}
}
