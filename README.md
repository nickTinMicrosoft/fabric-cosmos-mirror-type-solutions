# Fabric Cosmos Mirror Type Solutions

> ⚠️ **DISCLAIMER:** This code is provided for **demonstration and educational purposes only**. It is not production-ready and should not be deployed to any environment without thorough review, testing, and validation by your engineering team. Before running any code from this repository in your environment, ensure it has been fully vetted for security, performance, error handling, and compliance with your organization's policies and standards. Use at your own risk.

[![Docs](https://img.shields.io/badge/docs-included-blue)](#documentation)
[![Samples](https://img.shields.io/badge/sample%20data-included-success)](#sample-data)
[![License](https://img.shields.io/badge/license-MIT-green)](#license)

Reference implementations and sample assets for handling unsupported or incompatible data types when mirroring Azure Cosmos DB for NoSQL into Microsoft Fabric.

## Problem statement

Microsoft Fabric mirroring for Azure Cosmos DB is convenient, but it is intentionally schema-light and not fully fidelity-preserving:

- **First non-null type wins** for a property and effectively locks the mirrored column shape.
- Later values that are **not compatible with the established type** are materialized as **silent `NULL` values**.
- **Unsupported BSON-style types** such as Decimal128 and Regex are not represented faithfully and must be normalized before analytics.
- **Nested objects and arrays** are commonly surfaced as **JSON text** in the SQL analytics endpoint.
- **Mixed-type arrays** and shape drift can lead to missing values, hard-to-query JSON blobs, or excluded records depending on how the data lands.

This repository shows five practical ways to reduce data loss, preserve analytics usability, and make Fabric mirroring predictable.

## Solutions at a glance

| Solution | Folder | Summary |
| --- | --- | --- |
| 1. Pre-ingestion normalization | [`solution-1-pre-ingestion-normalization`](./solution-1-pre-ingestion-normalization) | Clean and coerce source data before it reaches the mirrored container. |
| 2. Dual-property pattern | [`solution-2-dual-property-pattern`](./solution-2-dual-property-pattern) | Keep a strongly typed analytics field plus a raw/original field for fidelity. |
| 3. Post-mirror Spark normalization | [`solution-3-post-mirror-spark`](./solution-3-post-mirror-spark) | Use Spark over mirrored files to repair, cast, and curate downstream tables. |
| 4. SQL views + `OPENJSON` | [`solution-4-sql-views-openjson`](./solution-4-sql-views-openjson) | Flatten nested JSON text and present stable SQL views to analysts. |
| 5. Schema governance | [`solution-5-schema-governance`](./solution-5-schema-governance) | Prevent bad writes by enforcing contracts, drift checks, and release discipline. |

## Decision matrix

| Solution | Complexity | Data loss prevention | Real-time | Requires code change |
| --- | --- | --- | --- | --- |
| Pre-ingestion normalization | Medium | Very High | Yes | Yes |
| Dual-property pattern | Medium | High | Yes | Yes |
| Post-mirror Spark normalization | Medium-High | Medium | Near real-time / batch | No |
| SQL views + `OPENJSON` | Low-Medium | Low-Medium | Yes | No |
| Schema governance | Low-Medium | High (preventive) | Yes | Usually yes |

## Documentation

- [Problem explained](./docs/problem-explained.md)
- [Solution comparison](./docs/solution-comparison.md)
- [Supported types reference](./docs/supported-types-reference.md)

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3.10+](https://www.python.org/downloads/)
- An [Azure Cosmos DB Emulator](https://learn.microsoft.com/azure/cosmos-db/emulator) **or** a live Azure Cosmos DB for NoSQL account
- A Microsoft Fabric workspace/capacity with permission to create mirrored databases
- Optional: [Az PowerShell](https://learn.microsoft.com/powershell/azure/install-az-ps) for provisioning with the setup scripts

## How to use this repo

1. Clone the repository.
2. Open the solution folder you want to try.
3. Read that folder's README and run its samples.
4. Use the shared assets in [`common`](./common):
   - [`common/sample-data`](./common/sample-data)
   - [`common/cosmos-setup`](./common/cosmos-setup)
5. Compare trade-offs with [`docs/solution-comparison.md`](./docs/solution-comparison.md).

## Useful Microsoft documentation

- [Fabric mirroring limitations for Azure Cosmos DB](https://learn.microsoft.com/en-us/fabric/mirroring/azure-cosmos-db-limitations)
- [Fabric mirroring troubleshooting for Azure Cosmos DB](https://learn.microsoft.com/en-us/fabric/mirroring/azure-cosmos-db-troubleshooting)
- [Query nested mirrored data with `OPENJSON`](https://learn.microsoft.com/en-us/fabric/mirroring/azure-cosmos-db-how-to-query-nested)
- [Azure Cosmos DB analytical store schema constraints](https://learn.microsoft.com/en-us/azure/cosmos-db/analytical-store-introduction#schema-constraints)

## Sample data

The repository includes sample documents for:

- clean, well-typed orders
- intentional type drift and mixed arrays
- unsupported BSON-style values that require normalization

See [`common/sample-data`](./common/sample-data).

## License

MIT. See the project license file if one is added later; otherwise treat this repository content as intended for MIT-style use and adaptation.
