using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadworksManaged;

/// <summary>
/// Finds keys in a hand-edited JSON file that the type it's read into doesn't have. System.Text.Json ignores them, so a
/// typo like "immunty" silently does nothing; this lets the loader say so instead. Refusing the file would be worse:
/// one typo would then take away everyone's roles.
/// </summary>
internal static class UnknownJsonKeys
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Each unknown key as <c>where: 'key'</c>, with a suggestion when a known key is close. Empty if the JSON doesn't parse.</summary>
    public static List<string> Find(string json, Type type)
    {
        var found = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            Walk(doc.RootElement, type, "", found);
        }
        catch (JsonException)
        {
            // The loader reports the parse error itself.
        }
        return found;
    }

    /// <summary>Prints each unknown key in <paramref name="file"/> as a warning with <paramref name="prefix"/>.</summary>
    public static void Warn(string json, Type type, string file, string prefix)
    {
        foreach (var key in Find(json, type))
            Console.WriteLine($"{prefix} {file}: {key} isn't a setting Deadworks knows, so it's ignored.");
    }

    private static void Walk(JsonElement element, Type type, string path, List<string> found)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (element.ValueKind == JsonValueKind.Array && ElementType(type) is { } itemType)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
                Walk(item, itemType, $"{path}[{i++}]", found);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object || IsLeaf(type))
            return;

        if (DictionaryValueType(type) is { } valueType)
        {
            foreach (var entry in element.EnumerateObject())
                Walk(entry.Value, valueType, Join(path, entry.Name), found);
            return;
        }

        var known = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null && p.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
            // Named as the files write them: an explicit JSON name, otherwise camelCase.
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name),
                p => p.PropertyType, StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (known.TryGetValue(property.Name, out var propertyType))
            {
                Walk(property.Value, propertyType, Join(path, property.Name), found);
                continue;
            }
            var suggestion = known.Keys.Where(k => Distance(k, property.Name) <= 2).OrderBy(k => Distance(k, property.Name)).FirstOrDefault();
            var where = path.Length > 0 ? $"in '{path}', " : "";
            found.Add($"{where}'{property.Name}'{(suggestion != null ? $" (did you mean '{suggestion}'?)" : "")}");
        }
    }

    private static string Join(string path, string key) => path.Length > 0 ? $"{path}.{key}" : key;

    private static bool IsLeaf(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                                             || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(object);

    private static Type? DictionaryValueType(Type type)
        => type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            ?.GetGenericArguments()[1];

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return null;
        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }
}
