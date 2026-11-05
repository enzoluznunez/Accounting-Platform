"""A deployment sets API_KEY; a laptop does not. These hold both halves: with a
key configured every request but /health needs it, and with none configured
nothing is asked for, which is what every other test relies on."""

import pytest

import database


@pytest.fixture
def keyed(monkeypatch):
    monkeypatch.setenv("API_KEY", "right-key")
    database.setting.cache_clear()
    yield "right-key"
    monkeypatch.delenv("API_KEY")
    database.setting.cache_clear()


@pytest.mark.parametrize("path", ["/industries", "/ratios", "/fields", "/sheet?division=Mining&limit=1",
                                  "/openapi.json", "/docs"])
def test_without_the_key_nothing_is_served(client, keyed, path):
    response = client.get(path)
    assert response.status_code == 401
    assert response.json()["detail"] == "The API key is missing or wrong."


def test_a_wrong_key_is_refused(client, keyed):
    assert client.get("/industries", headers={"X-Api-Key": "wrong-key"}).status_code == 401


def test_the_right_key_is_served(client, keyed):
    response = client.get("/industries", headers={"X-Api-Key": keyed})
    assert response.status_code == 200
    assert response.json()["industries"]


def test_health_needs_no_key(client, keyed):
    assert client.get("/health").status_code == 200


def test_with_no_key_configured_nothing_is_asked_for(client, monkeypatch):
    monkeypatch.delenv("API_KEY", raising=False)
    monkeypatch.delenv("API_KEY_PARAMETER", raising=False)
    database.setting.cache_clear()
    assert client.get("/ratios").status_code == 200
