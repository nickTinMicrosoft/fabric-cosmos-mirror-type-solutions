using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solution5.SchemaGovernance.SchemaValidator;

/// <summary>
/// Specifies how schema violations should be handled.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Reject documents when violations are detected.
    /// </summary>
    Strict,

    /// <summary>
    /// Attempt safe coercions and default-value fixes before deciding whether to reject the document.
    /// </summary>
    Normalize
}

/// <summary>
/// Validates JSON documents before they are written to Azure Cosmos DB.
/// </summary>
public sealed class CosmosSchemaValidator
{
    private static readonly JsonSerializerOptions RulesSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly CollectionSchemaRules _rules;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosSchemaValidator"/> class.
    /// </summary>
    /// <param name="rules">The schema rules to enforce.</param>
    public CosmosSchemaValidator(CollectionSchemaRules rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _rules.Normalize();
    }

    /// <summary>
    /// Loads schema rules from a JSON file.
    /// </summary>
    /// <param name="path">The absolute or relative path to the rules file.</param>
    /// <returns>The deserialized schema rules.</returns>
    public static CollectionSchemaRules LoadRulesFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        var rules = JsonSerializer.Deserialize<CollectionSchemaRules>(stream, RulesSerializerOptions)
            ?? throw new InvalidOperationException($"Schema rules file '{path}' could not be deserialized.");

