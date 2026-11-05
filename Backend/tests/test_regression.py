"""Regression tests: every recorded request, answered exactly as it was.

One test per request in regression.requests(), so a failure names the request
that changed. The recording comes from `python regression.py` and is not in the
repository; without it these skip.

By default the answers come from the API in this process, reading the test
database. Set REGRESSION_URL (and REGRESSION_KEY) to hold a deployed API to the
same recording instead:

    REGRESSION_URL=https://... REGRESSION_KEY=... pytest tests/test_regression.py
"""

import os

import pytest

import regression

RECORDED = regression.load()
URL = os.environ.get("REGRESSION_URL")
KEY = os.environ.get("REGRESSION_KEY")

pytestmark = pytest.mark.skipif(
    RECORDED is None, reason=f"no recording at {regression.SNAPSHOT}; run: python regression.py")


def test_the_recording_covers_every_request():
    assert set(RECORDED) == set(regression.requests())


@pytest.mark.parametrize("path", regression.requests())
def test_answer_is_unchanged(client, path):
    if URL:
        status, body = regression.fetch(URL, KEY, path)
    else:
        response = client.get(path)
        status, body = response.status_code, response.text

    want_status, want_body = RECORDED[path]
    assert status == want_status, f"{path}: status {status}, was {want_status}"
    assert body == want_body, f"{path}: body changed\n  was: {want_body[:200]!r}\n  now: {body[:200]!r}"
