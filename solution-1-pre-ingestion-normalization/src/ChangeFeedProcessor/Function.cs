using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Solution1.PreIngestionNormalization.ChangeFeedProcessor;

public sealed class Function
{
    private readonly Container _destinationContainer;
    private readonly TypeNormalizer _typeNormalizer;
    private readonly ILogger<Function> _logger;

    public Function(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        TypeNormalizer typeNormalizer,
        ILogger<Function> logger)
    {
        _typeNormalizer = typeNormalizer;
        _logger = logger;

        var destinationDatabase = configuration["DestinationDatabase"]
            ?? throw new InvalidOperationException("DestinationDatabase configuration is missing.");
        var destinationContainer = configuration["DestinationContainer"]
            ?? throw new InvalidOperationException("DestinationContainer configuration is missing.");

        _destinationContainer = cosmosClient.GetContainer(destinationDatabase, destinationContainer);
    }

    [Function("ProcessMirrorNormalization")]
    public async Task RunAsync(
        [CosmosDBTrigger(
            databaseName: "%SourceDatabase%",
            containerName: "%SourceContainer%",
            Connection = "CosmosDBConnection",
            LeaseContainerName = "mirror-normalizer-leases",
            CreateLeaseContainerIfNotExists = true)] IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            _logger.LogDebug("Change feed invocation received no documents.");
            return;
        }

        _logger.LogInformation("Processing {DocumentCount} changed documents.", documents.Count);

        foreach (var rawDocument in documents)
        {
            try
            {
                using var sourceDocument = JsonDocument.Parse(rawDocument);
                using var normalizedDocument = _typeNormalizer.Normalize(sourceDocument);

                var normalizedJson = normalizedDocument.RootElement.GetRawText();
                using var payloadStream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedJson));

                var partitionKey = ResolvePartitionKey(normalizedDocument.RootElement);
                using var response = await _destinationContainer.UpsertItemStreamAsync(
                    payloadStream,
                    partitionKey,
                    cancellationToken: cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Upsert returned status code {StatusCode} for document id '{DocumentId}'.",
                        response.StatusCode,
                        TryGetId(normalizedDocument.RootElement));
                }
                else
                {
                    _logger.LogInformation(
                        "Normalized and upserted document id '{DocumentId}'.",
                        TryGetId(normalizedDocument.RootElement));
                }
            }
            catch (JsonException jsonException)
            {
                _logger.LogError(jsonException, "Invalid JSON encountered in change feed payload. Document skipped.");
            }
            catch (CosmosException cosmosException)
            {
                _logger.LogError(
                    cosmosException,
                    "Cosmos DB write failed for a normalized document with status code {StatusCode}.",
                    cosmosException.StatusCode);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected failure while normalizing a change feed document.");
                throw;
            }
        }
    }

    private static PartitionKey ResolvePartitionKey(JsonElement document)
    {
        var id = TryGetId(document);
        return new PartitionKey(id ?? throw new InvalidOperationException("Normalized document must contain an 'id' property."));
    }

    private static string? TryGetId(JsonElement document)
    {
        if (!document.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return idProperty.GetString();
    }
}
