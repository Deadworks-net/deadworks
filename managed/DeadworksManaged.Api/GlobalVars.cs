using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Provides access to CGlobalVars (engine global variables such as curtime, tickcount, etc.).
/// </summary>
public static unsafe class GlobalVars
{
	// sourcesdk/public/globalvars_base.h
	private const int kRealTime = 0x00;
	private const int kFrameCount = 0x04;
	private const int kAbsoluteFrameTime = 0x08;
	private const int kAbsoluteFrameStartTimeStdDev = 0x0C;
	private const int kMaxClients = 0x10;
	// 5 unknown ints (0x14-0x27) + function pointer (0x28-0x2F)
	private const int kCurTime = 0x30;
	private const int kFrameTime = 0x34;
	// 2 unknown floats (0x38-0x3F)
	private const int kInSimulation = 0x40;
	// bool m_bEnableAssertions = 0x41
	private const int kTickCount = 0x44;
	// 2 unknown ints (0x48-0x4F)
	private const int kSubtickFraction = 0x50;
	private const int kIntervalPerTick = 0x54;

	// CGlobalVars field offsets (from sourcesdk/public/edict.h)
	// CGlobalVarsBase ends at 0x5C, then CGlobalVars adds:
	//   string_t mapname (8 bytes), string_t startspot (8 bytes)
	//   MapLoadType_t eLoadType (4 bytes), bool mp_teamplay (1 byte + padding)
	//   int maxEntities (4 bytes), int serverCount (4 bytes)

	// Without the native callback (tests, or before startup) there are no globals; every property handles null.
	private static byte* Get()
		=> NativeInterop.GetGlobalVars == null ? null : (byte*)NativeInterop.GetGlobalVars();

	/// <summary>Whether the global vars pointer is currently available.</summary>
	public static bool IsValid => Get() != null;

	/// <summary>Absolute time (per frame, not high-precision). Use for render-related timing.</summary>
	public static float RealTime { get { var p = Get(); return p != null ? *(float*)(p + kRealTime) : 0f; } }

	/// <summary>Absolute frame counter - continues to increase even if game is paused.</summary>
	public static int FrameCount { get { var p = Get(); return p != null ? *(int*)(p + kFrameCount) : 0; } }

	/// <summary>Non-paused frame time.</summary>
	public static float AbsoluteFrameTime { get { var p = Get(); return p != null ? *(float*)(p + kAbsoluteFrameTime) : 0f; } }

	/// <summary>Maximum number of connected clients.</summary>
	public static int MaxClients { get { var p = Get(); return p != null ? *(int*)(p + kMaxClients) : 0; } }

	/// <summary>Current server simulation time.</summary>
	public static float CurTime { get { var p = Get(); return p != null ? *(float*)(p + kCurTime) : 0f; } }

	/// <summary>Time spent on the last server frame.</summary>
	public static float FrameTime { get { var p = Get(); return p != null ? *(float*)(p + kFrameTime) : 0f; } }

	/// <summary>Whether the engine is currently in simulation (not rendering).</summary>
	public static bool InSimulation { get { var p = Get(); return p != null && *(bool*)(p + kInSimulation); } }

	/// <summary>Simulation tick count - does not increase when game is paused.</summary>
	public static int TickCount { get { var p = Get(); return p != null ? *(int*)(p + kTickCount) : 0; } }

	/// <summary>Subtick fraction for the current movement processing.</summary>
	public static float SubtickFraction { get { var p = Get(); return p != null ? *(float*)(p + kSubtickFraction) : 0f; } }

	/// <summary>Simulation tick interval (seconds per tick).</summary>
	public static float IntervalPerTick { get { var p = Get(); return p != null ? *(float*)(p + kIntervalPerTick) : 0f; } }
}
