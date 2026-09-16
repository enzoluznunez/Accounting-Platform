"""The schema may grow descriptive columns freely; the metric surface may not.
These tests hold that line so a later SELECT cannot quietly widen it."""

import psycopg
import pytest

import api
from metrics import DEFAULT_METRICS, FUNDAMENTALS, RATIOS


def columns_of(table):
    with psycopg.connect(api.DSN) as conn:
        rows = conn.execute(
            "SELECT column_name FROM information_schema.columns"
            " WHERE table_name = %s ORDER BY ordinal_position",
            (table,),
        ).fetchall()
    return [row[0] for row in rows]


# The partition key is the one column on financials that is neither a key of
# the row nor a metric. It is admitted here by name so that anything else added
# to the table still fails this test.
KEYS = ["ticker", "year", "sic_code"]


def test_financials_holds_exactly_the_metric_surface():
    assert columns_of("financials") == [*KEYS, *RATIOS]


def test_fundamentals_holds_nothing_plottable():
    assert not set(columns_of("fundamentals")) & set(RATIOS)


def test_the_two_surfaces_are_disjoint():
    # A ratio is what a sheet draws; a fundamental is what a request filters on.
    # Nothing may be both, or the metric surface widens through the back door.
    assert not set(FUNDAMENTALS) & set(RATIOS)
    assert columns_of("fundamentals") == [*KEYS, *FUNDAMENTALS]


@pytest.mark.parametrize("column", FUNDAMENTALS[:4] + ["sic_code"])
def test_no_filterable_field_is_accepted_as_a_metric(client, column):
    response = client.get(f"/sheet?sic=7370&metrics={column}")
    assert response.status_code == 422


def test_companies_holds_nothing_plottable():
    assert not set(columns_of("companies")) & set(RATIOS)


def test_division_is_not_repeated_on_companies():
    # 3NF: division depends on sic_code, so it lives on industries alone.
    assert "division" not in columns_of("companies")
    assert columns_of("industries") == ["sic_code", "division"]


def test_each_sic_code_has_one_division():
    with psycopg.connect(api.DSN) as conn:
        offenders = conn.execute(
            "SELECT count(*) FROM (SELECT sic_code FROM industries"
            " GROUP BY sic_code HAVING count(DISTINCT division) > 1) x"
        ).fetchone()[0]
    assert offenders == 0


@pytest.mark.parametrize("column", [c for c in columns_of("companies") if c != "ticker"])
def test_no_descriptive_column_is_accepted_as_a_metric(client, column):
    response = client.get(f"/sheet?sic=7370&metrics={column}")
    assert response.status_code == 422
    assert column in response.json()["detail"]


def test_sheet_header_is_company_plus_metric_year_pairs(client):
    header = client.get("/sheet?sic=7370&limit=3").text.splitlines()[1].split(",")
    assert header[0] == "Company"
    titles = {metric.replace("_", " ").title() for metric in RATIOS}
    for column in header[1:]:
        metric, year = column.rsplit(" ", 1)
        assert metric in titles
        assert year in {"2019", "2020"}


def test_openapi_publishes_the_metric_enum(client):
    schema = client.get("/openapi.json").json()
    text = str(schema)
    assert all(ratio in text for ratio in RATIOS)
    for endpoint in ["/health", "/ratios", "/industries"]:
        responses = schema["paths"][endpoint]["get"]["responses"]["200"]
        assert "$ref" in str(responses), f"{endpoint} has no typed response schema"


def test_defaults_are_part_of_the_surface():
    assert set(DEFAULT_METRICS) <= set(RATIOS)
