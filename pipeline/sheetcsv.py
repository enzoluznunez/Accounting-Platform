"""The one definition of the sheet CSV the Unity app reads.

/sheet renders every sheet through this, so the format the app parses has one
definition here rather than one per caller.
"""

import csv
import io

from metrics import DIVISION_COLORS, UNKNOWN_DIVISION_COLOR


def pairs(metrics, years):
    """The column order: metric-major, one column per year within each metric."""
    return [(metric, year) for metric in metrics for year in years]


def title(metric, year):
    return f"{metric.replace('_', ' ').title()} {year}"


def directive(name, values):
    """A directive line whose body is itself CSV, so a value holding a comma —
    'Finance, Insurance, Real Estate' — arrives quoted rather than splitting into
    three. The parser reads it back with the same CSV rules it reads rows with."""
    body = io.StringIO()
    csv.writer(body, lineterminator="").writerow(values)
    return f"#{name} {body.getvalue()}\n"


def split(text):
    """A rendered sheet as (directives, body): the leading '#' lines parsed into
    {name: [values]}, and the CSV that follows. The reader half of render, so a
    caller reads a sheet the way the app does instead of counting lines — adding
    a directive then costs nothing on this side."""
    lines = text.splitlines(keepends=True)
    directives = {}
    at = 0
    for line in lines:
        if not line.startswith("#"):
            break
        name, _, body = line[1:].strip().partition(" ")
        directives[name] = next(csv.reader([body])) if body else []
        at += 1
    return directives, "".join(lines[at:])


def read(text):
    """A rendered sheet as (directives, header, rows) — split() plus the CSV read
    every caller was doing for itself. The body goes through csv.reader rather
    than str.split because company names hold commas and render quotes them, so
    splitting on the delimiter cuts one of those names in half."""
    directives, body = split(text)
    rows = [row for row in csv.reader(io.StringIO(body)) if row]
    return directives, rows[0] if rows else [], rows[1:]


def render(companies, metrics, years):
    """companies: an iterable of {'name': str, 'industry': str,
    'years': {year: {metric: value}}}.

    Three directives sit above the header. '#group' tells the parser how many
    columns belong to one metric; it reads the years off the end of each header
    cell. '#industry' and '#color' carry one value per data row, in row order, so
    a row arrives already knowing its industry and the colour it is drawn in.
    They travel with the sheet rather than being looked up afterwards because the
    only key a row carries is a company name, and seven companies in this data
    share one — a lookup by name would colour those rows by the wrong industry.
    """
    columns = pairs(metrics, years)
    companies = list(companies)

    buffer = io.StringIO()
    buffer.write(directive("group", [len(years)]))
    buffer.write(directive("industry", [company["industry"] for company in companies]))
    buffer.write(directive("color", [DIVISION_COLORS.get(company["industry"], UNKNOWN_DIVISION_COLOR)
                                     for company in companies]))
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(["Company", *(title(metric, year) for metric, year in columns)])
    for company in companies:
        cells = (company["years"].get(year, {}).get(metric) for metric, year in columns)
        writer.writerow([company["name"], *("" if value is None else f"{value:.4f}" for value in cells)])

    return buffer.getvalue()
