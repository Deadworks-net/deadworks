using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Server-side utilities for sending commands to player clients.</summary>
public static unsafe class Server {
	/// <summary>The current map name, set when the server starts up.</summary>
	public static string MapName { get; internal set; } = "";

	/// <summary>
	/// True while the map is changing, until the next map's <see cref="IDeadworksPlugin.OnStartupServer"/>. It turns on
	/// at <see cref="ChangeLevel"/>, or for a change started elsewhere, such as <c>changelevel</c> in the server console,
	/// once the current map starts shutting down, which is before every player gets a disconnect event with
	/// <see cref="ClientDisconnectedEvent.IsMapChange"/>. Anything done to players, pawns or panels meanwhile is lost with
	/// the old map. If the change fails, such as for a map that doesn't exist, it turns off again after a few seconds.
	/// </summary>
	public static bool IsChangingLevel { get; private set; }

	// When ChangeLevel was called, in Environment.TickCount64 ms; 0 once the map has started shutting down.
	private static long _changeLevelRequestedAt;

	// A changelevel that works shuts the current map down within a frame or two; one that fails never does.
	private const long ChangeLevelTimeoutMs = 5000;

	/// <summary>
	/// Change to <paramref name="map"/>. Everyone connected stays connected and reloads into it; see
	/// <see cref="ClientConnectEvent.IsMapChangeReconnect"/>. <see cref="IsChangingLevel"/> is true from this call.
	/// </summary>
	/// <param name="map">A map name, such as <c>dl_midtown</c>.</param>
	/// <exception cref="ArgumentException"><paramref name="map"/> is empty or isn't a plain map name.</exception>
	public static void ChangeLevel(string map) {
		if (string.IsNullOrWhiteSpace(map) || map.Any(c => char.IsWhiteSpace(c) || c is ';' or '"'))
			throw new ArgumentException($"'{map}' isn't a map name", nameof(map));
		IsChangingLevel = true;
		_changeLevelRequestedAt = Environment.TickCount64;
		ExecuteCommand($"changelevel {map}");
	}

	internal static void OnMapStart(string mapName) {
		MapName = mapName;
		IsChangingLevel = false;
		_changeLevelRequestedAt = 0;
	}

	internal static void OnMapShutdown() {
		IsChangingLevel = true;
		_changeLevelRequestedAt = 0;
	}

	internal static void OnGameFrame() {
		if (_changeLevelRequestedAt == 0 || Environment.TickCount64 - _changeLevelRequestedAt < ChangeLevelTimeoutMs)
			return;
		Console.WriteLine($"[Server] changelevel didn't start within {ChangeLevelTimeoutMs} ms; staying on {MapName}");
		IsChangingLevel = false;
		_changeLevelRequestedAt = 0;
	}

