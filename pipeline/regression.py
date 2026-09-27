"""The API's answers, recorded, so a later API can be held to them.

tests/test_regression.py sends every request below and fails on any answer
that differs from the recording by a single byte, status included. The Unity
app only ever sees these responses, so while they match, a change underneath —
a new database, a new host, a rewritten pipeline — is invisible to it.

    python regression.py            record the current API's answers

Record before a change, change, run pytest. The recording holds real data, so
it is kept out of the repository (regression/answers.json is git-ignored), and
without one the regression tests skip rather than fail.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from metrics import CATEGORIES, DIVISION_NAMES, FUNDAMENTALS, RATIOS

SNAPSHOT = Path(__file__).with_name("regression") / "answers.json"

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


def fetch(url, key, path):
    """One request sent over the network to a deployed API, as [status, body].
    Only spaces are escaped, as the test client escapes them, so both see the
    same URL."""
    request = urllib.request.Request(url.rstrip("/") + path.replace(" ", "%20"),
                                     headers={"X-Api-Key": key} if key else {})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return [response.status, response.read().decode()]
    except urllib.error.HTTPError as refused:
        return [refused.code, refused.read().decode()]


def load():
    """The recorded answers, or None when nothing has been recorded."""
    if not SNAPSHOT.exists():
        return None
    return json.loads(SNAPSHOT.read_text())


def record():
    from fastapi.testclient import TestClient

    import api

    with TestClient(api.app) as client:
        answers = {}
        for path in requests():
            response = client.get(path)
            answers[path] = [response.status_code, response.text]

    SNAPSHOT.parent.mkdir(exist_ok=True)
    SNAPSHOT.write_text(json.dumps(answers, indent=0))
    print(f"recorded {len(answers)} answers to {SNAPSHOT}")
    return 0


if __name__ == "__main__":
    sys.exit(record())
