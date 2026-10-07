using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeadworksManaged.Api;
using DeadworksManaged.Game;

namespace SpawnSurvey;

/// <summary>
/// The server side of the spawn survey. It spawns each name in a list alone, waits to see
/// whether the entity lives, removes it, and records what happened. survey.py starts the
/// server, restarts it when a spawn takes it down, and turns the log into spawn-survey.json.
/// <para>
/// The log is appended to and flushed before every step, so a name the server dies on is the
/// one with a "begin" and no "end". A name that has died once is tried again first thing on
/// the next server, so a crash that an earlier entity caused is not blamed on it; one that
/// has died twice is left to the runner to call.
/// </para>
/// <para>
/// A crash here is expected, dozens of times a survey, and must not reach anyone's screen.
/// Left alone, a fault under managed frames makes the runtime fail fast, and Windows puts up
/// a "System Error" box for that whatever the process's error mode says. So while the survey
/// runs, a vectored exception handler sees every fault first, writes where it happened, and
/// ends the process on the spot.
/// </para>
/// <para>
/// Does nothing unless DW_SURVEY_LIST and DW_SURVEY_OUT are set. With DW_SURVEY_CONVARS and
/// DW_SURVEY_CONVARS_OUT it also writes, once, which of the listed console variables this
/// server does not have.
/// </para>
/// </summary>
public unsafe class SpawnSurvey : DeadworksPluginBase {
	public override string Name => "Spawn Survey";

	private const double WarmUpSeconds = 6;     // after the map loads, before the first spawn
	private const double LiveSeconds = 0.5;     // how long an entity has to last to count as living
	private const double SettleSeconds = 0.15;  // after removing it, so a crash on removal lands on its own name
	private const int NamesPerServer = 150;     // then quit and let the runner start a clean server

	private static readonly Vector3 Origin = new(0, 0, 1500);

	private enum Stage { Waiting, Idle, Living, Settling, Finished }

	private static SpawnSurvey? _armed;   // the survey whose faults the exception handler ends the process for

	private readonly Queue<string> _queue = new();
	private readonly Stopwatch _clock = new();
	private readonly List<string> _log = [];
	private StreamWriter? _out;
	private Stage _stage = Stage.Finished;
	private string _current = "";
	private Schema.CEntityInstance? _entity;
	private string _result = "", _class = "", _model = "";
	private int _doneThisServer;

	// ---- Faults ------------------------------------------------------------------

	[StructLayout(LayoutKind.Sequential)]
	private struct ExceptionRecord {
		public uint Code, Flags;
		public nint Chained, Address;
		public uint ParameterCount;
		public nuint Parameter0, Parameter1;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ExceptionPointers {
		public ExceptionRecord* Record;
		public nint Context;
	}

	[DllImport("kernel32.dll")]
	private static extern nint AddVectoredExceptionHandler(uint first, delegate* unmanaged<ExceptionPointers*, int> handler);

	[DllImport("kernel32.dll")]
	private static extern bool TerminateProcess(nint process, uint exitCode);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern uint GetModuleFileNameW(nint module, char* name, uint size);

	private const uint AccessViolation = 0xC0000005;
	private const nuint ExecuteViolation = 8;

	/// <summary><c>server.dll+0x1A2B3C</c> for an address inside a loaded module, or null.</summary>
	private static string? ModuleOffset(nint address) {
		// FROM_ADDRESS | UNCHANGED_REFCOUNT
		if (!GetModuleHandleExW(0x4 | 0x2, address, out nint module) || module == 0) return null;
		char* path = stackalloc char[520];
		uint length = GetModuleFileNameW(module, path, 520);
		return $"{Path.GetFileName(new string(path, 0, (int)length))}+0x{address - module:X}";
	}

	[UnmanagedCallersOnly]
	private static int OnException(ExceptionPointers* info) {
		const int continueSearch = 0;
		var survey = _armed;
		if (survey == null) return continueSearch;

		var record = info->Record;
		// The faults that end a process: bad memory, a bad instruction, a divide by zero, a stray breakpoint.
		if (record->Code is not (AccessViolation or 0xC000001D or 0xC0000094 or 0xC0000096 or 0x80000003)) return continueSearch;

		// A fault in code that belongs to no module is the runtime's own business (a null check in
		// compiled C# faults there and becomes a NullReferenceException), unless the processor was
		// trying to execute the address: that is a call through a bad pointer.
		string? at = ModuleOffset(record->Address);
		bool executing = record->Code == AccessViolation && record->ParameterCount >= 1 && record->Parameter0 == ExecuteViolation;
		if (at == null && !executing) return continueSearch;

		_armed = null;
		try {
			bool spawning = survey._stage is Stage.Living or Stage.Settling;
			survey.Write(new { e = "fault", name = spawning ? survey._current : "", code = $"0x{record->Code:X8}", at = at ?? $"0x{record->Address:X}" });
		}
		catch {
			// Saying where is a courtesy; ending quietly is the job.
		}
		TerminateProcess(GetCurrentProcess(), record->Code);
		return continueSearch;
	}

	// ---- Survey ------------------------------------------------------------------

