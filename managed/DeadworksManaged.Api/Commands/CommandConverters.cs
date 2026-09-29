using System.Collections.Concurrent;
using System.Runtime.Loader;

namespace DeadworksManaged.Api;

/// <summary>
/// Per-type string parsers for <see cref="CommandAttribute"/> parameters, for types Deadworks doesn't parse itself.
/// Register from <c>OnLoad</c>; a plugin's converters are dropped automatically when it unloads. A parser may throw
/// <see cref="CommandException"/> to tell the caller what's wrong with their input; anything else shows the usage line.
/// A converter applies to every plugin's commands, so prefer registering one for your own types; one for a shared type
/// such as <see cref="TimeSpan"/> replaces any other plugin's.
/// </summary>
public static class CommandConverters
{
    private readonly record struct Converter(Func<string, object> Parse, AssemblyLoadContext? Owner);

    private static readonly ConcurrentDictionary<Type, Converter> _converters = new();

    /// <summary>Registers a parser for <typeparamref name="T"/>, replacing any earlier one (with a warning if another plugin's).</summary>
    /// <exception cref="ArgumentException">
    /// Deadworks parses <typeparamref name="T"/> itself: numbers, <see cref="bool"/>, <see cref="string"/>, enums,
    /// <see cref="Caller"/>, <see cref="Target"/> and <see cref="CCitadelPlayerController"/>. Take a <see cref="string"/>
    /// parameter and parse it in the command instead.
    /// </exception>
    public static void Register<T>(Func<string, T> parser)
    {
        if (IsBuiltIn(typeof(T)))
            throw new ArgumentException($"Deadworks parses {typeof(T).Name} command arguments itself, so a converter for it would never be used. "
                                        + "Take a string parameter and parse it in the command instead.", nameof(T));
        var owner = AssemblyLoadContext.GetLoadContext(parser.Method.Module.Assembly);
        if (_converters.TryGetValue(typeof(T), out var existing) && existing.Owner != owner)
            Console.WriteLine($"[CommandConverters] Warning: a converter for {typeof(T).Name} from another plugin was replaced. "
                              + "Commands of both plugins now use the newer one.");
        _converters[typeof(T)] = new Converter(s => parser(s)!, owner);
    }

    /// <summary>Removes the parser for <typeparamref name="T"/>. Returns false if there wasn't one.</summary>
    public static bool Unregister<T>() => _converters.TryRemove(typeof(T), out _);

    internal static bool Has(Type type) => _converters.ContainsKey(type);

    /// <summary>Types the command binder parses before it looks for a converter.</summary>
    internal static bool IsBuiltIn(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(uint) || type == typeof(ulong)
               || type == typeof(float) || type == typeof(double) || type == typeof(bool) || type == typeof(string)
               || type == typeof(Caller) || type == typeof(Target) || type == typeof(CCitadelPlayerController);
    }

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
