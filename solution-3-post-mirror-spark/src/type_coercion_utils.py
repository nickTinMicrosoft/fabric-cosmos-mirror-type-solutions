"""Utilities for repairing mirrored Cosmos DB data with PySpark.

These helpers are intended for Microsoft Fabric notebooks that read mirrored
Delta tables from OneLake, coerce problematic values into stable analytics
shapes, and write cleansed gold tables.
"""

from __future__ import annotations

from typing import Any, Iterable, Mapping, MutableMapping, Optional, Sequence, Union

try:
    from pyspark.sql import Column, DataFrame
    from pyspark.sql import functions as F
    from pyspark.sql.types import ArrayType, DataType, StringType, StructField, StructType
except ImportError:  # pragma: no cover - allows local editing without Spark installed
    Column = Any  # type: ignore[assignment]
    DataFrame = Any  # type: ignore[assignment]
    F = None  # type: ignore[assignment]
    ArrayType = Any  # type: ignore[assignment]
    DataType = Any  # type: ignore[assignment]
    StringType = Any  # type: ignore[assignment]
    StructField = Any  # type: ignore[assignment]
    StructType = Any  # type: ignore[assignment]


SchemaLike = Union[StructType, Mapping[str, str]]


def _require_pyspark() -> None:
    """Raise a clear error when PySpark is unavailable."""
    if F is None:
        raise ImportError("PySpark is required to use type coercion utilities.")


def safe_cast_column(column_name: str, target_type: str, default: Optional[Any] = None) -> Column:
    """Safely cast a column using Spark SQL `try_cast`.

    Args:
        column_name: Source column name.
        target_type: Spark SQL target type, such as ``double`` or ``timestamp``.
        default: Optional fallback value when the cast returns ``NULL``.

    Returns:
        A Spark ``Column`` expression.
    """
    _require_pyspark()
    expression = F.expr(f"try_cast(`{column_name}` as {target_type})")
    if default is None:
        return expression
    return F.coalesce(expression, F.lit(default))


def coerce_numeric_columns(df: DataFrame, mapping: Mapping[str, str]) -> DataFrame:
    """Apply safe numeric casts for a set of columns.

    Args:
        df: Source DataFrame.
        mapping: Dict of column name to Spark SQL numeric type.

    Returns:
        DataFrame with repaired numeric columns.
    """
    _require_pyspark()
    output = df
    for column_name, target_type in mapping.items():
        if column_name in output.columns:
            output = output.withColumn(column_name, safe_cast_column(column_name, target_type))
    return output


def parse_json_column(
    df: DataFrame,
    source_column: str,
    schema: StructType,
    output_column: Optional[str] = None,
) -> DataFrame:
    """Parse a JSON string column into a struct column.

    Args:
        df: Source DataFrame.
        source_column: Column containing JSON text.
        schema: Expected struct schema.
        output_column: Optional destination column. Defaults to source name.

    Returns:
        DataFrame with parsed JSON column.
    """
    _require_pyspark()
    destination = output_column or source_column
    return df.withColumn(destination, F.from_json(F.col(source_column), schema))


def parse_json_columns(df: DataFrame, mapping: Mapping[str, StructType]) -> DataFrame:
    """Parse multiple JSON string columns using supplied schemas."""
    _require_pyspark()
    output = df
    for column_name, schema in mapping.items():
        if column_name in output.columns:
            output = parse_json_column(output, column_name, schema)
    return output


def normalize_array_column(df: DataFrame, column_name: str) -> DataFrame:
    """Normalize an array column into ``array<string>`` for analytics stability.

    Nulls are removed and every surviving element is converted to a JSON/string
    representation so mixed element types can still be preserved downstream.
    """
    _require_pyspark()
    return df.withColumn(
        column_name,
        F.when(
            F.col(column_name).isNull(),
            F.lit(None).cast("array<string>"),
        ).otherwise(
            F.expr(
                f"filter(transform(`{column_name}`, x -> CASE "
                f"WHEN x IS NULL THEN NULL "
                f"WHEN typeof(x) = 'string' THEN cast(x as string) "
                f"ELSE to_json(x) END), x -> x IS NOT NULL)"
            )
        ),
    )


