using System.Reflection;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Entity IO hook registries ---
    //
    // Two parallel maps (input vs output). Each keyed by (ClassName, Name, Mode) — case-sensitive,
    // ordinal comparison, exact-match. Wildcard lookup is performed at dispatch time by checking
    // four keys: (class, name) → (class, *) → (*, name) → (*, *).

    private readonly record struct IOKey(string ClassName, string Name, HookMode Mode);
    private sealed record IOEntry(Delegate Handler, string? OwnerPath);

    private static readonly Dictionary<IOKey, List<IOEntry>> _entityInputHooks = new();
    private static readonly Dictionary<IOKey, List<IOEntry>> _entityOutputHooks = new();
    // Per-plugin tracking so plugin unload sweeps handlers automatically.
    private static readonly Dictionary<string, List<(Dictionary<IOKey, List<IOEntry>> Map, IOKey Key, IOEntry Entry)>> _pluginEntityIOHandlers = new(StringComparer.OrdinalIgnoreCase);

    // Programmatic API entrypoints — wired in PluginLoader.Initialize via EntityIO.OnHookInput / OnHookOutput.
    private static IHandle OnEntityIOHookInputProgrammatic(string className, string inputName, Delegate handler, HookMode mode)
        => RegisterIOHook(_entityInputHooks, className, inputName, handler, mode, ownerPath: null);

    private static IHandle OnEntityIOHookOutputProgrammatic(string className, string outputName, Delegate handler, HookMode mode)
        => RegisterIOHook(_entityOutputHooks, className, outputName, handler, mode, ownerPath: null);

    private static IHandle RegisterIOHook(Dictionary<IOKey, List<IOEntry>> map,
                                          string className, string name, Delegate handler, HookMode mode,
                                          string? ownerPath)
    {
        var key = new IOKey(className, name, mode);
        var entry = new IOEntry(handler, ownerPath);

        lock (_lock)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<IOEntry>();
                map[key] = list;
            }
            list.Add(entry);

            if (ownerPath != null)
            {
                if (!_pluginEntityIOHandlers.TryGetValue(ownerPath, out var owned))
                {
                    owned = new List<(Dictionary<IOKey, List<IOEntry>>, IOKey, IOEntry)>();
                    _pluginEntityIOHandlers[ownerPath] = owned;
                }
                owned.Add((map, key, entry));
            }
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                RemoveEntry(map, key, entry);
            }
        });
    }

    // Must be called under _lock.
    private static void RemoveEntry(Dictionary<IOKey, List<IOEntry>> map, IOKey key, IOEntry entry)
    {
        if (map.TryGetValue(key, out var list))
        {
            list.Remove(entry);
            if (list.Count == 0)
                map.Remove(key);
        }
    }

    // --- Attribute-based discovery ---

    private static void RegisterPluginEntityIOHooks(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                foreach (var attr in method.GetCustomAttributes<EntityInputHookAttribute>())
                    TryRegisterAttributeHook(_entityInputHooks, plugin, method, attr.ClassName, attr.InputName, attr.Mode,
                                              typeof(EntityInputEvent), normalizedPath, "input");
                foreach (var attr in method.GetCustomAttributes<EntityOutputHookAttribute>())
                    TryRegisterAttributeHook(_entityOutputHooks, plugin, method, attr.ClassName, attr.OutputName, attr.Mode,
                                              typeof(EntityOutputEvent), normalizedPath, "output");
            }
        }
    }

    private static void TryRegisterAttributeHook(Dictionary<IOKey, List<IOEntry>> map,
                                                  IDeadworksPlugin plugin, MethodInfo method,
                                                  string className, string name, HookMode mode,
                                                  Type eventType, string normalizedPath, string label)
    {
        // Pre-mode: must return HookResult and take (EventType). Post-mode: must return void and take (EventType).
        bool isPre = mode == HookMode.Pre;
        Type delegateType = isPre
            ? typeof(Func<,>).MakeGenericType(eventType, typeof(HookResult))
            : typeof(Action<>).MakeGenericType(eventType);

        Delegate del;
        try
        {
            del = Delegate.CreateDelegate(delegateType, plugin, method);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLoader] {plugin.Name}.{method.Name}: cannot bind [Entity{(label == "input" ? "Input" : "Output")}Hook] in {mode} mode — signature must be {(isPre ? $"HookResult({eventType.Name})" : $"void({eventType.Name})")}. ({ex.Message})");
            return;
        }

        RegisterIOHook(map, className, name, del, mode, ownerPath: normalizedPath);
        PluginRegistrationTracker.Add(normalizedPath, $"entity-{label}", $"{className}:{name} ({mode})");
        Console.WriteLine($"[PluginLoader] Registered entity {label} hook: {plugin.Name}.{method.Name} -> {className}:{name} ({mode})");
    }

    private static void UnregisterPluginEntityIOHooks(string normalizedPath)
    {
        lock (_lock)
        {
            if (!_pluginEntityIOHandlers.Remove(normalizedPath, out var owned))
                return;

            foreach (var (map, key, entry) in owned)
                RemoveEntry(map, key, entry);
        }
    }

    // --- Dispatch from EntryPoint.cs ---
    //
    // Wildcard search order: most-specific to least-specific. Pre-mode aggregates results with max(),
    // short-circuits on Stop. Post-mode runs everything and returns void.

    internal static int DispatchEntityAcceptInputPre(string className, EntityInputEvent evt)
        => DispatchIO(_entityInputHooks, className, evt.InputName, HookMode.Pre, evt, label: "input");

    internal static void DispatchEntityAcceptInputPost(string className, EntityInputEvent evt)
        => DispatchIO(_entityInputHooks, className, evt.InputName, HookMode.Post, evt, label: "input");

    internal static int DispatchEntityFireOutputPre(string callerClass, EntityOutputEvent evt)
        => DispatchIO(_entityOutputHooks, callerClass, evt.OutputName, HookMode.Pre, evt, label: "output");

    internal static void DispatchEntityFireOutputPost(string callerClass, EntityOutputEvent evt)
        => DispatchIO(_entityOutputHooks, callerClass, evt.OutputName, HookMode.Post, evt, label: "output");

    private static int DispatchIO<TEvent>(Dictionary<IOKey, List<IOEntry>> map,
                                           string className, string name, HookMode mode,
                                           TEvent evt, string label) where TEvent : class
    {
        // Snapshot all four wildcard buckets under the lock.
        IOEntry[]? entries = null;
        lock (_lock)
        {
            var keys = new[] {
                new IOKey(className, name, mode),
                new IOKey(className, "*", mode),
                new IOKey("*", name, mode),
                new IOKey("*", "*", mode),
            };

            int total = 0;
            foreach (var k in keys)
                if (map.TryGetValue(k, out var l)) total += l.Count;
            if (total == 0) return (int)HookResult.Continue;

            entries = new IOEntry[total];
            int i = 0;
            foreach (var k in keys)
                if (map.TryGetValue(k, out var l))
                    foreach (var e in l) entries[i++] = e;
        }

        var result = HookResult.Continue;
        foreach (var entry in entries)
        {
            try
            {
                if (mode == HookMode.Pre)
                {
                    var hr = (HookResult)((Func<TEvent, HookResult>)entry.Handler)(evt);
                    if (hr > result) result = hr;
                    if (result >= HookResult.Stop) break;
                }
                else
                {
                    ((Action<TEvent>)entry.Handler)(evt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Entity {label} hook for '{className}:{name}' ({mode}) threw: {ex.Message}");
            }
        }
        return (int)result;
    }
}
