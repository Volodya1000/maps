"""Validation for the non-production Windows 7 prototype guest smoke result."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from tools.windows7_result import ResultValidationError, _valid_png

SCHEMA = "maps.win7.prototype.v1"
REQUIRED_CHECKS = (
    "P-01-process-started",
    "P-02-window-visible",
    "P-03-sqlite-read",
    "P-04-mapsui-layer",
    "P-05-native-window",
    "P-06-screenshot",
    "P-07-gui-point-assertion",
    "P-08-no-exception",
)


def validate_prototype_result(
    result_path: Path, screenshot_path: Path, commit: str
) -> dict[str, Any]:
    try:
        result = json.loads(result_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ResultValidationError(
            f"invalid prototype result JSON: {error}"
        ) from error
    if not isinstance(result, dict) or result.get("schema") != SCHEMA:
        raise ResultValidationError("unsupported prototype result schema")
    if result.get("status") != "PASS" or result.get("commit") != commit:
        raise ResultValidationError("prototype result is not a PASS for this commit")
    if result.get("result_flushed") is not True:
        raise ResultValidationError("prototype result was not flushed before shutdown")
    if result.get("shutdown_requested") is not True:
        raise ResultValidationError("prototype did not request guest shutdown")
    checks = result.get("checks")
    if not isinstance(checks, dict) or any(
        checks.get(name) is not True for name in REQUIRED_CHECKS
    ):
        raise ResultValidationError(
            "prototype result is missing a required successful check"
        )
    if not screenshot_path.is_file() or not _valid_png(screenshot_path):
        raise ResultValidationError("prototype screenshot is missing or invalid")
    return result
