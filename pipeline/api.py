import hmac
from enum import StrEnum
from typing import Annotated

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse
from mangum import Mangum
from pydantic import BaseModel, Field, field_validator, model_validator

import database
import sheetcsv
from metrics import (
    CATEGORIES,
    DIVISION_NAMES,
    DEFAULT_METRICS,
    FUNDAMENTALS,
    LIMIT_MAXIMUM,
    LIMIT_MINIMUM,
    RATIOS,
    SHEET_LIMIT,
    SHEET_PER,
    SIZE_METRIC,
    UNIT_NOTE,
    UNITS,
    YEARS,
    category_of,
)

# The metric surface. A request can name these columns and nothing else, so the
# descriptive columns on companies stay unreachable as sheet data no matter how
# wide that table grows.
MetricName = StrEnum("MetricName", {name: name for name in RATIOS})

# The filter surface, kept separate from the metric surface on purpose. A ratio
# is what a sheet can draw; a fundamental is what a request can filter on. The
# two lists are disjoint, so neither can quietly become the other.
FieldName = StrEnum("FieldName", {name: name for name in FUNDAMENTALS})

Operator = StrEnum("Operator", {op: op for op in ("eq", "ne", "lt", "lte", "gt", "gte")})

# An industry means one thing in this system: a division. It is what every
# company document carries, what /industries lists, and what the app lists as
# a dataset. A SIC code still names a narrower slice within
# one, and /sheet takes either.
DivisionName = StrEnum("DivisionName", {name: name for name in DIVISION_NAMES})

# Metrics come in five kinds. Naming a category is the column-side counterpart
# of naming an industry: one word that stands for a group, so a caller can ask
# for "the liquidity ratios" without spelling out which three those are.
CategoryName = StrEnum("CategoryName", {name: name for name in CATEGORIES})

COMPARISONS = {op: f"${op}" for op in ("eq", "ne", "lt", "lte", "gt", "gte")}


class Predicate(BaseModel):
    """One 'field:op:value' clause, e.g. revenues:gt:1e9."""

    field: FieldName
    op: Operator
    value: float

    @classmethod
    def parse(cls, text):
        parts = text.split(":")
        if len(parts) != 3:
            raise ValueError(
                f"{text!r} is not a filter; write it as field:op:value, "
                "like revenues:gt:1000000000. ListFields has the field names"
            )
        field, op, value = (part.strip() for part in parts)
        if field not in FUNDAMENTALS:
            raise ValueError(
                f"{field!r} is not a field this database can filter on; ListFields has the names"
            )
        if op not in COMPARISONS:
            raise ValueError(
                f"{op!r} is not a comparison; use one of {', '.join(COMPARISONS)}"
            )
        try:
            number = float(value)
        except ValueError:
            raise ValueError(f"{value!r} is not a number, so {field} cannot be compared to it") from None
        return cls(field=field, op=op, value=number)

app = FastAPI(title="NASBA Financial Ratios")

# What anyone may ask without a key: whether the service is up, and nothing
# about the data beyond how many rows it holds.
OPEN_PATHS = {"/health"}


@app.middleware("http")
async def require_api_key(request: Request, call_next):
    """The data is licensed, and a Function URL is reachable by anyone, so every
    request but a health check carries the key in X-Api-Key. Where no key is
    configured — a laptop, the tests — nothing is checked, so the key is a
    property of the deployment rather than of the code."""
    expected = database.setting("API_KEY")
    if expected and request.url.path not in OPEN_PATHS:
        given = request.headers.get("x-api-key", "")
        # Compared in constant time, so the reply's timing says nothing about
        # how much of a guess was right.
        if not hmac.compare_digest(given.encode(), expected.encode()):
            return JSONResponse(status_code=401,
                                content={"detail": "The API key is missing or wrong."})
    return await call_next(request)


# What to say when a parameter's value is not one of the names it enumerates.
# Listing all eighteen ratios, or all ten industries, would be most of the reply
# the assistant reads out, so each one points at the tool that lists them.
ENUM_HELP = {
    "metrics": "is not a ratio this database holds; ListRatios has the names",
    "categories": "is not a kind of ratio; ListRatios groups them by kind",
    "division": "is not an industry this database holds; ListIndustries has the names",
}


