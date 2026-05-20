using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;

namespace FabricCosmosMirrorTypeSolutions.Solution2DualPropertyPattern;

public static class BulkUpsertWithDualProperties
{
    private static readonly JsonSerializerOptions SummaryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            AppOptions options = AppOptions.Parse(args);

            if (options.ShowHelp)
            {
                AppOptions.WriteHelp();
                return 0;
            }

            MigrationSummary summary = await ExecuteAsync(options);
            Console.WriteLine($"SUMMARY_JSON:{JsonSerializer.Serialize(summary, SummaryJsonOptions)}");
            return summary.FailedDocuments == 0 ? 0 : 1;
        }
        catch (ArgumentException argumentException)
        {
            Console.Error.WriteLine(argumentException.Message);
            AppOptions.WriteHelp();
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fatal error: {exception}");
            return 1;
        }
    }

    private static async Task<MigrationSummary> ExecuteAsync(AppOptions options)
    {
        CosmosClientOptions clientOptions = new()
        {
            AllowBulkExecution = true,
            ConnectionMode = ConnectionMode.Gateway
        };

        using CosmosClient client = new(options.ConnectionString, clientOptions);
        Container sourceContainer = client.GetContainer(options.Database, options.SourceContainer);
        Container targetContainer = client.GetContainer(options.Database, options.TargetContainer);

        string partitionKeyPath = await GetPartitionKeyPathAsync(targetContainer, options.CancellationToken);
        MigrationSummary summary = new()
        {
            Database = options.Database,
            SourceContainer = options.SourceContainer,
            TargetContainer = options.TargetContainer,
            PropertiesToTransform = options.PropertiesToTransform.ToArray(),
            PartitionKeyPath = partitionKeyPath
        };

        QueryRequestOptions requestOptions = new()
        {
            MaxItemCount = options.BatchSize
        };

        FeedIterator iterator = sourceContainer.GetItemQueryStreamIterator(
            queryText: "SELECT * FROM c",
            requestOptions: requestOptions);

        while (iterator.HasMoreResults && !options.CancellationToken.IsCancellationRequested)
        {
            using ResponseMessage response = await iterator.ReadNextAsync(options.CancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new CosmosException(
                    message: $"Query failed while reading from source container '{options.SourceContainer}'.",
                    statusCode: response.StatusCode,
                    subStatusCode: 0,
                    activityId: response.Headers.ActivityId,
                    requestCharge: response.Headers.RequestCharge);
            }

            await ProcessFeedPageAsync(response.Content, targetContainer, partitionKeyPath, options, summary);

            if (options.MaxDocuments.HasValue && summary.DocumentsRead >= options.MaxDocuments.Value)
            {
                break;
            }
        }

        Console.WriteLine($"Completed. Read={summary.DocumentsRead}, Upserted={summary.UpsertedDocuments}, Failed={summary.FailedDocuments}, AddedProperties={summary.PropertiesAdded}");
        return summary;
    }

    private static async Task ProcessFeedPageAsync(
        Stream content,
        Container targetContainer,
        string partitionKeyPath,
        AppOptions options,
        MigrationSummary summary)
    {
        using JsonDocument feedDocument = await JsonDocument.ParseAsync(content, cancellationToken: options.CancellationToken);
        JsonElement documents = feedDocument.RootElement.GetProperty("Documents");
        List<Task<DocumentProcessingOutcome>> pendingTasks = new();

        foreach (JsonElement sourceDocument in documents.EnumerateArray())
        {
            if (options.MaxDocuments.HasValue && summary.DocumentsRead >= options.MaxDocuments.Value)
            {
                break;
            }

            summary.DocumentsRead++;
            pendingTasks.Add(ProcessDocumentAsync(sourceDocument, targetContainer, partitionKeyPath, options));
        }

        DocumentProcessingOutcome[] outcomes = await Task.WhenAll(pendingTasks);

        foreach (DocumentProcessingOutcome outcome in outcomes)
        {
            summary.PropertiesAdded += outcome.PropertiesAdded;
            summary.DocumentsTransformed += outcome.WasTransformed ? 1 : 0;
            summary.UpsertedDocuments += outcome.WasUpserted ? 1 : 0;
            summary.FailedDocuments += outcome.WasSuccessful ? 0 : 1;

            if (!outcome.WasSuccessful && !string.IsNullOrWhiteSpace(outcome.ErrorMessage))
            {
                summary.Errors.Add(outcome.ErrorMessage);
                Console.Error.WriteLine(outcome.ErrorMessage);
            }
        }
    }

    private static async Task<DocumentProcessingOutcome> ProcessDocumentAsync(
        JsonElement sourceDocument,
        Container targetContainer,
        string partitionKeyPath,
        AppOptions options)
    {
        try
        {
            JsonObject transformed = DualPropertyTransformer.Transform(sourceDocument, options.PropertiesToTransform, out DualPropertyTransformer.DocumentTransformationResult result);

            // Query results include system-managed Cosmos fields that should not be written back during upsert.
            RemoveSystemManagedProperties(transformed);

            // The target container can be different from the source, so resolve the partition key dynamically.
            PartitionKey partitionKey = ResolvePartitionKey(transformed, partitionKeyPath);
            string transformedJson = transformed.ToJsonString();
            using MemoryStream payloadStream = new(Encoding.UTF8.GetBytes(transformedJson));
            using ResponseMessage upsertResponse = await targetContainer.UpsertItemStreamAsync(payloadStream, partitionKey, cancellationToken: options.CancellationToken);

            if (!upsertResponse.IsSuccessStatusCode)
            {
                return DocumentProcessingOutcome.Failed(
                    GetDocumentId(sourceDocument),
                    $"Failed to upsert document '{GetDocumentId(sourceDocument)}'. StatusCode={(int)upsertResponse.StatusCode} ({upsertResponse.StatusCode}).");
            }

            return DocumentProcessingOutcome.Success(GetDocumentId(sourceDocument), result.PropertiesAdded, result.PropertiesAdded > 0);
        }
        catch (Exception exception)
        {
            return DocumentProcessingOutcome.Failed(GetDocumentId(sourceDocument), $"Document '{GetDocumentId(sourceDocument)}' failed: {exception.Message}");
        }
    }

    private static void RemoveSystemManagedProperties(JsonObject document)
    {
        foreach (string propertyName in new[] { "_rid", "_self", "_etag", "_attachments", "_ts" })
        {
            document.Remove(propertyName);
        }
    }

    private static string GetDocumentId(JsonElement sourceDocument)
    {
        return sourceDocument.TryGetProperty("id", out JsonElement idElement)
            ? idElement.GetString() ?? "<null-id>"
            : "<missing-id>";
    }

    private static async Task<string> GetPartitionKeyPathAsync(Container container, CancellationToken cancellationToken)
    {
        ContainerResponse response = await container.ReadContainerAsync(cancellationToken: cancellationToken);
        string? partitionKeyPath = response.Resource.PartitionKeyPath;

        if (string.IsNullOrWhiteSpace(partitionKeyPath))
        {
            throw new InvalidOperationException("Unable to determine the target container partition key path.");
        }

        return partitionKeyPath;
    }

    private static PartitionKey ResolvePartitionKey(JsonObject document, string partitionKeyPath)
    {
        string[] segments = partitionKeyPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonNode? current = document;

        foreach (string segment in segments)
        {
            current = current?[segment];
        }

        if (current is null)
        {
            return PartitionKey.Null;
        }

        return current switch
        {
            JsonValue value when value.TryGetValue<string>(out string? stringValue) => new PartitionKey(stringValue),
            JsonValue value when value.TryGetValue<long>(out long longValue) => new PartitionKey(longValue),
            JsonValue value when value.TryGetValue<double>(out double doubleValue) => new PartitionKey(doubleValue),
            JsonValue value when value.TryGetValue<bool>(out bool boolValue) => new PartitionKey(boolValue),
            _ => throw new InvalidOperationException($"Partition key path '{partitionKeyPath}' resolved to an unsupported JSON value.")
        };
    }

    private sealed class AppOptions
    {
        public string ConnectionString { get; private set; } = string.Empty;

        public string Database { get; private set; } = string.Empty;

        public string SourceContainer { get; private set; } = string.Empty;

        public string TargetContainer { get; private set; } = string.Empty;

        public IReadOnlyList<string> PropertiesToTransform { get; private set; } = Array.Empty<string>();

        public int BatchSize { get; private set; } = 100;

        public int? MaxDocuments { get; private set; }

        public bool ShowHelp { get; private set; }

        public CancellationToken CancellationToken => CancellationToken.None;

        public static AppOptions Parse(string[] args)
        {
            Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < args.Length; index++)
            {
                string current = args[index];

                if (current is "--help" or "-h" or "/?")
                {
                    flags.Add("help");
                    continue;
                }

                if (!current.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{current}'. Arguments must use the --name value format.");
                }

                if (index == args.Length - 1)
                {
                    throw new ArgumentException($"Missing value for argument '{current}'.");
                }

                parsed[current[2..]] = args[++index];
            }

            AppOptions options = new()
            {
                ShowHelp = flags.Contains("help")
            };

            if (options.ShowHelp)
            {
                return options;
            }

            options.ConnectionString = GetRequired(parsed, "connection-string");
            options.Database = GetRequired(parsed, "database");
            options.SourceContainer = GetRequired(parsed, "source-container");
            options.TargetContainer = parsed.TryGetValue("target-container", out string? targetContainer)
                ? targetContainer
                : options.SourceContainer;
            options.PropertiesToTransform = GetRequired(parsed, "properties")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parsed.TryGetValue("batch-size", out string? batchSizeValue) && int.TryParse(batchSizeValue, out int batchSize) && batchSize > 0)
            {
                options.BatchSize = batchSize;
            }

            if (parsed.TryGetValue("max-documents", out string? maxDocumentsValue) && int.TryParse(maxDocumentsValue, out int maxDocuments) && maxDocuments > 0)
            {
                options.MaxDocuments = maxDocuments;
            }

            if (options.PropertiesToTransform.Count == 0)
            {
                throw new ArgumentException("At least one property name must be provided via --properties.");
            }

            return options;
        }

        public static void WriteHelp()
        {
            Console.WriteLine("BulkUpsertWithDualProperties - Apply the dual-property pattern to Cosmos DB documents.");
            Console.WriteLine();
            Console.WriteLine("Required arguments:");
            Console.WriteLine("  --connection-string <value>   Cosmos DB connection string");
            Console.WriteLine("  --database <value>            Database name");
            Console.WriteLine("  --source-container <value>    Source container name");
            Console.WriteLine("  --properties <csv>            Comma-separated property names to dual-ify");
            Console.WriteLine();
            Console.WriteLine("Optional arguments:");
            Console.WriteLine("  --target-container <value>    Target container name (defaults to source container)");
            Console.WriteLine("  --batch-size <value>          Query page size for bulk processing (default: 100)");
            Console.WriteLine("  --max-documents <value>       Stop after processing N documents");
            Console.WriteLine("  --help                        Show this message");
        }

        private static string GetRequired(IReadOnlyDictionary<string, string> parsed, string name)
        {
            if (!parsed.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Missing required argument '--{name}'.");
            }

            return value;
        }
    }

    public sealed class MigrationSummary
    {
        public string Database { get; set; } = string.Empty;

        public string SourceContainer { get; set; } = string.Empty;

        public string TargetContainer { get; set; } = string.Empty;

        public string PartitionKeyPath { get; set; } = string.Empty;

        public string[] PropertiesToTransform { get; set; } = Array.Empty<string>();

        public int DocumentsRead { get; set; }

        public int DocumentsTransformed { get; set; }

        public int UpsertedDocuments { get; set; }

        public int FailedDocuments { get; set; }

        public int PropertiesAdded { get; set; }

        public List<string> Errors { get; } = new();
    }

    private sealed class DocumentProcessingOutcome
    {
        public string DocumentId { get; init; } = string.Empty;

        public bool WasSuccessful { get; init; }

        public bool WasUpserted { get; init; }

        public bool WasTransformed { get; init; }

        public int PropertiesAdded { get; init; }

        public string? ErrorMessage { get; init; }

        public static DocumentProcessingOutcome Success(string documentId, int propertiesAdded, bool wasTransformed)
        {
            return new DocumentProcessingOutcome
            {
                DocumentId = documentId,
                WasSuccessful = true,
                WasUpserted = true,
                WasTransformed = wasTransformed,
                PropertiesAdded = propertiesAdded
            };
        }

        public static DocumentProcessingOutcome Failed(string documentId, string errorMessage)
        {
            return new DocumentProcessingOutcome
            {
                DocumentId = documentId,
                WasSuccessful = false,
                WasUpserted = false,
                WasTransformed = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
