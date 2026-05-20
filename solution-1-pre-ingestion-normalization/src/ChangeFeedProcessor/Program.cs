using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solution1.PreIngestionNormalization.ChangeFeedProcessor;
using Solution1.PreIngestionNormalization.Models;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, builder) =>
    {
        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
               .AddEnvironmentVariables();

        var localSettingsPath = Path.Combine(context.HostingEnvironment.ContentRootPath, "local.settings.json");
        if (File.Exists(localSettingsPath))
        {
            using var localSettingsStream = File.OpenRead(localSettingsPath);
            using var localSettings = JsonDocument.Parse(localSettingsStream);

            if (localSettings.RootElement.TryGetProperty("Values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Object)
            {
                var flattenedValues = valuesElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                builder.AddInMemoryCollection(flattenedValues);
            }
        }

        var interimConfiguration = builder.Build();
        var rulesPath = interimConfiguration["NormalizationRulesPath"];

        if (!string.IsNullOrWhiteSpace(rulesPath))
        {
            var fullPath = Path.IsPathRooted(rulesPath)
                ? rulesPath
                : Path.Combine(context.HostingEnvironment.ContentRootPath, rulesPath);

            if (File.Exists(fullPath))
            {
                builder.AddJsonFile(fullPath, optional: true, reloadOnChange: false);
            }
        }
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddOptions<NormalizationRules>()
            .Bind(context.Configuration.GetSection("NormalizationRules"));

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<NormalizationRules>>().Value);
        services.AddSingleton<TypeNormalizer>();
        services.AddSingleton(_ => new CosmosClient(context.Configuration["CosmosDBConnection"]));
    })
    .Build();

host.Run();
