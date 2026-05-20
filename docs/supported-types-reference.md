# Supported Types Reference

Use this page as a quick guide when deciding whether a Cosmos DB property is safe to mirror directly, should be normalized first, or must be handled as JSON text.

> Note: Fabric mirroring behavior is schema-inferred and may evolve. Treat the tables below as a practical design reference for this repository, not a protocol specification.

## Supported JSON-oriented shapes

| Cosmos DB value/shape | Practical mirror representation | Notes |
| --- | --- | --- |
| `"text"` | String column (`string` / `varchar`) | Safe when the property stays a string everywhere. |
| `123` | Integer-like numeric column (`long` / `bigint`) | Whole numbers are generally safe if the field remains numeric. |
| `123.45` | Floating-point numeric column (`double`) | Mixing integers and floats is usually an upcast-friendly numeric scenario. |
| `true` / `false` | Boolean column | Safe if the field stays boolean. |
| `null` | `NULL` | `null` alone does not define the final type; the first non-null value usually does. |
| `{ ... }` | Commonly surfaced as JSON text in SQL analytics endpoint | Query with `OPENJSON` when you need relational expansion. |
| `[ ... ]` (single logical element type) | Often handled as nested/JSON content for querying | Keep array element shapes consistent. |

## Conceptual type mapping

| Cosmos DB logical type | Delta/Parquet-style landing concept | SQL analytics endpoint experience |
| --- | --- | --- |
| String | `STRING` / UTF-8 text | `varchar(...)` / `varchar(max)` |
| Integer | `LONG` / `INT64` | numeric column |
| Float | `DOUBLE` | numeric column |
| Boolean | `BOOLEAN` | bit/boolean-like column |
| Null | null marker | `NULL` |
| Object | nested structure in landing data, but commonly surfaced as JSON text for SQL | JSON string |
| Array | nested collection in landing data, but commonly surfaced as JSON text for SQL | JSON string |

## Unsupported BSON-style types

These values should be normalized before analytics use.

| Unsupported type | Example representation | Recommended normalization |
| --- | --- | --- |
| Decimal128 | `{ "$numberDecimal": "19.99" }` | Convert to string or numeric minor units before mirroring |
| Regex | `{ "$regularExpression": { "pattern": "^A", "options": "i" } }` | Split into pattern/options strings |
| DB Pointer | `{ "$dbPointer": { "$ref": "customers", "$id": "C1" } }` | Store target collection/id as plain strings |
| JavaScript / Code | `{ "$code": "function() { ... }" }` | Store descriptive text, not executable content |
| Symbol | `{ "$symbol": "VIP" }` | Convert to plain string |
| MinKey / MaxKey | `{ "$minKey": 1 }`, `{ "$maxKey": 1 }` | Replace with sentinel strings or explicit rank values |

## Compatible upcasts

These changes are usually less risky because they stay within the numeric family or represent absence.

| Earlier value | Later value | Typical outcome |
| --- | --- | --- |
| `null` | string/number/boolean | The later non-null value establishes the type |
| integer | float | Upcast to floating-point-compatible numeric |
| float | integer | Usually still compatible with numeric projection |
| missing property | present property | New column/value can appear automatically |

## Incompatible changes

These changes commonly produce silent `NULL`s or brittle projections.

| Earlier shape | Later shape | Risk |
| --- | --- | --- |
| number | string | High |
| string | number | High |
| array | string | High |
| string | array | High |
| object | scalar | High |
| scalar | object | High |
| boolean | array | High |
| homogeneous array | mixed-type array | High |

## Nested object behavior

Nested objects are not automatically expanded into friendly relational columns. In the SQL analytics endpoint, they are commonly exposed as JSON strings.

### Example

```json
{
  "address": {
    "city": "Seattle",
    "country": "USA"
  }
}
```

Use T-SQL like this:

```sql
SELECT t.id, a.city, a.country
FROM dbo.Orders AS t
OUTER APPLY OPENJSON(t.address) WITH (
    city varchar(100) '$.city',
    country varchar(100) '$.country'
) AS a;
```

## Array behavior constraints

Design arrays carefully:

- Prefer a **single element type** per array.
- Keep object arrays structurally consistent.
- Avoid mixing scalars and objects in the same array.
- Avoid changing a property from array to scalar later.

### Good

```json
"tags": ["priority", "gift"]
```

```json
"items": [
  { "sku": "A-100", "qty": 1 },
  { "sku": "B-200", "qty": 2 }
]
```

### Bad

```json
"tags": ["priority", 1, true]
```

```json
"items": "A-100"
```

## Design guidance

If a field is business-critical, make it one of these before it reaches Fabric mirroring:

- stable string
- stable numeric
- stable boolean
- predictable JSON object/array that you intentionally query with `OPENJSON`

If it cannot meet that bar, normalize it or preserve it separately as raw text.
