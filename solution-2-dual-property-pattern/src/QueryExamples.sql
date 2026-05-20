-- Reconstruct a logical amount column from mirrored dual-property columns.
SELECT
    id,
    COALESCE(amount_float, TRY_CAST(amount_string AS FLOAT), CAST(amount_int AS FLOAT)) AS amount
FROM dbo.orders_mirrored;

-- Preserve observability by also showing which typed column supplied the final value.
SELECT
    id,
    COALESCE(amount_float, TRY_CAST(amount_string AS FLOAT), CAST(amount_int AS FLOAT)) AS amount,
    CASE
        WHEN amount_float IS NOT NULL THEN 'float'
        WHEN amount_string IS NOT NULL THEN 'string'
        WHEN amount_int IS NOT NULL THEN 'int'
        ELSE 'missing'
    END AS amount_source_type
FROM dbo.orders_mirrored;

-- Query a boolean field protected with the dual-property pattern.
SELECT
    id,
    approved_bool AS approved
FROM dbo.invoices_mirrored
WHERE approved_bool = 1;

-- Parse JSON or array string companions only when needed.
SELECT
    id,
    profile_json,
    tags_array
FROM dbo.customers_mirrored
WHERE profile_json IS NOT NULL
   OR tags_array IS NOT NULL;

-- Example view that hides mirror-time null behavior from analysts.
CREATE OR ALTER VIEW dbo.vw_orders_normalized
AS
SELECT
    id,
    customerId,
    COALESCE(amount_float, TRY_CAST(amount_string AS FLOAT), CAST(amount_int AS FLOAT)) AS amount,
    COALESCE(status_string, CAST(status_int AS VARCHAR(50))) AS status
FROM dbo.orders_mirrored;
