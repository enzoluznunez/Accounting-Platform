import os

# The tests seed companies and delete them again, so they never run against the
# database the deployed API serves: every test reads and writes this one, which
# the session rebuilds from the export before anything runs. Set before
# database is imported, so no .env can point the tests anywhere else.
TEST_DATABASE = "nasba_test"
os.environ["MONGODB_DB"] = TEST_DATABASE

import pandas as pd  # noqa: E402
import pytest  # noqa: E402

import database  # noqa: E402
import publish  # noqa: E402
import rebuild  # noqa: E402
from metrics import FUNDAMENTALS, RATIOS  # noqa: E402


@pytest.fixture(scope="session", autouse=True)
def staging():
    """The export, cleaned, and published into the test database. Every run
    starts from the export in S3, so the tests check the whole path — export,
    cleaning, ratios, documents, API — rather than whatever was loaded last."""
    assert database.name() == TEST_DATABASE
    cleaned, docs = rebuild.build()
    publish.publish(docs, TEST_DATABASE)
    return cleaned


def per_year(section, names):
    """One row per company-year, flattened out of the documents the API
    serves, so the formula tests can line them up against the cleaned export. An unreported figure comes back as NaN."""
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
def clean(staging):
    """The export as it was cleaned, with the key column under the name every
    other frame calls it."""
    return staging.rename(columns={"trading_symbol": "ticker"})


@pytest.fixture(scope="session")
def ratios(staging):
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
def fundamentals(staging):
    return per_year("fundamentals", FUNDAMENTALS)


@pytest.fixture(scope="session")
def companies(staging):
    docs = database.companies().find({}, {"years": 0})
    return pd.DataFrame([{"ticker": doc.pop("_id"), **doc} for doc in docs])


@pytest.fixture(scope="session")
def name_order(staging):
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
