/*
    Null-safe query patterns for mirrored Cosmos DB tables in Fabric SQL analytics endpoint

    Focus areas:
    - distinguish type-mismatch NULLs from legitimate business NULLs
    - use mirror metadata (_rid, _ts) to prove the document exists
    - build COALESCE chains across multiple possible source representations
    - aggregate without letting type-mismatch NULLs distort results
    - diagnose columns with suspiciously high NULL rates
*/

/*
    Pattern 1: Identify likely type-mismatch NULLs.

    Interpretation:
    - _rid is populated => the mirrored document row exists
    - amount is NULL => the typed mirrored column could not be populated
    - raw JSON text still contains the value => likely type coercion issue, not a missing document
*/
SELECT TOP (100)
    o.id,
    o._rid,
    o._ts,
    o.amount,
    o.amountText,
    JSON_VALUE(o.orderTotalsJson, '$.amount') AS amount_from_json,
    CASE
        WHEN o._rid IS NOT NULL
         AND o.amount IS NULL
         AND (
                o.amountText IS NOT NULL
                OR JSON_VALUE(o.orderTotalsJson, '$.amount') IS NOT NULL
             )
            THEN 'Likely mirror type mismatch'
        WHEN o._rid IS NOT NULL
         AND o.amount IS NULL
         AND o.amountText IS NULL
         AND JSON_VALUE(o.orderTotalsJson, '$.amount') IS NULL
            THEN 'Likely intentional or source NULL'
        ELSE 'Typed column populated'
    END AS null_diagnosis
FROM dbo.Orders AS o
WHERE o.amount IS NULL;
GO

/*
    Pattern 2: COALESCE chain across potential type variants.
    Use this in ad hoc queries when a typed view is not yet available.
*/
SELECT
    o.id,
    COALESCE(
        TRY_CAST(o.amount AS decimal(18,2)),
        TRY_CAST(o.amountText AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2)),
        0.00
    ) AS resolved_amount
FROM dbo.Orders AS o;
GO

/*
    Pattern 3: Conditional aggregation that skips type-mismatch NULLs.
    Only aggregate rows where at least one numeric representation is available.
*/
SELECT
    COUNT(*) AS total_orders,
    SUM(CASE
            WHEN COALESCE(o.amountText, JSON_VALUE(o.orderTotalsJson, '$.amount')) IS NOT NULL
              OR o.amount IS NOT NULL
                THEN 1
            ELSE 0
        END) AS orders_with_recoverable_amount,
    SUM(COALESCE(
        TRY_CAST(o.amount AS decimal(18,2)),
        TRY_CAST(o.amountText AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2)),
        0.00
    )) AS total_recovered_amount,
    AVG(CASE
            WHEN COALESCE(
                    TRY_CAST(o.amount AS decimal(18,2)),
                    TRY_CAST(o.amountText AS decimal(18,2)),
                    TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2))
                 ) IS NOT NULL
                THEN COALESCE(
                    TRY_CAST(o.amount AS decimal(18,2)),
                    TRY_CAST(o.amountText AS decimal(18,2)),
                    TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2))
                )
            ELSE NULL
        END) AS average_recovered_amount
FROM dbo.Orders AS o;
GO

/*
    Pattern 4: Compare row count versus non-null count per column.
    A simple, explicit diagnostic for one mirrored table.
*/
SELECT
    column_profile.column_name,
    COUNT(*) AS row_count,
    SUM(column_profile.non_null_flag) AS non_null_count,
    COUNT(*) - SUM(column_profile.non_null_flag) AS null_count,
    CAST(100.0 * (COUNT(*) - SUM(column_profile.non_null_flag)) / NULLIF(COUNT(*), 0) AS decimal(5,2)) AS null_rate_pct
