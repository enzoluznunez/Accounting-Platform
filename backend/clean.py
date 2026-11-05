"""Read the export as it was delivered and keep the rows that are usable.

The export is the seed the whole dataset grows from. It lives in a private S3
bucket, verbatim and undiscarded, so a row cleaning drops is still there to
ask about. Nothing here writes it anywhere: rebuild.py reads it, cleans it,
computes from it and publishes the result.

    raw = read_raw()            the export, every column text
    staging = usable(raw)       the typed subset everything downstream reads

usable() is the whole definition of "usable".
"""

import gzip
import io
import os
import re

import pandas as pd

from metrics import YEARS

# Where the export is: an s3:// URI or a local path, gzipped or not.
RAW_SOURCE = os.environ.get("RAW_SOURCE", "s3://nasba-data-354363694859/raw.csv.gz")

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
    place the export's headings are turned into identifiers. Applying it to a
    name it already produced changes nothing, so the stored export, whose
    headings are already names, reads the same as the original."""
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", heading.lower())).strip("_")


KEY_COLUMNS = {column(h) for h in KEYS}
TEXT_COLUMNS = {column(h) for h in TEXT}


def read_bytes(source):
    if source.startswith("s3://"):
        # Imported here: only a read from S3 needs it.
        import boto3

        bucket, _, key = source.removeprefix("s3://").partition("/")
        return boto3.client("s3").get_object(Bucket=bucket, Key=key)["Body"].read()
    with open(source, "rb") as handle:
        return handle.read()


def read_raw(source=RAW_SOURCE):
    """The export, every value as the text it was written as. Nothing is
    parsed or guessed at here: an empty cell is an empty string, and 'NA' is
    the two letters, so what counts as missing is decided in one place below."""
    data = read_bytes(source)
    if data[:2] == b"\x1f\x8b":
        data = gzip.decompress(data)
    raw = pd.read_csv(io.BytesIO(data), dtype=str, keep_default_na=False, na_filter=False)
    raw.columns = [column(h.replace("\n", " ")) for h in raw.columns]
    return raw


def usable(raw):
    """raw -> staging. A row is usable when every column has a figure and its
    year is one the sheets draw; a company is usable when every one of those
    years survives, so a sheet never shows a bar for one year and a gap for
    the other."""
    present = ~raw.isin(MISSING).any(axis=1)
    in_years = raw["year"].isin([str(year) for year in YEARS])
    staging = raw[present & in_years]

    years_each = staging.groupby("trading_symbol")["year"].transform("size")
    staging = staging[years_each == len(YEARS)].copy()

    # Text before key: the ticker is both, and it stays text.
    for name in staging.columns:
        if name in TEXT_COLUMNS:
            continue
        if name in KEY_COLUMNS:
            staging[name] = staging[name].str.strip().astype("int64")
        else:
            staging[name] = staging[name].astype("float64")

    if staging.duplicated(["trading_symbol", "year"]).any():
        raise ValueError("the export reports some company twice for one year")
    # In company-then-year order, so everything computed from it comes out the
    # same however the export happened to be ordered.
    return staging.sort_values(["trading_symbol", "year"]).reset_index(drop=True)


def main():
    raw = read_raw()
    staging = usable(raw)
    print(f"raw: {len(raw)} rows -> staging: {len(staging)} rows, "
          f"{staging['trading_symbol'].nunique()} companies ({len(raw) - len(staging)} discarded)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
