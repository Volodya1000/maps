"""Validate the Cobertura report produced by the single .NET test run."""

from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

LINE_THRESHOLD = 85.0
BRANCH_THRESHOLD = 80.0
REQUIRED_PROJECTS = (
    "CableRoutePlanner.Domain",
    "CableRoutePlanner.Application",
    "CableRoutePlanner.ViewModels",
    "CableRoutePlanner.Infrastructure",
    "CableRoutePlanner.Persistence",
    "CableRoutePlanner.UI",
)
CONDITION_COVERAGE = re.compile(r"\((\d+)\s*/\s*(\d+)\)")


class CoverageError(ValueError):
    """Raised when a report cannot prove the coverage requirements."""


def find_report(results: Path) -> Path:
    reports = sorted(results.rglob("coverage.cobertura.xml"), key=lambda path: path.stat().st_mtime)
    if not reports:
        raise CoverageError(f"No Cobertura report found below {results}")
    return reports[-1]


def _branch_counts(line: ET.Element) -> tuple[int, int]:
    if line.get("branch", "false").lower() != "true":
        return 0, 0
    coverage = line.get("condition-coverage", "")
    match = CONDITION_COVERAGE.search(coverage)
    if not match:
        raise CoverageError(f"Branch line has no usable condition coverage: {ET.tostring(line, encoding='unicode')}")
    return int(match.group(1)), int(match.group(2))


def read_report(report: Path) -> dict[str, dict[str, int]]:
    try:
        root = ET.parse(report).getroot()
    except (ET.ParseError, OSError) as error:
        raise CoverageError(f"Cannot read Cobertura XML {report}: {error}") from error

    projects: dict[str, dict[str, int]] = defaultdict(lambda: {"lines": 0, "covered_lines": 0, "branches": 0, "covered_branches": 0})
    for package in root.findall(".//package"):
        project = package.get("name", "").strip()
        if project not in REQUIRED_PROJECTS:
            continue
        for line in package.findall("./classes/class/lines/line"):
            stats = projects[project]
            stats["lines"] += 1
            stats["covered_lines"] += int(line.get("hits", "0")) > 0
            covered, total = _branch_counts(line)
            stats["covered_branches"] += covered
            stats["branches"] += total

    missing = [project for project in REQUIRED_PROJECTS if project not in projects]
    if missing:
        raise CoverageError("Missing required assemblies: " + ", ".join(missing))
    if sum(stats["lines"] for stats in projects.values()) == 0:
        raise CoverageError("Cobertura report contains no measurable source lines")
    return dict(projects)


def percent(covered: int, total: int) -> float:
    return 100.0 * covered / total if total else 100.0


def check(report: Path) -> bool:
    projects = read_report(report)
    line_total = sum(stats["lines"] for stats in projects.values())
    line_covered = sum(stats["covered_lines"] for stats in projects.values())
    branch_total = sum(stats["branches"] for stats in projects.values())
    branch_covered = sum(stats["covered_branches"] for stats in projects.values())
    line_rate = percent(line_covered, line_total)
    branch_rate = percent(branch_covered, branch_total) if branch_total else 0.0

    print("C# Code Coverage")
    print("------------------------------")
    print(f"{'Metric':<16}{'Actual':>8}{'Minimum':>10}")
    print(f"{'Line coverage':<16}{line_rate:>7.1f}%{LINE_THRESHOLD:>9.1f}%")
    print(f"{'Branch coverage':<16}{branch_rate:>7.1f}%{BRANCH_THRESHOLD:>9.1f}%")
    print("\nProjects:")
    for project, stats in projects.items():
        print(f"{project.removeprefix('CableRoutePlanner.'):20}{percent(stats['covered_lines'], stats['lines']):.1f}%")
    passed = line_rate >= LINE_THRESHOLD and branch_rate >= BRANCH_THRESHOLD
    print(f"\nResult: {'PASS' if passed else 'FAIL'}")
    return passed


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, help="Directory containing test result folders")
    options = parser.parse_args(argv)
    try:
        report = find_report(options.results)
        return 0 if check(report) else 1
    except CoverageError as error:
        print(f"Coverage check failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
