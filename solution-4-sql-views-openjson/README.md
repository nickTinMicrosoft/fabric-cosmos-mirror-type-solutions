# Solution 4: SQL Views with OPENJSON + TRY_CAST

This solution fixes Cosmos DB mirroring type issues at query time by adding a semantic layer of T-SQL views on top of the mirrored tables. Instead of changing ingestion pipelines or rewriting source documents, you leave the mirrored tables as-is and expose consumer-friendly views that:

- safely convert mixed data types with `TRY_CAST` and `TRY_CONVERT`
- reshape nested JSON stored in `varchar` columns with `OPENJSON`
- surface fallback values with `COALESCE`
- hide raw mirror quirks from Power BI, reports, and ad hoc SQL consumers

## Problem this solves

When Cosmos DB data is mirrored into Microsoft Fabric, incompatible values can be silently stored as `NULL` in the SQL analytics endpoint. Common examples include:

- numeric fields arriving as a mix of string, integer, and float values
- date fields arriving in inconsistent string formats
- nested objects and arrays materialized as JSON text instead of typed relational columns
- consumers querying raw mirrored tables and misinterpreting `NULL` values as missing business data

Views let you normalize these patterns at query time without modifying the mirrored table itself.

## When to use this approach

Use this solution when you need the **simplest downstream fix**:

- you cannot change the upstream Cosmos DB schema immediately
- you do not want to introduce a Spark or Data Factory pipeline yet
- analysts need a clean interface right away
- Power BI or SQL consumers can be redirected to views instead of raw tables
- the SQL analytics endpoint already exposes enough raw data to recover the fields you need

This is often the fastest path to a working analytics layer.

## Core T-SQL patterns

### 1. Safe numeric conversion with `TRY_CAST`

```sql
TRY_CAST(amount AS decimal(18,2))
```

If the value cannot be converted, SQL returns `NULL` instead of failing the query.

### 2. Date handling with `TRY_CONVERT`

```sql
TRY_CONVERT(datetime2(0), orderDate, 127)
```

This is useful when the source contains ISO 8601 strings, locale-dependent strings, or mixed formats.

### 3. Fallback logic with `COALESCE`

```sql
COALESCE(
    TRY_CAST(amount_text AS decimal(18,2)),
    TRY_CAST(amount_numeric AS decimal(18,2)),
    0.00
)
```

Use this when the same logical value may appear in more than one mirrored column or JSON location.

### 4. JSON scalar extraction with `JSON_VALUE`

```sql
JSON_VALUE(address_json, '$.city')
```

Use this for single values inside a nested JSON object stored as text.

### 5. JSON row expansion with `OPENJSON`

```sql
OUTER APPLY OPENJSON(items_json)
WITH (
    sku varchar(50) '$.sku',
    quantity int '$.quantity'
)
```

Use this to turn nested arrays into row sets that can be joined or queried like normal tables.

### 6. Protecting queries with `ISJSON`

```sql
CASE WHEN ISJSON(items_json) = 1 THEN items_json END
```

This guards `OPENJSON` and `JSON_VALUE` operations when malformed JSON is possible.

## Deployment to the Fabric SQL analytics endpoint

1. Open the mirrored database SQL analytics endpoint in Microsoft Fabric.
2. Open a new SQL query window against the database that contains the mirrored tables.
3. Review and update object names in `scripts\create-typed-views.sql` so they match your mirrored table names.
4. Run the view creation script.
5. Optionally run `scripts\nested-json-expansion.sql` and `scripts\null-safe-queries.sql` as reference queries during validation.
6. Point downstream tools to the new views such as `dbo.vw_Orders_Typed` and `dbo.vw_Customers_Typed`.

## Suggested consumption model

- raw mirrored tables remain the landing zone
- typed views become the analytics contract
- Power BI, notebooks, reports, and SQL users query the views
- additional views can be layered for domain-specific marts if needed

## Performance considerations

Views are computed at query time, so performance depends on the complexity of the expressions and the volume scanned.

Keep these considerations in mind:

- `TRY_CAST`, `TRY_CONVERT`, `JSON_VALUE`, and `OPENJSON` all add CPU overhead during query execution
- repeated parsing of the same JSON text is expensive; parse once with `OPENJSON ... WITH (...)` when possible
- prefer `varchar(n)` projections over `varchar(max)` when defining typed JSON outputs
- if the same heavy transformation is reused frequently, consider materializing the result in a downstream warehouse or Lakehouse table
- filter as early as possible in consumer queries to reduce the rows being transformed

## Limitations

This solution improves usability, but it does **not** recover information that was never stored in the SQL analytics endpoint.

Important limitations:

- if mirroring replaced a value with `NULL` and no alternate representation exists, the original value is gone from this layer
- views can reshape and reinterpret what is available, but they cannot reconstruct missing source data
- diagnostics still require knowledge of raw mirror behavior
- large-scale or repeatedly used transformations may eventually justify a persisted ETL pattern instead of a view-only approach

## Files in this solution

- `scripts\create-typed-views.sql` - reusable typed view definitions for Orders and Customers
- `scripts\nested-json-expansion.sql` - examples for expanding arrays and objects with `OPENJSON`
- `scripts\null-safe-queries.sql` - diagnostic and null-safe query patterns for mirrored data
- `examples\powerbi-connection-guide.md` - guidance for connecting Power BI to the typed views

## Bottom line

If you need an immediate, low-friction fix for mirrored Cosmos DB type issues, SQL views are the fastest way to provide a clean and stable analytics surface without changing upstream ingestion.