"""What Postgres enforced with columns, foreign keys and partitions, the company
collection enforces with a validator and two indexes. These tests hold that it
still does: a document that breaks the shape is refused, and a sheet scoped to
an industry reads that industry's companies through an index rather than
scanning all of them."""

import pytest
from pymongo.errors import WriteError

import api
import database
from api import SheetQuery
from metrics import DIVISION_NAMES


def insert(doc):
    database.companies().insert_one(doc)
    database.companies().delete_one({"_id": doc["_id"]})


def company(**changes):
    doc = {"_id": "ZZSHAPE", "name": "Shape Test", "gvkey": 0, "cik": 0, "sic_code": 7370,
           "division": "Services", "address": {},
           "years": [{"year": 2019, "ratios": {"net_margin": 0.1}}]}
    doc.update(changes)
    return doc


def test_a_well_formed_company_is_accepted():
    insert(company())


@pytest.mark.parametrize("bad", [
    {"division": "Atlantis"},
    {"years": [{"year": 2019, "ratios": {"revenues": 5}}]},          # a line item is not a ratio
    {"years": [{"year": 2019, "ratios": {"net_margin": "high"}}]},    # a figure is a number
    {"years": [{"year": 1850, "ratios": {}}]},
    {"years": [{"year": 2019, "ratios": {}, "notes": "x"}]},
    {"years": [{"year": 2019, "ratios": {}, "fundamentals": {"current_ratio": 1}}]},
])
def test_the_validator_refuses_a_malformed_company(bad):
    with pytest.raises(WriteError):
        insert(company(**bad))


def test_every_division_has_companies():
    assert set(database.companies().distinct("division")) == set(DIVISION_NAMES)


def index_used(query):
    plan = database.client()[database.name()].command(
        "explain",
        {"aggregate": database.COLLECTION, "pipeline": api.sheet_pipeline(query), "cursor": {}},
        verbosity="queryPlanner",
    )
    text = str(plan)
    return "IXSCAN" in text, text


@pytest.mark.parametrize("query", [SheetQuery(sic=7370), SheetQuery(division="Mining"),
                                   SheetQuery(sic=2836, where=["revenues:gt:1000"])])
def test_a_scoped_sheet_reads_through_an_index(query):
    used, plan = index_used(query)
    assert used, plan[:400]


def test_the_cross_industry_sheet_reads_everything():
    # The contrast that makes the test above mean something.
    used, _ = index_used(SheetQuery())
    assert not used
