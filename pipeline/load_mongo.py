"""Copy the model out of Postgres into MongoDB, one document per company.

Postgres still owns cleaning and computing: clean.py and ratios.py build the
four tables there, and this reads them and writes what the API serves.

    python clean.py && psql -d nasba -f schema.sql && python ratios.py
    python load_mongo.py

Four tables become one collection. A company's years travel inside it, each
with its ratios and its reported line items, because /sheet always wants a
company and its years together. Division is stored on the company rather than
looked up: it is a function of sic_code, the loader is the one place that
applies that function, and test_documents.py fails if a document disagrees.

A figure Postgres holds as NULL is left out of the document rather than written
as null, so "unreported" has one spelling.
"""

import sys

import psycopg
from psycopg.rows import dict_row
from pymongo import ASCENDING

import database
from clean import DSN
from metrics import DIVISION_NAMES, FUNDAMENTALS, RATIOS, division

ADDRESS = ["address_line1", "postal_code", "city", "country"]


def figures(row, names):
    return {name: row[name] for name in names if row[name] is not None}


def documents(conn):
    financials = conn.execute("SELECT * FROM financials ORDER BY ticker, year").fetchall()
    fundamentals = {
        (row["ticker"], row["year"]): row
        for row in conn.execute("SELECT * FROM fundamentals").fetchall()
    }

    # A year exists where Postgres has a financials row for it, which is what
    # /sheet read its years from; the line items ride along where reported.
    years = {}
    for row in financials:
        entry = {"year": row["year"], "ratios": figures(row, RATIOS)}
        reported = fundamentals.get((row["ticker"], row["year"]))
        if reported is not None:
            entry["fundamentals"] = figures(reported, FUNDAMENTALS)
        years.setdefault(row["ticker"], []).append(entry)

    for company in conn.execute("SELECT * FROM companies ORDER BY ticker").fetchall():
        yield {
            "_id": company["ticker"],
            "name": company["name"],
            "gvkey": company["gvkey"],
            "cik": company["cik"],
            "sic_code": company["sic_code"],
            "division": division(company["sic_code"]),
            "address": {key: company[key] for key in ADDRESS if company[key] is not None},
            "years": years.get(company["ticker"], []),
        }


def figures_schema(names):
    """An object that may hold these figures and nothing else, each a number.
    This is what keeps a descriptive field from being stored where a sheet
    could plot it: Postgres held that line with columns, this holds it with a
    validator."""
    return {
        "bsonType": "object",
        "additionalProperties": False,
        "properties": {name: {"bsonType": ["double", "int", "long"]} for name in names},
    }


VALIDATOR = {
    "$jsonSchema": {
        "bsonType": "object",
        "required": ["_id", "name", "sic_code", "division", "years"],
        "properties": {
            "_id": {"bsonType": "string", "minLength": 1},
            "name": {"bsonType": "string"},
            "sic_code": {"bsonType": ["int", "long"]},
            "division": {"enum": DIVISION_NAMES},
            "years": {
                "bsonType": "array",
                "items": {
                    "bsonType": "object",
                    "required": ["year", "ratios"],
                    "additionalProperties": False,
                    "properties": {
                        "year": {"bsonType": ["int", "long"], "minimum": 1900, "maximum": 2100},
                        "ratios": figures_schema(RATIOS),
                        "fundamentals": figures_schema(FUNDAMENTALS),
                    },
                },
            },
        },
    }
}


def main():
    with psycopg.connect(DSN, row_factory=dict_row) as conn:
        docs = list(documents(conn))

    db = database.client()[database.name()]
    # Replaced whole rather than updated in place: the collection is derived,
    # so rebuilding it is the one way to be sure nothing stale survives.
    db.drop_collection(database.COLLECTION)
    db.create_collection(database.COLLECTION, validator=VALIDATOR, validationLevel="strict")
    collection = db[database.COLLECTION]
    collection.insert_many(docs, ordered=True)

    # /sheet scopes by one of these two before it reads anything else.
    collection.create_index([("division", ASCENDING)])
    collection.create_index([("sic_code", ASCENDING)])

    rows = sum(len(doc["years"]) for doc in docs)
    print(f"{database.name()}.{database.COLLECTION}: {collection.count_documents({})} companies, "
          f"{rows} company-years")
    return 0


if __name__ == "__main__":
    sys.exit(main())
