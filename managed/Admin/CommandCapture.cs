using System.Globalization;
using System.Text;
using DeadworksManaged.Api;

namespace DeadworksManaged.AdminSystem;

/// <summary>
/// Backs <see cref="Server.ExecuteCommand(string, Action{string})"/>. The command is queued followed by a marker command;
/// engine log output is collected until the marker runs, which means the command has finished.
/// </summary>
internal static class CommandCapture
{
    private const string MarkerCommand = "dw_capture_done";

    private static readonly Lock _lock = new();
    private sealed record Capture(StringBuilder Output, Action<string> OnOutput, long StartedMs);

    // A command that never lets the marker run (quit, a map change) mustn't keep collecting the log forever.
    private const long TimeoutMs = 30_000;
    private const int MaxChars = 64 * 1024;

    private static readonly Dictionary<int, Capture> _pending = [];
    private static int _nextId;
    private static bool _listening;

    public static void Initialize()
    {
        ConCommandManager.RegisterBuiltInCommand(MarkerCommand, "Internal: marks the end of a captured command's output.", serverOnly: true, OnMarker);
        Server.ExecuteWithOutput = Execute;
    }

    private static void Execute(string command, Action<string> onOutput)
    {
        int id;
        lock (_lock)
        {
            id = ++_nextId;
            _pending[id] = new Capture(new StringBuilder(), onOutput, Environment.TickCount64);
            if (!_listening)
            {
                Server.AddEngineLogListener(OnLog);
                _listening = true;
            }
        }
        Server.ExecuteCommand(command);
        Server.ExecuteCommand($"{MarkerCommand} {id}");
    }

    // Runs inside the engine's log dispatch, so it only appends: no logging, no listener changes.
    private static void OnLog(string message)
    {
        lock (_lock)
        {
            foreach (var capture in _pending.Values)
            {
                var room = MaxChars - capture.Output.Length;
                if (room > 0)
                    capture.Output.Append(message.Length <= room ? message : message[..room]);
            }
        }
    }

    /// <summary>
    /// Gives up on commands whose marker never ran (quit, a map change), so they don't collect the log forever.
    /// Called once a second from the game thread.
    /// </summary>
    public static void Sweep()
    {
        List<Capture> expired;
        lock (_lock)
        {
            var now = Environment.TickCount64;
            expired = [];
            foreach (var (id, capture) in _pending.ToList())
            {
                if (now - capture.StartedMs <= TimeoutMs)
                    continue;
                _pending.Remove(id);
                expired.Add(capture);
            }
            if (expired.Count > 0)
                StopListeningIfIdle();
        }

        foreach (var capture in expired)
        {
            try
            {
                capture.OnOutput(capture.Output.ToString().TrimEnd()
                                 + $"\n(stopped collecting output: the command didn't finish within {TimeoutMs / 1000}s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommandCapture] Output callback threw: {ex.Message}");
            }
        }
    }

    // Must be called under _lock.
    private static void StopListeningIfIdle()
    {
        if (_pending.Count == 0 && _listening)
        {
            Server.RemoveEngineLogListener(OnLog);
            _listening = false;
        }
    }

    private static void OnMarker(ConCommandContext ctx)
    {
        if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return;

        Capture? capture;
        lock (_lock)
        {
            if (!_pending.Remove(id, out capture))
                return;
            StopListeningIfIdle();
        }

        try
        {
            capture.OnOutput(capture.Output.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandCapture] Output callback threw: {ex.Message}");
        }
    }
}
