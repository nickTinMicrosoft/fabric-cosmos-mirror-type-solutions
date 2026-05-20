using System.Text.Json;
using Solution1.PreIngestionNormalization.ChangeFeedProcessor;
using Solution1.PreIngestionNormalization.Models;
using Xunit;

namespace Solution1.PreIngestionNormalization.Tests;

public sealed class TypeNormalizerTests
{
    [Fact]
    public void Normalize_PassesThroughWellTypedDocument()
    {
        using var input = JsonDocument.Parse("""
        {
          "id": "1",
          "name": "Ada",
          "score": 42,
          "active": true,
          "tags": ["a", "b"]
        }
        """);

        var normalizer = CreateNormalizer();
        using var output = normalizer.Normalize(input);

        Assert.Equal("Ada", output.RootElement.GetProperty("name").GetString());
        Assert.Equal(42, output.RootElement.GetProperty("score").GetInt64());
        Assert.True(output.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(2, output.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public void Normalize_ConvertsDecimal128ToDouble()
    {
        using var input = JsonDocument.Parse("""
        {
          "id": "1",
          "amount": { "$numberDecimal": "123.45" }
        }
        """);

        var normalizer = CreateNormalizer();
        using var output = normalizer.Normalize(input);

        var amount = output.RootElement.GetProperty("amount");
        Assert.Equal(JsonValueKind.Number, amount.ValueKind);
        Assert.Equal(123.45d, amount.GetDouble(), 6);
    }

    [Fact]
    public void Normalize_HomogenizesMixedTypeArraysToStrings()
    {
        using var input = JsonDocument.Parse("""
        {
          "id": "1",
          "values": [1, "two", true]
        }
        """);

        var normalizer = CreateNormalizer();
        using var output = normalizer.Normalize(input);

        var values = output.RootElement.GetProperty("values").EnumerateArray().ToArray();
        Assert.All(values, item => Assert.Equal(JsonValueKind.String, item.ValueKind));
        Assert.Equal("1", values[0].GetString());
        Assert.Equal("two", values[1].GetString());
        Assert.Equal("true", values[2].GetString());
    }

    [Fact]
    public void Normalize_TruncatesDeepNestingToJsonString()
    {
        var rules = new NormalizationRules { MaxNestingDepth = 3 };
        var normalizer = new TypeNormalizer(rules);

        using var input = JsonDocument.Parse("""
        {
          "id": "1",
          "level1": {
            "level2": {
              "level3": {
                "level4": {
                  "value": "too-deep"
                }
              }
            }
          }
        }
        """);

        using var output = normalizer.Normalize(input);

        var truncated = output.RootElement
            .GetProperty("level1")
            .GetProperty("level2")
            .GetProperty("level3");

        Assert.Equal(JsonValueKind.String, truncated.ValueKind);
        Assert.Contains("level4", truncated.GetString());
    }

    [Fact]
    public void Normalize_EnforcesPropertyCountLimit()
    {
        var source = Enumerable.Range(1, 5)
            .ToDictionary(index => $"property{index}", index => (object?)index);
        source["id"] = "1";

        var rules = new NormalizationRules { MaxPropertyCount = 3 };
        var normalizer = new TypeNormalizer(rules);

        using var input = JsonDocument.Parse(JsonSerializer.Serialize(source));
        using var output = normalizer.Normalize(input);

        Assert.Equal(3, output.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Normalize_ResolvesTypeConflictsUsingStringFallback()
    {
        var normalizer = CreateNormalizer();

        using var first = JsonDocument.Parse("{\"id\":\"1\",\"status\":\"open\"}");
        using var second = JsonDocument.Parse("{\"id\":\"2\",\"status\":5}");

        using var _ = normalizer.Normalize(first);
        using var conflictResolved = normalizer.Normalize(second);

        var status = conflictResolved.RootElement.GetProperty("status");
        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal("5", status.GetString());
    }

    [Fact]
    public void Normalize_PreservesNulls()
    {
        using var input = JsonDocument.Parse("{\"id\":\"1\",\"optional\":null}");

        var normalizer = CreateNormalizer();
        using var output = normalizer.Normalize(input);

        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("optional").ValueKind);
    }

    [Fact]
    public void Normalize_HandlesEmptyDocument()
    {
        using var input = JsonDocument.Parse("{}");

        var normalizer = CreateNormalizer();
        using var output = normalizer.Normalize(input);

        Assert.Empty(output.RootElement.EnumerateObject());
    }

    private static TypeNormalizer CreateNormalizer() => new(new NormalizationRules());
}
