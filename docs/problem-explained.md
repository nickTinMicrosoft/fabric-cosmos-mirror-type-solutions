# Problem Explained

## Why this repository exists

Fabric mirroring makes Azure Cosmos DB data available in OneLake and the SQL analytics endpoint without building a custom ETL pipeline. That convenience comes with an important trade-off: the mirrored schema is inferred from incoming data and is **not full-fidelity**.

When a property changes type, or when the source contains values the mirror cannot represent, you usually do **not** get a loud failure. You get missing values, JSON text, or silently dropped fields.

---

## How Fabric Cosmos DB mirroring works

At a high level, the flow looks like this:

```text
+------------------------+
| Azure Cosmos DB        |
| API for NoSQL          |
| Containers + items     |
+-----------+------------+
            |
            | continuous backup / mirrored ingestion
            v
+------------------------+
| Fabric mirroring       |
| schema inference       |
| replication service    |
+-----------+------------+
            |
            v
+------------------------+      +---------------------------+
| OneLake Delta/Parquet  | ---> | SQL analytics endpoint    |
| mirrored landing files |      | warehouse-style querying  |
+------------------------+      +---------------------------+
```

Key points:

- Mirroring requires **Azure Cosmos DB for NoSQL**.
- The source account must use **continuous backup**.
- New properties are discovered and added automatically.
- Removed properties do not remove columns; they simply show up as `NULL` for later rows.

---

## The "first non-null type wins" rule

Fabric infers a column type from observed data. The first usable non-null value for a property strongly influences the type chosen for that mirrored column.

### Example

```json
// document A
{ "id": "1", "amount": null }

// document B
{ "id": "2", "amount": 19.95 }

// document C
{ "id": "3", "amount": "19.95" }
```

Likely outcome:

- `amount` becomes a numeric column because document B supplied the first non-null typed value.
- Document C still replicates, but `amount` becomes **NULL** in the mirrored projection because the string value is incompatible with the established numeric type.

```text
Source item          Mirrored column type     Mirrored value
------------------   ----------------------   --------------
amount = null        DOUBLE/DECIMAL-like      NULL
amount = 19.95       DOUBLE/DECIMAL-like      19.95
amount = "19.95"     DOUBLE/DECIMAL-like      NULL   <- silent mismatch
```

This is the core failure mode that the sample solutions address.

---

## What happens with incompatible types

When a later document sends a value that cannot be upcast into the established type, the mirror generally writes **NULL** for that row/column instead of failing the pipeline.

Common examples:

| Earlier accepted shape | Later shape | Typical result |
| --- | --- | --- |
| `number` | `string` | `NULL` |
| `array` | `string` | `NULL` |
| `object` represented as JSON text | scalar | `NULL` or unusable projection |
| homogeneous array | mixed-type array | unreliable projection / exclusion risk |

### Why this is dangerous

- No exception is raised in your application.
- No obvious SQL error points to the bad document.
- Analysts may interpret missing values as real business nulls.
- The problem can persist until containers or mirrored tables are recreated.

---

## What happens with unsupported BSON-style types

Some types have no clean destination in the mirrored schema. Examples commonly called out in interoperability discussions include:

- Decimal128
- Regex
- DB Pointer
- JavaScript / Code with scope
- Symbol
- MinKey / MaxKey

When these values appear in unsupported form, the mirror does **not** preserve them as strongly typed analytics columns. Depending on the source representation, they may be:

- omitted entirely,
- flattened into opaque JSON text only after custom normalization, or
- effectively unusable until converted into supported JSON-friendly shapes.

### Practical takeaway

If you need these values downstream, convert them **before mirroring** into stable strings, numbers, or structured JSON that you control.

---

## Nested objects become JSON strings

Mirroring does not expose nested objects as fully expanded relational columns by default in the SQL analytics endpoint. Instead, nested content is commonly surfaced as JSON text.

```json
{
  "id": "1001",
  "address": {
    "street": "1 Main St",
    "city": "Seattle",
    "country": "USA"
  }
}
```

Conceptually becomes:

```text
id      address
----    ----------------------------------------------
1001    {"street":"1 Main St","city":"Seattle",...}
```

That is why solution 4 uses `OPENJSON`, `CROSS APPLY`, and `OUTER APPLY` to selectively expand nested payloads into query-friendly views.

---

## Mixed-type arrays are risky

Arrays are especially fragile when element types drift.

### Safe-ish pattern

```json
"tags": ["priority", "export", "wholesale"]
```

### Problem pattern

```json
"tags": ["priority", 42, true, { "code": "VIP" }]
```

Mixed-type arrays can lead to:

- inconsistent schema inference,
- hard-to-query JSON text,
- missing values in projected tables, or
- document/value exclusion depending on how the data is interpreted.

Rule of thumb: keep each array to **one logical element type**.

---

## Existing table size limit for older mirrored tables

For mirrored tables created **before November 18, 2025**, the SQL analytics endpoint only supports **`varchar(8000)`** for string-shaped mirrored columns.

Implications:

- Large nested JSON strings can exceed 8 KB.
- Queries can fail with JSON formatting/parsing errors.
- Recreating older mirrored tables is required to move to the newer behavior.

For tables created after that date, the SQL analytics endpoint supports **`varchar(max)`** behavior for much larger values, up to the practical Cosmos DB document size limit.

---

## Schema depth and property count constraints

Fabric mirroring inherits important Azure Cosmos DB analytical store schema constraints:

- **Maximum 1,000 properties** across the schema
- **Maximum nesting depth of 127**

These limits matter even if the SQL endpoint can query JSON text more flexibly.

```text
Document shape pressure
┌────────────────────────────────────────────┐
│ wide documents   -> property count risk    │
│ deep documents   -> nesting depth risk     │
│ drifting shapes  -> nulls / brittle schema │
└────────────────────────────────────────────┘
```

If your source documents are both wide and deeply nested, you should strongly consider normalization or curation before mirroring.

---

## Typical failure scenarios

### Scenario 1: type drift

```text
order.total
  row 1 -> 25.50   (number)
  row 2 -> "25.50" (string)
  row 3 -> 31.00   (number)
```

Result: row 2 becomes `NULL` in analytics.

### Scenario 2: object vs scalar drift

```text
customerPreference
  row 1 -> {"contact":"email"}
  row 2 -> "email"
```

Result: one side of the drift becomes unusable or null.

### Scenario 3: array drift

```text
items
  row 1 -> [{...}, {...}]
  row 2 -> "not-an-array"
```

Result: later rows can project as `NULL` or become difficult to flatten safely.

---

## Why the five solutions exist

The solutions in this repository map to five response strategies:

1. **Fix the data before it lands**.
2. **Store both typed and raw values**.
3. **Repair the mirrored output after landing**.
4. **Query around the problem with SQL views**.
5. **Prevent drift through contracts and governance**.

Use the comparison guide next: [solution-comparison.md](./solution-comparison.md).
