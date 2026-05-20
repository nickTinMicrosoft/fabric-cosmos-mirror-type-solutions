using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Solution5.SchemaGovernance.SchemaValidator;

/// <summary>
/// Indicates the importance of a validation violation.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// The document must be rejected.
    /// </summary>
    Error,

    /// <summary>
    /// The document can proceed, but a fix or review is recommended.
    /// </summary>
    Warning,

    /// <summary>
    /// Informational detail that can be logged for observability.
    /// </summary>
    Info
}

/// <summary>
/// Represents a single schema validation issue.
/// </summary>
/// <param name="PropertyPath">The JSON path where the issue was found.</param>
/// <param name="ExpectedType">The expected type from schema rules.</param>
/// <param name="ActualType">The actual type detected in the document.</param>
/// <param name="Message">A human-readable explanation of the violation.</param>
/// <param name="Severity">The severity assigned to the violation.</param>
public sealed record ValidationViolation(
    string PropertyPath,
    string ExpectedType,
    string ActualType,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error);

/// <summary>
/// The result produced by schema validation.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>
    /// Gets a value indicating whether the document passed validation.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Gets the violations found during validation.
    /// </summary>
    public IReadOnlyList<ValidationViolation> Violations { get; init; } = Array.Empty<ValidationViolation>();

    /// <summary>
    /// Gets the normalized document when normalize mode is enabled and a transformed payload is produced.
    /// </summary>
    public JsonNode? NormalizedDocument { get; init; }
}
