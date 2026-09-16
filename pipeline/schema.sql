DROP TABLE IF EXISTS fundamentals;
DROP TABLE IF EXISTS financials;
DROP TABLE IF EXISTS companies;
DROP TABLE IF EXISTS industries;

-- division depends only on sic_code, so it is stored once per code rather than
-- once per company: the transitive dependency that kept companies out of 3NF.
CREATE TABLE industries (
    sic_code INTEGER PRIMARY KEY,
    division TEXT NOT NULL
);

-- Descriptive columns only. Nothing here is a metric; see financials for those.
CREATE TABLE companies (
    ticker        TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    gvkey         INTEGER NOT NULL,
    cik           INTEGER NOT NULL,
    sic_code      INTEGER NOT NULL REFERENCES industries (sic_code),
    address_line1 TEXT,
    postal_code   TEXT,
    city          TEXT,
    country       TEXT,
    -- Nothing new is true here: ticker is already unique on its own. It exists
    -- so the per-year tables can point a composite foreign key at (ticker,
    -- sic_code) and have their copy of sic_code checked against this one.
    UNIQUE (ticker, sic_code)
);

-- Both per-year tables carry sic_code as their partition key. Postgres requires
-- the partition key to be a column of the table it partitions, and neither of
-- these can reach sic_code except through companies, so it is copied here.
--
-- That copy is physical layout, not a second source of truth: the composite
-- foreign key below makes a row whose sic_code disagrees with companies
-- unstorable. division stays where 3NF put it, on industries alone.
--
-- The widened primary key is the other thing partitioning costs: a unique
-- constraint on a partitioned table has to contain the partition key, so
-- (ticker, year) becomes (ticker, year, sic_code). It still admits only one row
-- per company-year, because sic_code is functionally determined by ticker and
-- the foreign key enforces it.

CREATE TABLE financials (
    ticker                              TEXT NOT NULL,
    year                                INTEGER NOT NULL,
    sic_code                            INTEGER NOT NULL,
    working_capital                     DOUBLE PRECISION,
    current_ratio                       DOUBLE PRECISION,
    quick_ratio                         DOUBLE PRECISION,
    accounts_receivable_turnover        DOUBLE PRECISION,
    average_days_to_collect_receivables DOUBLE PRECISION,
    inventory_turnover                  DOUBLE PRECISION,
    average_days_to_collect_inventory   DOUBLE PRECISION,
    debt_to_assets                      DOUBLE PRECISION,
    debt_to_equity                      DOUBLE PRECISION,
    number_of_times_interest_is_earned  DOUBLE PRECISION,
    net_margin                          DOUBLE PRECISION,
    asset_turnover_ratio                DOUBLE PRECISION,
    return_on_investment                DOUBLE PRECISION,
    return_on_equity                    DOUBLE PRECISION,
    earnings_per_share                  DOUBLE PRECISION,
    book_value_per_share                DOUBLE PRECISION,
    price_earnings_ratio                DOUBLE PRECISION,
    dividend_yield                      DOUBLE PRECISION,
    PRIMARY KEY (ticker, year, sic_code),
    FOREIGN KEY (ticker, sic_code) REFERENCES companies (ticker, sic_code),
    CHECK (year BETWEEN 1900 AND 2100)
) PARTITION BY RANGE (sic_code);

-- The reported line items the ratios were computed from. Filterable and
-- joinable; never plottable. A sheet's columns come from financials alone, so
-- this table can grow without widening what the app can draw.
CREATE TABLE fundamentals (
    ticker                            TEXT NOT NULL,
    year                              INTEGER NOT NULL,
    sic_code                          INTEGER NOT NULL,
    assets                            DOUBLE PRECISION,
    assets_current                    DOUBLE PRECISION,
    cash_and_equivalents              DOUBLE PRECISION,
    inventory                         DOUBLE PRECISION,
    marketable_securities_current     DOUBLE PRECISION,
    receivables_net_current           DOUBLE PRECISION,
    accounts_receivable_gross_current DOUBLE PRECISION,
    allowance_for_doubtful_accounts   DOUBLE PRECISION,
    property_plant_equipment_net      DOUBLE PRECISION,
    liabilities                       DOUBLE PRECISION,
    liabilities_current               DOUBLE PRECISION,
    total_debt                        DOUBLE PRECISION,
    stockholders_equity               DOUBLE PRECISION,
    preferred_stock_value             DOUBLE PRECISION,
    revenues                          DOUBLE PRECISION,
    cost_of_goods_sold                DOUBLE PRECISION,
    earnings_before_interest_and_taxes DOUBLE PRECISION,
    net_income                        DOUBLE PRECISION,
    net_income_available_to_common    DOUBLE PRECISION,
    interest_expense                  DOUBLE PRECISION,
    dividends_paid_common             DOUBLE PRECISION,
    preferred_stock_dividends         DOUBLE PRECISION,
    common_shares_outstanding         DOUBLE PRECISION,
    price_close_annual                DOUBLE PRECISION,
    PRIMARY KEY (ticker, year, sic_code),
    FOREIGN KEY (ticker, sic_code) REFERENCES companies (ticker, sic_code),
    CHECK (year BETWEEN 1900 AND 2100)
) PARTITION BY RANGE (sic_code);

