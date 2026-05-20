# Solution 2: Dual-Property Pattern (Type-Safe Redundancy)

## Overview

The dual-property pattern protects Microsoft Fabric Cosmos DB mirroring from silently nulling values when the same logical property appears with different JSON types across documents.

Instead of relying on a single field such as `amount`, you keep the original property **and** add typed companion properties such as:

- `amount_int`
- `amount_float`
- `amount_string`
- `amount_bool`
- `amount_array`
- `amount_json`

Because Fabric mirror treats each property name as a separate column, each typed suffix becomes its own stable, type-safe column in Delta Lake.

## Why this pattern is needed

Fabric mirroring locks a mirrored column to the **first non-null type it sees**.

Example problem:

- Document A writes `amount: 125`
- Fabric mirror creates `amount` as a numeric column
- Document B later writes `amount: "125.99"`
- The mirrored `amount` value for Document B becomes `NULL`

With the dual-property pattern:

- Document A can emit `amount_int` and `amount_float`
- Document B can emit `amount_string`
- Fabric mirrors each property into a separate compatible column
- You recover the logical value at query time with `COALESCE` and `TRY_CAST`

## Before and after

### Before

```json
{
  "id": "order-1001",
  "customerId": "cust-01",
  "amount": 125,
  "status": "paid"
}
```

```json
{
  "id": "order-1002",
  "customerId": "cust-02",
  "amount": "125.99",
  "status": "paid"
}
```

### After

```json
{
  "id": "order-1001",
  "customerId": "cust-01",
  "amount": 125,
  "amount_int": 125,
  "amount_float": 125,
  "status": "paid",
  "status_string": "paid"
}
```

```json
{
  "id": "order-1002",
  "customerId": "cust-02",
  "amount": "125.99",
  "amount_string": "125.99",
  "status": "paid",
  "status_string": "paid"
}
```

## When to use

Use this solution when:

- the set of problematic properties is already known
- you want a low-complexity fix
- you want to keep the original document shape mostly intact
- you can tolerate some document growth for safer downstream analytics
- you want minimal application changes instead of a full normalization pipeline

This is often the simplest production-safe option when only a handful of fields drift between number, string, boolean, array, or object.

## Naming convention

Use a predictable suffix pattern:

- `property_int`
- `property_float`
- `property_string`
- `property_bool`
- `property_array`
- `property_json`

Example:

- `amount` → `amount_int`, `amount_float`, `amount_string`
- `isActive` → `isActive_bool`
- `tags` → `tags_array`
- `profile` → `profile_json`

This naming convention keeps the original semantic meaning while making the mirrored schema self-describing.

## How Fabric mirror sees it

Fabric mirror does **not** understand these fields as aliases of one logical property. It sees them as separate properties, so it produces separate columns such as:

- `amount`
- `amount_int`
- `amount_float`
- `amount_string`

That is exactly what we want. Each column remains type-stable, so values are preserved instead of being silently nullified.

## Querying tips

At query time, rebuild the logical field by coalescing across the typed columns.

```sql
SELECT
    id,
    COALESCE(amount_float, TRY_CAST(amount_string AS FLOAT), CAST(amount_int AS FLOAT)) AS amount
FROM dbo.orders_mirrored;
```

Other tips:

- prefer `TRY_CAST` for string variants
- keep query logic in a SQL view so analysts do not repeat the same expression
- add a derived `*_source_type` expression when you need observability into the original JSON shape

## Operational guidance

- keep the original property for application compatibility
- only dual-ify fields that are known to drift
- use a background migration or re-upsert job to backfill existing documents
- write new documents with the typed variants from day one to prevent future nulls

## Limitations

This pattern is pragmatic, but it has trade-offs:

- **document size growth**: each protected property adds more fields
- **maintenance burden**: applications and ETL jobs must consistently write the typed variants
- **query complexity**: consumers should use `COALESCE` and `TRY_CAST`
- **not full semantic normalization**: a string that contains a number is still stored as a string unless query logic converts it
- **schema sprawl**: protecting many drifting properties can create a wide mirrored table

## Included assets

- `src\DualPropertyTransformer.cs` - transforms JSON documents into dual-property form
- `src\BulkUpsertWithDualProperties.cs` - bulk Cosmos DB rewriter/upserter using the Cosmos SDK v3
- `src\sample-documents-before-after.json` - example transformations
- `src\QueryExamples.sql` - T-SQL patterns for querying mirrored data
- `scripts\apply-dual-properties.ps1` - PowerShell entry point for building and executing the migration tool
