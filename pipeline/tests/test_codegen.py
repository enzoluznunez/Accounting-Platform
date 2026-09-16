"""The Unity tool and the API used to repeat the same constraints in two
languages. These tests fail if the generated file, or the tool that consumes it,
drifts from the OpenAPI schema."""

from pathlib import Path

import codegen
from metrics import CATEGORIES, DEFAULT_METRICS, FUNDAMENTALS, RATIOS, UNIT_NOTE

TOOL = codegen.TARGET.parent / "QueryFinancials.cs"


def test_generated_file_is_current():
    assert codegen.TARGET.exists(), "run: python codegen.py"
    assert codegen.TARGET.read_text() == codegen.render(codegen.schema())


def test_generated_limits_match_the_pydantic_model():
    text = codegen.TARGET.read_text()
    limit = codegen.parameters(codegen.schema(), "/sheet")["limit"]
    assert f"LimitMinimum = {limit['minimum']};" in text
    assert f"LimitMaximum = {limit['maximum']};" in text
    assert f"LimitDefault = {limit['default']};" in text


def test_generated_metrics_are_the_metric_surface():
    text = codegen.TARGET.read_text()
    assert all(f'"{ratio}"' in text for ratio in RATIOS)
    assert all(f'"{metric}"' in text for metric in DEFAULT_METRICS)


def test_unity_tool_reads_the_contract_instead_of_repeating_it():
    source = TOOL.read_text()
    assert "FinancialsContract.LimitMinimum" in source
    assert "FinancialsContract.LimitMaximum" in source
    assert "FinancialsContract.LimitDefault" in source
    assert "FinancialsContract.Metrics" in source
    # The hand-copied constraints are gone.
    assert "Limits(1, 200)" not in source
    assert "?? 30" not in source


def test_generated_filter_fields_are_the_filter_surface():
    text = codegen.TARGET.read_text()
    assert all(f'"{field}"' in text for field in FUNDAMENTALS)
    for operator in ("eq", "ne", "lt", "lte", "gt", "gte"):
        assert f'"{operator}"' in text


def test_generated_categories_cover_every_ratio():
    text = codegen.TARGET.read_text()
    for name, members in CATEGORIES.items():
        assert f'"{name}"' in text
        assert all(f'"{ratio}"' in text for ratio in members)


def test_the_unit_note_reaches_the_generated_file():
    # A filter in the wrong unit comes back as an empty sheet rather than an
    # error, so this sentence has to travel with the contract.
    assert UNIT_NOTE in codegen.TARGET.read_text()


def test_unity_tool_validates_filters_against_the_contract():
    """The tool refuses a bad filter locally, using the generated lists rather
    than its own copy of them, so a typo costs no round trip."""
    source = TOOL.read_text()
    assert "FinancialsContract.FilterFields" in source
    assert "FinancialsContract.FilterOperators" in source
    assert "FinancialsContract.FilterUnits" in source
    # No hand-copied field names or operators.
    for literal in ('"revenues", "assets"', '"gt", "gte"'):
        assert literal not in source
