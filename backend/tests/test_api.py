
import pandas as pd
import pytest

import sheetcsv
from metrics import DIVISION_NAMES, RATIOS, SIZE_METRIC, YEARS, division


@pytest.fixture(scope="module")
def default_sheet(client):
    return client.get("/sheet?sic=7370&limit=5").text


def test_health_reports_loaded_rows(client):
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["financial_rows"] == 2498


def test_ratios_endpoint_lists_them_all(client):
    body = client.get("/ratios").json()
    assert body["ratios"] == RATIOS
    assert set(body["default"]).issubset(set(body["ratios"]))


def test_industries_respects_minimum(client):
    body = client.get("/industries?minimum=25").json()
    assert all(row["companies"] >= 25 for row in body["industries"])
    assert body["industries"][0]["division"] == "Manufacturing"


def test_industries_lists_one_row_per_division(client):
    """An industry means a division here, the same ten the app lists as datasets
    and the same ten financials is partitioned by. Listing them by
    SIC code instead handed the assistant four rows all called Manufacturing."""
    rows = client.get("/industries").json()["industries"]
    divisions = [row["division"] for row in rows]
    assert sorted(divisions) == sorted(DIVISION_NAMES)
    assert len(divisions) == len(set(divisions)) == 10
    assert sum(row["companies"] for row in rows) == 1249


def test_a_sheet_can_be_opened_by_division(client):
    text = client.get("/sheet?division=Mining&limit=200").text
    assert text.startswith("#group 2")
    assert len(sheetcsv.read(text)[2]) == 72


def test_a_division_holds_its_sic_codes(client, companies):
    """A SIC code is a slice inside a division: every company on the slice's
    sheet is one of that division's companies. Checked against the source
    rather than against the division's sheet, which the row cap would truncate.
    """
    slice_ = set(names_on(client.get("/sheet?sic=7370&limit=200").text))
    services = set(companies[companies.sic_code.map(division) == "Services"].ticker)
    assert slice_ and slice_ <= services


def test_a_sheet_takes_at_most_one_scope(client):
    response = client.get("/sheet?sic=7370&division=Mining")
    assert response.status_code == 422
    assert "one industry" in response.json()["detail"]


def test_naming_no_industry_gives_every_industry(client):
    """The view the app opens with: a few companies from each industry rather
    than the largest companies overall, which would be a leaderboard."""
    rows = names_on(client.get("/sheet?limit=200").text)
    assert rows
    assert len(rows) <= 200


def test_sheet_starts_with_group_directive(default_sheet):
    assert sheetcsv.read(default_sheet)[0]["group"] == ["2"]


def test_sheet_column_count_is_metrics_times_years(client):
    text = client.get("/sheet?sic=7370&metrics=current_ratio,net_margin&years=2019,2020&limit=5").text
    header = sheetcsv.read(text)[1]
    assert header[0] == "Company"
    assert len(header) == 1 + 2 * 2


def test_sheet_column_count_divides_by_group_size(default_sheet):
    directives, header, _ = sheetcsv.read(default_sheet)
    metric_columns = len(header) - 1
    assert metric_columns % int(directives["group"][0]) == 0


def test_sheet_respects_limit(client):
    text = client.get("/sheet?sic=7370&limit=3").text
    assert len(sheetcsv.read(text)[2]) == 3


def test_sheet_rejects_unknown_metric(client):
    response = client.get("/sheet?sic=7370&metrics=bogus_ratio")
    assert response.status_code == 422
    assert "bogus_ratio" in response.json()["detail"]


def test_sheet_rejects_malformed_years(client):
    response = client.get("/sheet?sic=7370&years=twenty-nineteen")
    assert response.status_code == 422
    assert "twenty-nineteen" in response.json()["detail"]


def test_validation_detail_is_a_string_the_assistant_can_read(client):
    # The Unity client reads 'detail' as a string; Pydantic's own shape is a list.
    detail = client.get("/sheet?sic=7370&limit=500").json()["detail"]
    assert isinstance(detail, str)
    assert "200" in detail


def test_sheet_rejects_out_of_range_limit(client):
    assert client.get("/sheet?sic=7370&limit=0").status_code == 422
    assert client.get("/sheet?sic=7370&limit=500").status_code == 422


def test_sheet_returns_404_for_empty_industry(client):
    assert client.get("/sheet?sic=9999").status_code == 404


def test_covid_collapse_is_visible_in_travel(client):
    text = client.get("/sheet?sic=7370&metrics=net_margin&years=2019,2020&limit=30").text
    rows = sheetcsv.read(text)[2]
    booking = [r for r in rows if r[0].upper() == "BKNG"]
    assert booking, "expected Booking Holdings in computer services"
    before, after = float(booking[0][1]), float(booking[0][2])
    assert after < before / 10


