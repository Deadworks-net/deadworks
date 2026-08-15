using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DeadworksManaged.Telemetry;

/// <summary>
/// Logger provider that writes formatted log messages to the native engine console
/// via the unmanaged callback pointer stored in <see cref="NativeLogCallback"/>.
/// </summary>
internal sealed unsafe class NativeEngineLoggerProvider : ILoggerProvider
{
    private readonly delegate* unmanaged[Cdecl]<char*, void> _callback;
    private readonly ConcurrentDictionary<string, NativeEngineLogger> _loggers = new();

    public NativeEngineLoggerProvider(delegate* unmanaged[Cdecl]<char*, void> callback, LogLevel minLevel)
    {
        _callback = callback;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new NativeEngineLogger(name, _callback));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

internal sealed unsafe class NativeEngineLogger : ILogger
{
    private readonly string _category;
    private readonly delegate* unmanaged[Cdecl]<char*, void> _callback;

    public NativeEngineLogger(string category, delegate* unmanaged[Cdecl]<char*, void> callback)
    {
        _category = category;
        _callback = callback;
    }

    public bool IsEnabled(LogLevel logLevel) => _callback != null;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var prefix = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "????"
        };

        var formatted = $"[{_category}] {prefix}: {message}";
        if (exception != null)
            formatted = $"{formatted}\n{exception}";

        fixed (char* ptr = formatted)
        {
            _callback(ptr);
        }
    }
}
