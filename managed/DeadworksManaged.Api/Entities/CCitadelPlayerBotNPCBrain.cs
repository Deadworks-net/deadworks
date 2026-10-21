using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// The brain of a bot hero (<c>npc_player_bot_brain</c>): a hidden NPC that plans the hero's movement and
/// schedules. Get it from <see cref="CCitadelPlayerPawn.BotBrain"/>.
/// </summary>
/// <remarks>
/// While <see cref="Bots.AiEnabled"/> is on, the brain drives the hero itself and may drop a
/// <see cref="CAI_BaseNPC.MoveTo"/> order for its own plans, such as a zipline. While it is off, the brain still
/// plans the path; <see cref="ApplyMovement"/> walks the hero along it.
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
	/// Fills in a usercmd to walk the bot along its brain's path. Returns false, leaving the usercmd alone, if
	/// the brain has nowhere to go.
	/// </summary>
	/// <remarks>
	/// Call from <see cref="IDeadworksPlugin.OnProcessUsercmds"/> on each of the bot's usercmds while
	/// <see cref="Bots.AiEnabled"/> is off, after <see cref="CAI_BaseNPC.MoveTo"/>. The bot stops about 50 units
	/// from the goal.
	/// </remarks>
	/// <param name="usercmd">The usercmd to fill in.</param>
	/// <param name="lookAt">A point to aim at while moving. Null aims where it is going.</param>
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
