"""The one list of ratios: ratios.py computes them in this order, each year of a
company document stores them in this order, and the API serves them by these names."""

RATIOS = [
    "working_capital",
    "current_ratio",
    "quick_ratio",
    "accounts_receivable_turnover",
    "average_days_to_collect_receivables",
    "inventory_turnover",
    "average_days_to_collect_inventory",
    "debt_to_assets",
    "debt_to_equity",
    "number_of_times_interest_is_earned",
    "net_margin",
    "asset_turnover_ratio",
    "return_on_investment",
    "return_on_equity",
    "earnings_per_share",
    "book_value_per_share",
    "price_earnings_ratio",
    "dividend_yield",
]

# What kind of question each ratio answers. Every ratio belongs to exactly one
# category and every category holds at least one, which test_ratios.py checks:
# the app groups the metric list by these, and eighteen flat names is a worse
# panel than five named groups.
CATEGORIES = {
    "liquidity": [
        "working_capital",
        "current_ratio",
        "quick_ratio",
    ],
    "efficiency": [
        "accounts_receivable_turnover",
        "average_days_to_collect_receivables",
        "inventory_turnover",
        "average_days_to_collect_inventory",
        "asset_turnover_ratio",
    ],
    "solvency": [
        "debt_to_assets",
        "debt_to_equity",
        "number_of_times_interest_is_earned",
    ],
    "profitability": [
        "net_margin",
        "return_on_investment",
        "return_on_equity",
        "earnings_per_share",
    ],
    "valuation": [
        "book_value_per_share",
        "price_earnings_ratio",
        "dividend_yield",
    ],
}


def category_of(ratio):
    for name, members in CATEGORIES.items():
        if ratio in members:
            return name
    return None


# The reported line items behind the ratios. These are filterable and joinable,
# never plottable: a sheet's columns come from RATIOS alone, so this list can
# grow without widening what the app can draw. 'earnings_per_share' is
# deliberately absent — it is reported rather than derived, so it already lives
# in RATIOS, and the two surfaces stay disjoint.
FUNDAMENTALS = [
    "assets",
    "assets_current",
    "cash_and_equivalents",
    "inventory",
    "marketable_securities_current",
    "receivables_net_current",
    "accounts_receivable_gross_current",
    "allowance_for_doubtful_accounts",
    "property_plant_equipment_net",
    "liabilities",
    "liabilities_current",
    "total_debt",
    "stockholders_equity",
    "preferred_stock_value",
    "revenues",
    "cost_of_goods_sold",
    "earnings_before_interest_and_taxes",
    "net_income",
    "net_income_available_to_common",
    "interest_expense",
    "dividends_paid_common",
    "preferred_stock_dividends",
    "common_shares_outstanding",
    "price_close_annual",
]

# The export reports monetary figures and share counts in millions; the closing
# price alone is a plain per-share amount. A filter is written in the field's own
# unit, so "revenue over $1B" is revenues:gt:1000 and not revenues:gt:1000000000.
# /fields publishes this with each field's observed range so a caller can tell
# which it is without guessing, because guessing wrong returns an empty sheet
# rather than an error.
UNITS = {name: "millions" for name in FUNDAMENTALS}
UNITS["price_close_annual"] = "usd_per_share"

UNIT_NOTE = (
    "Figures are in millions except price_close_annual, which is dollars per "
    "share. Revenue over one billion dollars is revenues:gt:1000."
)

DEFAULT_METRICS = [
    "current_ratio",
    "quick_ratio",
    "debt_to_equity",
    "net_margin",
    "return_on_equity",
    "asset_turnover_ratio",
]

# The /sheet defaults: what a request that names no years and no caps gets, and
# so what the app draws when it opens an industry without narrowing it.
YEARS = [2019, 2020]

# A hundred companies is 3,600 bars and a sheet about ten metres deep. It is the
# most the renderer is asked to draw at once: every bar is its own object with its
# own collider, so the ceiling here is a rendering budget rather than a limit on
# what the database will answer.
SHEET_LIMIT = 100

