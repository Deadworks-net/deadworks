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
    private static readonly Dictionary<int, (StringBuilder Output, Action<string> OnOutput)> _pending = [];
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
            _pending[id] = (new StringBuilder(), onOutput);
            if (!_listening)
            {
                Server.AddEngineLogListener(OnLog);
                _listening = true;
            }
        }
        Server.ExecuteCommand(command);
        Server.ExecuteCommand($"{MarkerCommand} {id}");
    }

    private static void OnLog(string message)
    {
        lock (_lock)
            foreach (var (output, _) in _pending.Values)
                output.Append(message);
    }

    private static void OnMarker(ConCommandContext ctx)
    {
        if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return;

        (StringBuilder Output, Action<string> OnOutput) capture;
        lock (_lock)
        {
            if (!_pending.Remove(id, out capture))
                return;
            if (_pending.Count == 0 && _listening)
            {
                Server.RemoveEngineLogListener(OnLog);
                _listening = false;
            }
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
