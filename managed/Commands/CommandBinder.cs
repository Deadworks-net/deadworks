using System.Reflection;
using System.Text;
using DeadworksManaged.Api;

namespace DeadworksManaged.Commands;

internal static class CommandBinder
{
    internal enum SlotKind { Caller, RawArgs, Typed, Params, Target }

    internal sealed class Slot
    {
        public required SlotKind Kind;
        public required Type Type;
        public required string Name;
        public bool HasDefault;
        public object? DefaultValue;
        public bool CallerNullable;
    }

    internal sealed class Plan
    {
        public required string Name;
        public required Slot[] Slots;
        public required bool HasCaller;
        public required bool CallerNullable;
    }

    public static Plan Build(MethodInfo method, string commandName)
    {
        var parameters = method.GetParameters();
        var slots = new Slot[parameters.Length];
        int callerIndex = -1;
        int rawArgsIndex = -1;
        int paramsIndex = -1;
        bool callerNullable = false;
        var nullCtx = new NullabilityInfoContext();

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pt = p.ParameterType;

            if (pt == typeof(CCitadelPlayerController))
            {
                if (callerIndex >= 0)
                    throw new InvalidOperationException(
                        $"[Command] method '{method.DeclaringType?.Name}.{method.Name}' has more than one CCitadelPlayerController parameter");

                var isNullable = nullCtx.Create(p).WriteState == NullabilityState.Nullable;
                slots[i] = new Slot
                {
                    Kind = SlotKind.Caller,
                    Type = pt,
                    Name = p.Name ?? $"caller",
                    CallerNullable = isNullable
                };
                callerIndex = i;
                callerNullable = isNullable;
                continue;
            }

            bool isParams = p.IsDefined(typeof(ParamArrayAttribute));
            if (isParams)
            {
                if (paramsIndex >= 0)
                    throw new InvalidOperationException(
                        $"[Command] method '{method.DeclaringType?.Name}.{method.Name}' has more than one params parameter");
                if (i != parameters.Length - 1)
                    throw new InvalidOperationException(
                        $"[Command] method '{method.DeclaringType?.Name}.{method.Name}' has a params parameter that isn't last");

                var elem = pt.GetElementType() ?? throw new InvalidOperationException(
                    $"[Command] method '{method.DeclaringType?.Name}.{method.Name}' params parameter has no element type");
                slots[i] = new Slot
                {
                    Kind = SlotKind.Params,
                    Type = elem,
                    Name = p.Name ?? $"args"
                };
                paramsIndex = i;
                continue;
            }

            if (pt == typeof(string[]) && string.Equals(p.Name, "rawArgs", StringComparison.OrdinalIgnoreCase))
            {
                if (rawArgsIndex >= 0)
                    throw new InvalidOperationException(
                        $"[Command] method '{method.DeclaringType?.Name}.{method.Name}' has more than one rawArgs parameter");

                slots[i] = new Slot
                {
                    Kind = SlotKind.RawArgs,
                    Type = pt,
                    Name = p.Name ?? "rawArgs"
                };
                rawArgsIndex = i;
                continue;
            }

            if (pt == typeof(Target))
            {
                slots[i] = new Slot
                {
                    Kind = SlotKind.Target,
                    Type = pt,
                    Name = p.Name ?? $"arg{i}",
                    HasDefault = p.HasDefaultValue,
                    DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
                };
                continue;
            }

            slots[i] = new Slot
            {
                Kind = SlotKind.Typed,
                Type = pt,
                Name = p.Name ?? $"arg{i}",
                HasDefault = p.HasDefaultValue,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            };
        }

