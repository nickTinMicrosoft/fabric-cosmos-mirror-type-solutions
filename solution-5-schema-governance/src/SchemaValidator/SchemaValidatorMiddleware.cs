using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solution5.SchemaGovernance.SchemaValidator;

/// <summary>
/// Middleware that validates incoming write payloads before they are persisted to Cosmos DB.
/// </summary>
public sealed class SchemaValidatorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CosmosSchemaValidator _validator;
    private readonly SchemaValidatorOptions _options;
    private readonly ILogger<SchemaValidatorMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaValidatorMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware component.</param>
    /// <param name="validator">The schema validator.</param>
    /// <param name="options">Runtime options.</param>
    /// <param name="logger">The middleware logger.</param>
    public SchemaValidatorMiddleware(
        RequestDelegate next,
        CosmosSchemaValidator validator,
        IOptions<SchemaValidatorOptions> options,
        ILogger<SchemaValidatorMiddleware> logger)
    {
        _next = next;
        _validator = validator;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Executes schema validation for matching requests.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the request has been processed.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldValidate(context.Request))
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering();

        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        context.Request.Body.Position = 0;

        var result = _validator.Validate(body, _options.Mode);
        LogViolations(context, result);

        if (!result.IsValid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(result).ConfigureAwait(false);
            return;
        }

        if (_options.Mode == ValidationMode.Normalize && result.NormalizedDocument is not null)
        {
            var normalizedJson = result.NormalizedDocument.ToJsonString();
            var normalizedBytes = Encoding.UTF8.GetBytes(normalizedJson);
            var replacementStream = new MemoryStream(normalizedBytes);
            replacementStream.Position = 0;

            context.Request.Body = replacementStream;
            context.Request.ContentLength = normalizedBytes.Length;
            context.Request.Headers.ContentLength = normalizedBytes.Length;
        }

        await _next(context);
    }

    private bool ShouldValidate(HttpRequest request)
    {
        if (_options.ValidateMethods.Count > 0 && !_options.ValidateMethods.Contains(request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_options.RequireJsonContentType && (request.ContentType is null || !request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_options.WriteEndpointPrefixes.Count == 0)
        {
            return true;
        }

        return _options.WriteEndpointPrefixes.Any(prefix =>
            !string.IsNullOrEmpty(prefix.Value)
            && request.Path.Value?.StartsWith(prefix.Value, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void LogViolations(HttpContext context, ValidationResult result)
    {
        foreach (var violation in result.Violations)
        {
            switch (violation.Severity)
            {
                case ValidationSeverity.Error:
                    _logger.LogError(
                        "Cosmos schema validation failed for {Method} {Path}. PropertyPath={PropertyPath}, Expected={ExpectedType}, Actual={ActualType}, Message={Message}",
                        context.Request.Method,
                        context.Request.Path,
                        violation.PropertyPath,
                        violation.ExpectedType,
                        violation.ActualType,
                        violation.Message);
                    break;

                case ValidationSeverity.Warning:
                    _logger.LogWarning(
                        "Cosmos schema normalization warning for {Method} {Path}. PropertyPath={PropertyPath}, Expected={ExpectedType}, Actual={ActualType}, Message={Message}",
                        context.Request.Method,
                        context.Request.Path,
                        violation.PropertyPath,
                        violation.ExpectedType,
                        violation.ActualType,
                        violation.Message);
                    break;

                default:
                    _logger.LogInformation(
                        "Cosmos schema validation info for {Method} {Path}. PropertyPath={PropertyPath}, Expected={ExpectedType}, Actual={ActualType}, Message={Message}",
                        context.Request.Method,
                        context.Request.Path,
                        violation.PropertyPath,
                        violation.ExpectedType,
                        violation.ActualType,
                        violation.Message);
                    break;
            }
        }
    }
}

/// <summary>
/// Options used to configure schema validation middleware.
/// </summary>
public sealed class SchemaValidatorOptions
{
    /// <summary>
    /// Gets or sets the path to the JSON rules file.
    /// </summary>
    public string RulesFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the validation mode.
    /// </summary>
    public ValidationMode Mode { get; set; } = ValidationMode.Strict;

    /// <summary>
    /// Gets the path prefixes that should be treated as Cosmos DB write endpoints.
    /// </summary>
    public IList<PathString> WriteEndpointPrefixes { get; } = new List<PathString>();

    /// <summary>
    /// Gets the HTTP methods that should be validated.
    /// </summary>
    public IList<string> ValidateMethods { get; } = new List<string> { HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch };

    /// <summary>
    /// Gets or sets a value indicating whether validation should only run for JSON requests.
    /// </summary>
    public bool RequireJsonContentType { get; set; } = true;
}

/// <summary>
/// Dependency injection helpers for schema validation.
/// </summary>
public static class SchemaValidatorRegistrationExtensions
{
    /// <summary>
    /// Registers the schema validator and middleware options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The options delegate.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddCosmosSchemaValidation(this IServiceCollection services, Action<SchemaValidatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SchemaValidatorOptions>()
            .Configure(configure)
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.RulesFilePath))
                {
                    throw new InvalidOperationException("SchemaValidatorOptions.RulesFilePath must be configured.");
                }
            });

        services.AddSingleton<CosmosSchemaValidator>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SchemaValidatorOptions>>().Value;
            var resolvedPath = Path.IsPathRooted(options.RulesFilePath)
                ? options.RulesFilePath
                : Path.Combine(AppContext.BaseDirectory, options.RulesFilePath);

            return CosmosSchemaValidator.CreateFromFile(resolvedPath);
        });

        return services;
    }

    /// <summary>
    /// Adds schema validation middleware to the ASP.NET Core pipeline.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The updated application builder.</returns>
    public static IApplicationBuilder UseCosmosSchemaValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SchemaValidatorMiddleware>();
    }
}