def test_pivot_cell_matches_the_source_row(client, companies, ratios):
    # The round trip the shape assertions never make: one cell in the wide sheet,
    # back to the long-form row it came from.
    text = client.get("/sheet?sic=7370&metrics=net_margin,current_ratio&years=2019,2020&limit=10").text
    _, header, rows = sheetcsv.read(text)

    long_form = ratios.set_index(["ticker", "year"])

    checked = 0
    for row in rows:
        ticker = row[0]
        for column, cell in zip(header[1:], row[1:]):
            metric = column.rsplit(" ", 1)[0].lower().replace(" ", "_")
            year = int(column.rsplit(" ", 1)[1])
            expected = long_form.loc[(ticker, year), metric]
            if cell == "":
                assert pd.isna(expected)
            else:
                assert float(cell) == pytest.approx(expected, abs=1e-4)
            checked += 1
    assert checked == len(rows) * 4


def test_single_year_gives_one_column_per_metric(client):
    text = client.get("/sheet?sic=7370&years=2019&metrics=current_ratio,net_margin&limit=5").text
    directives, header, _ = sheetcsv.read(text)
    assert directives["group"] == ["1"]
    assert header == ["Company", "Current Ratio 2019", "Net Margin 2019"]


def test_every_metric_can_be_requested_at_once(client):
    text = client.get(f"/sheet?sic=7370&metrics={','.join(RATIOS)}&years=2019,2020&limit=5").text
    header = sheetcsv.read(text)[1]
    assert len(header) == 1 + len(RATIOS) * 2


def test_years_are_sorted_and_deduplicated(client):
    text = client.get("/sheet?sic=7370&years=2020,2019,2020&metrics=net_margin&limit=3").text
    directives, header, _ = sheetcsv.read(text)
    assert directives["group"] == ["2"]
    assert header[1:] == ["Net Margin 2019", "Net Margin 2020"]


def test_companies_sharing_a_name_both_appear(client, twin):
    # Two companies share one name, and each reaches the sheet under its own
    # ticker. Keying the rows by name dropped one of them; labelling them by
    # ticker is also what tells the pair apart once they are both on the sheet.
    on_sheet = names_on(client.get("/sheet?sic=7370&limit=200").text)
    assert twin <= set(on_sheet)


def names_on(text):
    return [row[0] for row in sheetcsv.read(text)[2]]


def sizes_on(text):
    """Each row's working capital, the figure the sheet's cut is taken on."""
    _, header, rows = sheetcsv.read(text)
    title = sheetcsv.title(SIZE_METRIC, YEARS[0]).rsplit(" ", 1)[0]
    at = [i for i, column in enumerate(header) if column.rsplit(" ", 1)[0] == title]
    assert at, f"no {title} column"

    sizes = {}
    for row in rows:
        values = [float(row[i]) for i in at if row[i] != ""]
        sizes[row[0]] = max(values) if values else None
    return sizes


def test_the_sheet_is_cut_by_size(client):
    """Size decides which companies reach a sheet. It no longer decides what order
    they stand in, so this reads the cut rather than the ordering: the five on a
    limit of five are the five largest of the whole industry."""
    whole = sizes_on(client.get(f"/sheet?division=Mining&metrics={SIZE_METRIC}&limit=200").text)
    cut = sizes_on(client.get(f"/sheet?division=Mining&metrics={SIZE_METRIC}&limit=5").text)

    ranked = sorted((name for name, size in whole.items() if size is not None),
                    key=lambda name: whole[name], reverse=True)
    assert set(cut) == set(ranked[:5])


def test_rows_stand_in_ticker_order(client, name_order):
    """The row axis is the company axis: rows are ordered by the ticker they are
    labelled with and by nothing else, so industries interleave and colour is
    what tells them apart. The order to expect comes from the database, which is
    what the sheet is built with; sorted() would be comparing a different
    ordering than the one under test."""
    names = names_on(client.get("/sheet?limit=200").text)
    assert names

    place = {}
    for at, name in enumerate(name_order):
        place.setdefault(name, at)
    standing = [place[name] for name in names]
    assert standing == sorted(standing)


def test_row_titles_are_tickers(client, companies):
    """The first cell of every row is what the app labels that row with. It is
    the ticker: short enough that twenty of them stand side by side on the sheet
    without running into one another, and unique where a name need not be."""
    tickers = set(companies.ticker)
    on_sheet = names_on(client.get("/sheet?limit=200").text)
    assert on_sheet
    assert set(on_sheet) <= tickers
