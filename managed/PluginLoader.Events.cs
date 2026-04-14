using System.Reflection;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Game event infrastructure ---

    private static unsafe void RegisterEventWithNative(string eventName)
    {
        Span<byte> utf8 = Utf8.Encode(eventName, stackalloc byte[Utf8.Size(eventName)]);
        fixed (byte* ptr = utf8)
        {
            NativeInterop.RegisterGameEvent(ptr);
        }
    }

    private static void RegisterPluginEventHandlers(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                var attrs = method.GetCustomAttributes<GameEventHandlerAttribute>();
                foreach (var attr in attrs)
                {
                    GameEventHandler del;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType != typeof(GameEvent)
                        && typeof(GameEvent).IsAssignableFrom(parameters[0].ParameterType))
                    {
                        // Typed handler: e.g. OnPlayerDeath(PlayerDeathEvent e)
                        var typedDelegateType = typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, typeof(HookResult));
                        var typedDel = Delegate.CreateDelegate(typedDelegateType, plugin, method);
                        var eventType = parameters[0].ParameterType;
                        del = (GameEvent e) =>
                        {
                            if (eventType.IsInstanceOfType(e))
                                return (HookResult)typedDel.DynamicInvoke(e)!;
                            return HookResult.Continue;
                        };
                    }
                    else
                    {
                        del = (GameEventHandler)Delegate.CreateDelegate(typeof(GameEventHandler), plugin, method);
                    }

                    if (_eventRegistry.AddForPlugin(normalizedPath, attr.EventName, del))
                        RegisterEventWithNative(attr.EventName);

                    PluginRegistrationTracker.Add(normalizedPath, "event", attr.EventName);
                    _logger.LogDebug("Registered game event handler: {PluginName} -> {EventName}", plugin.Name, attr.EventName);
                }
            }
        }
    }

    private static IHandle OnManualAddListenerWithHandle(string eventName, GameEventHandler handler)
    {
        lock (_lock)
        {
            if (_eventRegistry.Add(eventName, handler))
                RegisterEventWithNative(eventName);
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                _eventRegistry.Remove(eventName, handler);
            }
        });
    }

    private static void OnManualRemoveListener(string eventName, GameEventHandler handler)
    {
        lock (_lock)
        {
            _eventRegistry.Remove(eventName, handler);
        }
    }

    public static HookResult DispatchGameEvent(string name, GameEvent e)
    {
        List<GameEventHandler>? handlers;
        lock (_lock)
        {
            handlers = _eventRegistry.Snapshot(name);
        }

        if (handlers == null)
            return HookResult.Continue;

        DeadworksMetrics.EventsDispatched.Add(1);

        var result = HookResult.Continue;
        foreach (var handler in handlers)
        {
            try
            {
                var hr = handler(e);
                if (hr > result) result = hr;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Game event handler for {EventName} threw", name);
                DeadworksMetrics.EventHandlerErrors.Add(1);
            }
        }

        return result;
    }
}