        return new Plan
        {
            Name = commandName,
            Slots = slots,
            HasCaller = callerIndex >= 0,
            CallerNullable = callerNullable
        };
    }

    public static bool TryBind(
        Plan plan,
        string[] tokens,
        CCitadelPlayerController? caller,
        out object?[] boundArgs,
        out string? error,
        out bool silentSkip,
        bool enforceImmunity = false)
    {
        error = null;
        silentSkip = false;
        boundArgs = new object?[plan.Slots.Length];
        int tokenIdx = 0;

        for (int i = 0; i < plan.Slots.Length; i++)
        {
            var slot = plan.Slots[i];
            switch (slot.Kind)
            {
                case SlotKind.Caller:
                    if (caller == null && !slot.CallerNullable)
                    {
                        silentSkip = true;
                        return false;
                    }
                    boundArgs[i] = caller;
                    break;

                case SlotKind.RawArgs:
                    boundArgs[i] = tokens;
                    break;

                case SlotKind.Typed:
                    if (tokenIdx >= tokens.Length)
                    {
                        if (slot.HasDefault)
                        {
                            boundArgs[i] = slot.DefaultValue;
                            break;
                        }
                        error = BuildUsage(plan);
                        return false;
                    }
                    try
                    {
                        boundArgs[i] = Convert(tokens[tokenIdx], slot.Type);
                        tokenIdx++;
                    }
                    catch
                    {
                        error = BuildUsage(plan);
                        return false;
                    }
                    break;

                case SlotKind.Target:
                    if (tokenIdx >= tokens.Length)
                    {
                        if (slot.HasDefault)
                        {
                            boundArgs[i] = slot.DefaultValue;
                            break;
                        }
                        error = BuildUsage(plan);
                        return false;
                    }
                    if (!TargetResolver.TryResolve(tokens[tokenIdx], caller, enforceImmunity, out var target, out var targetError))
                    {
                        error = targetError;
                        return false;
                    }
                    boundArgs[i] = target;
                    tokenIdx++;
                    break;

                case SlotKind.Params:
                    int remaining = tokens.Length - tokenIdx;
                    var arr = Array.CreateInstance(slot.Type, remaining);
                    try
                    {
                        for (int j = 0; j < remaining; j++)
                            arr.SetValue(Convert(tokens[tokenIdx + j], slot.Type), j);
                    }
                    catch
                    {
                        error = BuildUsage(plan);
                        return false;
                    }
                    boundArgs[i] = arr;
                    tokenIdx += remaining;
                    break;
            }
        }

        if (tokenIdx < tokens.Length && !HasParams(plan) && !HasRawArgs(plan))
        {
            error = BuildUsage(plan);
            return false;
        }

        return true;
    }

    public static object Convert(string token, Type type)
    {
        if (type.IsEnum)
            return Enum.Parse(type, token, ignoreCase: true);

        if (IsBuiltInScalar(type))
            return ConCommandManager.ConvertValue(token, type);

        if (CommandConverters.TryConvert(token, type, out var value))
            return value!;

        throw new NotSupportedException($"No converter registered for type '{type.Name}'");
    }

    private static bool IsBuiltInScalar(Type type) =>
        type == typeof(int) || type == typeof(long) ||
        type == typeof(float) || type == typeof(double) ||
        type == typeof(bool) || type == typeof(string);

    private static bool HasParams(Plan plan)
    {
        foreach (var s in plan.Slots) if (s.Kind == SlotKind.Params) return true;
        return false;
    }

    private static bool HasRawArgs(Plan plan)
    {
        foreach (var s in plan.Slots) if (s.Kind == SlotKind.RawArgs) return true;
        return false;
    }

    public static string BuildUsage(Plan plan)
    {
        var sb = new StringBuilder("Usage: ");
        sb.Append(plan.Name);

        foreach (var slot in plan.Slots)
        {
            if (slot.Kind == SlotKind.Caller || slot.Kind == SlotKind.RawArgs)
                continue;

            sb.Append(' ');
            if (slot.Kind == SlotKind.Params)
            {
                sb.Append('[');
                sb.Append(slot.Name);
                sb.Append(':');
                sb.Append(TypeLabel(slot.Type));
                sb.Append("...]");
                continue;
            }

            bool optional = slot.HasDefault;
            sb.Append(optional ? '[' : '<');
            sb.Append(slot.Name);
            sb.Append(':');
            sb.Append(TypeLabel(slot.Type));
            if (optional && slot.DefaultValue != null)
            {
                sb.Append('=');
                sb.Append(slot.DefaultValue);
            }
            sb.Append(optional ? ']' : '>');
        }

        return sb.ToString();
    }

    private static string TypeLabel(Type type)
    {
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(string)) return "string";
        if (type == typeof(Target)) return "player";
        return type.Name;
    }
}
