using System.Numerics;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// AI of a bot hero (<c>npc_player_bot_brain</c>), a hidden NPC. Get it from <see cref="CCitadelPlayerPawn.BotBrain"/>.
/// </summary>
/// <remarks>
/// With <see cref="Bots.AiEnabled"/> on, the brain moves the hero itself and may drop a
/// <see cref="CAI_BaseNPC.MoveTo"/> order for its own plans, such as a zipline. With it off, call
/// <see cref="ApplyMovement"/> to walk the hero along the brain's path.
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

	/// <summary>Hero this brain drives.</summary>
	public CCitadelPlayerPawn? Pawn {
		get {
			void* pawn = NativeInterop.BotGetPawn((void*)Handle);
			return pawn != null ? new CCitadelPlayerPawn((nint)pawn) : null;
		}
	}

	/// <summary>
	/// Fills in a usercmd that walks the bot along its brain's path. Returns false, leaving the usercmd alone, if the
	/// brain has nowhere to go.
	/// </summary>
	/// <remarks>
	/// Call on each of the bot's usercmds in <see cref="IDeadworksPlugin.OnProcessUsercmds"/>, with
	/// <see cref="Bots.AiEnabled"/> off, after <see cref="CAI_BaseNPC.MoveTo"/>. The first usercmd after
	/// <c>MoveTo</c> also returns false: treat only several false results in a row as arrival. The bot stops about
	/// 50 units from the goal.
	/// </remarks>
	/// <param name="usercmd">Usercmd to fill in.</param>
	/// <param name="lookAt">Point to aim at while moving. Null faces the direction of travel.</param>
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
