"""financials and fundamentals are cut into one partition per division. These
tests hold the three things that makes true: the cuts are where metrics.DIVISIONS
says, every row is under the cut its SIC code implies, and the query the app runs
reaches one partition instead of ten."""

import re

import psycopg
import pytest

import api
from api import SheetQuery
from metrics import DIVISION_NAMES, division, division_bounds, slug

PARTITIONED = ["financials", "fundamentals"]


def partitions(table):
    """{partition name: its declared bound}, straight from the catalog."""
    with psycopg.connect(api.DSN) as conn:
        rows = conn.execute(
            "SELECT c.relname, pg_get_expr(c.relpartbound, c.oid)"
            " FROM pg_class c JOIN pg_inherits i ON c.oid = i.inhrelid"
            " WHERE i.inhparent = %s::regclass",
            (table,),
        ).fetchall()
    return dict(rows)


def bound(lower, upper):
    low = "MINVALUE" if lower is None else str(lower)
    high = "MAXVALUE" if upper is None else str(upper)
    return f"FOR VALUES FROM ({low}) TO ({high})"


@pytest.mark.parametrize("table", PARTITIONED)
def test_one_partition_per_division(table):
    assert len(partitions(table)) == len(DIVISION_NAMES) == 10


@pytest.mark.parametrize("table", PARTITIONED)
def test_partition_bounds_match_the_division_table(table):
    # schema.sql writes these out by hand. This is what stops them drifting
    # from metrics.DIVISIONS, which is what ratios.py labels companies with.
    live = partitions(table)
    expected = {
        f"{table}_{slug(name)}": bound(lower, upper)
        for name, lower, upper in division_bounds()
    }
    assert live == expected


@pytest.mark.parametrize("table", PARTITIONED)
def test_every_row_is_under_the_cut_its_sic_implies(table):
    with psycopg.connect(api.DSN) as conn:
        rows = conn.execute(
            f"SELECT tableoid::regclass::text, sic_code FROM {table}"
        ).fetchall()
    assert rows
    for partition, sic in rows:
        assert partition == f"{table}_{slug(division(sic))}"


@pytest.mark.parametrize("table", PARTITIONED)
def test_partitioning_preserved_every_row(table):
    with psycopg.connect(api.DSN) as conn:
        total = conn.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
    assert total == 2498


def touched(sql, params, table="financials"):
    """The partitions of `table` a plan actually reads. Matched against the
    catalog, so an index or an alias that happens to share the prefix is not
    mistaken for a partition."""
    known = set(partitions(table))
    with psycopg.connect(api.DSN) as conn:
        plan = "\n".join(line for (line,) in conn.execute("EXPLAIN " + sql, params))
    return {name for name in re.findall(r"\w+", plan) if name in known}


def test_the_sheet_query_prunes_to_one_partition():
    # The query the endpoint runs, not a lookalike: sheet_sql is what /sheet calls.
    sql, params = api.sheet_sql(SheetQuery(sic=7370))
    assert touched(sql, params) == {"financials_services"}


def test_a_filtered_sheet_query_still_prunes():
    # Both per-year tables carry the key, so a filtered sheet prunes on each of
    # them: the filter reads one division's line items, not all ten.
    sql, params = api.sheet_sql(SheetQuery(sic=2836, where=["revenues:gt:1000"]))
    assert touched(sql, params) == {"financials_manufacturing"}
    assert touched(sql, params, "fundamentals") == {"fundamentals_manufacturing"}


def test_without_the_partition_key_every_partition_is_read():
    # The contrast that makes the pruning above mean something.
    reached = touched("SELECT ticker FROM financials WHERE year = %s", [2020])
    assert len(reached) == 10


def test_the_partition_key_cannot_disagree_with_companies():
    """sic_code is copied onto financials so Postgres can partition on it. The
    composite foreign key is what keeps that copy from becoming a second,
    disagreeing source of truth."""
    with psycopg.connect(api.DSN) as conn:
        ticker = conn.execute(
            "SELECT ticker FROM companies WHERE sic_code = 7370 ORDER BY ticker LIMIT 1"
        ).fetchone()[0]
        with pytest.raises(psycopg.errors.ForeignKeyViolation):
            conn.execute(
                "INSERT INTO financials (ticker, year, sic_code) VALUES (%s, 2021, 9999)",
                (ticker,),
            )
        conn.rollback()
