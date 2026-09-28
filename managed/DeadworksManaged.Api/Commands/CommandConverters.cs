using System.Collections.Concurrent;
using System.Runtime.Loader;

namespace DeadworksManaged.Api;

/// <summary>
/// Per-type string parsers for <see cref="CommandAttribute"/> parameters, for types Deadworks doesn't parse itself.
/// Register from <c>OnLoad</c>; a plugin's converters are dropped automatically when it unloads. A parser may throw
/// <see cref="CommandException"/> to tell the caller what's wrong with their input; anything else shows the usage line.
/// </summary>
public static class CommandConverters
{
    private readonly record struct Converter(Func<string, object> Parse, AssemblyLoadContext? Owner);

    private static readonly ConcurrentDictionary<Type, Converter> _converters = new();

    /// <summary>Registers a parser for <typeparamref name="T"/>, replacing any earlier one (with a warning if another plugin's).</summary>
    public static void Register<T>(Func<string, T> parser)
    {
        var owner = AssemblyLoadContext.GetLoadContext(parser.Method.Module.Assembly);
        if (_converters.TryGetValue(typeof(T), out var existing) && existing.Owner != owner)
            Console.WriteLine($"[CommandConverters] Warning: a converter for {typeof(T).Name} from another plugin was replaced. "
                              + "Commands of both plugins now use the newer one.");
        _converters[typeof(T)] = new Converter(s => parser(s)!, owner);
    }

    /// <summary>Removes the parser for <typeparamref name="T"/>. Returns false if there wasn't one.</summary>
    public static bool Unregister<T>() => _converters.TryRemove(typeof(T), out _);

    internal static bool Has(Type type) => _converters.ContainsKey(type);

    internal static bool TryConvert(string token, Type type, out object? value)
    {
        if (_converters.TryGetValue(type, out var converter))
        {
            value = converter.Parse(token);
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Drops the converters an unloading plugin registered, or registered for its own types: a parser left behind
    /// would keep the old plugin's code loaded, and its types can't be used again anyway.
    /// </summary>
    internal static void RemoveOwnedBy(AssemblyLoadContext context)
    {
        foreach (var (type, converter) in _converters)
        {
            if (converter.Owner == context || AssemblyLoadContext.GetLoadContext(type.Assembly) == context)
                _converters.TryRemove(type, out _);
        }
    }
}
