using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FabricCosmosMirrorTypeSolutions.Solution2DualPropertyPattern;

/// <summary>
/// Adds type-specific companion properties for selected fields so Fabric mirroring
/// can land stable, type-safe columns even when the original property drifts in type.
/// </summary>
public static class DualPropertyTransformer
{
    public static JsonObject Transform(string jsonDocument, IEnumerable<string> propertiesToDualify, out DocumentTransformationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonDocument);
        ArgumentNullException.ThrowIfNull(propertiesToDualify);

        using JsonDocument parsedDocument = JsonDocument.Parse(jsonDocument);
        return Transform(parsedDocument.RootElement, propertiesToDualify, out result);
    }

    public static JsonObject Transform(JsonElement document, IEnumerable<string> propertiesToDualify, out DocumentTransformationResult result)
    {
        ArgumentNullException.ThrowIfNull(propertiesToDualify);

        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The input document must be a JSON object.", nameof(document));
        }

        // Clone the source object first so the original property names remain available to the application.
        JsonObject transformed = JsonNode.Parse(document.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("The JSON document could not be converted into a mutable object.");

        result = new DocumentTransformationResult();

        foreach (string propertyName in propertiesToDualify.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            result.PropertiesInspected++;

            if (!document.TryGetProperty(propertyName, out JsonElement value))
            {
                result.PropertiesMissing++;
                continue;
            }

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                result.PropertiesSkippedAsNull++;
                continue;
            }

            // Each suffix becomes its own mirrored Fabric column, which is what prevents type-lock nulling.
            foreach (TypedPropertyVariant variant in BuildVariants(propertyName, value))
            {
                transformed[variant.Name] = variant.Value;
                result.PropertiesAdded++;
                result.AddedPropertyNames.Add(variant.Name);
            }
        }

        return transformed;
    }

    private static IEnumerable<TypedPropertyVariant> BuildVariants(string propertyName, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            {
                decimal decimalValue = value.TryGetDecimal(out decimal parsedDecimal)
                    ? parsedDecimal
                    : Convert.ToDecimal(value.GetDouble(), CultureInfo.InvariantCulture);

                yield return new TypedPropertyVariant($"{propertyName}_float", JsonValue.Create(decimalValue)!);

                if (value.TryGetInt64(out long integerValue))
                {
                    yield return new TypedPropertyVariant($"{propertyName}_int", JsonValue.Create(integerValue)!);
                }

                break;
            }

            case JsonValueKind.String:
                yield return new TypedPropertyVariant($"{propertyName}_string", JsonValue.Create(value.GetString())!);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                yield return new TypedPropertyVariant($"{propertyName}_bool", JsonValue.Create(value.GetBoolean())!);
                break;

            case JsonValueKind.Array:
                yield return new TypedPropertyVariant($"{propertyName}_array", JsonValue.Create(value.GetRawText())!);
                break;

            case JsonValueKind.Object:
                yield return new TypedPropertyVariant($"{propertyName}_json", JsonValue.Create(value.GetRawText())!);
                break;
        }
    }

    public sealed class DocumentTransformationResult
    {
        public int PropertiesInspected { get; set; }

        public int PropertiesMissing { get; set; }

        public int PropertiesSkippedAsNull { get; set; }

        public int PropertiesAdded { get; set; }

        public List<string> AddedPropertyNames { get; } = new();
    }

    private sealed record TypedPropertyVariant(string Name, JsonNode Value);
}
