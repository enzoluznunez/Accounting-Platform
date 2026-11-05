import pytest

from metrics import CATEGORIES, RATIOS


def row_for(frame, ticker, year):
    match = frame[(frame["ticker"] == ticker) & (frame["year"] == year)]
    assert len(match) == 1, f"expected one row for {ticker} {year}, got {len(match)}"
    return match.iloc[0]


@pytest.fixture(scope="module")
def source_a(clean):
    return row_for(clean, "A", 2019)


@pytest.fixture(scope="module")
def derived_a(ratios):
    return row_for(ratios, "A", 2019)


def test_every_ratio_column_present(ratios):
    assert list(ratios.columns) == ["ticker", "year", "sic_code", *RATIOS]


def test_row_and_company_counts(ratios):
    assert len(ratios) == 2498
    assert ratios["ticker"].nunique() == 1249


def test_every_company_has_both_years(ratios):
    years = ratios.groupby("ticker")["year"].agg(set)
    assert all(y == {2019, 2020} for y in years)


def test_current_ratio_matches_hand_calculation(source_a, derived_a):
    expected = source_a["assets_current"] / source_a["liabilities_current"]
    assert derived_a["current_ratio"] == pytest.approx(expected)
    assert derived_a["current_ratio"] == pytest.approx(1.5332, abs=1e-4)


def test_return_on_equity_uses_average_equity(source_a, derived_a):
    average_equity = (source_a["stockholders_equity"] + source_a["stockholders_equity_prior_year"]) / 2
    expected = source_a["net_income_loss_available_to_common_stockholders_basic"] / average_equity
    assert derived_a["return_on_equity"] == pytest.approx(expected)
    assert derived_a["return_on_equity"] == pytest.approx(0.2300, abs=1e-4)


def test_working_capital_is_a_difference_not_a_ratio(source_a, derived_a):
    assert derived_a["working_capital"] == pytest.approx(
        source_a["assets_current"] - source_a["liabilities_current"]
    )


def test_zero_inventory_produces_null_turnover(merged):
    zero_inventory = merged[(merged["inventory_net"] == 0) & (merged["inventory_net_prior_year"] == 0)]
    assert len(zero_inventory) > 0
    assert zero_inventory["inventory_turnover"].isna().all()


def test_zero_interest_produces_null_coverage(merged):
    debt_free = merged[merged["interest_expense"] == 0]
    assert len(debt_free) > 0
    assert debt_free["number_of_times_interest_is_earned"].isna().all()


def test_non_paying_companies_have_zero_yield_not_null(merged):
    non_payers = merged[merged["payments_of_dividends_common_stock"] == 0]
    assert len(non_payers) > 0
    assert (non_payers["dividend_yield"] == 0).all()


def test_days_ratios_invert_their_turnovers(ratios):
    sample = ratios.dropna(subset=["inventory_turnover"]).iloc[0]
    assert sample["average_days_to_collect_inventory"] == pytest.approx(
        365.0 / sample["inventory_turnover"]
    )


def test_every_ratio_has_exactly_one_category():
    placed = [ratio for members in CATEGORIES.values() for ratio in members]
    assert sorted(placed) == sorted(RATIOS)
    assert len(placed) == len(set(placed)) == 18


def test_no_category_is_empty():
    assert all(members for members in CATEGORIES.values())
