using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Custom event infrastructure ---

    private sealed class CustomEventSubscription
    {
        public required Delegate Original;
        public required Func<CustomEventContext, HookResult> Invoke;
    }

    internal readonly record struct RecentPublish(string Name, int SubscriberCount, DateTime At);

    private const int RecentPublishCapacity = 64;
    private static readonly Queue<RecentPublish> _recentPublishes = new();

    private static readonly MethodInfo _buildTypedFuncMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildTypedFunc), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo _buildTypedActionMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildTypedAction), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Func<CustomEventContext, HookResult> BuildTypedFunc<T>(Func<T, HookResult> typed) where T : class
        => ctx => ctx.Payload is T t ? typed(t) : HookResult.Continue;

    private static Func<CustomEventContext, HookResult> BuildTypedAction<T>(Action<T> typed) where T : class
        => ctx => { if (ctx.Payload is T t) typed(t); return HookResult.Continue; };

    /// <summary>Wraps a typed <c>Func&lt;T,HookResult&gt;</c> or <c>Action&lt;T&gt;</c> into a context-invoking form that skips on payload mismatch. No DynamicInvoke at dispatch.</summary>
    private static Func<CustomEventContext, HookResult> WrapTypedPayloadHandler(Delegate typed, Type payloadType, bool returnsHookResult)
    {
        var builder = returnsHookResult ? _buildTypedFuncMethod : _buildTypedActionMethod;
        return (Func<CustomEventContext, HookResult>)builder.MakeGenericMethod(payloadType).Invoke(null, [typed])!;
    }

    private static void RegisterPluginCustomEventHandlers(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                CustomEventHandlerAttribute[] attrList = [.. method.GetCustomAttributes<CustomEventHandlerAttribute>()];
                if (attrList.Length == 0) continue;

                var wrapped = BuildAttributeHandler(plugin, method);
                if (wrapped == null) continue;

                foreach (var attr in attrList)
                {
                    var sub = new CustomEventSubscription { Original = wrapped, Invoke = wrapped };
                    _customEventRegistry.AddForPlugin(normalizedPath, attr.EventName, sub);
                    PluginRegistrationTracker.Add(normalizedPath, "customevent", attr.EventName);
                    Console.WriteLine($"[PluginLoader] Registered custom event handler: {plugin.Name} -> {attr.EventName}");
                }
            }
        }
    }

    /// <summary>Builds a normalized invoke delegate for an attribute-annotated method, or null if the signature is invalid.</summary>
    private static Func<CustomEventContext, HookResult>? BuildAttributeHandler(IDeadworksPlugin plugin, MethodInfo method)
    {
        var returnsHookResult = method.ReturnType == typeof(HookResult);
        var returnsVoid = method.ReturnType == typeof(void);
        if (!returnsHookResult && !returnsVoid)
        {
            Console.WriteLine($"[CustomEvents] {plugin.Name}.{method.Name}: [CustomEventHandler] method must return HookResult or void — got {method.ReturnType.Name}. Skipping.");
            return null;
        }

        var parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            if (returnsVoid)
            {
                var del = (Action)Delegate.CreateDelegate(typeof(Action), plugin, method);
                return _ => { del(); return HookResult.Continue; };
            }
            var func = (Func<HookResult>)Delegate.CreateDelegate(typeof(Func<HookResult>), plugin, method);
            return _ => func();
        }

        if (parameters.Length != 1)
        {
            Console.WriteLine($"[CustomEvents] {plugin.Name}.{method.Name}: [CustomEventHandler] method must take 0 parameters, one CustomEventContext, or one reference-type payload — got {parameters.Length} parameters. Skipping.");
            return null;
        }

        var paramType = parameters[0].ParameterType;

        if (paramType == typeof(CustomEventContext))
        {
            if (returnsVoid)
            {
                var del = (Action<CustomEventContext>)Delegate.CreateDelegate(typeof(Action<CustomEventContext>), plugin, method);
                return ctx => { del(ctx); return HookResult.Continue; };
            }
            return (Func<CustomEventContext, HookResult>)Delegate.CreateDelegate(typeof(Func<CustomEventContext, HookResult>), plugin, method);
        }

        if (paramType.IsValueType)
        {
            Console.WriteLine($"[CustomEvents] {plugin.Name}.{method.Name}: [CustomEventHandler] payload parameter must be a reference type — got {paramType.Name}. Skipping.");
            return null;
        }

        var delegateType = returnsHookResult
            ? typeof(Func<,>).MakeGenericType(paramType, typeof(HookResult))
            : typeof(Action<>).MakeGenericType(paramType);
        var typedDel = Delegate.CreateDelegate(delegateType, plugin, method);
        return WrapTypedPayloadHandler(typedDel, paramType, returnsHookResult);
    }

    /// <summary>Normalizes a user-supplied delegate (from manual Subscribe) into a context-invoking form, or null on shape error.</summary>
    private static Func<CustomEventContext, HookResult>? NormalizeSubscribeDelegate(Delegate handler)
    {
        switch (handler)
        {
            case Func<CustomEventContext, HookResult> f: return f;
            case Action<CustomEventContext> a: return ctx => { a(ctx); return HookResult.Continue; };
        }

        var type = handler.GetType();
        if (!type.IsGenericType) return null;

        var genericDef = type.GetGenericTypeDefinition();
        var args = type.GetGenericArguments();

        if (genericDef == typeof(Func<,>) && args.Length == 2 && args[1] == typeof(HookResult) && !args[0].IsValueType)
            return WrapTypedPayloadHandler(handler, args[0], returnsHookResult: true);

        if (genericDef == typeof(Action<>) && args.Length == 1 && !args[0].IsValueType)
            return WrapTypedPayloadHandler(handler, args[0], returnsHookResult: false);

        return null;
    }

    private static IHandle OnCustomEventSubscribe(string name, Delegate handler)
    {
        var invoke = NormalizeSubscribeDelegate(handler);
        if (invoke == null)
        {
            Console.WriteLine($"[CustomEvents] Subscribe('{name}'): unsupported delegate shape {handler.GetType().Name}. Ignored.");
            return CallbackHandle.Noop;
        }

        var sub = new CustomEventSubscription { Original = handler, Invoke = invoke };
        var callerPluginPath = ResolveCallingPluginPath();

        lock (_lock)
        {
            if (callerPluginPath != null)
                _customEventRegistry.AddForPlugin(callerPluginPath, name, sub);
            else
                _customEventRegistry.Add(name, sub);
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                _customEventRegistry.Remove(name, sub);
            }
        });
    }

    private static void OnCustomEventUnsubscribe(string name, Delegate handler)
    {
        lock (_lock)
        {
            var list = _customEventRegistry.Snapshot(name);
            if (list == null) return;
            foreach (var sub in list)
            {
                if (ReferenceEquals(sub.Original, handler) || sub.Original.Equals(handler))
                {
                    _customEventRegistry.Remove(name, sub);
                    return;
                }
            }
        }
    }

    private static int OnCustomEventSubscriberCount(string name)
    {
        lock (_lock)
        {
            return _customEventRegistry.Snapshot(name)?.Count ?? 0;
        }
    }

    private static HookResult OnCustomEventPublish(string name, object? payload)
    {
        List<CustomEventSubscription>? handlers;
        lock (_lock)
        {
            handlers = _customEventRegistry.Snapshot(name);
        }

        var count = handlers?.Count ?? 0;
        RecordRecentPublish(name, count);

        if (count == 0)
            return HookResult.Continue;

        var sender = ResolveCallingPluginName();
        var ctx = new CustomEventContext(name, payload, sender);

        var result = HookResult.Continue;
        foreach (var sub in handlers!)
        {
            try
            {
                var hr = sub.Invoke(ctx);
                if (hr > result) result = hr;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Custom event handler for '{name}' threw: {ex.Message}");
            }
        }

        return result;
    }

    private static void RecordRecentPublish(string name, int subscriberCount)
    {
        lock (_recentPublishes)
        {
            _recentPublishes.Enqueue(new RecentPublish(name, subscriberCount, DateTime.UtcNow));
            while (_recentPublishes.Count > RecentPublishCapacity)
                _recentPublishes.Dequeue();
        }
    }

    internal static IReadOnlyList<RecentPublish> GetRecentPublishes(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        lock (_recentPublishes)
        {
            return _recentPublishes.Where(p => p.At >= cutoff).ToArray();
        }
    }

    internal static IReadOnlyList<(string Name, int Count, IReadOnlyList<string> Plugins)> GetCustomEventSubscriptions()
    {
        lock (_lock)
        {
            var rows = _customEventRegistry.ListHandlers();
            var result = new List<(string, int, IReadOnlyList<string>)>(rows.Count);
            foreach (var (name, count) in rows)
            {
                var plugins = _customEventRegistry.PluginsWithHandlerFor(name)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                result.Add((name, count, plugins));
            }
            return result.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
        }
    }

    // --- Test hooks ---

    internal static void InitializeCustomEventsForTests()
    {
        CustomEvents.OnSubscribe = OnCustomEventSubscribe;
        CustomEvents.OnUnsubscribe = OnCustomEventUnsubscribe;
        CustomEvents.OnPublish = OnCustomEventPublish;
        CustomEvents.OnSubscriberCount = OnCustomEventSubscriberCount;
    }

    internal static void ResetCustomEventsForTests()
    {
        lock (_lock)
        {
            _customEventRegistry.Clear();
            _contextToPluginName.Clear();
        }
        lock (_recentPublishes) { _recentPublishes.Clear(); }
        CustomEvents.OnSubscribe = null;
        CustomEvents.OnUnsubscribe = null;
        CustomEvents.OnPublish = null;
        CustomEvents.OnSubscriberCount = null;
    }

    internal static void RegisterPluginCustomEventHandlersForTests(string normalizedPath, IDeadworksPlugin plugin)
        => RegisterPluginCustomEventHandlers(normalizedPath, [plugin]);

    internal static void UnregisterPluginCustomEventsForTests(string normalizedPath)
    {
        lock (_lock) { _customEventRegistry.UnregisterPlugin(normalizedPath); }
    }

    // --- Stack-walk helpers for caller resolution ---

    private static string ResolveCallingPluginName()
    {
        var path = ResolveCallingPluginPath();
        if (path == null) return CustomEventContext.HostSenderName;
        lock (_lock)
        {
            if (_loaded.TryGetValue(path, out var entry) && entry.Plugins.Count > 0)
                return entry.Plugins[0].Name;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? ResolveCallingPluginPath()
    {
        var hostAsm = typeof(PluginLoader).Assembly;
        var apiAsm = typeof(CustomEvents).Assembly;

        var trace = new StackTrace(2, false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
            if (asm == null || asm == hostAsm || asm == apiAsm) continue;

            var alc = AssemblyLoadContext.GetLoadContext(asm);
            if (alc == null) continue;

            lock (_lock)
            {
                if (_contextToPluginName.TryGetValue(alc, out var path))
                    return path;
            }
        }

        return null;
    }
}