        rules.Normalize();
        return rules;
    }

    /// <summary>
    /// Creates a validator directly from a JSON rules file.
    /// </summary>
    /// <param name="path">The path to the schema rules file.</param>
    /// <returns>A configured validator instance.</returns>
    public static CosmosSchemaValidator CreateFromFile(string path) => new(LoadRulesFromFile(path));

    /// <summary>
    /// Validates a JSON payload string.
    /// </summary>
    /// <param name="jsonDocument">The raw JSON document to validate.</param>
    /// <param name="mode">The requested enforcement mode.</param>
    /// <returns>The validation result, including any normalized payload.</returns>
    public ValidationResult Validate(string jsonDocument, ValidationMode mode = ValidationMode.Strict)
    {
        if (string.IsNullOrWhiteSpace(jsonDocument))
        {
            return InvalidResult(new ValidationViolation(
                "$",
                "object",
                "empty",
                "The request body is empty and cannot be written to Cosmos DB.",
                ValidationSeverity.Error));
        }

        JsonNode? parsedDocument;

        try
        {
            parsedDocument = JsonNode.Parse(jsonDocument);
        }
        catch (JsonException ex)
        {
            return InvalidResult(new ValidationViolation(
                "$",
                "valid JSON",
                "invalid JSON",
                $"The request body could not be parsed: {ex.Message}",
                ValidationSeverity.Error));
        }

        if (parsedDocument is not JsonObject documentObject)
        {
            return InvalidResult(new ValidationViolation(
                "$",
                "object",
                GetJsonType(parsedDocument),
                "The root document must be a JSON object.",
                ValidationSeverity.Error));
        }

        return Validate(documentObject, mode);
    }

    /// <summary>
    /// Validates a mutable JSON object.
    /// </summary>
    /// <param name="document">The JSON document to validate.</param>
    /// <param name="mode">The requested enforcement mode.</param>
    /// <returns>The validation result.</returns>
    public ValidationResult Validate(JsonObject document, ValidationMode mode = ValidationMode.Strict)
    {
        ArgumentNullException.ThrowIfNull(document);

        var workingDocument = CloneNode(document)?.AsObject()
            ?? throw new InvalidOperationException("A working copy of the document could not be created.");

        var violations = new List<ValidationViolation>();
        var propertyCount = CountProperties(workingDocument);
        var depth = CalculateDepth(workingDocument);

        if (propertyCount > _rules.MaxPropertyCount)
        {
            violations.Add(new ValidationViolation(
                "$",
                $"<= {_rules.MaxPropertyCount} properties",
                propertyCount.ToString(CultureInfo.InvariantCulture),
                $"The document contains {propertyCount} properties, which exceeds the limit of {_rules.MaxPropertyCount}.",
                ValidationSeverity.Error));
        }

        if (depth > _rules.MaxDepth)
        {
            violations.Add(new ValidationViolation(
                "$",
                $"<= {_rules.MaxDepth} levels",
                depth.ToString(CultureInfo.InvariantCulture),
                $"The document nesting depth of {depth} exceeds the supported maximum of {_rules.MaxDepth}.",
                ValidationSeverity.Error));
        }

        ValidateObject(
            workingDocument,
            _rules.Properties,
            _rules.RequiredProperties,
            _rules.AllowAdditionalProperties,
            "$",
            mode,
            violations);

        var isValid = violations.All(v => v.Severity != ValidationSeverity.Error);

        return new ValidationResult
        {
            IsValid = isValid,
            Violations = violations.AsReadOnly(),
            NormalizedDocument = mode == ValidationMode.Normalize ? workingDocument : null
        };
    }

    private void ValidateObject(
        JsonObject document,
        IReadOnlyDictionary<string, PropertyRule> propertyRules,
        IReadOnlyCollection<string> requiredProperties,
        bool allowAdditionalProperties,
        string path,
        ValidationMode mode,
        IList<ValidationViolation> violations)
    {
        foreach (var requiredProperty in requiredProperties)
        {
            if (document.ContainsKey(requiredProperty))
            {
                continue;
            }

            propertyRules.TryGetValue(requiredProperty, out var requiredRule);

            if (mode == ValidationMode.Normalize && requiredRule?.DefaultValue is not null)
            {
                document[requiredProperty] = CloneNode(requiredRule.DefaultValue);
                violations.Add(new ValidationViolation(
                    BuildPath(path, requiredProperty),
                    requiredRule.Type,
                    "missing",
                    $"Inserted default value for missing required property '{requiredProperty}'.",
                    ValidationSeverity.Warning));
                continue;
            }

            violations.Add(new ValidationViolation(
                BuildPath(path, requiredProperty),
                requiredRule?.Type ?? "defined value",
                "missing",
                $"Required property '{requiredProperty}' is missing.",
                ValidationSeverity.Error));
        }

        if (!allowAdditionalProperties)
        {
            foreach (var property in document)
            {
                if (propertyRules.ContainsKey(property.Key))
                {
                    continue;
                }

                violations.Add(new ValidationViolation(
                    BuildPath(path, property.Key),
                    "defined property",
                    "additional property",
                    $"Property '{property.Key}' is not permitted by the schema.",
                    ValidationSeverity.Error));
            }
        }

        foreach (var propertyRule in propertyRules)
        {
            if (!document.TryGetPropertyValue(propertyRule.Key, out var value))
            {
                continue;
            }

            var normalizedValue = ValidateNode(value, propertyRule.Value, BuildPath(path, propertyRule.Key), mode, violations);
            document[propertyRule.Key] = normalizedValue;
        }
    }

    private JsonNode? ValidateNode(
        JsonNode? node,
        PropertyRule rule,
        string path,
        ValidationMode mode,
        IList<ValidationViolation> violations)
    {
        if (node is null)
        {
            if (rule.AllowNull)
            {
                return null;
            }

            if (mode == ValidationMode.Normalize && rule.DefaultValue is not null)
            {
                violations.Add(new ValidationViolation(
                    path,
                    rule.Type,
                    "null",
                    "Null value replaced with the configured default.",
                    ValidationSeverity.Warning));
                return CloneNode(rule.DefaultValue);
            }

            violations.Add(new ValidationViolation(
                path,
                rule.Type,
                "null",
                "Null is not allowed for this property.",
                ValidationSeverity.Error));
            return node;
        }

        var actualType = GetJsonType(node);
        var normalizedNode = node;

        if (!TypeMatches(actualType, rule.Type))
        {
            if (mode == ValidationMode.Normalize && TryNormalizeValue(node, rule, out var coercedNode, out var normalizationMessage))
            {
                normalizedNode = coercedNode;
                actualType = GetJsonType(normalizedNode);
                violations.Add(new ValidationViolation(
                    path,
                    rule.Type,
                    GetJsonType(node),
                    normalizationMessage,
                    ValidationSeverity.Warning));
            }
            else
            {
                violations.Add(new ValidationViolation(
                    path,
                    rule.Type,
                    actualType,
                    $"Expected type '{rule.Type}' but found '{actualType}'.",
                    ValidationSeverity.Error));
                return node;
            }
        }

        if (normalizedNode is JsonValue valueNode)
        {
            normalizedNode = ValidateScalar(valueNode, rule, path, mode, violations);
        }
        else if (normalizedNode is JsonObject objectNode)
        {
            ValidateObject(
                objectNode,
                rule.Properties ?? EmptyProperties,
                GetRequiredProperties(rule),
                rule.AllowAdditionalProperties,
                path,
                mode,
                violations);
        }
        else if (normalizedNode is JsonArray arrayNode)
        {
            ValidateArray(arrayNode, rule, path, mode, violations);
        }

        return normalizedNode;
    }

    private JsonNode? ValidateScalar(
        JsonValue valueNode,
        PropertyRule rule,
        string path,
        ValidationMode mode,
        IList<ValidationViolation> violations)
    {
        if (rule.Type.Equals("string", StringComparison.OrdinalIgnoreCase) && valueNode.TryGetValue<string>(out var stringValue))
        {
            if (rule.MaxLength is int maxLength && stringValue.Length > maxLength)
            {
                if (mode == ValidationMode.Normalize)
                {
                    var truncated = stringValue[..maxLength];
                    violations.Add(new ValidationViolation(
                        path,
                        $"string(maxLength:{maxLength})",
                        $"string(length:{stringValue.Length})",
                        $"String value exceeded {maxLength} characters and was truncated.",
                        ValidationSeverity.Warning));
                    valueNode = JsonValue.Create(truncated)!;
                    stringValue = truncated;
                }
                else
                {
                    violations.Add(new ValidationViolation(
                        path,
                        $"string(maxLength:{maxLength})",
                        $"string(length:{stringValue.Length})",
                        $"String value exceeds the configured maximum length of {maxLength}.",
                        ValidationSeverity.Error));
                }
            }

            if (rule.AllowedValues is { Count: > 0 } && !rule.AllowedValues.Contains(stringValue, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new ValidationViolation(
                    path,
                    string.Join(", ", rule.AllowedValues),
                    stringValue,
                    "Value is not in the list of allowed values.",
                    ValidationSeverity.Error));
            }

            return valueNode;
        }

        if (rule.Type.Equals("number", StringComparison.OrdinalIgnoreCase) && !IsNumericJsonValue(valueNode))
        {
            violations.Add(new ValidationViolation(
                path,
                "number",
                GetJsonType(valueNode),
                "The value could not be represented as a numeric type.",
                ValidationSeverity.Error));
        }

        if (rule.Type.Equals("boolean", StringComparison.OrdinalIgnoreCase) && !valueNode.TryGetValue<bool>(out _))
        {
            violations.Add(new ValidationViolation(
                path,
                "boolean",
                GetJsonType(valueNode),
                "The value could not be represented as a boolean.",
                ValidationSeverity.Error));
        }

        return valueNode;
    }

    private void ValidateArray(
        JsonArray array,
        PropertyRule rule,
        string path,
        ValidationMode mode,
        IList<ValidationViolation> violations)
    {
        if (rule.MinItems is int minItems && array.Count < minItems)
        {
            violations.Add(new ValidationViolation(
                path,
                $"array(minItems:{minItems})",
                $"array(count:{array.Count})",
                $"Array contains {array.Count} items but requires at least {minItems}.",
                ValidationSeverity.Error));
        }

        string? homogeneousType = null;

        for (var index = 0; index < array.Count; index++)
        {
            var currentItem = array[index];
            var itemPath = $"{path}[{index}]";

            if (rule.Items is not null)
            {
                var normalizedItem = ValidateNode(currentItem, rule.Items, itemPath, mode, violations);
                array[index] = normalizedItem;
                currentItem = normalizedItem;
            }

            var itemType = GetJsonType(currentItem);

            if (homogeneousType is null && itemType != "null")
            {
                homogeneousType = itemType;
                continue;
            }

            if (homogeneousType is not null && itemType != "null" && !homogeneousType.Equals(itemType, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new ValidationViolation(
                    itemPath,
                    homogeneousType,
                    itemType,
                    "Array elements must remain homogeneous to avoid schema drift in Fabric mirroring.",
                    ValidationSeverity.Error));
            }
        }
    }

    private static bool TryNormalizeValue(JsonNode node, PropertyRule rule, out JsonNode? normalizedNode, out string message)
    {
        normalizedNode = node;
        message = "The value was normalized.";

        var expectedType = rule.Type.ToLowerInvariant();

        switch (expectedType)
        {
            case "string":
                if (TryExtractScalarString(node, out var scalarText))
                {
                    normalizedNode = JsonValue.Create(scalarText);
                    message = "Converted scalar value to string.";
                    return true;
                }

                break;

            case "number":
                if (node is JsonValue stringNumberNode && stringNumberNode.TryGetValue<string>(out var numericText)
                    && decimal.TryParse(numericText, NumberStyles.Number, CultureInfo.InvariantCulture, out var numericValue))
                {
                    normalizedNode = JsonValue.Create(numericValue);
                    message = "Parsed numeric string into a number.";
                    return true;
                }

                break;

            case "boolean":
                if (node is JsonValue stringBooleanNode)
                {
                    if (stringBooleanNode.TryGetValue<string>(out var booleanText))
                    {
                        if (bool.TryParse(booleanText, out var booleanValue))
                        {
                            normalizedNode = JsonValue.Create(booleanValue);
                            message = "Parsed boolean string into a boolean.";
                            return true;
                        }

                        if (booleanText == "1" || booleanText.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        {
                            normalizedNode = JsonValue.Create(true);
                            message = "Normalized truthy string into boolean true.";
                            return true;
                        }

                        if (booleanText == "0" || booleanText.Equals("no", StringComparison.OrdinalIgnoreCase))
                        {
                            normalizedNode = JsonValue.Create(false);
                            message = "Normalized falsy string into boolean false.";
                            return true;
                        }
                    }

                    if (stringBooleanNode.TryGetValue<int>(out var numericBoolean))
                    {
                        if (numericBoolean == 0 || numericBoolean == 1)
                        {
                            normalizedNode = JsonValue.Create(numericBoolean == 1);
                            message = "Converted numeric boolean indicator into a boolean.";
                            return true;
                        }
                    }
                }

                break;

            case "array":
                if (rule.Items is not null)
                {
                    if (TryNormalizeValue(node, rule.Items, out var normalizedItem, out _))
                    {
                        normalizedNode = new JsonArray(normalizedItem);
                        message = "Wrapped a scalar value into a single-item array after normalizing the element type.";
                        return true;
                    }

                    if (TypeMatches(GetJsonType(node), rule.Items.Type))
                    {
                        normalizedNode = new JsonArray(CloneNode(node));
                        message = "Wrapped a scalar value into a single-item array.";
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static bool TryExtractScalarString(JsonNode? node, out string value)
    {
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var stringValue))
            {
                value = stringValue;
                return true;
            }

            if (scalar.TryGetValue<bool>(out var boolValue))
            {
                value = boolValue.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
                return true;
            }

            if (scalar.TryGetValue<decimal>(out var decimalValue))
            {
                value = decimalValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (scalar.TryGetValue<double>(out var doubleValue))
            {
                value = doubleValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (scalar.TryGetValue<long>(out var longValue))
            {
                value = longValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string GetJsonType(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonObject)
        {
            return "object";
        }

        if (node is JsonArray)
        {
            return "array";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out _))
            {
                return "string";
            }

            if (value.TryGetValue<bool>(out _))
            {
                return "boolean";
            }

            if (IsNumericJsonValue(value))
            {
                return "number";
            }
        }

        return "unknown";
    }

    private static bool IsNumericJsonValue(JsonValue value) =>
        value.TryGetValue<byte>(out _)
        || value.TryGetValue<short>(out _)
        || value.TryGetValue<int>(out _)
        || value.TryGetValue<long>(out _)
        || value.TryGetValue<float>(out _)
        || value.TryGetValue<double>(out _)
        || value.TryGetValue<decimal>(out _);

    private static bool TypeMatches(string actualType, string expectedType) =>
        actualType.Equals(expectedType, StringComparison.OrdinalIgnoreCase);

    private static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();

    private static int CountProperties(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => obj.Count + obj.Sum(property => CountProperties(property.Value)),
            JsonArray array => array.Sum(CountProperties),
            _ => 0
        };
    }

    private static int CalculateDepth(JsonNode? node)
    {
        return node switch
        {
            null => 0,
            JsonObject obj when obj.Count == 0 => 1,
            JsonArray array when array.Count == 0 => 1,
            JsonObject obj => 1 + obj.Max(property => CalculateDepth(property.Value)),
            JsonArray array => 1 + array.Max(CalculateDepth),
            _ => 1
        };
    }

    private static string BuildPath(string parentPath, string propertyName) =>
        parentPath == "$" ? $"$.{propertyName}" : $"{parentPath}.{propertyName}";

    private static ValidationResult InvalidResult(ValidationViolation violation) => new()
    {
        IsValid = false,
        Violations = new[] { violation }
    };

    private static IReadOnlyCollection<string> GetRequiredProperties(PropertyRule rule)
    {
        if (rule.Properties is null || rule.Properties.Count == 0)
        {
            return Array.Empty<string>();
        }

        return rule.Properties
            .Where(property => property.Value.Required)
            .Select(property => property.Key)
            .ToArray();
    }

    private static readonly IReadOnlyDictionary<string, PropertyRule> EmptyProperties =
        new Dictionary<string, PropertyRule>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents schema rules for a Cosmos DB collection.
/// </summary>
public sealed class CollectionSchemaRules
{
    /// <summary>
    /// Gets or sets the logical collection name.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a description of the schema rule set.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed nesting depth.
    /// </summary>
    public int MaxDepth { get; set; } = 127;

    /// <summary>
    /// Gets or sets the maximum allowed property count.
    /// </summary>
    public int MaxPropertyCount { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the required properties at the document root.
    /// </summary>
    public List<string> RequiredProperties { get; set; } = new();

    /// <summary>
    /// Gets or sets the root property definitions.
    /// </summary>
    public Dictionary<string, PropertyRule> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets a value indicating whether properties not listed in the schema are permitted.
    /// </summary>
    public bool AllowAdditionalProperties { get; set; } = true;

    internal void Normalize()
    {
        MaxDepth = MaxDepth <= 0 ? 127 : MaxDepth;
        MaxPropertyCount = MaxPropertyCount <= 0 ? 1000 : MaxPropertyCount;
        RequiredProperties ??= new List<string>();
        Properties = NormalizeProperties(Properties);
    }

    private static Dictionary<string, PropertyRule> NormalizeProperties(Dictionary<string, PropertyRule>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return new Dictionary<string, PropertyRule>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, PropertyRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties)
        {
            property.Value.Properties = NormalizeProperties(property.Value.Properties);
            normalized[property.Key] = property.Value;
        }

        return normalized;
    }
}

/// <summary>
/// Defines validation rules for a single property.
/// </summary>
public sealed class PropertyRule
{
    /// <summary>
    /// Gets or sets the expected JSON type.
    /// </summary>
    public string Type { get; set; } = "string";

    /// <summary>
    /// Gets or sets a value indicating whether the property is required.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether null is permitted.
    /// </summary>
    public bool AllowNull { get; set; } = true;

    /// <summary>
    /// Gets or sets a description for human operators.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets an optional maximum string length.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Gets or sets an optional minimum array item count.
    /// </summary>
    public int? MinItems { get; set; }

    /// <summary>
    /// Gets or sets an optional list of allowed values.
    /// </summary>
    public List<string>? AllowedValues { get; set; }

    /// <summary>
    /// Gets or sets an optional default value used by normalize mode.
    /// </summary>
    public JsonNode? DefaultValue { get; set; }

    /// <summary>
    /// Gets or sets nested object properties.
    /// </summary>
    public Dictionary<string, PropertyRule>? Properties { get; set; }

    /// <summary>
    /// Gets or sets the item rule for arrays.
    /// </summary>
    public PropertyRule? Items { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether additional nested properties are permitted.
    /// </summary>
    public bool AllowAdditionalProperties { get; set; } = true;
}
