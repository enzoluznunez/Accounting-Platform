"""Compute the ratios from the staging table and load them into the model.

Nothing round-trips through a file: clean.py puts the export in staging, this
reads it, and what it computes goes straight into industries, companies,
financials and fundamentals. schema.sql builds those tables; this fills them.

    python clean.py && psql -d nasba -f schema.sql && python ratios.py
"""

import numpy as np
import pandas as pd
import psycopg

from clean import DSN, column
from metrics import FUNDAMENTALS, RATIOS, division

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


def load(conn, table, frame):
    """Replace a table's contents with a frame, NaN written as NULL. COPY routes
    rows to the right partition on its own, so financials and fundamentals load
    the same way the unpartitioned tables do."""
    columns = list(frame.columns)
    blanked = frame.astype(object).where(pd.notna(frame), None)
    with conn.cursor().copy(f"COPY {table} ({', '.join(columns)}) FROM STDIN") as copy:
        for row in blanked.itertuples(index=False, name=None):
            copy.write_row(row)
    return len(frame)


def safe(numerator, denominator):
    return numerator / denominator.replace(0, np.nan)


def average(current, prior):
    return (current + prior) / 2


with psycopg.connect(DSN) as source:
    rows = source.execute("SELECT * FROM staging")
    df = pd.DataFrame(rows.fetchall(), columns=[c.name for c in rows.description])


def src(heading):
    """A staging column by the heading it had in the export, so the formulas
    below still read the way the source data is labelled."""
    return df[column(heading)]

# Everything descriptive about a company lives here and nowhere else. The
# metric surface is metrics.RATIOS alone, so widening this table adds joinable
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

# division is a function of sic_code, so it is its own table rather than a
# value repeated once per company.
industries = pd.DataFrame({"sic_code": sorted(src("SIC Code").unique())})
industries["division"] = industries["sic_code"].map(division)

# sic_code rides along on the per-year tables as the partition key. It is a
# copy of what companies already holds, which schema.sql pins with a composite
# foreign key so the two can never disagree: physical layout, not a second
# source of truth.
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

# Column order is each table's, so COPY can take the frames as they are.
out = out[["ticker", "year", "sic_code", *RATIOS]]
fundamentals = fundamentals[["ticker", "year", "sic_code", *FUNDAMENTALS]]

# Parents before children: financials and fundamentals point a composite key at
# companies, which points at industries.
with psycopg.connect(DSN) as conn:
    for table in ("fundamentals", "financials", "companies", "industries"):
        conn.execute(f"DELETE FROM {table}")
    written = {
        "industries": load(conn, "industries", industries),
        "companies": load(conn, "companies", companies),
        "financials": load(conn, "financials", out),
        "fundamentals": load(conn, "fundamentals", fundamentals),
    }
    for table in written:
        conn.execute(f"ANALYZE {table}")

print(", ".join(f"{table}: {count}" for table, count in written.items()))
print(f"ratios: {len(RATIOS)}, line items: {len(FUNDAMENTALS)}")
print(out[RATIOS].notna().sum().sort_values().head(5))
