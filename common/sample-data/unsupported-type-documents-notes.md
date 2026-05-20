# Unsupported Type Sample Notes

These documents intentionally use **BSON-style extended JSON representations** to illustrate values that should be normalized before analytics. They are teaching samples, not recommended production payloads for Azure Cosmos DB for NoSQL.

## Expected behavior by sample

| Document | Field | Example | What to expect in Fabric mirroring |
| --- | --- | --- | --- |
| `order-unsupported-3001` | `amountDecimal128` | `{ "$numberDecimal": "1234.5678" }` | Decimal128 is not a safe analytics type here; convert to string or numeric before mirroring. |
| `order-unsupported-3001` | `promoRegex` | `{ "$regularExpression": ... }` | Regex metadata is not query-friendly as a mirrored typed column; split to pattern/options strings. |
| `order-unsupported-3002` | `legacyCustomerPointer` | `{ "$dbPointer": ... }` | DB pointer semantics are not preserved; flatten to `targetCollection` and `targetId`. |
| `order-unsupported-3002` | `ruleScript` | `{ "$code": "function ..." }` | Code objects should be treated as opaque text at most; do not rely on mirrored execution semantics. |
| `order-unsupported-3003` | `membershipSymbol` | `{ "$symbol": "PLATINUM" }` | Convert to a normal string field such as `membershipLevel`. |
| `order-unsupported-3003` | `rangeMarker` | `{ "$minKey": 1 }` | MinKey/MaxKey should be replaced with explicit sentinel strings or ordinal values. |
| `order-unsupported-3004` | `pricing.list` | nested Decimal128 | Nested unsupported types are still problematic even when buried inside an object. |
| `order-unsupported-3004` | `pricing.discountRule` | nested Regex | Nested unsupported values commonly become unusable without normalization. |

## Recommended normalization patterns

- Decimal128 -> string or integer minor units (`amountInCents`)
- Regex -> `patternText` + `patternOptions`
- DB Pointer -> `refCollection` + `refId`
- JavaScript/Code -> description string only
- Symbol -> plain string enum
- MinKey/MaxKey -> explicit lower/upper bound markers

## Why keep these samples?

They make it easy to demonstrate solution 1 (pre-ingestion normalization), solution 2 (dual-property pattern), and solution 5 (schema governance) without needing a live production source.
