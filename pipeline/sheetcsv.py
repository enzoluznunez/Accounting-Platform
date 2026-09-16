"""The one definition of the sheet CSV the Unity app reads.

/sheet renders every sheet through this, so the format the app parses has one
definition here rather than one per caller.
"""

import csv
import io


def pairs(metrics, years):
    """The column order: metric-major, one column per year within each metric."""
    return [(metric, year) for metric in metrics for year in years]


def title(metric, year):
    return f"{metric.replace('_', ' ').title()} {year}"


def render(companies, metrics, years):
    """companies: an iterable of {'name': str, 'years': {year: {metric: value}}}.

    The '#group' directive tells the parser how many columns belong to one
    metric; it reads the years off the end of each header cell.
    """
    columns = pairs(metrics, years)

    buffer = io.StringIO()
    buffer.write(f"#group {len(years)}\n")
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(["Company", *(title(metric, year) for metric, year in columns)])
    for company in companies:
        cells = (company["years"].get(year, {}).get(metric) for metric, year in columns)
        writer.writerow([company["name"], *("" if value is None else f"{value:.4f}" for value in cells)])

    return buffer.getvalue()