# Pydantic reports a list of structured errors; the voice client reads 'detail'
# as one string, so flatten it into a sentence the model can say out loud.
@app.exception_handler(RequestValidationError)
async def readable_validation_error(request: Request, exc: RequestValidationError):
    parts = []
    for error in exc.errors():
        path = [p for p in error["loc"] if p not in ("query", "body", "path")]
        where = ".".join(str(p) for p in path)
        given = error.get("input")
        if error["type"] == "enum":
            field = str(path[0]) if path else ""
            message = f"{given!r} {ENUM_HELP.get(field, 'is not one of the names this parameter takes')}"
        else:
            # A ValueError raised in a validator arrives as "Value error, <text>".
            # The assistant reads this out loud, so drop the machinery.
            message = error["msg"].removeprefix("Value error, ")
            if given is not None and not isinstance(given, (list, dict)):
                message = f"{message} (got {given!r})"
        parts.append(f"{where}: {message}" if where else message)
    return JSONResponse(status_code=422, content={"detail": "; ".join(parts)})


class Health(BaseModel):
    status: str
    financial_rows: int


class RatioCatalog(BaseModel):
    ratios: list[MetricName]
    default: list[MetricName]
    categories: dict[str, list[MetricName]]


class Filterable(BaseModel):
    name: FieldName
    unit: str
    minimum: float | None
    maximum: float | None


class FieldCatalog(BaseModel):
    fields: list[Filterable]
    operators: list[Operator]
    note: str
    example: str


class Industry(BaseModel):
    division: DivisionName
    companies: int
    sic_codes: int


class IndustryList(BaseModel):
    industries: list[Industry]


class SheetQuery(BaseModel):
    """Every /sheet parameter, including the two that used to be parsed by hand."""

    model_config = {"extra": "forbid"}

    # Which rows. A division is one industry and a SIC code a narrower slice
    # inside one; naming neither gives the cross-industry sheet, which is the
    # view the app opens with. At most one of them, so a sheet is never
    # ambiguous about what it holds.
    sic: int | None = None
    division: DivisionName | None = None

    # Which columns. 'metrics' names ratios one by one; 'categories' names whole
    # groups of them and decides the columns on its own when it is given, since
    # FastAPI fills every parameter in and a model cannot tell a default apart
    # from a caller who sent the same value.
    metrics: Annotated[list[MetricName], Field(min_length=1)] = [MetricName(m) for m in DEFAULT_METRICS]
    categories: list[CategoryName] = []

    years: Annotated[list[int], Field(min_length=1)] = YEARS
    limit: Annotated[int, Field(ge=LIMIT_MINIMUM, le=LIMIT_MAXIMUM)] = SHEET_LIMIT

    # How many companies each industry contributes to a cross-industry sheet.
    # Ranking the whole database by size and taking the top thirty returns
    # twenty-one manufacturers and nothing at all from six industries, which is
    # a leaderboard rather than a comparison. Taking a few from each keeps every
    # industry on the sheet and keeps an industry filter from coming back empty.
    per: Annotated[int, Field(ge=1, le=LIMIT_MAXIMUM)] = SHEET_PER

    # Filters on the reported line items: repeat the parameter, or comma-separate
    # it, for more than one. They narrow which companies reach the sheet; they
    # never change which columns it holds.
    where: list[str] = []

    # Whether a company has to satisfy the filters in every requested year or in
    # at least one of them. 'any' is the default because a company that cleared
    # the bar in 2019 and fell below it in 2020 is exactly what this data is for.
    match: Annotated[str, Field(pattern="^(any|all)$")] = "any"

    # Callers send these as one comma-separated value; FastAPI hands a list only
    # when the parameter is repeated. Accept both and flatten.
    @field_validator("years", "metrics", "categories", "where", mode="before")
    @classmethod
    def split_commas(cls, value):
        if value is None:
            return value
        items = value if isinstance(value, (list, tuple)) else [value]
        flattened = []
        for item in items:
            if isinstance(item, str):
                flattened.extend(part.strip() for part in item.split(",") if part.strip())
            else:
                flattened.append(item)
        return flattened

    @field_validator("years")
    @classmethod
    def ascending_and_unique(cls, value):
        return sorted(set(value))

    @model_validator(mode="after")
    def one_scope(self):
        if self.sic is not None and self.division is not None:
            raise ValueError(
                "name one industry, not two: division, as ListIndustries gives it, "
                "or sic for a narrower slice inside one, or neither for every industry"
            )
        return self

    @property
    def columns(self):
        """The metrics the sheet draws, in RATIOS order however they were named,
        so two requests for the same columns produce the same sheet. Naming
        categories decides them; naming metrics picks them out one at a time."""
        if not self.categories:
            return [metric.value for metric in self.metrics]
        wanted = {ratio for name in self.categories for ratio in CATEGORIES[name.value]}
        return [ratio for ratio in RATIOS if ratio in wanted]

    @property
    def everywhere(self):
        return self.sic is None and self.division is None

    @property
    def scope_label(self):
        if self.sic is not None:
            return f"SIC {self.sic}"
        return self.division.value if self.division is not None else "any industry"

    @property
    def scope(self):
        """Which company documents the sheet is drawn from, as a MongoDB filter.
        Division and sic_code are both indexed, so either narrows before
        anything else is read."""
        if self.sic is not None:
            return {"sic_code": self.sic}
        if self.division is not None:
            return {"division": self.division.value}
        return {}

    @field_validator("where")
    @classmethod
    def parseable(cls, value):
        # Parsed here rather than in the handler so a malformed filter is a 422
        # with the reason, in the same voice as every other rejection.
        for text in value:
            Predicate.parse(text)
        return value

    @property
    def predicates(self):
        return [Predicate.parse(text) for text in self.where]


