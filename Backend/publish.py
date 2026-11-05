"""Publish the computed model to MongoDB, one document per company.

rebuild.py calls this with what ratios.compute() returned. Three frames become
one collection: a company's years travel inside it, each with its ratios and
its reported line items, because /sheet always wants a company and its years
together. Division is stored on the company rather than looked up: it is a
function of sic_code, this is the one place that applies that function, and
test_contract.py fails if a document disagrees.

A figure that could not be computed is left out of the document rather than
written as null, so "unreported" has one spelling.
"""

import math

from pymongo import ASCENDING

import database
from metrics import DIVISION_NAMES, FUNDAMENTALS, RATIOS, division

ADDRESS = ["address_line1", "postal_code", "city", "country"]


def plain(value):
    """A frame's cell as the Python value BSON can hold: numpy's integers are
    not ints to the driver, and a missing figure is None."""
    if value is None or (isinstance(value, float) and math.isnan(value)):
        return None
    if hasattr(value, "item"):
        value = value.item()
        if isinstance(value, float) and math.isnan(value):
            return None
    return value


def figures(row, names):
    found = {}
    for name in names:
        value = plain(row[name])
        if value is not None:
            found[name] = float(value)
    return found


def documents(frames):
    fundamentals = {
        (row["ticker"], plain(row["year"])): row
        for row in frames["fundamentals"].to_dict("records")
    }

    # A year exists where a financials row does; the line items ride along
    # where reported.
    years = {}
    for row in sorted(frames["financials"].to_dict("records"), key=lambda r: (r["ticker"], r["year"])):
        year = plain(row["year"])
        entry = {"year": year, "ratios": figures(row, RATIOS)}
        reported = fundamentals.get((row["ticker"], year))
        if reported is not None:
            entry["fundamentals"] = figures(reported, FUNDAMENTALS)
        years.setdefault(row["ticker"], []).append(entry)

    for company in sorted(frames["companies"].to_dict("records"), key=lambda r: r["ticker"]):
        sic_code = plain(company["sic_code"])
        yield {
            "_id": company["ticker"],
            "name": company["name"],
            "gvkey": plain(company["gvkey"]),
            "cik": plain(company["cik"]),
            "sic_code": sic_code,
            "division": division(sic_code),
            "address": {key: company[key] for key in ADDRESS if plain(company[key]) is not None},
            "years": years.get(company["ticker"], []),
        }


def figures_schema(names):
    """An object that may hold these figures and nothing else, each a number.
    This is what keeps a descriptive field from being stored where a sheet
    could plot it: the validator holds that line, so no code path can widen
    the metric surface by accident."""
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


def publish(docs, name=None):
    """Replace the collection with these documents. Replaced whole rather than
    updated in place: the collection is derived, so rebuilding it is the one
    way to be sure nothing stale survives."""
    db = database.client()[name or database.name()]
    db.drop_collection(database.COLLECTION)
    db.create_collection(database.COLLECTION, validator=VALIDATOR, validationLevel="strict")
    collection = db[database.COLLECTION]
    collection.insert_many(list(docs), ordered=True)

    # /sheet scopes by one of these two before it reads anything else.
    collection.create_index([("division", ASCENDING)])
    collection.create_index([("sic_code", ASCENDING)])
    return collection
