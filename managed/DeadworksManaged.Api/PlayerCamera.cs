using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Server-driven control over a player's third-person camera, built on
/// <c>CCitadelUserMsg_CameraController</c>. Obtain via <see cref="CCitadelPlayerController.Camera"/>.
/// </summary>
/// <remarks>
/// <para>
/// The client camera runs a stack of "operations" (lerp, spring, approach, lag, maintain), each
/// targeting one <see cref="CameraParam"/> such as the target position, distance or field of view.
/// Operations are grouped by a numeric <b>context</b> so that a plugin can clear its own operations
/// without disturbing the game's. Every method here takes an optional context and defaults to
/// <see cref="DefaultContext"/>, which is far outside the range the game uses.
/// </para>
/// <para>
/// This is the only known way to move the camera independently of the pawn from the server; there is
/// no server-side view entity for players. The client still culls the world around the pawn, so a
/// camera moved far from the pawn will see missing geometry unless the pawn is moved along with it.
/// </para>
/// </remarks>
public readonly struct PlayerCamera {
	/// <summary>Context id used when none is supplied. Chosen to avoid colliding with the game's own operations.</summary>
	public const uint DefaultContext = 0x44570001;

	/// <summary>Priority used when none is supplied. Higher than the game's default of 1 so plugin operations win.</summary>
	public const uint DefaultPriority = 30;

	private readonly CCitadelPlayerController _player;

	internal PlayerCamera(CCitadelPlayerController player) => _player = player;

	/// <summary>
	/// Smoothly interpolates a vector camera parameter (normally <see cref="CameraParam.KEparamTargetPosition"/>)
	/// from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/> seconds.
	/// </summary>
	/// <param name="param">Which camera parameter to drive. Only <see cref="CameraParam.KEparamTargetPosition"/> takes a vector.</param>
	/// <param name="from">Start value.</param>
	/// <param name="to">End value.</param>
	/// <param name="duration">Seconds the interpolation takes.</param>
	/// <param name="bias">Interpolation bias in [0,1]; 0.5 is linear.</param>
	/// <param name="gain">Interpolation gain in [0,1]; 0.5 is linear.</param>
	/// <param name="delay">Seconds to wait before the operation starts. Lets a sequence of segments be queued ahead of time.</param>
	/// <param name="context">Operation context; see the type remarks.</param>
	/// <param name="priority">Operation priority; higher wins when several operations target the same parameter.</param>
	public void Lerp(CameraParam param, Vector3 from, Vector3 to, float duration,
	                 float bias = 0.5f, float gain = 0.5f, float delay = 0f,
	                 uint context = DefaultContext, uint priority = DefaultPriority) {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionAddOp,
			Operation = CameraOperation.KEcameraOpLerp,
			Param = param,
			ParamMode = CameraParamMode.KEparamModeAllowInMultipleContexts,
			Delay = delay,
			ContextSymbolId = context,
			Priority = priority,
			Lerp = new CCitadelUserMsg_CameraController.Types.Lerp {
				StartVector = ToMsg(from),
				EndVector = ToMsg(to),
				Duration = duration,
				Bias = bias,
				Gain = gain,
			}
		});
	}

	/// <summary>
	/// Smoothly interpolates a scalar camera parameter (distance, FOV, vertical or horizontal offset)
	/// from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/> seconds.
	/// </summary>
	/// <param name="param">Which camera parameter to drive.</param>
	/// <param name="from">Start value.</param>
	/// <param name="to">End value.</param>
	/// <param name="duration">Seconds the interpolation takes.</param>
	/// <param name="bias">Interpolation bias in [0,1]; 0.5 is linear.</param>
	/// <param name="gain">Interpolation gain in [0,1]; 0.5 is linear.</param>
	/// <param name="delay">Seconds to wait before the operation starts.</param>
	/// <param name="context">Operation context; see the type remarks.</param>
	/// <param name="priority">Operation priority; higher wins when several operations target the same parameter.</param>
	public void Lerp(CameraParam param, float from, float to, float duration,
	                 float bias = 0.5f, float gain = 0.5f, float delay = 0f,
	                 uint context = DefaultContext, uint priority = DefaultPriority) {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionAddOp,
			Operation = CameraOperation.KEcameraOpLerp,
			Param = param,
			ParamMode = CameraParamMode.KEparamModeAllowInMultipleContexts,
			Delay = delay,
			ContextSymbolId = context,
			Priority = priority,
			Lerp = new CCitadelUserMsg_CameraController.Types.Lerp {
				StartFloat = from,
				EndFloat = to,
				Duration = duration,
				Bias = bias,
				Gain = gain,
			}
		});
	}

	/// <summary>Moves the camera's look-at target from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/> seconds.</summary>
	public void LerpTarget(Vector3 from, Vector3 to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraParam.KEparamTargetPosition, from, to, duration, delay: delay, context: context);

	/// <summary>Changes the camera distance from its target over <paramref name="duration"/> seconds.</summary>
	public void LerpDistance(float from, float to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraParam.KEparamDistance, from, to, duration, delay: delay, context: context);

	/// <summary>Changes the camera field of view over <paramref name="duration"/> seconds.</summary>
	public void LerpFov(float from, float to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraParam.KEparamFov, from, to, duration, delay: delay, context: context);

	/// <summary>Holds the current value of <paramref name="param"/> for <paramref name="duration"/> seconds.</summary>
	public void Maintain(CameraParam param, float duration, float delay = 0f,
	                     uint context = DefaultContext, uint priority = DefaultPriority) {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionAddOp,
			Operation = CameraOperation.KEcameraOpMaintain,
			Param = param,
			ParamMode = CameraParamMode.KEparamModeAllowInMultipleContexts,
			Delay = delay,
			ContextSymbolId = context,
			Priority = priority,
			Maintain = new CCitadelUserMsg_CameraController.Types.Maintain { Duration = duration }
		});
	}

	/// <summary>Removes every camera operation that was added under <paramref name="context"/>, returning those parameters to the game.</summary>
	public void Clear(uint context = DefaultContext) {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionClearOpsForContext,
			Param = CameraParam.KEparamClearAllOpsForContext,
			ContextSymbolId = context,
		});
	}

	/// <summary>Removes every camera operation in every context, including the game's own. Prefer <see cref="Clear"/>.</summary>
	public void ClearAll() {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionClearAllOps,
			Param = CameraParam.KEparamClearAllOps,
		});
	}

	private void Send(CCitadelUserMsg_CameraController msg) => NetMessages.Send(msg, _player.Recipients);

	private static CMsgVector ToMsg(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