def normalize_array_columns(df: DataFrame, columns: Sequence[str]) -> DataFrame:
    """Normalize each named array column into a consistent array-of-string shape."""
    _require_pyspark()
    output = df
    for column_name in columns:
        if column_name in output.columns:
            output = normalize_array_column(output, column_name)
    return output


def extract_json_from_raw_body(
    df: DataFrame,
    raw_body_column: str,
    field_name: str,
    target_type: str = "string",
    output_column: Optional[str] = None,
) -> DataFrame:
    """Recover a field from the mirrored raw body JSON payload.

    Args:
        df: Source DataFrame.
        raw_body_column: Column holding the full-fidelity JSON body.
        field_name: JSON field to recover.
        target_type: Spark SQL type for the repaired value.
        output_column: Optional target column name.

    Returns:
        DataFrame with the recovered field added or replaced.
    """
    _require_pyspark()
    destination = output_column or field_name
    json_path = f"$.{field_name}"
    return df.withColumn(
        destination,
        F.expr(f"try_cast(get_json_object(`{raw_body_column}`, '{json_path}') as {target_type})"),
    )


def null_rate_report(df: DataFrame) -> DataFrame:
    """Return per-column null counts and percentages for a DataFrame."""
    _require_pyspark()
    total_rows = df.count()
    if total_rows == 0:
        raise ValueError("null_rate_report requires at least one row.")

    expressions = [
        F.sum(F.when(F.col(column_name).isNull(), 1).otherwise(0)).alias(column_name)
        for column_name in df.columns
    ]
    null_counts_row = df.agg(*expressions).collect()[0].asDict()
    rows = [
        (
            column_name,
            int(null_count),
            float(null_count) / float(total_rows),
        )
        for column_name, null_count in null_counts_row.items()
    ]
    return df.sparkSession.createDataFrame(rows, ["column_name", "null_count", "null_pct"])


def columns_exceeding_null_threshold(df: DataFrame, threshold: float) -> DataFrame:
    """Filter the null rate report to columns above the supplied threshold."""
    _require_pyspark()
    return null_rate_report(df).filter(F.col("null_pct") >= F.lit(threshold)).orderBy(F.col("null_pct").desc())


def compare_schema(expected: SchemaLike, actual: SchemaLike) -> list[dict[str, Optional[str]]]:
    """Compare expected and actual schemas and return mismatches.

    Args:
        expected: Expected schema as ``StructType`` or ``dict[str, str]``.
        actual: Actual schema as ``StructType`` or ``dict[str, str]``.

    Returns:
        A list of mismatch records with column name, expected type, actual type,
        and status.
    """
    expected_map = _schema_to_mapping(expected)
    actual_map = _schema_to_mapping(actual)
    all_columns = sorted(set(expected_map) | set(actual_map))

    mismatches: list[dict[str, Optional[str]]] = []
    for column_name in all_columns:
        expected_type = expected_map.get(column_name)
        actual_type = actual_map.get(column_name)
        if expected_type == actual_type:
            continue
        status = "missing_in_actual" if actual_type is None else "unexpected_in_actual" if expected_type is None else "type_mismatch"
        mismatches.append(
            {
                "column_name": column_name,
                "expected_type": expected_type,
                "actual_type": actual_type,
                "status": status,
            }
        )
    return mismatches


def _schema_to_mapping(schema: SchemaLike) -> dict[str, str]:
    """Convert a schema input into a column-to-type mapping."""
    if hasattr(schema, "fields"):
        return {field.name: field.dataType.simpleString() for field in schema.fields}  # type: ignore[union-attr]
    return {str(key): str(value) for key, value in schema.items()}
