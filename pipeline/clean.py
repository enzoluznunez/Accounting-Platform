"""Hold the export in Postgres and derive the usable rows from it in SQL.

Two tables. 'raw' is the export verbatim, every column text, nothing discarded:
it is the seed this whole database grows from, and it is what the rows that do
not survive cleaning can still be asked about. 'staging' is the typed subset
everything downstream reads, and the WHERE clause that separates them is the
whole definition of "usable".

    python clean.py                  rebuild staging from raw
    python clean.py --import FILE    load an export into raw first

The import is a bootstrap: once 'raw' holds the export, the file has nothing
left to say. Back it up with pg_dump rather than keeping a copy on disk:

    pg_dump -d nasba -t raw -Fc -f raw.dump
"""

import argparse
import csv
import re
import sys

import psycopg

from metrics import YEARS

DSN = "dbname=nasba"

KEYS = ["Trading Symbol", "Year", "GVKEY", "SIC Code", "Entity Central Index Key"]
TEXT = [
    "Trading Symbol",
    "Entity Registrant Name",
    "Entity Address, Address Line One",
    "Entity Address, Postal Zip Code",
    "Entity Address, City or Town",
    "Entity Address, Country",
]

# What the export writes where it has no figure. These are the values that make
# a row unusable, named rather than left to a library's defaults.
MISSING = [".", "", "NA", "N/A", "NaN", "nan", "null", "NULL"]


def column(heading):
    """'Entity Address, City or Town' -> entity_address_city_or_town. The one
    place the export's headings are turned into names SQL can hold."""
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", heading.lower())).strip("_")


KEY_COLUMNS = {column(h) for h in KEYS}
TEXT_COLUMNS = {column(h) for h in TEXT}


def kind(name):
    return ("TEXT" if name in TEXT_COLUMNS
            else "INTEGER" if name in KEY_COLUMNS
            else "DOUBLE PRECISION")


def columns_of(conn, table):
    return [
        row[0] for row in conn.execute(
            "SELECT column_name FROM information_schema.columns"
            " WHERE table_name = %s ORDER BY ordinal_position", (table,)
        )
    ]


def import_export(conn, path):
    """Load an export file into 'raw', replacing whatever is there. Every column
    is text, so a malformed figure is a value to look at rather than a load that
    fails."""
    with open(path, newline="") as handle:
        heads = [h.replace("\n", " ") for h in next(csv.reader(handle))]
    names = [column(h) for h in heads]

    conn.execute("DROP TABLE IF EXISTS raw CASCADE")
    conn.execute(f"CREATE TABLE raw ({', '.join(f'{n} TEXT' for n in names)})")
    with open(path, "rb") as handle:
        with conn.cursor().copy(
            f"COPY raw ({', '.join(names)}) FROM STDIN WITH (FORMAT csv, HEADER)"
        ) as copy:
            while chunk := handle.read(1 << 20):
                copy.write(chunk)
    conn.execute("ANALYZE raw")


def transform(conn):
    """raw -> staging. Reads the shape from the table, not from a file, so this
    works whether or not an export is still lying around."""
    names = columns_of(conn, "raw")
    if not names:
        raise SystemExit("no 'raw' table; load an export first: python clean.py --import FILE")

    # A row is usable when every column has a figure. Spelled out per column
    # rather than hidden inside a dropna(), so what was discarded stays a
    # question anyone can put to 'raw'.
    present = " AND ".join(
        f"{n} IS NOT NULL AND {n} <> ALL(%(missing)s::text[])" for n in names
    )
    cast = ",\n    ".join(
        f"{n} AS {n}" if kind(n) == "TEXT" else f"{n}::{kind(n)} AS {n}"
        for n in names
    )

    conn.execute("DROP TABLE IF EXISTS staging CASCADE")
    conn.execute(
        f"CREATE TABLE staging AS SELECT\n    {cast}\nFROM raw\n"
        # Compared as text: a cast in WHERE would be evaluated against every
        # row, including the ones this clause exists to exclude.
        f"WHERE {present} AND year = ANY(%(years)s::text[])",
        {"missing": MISSING, "years": [str(y) for y in YEARS]},
    )

    # Only companies reported in every year survive, so a sheet never shows a
    # bar for one year and a gap for the other.
    conn.execute(
        "DELETE FROM staging s WHERE ("
        "  SELECT count(*) FROM staging t WHERE t.trading_symbol = s.trading_symbol"
        ") <> %s",
        (len(YEARS),),
    )
    conn.execute("ALTER TABLE staging ADD PRIMARY KEY (trading_symbol, year)")
    conn.execute("ANALYZE staging")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--import", dest="source", metavar="FILE",
                        help="load this export into 'raw' before transforming")
    args = parser.parse_args()

    with psycopg.connect(DSN) as conn:
        if args.source:
            import_export(conn, args.source)
        transform(conn)

        raw_rows = conn.execute("SELECT count(*) FROM raw").fetchone()[0]
        rows, companies = conn.execute(
            "SELECT count(*), count(DISTINCT trading_symbol) FROM staging"
        ).fetchone()

    print(f"raw: {raw_rows} rows -> staging: {rows} rows, {companies} companies "
          f"({raw_rows - rows} discarded)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
