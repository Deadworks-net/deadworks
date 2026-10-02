using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Event subscriptions ---

    private sealed class EventSubscription
    {
        public required Func<EventContext, HookResult> Invoke;
    }

    // --- Query subscriptions ---

    /// <summary>Sentinel returned from an <see cref="QuerySubscription.Invoke"/> when the handler's declared request type does not match the actual request.</summary>
    private static readonly object s_querySkip = new();

    private sealed class QuerySubscription
    {
        public required Type ResponseType;
        /// <summary>Returns the handler's response boxed as <see cref="object"/>, or <see cref="s_querySkip"/> to skip this handler.</summary>
        public required Func<QueryContext, object?> Invoke;
    }

    // --- Diagnostic ring buffers ---

    internal readonly record struct RecentPublish(string Name, int SubscriberCount, DateTime At);
    internal readonly record struct RecentQuery(string Name, int HandlerCount, int ResponseCount, DateTime At);

    private const int RecentHistoryCapacity = 64;
    private static readonly Queue<RecentPublish> _recentPublishes = new();
    private static readonly Queue<RecentQuery> _recentQueries = new();

    // --- Typed-delegate wrapping (no DynamicInvoke at dispatch) ---

    private static readonly MethodInfo _buildTypedEventFuncMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildTypedEventFunc), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo _buildTypedEventActionMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildTypedEventAction), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Func<EventContext, HookResult> BuildTypedEventFunc<T>(Func<T, HookResult> typed) where T : class
        => ctx => ctx.Payload is T t ? typed(t) : HookResult.Continue;

    private static Func<EventContext, HookResult> BuildTypedEventAction<T>(Action<T> typed) where T : class
        => ctx => { if (ctx.Payload is T t) typed(t); return HookResult.Continue; };

    /// <summary>Wraps a typed event <c>Func&lt;T,HookResult&gt;</c> or <c>Action&lt;T&gt;</c> into a context-invoking form that skips on payload mismatch.</summary>
    private static Func<EventContext, HookResult> WrapTypedEventHandler(Delegate typed, Type payloadType, bool returnsHookResult)
    {
        var builder = returnsHookResult ? _buildTypedEventFuncMethod : _buildTypedEventActionMethod;
        return (Func<EventContext, HookResult>)builder.MakeGenericMethod(payloadType).Invoke(null, [typed])!;
    }

    // Query wrappers

    private static readonly MethodInfo _buildQueryBareFuncMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildQueryBareFunc), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo _buildQueryContextFuncMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildQueryContextFunc), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo _buildQueryTypedFuncMethod =
        typeof(PluginLoader).GetMethod(nameof(BuildQueryTypedFunc), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Func<QueryContext, object?> BuildQueryBareFunc<TResponse>(Func<TResponse> typed)
        => _ => typed();

    private static Func<QueryContext, object?> BuildQueryContextFunc<TResponse>(Func<QueryContext, TResponse> typed)
        => ctx => typed(ctx);

    private static Func<QueryContext, object?> BuildQueryTypedFunc<TRequest, TResponse>(Func<TRequest, TResponse> typed) where TRequest : class
        => ctx => ctx.Request is TRequest t ? typed(t) : s_querySkip;

    // --- Attribute scan (events + queries in one pass) ---

    private static void RegisterPluginBusHandlers(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                var eventAttrs = method.GetCustomAttributes<EventHandlerAttribute>().ToArray();
                var queryAttrs = method.GetCustomAttributes<QueryHandlerAttribute>().ToArray();
                if (eventAttrs.Length == 0 && queryAttrs.Length == 0) continue;

                if (eventAttrs.Length > 0)
                {
                    var wrapped = BuildAttributeEventHandler(plugin, method);
                    if (wrapped != null)
                    {
                        foreach (var attr in eventAttrs)
                        {
                            var sub = new EventSubscription { Invoke = wrapped };
                            _eventRegistry.AddForPlugin(normalizedPath, attr.EventName, sub);
                            PluginRegistrationTracker.Add(normalizedPath, "event", attr.EventName);
                            Console.WriteLine($"[PluginLoader] Registered event handler: {plugin.Name} -> {attr.EventName}");
                        }
                    }
                }

                if (queryAttrs.Length > 0)
                {
                    var built = BuildAttributeQueryHandler(plugin, method);
                    if (built != null)
                    {
                        var (invoke, responseType) = built.Value;
                        foreach (var attr in queryAttrs)
                        {
                            var sub = new QuerySubscription
                            {
                                ResponseType = responseType,
                                Invoke = invoke,
                            };
                            _queryRegistry.AddForPlugin(normalizedPath, attr.QueryName, sub);
                            PluginRegistrationTracker.Add(normalizedPath, "query", attr.QueryName);
                            Console.WriteLine($"[PluginLoader] Registered query handler: {plugin.Name} -> {attr.QueryName} : {responseType.Name}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Builds a normalized invoke delegate for an [EventHandler]-annotated method, or null if the signature is invalid.</summary>
    private static Func<EventContext, HookResult>? BuildAttributeEventHandler(IDeadworksPlugin plugin, MethodInfo method)
    {
        var returnsHookResult = method.ReturnType == typeof(HookResult);
        var returnsVoid = method.ReturnType == typeof(void);
        if (!returnsHookResult && !returnsVoid)
        {
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [EventHandler] method must return HookResult or void — got {method.ReturnType.Name}. Skipping.");
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
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [EventHandler] method must take 0 parameters, one EventContext, or one reference-type payload — got {parameters.Length} parameters. Skipping.");
            return null;
        }

        var paramType = parameters[0].ParameterType;

        if (paramType == typeof(EventContext))
        {
            if (returnsVoid)
            {
                var del = (Action<EventContext>)Delegate.CreateDelegate(typeof(Action<EventContext>), plugin, method);
                return ctx => { del(ctx); return HookResult.Continue; };
            }
            return (Func<EventContext, HookResult>)Delegate.CreateDelegate(typeof(Func<EventContext, HookResult>), plugin, method);
        }

        if (paramType.IsValueType)
        {
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [EventHandler] payload parameter must be a reference type — got {paramType.Name}. Skipping.");
            return null;
        }

        var delegateType = returnsHookResult
            ? typeof(Func<,>).MakeGenericType(paramType, typeof(HookResult))
            : typeof(Action<>).MakeGenericType(paramType);
        var typedDel = Delegate.CreateDelegate(delegateType, plugin, method);
        return WrapTypedEventHandler(typedDel, paramType, returnsHookResult);
    }

    /// <summary>Builds a normalized invoke delegate for a [QueryHandler]-annotated method, or null if the signature is invalid.</summary>
    private static (Func<QueryContext, object?> Invoke, Type ResponseType)? BuildAttributeQueryHandler(IDeadworksPlugin plugin, MethodInfo method)
    {
        var responseType = method.ReturnType;
        if (responseType == typeof(void))
        {
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [QueryHandler] method must return a response type — got void. Skipping.");
            return null;
        }

        var parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            var bareDelType = typeof(Func<>).MakeGenericType(responseType);
            var bareDel = Delegate.CreateDelegate(bareDelType, plugin, method);
            var invoke = (Func<QueryContext, object?>)_buildQueryBareFuncMethod
                .MakeGenericMethod(responseType).Invoke(null, [bareDel])!;
            return (invoke, responseType);
        }

        if (parameters.Length != 1)
        {
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [QueryHandler] method must take 0 parameters, one QueryContext, or one reference-type request — got {parameters.Length} parameters. Skipping.");
            return null;
        }

        var paramType = parameters[0].ParameterType;

        if (paramType == typeof(QueryContext))
        {
            var ctxDelType = typeof(Func<,>).MakeGenericType(typeof(QueryContext), responseType);
            var ctxDel = Delegate.CreateDelegate(ctxDelType, plugin, method);
            var invoke = (Func<QueryContext, object?>)_buildQueryContextFuncMethod
                .MakeGenericMethod(responseType).Invoke(null, [ctxDel])!;
            return (invoke, responseType);
        }

        if (paramType.IsValueType)
        {
            Console.WriteLine($"[PluginBus] {plugin.Name}.{method.Name}: [QueryHandler] request parameter must be a reference type — got {paramType.Name}. Skipping.");
            return null;
        }

        var typedDelType = typeof(Func<,>).MakeGenericType(paramType, responseType);
        var typedDel = Delegate.CreateDelegate(typedDelType, plugin, method);
        var typedInvoke = (Func<QueryContext, object?>)_buildQueryTypedFuncMethod
            .MakeGenericMethod(paramType, responseType).Invoke(null, [typedDel])!;
        return (typedInvoke, responseType);
    }

    // --- Manual subscribe/handle delegate normalization ---

    /// <summary>Normalizes a user-supplied event delegate into a context-invoking form, or null on shape error.</summary>
    private static Func<EventContext, HookResult>? NormalizeEventDelegate(Delegate handler)
    {
        switch (handler)
        {
            case Func<EventContext, HookResult> f: return f;
            case Action<EventContext> a: return ctx => { a(ctx); return HookResult.Continue; };
        }

        var type = handler.GetType();
        if (!type.IsGenericType) return null;

        var genericDef = type.GetGenericTypeDefinition();
        var args = type.GetGenericArguments();

        if (genericDef == typeof(Func<,>) && args.Length == 2 && args[1] == typeof(HookResult) && !args[0].IsValueType)
            return WrapTypedEventHandler(handler, args[0], returnsHookResult: true);

        if (genericDef == typeof(Action<>) && args.Length == 1 && !args[0].IsValueType)
            return WrapTypedEventHandler(handler, args[0], returnsHookResult: false);

        return null;
    }

    /// <summary>Normalizes a user-supplied query delegate into a context-invoking form returning a boxed response, or null on shape error.</summary>
    private static Func<QueryContext, object?>? NormalizeQueryDelegate(Delegate handler, Type responseType)
    {
        var type = handler.GetType();
        if (!type.IsGenericType) return null;

        var genericDef = type.GetGenericTypeDefinition();
        var args = type.GetGenericArguments();

        // Func<TResponse>
        if (genericDef == typeof(Func<>) && args.Length == 1 && args[0] == responseType)
        {
            return (Func<QueryContext, object?>)_buildQueryBareFuncMethod
                .MakeGenericMethod(responseType).Invoke(null, [handler])!;
        }

        // Func<QueryContext, TResponse> or Func<TRequest, TResponse>
        if (genericDef == typeof(Func<,>) && args.Length == 2 && args[1] == responseType)
        {
            if (args[0] == typeof(QueryContext))
            {
                return (Func<QueryContext, object?>)_buildQueryContextFuncMethod
                    .MakeGenericMethod(responseType).Invoke(null, [handler])!;
            }
            if (!args[0].IsValueType)
            {
                return (Func<QueryContext, object?>)_buildQueryTypedFuncMethod
                    .MakeGenericMethod(args[0], responseType).Invoke(null, [handler])!;
            }
        }

        return null;
    }

    // --- PluginBus entry points (wired into PluginBus.On* at init) ---

    private static IHandle OnPluginBusSubscribe(string name, Delegate handler)
    {
        var invoke = NormalizeEventDelegate(handler);
        if (invoke == null)
        {
            Console.WriteLine($"[PluginBus] Subscribe('{name}'): unsupported delegate shape {handler.GetType().Name}. Ignored.");
            return CallbackHandle.Noop;
        }

        var sub = new EventSubscription { Invoke = invoke };
        var callerPluginPath = ResolveCallingPluginPath();

        lock (_lock)
        {
            if (callerPluginPath != null)
                _eventRegistry.AddForPlugin(callerPluginPath, name, sub);
            else
                _eventRegistry.Add(name, sub);
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                _eventRegistry.Remove(name, sub);
            }
        });
    }

    private static int OnPluginBusSubscriberCount(string name)
    {
        lock (_lock)
        {
            return _eventRegistry.Snapshot(name)?.Count ?? 0;
        }
    }

    private static HookResult OnPluginBusPublish(string name, object? payload)
    {
        List<EventSubscription>? handlers;
        lock (_lock)
        {
            handlers = _eventRegistry.Snapshot(name);
        }

        var count = handlers?.Count ?? 0;
        RecordRecentPublish(name, count);

        if (count == 0)
            return HookResult.Continue;

        var sender = ResolveCallingPluginName();
        var ctx = new EventContext(name, payload, sender);

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
                Console.WriteLine($"[PluginBus] Event handler for '{name}' threw: {ex.Message}");
            }
        }

        return result;
    }

    private static IHandle OnPluginBusHandleQuery(string name, Delegate handler, Type responseType)
    {
        var invoke = NormalizeQueryDelegate(handler, responseType);
        if (invoke == null)
        {
            Console.WriteLine($"[PluginBus] HandleQuery('{name}'): unsupported delegate shape {handler.GetType().Name} for TResponse={responseType.Name}. Ignored.");
            return CallbackHandle.Noop;
        }

        var sub = new QuerySubscription { ResponseType = responseType, Invoke = invoke };
        var callerPluginPath = ResolveCallingPluginPath();

        lock (_lock)
        {
            if (callerPluginPath != null)
                _queryRegistry.AddForPlugin(callerPluginPath, name, sub);
            else
                _queryRegistry.Add(name, sub);
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                _queryRegistry.Remove(name, sub);
            }
        });
    }

    private static int OnPluginBusQueryHandlerCount(string name)
    {
        lock (_lock)
        {
            return _queryRegistry.Snapshot(name)?.Count ?? 0;
        }
    }

    private static IReadOnlyList<object?> OnPluginBusQuery(string name, object? request, Type expectedResponseType)
    {
        List<QuerySubscription>? handlers;
        lock (_lock)
        {
            handlers = _queryRegistry.Snapshot(name);
        }

        var handlerCount = handlers?.Count ?? 0;
        if (handlerCount == 0)
        {
            RecordRecentQuery(name, 0, 0);
            return Array.Empty<object?>();
        }

        var sender = ResolveCallingPluginName();
        var ctx = new QueryContext(name, request, sender);

        var responses = new List<object?>(handlerCount);
        foreach (var sub in handlers!)
        {
            if (sub.ResponseType != expectedResponseType) continue;
            try
            {
                var result = sub.Invoke(ctx);
                if (ReferenceEquals(result, s_querySkip)) continue;
                responses.Add(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginBus] Query handler for '{name}' threw: {ex.Message}");
            }
        }

        RecordRecentQuery(name, handlerCount, responses.Count);
        return responses;
    }

    // --- Diagnostic recording ---

    private static void RecordRecentPublish(string name, int subscriberCount)
    {
        lock (_recentPublishes)
        {
            _recentPublishes.Enqueue(new RecentPublish(name, subscriberCount, DateTime.UtcNow));
            while (_recentPublishes.Count > RecentHistoryCapacity)
                _recentPublishes.Dequeue();
        }
    }

    private static void RecordRecentQuery(string name, int handlerCount, int responseCount)
    {
        lock (_recentQueries)
        {
            _recentQueries.Enqueue(new RecentQuery(name, handlerCount, responseCount, DateTime.UtcNow));
            while (_recentQueries.Count > RecentHistoryCapacity)
                _recentQueries.Dequeue();
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

    internal static IReadOnlyList<RecentQuery> GetRecentQueries(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        lock (_recentQueries)
        {
            return _recentQueries.Where(p => p.At >= cutoff).ToArray();
        }
    }

    internal static IReadOnlyList<(string Name, int Count, IReadOnlyList<string> Plugins)> GetEventSubscriptions()
        => SummarizeRegistry(_eventRegistry);

    internal static IReadOnlyList<(string Name, int Count, IReadOnlyList<string> Plugins)> GetQueryHandlers()
        => SummarizeRegistry(_queryRegistry);

    private static IReadOnlyList<(string Name, int Count, IReadOnlyList<string> Plugins)> SummarizeRegistry<T>(HandlerRegistry<string, T> registry)
    {
        lock (_lock)
        {
            var rows = registry.ListHandlers();
            var result = new List<(string, int, IReadOnlyList<string>)>(rows.Count);
            foreach (var (name, count) in rows)
            {
                var plugins = registry.PluginsWithHandlerFor(name)
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

    internal static void InitializePluginBusForTests()
    {
        PluginBus.OnSubscribe = OnPluginBusSubscribe;
        PluginBus.OnPublish = OnPluginBusPublish;
        PluginBus.OnSubscriberCount = OnPluginBusSubscriberCount;
        PluginBus.OnHandleQuery = OnPluginBusHandleQuery;
        PluginBus.OnQuery = OnPluginBusQuery;
        PluginBus.OnQueryHandlerCount = OnPluginBusQueryHandlerCount;
    }

    internal static void ResetPluginBusForTests()
    {
        lock (_lock)
        {
            _eventRegistry.Clear();
            _queryRegistry.Clear();
            _contextToPluginName.Clear();
        }
        lock (_recentPublishes) { _recentPublishes.Clear(); }
        lock (_recentQueries) { _recentQueries.Clear(); }
        PluginBus.OnSubscribe = null;
        PluginBus.OnPublish = null;
        PluginBus.OnSubscriberCount = null;
        PluginBus.OnHandleQuery = null;
        PluginBus.OnQuery = null;
        PluginBus.OnQueryHandlerCount = null;
    }

    internal static void RegisterPluginBusHandlersForTests(string normalizedPath, IDeadworksPlugin plugin)
        => RegisterPluginBusHandlers(normalizedPath, [plugin]);

    internal static void UnregisterPluginBusHandlersForTests(string normalizedPath)
    {
        lock (_lock)
        {
            _eventRegistry.UnregisterPlugin(normalizedPath);
            _queryRegistry.UnregisterPlugin(normalizedPath);
        }
    }

    // --- Stack-walk helpers for caller resolution ---

    private static string ResolveCallingPluginName()
    {
        var path = ResolveCallingPluginPath();
        if (path == null) return EventContext.HostSenderName;
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
        var apiAsm = typeof(PluginBus).Assembly;

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
