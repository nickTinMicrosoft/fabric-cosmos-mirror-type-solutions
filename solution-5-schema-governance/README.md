# Solution 5: Schema Governance + Drift Monitoring

This solution prevents Microsoft Fabric Cosmos DB mirroring type issues before they happen and detects schema drift if upstream behavior changes.

## Two-part approach

1. **Prevention:** validate documents before they are written to Cosmos DB.
2. **Detection:** monitor mirrored tables for unexpected `NULL` spikes that often indicate type drift.

## Architecture

```text
App -> Schema Validator -> Cosmos DB -> Mirror -> Drift Monitor -> Alerts
```

## When to use

Use this pattern when:

- you control the application or API write path
- you want to prevent bad documents from landing in Cosmos DB
- you need early warning when data starts drifting from the expected schema
- you can invest in ongoing schema governance

## Solution contents

- `src/SchemaValidator/CosmosSchemaValidator.cs` - reusable .NET 8 validator library
- `src/SchemaValidator/SchemaValidatorMiddleware.cs` - ASP.NET Core middleware for write APIs
- `src/SchemaValidator/schema-rules.json` - example schema rules for an `Orders` collection
- `src/DriftMonitor/monitor-pipeline.json` - Fabric pipeline template for drift detection
- `src/DriftMonitor/drift-detection.kql` - KQL queries for null-rate monitoring
- `alerts/logic-app-template.json` - Logic App ARM template for notifications

## Prevention setup

### 1. Define schema rules

Update `src/SchemaValidator/schema-rules.json` with:

- required properties
- expected property types
- nested object rules
- array element rules
- string length limits
- optional default values for normalization mode

Example:

```json
{
  "collectionName": "Orders",
  "requiredProperties": ["id", "amount", "customer"],
  "properties": {
    "amount": {
      "type": "number",
      "required": true,
      "allowNull": false
    }
  }
}
```

### 2. Register the validator in ASP.NET Core

```csharp
using Solution5.SchemaGovernance.SchemaValidator;

builder.Services.AddCosmosSchemaValidation(options =>
{
    options.RulesFilePath = Path.Combine(builder.Environment.ContentRootPath, "src", "SchemaValidator", "schema-rules.json");
    options.Mode = ValidationMode.Strict;
    options.WriteEndpointPrefixes.Add("/api/orders");
});

var app = builder.Build();
app.UseCosmosSchemaValidation();
```

### 3. Choose an enforcement mode

- **Strict mode**: reject invalid writes with HTTP 400 and a list of violations.
- **Normalize mode**: attempt safe fixes such as numeric or boolean coercion, string truncation, and default-value insertion.

## Drift monitoring setup

### 1. Deploy monitoring artifacts

- publish `src/DriftMonitor/monitor-pipeline.json` as a Fabric Data Pipeline
- store or adapt the queries from `src/DriftMonitor/drift-detection.kql`
- deploy `alerts/logic-app-template.json` to Azure

### 2. Configure the hourly schedule

Set the pipeline trigger to run hourly, or more frequently for high-volume collections.

### 3. Baseline expected null rates

Create and maintain a baseline table with expected null percentages per mirrored column. The pipeline compares current values with that baseline and flags drift when the delta exceeds a threshold.

### 4. Configure alerts

Pass the Logic App webhook URL into the pipeline parameter `alertWebhookUrl` and configure:

- email recipients
- Teams webhook URL
- severity threshold
- null-rate delta threshold

## How schema rules work

The validator supports rules for:

- `string`, `number`, `boolean`, `object`, `array`
- required vs optional properties
- `allowNull`
- `maxLength`
- `allowedValues`
- `minItems`
- nested `properties`
- array `items`
- default values used in normalize mode

## Operational guidance

- version-control schema rules with the application
- review violations as part of release validation
- update the baseline when intentional schema changes are released
- keep write endpoints narrow so validation only runs where needed

## Limitations

- requires changes in the Cosmos DB write path
- normalization can only fix predictable issues, not arbitrary malformed documents
- schema rules and baselines require ongoing maintenance
- drift monitoring detects symptoms in mirrored data; it does not replace source-side validation
