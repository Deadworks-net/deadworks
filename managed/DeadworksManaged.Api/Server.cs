using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Server-side utilities for sending commands to player clients.</summary>
public static unsafe class Server {
	/// <summary>The current map name, set when the server starts up.</summary>
	public static string MapName { get; internal set; } = "";

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

	/// <summary>
	/// Disconnects the player in <paramref name="slot"/> with a message. The message is printed to their chat and
	/// console first, then passed to the engine as the disconnect reason.
	/// </summary>
	public static void Kick(int slot, string message, ENetworkDisconnectionReason reason = ENetworkDisconnectionReason.NetworkDisconnectKicked) {
		if (string.IsNullOrWhiteSpace(message)) {
			Kick(slot, reason);
			return;
		}

		// The chat and console copies are what a player is sure to see before the client drops.
		Chat.PrintToChat(slot, message);
		Players.FromSlot(slot)?.PrintToConsole(message);

		if (NativeInterop.KickClient == null) {
			Kick(slot, reason);
			return;
		}
		Span<byte> utf8 = Utf8.Encode(message, stackalloc byte[Utf8.Size(message)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.KickClient(slot, ptr, (int)reason);
		}
	}

	/// <summary>Where the game keeps its maps: <c>game/citadel/maps</c>, found relative to the managed folder.</summary>
	private static string MapsDir => Path.GetFullPath(Path.Combine(
		Path.GetDirectoryName(typeof(Server).Assembly.Location) ?? ".", "..", "..", "..", "citadel", "maps"));

	/// <summary>Extra map names from <c>serverbrowser.extra_maps</c> in <c>deadworks.jsonc</c>. Set by the host.</summary>
	internal static Func<IEnumerable<string>>? ExtraMaps;

	/// <summary>
	/// Maps the server can change to: the game's own <c>.vpk</c> maps plus <c>extra_maps</c> from <c>deadworks.jsonc</c>,
	/// sorted by name.
	/// </summary>
	public static IReadOnlyList<string> GetMapList() {
		var maps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		try {
			if (Directory.Exists(MapsDir))
				foreach (var file in Directory.GetFiles(MapsDir, "*.vpk"))
					maps.Add(Path.GetFileNameWithoutExtension(file));
		} catch (IOException) { } catch (UnauthorizedAccessException) { }

		foreach (var map in ExtraMaps?.Invoke() ?? [])
			if (IsMapNameWellFormed(map))
				maps.Add(map);
		return [.. maps];
	}

	/// <summary>
	/// Whether <paramref name="map"/> can be loaded: the engine knows it, or it's in <see cref="GetMapList"/>.
	/// Names with spaces, quotes, <c>;</c> or path tricks are always rejected, so a valid name is safe to put in a command.
	/// </summary>
	public static bool IsMapValid(string map) {
		if (!IsMapNameWellFormed(map))
			return false;
		if (NativeInterop.IsMapValid != null) {
			Span<byte> utf8 = Utf8.Encode(map, stackalloc byte[Utf8.Size(map)]);
			fixed (byte* ptr = utf8) {
				if (NativeInterop.IsMapValid(ptr) != 0)
					return true;
			}
		}
		return GetMapList().Contains(map, StringComparer.OrdinalIgnoreCase);
	}

	internal static bool IsMapNameWellFormed(string map)
		=> map.Length is > 0 and <= 128
			&& !map.Contains("..", StringComparison.Ordinal)
			&& map.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/');

	/// <summary>Changes to <paramref name="map"/> if <see cref="IsMapValid"/> accepts it. Returns false otherwise.</summary>
	public static bool ChangeMap(string map) {
		if (!IsMapValid(map))
			return false;
		ExecuteCommand($"changelevel {map}");
		return true;
	}

	/// <summary>Set by the host; runs a command and collects what it prints.</summary>
	internal static Action<string, Action<string>>? ExecuteWithOutput;

	/// <summary>
	/// Runs a server console command and passes what it printed to <paramref name="onOutput"/>. Commands run on the
	/// next frame, so the callback always comes later. Setting a cvar prints nothing, so the output is empty.
	/// </summary>
	public static void ExecuteCommand(string command, Action<string> onOutput) {
		ArgumentNullException.ThrowIfNull(onOutput);
		if (ExecuteWithOutput == null)
			throw new InvalidOperationException("Command output capture is not initialized.");
		ExecuteWithOutput(command, onOutput);
	}

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
