"""Compute the ratios from the cleaned export.

compute() takes clean.usable()'s staging frame and returns the three frames the
model is made of: companies, and per company-year its financials (the ratios)
and fundamentals (the reported line items). Nothing
is read from or written to anywhere here; rebuild.py publishes the result.
"""

import numpy as np
import pandas as pd

from clean import column
from metrics import FUNDAMENTALS, RATIOS

# Where each reported line item comes from. metrics.FUNDAMENTALS owns the names
# the database and the API use; this owns the mapping back to the export's own
# headings, which is an ingestion detail and changes with the export, not the
# contract. The same split RATIOS and the formulas below already have.
SOURCE_COLUMNS = {
    "assets": "Assets",
    "assets_current": "Assets, Current",
    "cash_and_equivalents": "Cash and Cash Equivalents, at Carrying Value",
    "inventory": "Inventory, Net",
    "marketable_securities_current": "Marketable Securities, Current",
    "receivables_net_current": "Receivables, Net, Current",
    "accounts_receivable_gross_current": "Accounts Receivable, Gross, Current",
    "allowance_for_doubtful_accounts": "Allowance for Doubtful Accounts Receivable",
    "property_plant_equipment_net": "Property, Plant and Equipment, Net",
    "liabilities": "Liabilities",
    "liabilities_current": "Liabilities, Current",
    "total_debt": "Total Debt Including Current",
    "stockholders_equity": "Stockholders' Equity",
    "preferred_stock_value": "Preferred Stock, Value",
    "revenues": "Revenues",
    "cost_of_goods_sold": "Cost of Goods and Services Sold",
    "earnings_before_interest_and_taxes": "Earnings Before Interest and Taxes",
    "net_income": "Net Income (Loss)",
    "net_income_available_to_common": "Net Income (Loss) Available to Common Stockholders, Basic",
    "interest_expense": "Interest Expense",
    "dividends_paid_common": "Payments of Dividends, Common Stock",
    "preferred_stock_dividends": "Preferred Stock Dividends, Income Statement Impact",
    "common_shares_outstanding": "Common Stock, Shares Outstanding",
    "price_close_annual": "Price Close - Annual -",
}


def safe(numerator, denominator):
    return numerator / denominator.replace(0, np.nan)


def average(current, prior):
    return (current + prior) / 2


def compute(staging):
    df = staging

    def src(heading):
        """A staging column by the heading it had in the export, so the formulas
        below still read the way the source data is labelled."""
        return df[column(heading)]

    # Everything descriptive about a company lives here and nowhere else. The
    # metric surface is metrics.RATIOS alone, so widening this adds descriptive
    # detail without adding anything a sheet can plot.
    companies = pd.DataFrame({
        "ticker": src("Trading Symbol"),
        "name": src("Entity Registrant Name"),
        "gvkey": src("GVKEY"),
        "cik": src("Entity Central Index Key"),
        "sic_code": src("SIC Code"),
        "address_line1": src("Entity Address, Address Line One"),
        "postal_code": src("Entity Address, Postal Zip Code"),
        "city": src("Entity Address, City or Town"),
        "country": src("Entity Address, Country"),
    }).drop_duplicates("ticker")

    keys = pd.DataFrame({
        "ticker": src("Trading Symbol"),
        "year": src("Year"),
        "sic_code": src("SIC Code"),
    })

    out = keys.copy()

    current_assets = src("Assets, Current")
    current_liabilities = src("Liabilities, Current")
    inventory = src("Inventory, Net")
    assets = src("Assets")
    equity = src("Stockholders' Equity")
    revenues = src("Revenues")
    cogs = src("Cost of Goods and Services Sold")
    ebit = src("Earnings Before Interest and Taxes")
    net_income = src("Net Income (Loss)")
    common_income = src("Net Income (Loss) Available to Common Stockholders, Basic")
    debt = src("Total Debt Including Current")
    shares = src("Common Stock, Shares Outstanding")
    price = src("Price Close - Annual -")
    eps = src("Earnings Per Share, Basic")

    avg_receivables = average(src("Receivables, Net, Current"), src("Receivables, Net, Current (Prior Year)"))
    avg_inventory = average(inventory, src("Inventory, Net (Prior Year)"))
    avg_assets = average(assets, src("Assets (Prior Year)"))
    avg_equity = average(equity, src("Stockholders' Equity (Prior Year)"))

    out["working_capital"] = current_assets - current_liabilities
    out["current_ratio"] = safe(current_assets, current_liabilities)
    out["quick_ratio"] = safe(current_assets - inventory, current_liabilities)
    out["accounts_receivable_turnover"] = safe(revenues, avg_receivables)
    out["average_days_to_collect_receivables"] = safe(365.0, out["accounts_receivable_turnover"])
    out["inventory_turnover"] = safe(cogs, avg_inventory)
    out["average_days_to_collect_inventory"] = safe(365.0, out["inventory_turnover"])
    out["debt_to_assets"] = safe(debt, assets)
    out["debt_to_equity"] = safe(debt, equity)
    out["number_of_times_interest_is_earned"] = safe(ebit, src("Interest Expense"))
    out["net_margin"] = safe(net_income, revenues)
    out["asset_turnover_ratio"] = safe(revenues, avg_assets)
    out["return_on_investment"] = safe(net_income, avg_assets)
    out["return_on_equity"] = safe(common_income, avg_equity)
    out["earnings_per_share"] = eps
    out["book_value_per_share"] = safe(equity - src("Preferred Stock, Value"), shares)
    out["price_earnings_ratio"] = safe(price, eps)
    out["dividend_yield"] = safe(src("Payments of Dividends, Common Stock"), shares * price)

    # The reported line items the ratios were computed from, carried through
    # unchanged. They are what a filter can name; only RATIOS can be plotted.
    fundamentals = keys.copy()
    for name in FUNDAMENTALS:
        fundamentals[name] = src(SOURCE_COLUMNS[name])

    # Ratios in RATIOS order and line items in FUNDAMENTALS order, which is the
    # order a document lists them in.
    out = out[["ticker", "year", "sic_code", *RATIOS]]
    fundamentals = fundamentals[["ticker", "year", "sic_code", *FUNDAMENTALS]]

    return {"companies": companies, "financials": out, "fundamentals": fundamentals}
