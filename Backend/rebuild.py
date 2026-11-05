"""Rebuild the database the API serves, from the export, in one command.

    python rebuild.py                  the export in S3 -> the 'nasba' database
    python rebuild.py --db nasba_test  the same, into a scratch database
    python rebuild.py --source FILE    a local copy of the export instead

The export is read (clean.read_raw), the usable rows kept (clean.usable), the
ratios computed (ratios.compute) and the result published as one document per
company (publish.publish). Nothing is kept in between: every run starts from
the export, so the database is always what the export and this code say.

Reading from S3 needs AWS credentials (AWS_PROFILE=nasba after
`aws sso login --profile nasba`); publishing needs the Atlas admin user in .env.
"""

import argparse
import sys

import clean
import database
import publish
import ratios


def build(source=clean.RAW_SOURCE):
    """The export -> (staging, documents), without writing anything."""
    staging = clean.usable(clean.read_raw(source))
    return staging, list(publish.documents(ratios.compute(staging)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=clean.RAW_SOURCE,
                        help="s3://bucket/key or a local path to the export (default: %(default)s)")
    parser.add_argument("--db", default=None, help="database to publish into (default: MONGODB_DB or nasba)")
    args = parser.parse_args()

    staging, docs = build(args.source)
    collection = publish.publish(docs, args.db)

    years = sum(len(doc["years"]) for doc in docs)
    print(f"{args.source}: {len(staging)} usable rows -> "
          f"{args.db or database.name()}.{database.COLLECTION}: "
          f"{collection.count_documents({})} companies, {years} company-years")
    return 0


if __name__ == "__main__":
    sys.exit(main())
