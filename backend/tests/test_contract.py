"""The documents may grow descriptive fields freely; the metric surface may not.
These tests hold that line so a later projection cannot quietly widen it."""

import pytest

import database
import sheetcsv
from metrics import DEFAULT_METRICS, FUNDAMENTALS, RATIOS, division


def keys_under(section):
    """Every figure name stored under one section of any year, across all
    companies."""
    found = database.companies().aggregate([
        {"$unwind": "$years"},
        {"$project": {"pairs": {"$objectToArray": {"$ifNull": [f"$years.{section}", {}]}}}},
        {"$unwind": "$pairs"},
        {"$group": {"_id": "$pairs.k"}},
    ])
    return {row["_id"] for row in found}


def company_fields():
    """Every field a company document holds outside its years."""
    found = database.companies().aggregate([
        {"$project": {"pairs": {"$objectToArray": "$$ROOT"}}},
        {"$unwind": "$pairs"},
        {"$group": {"_id": "$pairs.k"}},
    ])
    return {row["_id"] for row in found} - {"years"}


# What a company document describes it with. Named here rather than read from
# the database, so the parametrised test below is decided before anything runs.
DESCRIPTIVE = ["name", "gvkey", "cik", "sic_code", "division", "address", "city", "country"]


def test_ratios_hold_exactly_the_metric_surface():
    assert keys_under("ratios") == set(RATIOS)


def test_fundamentals_hold_nothing_plottable():
    assert not keys_under("fundamentals") & set(RATIOS)


def test_the_two_surfaces_are_disjoint():
    # A ratio is what a sheet draws; a fundamental is what a request filters on.
    # Nothing may be both, or the metric surface widens through the back door.
    assert not set(FUNDAMENTALS) & set(RATIOS)
    assert keys_under("fundamentals") == set(FUNDAMENTALS)


@pytest.mark.parametrize("column", FUNDAMENTALS[:4] + ["sic_code"])
def test_no_filterable_field_is_accepted_as_a_metric(client, column):
    response = client.get(f"/sheet?sic=7370&metrics={column}")
    assert response.status_code == 422


def test_companies_hold_nothing_plottable():
    assert not company_fields() & set(RATIOS)


def test_division_always_agrees_with_the_sic_code():
    # Division is stored on each company rather than looked up, so it is a
    # copy of what metrics.division says of the SIC code, and has to stay one.
    pairs = database.companies().aggregate([
        {"$group": {"_id": {"sic_code": "$sic_code", "division": "$division"}}},
    ])
    for pair in pairs:
        assert pair["_id"]["division"] == division(pair["_id"]["sic_code"])


@pytest.mark.parametrize("column", DESCRIPTIVE)
def test_no_descriptive_column_is_accepted_as_a_metric(client, column):
    response = client.get(f"/sheet?sic=7370&metrics={column}")
    assert response.status_code == 422
    assert column in response.json()["detail"]


def test_sheet_header_is_company_plus_metric_year_pairs(client):
    header = sheetcsv.read(client.get("/sheet?sic=7370&limit=3").text)[1]
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
