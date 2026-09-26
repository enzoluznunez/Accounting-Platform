"""The cross-industry sheet and the column groups: the two halves of filtering
a sheet by row and by column.

Ranking the whole database by size and taking the top thirty returns twenty-one
manufacturers and nothing at all from six industries. These tests hold the rule
that replaced it — a few companies from each industry — because it is what keeps
filtering by industry from coming back empty."""


import pytest

import sheetcsv
from metrics import CATEGORIES, DIVISION_NAMES, RATIOS, SHEET_LIMIT, YEARS, division


def names_on(text):
    return [row[0] for row in sheetcsv.read(text)[2]]


def headers_on(text):
    return sheetcsv.read(text)[1][1:]


# A sheet labels rows with tickers, and a ticker means one company where six
# names in this data mean more than one. So the industry of a row follows from
# its label outright, and every row on the sheet counts towards the spread
# rather than the ambiguous ones being set aside.
@pytest.fixture(scope="module")
def industry_of(companies):
    return {ticker: division(sic)
            for ticker, sic in zip(companies.ticker, companies.sic_code)}


def spread_of(text, industry_of):
    """How many companies each industry put on the sheet."""
    counts = {}
    for ticker in names_on(text):
        industry = industry_of[ticker]
        counts[industry] = counts.get(industry, 0) + 1
    return counts


# --- rows: every industry is present, and an industry filter narrows to it ----

def test_the_combined_sheet_holds_every_industry(client, industry_of):
    present = set(spread_of(client.get("/sheet?per=3&limit=200").text, industry_of))
    assert present == set(DIVISION_NAMES)


def test_no_industry_dominates_the_combined_sheet(client, industry_of):
    """The failure the per-industry rule exists to avoid, stated as a test so it
    cannot quietly come back: ranked by size, the top thirty of this database
    are twenty-one manufacturers and nothing at all from six industries."""
    spread = spread_of(client.get("/sheet?per=3&limit=200").text, industry_of)
    assert len(spread) == 10
    assert max(spread.values()) <= 3


@pytest.mark.parametrize("per", [1, 2, 5])
def test_per_decides_how_many_each_industry_contributes(client, industry_of, per):
    spread = spread_of(client.get(f"/sheet?per={per}&limit=200").text, industry_of)
    assert max(spread.values()) <= per
    assert len(spread) == 10


def test_a_small_industry_contributes_all_it_has(client, industry_of):
    # Agriculture holds two companies, so asking for three from each gets two.
    spread = spread_of(client.get("/sheet?per=3&limit=200").text, industry_of)
    assert spread["Agriculture"] == 2


def test_filtering_to_an_industry_narrows_the_rows(client, industry_of):
    combined = set(names_on(client.get("/sheet?per=3&limit=200").text))
    filtered = names_on(client.get("/sheet?division=Mining&limit=200").text)
    assert filtered
    assert all(industry_of[ticker] == "Mining" for ticker in filtered)
    # The combined sheet showed three miners; the filtered one shows all of them.
    assert len(filtered) > len(combined & set(filtered))


# --- columns: a category names a group of ratios -----------------------------

@pytest.mark.parametrize("category", sorted(CATEGORIES))
def test_a_category_draws_exactly_its_ratios(client, category):
    text = client.get(f"/sheet?division=Mining&categories={category}&limit=3").text
    titles = {column.rsplit(" ", 1)[0] for column in headers_on(text)}
    assert titles == {ratio.replace("_", " ").title() for ratio in CATEGORIES[category]}


def test_categories_compose(client):
    both = headers_on(client.get("/sheet?division=Mining&categories=liquidity,valuation&limit=3").text)
    assert len(both) == (len(CATEGORIES["liquidity"]) + len(CATEGORIES["valuation"])) * len(YEARS)


def test_columns_keep_their_canonical_order(client):
    """However the columns were named, they come out in RATIOS order, so the
    same sheet asked for two ways looks the same. Categories given back to
    front do not put the sheet back to front."""
    text = client.get("/sheet?division=Mining&categories=valuation,liquidity&limit=3").text
    titles = list(dict.fromkeys(column.rsplit(" ", 1)[0] for column in headers_on(text)))
    canonical = [ratio.replace("_", " ").title() for ratio in RATIOS]
    assert titles == sorted(titles, key=canonical.index)
    assert titles[0] == "Working Capital"  # liquidity leads RATIOS, however it was asked for


def test_every_category_together_is_every_ratio(client):
    text = client.get(f"/sheet?division=Mining&categories={','.join(CATEGORIES)}&limit=3").text
    assert len(headers_on(text)) == len(RATIOS) * len(YEARS)


def test_an_unknown_category_points_at_the_grouping(client):
    detail = client.get("/sheet?division=Mining&categories=profit").json()["detail"]
    assert "profit" in detail and "ListRatios" in detail


def test_rows_and_columns_narrow_independently(client, industry_of):
    text = client.get("/sheet?division=Mining&categories=liquidity&limit=200").text
    assert all(industry_of[ticker] == "Mining" for ticker in names_on(text))
    assert len(headers_on(text)) == len(CATEGORIES["liquidity"]) * len(YEARS)
