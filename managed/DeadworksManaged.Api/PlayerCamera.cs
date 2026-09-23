using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Moves a player's camera without moving their hero: pull it back, zoom, point it somewhere else and hold it
/// there. Get one from <see cref="CCitadelPlayerController.Camera"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every call is a timed change to one <see cref="CameraSetting"/>: a lerp moves it from one value to another,
/// and <see cref="Maintain"/> holds it where it is. When a change ends, the setting snaps back to what the game
/// wants, so follow a lerp with <see cref="Maintain"/> to keep its end value. Pass a <c>delay</c> to line up
/// several changes back to back in one go, like the legs of a fly-through.
/// </para>
/// <para>
/// Your changes go in a group, <see cref="DefaultContext"/> unless you pass your own id, and <see cref="Clear"/>
/// removes one group without touching the game's own camera effects. When two changes to the same setting
/// overlap, the higher priority wins, and <see cref="DefaultPriority"/> beats the game's.
/// </para>
/// <para>
/// The camera still bumps into walls, so pointing it at something behind geometry pulls it in close. And the
/// world is still loaded around the hero, not the camera, so a camera sent far away can show missing scenery.
/// Move the hero along with it if that matters.
/// </para>
/// </remarks>
/// <example>
/// Pull the camera back over 2 seconds, keep it there for 5, then give it back to the game:
/// <code>
/// var camera = player.Camera;
/// camera.LerpDistance(150f, 400f, 2f);
/// camera.Maintain(CameraSetting.Distance, 5f, delay: 2f);
/// </code>
/// </example>
public readonly struct PlayerCamera {
	/// <summary>
	/// The group your changes go in when you don't pass a context. <see cref="Clear"/> with no arguments removes
	/// exactly these.
	/// </summary>
	public const uint DefaultContext = 0x44570001;

	/// <summary>The priority your changes get when you don't pass one. It's above the game's own, so yours win.</summary>
	public const uint DefaultPriority = 30;

	private readonly CCitadelPlayerController _player;

	internal PlayerCamera(CCitadelPlayerController player) => _player = player;

	/// <summary>
	/// Moves the camera's look-at point from <paramref name="from"/> to <paramref name="to"/> over
	/// <paramref name="duration"/> seconds, with full control over the motion, then snaps it back. Use
	/// <see cref="Maintain"/> to keep the end value. <see cref="LerpTarget"/> is the short form.
	/// </summary>
	/// <param name="setting">Must be <see cref="CameraSetting.Target"/>, the only setting that takes a position.</param>
	/// <param name="from">Where the move starts. Pass where the camera looks now for a smooth move.</param>
	/// <param name="to">Where the move ends.</param>
	/// <param name="duration">How long the move takes, in seconds.</param>
	/// <param name="bias">From 0 to 1. Below 0.5 starts slow and speeds up, above 0.5 starts fast and slows down.</param>
	/// <param name="gain">From 0 to 1. Above 0.5 eases in and out, below 0.5 does the opposite. 0.5 for both keeps a steady speed.</param>
	/// <param name="delay">Seconds to wait before starting.</param>
	/// <param name="context">The group this change goes in, for <see cref="Clear"/>.</param>
	/// <param name="priority">Beats overlapping changes to the same setting that have a lower priority.</param>
	/// <exception cref="ArgumentException"><paramref name="setting"/> isn't <see cref="CameraSetting.Target"/>.</exception>
	public void Lerp(CameraSetting setting, Vector3 from, Vector3 to, float duration,
	                 float bias = 0.5f, float gain = 0.5f, float delay = 0f,
	                 uint context = DefaultContext, uint priority = DefaultPriority) {
		if (setting != CameraSetting.Target)
			throw new ArgumentException($"{setting} takes a number, not a position. Use the float overload.", nameof(setting));
		Send(AddOp(CameraOperation.KEcameraOpLerp, setting, delay, context, priority, new CCitadelUserMsg_CameraController {
			Lerp = new CCitadelUserMsg_CameraController.Types.Lerp {
				StartVector = ToMsg(from),
				EndVector = ToMsg(to),
				Duration = duration,
				Bias = bias,
				Gain = gain,
			}
		}));
	}

	/// <summary>
	/// Moves a camera setting from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>
	/// seconds, then snaps it back. Use <see cref="Maintain"/> to keep the end value.
	/// </summary>
	/// <param name="setting">
	/// Any setting except <see cref="CameraSetting.Target"/>, which takes positions: use the <see cref="Vector3"/>
	/// overload or <see cref="LerpTarget"/> for that.
	/// </param>
	/// <param name="from">Where the move starts. Pass the setting's current value for a smooth move.</param>
	/// <param name="to">Where the move ends.</param>
	/// <param name="duration">How long the move takes, in seconds.</param>
	/// <param name="bias">From 0 to 1. Below 0.5 starts slow and speeds up, above 0.5 starts fast and slows down.</param>
	/// <param name="gain">From 0 to 1. Above 0.5 eases in and out, below 0.5 does the opposite. 0.5 for both keeps a steady speed.</param>
	/// <param name="delay">Seconds to wait before starting.</param>
	/// <param name="context">The group this change goes in, for <see cref="Clear"/>.</param>
	/// <param name="priority">Beats overlapping changes to the same setting that have a lower priority.</param>
	/// <exception cref="ArgumentException"><paramref name="setting"/> is <see cref="CameraSetting.Target"/>.</exception>
	public void Lerp(CameraSetting setting, float from, float to, float duration,
	                 float bias = 0.5f, float gain = 0.5f, float delay = 0f,
	                 uint context = DefaultContext, uint priority = DefaultPriority) {
		if (setting == CameraSetting.Target)
			throw new ArgumentException("Target takes a position, not a number. Use the Vector3 overload or LerpTarget.", nameof(setting));
		Send(AddOp(CameraOperation.KEcameraOpLerp, setting, delay, context, priority, new CCitadelUserMsg_CameraController {
			Lerp = new CCitadelUserMsg_CameraController.Types.Lerp {
				StartFloat = from,
				EndFloat = to,
				Duration = duration,
				Bias = bias,
				Gain = gain,
			}
		}));
	}

	/// <summary>Moves the camera's look-at point from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/> seconds, then snaps it back.</summary>
	public void LerpTarget(Vector3 from, Vector3 to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraSetting.Target, from, to, duration, delay: delay, context: context);

	/// <summary>
	/// Pulls the camera back or pushes it in, from <paramref name="from"/> to <paramref name="to"/> units behind what
	/// it looks at, over <paramref name="duration"/> seconds, then snaps it back. The normal distance is 150.
	/// </summary>
	public void LerpDistance(float from, float to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraSetting.Distance, from, to, duration, delay: delay, context: context);

	/// <summary>Zooms by changing the field of view from <paramref name="from"/> to <paramref name="to"/> degrees over <paramref name="duration"/> seconds, then snaps it back.</summary>
	public void LerpFov(float from, float to, float duration, float delay = 0f, uint context = DefaultContext)
		=> Lerp(CameraSetting.Fov, from, to, duration, delay: delay, context: context);

	/// <summary>
	/// Holds a camera setting where it is for <paramref name="duration"/> seconds, then gives it back to the game.
	/// To keep a lerp's end value instead of snapping back, queue this with <paramref name="delay"/> set to the
	/// lerp's duration.
	/// </summary>
	/// <param name="setting">The setting to hold.</param>
	/// <param name="duration">How long to hold it, in seconds.</param>
	/// <param name="delay">Seconds to wait before starting.</param>
	/// <param name="context">The group this change goes in, for <see cref="Clear"/>.</param>
	/// <param name="priority">Beats overlapping changes to the same setting that have a lower priority.</param>
	public void Maintain(CameraSetting setting, float duration, float delay = 0f,
	                     uint context = DefaultContext, uint priority = DefaultPriority) {
		Send(AddOp(CameraOperation.KEcameraOpMaintain, setting, delay, context, priority, new CCitadelUserMsg_CameraController {
			Maintain = new CCitadelUserMsg_CameraController.Types.Maintain { Duration = duration }
		}));
	}

	/// <summary>
	/// Cancels every change in <paramref name="context"/> and gives those settings back to the game. The game's own
	/// camera effects are left alone.
	/// </summary>
	public void Clear(uint context = DefaultContext) {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionClearOpsForContext,
			Param = CameraParam.KEparamClearAllOpsForContext,
			ContextSymbolId = context,
		});
	}

	/// <summary>
	/// Cancels every camera change on this player, the game's own included. Prefer <see cref="Clear"/>, which only
	/// removes yours.
	/// </summary>
	public void ClearAll() {
		Send(new CCitadelUserMsg_CameraController {
			Action = CameraAction.KEactionClearAllOps,
			Param = CameraParam.KEparamClearAllOps,
		});
	}

	// Fills in the fields every added operation shares. Multiple-context mode lets operations from different
	// contexts share a parameter, which priorities and Clear(context) rely on.
	private static CCitadelUserMsg_CameraController AddOp(CameraOperation operation, CameraSetting setting, float delay,
	                                                      uint context, uint priority, CCitadelUserMsg_CameraController msg) {
		if (!Enum.IsDefined(setting))
			throw new ArgumentOutOfRangeException(nameof(setting), setting, "Not a camera setting.");
		msg.Action = CameraAction.KEactionAddOp;
		msg.Operation = operation;
		msg.Param = (CameraParam)setting;
		msg.ParamMode = CameraParamMode.KEparamModeAllowInMultipleContexts;
		msg.Delay = delay;
		msg.ContextSymbolId = context;
		msg.Priority = priority;
		return msg;
	}

	private void Send(CCitadelUserMsg_CameraController msg) => NetMessages.Send(msg, _player.Recipients);

	private static CMsgVector ToMsg(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
