namespace Solution1.PreIngestionNormalization.Models;

public sealed class NormalizationRules
{
    public int MaxNestingDepth { get; set; } = 127;

    public int MaxPropertyCount { get; set; } = 1000;

    public Dictionary<string, string> TypeMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Decimal128"] = "double",
        ["Regex"] = "remove",
        ["DBPointer"] = "remove",
        ["JavaScript"] = "remove",
        ["Symbol"] = "remove",
        ["MinKey"] = "remove",
        ["MaxKey"] = "remove"
    };

    public ArrayHomogenizationStrategy ArrayHomogenizationStrategy { get; set; } = ArrayHomogenizationStrategy.ConvertToString;

    public List<string> PropertiesToExclude { get; set; } = new();

    public bool DefaultStringFallback { get; set; } = true;
}

public enum ArrayHomogenizationStrategy
{
    ConvertToString,
    UseFirstType,
    DropIncompatible
}
