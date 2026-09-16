import pandas as pd
import psycopg
import pytest

import api


def table(name):
    """A table as a frame. The database is the source these tests check against;
    nothing is read back from a file."""
    with psycopg.connect(api.DSN) as conn:
        rows = conn.execute(f"SELECT * FROM {name}")
        return pd.DataFrame(rows.fetchall(), columns=[c.name for c in rows.description])


@pytest.fixture(scope="session")
def clean():
    """The export as it was loaded, with the key column under the name every
    other table calls it."""
    return table("staging").rename(columns={"trading_symbol": "ticker"})


@pytest.fixture(scope="session")
def ratios():
    return table("financials")


@pytest.fixture(scope="session")
def merged(clean, ratios):
    return clean.merge(ratios, on=["ticker", "year"])


@pytest.fixture(scope="session")
def client():
    from fastapi.testclient import TestClient

    import api

    # Entering the context manager runs the lifespan, which opens the pool.
    with TestClient(api.app) as connected:
        yield connected


@pytest.fixture(scope="session")
def fundamentals():
    return table("fundamentals")


@pytest.fixture(scope="session")
def companies():
    return table("companies")


@pytest.fixture
def blank():
    """Seed a company in SIC 7370 whose revenues are unreported, then take it
    back out. clean.py drops any row with a missing value, so the shipped data
    has no nulls at all and the 'unknown is not zero' rule has nothing to
    exercise unless one is arranged."""
    import psycopg

    import api

    with psycopg.connect(api.DSN) as conn:
        conn.execute(
            "INSERT INTO companies (ticker, name, gvkey, cik, sic_code)"
            " VALUES ('ZZBLANK', 'Unreported Holdings', 0, 0, 7370)"
        )
        conn.execute(
            "INSERT INTO financials (ticker, year, sic_code, working_capital)"
            " VALUES ('ZZBLANK', 2019, 7370, 5), ('ZZBLANK', 2020, 7370, 5)"
        )
        # revenues left NULL; assets given so the row is not empty.
        conn.execute(
            "INSERT INTO fundamentals (ticker, year, sic_code, assets)"
            " VALUES ('ZZBLANK', 2019, 7370, 10), ('ZZBLANK', 2020, 7370, 10)"
        )
    try:
        yield "Unreported Holdings"
    finally:
        with psycopg.connect(api.DSN) as conn:
            for table in ("fundamentals", "financials", "companies"):
                conn.execute(f"DELETE FROM {table} WHERE ticker = 'ZZBLANK'")


@pytest.fixture
def twin():
    """Seed a second company in SIC 7370 that shares an existing company's name,
    then take it back out. Nothing in the source data collides inside one
    industry, so the collision has to be arranged to be tested."""
    import psycopg

    import api

    with psycopg.connect(api.DSN) as conn:
        name = conn.execute(
            "SELECT name FROM companies WHERE sic_code = 7370 ORDER BY ticker LIMIT 1"
        ).fetchone()[0]
        conn.execute(
            "INSERT INTO companies (ticker, name, gvkey, cik, sic_code)"
            " VALUES ('ZZTWIN', %s, 0, 0, 7370)",
            (name,),
        )
        conn.execute(
            "INSERT INTO financials (ticker, year, sic_code, working_capital, net_margin)"
            " VALUES ('ZZTWIN', 2019, 7370, 1, 0.1), ('ZZTWIN', 2020, 7370, 1, 0.1)"
        )
    try:
        yield name
    finally:
        with psycopg.connect(api.DSN) as conn:
            conn.execute("DELETE FROM fundamentals WHERE ticker = 'ZZTWIN'")
            conn.execute("DELETE FROM financials WHERE ticker = 'ZZTWIN'")
            conn.execute("DELETE FROM companies WHERE ticker = 'ZZTWIN'")
