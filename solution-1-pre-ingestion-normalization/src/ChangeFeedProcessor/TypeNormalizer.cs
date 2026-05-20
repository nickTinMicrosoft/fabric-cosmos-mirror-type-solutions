using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Solution1.PreIngestionNormalization.Models;

namespace Solution1.PreIngestionNormalization.ChangeFeedProcessor;

public sealed class TypeNormalizer
{
    private static readonly object RemovedValue = new();

    private static readonly HashSet<string> UnsupportedBsonMarkers = new(StringComparer.Ordinal)
    {
        "$regularExpression",
        "$regex",
        "$dbPointer",
        "$code",
        "$symbol",
        "$minKey",
        "$maxKey",
        "$undefined"
    };

    private readonly ConcurrentDictionary<string, NormalizedType> _observedPropertyTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _forcedStringPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly NormalizationRules _rules;

    public TypeNormalizer(NormalizationRules rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public JsonDocument Normalize(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Normalize(document.RootElement);
    }

    public JsonDocument Normalize(JsonElement element)
    {
        var state = new TraversalState(_rules.MaxPropertyCount);
        var normalized = NormalizeElement(element, "$", 0, state);

        if (ReferenceEquals(normalized, RemovedValue))
        {
            normalized = new Dictionary<string, object?>();
        }

        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        return JsonDocument.Parse(json);
    }

    private object? NormalizeElement(JsonElement element, string path, int depth, TraversalState state)
    {
        if (depth >= _rules.MaxNestingDepth && (element.ValueKind == JsonValueKind.Object || element.ValueKind == JsonValueKind.Array))
        {
            // Fabric truncates documents deeper than 127 levels. Converting the remainder to JSON preserves data without breaking mirroring.
            return element.GetRawText();
        }

        if (TryHandleExtendedJson(element, out var extendedValue))
        {
            return extendedValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeObject(element, path, depth, state),
            JsonValueKind.Array => NormalizeArray(element, path, depth, state),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NormalizeNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private object? NormalizeObject(JsonElement element, string path, int depth, TraversalState state)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (path == "$" && element.TryGetProperty("id", out var idProperty) && !_rules.PropertiesToExclude.Any(excluded => string.Equals(excluded, "id", StringComparison.OrdinalIgnoreCase)))
        {
            if (state.TryRegisterProperty())
            {
                normalized["id"] = idProperty.GetString();
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (path == "$" && property.NameEquals("id"))
            {
                continue;
            }

            if (_rules.PropertiesToExclude.Any(excluded => string.Equals(excluded, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!state.TryRegisterProperty())
            {
                break;
            }

            var propertyPath = $"{path}.{property.Name}";
            var propertyValue = NormalizeElement(property.Value, propertyPath, depth + 1, state);

            if (ReferenceEquals(propertyValue, RemovedValue))
            {
                // Unsupported BSON artifacts are dropped because Fabric can otherwise null or exclude the affected values.
                continue;
            }

            propertyValue = EnsureConsistentPropertyType(propertyPath, propertyValue);
            normalized[property.Name] = propertyValue;
        }

        return normalized;
    }

    private object? NormalizeArray(JsonElement element, string path, int depth, TraversalState state)
    {
        var items = new List<object?>();

        foreach (var item in element.EnumerateArray())
        {
            var normalizedItem = NormalizeElement(item, $"{path}[]", depth + 1, state);
            if (!ReferenceEquals(normalizedItem, RemovedValue))
            {
                items.Add(normalizedItem);
            }
        }

        return HomogenizeArray(items);
    }

    private object? HomogenizeArray(List<object?> items)
    {
        var distinctTypes = items
            .Where(item => item is not null)
            .Select(GetNormalizedType)
            .Distinct()
            .ToList();

        if (distinctTypes.Count <= 1)
        {
            return items;
        }

        // Mixed-type arrays can cause Fabric to skip or incompletely mirror documents, so the default is to stringify the elements.
        return _rules.ArrayHomogenizationStrategy switch
        {
            ArrayHomogenizationStrategy.ConvertToString => items.Select(ToSafeString).ToList(),
            ArrayHomogenizationStrategy.DropIncompatible => DropIncompatible(items),
            ArrayHomogenizationStrategy.UseFirstType => KeepFirstType(items),
            _ => items.Select(ToSafeString).ToList()
        };
    }

    private List<object?> KeepFirstType(List<object?> items)
    {
        var firstNonNull = items.FirstOrDefault(item => item is not null);
        if (firstNonNull is null)
        {
            return items;
        }

        var targetType = GetNormalizedType(firstNonNull);
        return items
            .Where(item => item is null || GetNormalizedType(item) == targetType)
            .ToList();
    }

    private List<object?> DropIncompatible(List<object?> items)
    {
        var firstNonNull = items.FirstOrDefault(item => item is not null);
        if (firstNonNull is null)
        {
            return items;
        }

        var targetType = GetNormalizedType(firstNonNull);
        return items
            .Where(item => item is null || GetNormalizedType(item) == targetType)
            .ToList();
    }

    private object? EnsureConsistentPropertyType(string propertyPath, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (_forcedStringPaths.ContainsKey(propertyPath))
        {
            return ToSafeString(value);
        }

        var currentType = GetNormalizedType(value);
        var observedType = _observedPropertyTypes.GetOrAdd(propertyPath, currentType);

        if (observedType == currentType)
        {
            return value;
        }

        if (_rules.DefaultStringFallback)
        {
            // Fabric locks on the first non-null type. Promoting conflicting future values to string avoids silent downstream NULLs.
            _forcedStringPaths.TryAdd(propertyPath, 0);
            return ToSafeString(value);
        }

        return value;
    }

    private static object NormalizeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        return element.GetDouble();
    }

    private bool TryHandleExtendedJson(JsonElement element, out object? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().ToList();
        if (properties.Count == 0)
        {
            value = new Dictionary<string, object?>();
            return true;
        }

        if (properties.Count == 1 && properties[0].NameEquals("$numberDecimal"))
        {
            value = ConvertDecimal128(properties[0].Value);
            return true;
        }

        if (properties.Any(p => UnsupportedBsonMarkers.Contains(p.Name)))
        {
            value = RemovedValue;
            return true;
        }

        return false;
    }

    private object ConvertDecimal128(JsonElement element)
    {
        if (_rules.TypeMappings.TryGetValue("Decimal128", out var mappedType) && mappedType.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            return element.GetString() ?? string.Empty;
        }

        var raw = element.GetString();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return _rules.DefaultStringFallback ? raw ?? string.Empty : 0d;
    }

    private static string? ToSafeString(object? value)
    {
        return value switch
        {
            null => null,
            string stringValue => stringValue,
            bool boolValue => boolValue ? "true" : "false",
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("R", CultureInfo.InvariantCulture),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            Dictionary<string, object?> dictionaryValue => JsonSerializer.Serialize(dictionaryValue, SerializerOptions),
            List<object?> listValue => JsonSerializer.Serialize(listValue, SerializerOptions),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static NormalizedType GetNormalizedType(object value)
    {
        return value switch
        {
            string => NormalizedType.String,
            bool => NormalizedType.Boolean,
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => NormalizedType.Number,
            Dictionary<string, object?> => NormalizedType.Object,
            List<object?> => NormalizedType.Array,
            _ => NormalizedType.String
        };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private sealed class TraversalState
    {
        private int _propertyCount;
        private readonly int _maxPropertyCount;

        public TraversalState(int maxPropertyCount)
        {
            _maxPropertyCount = maxPropertyCount;
        }

        public bool TryRegisterProperty()
        {
            if (_propertyCount >= _maxPropertyCount)
            {
                return false;
            }

            _propertyCount++;
            return true;
        }
    }

    private enum NormalizedType
    {
        String,
        Number,
        Boolean,
        Object,
        Array
    }
}