	/// <summary>Sends a console command to the client in the given slot.</summary>
	public static void ClientCommand(int slot, string command) {
		Span<byte> utf8 = Utf8.Encode(command, stackalloc byte[Utf8.Size(command)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.ClientCommand(slot, ptr);
		}
	}

	/// <summary>Executes a command on the server console.</summary>
	public static void ExecuteCommand(string command) {
		Span<byte> utf8 = Utf8.Encode(command, stackalloc byte[Utf8.Size(command)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.ExecuteServerCommand(ptr);
		}
	}

	/// <summary>Sets the server's addons string. Clients receive this in SignonState/connection messages.</summary>
	public static void SetAddons(string addons) {
		Span<byte> utf8 = Utf8.Encode(addons, stackalloc byte[Utf8.Size(addons)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.SetServerAddons(ptr);
		}
	}

	/// <summary>Adds a search path to the engine's filesystem. Use pathID "GAME" for general content.</summary>
	/// <param name="path">Path to a directory or VPK file.</param>
	/// <param name="pathID">Search path group (e.g. "GAME", "MOD").</param>
	/// <param name="addToHead">If true, path is searched first (highest priority).</param>
	public static bool AddSearchPath(string path, string pathID = "GAME", bool addToHead = true) {
		Span<byte> pathUtf8 = Utf8.Encode(path, stackalloc byte[Utf8.Size(path)]);
		Span<byte> idUtf8 = Utf8.Encode(pathID, stackalloc byte[Utf8.Size(pathID)]);
		fixed (byte* pPath = pathUtf8)
		fixed (byte* pId = idUtf8) {
			return NativeInterop.AddFileSystemSearchPath(pPath, pId, addToHead ? 0 : 1) != 0;
		}
	}

	/// <summary>Enumerates all registered ConVars by index.</summary>
	public static List<ConVarEntry> EnumerateConVars() {
		var list = new List<ConVarEntry>();
		ConVarInfoNative info;
		for (ushort i = 0; NativeInterop.GetConVarAt(i, &info) != 0; i++) {
			list.Add(new ConVarEntry(
				Utf8Str(info.Name), Utf8Str(info.TypeName), Utf8Str(info.Value), Utf8Str(info.DefaultValue),
				Utf8Str(info.Description), info.Flags,
				info.MinValue != null ? Utf8Str(info.MinValue) : null,
				info.MaxValue != null ? Utf8Str(info.MaxValue) : null));
		}
		return list;
	}

	/// <summary>Enumerates all registered ConCommands by index.</summary>
	public static List<ConCommandEntry> EnumerateConCommands() {
		var list = new List<ConCommandEntry>();
		ConCommandInfoNative info;
		for (ushort i = 0; NativeInterop.GetConCommandAt(i, &info) != 0; i++) {
			list.Add(new ConCommandEntry(Utf8Str(info.Name), Utf8Str(info.Description), info.Flags));
		}
		return list;
	}

	/// <summary>Delegate type for engine log callbacks.</summary>
	public delegate void EngineLogHandler(string message);

	private static readonly List<EngineLogHandler> _engineLogListeners = new();
	private static readonly object _engineLogLock = new();

	[UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
	private static void EngineLogTrampoline(byte* message) {
		if (message == null) return;
		string msg = Marshal.PtrToStringUTF8((nint)message) ?? "";
		if (string.IsNullOrEmpty(msg)) return;

		EngineLogHandler[] snapshot;
		lock (_engineLogLock) {
			if (_engineLogListeners.Count == 0) return;
			snapshot = _engineLogListeners.ToArray();
		}
		foreach (var handler in snapshot)
			handler(msg);
	}

	/// <summary>Adds a listener to receive all engine logging output. Multiple listeners are supported.</summary>
	public static void AddEngineLogListener(EngineLogHandler handler) {
		ArgumentNullException.ThrowIfNull(handler);
		bool wasEmpty;
		lock (_engineLogLock) {
			wasEmpty = _engineLogListeners.Count == 0;
			_engineLogListeners.Add(handler);
		}
		if (wasEmpty) {
			nint fnPtr = (nint)(delegate* unmanaged[Cdecl]<byte*, void>)&EngineLogTrampoline;
			NativeInterop.SetEngineLogCallback(fnPtr);
		}
	}

	/// <summary>Removes a previously added engine log listener. The native listener is unregistered when the last listener is removed.</summary>
	public static void RemoveEngineLogListener(EngineLogHandler handler) {
		ArgumentNullException.ThrowIfNull(handler);
		bool isEmpty;
		lock (_engineLogLock) {
			_engineLogListeners.Remove(handler);
			isEmpty = _engineLogListeners.Count == 0;
		}
		if (isEmpty)
			NativeInterop.SetEngineLogCallback(0);
	}

	/// <summary>
	/// Connects a fake client ("bot") that occupies a real player slot but has no netchannel.
	/// Returns its player slot, or -1 when the engine had no slot free. An unreserved Deadlock
	/// server keeps no slots at all, so this fails until a match has been set up.
	/// </summary>
	public static int CreateFakeClient(string name) {
		Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.CreateFakeClient(ptr);
		}
	}

	/// <summary>
	/// Disconnects the player in <paramref name="slot"/>. They're sent back to the main menu without being told why, so
	/// tell them first if they should know. The reason is for plugins: it's what
	/// <see cref="IDeadworksPlugin.OnClientDisconnecting"/> and <see cref="IDeadworksPlugin.OnClientDisconnect"/> see.
	/// </summary>
	public static void Kick(int slot, ENetworkDisconnectionReason reason = ENetworkDisconnectionReason.NetworkDisconnectKicked)
		=> NativeInterop.DisconnectClient(slot, (int)reason);

	/// <summary>Returns true if the given parameter is present on the engine command line (e.g. "-nomaster").</summary>
	public static bool HasCommandLineParm(string parm) {
		Span<byte> utf8 = Utf8.Encode(parm, stackalloc byte[Utf8.Size(parm)]);
		fixed (byte* ptr = utf8) {
			return NativeInterop.HasCommandLineParm(ptr) != 0;
		}
	}

	private static string Utf8Str(byte* ptr) =>
		ptr != null ? Marshal.PtrToStringUTF8((nint)ptr) ?? "" : "";
}
