"""Hold the API to its own past answers across a change of database.

Records every request below against the API as it stands, then checks a later
API gives back the same status and the same body, byte for byte. The Unity app
only ever sees these responses, so if they match, the app cannot tell which
database it is reading.

    python parity.py --record FILE    save the current API's answers
    python parity.py --check FILE     compare the current API against them

The snapshot holds real data, so keep it out of the repository.
"""

import argparse
import json
import sys

from fastapi.testclient import TestClient

from metrics import CATEGORIES, DIVISION_NAMES, FUNDAMENTALS, RATIOS

OPERATORS = ["eq", "ne", "lt", "lte", "gt", "gte"]

# A spread of SIC codes: the busiest, the rarest, and one per division edge.
SICS = [100, 1000, 1311, 1381, 1531, 2834, 2836, 3674, 4911, 5045, 5812, 6022, 7370, 8062, 9995, 9999]


def requests():
    paths = ["/health", "/ratios", "/fields", "/sheet"]
    paths += [f"/industries?minimum={m}" for m in (1, 2, 17, 25, 100, 656, 657)]

    for division in DIVISION_NAMES:
        paths.append(f"/sheet?division={division}&limit=200")
        paths.append(f"/sheet?division={division}&limit=3&categories=liquidity")
        paths.append(f"/sheet?division={division}&where=revenues:gt:1000&match=all")
    for sic in SICS:
        paths.append(f"/sheet?sic={sic}&limit=200")
        paths.append(f"/sheet?sic={sic}&limit=5&years=2020")
    for per in (1, 2, 3, 5, 10, 50):
        paths.append(f"/sheet?per={per}&limit=200")
        paths.append(f"/sheet?per={per}&limit=7")
    for category in CATEGORIES:
        paths.append(f"/sheet?categories={category}&limit=50")
    paths.append(f"/sheet?metrics={','.join(RATIOS)}&limit=200")
    paths.append("/sheet?years=2019&metrics=net_margin,current_ratio&limit=200")
    paths.append("/sheet?years=2020,2019,2020&limit=10")

    for field in ("revenues", "net_income", "assets", "price_close_annual", "total_debt"):
        for op in OPERATORS:
            for match in ("any", "all"):
                paths.append(f"/sheet?sic=7370&limit=200&where={field}:{op}:100&match={match}")
    for match in ("any", "all"):
        paths.append(f"/sheet?limit=200&where=net_income:lt:0&match={match}")
        paths.append(f"/sheet?division=Manufacturing&limit=200&where=revenues:gt:1000,net_income:gt:0&match={match}")
        paths.append(f"/sheet?division=Services&limit=200&where=revenues:gte:500&where=assets:lt:5000&match={match}")
    paths.append(f"/sheet?sic=7370&where={FUNDAMENTALS[0]}:gt:99999999")
    paths.append("/sheet?division=Mining&where=revenues:gt:99999999&match=all")

    # Rejections: the assistant reads these out loud, so their wording is part
    # of the contract too.
    paths += [
        "/sheet?sic=7370&division=Mining",
        "/sheet?metrics=bogus_ratio",
        "/sheet?categories=profit",
        "/sheet?division=Atlantis",
        "/sheet?years=twenty-nineteen",
        "/sheet?limit=0",
        "/sheet?limit=500",
        "/sheet?where=revenue:gt:1000",
        "/sheet?where=revenues:above:1000",
        "/sheet?where=revenues:gt:lots",
        "/sheet?where=revenues:gt",
        "/sheet?where=current_ratio:gt:2",
        "/industries?minimum=0",
    ]
    return paths


def answers():
    import api

    with TestClient(api.app) as client:
        out = {}
        for path in requests():
            response = client.get(path)
            out[path] = [response.status_code, response.text]
        return out


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--record", metavar="FILE")
    group.add_argument("--check", metavar="FILE")
    args = parser.parse_args()

    current = answers()
    if args.record:
        with open(args.record, "w") as handle:
            json.dump(current, handle, indent=0)
        print(f"recorded {len(current)} answers to {args.record}")
        return 0

    with open(args.check) as handle:
        expected = json.load(handle)

    differing = [path for path in expected if current.get(path) != expected[path]]
    for path in differing:
        want, got = expected[path], current.get(path)
        print(f"DIFFERS {path}\n  was: {want[0]} {want[1][:160]!r}\n  now: {got[0]} {got[1][:160]!r}")
    print(f"{len(expected) - len(differing)}/{len(expected)} answers identical")
    return 1 if differing else 0


if __name__ == "__main__":
    sys.exit(main())