@app.get("/health", response_model=Health)
def health():
    # One financial row per company-year.
    counted = list(database.companies().aggregate([
        {"$group": {"_id": None, "n": {"$sum": {"$size": "$years"}}}},
    ]))
    return {"status": "ok", "financial_rows": counted[0]["n"] if counted else 0}


@app.get("/ratios", response_model=RatioCatalog)
def list_ratios():
    return {"ratios": RATIOS, "default": DEFAULT_METRICS, "categories": CATEGORIES}


@app.get("/fields", response_model=FieldCatalog)
def list_fields():
    """What /sheet's 'where' can name. These are the reported figures the ratios
    were computed from: a request can filter on them, and a sheet never plots
    them."""
    # $min and $max pass over a figure that was never reported, as SQL's did.
    ranges = {}
    for n in FUNDAMENTALS:
        ranges[f"lo_{n}"] = {"$min": f"$years.fundamentals.{n}"}
        ranges[f"hi_{n}"] = {"$max": f"$years.fundamentals.{n}"}
    found = list(database.companies().aggregate([
        {"$unwind": "$years"},
        {"$group": {"_id": None, **ranges}},
    ]))
    seen = found[0] if found else {key: None for key in ranges}

    return {
        "fields": [
            {
                "name": name,
                "unit": UNITS[name],
                "minimum": seen[f"lo_{name}"],
                "maximum": seen[f"hi_{name}"],
            }
            for name in FUNDAMENTALS
        ],
        "operators": list(COMPARISONS),
        "note": UNIT_NOTE,
        "example": "revenues:gt:1000",
    }


@app.get("/industries", response_model=IndustryList)
def industries(minimum: Annotated[int, Query(ge=1)] = 1):
    """The industries the database holds, one row per division. There are ten,
    the same ten the app lists as datasets and the same ten every company
    document is labelled with, so a name means one thing everywhere. This is the request
    the app makes at startup to know what it can open."""
    rows = database.companies().aggregate([
        {"$group": {"_id": "$division", "companies": {"$sum": 1}, "codes": {"$addToSet": "$sic_code"}}},
        {"$match": {"companies": {"$gte": minimum}}},
        {"$sort": {"companies": -1, "_id": 1}},
        {"$project": {"_id": 0, "division": "$_id", "companies": 1, "sic_codes": {"$size": "$codes"}}},
    ])
    return {"industries": list(rows)}


