# Power BI Connection Guide for SQL Analytics Endpoint Views

## Why connect Power BI to the views instead of the raw mirrored tables?

Connect Power BI to the typed SQL views, not directly to the raw mirror tables.

The views:

- hide Fabric mirroring type issues behind `TRY_CAST`, `TRY_CONVERT`, and `COALESCE`
- expose cleaner column names for report authors
- flatten or normalize JSON-backed fields into report-friendly columns
- reduce the chance that model authors misread type-mismatch `NULL` values as missing business data

Recommended objects:

- `dbo.vw_Orders_Typed`
- `dbo.vw_Customers_Typed`

## How to connect Power BI

1. Open **Power BI Desktop**.
2. Select **Get Data**.
3. Choose **SQL Server** if you are connecting through the SQL analytics endpoint connection details exposed by Fabric.
4. Enter the **server** and **database** values for the Fabric SQL analytics endpoint.
5. Choose **Import** or **DirectQuery** based on your reporting pattern.
6. In Navigator, select the typed views rather than the raw `Orders` and `Customers` mirrored tables.
7. Load the data and build the semantic model from the views.

If your Fabric environment exposes the endpoint through OneLake shortcuts or a published semantic model path, keep the same principle: select the curated views whenever possible.

## DirectLake mode considerations

DirectLake is typically used with Fabric semantic models backed by Lakehouse or Warehouse storage. For this solution, the SQL views are most useful when the SQL analytics endpoint is the consumer-facing layer.

Considerations:

- a SQL view adds transformation logic at query time, which is conceptually closer to SQL endpoint consumption than raw DirectLake table access
- if you later need DirectLake-scale performance, consider materializing the cleaned output into a Warehouse or Lakehouse table and building the semantic model on that persisted result
- use the views first when the priority is correctness and rapid delivery, then optimize later if usage patterns justify it

## Refresh and caching behavior

Because the views are computed at query time:

- **Import mode** refreshes evaluate the view logic during dataset refresh
- **DirectQuery** sends queries through the view each time visuals execute, subject to Power BI and Fabric caching behavior
- any update in the raw mirrored table is reflected through the view on the next query or refresh
- complex JSON parsing and repeated `TRY_CAST` operations can increase query cost for DirectQuery reports

Practical guidance:

- use Import mode when data latency requirements allow scheduled refresh
- use DirectQuery when near-real-time visibility matters more than peak query speed
- if a report becomes slow, consider pushing the heaviest view logic into a persisted downstream table

## Modeling tips

- mark `order_id` and `customer_id` as keys where appropriate
- build relationships from `vw_Orders_Typed.customer_id` to `vw_Customers_Typed.customer_id`
- hide raw diagnostic columns such as `_rid` or `_ts` from report authors unless they are needed for data-quality reporting
- keep fallback defaults in mind; for example, `Unknown` or `Unclassified` may represent data remediation needs rather than true business categories

## Sample DAX measures for residual NULL handling

Even after view cleanup, some values may still be `BLANK()` because the source value was never recoverable.

### Total Sales

```DAX
Total Sales =
SUMX(
    'vw_Orders_Typed',
    COALESCE('vw_Orders_Typed'[order_amount], 0)
)
```

### Average Order Amount (ignoring unrecoverable blanks)

```DAX
Average Order Amount =
AVERAGEX(
    FILTER(
        'vw_Orders_Typed',
        NOT ISBLANK('vw_Orders_Typed'[order_amount])
    ),
    'vw_Orders_Typed'[order_amount]
)
```

### Customers with Missing Email

```DAX
Customers Missing Email =
COUNTROWS(
    FILTER(
        'vw_Customers_Typed',
        ISBLANK('vw_Customers_Typed'[email_address])
    )
)
```

### Recoverable vs Unrecoverable Orders

```DAX
Orders Missing Amount =
COUNTROWS(
    FILTER(
        'vw_Orders_Typed',
        ISBLANK('vw_Orders_Typed'[order_amount])
    )
)
```

## Recommended rollout pattern

1. Deploy the SQL views.
2. Validate a few known records against the raw mirrored tables.
3. Update Power BI datasets to use the views.
4. Republish reports.
5. Monitor query performance and data-quality exceptions.

## Bottom line

For Power BI, the views are the safest contract. They centralize type recovery, JSON extraction, and fallback handling so report authors can focus on analytics instead of mirror quirks.