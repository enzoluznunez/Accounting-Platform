"""Colour is the sheet's fourth channel: rows are companies, columns are metrics,
height is the value, and the colour of a company's bars says which industry it
belongs to. These tests hold the two things that makes true — every row carries
its industry, and the colour is a function of that industry alone, so an industry
looks the same wherever it appears and no filter or reordering repaints it."""


import pytest

import sheetcsv
from metrics import DIVISION_COLORS, DIVISION_NAMES


@pytest.fixture(scope="module")
def combined(client):
    return sheetcsv.read(client.get("/sheet?limit=200").text)


@pytest.fixture(scope="module")
def mining(client):
    return sheetcsv.read(client.get("/sheet?division=Mining&limit=200").text)


def test_the_palette_covers_every_division():
    assert list(DIVISION_COLORS) == DIVISION_NAMES
    assert len(set(DIVISION_COLORS.values())) == len(DIVISION_NAMES)


def test_every_row_carries_an_industry_and_a_colour(combined):
    directives, _, rows = combined
    assert len(directives["industry"]) == len(rows)
    assert len(directives["color"]) == len(rows)


def test_industries_are_divisions(combined):
    directives, _, _ = combined
    assert set(directives["industry"]) <= set(DIVISION_NAMES)


def test_a_rows_colour_is_its_industrys_colour(combined):
    """The whole encoding in one line: colour is a function of the industry and
    of nothing else, so two rows of the same industry cannot differ."""
    directives, _, _ = combined
    for industry, hex_ in zip(directives["industry"], directives["color"]):
        assert hex_ == DIVISION_COLORS[industry]


def test_industries_are_not_grouped_by_the_row_order(combined):
    """The premise of the encoding: rows are in company-name order, so an industry
    is scattered down the sheet and colour is the only thing that gathers it."""
    directives, _, _ = combined
    industries = directives["industry"]
    runs = sum(1 for i in range(1, len(industries)) if industries[i] != industries[i - 1]) + 1
    assert runs > len(set(industries)), "rows are blocked by industry, not interleaved"


def test_one_industrys_sheet_is_one_colour(mining):
    directives, _, rows = mining
    assert set(directives["industry"]) == {"Mining"}
    assert set(directives["color"]) == {DIVISION_COLORS["Mining"]}
    assert len(rows) > 1


def test_an_industry_keeps_its_colour_across_sheets(combined, mining):
    """Colour follows the entity, never its rank: Mining is the same colour on its
    own sheet as it is among ten industries, so opening a different dataset does
    not repaint what the user just learned."""
    directives, _, _ = combined
    alone = mining[0]
    on_combined = {
        hex_ for industry, hex_ in zip(directives["industry"], directives["color"])
        if industry == "Mining"
    }
    assert on_combined == set(alone["color"])


def test_filtering_does_not_repaint_the_survivors(client, combined):
    """A filter drops rows. The rows that remain keep the colour they had, because
    the colour was never a function of how many rows there were."""
    directives, _, rows = combined
    before = dict(zip((row[0] for row in rows), directives["color"]))

    narrowed, _, narrowed_rows = sheetcsv.read(client.get("/sheet?limit=200&where=revenues:gt:1000").text)
    after = dict(zip((row[0] for row in narrowed_rows), narrowed["color"]))

    assert after, "the filter left nothing to compare"
    assert set(after) <= set(before)
    for company, hex_ in after.items():
        assert hex_ == before[company]


def test_a_sic_slice_is_coloured_by_its_division(client):
    """A SIC code is a slice inside one division, so its sheet takes that
    division's colour rather than a colour of its own."""
    directives, _, _ = sheetcsv.read(client.get("/sheet?sic=7370&limit=200").text)
    assert set(directives["color"]) == {DIVISION_COLORS["Services"]}