def sheet_pipeline(query):
    """The /sheet aggregation. Separate from the endpoint so a test can explain
    the pipeline the app actually runs rather than one that looks like it.

    Every field named below comes from RATIOS or FUNDAMENTALS by way of an
    enum, so nothing a caller typed becomes a field path; only the values
    compared against are free text, and they arrive as numbers."""
    size = f"$years.ratios.{SIZE_METRIC}"
    stages = [
        {"$match": query.scope},
        # Only the requested years; a company with none of them has nothing to
        # draw and does not reach the sheet.
        {"$set": {"years": {"$filter": {"input": "$years", "as": "y",
                                        "cond": {"$in": ["$$y.year", query.years]}}}}},
        {"$match": {"years.0": {"$exists": True}}},
        # $max passes over an unreported figure, and is null when every year is
        # unreported; a descending sort puts null last, as NULLS LAST did.
        {"$set": {"size": {"$max": size}}},
    ]

    if query.everywhere:
        # No industry named, so the ranking is done within each industry rather
        # than across all of them: a few from each keeps every industry on the
        # sheet. Taken before any filter, so a filter narrows those few.
        # $topN rather than a window: a window numbers rows by one sort key, and
        # ties on size have to fall to the ticker, as they do in the final cut.
        stages += [
            {"$group": {"_id": "$division",
                        "top": {"$topN": {"n": query.per, "sortBy": {"size": -1, "_id": 1},
                                          "output": "$$ROOT"}}}},
            {"$unwind": "$top"},
            {"$replaceWith": "$top"},
        ]

    predicates = query.predicates
    if predicates:
        # A company qualifies on the years it actually satisfies every filter in;
        # 'all' demands that of each requested year. An unreported figure is not
        # a number, and a comparison it takes part in never holds — MongoDB
        # would otherwise rank a missing figure below every number and let it
        # pass 'lt'.
        holds = {"$and": [
            clause
            for p in predicates
            for figure in [f"$$y.fundamentals.{p.field.value}"]
            for clause in ({"$isNumber": figure}, {COMPARISONS[p.op.value]: [figure, p.value]})
        ]}
        satisfied = {"$size": {"$filter": {"input": "$years", "as": "y", "cond": holds}}}
        needed = len(query.years) if query.match == "all" else 1
        stages.append({"$match": {"$expr": {"$gte": [satisfied, needed]}}})

    stages += [
        # Which companies reach the sheet is a question of size; what order they
        # stand in on it is not. The cut is taken by size, and the rows leave
        # in ticker order, which is what they are labelled with.
        {"$sort": {"size": -1, "_id": 1}},
        {"$limit": query.limit},
        {"$sort": {"_id": 1}},
        {"$project": {"division": 1, "years.year": 1,
                      **{f"years.ratios.{metric}": 1 for metric in query.columns}}},
    ]
    return stages


class EmptySheet(Exception):
    """No company matched. The endpoint turns this into a 404, which the app
    reports as an industry it could not open."""


def sheet_csv(query):
    """One industry, or every industry, as the CSV the app parses. Every sheet
    the app draws comes through here, so the rows it shows are the database as
    it stands rather than an export of how it once stood."""
    found = list(database.companies().aggregate(sheet_pipeline(query)))
    if not found:
        raise EmptySheet(describe_empty(query))

    # Labelled by ticker: a ticker is a handful of characters where a name runs
    # to twenty-seven, so every label fits beside its row on the sheet instead
    # of running into its neighbours. It is unique too, where seven companies
    # in this data share one name.
    companies = (
        {"name": doc["_id"], "industry": doc["division"],
         "years": {entry["year"]: entry.get("ratios", {}) for entry in doc["years"]}}
        for doc in found
    )
    return sheetcsv.render(companies, query.columns, query.years)


def describe_empty(query):
    predicates = query.predicates
    if not predicates:
        return f"no companies found for {query.scope_label}"
    spelled = ", ".join(
        f"{p.field.value} {p.op.value} {p.value:,.0f}" if p.value == int(p.value)
        else f"{p.field.value} {p.op.value} {p.value:,}"
        for p in predicates
    )
    return (f"no companies in {query.scope_label} match {spelled}"
            + (" in every requested year" if query.match == "all" else ""))


@app.get("/sheet", response_class=PlainTextResponse)
def sheet(query: Annotated[SheetQuery, Query()]):
    try:
        return sheet_csv(query)
    except EmptySheet as empty:
        raise HTTPException(404, str(empty)) from None


# Lambda's entry point: Mangum turns a Function URL event into the request
# FastAPI expects and its response back. uvicorn on a laptop never touches it.
handler = Mangum(app, lifespan="off")
