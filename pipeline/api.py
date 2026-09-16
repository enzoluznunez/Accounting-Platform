from contextlib import asynccontextmanager
from enum import StrEnum
from typing import Annotated

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse
import psycopg
from psycopg.rows import dict_row
from psycopg_pool import ConnectionPool
from pydantic import BaseModel, Field, field_validator, model_validator

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
    division_bounds,
    UNITS,
    YEARS,
    category_of,
)

DSN = "dbname=nasba"

# The metric surface. A request can name these columns and nothing else, so the
# descriptive columns on companies stay unreachable as sheet data no matter how
# wide that table grows.
MetricName = StrEnum("MetricName", {name: name for name in RATIOS})

# The filter surface, kept separate from the metric surface on purpose. A ratio
# is what a sheet can draw; a fundamental is what a request can filter on. The
# two lists are disjoint, so neither can quietly become the other.
FieldName = StrEnum("FieldName", {name: name for name in FUNDAMENTALS})

Operator = StrEnum("Operator", {op: op for op in ("eq", "ne", "lt", "lte", "gt", "gte")})

# An industry means one thing in this system: a division. It is what the
# financials partitions are cut by, what /industries lists, and what the app
# lists as a dataset. A SIC code still names a narrower slice within
# one, and /sheet takes either.
DivisionName = StrEnum("DivisionName", {name: name for name in DIVISION_NAMES})

# Metrics come in five kinds. Naming a category is the column-side counterpart
# of naming an industry: one word that stands for a group, so a caller can ask
# for "the liquidity ratios" without spelling out which three those are.
CategoryName = StrEnum("CategoryName", {name: name for name in CATEGORIES})

BOUNDS = {name: (lower, upper) for name, lower, upper in division_bounds()}

SQL_OPERATORS = {"eq": "=", "ne": "<>", "lt": "<", "lte": "<=", "gt": ">", "gte": ">="}


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
        if op not in SQL_OPERATORS:
            raise ValueError(
                f"{op!r} is not a comparison; use one of {', '.join(SQL_OPERATORS)}"
            )
        try:
            number = float(value)
        except ValueError:
            raise ValueError(f"{value!r} is not a number, so {field} cannot be compared to it") from None
        return cls(field=field, op=op, value=number)

pool = ConnectionPool(DSN, min_size=1, max_size=8, kwargs={"row_factory": dict_row}, open=False)


@asynccontextmanager
async def lifespan(app: FastAPI):
    pool.open()
    pool.wait()
    yield
    pool.close()


app = FastAPI(title="NASBA Financial Ratios", lifespan=lifespan)


def connect():
    """A pooled connection while the service is running, a plain one otherwise,
    so calling sheet_csv from a script or a test does not require standing the
    service up first."""
    if pool.closed:
        return psycopg.connect(DSN, row_factory=dict_row)
    return pool.connection()


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

    # Which rows. A division is one partition and a SIC code a narrower slice
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

    def scope(self, alias):
        """The partition-key predicate and its parameters. Written against the
        partitioned table itself, which is what lets the planner prune before it
        reads anything."""
        if self.everywhere:
            return "TRUE", []
        if self.sic is not None:
            return f"{alias}.sic_code = %s", [self.sic]
        lower, upper = BOUNDS[self.division.value]
        clauses, params = [], []
        if lower is not None:
            clauses.append(f"{alias}.sic_code >= %s")
            params.append(lower)
        if upper is not None:
            clauses.append(f"{alias}.sic_code < %s")
            params.append(upper)
        return " AND ".join(clauses), params

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
    with connect() as conn:
        row = conn.execute("SELECT count(*) AS n FROM financials").fetchone()
    return {"status": "ok", "financial_rows": row["n"]}


@app.get("/ratios", response_model=RatioCatalog)
def list_ratios():
    return {"ratios": RATIOS, "default": DEFAULT_METRICS, "categories": CATEGORIES}


@app.get("/fields", response_model=FieldCatalog)
def list_fields():
    """What /sheet's 'where' can name. These are the reported figures the ratios
    were computed from: a request can filter on them, and a sheet never plots
    them."""
    # Field names come from FUNDAMENTALS, so interpolating them is safe.
    aggregates = ", ".join(f"min({n}) AS lo_{n}, max({n}) AS hi_{n}" for n in FUNDAMENTALS)
    with connect() as conn:
        seen = conn.execute(f"SELECT {aggregates} FROM fundamentals").fetchone()

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
        "operators": list(SQL_OPERATORS),
        "note": UNIT_NOTE,
        "example": "revenues:gt:1000",
    }