	public override void OnLoad(bool isReload) {
		string? list = Environment.GetEnvironmentVariable("DW_SURVEY_LIST");
		string? output = Environment.GetEnvironmentVariable("DW_SURVEY_OUT");
		if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(output)) return;

		// What earlier servers got through: finished names, and how often each was begun without finishing.
		var finished = new HashSet<string>();
		var begun = new Dictionary<string, int>();
		if (File.Exists(output)) {
			foreach (string line in File.ReadLines(output)) {
				if (line.Length == 0) continue;
				using var entry = JsonDocument.Parse(line);
				string name = entry.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
				switch (entry.RootElement.GetProperty("e").GetString()) {
					case "begin": begun[name] = begun.GetValueOrDefault(name) + 1; break;
					case "end": finished.Add(name); break;
				}
			}
		}

		var names = File.ReadLines(list).Select(l => l.Split('\t')[0]).Where(n => n.Length > 0 && !finished.Contains(n)).ToList();
		// A name that died once goes first, on a server nothing else has touched yet. Twice is the runner's to call.
		foreach (string name in names.Where(n => begun.GetValueOrDefault(n) == 1)) _queue.Enqueue(name);
		foreach (string name in names.Where(n => begun.GetValueOrDefault(n) == 0)) _queue.Enqueue(name);

		_out = new StreamWriter(output, append: true) { AutoFlush = true };
		Server.AddEngineLogListener(OnEngineLog);
		AddVectoredExceptionHandler(1, &OnException);
		_stage = Stage.Waiting;
		Console.WriteLine($"[Survey] {_queue.Count} names to try");
	}

	public override void OnUnload() {
		_armed = null;
		_out?.Dispose();
	}

	public override void OnStartupServer() {
		if (_stage == Stage.Waiting) _clock.Restart();
		WriteAbsentConVars();
	}

	// The engine says so when an entity's model is not loaded; that is worth knowing about a name that otherwise lives.
	private void OnEngineLog(string message) {
		if (_stage is not (Stage.Living or Stage.Settling)) return;
		const string marker = "to nonresident asset ";
		int at = message.IndexOf(marker, StringComparison.Ordinal);
		if (at >= 0 && _model.Length == 0) _model = message[(at + marker.Length)..].Trim().TrimEnd('.');
		else if (_log.Count < 4 && (message.Contains("Assert", StringComparison.Ordinal) || message.Contains("rror", StringComparison.Ordinal)))
			_log.Add(message.Trim());
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void Write(object entry) => _out!.WriteLine(JsonSerializer.Serialize(entry));

	public override void OnGameFrame(bool simulating, bool firstTick, bool lastTick) {
		switch (_stage) {
			case Stage.Waiting:
				if (!_clock.IsRunning || _clock.Elapsed.TotalSeconds < WarmUpSeconds) break;
				// From here on a fault is the survey's doing, or the doing of something it spawned.
				_armed = this;
				_stage = Stage.Idle;
				break;

			case Stage.Idle:
				if (_queue.Count == 0) { Finish(done: true); break; }
				if (_doneThisServer >= NamesPerServer) { Finish(done: false); break; }
				Begin(_queue.Dequeue());
				break;

			case Stage.Living:
				if (_clock.Elapsed.TotalSeconds < LiveSeconds) break;
				_result = _entity is { IsValid: true } ? "lived" : "vanished";
				_entity?.Remove();
				_clock.Restart();
				_stage = Stage.Settling;
				break;

			case Stage.Settling:
				if (_clock.Elapsed.TotalSeconds < SettleSeconds) break;
				End();
				break;
		}
	}

	private void Begin(string name) {
		_current = name;
		_result = _class = _model = "";
		_log.Clear();
		_entity = null;
		Write(new { e = "begin", name });
		_clock.Restart();
		_stage = Stage.Living;   // set first, so engine output during the spawn itself is this name's
		try {
			_entity = Spawner.Create<Schema.CEntityInstance>(name, new Keys.CBaseEntity { origin = Origin });
		}
		catch (Exception error) {
			_result = "threw";
			_log.Add($"{error.GetType().Name}: {error.Message}");
			End();
			return;
		}
		if (_entity == null) {
			_result = "refused";
			End();
			return;
		}
		_class = _entity.GetType().Name;
	}

	private void End() {
		Write(new { e = "end", name = _current, result = _result, @class = _class, model = _model, log = _log.ToArray() });
		_doneThisServer++;
		_entity = null;
		_stage = Stage.Idle;
	}

	private void Finish(bool done) {
		if (done) Write(new { e = "done" });
		_armed = null;   // shutting down is the engine's business again
		_stage = Stage.Finished;
		Server.ExecuteCommand("quit");
	}

	// Once per survey: which of the console variables the game declares this dedicated server never registered.
	private static void WriteAbsentConVars() {
		string? list = Environment.GetEnvironmentVariable("DW_SURVEY_CONVARS");
		string? output = Environment.GetEnvironmentVariable("DW_SURVEY_CONVARS_OUT");
		if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(output) || File.Exists(output)) return;
		var absent = File.ReadLines(list).Where(name => name.Length > 0 && ConVar.Find(name) == null).ToList();
		File.WriteAllText(output, JsonSerializer.Serialize(absent));
	}
}
