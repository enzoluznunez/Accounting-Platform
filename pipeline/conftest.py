import pandas as pd
import psycopg
import pytest

import database
from clean import DSN
from metrics import FUNDAMENTALS, RATIOS


def staging():
    """The export as clean.py loaded it. Cleaning and computing still happen in
    Postgres, so the formula tests check what reached MongoDB against the rows
    it was computed from there."""
    with psycopg.connect(DSN) as conn:
        rows = conn.execute("SELECT * FROM staging")
        return pd.DataFrame(rows.fetchall(), columns=[c.name for c in rows.description])


def per_year(section, names):
    """One row per company-year, as the Postgres tables had them, read out of
    the documents the API serves. An unreported figure comes back as NaN."""
    rows = []
    for doc in database.companies().find({}, {"sic_code": 1, "years": 1}):
        for entry in doc["years"]:
            if section == "fundamentals" and section not in entry:
                continue
            figures = entry.get(section, {})
            rows.append({"ticker": doc["_id"], "year": entry["year"], "sic_code": doc["sic_code"],
                         **{name: figures.get(name) for name in names}})
    frame = pd.DataFrame(rows, columns=["ticker", "year", "sic_code", *names])
    return frame.astype({name: "float64" for name in names})


@pytest.fixture(scope="session")
def clean():
    """The export as it was loaded, with the key column under the name every
    other table calls it."""
    return staging().rename(columns={"trading_symbol": "ticker"})


@pytest.fixture(scope="session")
def ratios():
    return per_year("ratios", RATIOS)


@pytest.fixture(scope="session")
def merged(clean, ratios):
    return clean.merge(ratios, on=["ticker", "year"])


@pytest.fixture(scope="session")
def client():
    from fastapi.testclient import TestClient

    import api

    with TestClient(api.app) as connected:
        yield connected


@pytest.fixture(scope="session")
def fundamentals():
    return per_year("fundamentals", FUNDAMENTALS)


@pytest.fixture(scope="session")
def companies():
    docs = database.companies().find({}, {"years": 0})
    return pd.DataFrame([{"ticker": doc.pop("_id"), **doc} for doc in docs])


@pytest.fixture(scope="session")
def name_order():
    """Tickers in the order the database puts them in, which is the order the
    sheet is built with. Rows are labelled by ticker rather than by company
    name, so a ticker is what the row axis is ordered on."""
    return [doc["_id"] for doc in database.companies().find({}, {"_id": 1}).sort("_id", 1)]


def seeded(ticker, name, years):
    return {"_id": ticker, "name": name, "gvkey": 0, "cik": 0, "sic_code": 7370,
            "division": "Services", "address": {}, "years": years}


@pytest.fixture
def blank():
    """Seed a company in SIC 7370 whose revenues are unreported, then take it
    back out. clean.py drops any row with a missing value, so the shipped data
    has no gaps at all and the 'unknown is not zero' rule has nothing to
    exercise unless one is arranged."""
    # revenues left out; assets given so the line items are not empty.
    years = [{"year": year, "ratios": {"working_capital": 5}, "fundamentals": {"assets": 10}}
             for year in (2019, 2020)]
    database.companies().insert_one(seeded("ZZBLANK", "Unreported Holdings", years))
    try:
        # The ticker, because that is what a row on the sheet is labelled with.
        yield "ZZBLANK"
    finally:
        database.companies().delete_one({"_id": "ZZBLANK"})


@pytest.fixture
def twin():
    """Seed a second company in SIC 7370 that shares an existing company's name,
    then take it back out, and hand back the two tickers that now share it.
    Nothing in the source data collides inside one industry, so the collision has
    to be arranged to be tested. The tickers come from here rather than from the
    companies frame, which is read once per session and so predates this one."""
    existing = database.companies().find_one({"sic_code": 7370}, {"name": 1}, sort=[("_id", 1)])
    years = [{"year": year, "ratios": {"working_capital": 1, "net_margin": 0.1}} for year in (2019, 2020)]
    database.companies().insert_one(seeded("ZZTWIN", existing["name"], years))
    try:
        yield {existing["_id"], "ZZTWIN"}
    finally:
        database.companies().delete_one({"_id": "ZZTWIN"})