@app.get("/industries", response_model=IndustryList)
def industries(minimum: Annotated[int, Query(ge=1)] = 1):
    """The industries the database holds, one row per division. There are ten,
    the same ten the app lists as datasets and the same ten the financials table
    is partitioned by, so a name means one thing everywhere. This is the request
    the app makes at startup to know what it can open."""
    sql = """
        SELECT i.division, count(*) AS companies, count(DISTINCT c.sic_code) AS sic_codes
        FROM companies c
        JOIN industries i USING (sic_code)
        GROUP BY i.division
        HAVING count(*) >= %s
        ORDER BY companies DESC, i.division
    """
    with connect() as conn:
        rows = conn.execute(sql, (minimum,)).fetchall()
    return {"industries": rows}


def sheet_sql(query):
    """The /sheet query and its parameters. Separate from the endpoint so a test
    can EXPLAIN the statement the app actually runs rather than one that looks
    like it."""
    # Every metric name is a MetricName and every filter field a FieldName, so
    # both come from an enumerated list and are safe to interpolate. Only the
    # values compared against are parameters, and only they were ever free text.
    columns = ", ".join(f"f.{metric}" for metric in query.columns)

    # The scope predicate goes on the partitioned table itself rather than on
    # companies through the join. That is what lets the planner prune to one
    # division at plan time instead of reading all ten and discarding nine.
    ranked_scope, ranked_params = query.scope("f")
    final_scope, final_params = query.scope("f")

    if query.everywhere:
        # No industry named, so every partition is read and the ranking is done
        # within each industry rather than across all of them: a few from each
        # keeps every industry on the sheet. Naming an industry afterwards is
        # what prunes.
        ranking = f"""
        WITH ranked AS (
            SELECT ticker, size FROM (
                SELECT f.ticker, max(f.{SIZE_METRIC}) AS size,
                       row_number() OVER (PARTITION BY i.division
                                          ORDER BY max(f.{SIZE_METRIC}) DESC NULLS LAST, f.ticker) AS place
                FROM financials f
                JOIN industries i ON i.sic_code = f.sic_code
                WHERE f.year = ANY(%s)
                GROUP BY f.ticker, i.division
            ) placed
            WHERE place <= %s
        )"""
        ranking_params = [query.years, query.per]
    else:
        ranking = f"""
        WITH ranked AS (
            SELECT f.ticker, max(f.{SIZE_METRIC}) AS size
            FROM financials f
            WHERE {ranked_scope} AND f.year = ANY(%s)
            GROUP BY f.ticker
        )"""
        ranking_params = [*ranked_params, query.years]

    narrowing, narrow_params = "", []
    predicates = query.predicates
    if predicates:
        matched_scope, matched_params = query.scope("u")
        clauses = " AND ".join(
            f"u.{p.field.value} {SQL_OPERATORS[p.op.value]} %s" for p in predicates
        )
        # A company qualifies on the years it actually satisfies the filters in;
        # 'all' demands one such row per requested year. A NULL line item never
        # satisfies a comparison, so a company missing the figure is excluded
        # rather than assumed either way.
        having = "HAVING count(*) = %s" if query.match == "all" else ""
        narrowing = f"""
        , matched AS (
            SELECT u.ticker
            FROM fundamentals u
            WHERE {matched_scope} AND u.year = ANY(%s) AND {clauses}
            GROUP BY u.ticker
            {having}
        )"""
        narrow_params = [*matched_params, query.years, *(p.value for p in predicates)]
        if query.match == "all":
            narrow_params.append(len(query.years))

    gate = "JOIN matched USING (ticker)" if predicates else ""

    sql = f"""
        {ranking}{narrowing}
        , top AS (
            SELECT ranked.ticker, ranked.size
            FROM ranked {gate}
            ORDER BY ranked.size DESC NULLS LAST, ranked.ticker
            LIMIT %s
        )
        SELECT c.ticker, c.name, f.year, {columns}
        FROM top
        JOIN financials f USING (ticker)
        JOIN companies c USING (ticker)
        WHERE {final_scope} AND f.year = ANY(%s)
        ORDER BY top.size DESC NULLS LAST, f.ticker, f.year
    """

    params = [*ranking_params, *narrow_params, query.limit, *final_params, query.years]
    return sql, params


class EmptySheet(Exception):
    """No company matched. The endpoint turns this into a 404, which the app
    reports as an industry it could not open."""


def sheet_csv(query):
    """One industry, or every industry, as the CSV the app parses. Every sheet
    the app draws comes through here, so the rows it shows are the database as
    it stands rather than an export of how it once stood."""
    sql, params = sheet_sql(query)
    with connect() as conn:
        rows = conn.execute(sql, params).fetchall()

    if not rows:
        raise EmptySheet(describe_empty(query))

    # Keyed by ticker, not name: seven companies in this data share one name, and
    # keying on it would let one silently overwrite another.
    by_ticker = {}
    for row in rows:
        company = by_ticker.setdefault(row["ticker"], {"name": row["name"], "years": {}})
        company["years"][row["year"]] = row

    return sheetcsv.render(by_ticker.values(), query.columns, query.years)


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
