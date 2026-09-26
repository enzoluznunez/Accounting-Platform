"""/sheet's 'where' narrows which companies reach a sheet. These tests hold that
it narrows by the right rule, that it never changes what the sheet holds, and
that every way of getting it wrong comes back as a sentence rather than a stack
trace: the assistant reads these out loud."""


import pytest

import sheetcsv
from metrics import FUNDAMENTALS, RATIOS

SIC = 7370  # 85 companies, the largest industry in the data


def sheet(client, query, expect=200):
    response = client.get(f"/sheet?sic={SIC}&limit=200&{query}")
    assert response.status_code == expect, response.text[:200]
    return response


def names(response):
    return [row[0] for row in sheetcsv.read(response.text)[2]]


def detail(client, query):
    response = client.get(f"/sheet?sic={SIC}&{query}")
    assert response.status_code == 422
    return response.json()["detail"]


def test_a_filter_narrows_the_sheet(client):
    everyone = names(sheet(client, "limit=200"))
    filtered = names(sheet(client, "where=revenues:gt:1000"))
    assert 0 < len(filtered) < len(everyone)
    assert set(filtered) <= set(everyone)


def test_a_filter_does_not_change_what_the_sheet_holds(client):
    """Filters choose rows. Columns come from 'metrics' and nothing else."""
    plain, plain_body = sheetcsv.split(sheet(client, "limit=200").text)
    narrowed, narrowed_body = sheetcsv.split(sheet(client, "where=revenues:gt:1000").text)
    assert plain["group"] == narrowed["group"] == ["2"]
    # The header, not the whole first line: '#industry' and '#color' are per-row
    # and so are expected to be shorter once rows have been filtered away.
    assert plain_body.splitlines()[0] == narrowed_body.splitlines()[0]


def test_predicates_compose(client):
    big = set(names(sheet(client, "where=revenues:gt:1000")))
    profitable = set(names(sheet(client, "where=net_income:gt:0")))
    both = set(names(sheet(client, "where=revenues:gt:1000,net_income:gt:0")))
    assert both == big & profitable
    assert both < big


def test_all_is_stricter_than_any(client):
    loose = set(names(sheet(client, "where=net_income:lt:0&match=any")))
    strict = set(names(sheet(client, "where=net_income:lt:0&match=all")))
    assert strict < loose


def test_every_row_really_satisfies_the_filter(client, fundamentals, companies):
    """The rows that came back, checked against the source rather than against
    another query."""
    returned = set(names(sheet(client, "where=revenues:gt:1000&match=all")))

    source = fundamentals[fundamentals.sic_code == SIC]
    qualifying = source.groupby("ticker").revenues.apply(lambda years: (years > 1000).all())
    assert returned == set(qualifying[qualifying].index)


def test_an_unreported_figure_satisfies_no_comparison(client, blank):
    """A NULL is unknown, not zero. A company missing the figure is left off
    either side of a comparison rather than assumed to pass or to fail."""
    everyone = set(names(sheet(client, "limit=200")))
    assert blank in everyone, "the seeded company should be on an unfiltered sheet"

    # Together these cover every real number, so a company with a revenue would
    # have to appear in one of them.
    assert blank not in set(names(sheet(client, "where=revenues:gte:0")))
    assert blank not in set(names(sheet(client, "where=revenues:lt:99999999")))


@pytest.mark.parametrize("op", ["eq", "ne", "lt", "lte", "gt", "gte"])
def test_every_operator_is_accepted(client, op):
    response = client.get(f"/sheet?sic={SIC}&limit=200&where=revenues:{op}:1000")
    assert response.status_code in (200, 404)


def test_repeated_and_comma_separated_agree(client):
    joined = names(sheet(client, "where=revenues:gt:1000,net_income:gt:0"))
    repeated = names(sheet(client, "where=revenues:gt:1000&where=net_income:gt:0"))
    assert joined == repeated


def test_an_unknown_field_names_itself_and_points_at_the_catalog(client):
    message = detail(client, "where=revenue:gt:1000")
    assert "revenue" in message and "ListFields" in message


def test_a_ratio_is_not_filterable(client):
    # The two surfaces are disjoint on purpose: a sheet plots ratios, a request
    # filters on fundamentals, and neither list leaks into the other.
    message = detail(client, "where=current_ratio:gt:2")
    assert "current_ratio" in message


def test_an_unknown_operator_lists_the_ones_that_exist(client):
    message = detail(client, "where=revenues:above:1000")
    assert "above" in message
    assert all(op in message for op in ("eq", "lt", "gte"))


def test_a_value_that_is_not_a_number_says_so(client):
    assert "lots" in detail(client, "where=revenues:gt:lots")


def test_a_malformed_filter_shows_the_shape_it_wanted(client):
    message = detail(client, "where=revenues:gt")
    assert "field:op:value" in message


def test_no_matches_is_a_404_that_repeats_the_filter(client):
    response = client.get(f"/sheet?sic={SIC}&where=revenues:gt:99999999")
    assert response.status_code == 404
    message = response.json()["detail"]
    assert "revenues" in message and str(SIC) in message


def test_fields_publishes_the_unit_and_the_range(client):
    catalog = client.get("/fields").json()
    assert [f["name"] for f in catalog["fields"]] == FUNDAMENTALS
    assert not set(FUNDAMENTALS) & set(RATIOS)

    by_name = {f["name"]: f for f in catalog["fields"]}
    # The trap this exists to close: revenue is reported in millions, so a
    # caller asking for "over a billion dollars" must send 1000, not 1e9.
    assert by_name["revenues"]["unit"] == "millions"
    assert by_name["revenues"]["maximum"] < 1e9
    assert by_name["price_close_annual"]["unit"] == "usd_per_share"
    assert "millions" in catalog["note"]

    for field in catalog["fields"]:
        assert field["minimum"] <= field["maximum"]
