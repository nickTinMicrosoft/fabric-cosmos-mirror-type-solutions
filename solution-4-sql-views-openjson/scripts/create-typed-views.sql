/*
    Solution 4: typed SQL views over Fabric mirrored Cosmos DB tables

    Purpose:
    - Hide raw mirror type inconsistencies from downstream consumers.
    - Safely convert mixed-type columns without failing queries.
    - Provide clean column names and business-friendly defaults.

    Assumptions:
    - dbo.Orders and dbo.Customers are mirrored tables from Cosmos DB.
    - Nested objects may be stored as JSON text in varchar columns.
    - Some numeric/date columns may be NULL because mirroring could not coerce the original value.
*/

/*
    View: dbo.vw_Orders_Typed

    What problem it solves:
    - Orders often contain mixed numeric types for amount columns.
    - Date fields may arrive as ISO strings, local strings, or epoch-like text.
    - Nested shipping and status objects may be stored as JSON strings.
    - Consumers should not have to know which raw column shape was populated.
*/
CREATE OR ALTER VIEW dbo.vw_Orders_Typed
AS
SELECT
    o.id                                              AS order_id,
    o.customerId                                      AS customer_id,
    COALESCE(o.orderNumber, JSON_VALUE(o.orderMetaJson, '$.orderNumber')) AS order_number,

    /*
        Pattern: TRY_CAST handles string/int/float inputs without raising an error.
        Example source values: '125.50', 125, 125.5
    */
    COALESCE(
        TRY_CAST(o.amount AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2)),
        TRY_CAST(o.amountText AS decimal(18,2)),
        0.00
    )                                                 AS order_amount,

    /*
        Pattern: CASE + ISNUMERIC for conditional casting.
        ISNUMERIC is not perfect for every locale/currency symbol, but it is a useful guard
        when the mirrored value may contain free-form text.
    */
    CASE
        WHEN ISNUMERIC(o.taxAmountText) = 1 THEN TRY_CAST(o.taxAmountText AS decimal(18,2))
        WHEN ISNUMERIC(JSON_VALUE(o.orderTotalsJson, '$.tax')) = 1 THEN TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.tax') AS decimal(18,2))
        ELSE NULL
    END                                               AS tax_amount,

    COALESCE(
        TRY_CAST(o.discountAmount AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.discount') AS decimal(18,2)),
        0.00
    )                                                 AS discount_amount,

    COALESCE(
        TRY_CAST(o.amount AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.amount') AS decimal(18,2)),
        0.00
    ) - COALESCE(
        TRY_CAST(o.discountAmount AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(o.orderTotalsJson, '$.discount') AS decimal(18,2)),
        0.00
    )                                                 AS net_amount,

    /*
        Pattern: TRY_CONVERT safely handles inconsistent date strings.
        Attempt ISO 8601 first (style 127), then fall back to generic conversion.
    */
    COALESCE(
        TRY_CONVERT(datetime2(0), o.orderDate, 127),
        TRY_CONVERT(datetime2(0), o.orderDate),
        TRY_CONVERT(datetime2(0), JSON_VALUE(o.orderMetaJson, '$.createdAt'), 127)
    )                                                 AS order_date_utc,

    COALESCE(
        TRY_CONVERT(date, o.shipDate, 127),
        TRY_CONVERT(date, o.shipDate),
        TRY_CONVERT(date, JSON_VALUE(o.shippingJson, '$.shipDate'), 127)
    )                                                 AS ship_date,

    COALESCE(o.currencyCode, JSON_VALUE(o.orderTotalsJson, '$.currency'), 'USD') AS currency_code,
    COALESCE(o.status, JSON_VALUE(o.statusJson, '$.code'), 'Unknown')             AS order_status,
    COALESCE(TRY_CAST(o.itemCount AS int), TRY_CAST(JSON_VALUE(o.orderMetaJson, '$.itemCount') AS int), 0) AS item_count,

    /* Nested scalar extraction from JSON object columns */
    JSON_VALUE(o.shippingJson, '$.method')            AS shipping_method,
    JSON_VALUE(o.shippingJson, '$.carrier')           AS shipping_carrier,
    JSON_VALUE(o.shippingJson, '$.address.city')      AS ship_to_city,
    JSON_VALUE(o.shippingJson, '$.address.state')     AS ship_to_state,
    JSON_VALUE(o.shippingJson, '$.address.postalCode') AS ship_to_postal_code,

    /* Preserve raw metadata for diagnostics */
    o._rid                                            AS mirror_rid,
    o._ts                                             AS mirror_epoch_ts
FROM dbo.Orders AS o;
GO

/*
    View: dbo.vw_Customers_Typed

    What problem it solves:
    - Customer profile fields may shift between scalar columns and JSON object text.
    - Spend/credit style fields may arrive as numeric text or numeric values.
    - Date attributes may be inconsistent across records.
    - Default handling makes BI joins and dimensions easier to use.
*/
CREATE OR ALTER VIEW dbo.vw_Customers_Typed
AS
SELECT
    c.id                                              AS customer_id,
    COALESCE(c.customerNumber, JSON_VALUE(c.profileJson, '$.customerNumber')) AS customer_number,
    COALESCE(c.firstName, JSON_VALUE(c.profileJson, '$.firstName'), 'Unknown') AS first_name,
    COALESCE(c.lastName, JSON_VALUE(c.profileJson, '$.lastName'), 'Unknown')   AS last_name,
    COALESCE(c.email, JSON_VALUE(c.contactJson, '$.email'))                    AS email_address,
    COALESCE(c.phone, JSON_VALUE(c.contactJson, '$.phone'))                    AS phone_number,

    /* Safe numeric conversion with fallback across mirrored columns and JSON */
    COALESCE(
        TRY_CAST(c.lifetimeValue AS decimal(18,2)),
        TRY_CAST(c.lifetimeValueText AS decimal(18,2)),
        TRY_CAST(JSON_VALUE(c.metricsJson, '$.lifetimeValue') AS decimal(18,2)),
        0.00
    )                                                 AS lifetime_value,

    CASE
        WHEN ISNUMERIC(c.creditLimitText) = 1 THEN TRY_CAST(c.creditLimitText AS decimal(18,2))
        WHEN ISNUMERIC(JSON_VALUE(c.metricsJson, '$.creditLimit')) = 1 THEN TRY_CAST(JSON_VALUE(c.metricsJson, '$.creditLimit') AS decimal(18,2))
        ELSE 0.00
    END                                               AS credit_limit,

    COALESCE(
        TRY_CONVERT(datetime2(0), c.createdDate, 127),
        TRY_CONVERT(datetime2(0), c.createdDate),
        TRY_CONVERT(datetime2(0), JSON_VALUE(c.profileJson, '$.createdAt'), 127)
    )                                                 AS customer_created_utc,

    COALESCE(
        TRY_CONVERT(date, c.birthDate, 127),
        TRY_CONVERT(date, c.birthDate),
        TRY_CONVERT(date, JSON_VALUE(c.profileJson, '$.birthDate'), 127)
    )                                                 AS birth_date,

    COALESCE(c.segment, JSON_VALUE(c.metricsJson, '$.segment'), 'Unclassified') AS customer_segment,
    COALESCE(c.loyaltyTier, JSON_VALUE(c.metricsJson, '$.loyaltyTier'), 'Standard') AS loyalty_tier,

    /* Address fields extracted from JSON text for easier reporting */
    COALESCE(c.addressLine1, JSON_VALUE(c.addressJson, '$.line1'))              AS address_line_1,
    JSON_VALUE(c.addressJson, '$.line2')                                        AS address_line_2,
    COALESCE(c.city, JSON_VALUE(c.addressJson, '$.city'))                       AS city,
    COALESCE(c.stateProvince, JSON_VALUE(c.addressJson, '$.state'))             AS state_province,
    COALESCE(c.postalCode, JSON_VALUE(c.addressJson, '$.postalCode'))           AS postal_code,
    COALESCE(c.countryCode, JSON_VALUE(c.addressJson, '$.country'), 'US')       AS country_code,

    /* Boolean-like flags sometimes arrive as strings in mirrored data */
    CASE
        WHEN LOWER(COALESCE(TRY_CAST(c.isActive AS varchar(10)), JSON_VALUE(c.profileJson, '$.isActive'))) IN ('true', '1', 'yes') THEN CAST(1 AS bit)
        WHEN LOWER(COALESCE(TRY_CAST(c.isActive AS varchar(10)), JSON_VALUE(c.profileJson, '$.isActive'))) IN ('false', '0', 'no') THEN CAST(0 AS bit)
        ELSE NULL
    END                                               AS is_active,

    c._rid                                            AS mirror_rid,
    c._ts                                             AS mirror_epoch_ts
FROM dbo.Customers AS c;
GO

/*
    Example validation queries after deployment
*/
SELECT TOP (25) * FROM dbo.vw_Orders_Typed ORDER BY order_date_utc DESC;
SELECT TOP (25) * FROM dbo.vw_Customers_Typed ORDER BY customer_created_utc DESC;
GO