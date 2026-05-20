# Solution 1: Pre-Ingestion Data Normalization

> ⚠️ **DISCLAIMER:** This code is provided for **demonstration and educational purposes only**. It is not production-ready and should not be deployed to any environment without thorough review, testing, and validation by your engineering team. Before running any code from this repository in your environment, ensure it has been fully vetted for security, performance, error handling, and compliance with your organization's policies and standards. Use at your own risk.

## Overview

This solution places an Azure Function between the source Cosmos DB container and the mirror-ready destination container.

The function listens to the **Cosmos DB Change Feed**, normalizes documents before they are written to the destination container, and ensures Microsoft Fabric mirroring sees stable, supported types.

This prevents the common Fabric mirroring failure mode where the **first non-null type wins**, the column type becomes fixed, and later incompatible values are silently surfaced as `NULL` in OneLake.

## How it works

1. Application writes documents to the **source container**.
2. Azure Function reads inserts/updates from the **Change Feed**.
3. `TypeNormalizer` recursively cleans and standardizes each document.
4. The normalized document is written to the **mirror-ready destination container**.
5. Microsoft Fabric mirrors the destination container instead of the raw source container.

## Architecture

```text
+----------------------+        Change Feed        +-------------------------+
| Source Cosmos DB     |  ---------------------->  | Azure Function          |
| Container            |                           | ChangeFeedProcessor     |
| (raw / variable data)|                           | + TypeNormalizer        |
+----------------------+                           +-----------+-------------+
                                                                |
                                                                | upsert normalized JSON
                                                                v
                                                     +-------------------------+
                                                     | Mirror-ready Cosmos DB  |
                                                     | Destination Container   |
                                                     | (stable supported types)|
                                                     +-----------+-------------+
                                                                 |
                                                                 | mirror
                                                                 v
                                                     +-------------------------+
                                                     | Microsoft Fabric        |
                                                     | OneLake / Delta tables  |
                                                     +-------------------------+
```

## What gets normalized

- **Decimal128 / extended numeric wrappers** are converted to `double`
- **Mixed-type arrays** are homogenized so Fabric sees one stable element type
- **Unsupported BSON-style artifacts** such as regex, symbol, DB pointer, MinKey, and MaxKey are removed
- **Cross-document type conflicts** are string-fallbacked during processing lifetime to reduce downstream schema drift
- **Deep nesting** beyond the configured limit is flattened to a JSON string
- **Total property count** is capped to avoid Fabric truncation and exclusion scenarios

## When to use this solution

Use this pattern when:

- You control the ingestion pipeline
- You need near real-time processing
- You want to avoid silent `NULL` values in Fabric
- You can mirror a curated destination container instead of the original source container

## Project structure

```text
solution-1-pre-ingestion-normalization/
├── README.md
├── src/
│   ├── ChangeFeedProcessor/
│   │   ├── ChangeFeedProcessor.csproj
│   │   ├── Program.cs
│   │   ├── Function.cs
│   │   ├── TypeNormalizer.cs
│   │   ├── host.json
│   │   └── local.settings.template.json
│   └── Models/
│       └── NormalizationRules.cs
└── tests/
    ├── ChangeFeedProcessor.Tests.csproj
    └── TypeNormalizerTests.cs
```

## Setup

### Prerequisites

- .NET 8 SDK
- Azure Functions Core Tools v4
- Azure subscription
- Cosmos DB account with source and destination containers
- Microsoft Fabric mirroring configured against the **destination** container

### Recommended container setup

- Source container: receives raw application writes
- Destination container: receives normalized documents from the function
- Lease container: used by the Change Feed trigger
- For the sample code, use `/id` as the destination partition key or adjust the write logic for your partitioning model

### Local development

1. Copy `src\ChangeFeedProcessor\local.settings.template.json` to `local.settings.json`
2. Fill in your Cosmos DB connection and container names
3. Optionally create a rules file and point `NormalizationRulesPath` to it
4. Restore packages and run tests:
   - `dotnet test tests\ChangeFeedProcessor.Tests.csproj`
5. Run the function locally:
   - `func start --csharp`

## Configuration

`NormalizationRules` supports these settings:

- `MaxNestingDepth` (default `127`)
- `MaxPropertyCount` (default `1000`)
- `TypeMappings` for known source type conversions
- `ArrayHomogenizationStrategy`
  - `ConvertToString`
  - `UseFirstType`
  - `DropIncompatible`
- `PropertiesToExclude`
- `DefaultStringFallback`

### Example rules file

```json
{
  "NormalizationRules": {
    "MaxNestingDepth": 127,
    "MaxPropertyCount": 1000,
    "DefaultStringFallback": true,
    "ArrayHomogenizationStrategy": "ConvertToString",
    "PropertiesToExclude": ["_etag"],
    "TypeMappings": {
      "Decimal128": "double",
      "Regex": "remove",
      "DBPointer": "remove"
    }
  }
}
```

## Limitations

- The sample keeps cross-document type observations in memory, so type consistency is **best effort per function host instance**
- Existing destination documents are not backfilled automatically when a property later gets promoted to string
- If your destination container uses a partition key other than `/id`, update the write path accordingly
- Deep objects beyond the configured limit are preserved as JSON strings rather than full nested structures
- Truncating properties above the configured limit protects mirrorability, but dropped fields are not persisted unless you add custom dead-letter handling

## Why this works for Fabric mirroring

Fabric mirroring is optimized for stable, compatible schemas. By normalizing before mirroring:

- the first observed non-null value is already the intended type,
- incompatible source types are converted before they become `NULL`, and
- documents that would otherwise be excluded because of mixed arrays or unsupported artifacts are made mirror-safe first.
