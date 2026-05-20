# Solution 3: Post-Mirror Transformation via Fabric Spark Notebook

This solution accepts the mirrored table as-is, including any `NULL` values introduced by incompatible Cosmos DB types in the SQL analytics endpoint, and repairs those values downstream with a Microsoft Fabric Spark notebook.

Spark reads the mirrored Delta data directly from OneLake, applies schema-aware coercion and parsing logic, and writes a cleansed **gold** Delta table for analytics consumers.

## How it works

1. Cosmos DB data is mirrored into Fabric.
2. The SQL analytics endpoint may expose incompatible values as `NULL`.
3. Spark reads the underlying Delta files from OneLake with more flexible type handling.
4. A notebook repairs numeric, JSON, and array-shaped fields.
5. The repaired dataset is written to a Lakehouse gold table.
6. Power BI connects to the gold table instead of the raw mirrored table.

## Architecture

```mermaid
flowchart LR
    A[Cosmos DB] --> B[Fabric Mirror]
    B --> C[Raw Delta in OneLake]
    C --> D[Fabric Spark Notebook]
    D --> E[Gold Table in Lakehouse]
    E --> F[Power BI]
```

## When to use

Use this approach when:

- You need a **downstream fix** without changing the Cosmos DB source payloads.
- Silent `NULL` values in the SQL analytics endpoint are acceptable as an intermediate state.
- Batch-oriented repair is acceptable for reporting workloads.
- You want Spark-based parsing for schema drift, nested JSON, or mixed array/object payloads.

## Setup

1. **Create or identify a Lakehouse** for your gold tables.
2. **Create a Fabric notebook** and paste in the logic from `notebooks\type-repair-notebook.ipynb`.
3. Update notebook configuration:
   - mirrored Delta source path in OneLake
   - destination Lakehouse / gold table name
   - expected type mappings and JSON schemas
4. Add the helper module from `src\type_coercion_utils.py` to your workspace repo or notebook environment.
5. Run the notebook once to validate repairs and gold table output.
6. Wire the notebook into a **Fabric pipeline** for scheduled or orchestrated execution.

## Scheduling options

- **Pipeline trigger**: run after mirror refresh or in a scheduled Fabric pipeline.
- **Event-based**: trigger when upstream orchestration completes and new mirrored data is available.
- **Manual**: useful for initial validation, ad-hoc repair, or troubleshooting schema drift.

## Benefits

- No source-system changes required.
- Uses Spark's stronger support for schema evolution and coercion.
- Preserves a clean semantic layer for Power BI and SQL consumers.
- Enables reusable batch repair logic and monitoring.

## Limitations

- **Batch latency**: repaired data is only as current as the notebook schedule.
- **Compute cost**: Spark jobs add Fabric capacity consumption.
- **Operational complexity**: transformation logic must be maintained as schemas evolve.
- **Not a true source fix**: raw mirror issues still exist; this solution creates a trustworthy downstream projection instead.
