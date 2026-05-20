# Solution Comparison

This repository presents five complementary patterns. None is universally best; the right choice depends on where you can intervene: application, ingestion, Fabric, or governance.

## Comparison summary

| Solution | Best for | Main idea | Strengths | Trade-offs |
| --- | --- | --- | --- | --- |
| Pre-ingestion normalization | Teams that control writes or have an ingestion pipeline | Convert risky values into safe canonical shapes before mirroring | Highest fidelity, best prevention, predictable schema | Requires app/pipeline work |
| Dual-property pattern | Important fields with business ambiguity | Store both a typed analytics property and a raw/original property | Preserves meaning and auditability | Extra storage, extra modeling discipline |
| Post-mirror Spark normalization | Teams that cannot change the source quickly | Clean mirrored files into curated downstream tables | No source change required, scalable | Adds downstream processing layer |
| SQL views + `OPENJSON` | Consumers mostly need query convenience | Hide JSON text and flatten nested data in reusable SQL views | Fastest path for analysts | Does not prevent upstream data loss |
| Schema governance | Mature teams with platform controls | Stop drift before it becomes a production analytics problem | Low runtime cost, organization-wide benefit | Requires process adoption |

---

## 1. Pre-ingestion normalization

**Folder:** `solution-1-pre-ingestion-normalization`

### When to use it

Use this when you can change the producer, API, Azure Function, data factory step, or worker that writes to Cosmos DB.

### What it does

- coerces values to a stable type before insert/update
- converts unsupported values to strings or supported JSON
- removes or reshapes mixed arrays
- limits document width/depth before Fabric sees it

### Pros

- Best protection against silent `NULL`s
- Best way to preserve business meaning
- Keeps mirrored schema stable over time
- Simplifies all downstream consumers

### Cons

- Requires engineering change where data is written
- Can add latency or complexity to ingest path
- Needs clear data contracts and test coverage

### Typical examples

- Convert `"19.95"` to `19.95`
- Convert Decimal128 to string or integer minor units
- Replace regex/code objects with descriptive strings

---

## 2. Dual-property pattern

**Folder:** `solution-2-dual-property-pattern`

### When to use it

Use this when a value is operationally messy but analytically important.

### What it does

Store two fields:

- `amountNumeric`: safe typed field for analytics
- `amountRaw`: original field preserved for troubleshooting or replay

### Pros

- Reduces analytics breakage
- Keeps original payload for traceability
- Easy for BI models to prefer the typed column

### Cons

- Increases storage and schema surface area
- Requires naming conventions and discipline
- Raw field can still be messy JSON/text

### Typical examples

- `statusCode` plus `statusCodeRaw`
- `eventTimestampUtc` plus `eventTimestampOriginal`
- `patternText` plus `patternOptions`

---

## 3. Post-mirror Spark normalization

**Folder:** `solution-3-post-mirror-spark`

### When to use it

Use this when the source system is hard to change, shared by multiple teams, or owned by a vendor.

### What it does

- reads mirrored Delta files with Spark
- casts, flattens, and validates rows
- writes curated tables for analytics/Power BI
- can quarantine bad records for review

### Pros

- No producer change required
- Handles large volumes well
- Great for building bronze/silver/gold patterns
- Lets you repair historical data after the fact

### Cons

- Data loss may already have happened in the mirrored projection
- Adds cost and orchestration complexity
- Usually batch or micro-batch, not true source-side prevention

### Typical examples

- explode nested arrays into child tables
- re-cast columns based on business rules
- separate valid and invalid records

---

## 4. SQL views + `OPENJSON`

**Folder:** `solution-4-sql-views-openjson`

### When to use it

Use this when the biggest pain is nested JSON text in the SQL analytics endpoint rather than source-side type drift.

### What it does

- keeps the mirrored table as-is
- exposes analyst-friendly SQL views
- uses `OPENJSON`, `CROSS APPLY`, and `OUTER APPLY`
- flattens selected paths without rebuilding the whole dataset

### Pros

- Fastest to adopt
- Familiar to SQL users
- Good for nested objects and arrays represented as JSON text
- Can be layered over existing mirrored assets immediately

### Cons

- Cannot recover values already mirrored as `NULL`
- Complex views can become hard to maintain
- Query performance depends on JSON size and parsing complexity

### Typical examples

- flatten `items[]` into one row per item
- expose `address.city` as a relational column
- convert nested arrays into reusable reporting views

---

## 5. Schema governance

**Folder:** `solution-5-schema-governance`

### When to use it

Use this when several teams write to Cosmos DB and you need a sustainable operating model.

### What it does

- defines allowed field names and types
- adds CI checks and producer validation
- monitors drift, new properties, and risky changes
- documents approved evolution paths

### Pros

- Prevents incidents before they hit Fabric
- Scales across teams and services
- Usually cheaper than repeated downstream cleanup

### Cons

- Process change can be slower than code change
- Requires ownership and operational follow-through
- Governance alone does not repair already-damaged data

### Typical examples

- JSON schema or contract tests in CI
- release checklist for new fields
- alerts when a column begins arriving as null unexpectedly

---

## When to use which solution

| Situation | Best starting point |
| --- | --- |
| You own the producer and need the cleanest mirror | Pre-ingestion normalization |
| You need both accuracy and raw fidelity | Dual-property pattern |
| You cannot change the source soon | Post-mirror Spark normalization |
| Nested JSON is the main reporting pain | SQL views + `OPENJSON` |
| Multiple teams keep breaking schema assumptions | Schema governance |

---

## Can the solutions be combined?

Yes, and they often should be.

### Common combinations

1. **Pre-ingestion normalization + schema governance**
   - best long-term operating model
2. **Dual-property pattern + SQL views**
   - preserve raw values while exposing easy SQL surfaces
3. **Post-mirror Spark + governance**
   - clean existing data while preventing future drift
4. **All five**
   - realistic for enterprise environments with critical analytics

A good rule is:

- **prevent upstream** whenever possible,
- **preserve raw context** where needed,
- **curate downstream** for consumers.

---

## Cost implications

| Solution | Cost profile |
| --- | --- |
| Pre-ingestion normalization | Mostly engineering time; minimal recurring analytics cost |
| Dual-property pattern | Extra storage and some model complexity |
| Post-mirror Spark normalization | Ongoing Fabric/Spark compute cost |
| SQL views + `OPENJSON` | Low setup cost; query-time compute overhead |
| Schema governance | Low platform cost; moderate people/process cost |

### Important nuance

The cheapest-looking option is not always cheapest overall. Silent `NULL`s can create reporting defects, manual investigations, and business mistrust that cost more than preventive engineering.

---

## Implementation effort

| Solution | Effort | Operational burden |
| --- | --- | --- |
| Pre-ingestion normalization | Medium | Low-Medium |
| Dual-property pattern | Medium | Medium |
| Post-mirror Spark normalization | Medium-High | Medium-High |
| SQL views + `OPENJSON` | Low-Medium | Medium |
| Schema governance | Low-Medium initially, then ongoing | Medium |

---

## Recommended selection logic

```text
Can you change the writer?
├─ Yes -> Start with pre-ingestion normalization
│         + add schema governance
└─ No  -> Do you need fast analytics relief?
          ├─ Yes -> Add SQL views / OPENJSON
          └─ Need curated trusted data -> Add post-mirror Spark

Need original value preserved?
└─ Add dual-property pattern wherever the business field is high value.
```
