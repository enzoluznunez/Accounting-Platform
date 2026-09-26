"""Where the data lives: one MongoDB collection, 'companies', one document per
company with its years inside it.

    {_id: ticker, name, gvkey, cik, sic_code, division, address: {...},
     years: [{year, ratios: {...}, fundamentals: {...}}, ...]}

The connection string comes from the environment, or from pipeline/.env when
run on a laptop; .env.example shows its shape. Nothing here holds a password.
"""

import os
from functools import cache
from pathlib import Path

from dotenv import load_dotenv
from pymongo import MongoClient

load_dotenv(Path(__file__).with_name(".env"))

COLLECTION = "companies"


@cache
def client():
    """One client per process. It pools its own connections, so every request
    shares it rather than opening a connection of its own."""
    uri = os.environ.get("MONGODB_URI")
    if not uri:
        raise RuntimeError(
            "MONGODB_URI is not set. Copy pipeline/.env.example to pipeline/.env "
            "and put your Atlas connection string in it."
        )
    # Five seconds rather than the default thirty: an unreachable database
    # should fail a request while the headset is still waiting for it.
    return MongoClient(uri, appname="nasba-api", serverSelectionTimeoutMS=5000, tz_aware=True)


def name():
    return os.environ.get("MONGODB_DB", "nasba")


def companies():
    return client()[name()][COLLECTION]