-- One partition per division, cut on the boundaries in metrics.DIVISIONS. The
-- ranges are contiguous and run to both infinities, so every SIC code that can
-- exist lands in exactly one and no DEFAULT partition is needed. Partition
-- names are the division slugs metrics.slug produces, so a partition and the
-- division it holds are the same industry under the same name.
-- test_partitions.py fails if these bounds and metrics.DIVISIONS disagree.

CREATE TABLE financials_agriculture            PARTITION OF financials FOR VALUES FROM (MINVALUE) TO (1000);
CREATE TABLE financials_mining                 PARTITION OF financials FOR VALUES FROM (1000) TO (1500);
CREATE TABLE financials_construction           PARTITION OF financials FOR VALUES FROM (1500) TO (1800);
CREATE TABLE financials_manufacturing          PARTITION OF financials FOR VALUES FROM (1800) TO (4000);
CREATE TABLE financials_transportation_public_utilities PARTITION OF financials FOR VALUES FROM (4000) TO (5000);
CREATE TABLE financials_wholesale_trade        PARTITION OF financials FOR VALUES FROM (5000) TO (5200);
CREATE TABLE financials_retail_trade           PARTITION OF financials FOR VALUES FROM (5200) TO (6000);
CREATE TABLE financials_finance_insurance_real_estate PARTITION OF financials FOR VALUES FROM (6000) TO (6800);
CREATE TABLE financials_services               PARTITION OF financials FOR VALUES FROM (6800) TO (9000);
CREATE TABLE financials_public_administration  PARTITION OF financials FOR VALUES FROM (9000) TO (MAXVALUE);

CREATE TABLE fundamentals_agriculture          PARTITION OF fundamentals FOR VALUES FROM (MINVALUE) TO (1000);
CREATE TABLE fundamentals_mining               PARTITION OF fundamentals FOR VALUES FROM (1000) TO (1500);
CREATE TABLE fundamentals_construction         PARTITION OF fundamentals FOR VALUES FROM (1500) TO (1800);
CREATE TABLE fundamentals_manufacturing        PARTITION OF fundamentals FOR VALUES FROM (1800) TO (4000);
CREATE TABLE fundamentals_transportation_public_utilities PARTITION OF fundamentals FOR VALUES FROM (4000) TO (5000);
CREATE TABLE fundamentals_wholesale_trade      PARTITION OF fundamentals FOR VALUES FROM (5000) TO (5200);
CREATE TABLE fundamentals_retail_trade         PARTITION OF fundamentals FOR VALUES FROM (5200) TO (6000);
CREATE TABLE fundamentals_finance_insurance_real_estate PARTITION OF fundamentals FOR VALUES FROM (6000) TO (6800);
CREATE TABLE fundamentals_services             PARTITION OF fundamentals FOR VALUES FROM (6800) TO (9000);
CREATE TABLE fundamentals_public_administration PARTITION OF fundamentals FOR VALUES FROM (9000) TO (MAXVALUE);

CREATE INDEX idx_companies_sic ON companies (sic_code);

-- /sheet ranks an industry's companies by size before it pivots. Declared on
-- the parent, so Postgres builds one per partition and the ranking pass inside
-- the pruned partition is an index-only scan.
CREATE INDEX idx_financials_year_ticker
    ON financials (year, ticker) INCLUDE (working_capital);

-- No per-column indexes on fundamentals. A filter only ever runs inside one
-- pruned partition, and the largest of those is 1,312 rows: twenty-four indexes
-- would cost more to maintain than the scans they would save.

-- This file builds the tables; ratios.py fills them, straight from staging, and
-- analyzes them once the rows are in.