FROM dbo.Orders AS o
CROSS APPLY (
    VALUES
        ('amount',              CASE WHEN o.amount IS NOT NULL THEN 1 ELSE 0 END),
        ('amountText',          CASE WHEN o.amountText IS NOT NULL THEN 1 ELSE 0 END),
        ('orderDate',           CASE WHEN o.orderDate IS NOT NULL THEN 1 ELSE 0 END),
        ('shippingJson',        CASE WHEN o.shippingJson IS NOT NULL THEN 1 ELSE 0 END),
        ('status',              CASE WHEN o.status IS NOT NULL THEN 1 ELSE 0 END),
        ('customerId',          CASE WHEN o.customerId IS NOT NULL THEN 1 ELSE 0 END),
        ('discountAmount',      CASE WHEN o.discountAmount IS NOT NULL THEN 1 ELSE 0 END)
) AS column_profile(column_name, non_null_flag)
GROUP BY column_profile.column_name
ORDER BY null_rate_pct DESC, column_profile.column_name;
GO

/*
    Pattern 5: Find suspicious NULL rates across important columns in Orders and Customers.
    Replace the threshold as needed.
*/
WITH column_null_rates AS (
    SELECT
        'Orders' AS table_name,
        column_profile.column_name,
        COUNT(*) AS row_count,
        SUM(column_profile.non_null_flag) AS non_null_count,
        CAST(100.0 * (COUNT(*) - SUM(column_profile.non_null_flag)) / NULLIF(COUNT(*), 0) AS decimal(5,2)) AS null_rate_pct
    FROM dbo.Orders AS o
    CROSS APPLY (
        VALUES
            ('amount',          CASE WHEN o.amount IS NOT NULL THEN 1 ELSE 0 END),
            ('amountText',      CASE WHEN o.amountText IS NOT NULL THEN 1 ELSE 0 END),
            ('orderDate',       CASE WHEN o.orderDate IS NOT NULL THEN 1 ELSE 0 END),
            ('status',          CASE WHEN o.status IS NOT NULL THEN 1 ELSE 0 END),
            ('shippingJson',    CASE WHEN o.shippingJson IS NOT NULL THEN 1 ELSE 0 END)
    ) AS column_profile(column_name, non_null_flag)
    GROUP BY column_profile.column_name

    UNION ALL

    SELECT
        'Customers' AS table_name,
        column_profile.column_name,
        COUNT(*) AS row_count,
        SUM(column_profile.non_null_flag) AS non_null_count,
        CAST(100.0 * (COUNT(*) - SUM(column_profile.non_null_flag)) / NULLIF(COUNT(*), 0) AS decimal(5,2)) AS null_rate_pct
    FROM dbo.Customers AS c
    CROSS APPLY (
        VALUES
            ('lifetimeValue',   CASE WHEN c.lifetimeValue IS NOT NULL THEN 1 ELSE 0 END),
            ('lifetimeValueText', CASE WHEN c.lifetimeValueText IS NOT NULL THEN 1 ELSE 0 END),
            ('createdDate',     CASE WHEN c.createdDate IS NOT NULL THEN 1 ELSE 0 END),
            ('addressJson',     CASE WHEN c.addressJson IS NOT NULL THEN 1 ELSE 0 END),
            ('email',           CASE WHEN c.email IS NOT NULL THEN 1 ELSE 0 END)
    ) AS column_profile(column_name, non_null_flag)
    GROUP BY column_profile.column_name
)
SELECT
    table_name,
    column_name,
    row_count,
    non_null_count,
    row_count - non_null_count AS null_count,
    null_rate_pct
FROM column_null_rates
WHERE null_rate_pct >= 40.00
ORDER BY null_rate_pct DESC, table_name, column_name;
GO

/*
    Pattern 6: Metadata check to show the row exists even when business columns are NULL.
*/
SELECT TOP (50)
    o.id,
    o._rid,
    o._ts,
    CASE WHEN o._rid IS NOT NULL THEN 'Document exists in mirror' ELSE 'No mirrored row' END AS row_status,
    o.amount,
    o.orderDate,
    o.status
FROM dbo.Orders AS o
WHERE o.amount IS NULL
   OR o.orderDate IS NULL
   OR o.status IS NULL
ORDER BY o._ts DESC;
GO