# How many companies each industry contributes to the cross-industry sheet.
# Ranking the whole database by size and taking the top hundred returns mostly
# manufacturers, which is a leaderboard rather than a comparison; ten from each
# keeps every industry on the sheet and keeps filtering to an industry from
# coming back empty. Ten of ten industries is a hundred rows, which is the row
# ceiling, so the two numbers are meant to be read together.
SHEET_PER = 10
LIMIT_MINIMUM = 1
LIMIT_MAXIMUM = 200

# What "largest first" ranks companies by.
SIZE_METRIC = "working_capital"

# Each division is a contiguous run of SIC codes: a code belongs to the first
# division whose ceiling it falls under. publish.py labels every company with it,
# and /sheet and /industries read the label, so an industry means one set of
# companies everywhere.
DIVISIONS = [
    (1000, "Agriculture"),
    (1500, "Mining"),
    (1800, "Construction"),
    (4000, "Manufacturing"),
    (5000, "Transportation & Public Utilities"),
    (5200, "Wholesale Trade"),
    (6000, "Retail Trade"),
    (6800, "Finance, Insurance, Real Estate"),
    (9000, "Services"),
]

# The division holding every code above the last ceiling.
LAST_DIVISION = "Public Administration"


def division(sic):
    for ceiling, name in DIVISIONS:
        if sic < ceiling:
            return name
    return LAST_DIVISION


def division_bounds():
    """(division, lower, upper) per division, lower/upper being None at the
    open ends. Contiguous and total, so every SIC code lands in exactly one."""
    bounds = []
    floor = None
    for ceiling, name in DIVISIONS:
        bounds.append((name, floor, ceiling))
        floor = ceiling
    bounds.append((LAST_DIVISION, floor, None))
    return bounds


DIVISION_NAMES = [name for name, _, _ in division_bounds()]

# One color per division, and the app colors a company's bars by the industry it
# belongs to. Assignment follows DIVISION_NAMES rather than a row's rank, so an
# industry is the same color on every sheet it appears on and a filter that drops
# rows never repaints the survivors.
#
# Any two of these can end up side by side: rows leave here in company-name order
# but the user sorts them at will, so no ordering is durable and every pair has to
# stand on its own. Ten categorical colors cannot all be told apart under that —
# measured worst pairs are dE 2.9 between Transportation and Agriculture for a
# deuteranope and 7.1 between Manufacturing and Mining for normal vision (OKLab
# x100, against gates of 8 and 15). So colour is a fast way to see that two rows
# differ in kind, not a reliable way to name which kind: every row is labelled
# with its company, and the assistant names the industries on a piece when asked.
# A legend is what would fix it.
# Keyed by name rather than zipped against DIVISION_NAMES by position: inserting
# a division into DIVISIONS would otherwise shift every colour below it, which is
# exactly the "same colour on every sheet" promise above failing silently.
DIVISION_COLORS = {
    "Agriculture": "#2a78d6",                       # blue
    "Mining": "#eb6834",                            # orange
    "Construction": "#12a3b4",                      # teal
    "Manufacturing": "#e34948",                     # red
    "Transportation & Public Utilities": "#9b4dca",  # purple
    "Wholesale Trade": "#1baf7a",                   # aqua
    "Retail Trade": "#eda100",                      # yellow
    "Finance, Insurance, Real Estate": "#4a3aa7",   # violet
    "Services": "#008300",                          # green
    "Public Administration": "#e87ba4",             # magenta
}

# What a division outside DIVISION_NAMES is drawn in. The ten above are every
# division division_bounds() can return, so reaching this means the database
# holds a name this file does not know — a sheet drawn in neutral grey says so
# and stays readable, where a lookup that raised would cost the whole request.
UNKNOWN_DIVISION_COLOR = "#8a8a8a"
