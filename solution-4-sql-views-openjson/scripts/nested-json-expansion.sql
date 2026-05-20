/*
    Nested JSON expansion patterns for Fabric SQL analytics endpoint

    Goals demonstrated:
    - OUTER APPLY with OPENJSON to expand optional arrays
    - CROSS APPLY for required nested arrays
    - JSON_VALUE for scalar extraction from JSON objects
    - multi-level expansion (array within array / object within array)
    - OPENJSON WITH clause for typed extraction

    Performance tips:
    - Prefer OPENJSON ... WITH (...) so JSON is parsed once and typed once.
    - Prefer varchar(n) over varchar(max) for projected properties when practical.
    - Avoid calling JSON_VALUE repeatedly for the same object when OPENJSON WITH can project all needed fields.
    - Filter the base table first when possible so fewer rows hit JSON parsing.
*/

/*
    Example 1: Expand an optional items array into one row per item.
    OUTER APPLY preserves the order row even if itemsJson is NULL or empty.
*/
SELECT
    o.id                                               AS order_id,
    o.customerId                                       AS customer_id,
    TRY_CONVERT(datetime2(0), o.orderDate, 127)        AS order_date_utc,
    item.line_number,
    item.sku,
    item.product_name,
    item.quantity,
    item.unit_price,
    item.extended_price,
    item.item_attributes_json
FROM dbo.Orders AS o
OUTER APPLY OPENJSON(
    CASE WHEN ISJSON(o.itemsJson) = 1 THEN o.itemsJson END
)
WITH (
    line_number         int             '$.lineNumber',
    sku                 varchar(50)     '$.sku',
    product_name        varchar(200)    '$.name',
    quantity            int             '$.quantity',
    unit_price          decimal(18,2)   '$.unitPrice',
    extended_price      decimal(18,2)   '$.extendedPrice',
    item_attributes_json varchar(2000)  '$.attributes' AS JSON
) AS item
ORDER BY o.id, item.line_number;
GO

/*
    Example 2: Extract address fields from a nested JSON object column.
    JSON_VALUE is ideal for scalar values inside an object.
*/
SELECT
    c.id                                                AS customer_id,
    JSON_VALUE(c.addressJson, '$.line1')                AS address_line_1,
    JSON_VALUE(c.addressJson, '$.line2')                AS address_line_2,
    JSON_VALUE(c.addressJson, '$.city')                 AS city,
    JSON_VALUE(c.addressJson, '$.state')                AS state_province,
    JSON_VALUE(c.addressJson, '$.postalCode')           AS postal_code,
    JSON_VALUE(c.addressJson, '$.country')              AS country_code,
    JSON_VALUE(c.addressJson, '$.geo.lat')              AS latitude_text,
    JSON_VALUE(c.addressJson, '$.geo.lon')              AS longitude_text
FROM dbo.Customers AS c
WHERE ISJSON(c.addressJson) = 1;
GO

/*
    Example 3: CROSS APPLY for required nested data.
    Use CROSS APPLY when you only want orders that definitely contain line items.
*/
SELECT
    o.id                                                AS order_id,
    required_item.sku,
    required_item.quantity,
    required_item.unit_price
FROM dbo.Orders AS o
CROSS APPLY OPENJSON(
    CASE WHEN ISJSON(o.itemsJson) = 1 THEN o.itemsJson END
)
WITH (
    sku                 varchar(50)     '$.sku',
    quantity            int             '$.quantity',
    unit_price          decimal(18,2)   '$.unitPrice'
) AS required_item;
GO

/*
    Example 4: Multi-level nesting.
    First expand the items array, then expand a nested discounts array inside each item.
    OUTER APPLY is used twice so missing discounts do not remove the parent item.
*/
SELECT
    o.id                                                AS order_id,
    item.line_number,
    item.sku,
    discount.discount_code,
    discount.discount_type,
    discount.discount_amount
FROM dbo.Orders AS o
OUTER APPLY OPENJSON(
    CASE WHEN ISJSON(o.itemsJson) = 1 THEN o.itemsJson END
)
WITH (
    line_number         int             '$.lineNumber',
    sku                 varchar(50)     '$.sku',
    discounts_json      varchar(2000)   '$.discounts' AS JSON
) AS item
OUTER APPLY OPENJSON(item.discounts_json)
WITH (
    discount_code       varchar(50)     '$.code',
    discount_type       varchar(50)     '$.type',
    discount_amount     decimal(18,2)   '$.amount'
) AS discount
ORDER BY o.id, item.line_number;
GO

/*
    Example 5: Arrays of objects in customer preferences.
    Demonstrates typed extraction from an array where each element is an object.
*/
SELECT
    c.id                                                AS customer_id,
    pref.channel_name,
    pref.is_opted_in,
    pref.preference_rank
FROM dbo.Customers AS c
OUTER APPLY OPENJSON(
    CASE WHEN ISJSON(c.preferencesJson) = 1 THEN c.preferencesJson END,
    '$.channels'
)
WITH (
    channel_name        varchar(50)     '$.name',
    is_opted_in         bit             '$.isOptedIn',
    preference_rank     int             '$.rank'
) AS pref;
GO

/*
    Example 6: Multi-level nesting with object-inside-array and array-inside-object.
    Items contain a fulfillment object, which contains a packages array.
    Parse once at each level and keep projections typed.
*/
SELECT
    o.id                                                AS order_id,
    item.line_number,
    item.sku,
    pkg.package_id,
    pkg.package_status,
    pkg.weight_kg
FROM dbo.Orders AS o
OUTER APPLY OPENJSON(
    CASE WHEN ISJSON(o.itemsJson) = 1 THEN o.itemsJson END
)
WITH (
    line_number         int             '$.lineNumber',
    sku                 varchar(50)     '$.sku',
    fulfillment_json    varchar(2000)   '$.fulfillment' AS JSON
) AS item
OUTER APPLY OPENJSON(item.fulfillment_json)
WITH (
    packages_json       varchar(2000)   '$.packages' AS JSON
) AS fulfillment
OUTER APPLY OPENJSON(fulfillment.packages_json)
WITH (
    package_id          varchar(100)    '$.packageId',
    package_status      varchar(50)     '$.status',
    weight_kg           decimal(10,3)   '$.weightKg'
) AS pkg
ORDER BY o.id, item.line_number, pkg.package_id;
GO