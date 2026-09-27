# pipeline

Turns the raw financial export into the sheets the app draws, and serves them.

The export lives in a private S3 bucket; `rebuild.py` turns it into the
documents MongoDB Atlas holds, and the API serves them. Settings live in
`pipeline/.env`, which git ignores — copy `.env.example` to `.env` and fill it
in (the Atlas admin user, since rebuilding writes). Reading the export needs an
AWS login: `aws sso login --profile nasba`.

## Rebuilding the data

```sh
python rebuild.py                  # the export in S3 -> Atlas, one document
                                   #   per company
```

One command, and nothing kept in between: every run starts from the export.
`clean.py` owns what "usable" means, `ratios.py` computes, `publish.py` shapes
and writes the documents, and `rebuild.py` runs the three in order. `--db`
publishes somewhere other than the live database; `--source` reads a local
copy of the export instead of S3.

### The export

`s3://nasba-data-354363694859/raw.csv.gz` is the seed everything grows from:
every row the export had, verbatim, every value text. The bucket is private,
encrypted and versioned, so replacing the file keeps the old one. To replace
it with a new export:

```sh
gzip -k export.csv
aws s3 cp export.csv.gz s3://nasba-data-354363694859/raw.csv.gz --profile nasba
python rebuild.py
```

Because the export keeps every row, the ones cleaning discards are still there
to ask about — `clean.read_raw()` hands back all of them, and
`raw[raw.esg_score == "."]` answers why a company is missing from a sheet.

## Serving the app

The app ships no data. It asks `/industries` at startup for what exists, and
draws each sheet from `/sheet`, so every row it shows is the database as it
stands rather than an export of how it once stood:

```sh
uvicorn api:app --host 0.0.0.0 --port 8000
```

Bind `0.0.0.0` rather than localhost — the request comes from a headset on the
same network, and the address it uses is the one line in
`Assets/StreamingAssets/api.url`.

## Deploying to AWS

The same `api.py` runs on Lambda behind a Function URL; `template.yaml`
defines it and `samconfig.toml` holds the deploy settings (profile `nasba`,
us-east-1). Two SecureString parameters must exist first, and the function
reads them itself — the template names them, it never holds them:

- `/nasba/mongodb-uri` — the Atlas connection string for the read-only user
- `/nasba/api-key` — what callers send as `X-Api-Key`; only `/health` is open

```sh
aws sso login --profile nasba
sam build && sam deploy          # prints ApiUrl when it finishes
REGRESSION_URL=<ApiUrl> REGRESSION_KEY=<key> pytest tests/test_regression.py
```

Atlas has to accept connections from anywhere (`0.0.0.0/0`), since Lambda has
no fixed address; the database password is what keeps it closed.

Requirements are split the same way: `requirements.txt` is what the API needs
and all that goes into the Lambda package; `requirements-dev.txt` adds the
pipeline that rebuilds the data, the tests and uvicorn for working on a laptop.

## Generated artifacts

One thing is generated *outside* this directory and checked in, so the C# tool
contract cannot drift from the metric list. It has a `--check` mode that fails if
the checked-in copy is stale, and it needs the database:

```sh
python codegen.py --check      # Assets/Source/Gemini/Tools/FinancialsContract.g.cs
```

## Tests

```sh
pytest
```

Every run rebuilds a separate database, `nasba_test`, from the export before
anything else happens, and every test reads that one — never `nasba`, which the
deployed API serves. So a run checks the whole path: export, cleaning, ratios,
documents and API. The formula tests check the documents against arithmetic
done by hand on the cleaned export, and the API tests seed a company or two
and delete them again. Needs the AWS login and `.env` above.

Three kinds of test, all under pytest:

- **Unit** — ratio formulas against hand arithmetic, the document validator,
  the API key check.
- **Integration** — every endpoint through FastAPI's test client, against the
  test database rebuilt from the export.
- **Regression** — `tests/test_regression.py` sends 174 requests and fails on
  any answer that differs by a byte from a recording, one test per request.
  Record before a change with `python regression.py`; the recording holds real
  data, so it lives in the git-ignored `regression/` and the tests skip without
  it. Set `REGRESSION_URL` and `REGRESSION_KEY` to hold the deployed API to the
  same recording.

## The one list

`metrics.py` owns the names: the 18 ratios and their categories, the filterable
line items, the year range, the sheet defaults, and the SIC boundaries the
divisions are cut on. `ratios.py` computes, `publish.py` labels and validates
and `api.py` serves — all from that one file, so they cannot disagree about
what an industry or a metric is.
