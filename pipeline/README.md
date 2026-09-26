# pipeline

Turns the raw financial export into the sheets the app draws, and serves them.

Postgres cleans and computes; MongoDB Atlas holds what the API serves. The
connection string lives in `pipeline/.env`, which git ignores — copy
`.env.example` to `.env` and fill it in (the Atlas admin user, since loading
writes).

## Build order

No data files. The export lives in Postgres as `raw`, verbatim and undiscarded,
and everything is derived from there.

```sh
python clean.py                    # raw -> staging          (in SQL)
psql -d nasba -f schema.sql        # builds the tables
python ratios.py                   # staging -> industries, companies,
                                   #            financials, fundamentals
python load_mongo.py               # those four tables -> Atlas, one
                                   #   document per company
```

`clean.py` owns what "usable" means, `schema.sql` owns the model, `ratios.py`
computes, `load_mongo.py` publishes. Nothing round-trips through a file at any
point.

### Seeding raw

`raw` is the seed the rest of the database grows from. To load an export into it
the first time, or to replace it:

```sh
python clean.py --import Messy.csv
```

Back it up from the database rather than keeping the export on disk:

```sh
pg_dump -d nasba -t raw -Fc -f raw.dump     # restore: pg_restore -d nasba raw.dump
```

Because `raw` keeps every row the export had, the ones cleaning discards are
still there to ask about — `SELECT count(*) FROM raw WHERE esg_score = '.'`
answers why a company is missing from a sheet.

## Serving the app

The API reads MongoDB only; Postgres need not be running to serve. The app
ships no data. It asks `/industries` at startup for what exists, and
draws each sheet from `/sheet`, so every row it shows is the database as it
stands rather than an export of how it once stood:

```sh
uvicorn api:app --host 0.0.0.0 --port 8000
```

Bind `0.0.0.0` rather than localhost — the request comes from a headset on the
same network, and the address it uses is the one line in
`Assets/StreamingAssets/api.url`.

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

Needs both halves built by the steps above: `staging` in Postgres for the
export as loaded, and the Atlas collection for what was computed from it. The
formula tests check what reached Atlas against arithmetic done by hand on
`staging`, so they verify the computation and the load together. The API
tests seed a company or two and delete them again.

Changing the database under the API is checked with `parity.py`: record the
answers before the change, check them after, and every one must match byte for
byte. The snapshot holds real data, so keep it outside the repository.

## The one list

`metrics.py` owns the names: the 18 ratios and their categories, the filterable
line items, the year range, the sheet defaults, the SIC boundaries the divisions
are cut on, and the slug a division takes as an identifier. `ratios.py` computes,
`schema.sql` partitions and `api.py` serves — all from that one file, so they
cannot disagree about what an industry or a metric is.
