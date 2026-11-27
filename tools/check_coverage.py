"""Validate the Cobertura report produced by the single .NET test run."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path, PurePosixPath
from uuid import UUID

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


TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
COVERLET_COLLECTOR_URI = "datacollector://microsoft/CoverletCodeCoverage/1.0"


def _relative_trx_path(value: str) -> Path:
    path = PurePosixPath(value.replace("\\", "/"))
    if not path.parts or path.is_absolute() or ".." in path.parts or ":" in value:
        raise CoverageError(
            f"TRX attachment path must be relative to its run: {value!r}"
        )
    return Path(*path.parts)


def _trx_report(results: Path, trx: Path) -> Path:
    try:
        root = ET.parse(trx).getroot()
    except (ET.ParseError, OSError) as error:
        raise CoverageError(f"Cannot read test run TRX {trx}: {error}") from error
    if root.tag != TRX_NAMESPACE + "TestRun":
        raise CoverageError(f"Unsupported test run TRX format: {trx}")
    collectors = [
        node
        for node in root.findall(
            f"{TRX_NAMESPACE}ResultSummary/{TRX_NAMESPACE}CollectorDataEntries/"
            f"{TRX_NAMESPACE}Collector"
        )
        if node.get("uri", "").casefold() == COVERLET_COLLECTOR_URI.casefold()
    ]
    if len(collectors) != 1:
        raise CoverageError(
            f"Expected one Coverlet collector in {trx}, found {len(collectors)}"
        )
    attachments = collectors[0].findall(
        f"{TRX_NAMESPACE}UriAttachments/{TRX_NAMESPACE}UriAttachment/{TRX_NAMESPACE}A"
    )
    if len(attachments) != 1:
        raise CoverageError(
            f"Expected one Coverlet attachment in {trx}, found {len(attachments)}"
        )
    attachment = _relative_trx_path(attachments[0].get("href", ""))
    if attachment.name != "coverage.cobertura.xml":
        raise CoverageError(f"Coverlet attachment is not Cobertura: {attachment}")
    candidates = [results / attachment]
    deployment = root.find(f"{TRX_NAMESPACE}TestSettings/{TRX_NAMESPACE}Deployment")
    if deployment is not None:
        folder = _relative_trx_path(deployment.get("runDeploymentRoot", ""))
        candidates.append(results / folder / "In" / attachment)
    existing = [path for path in candidates if path.is_file()]
    if len(existing) != 1 or not existing[0].resolve().is_relative_to(
        results.resolve()
    ):
        raise CoverageError(
            f"TRX must identify one existing attachment in {results}: {attachment}"
        )
    return existing[0]


def _same_collector_copy(results: Path, canonical: Path, copy: Path) -> bool:
    # VSTest on Windows keeps the collector GUID directory and copies the same
    # attachment into TRX's deployment/In/<machine> tree. No arbitrary aliasing.
    deployed = canonical.relative_to(results)
    relative = copy.relative_to(results)
    if len(deployed.parts) != 4 or deployed.parts[1] != "In":
        return False
    if len(relative.parts) != 2:
        return False
    try:
        UUID(relative.parts[0])
    except ValueError:
        return False
    with canonical.open("rb") as first, copy.open("rb") as second:
        return (
            hashlib.file_digest(first, "sha256").digest()
            == hashlib.file_digest(second, "sha256").digest()
        )


def find_report(results: Path) -> Path:
    # check.py passes a fresh UUID directory, so only this test run is eligible.
    # TRX identifies its collector attachment independently of filesystem mtime.
    reports = sorted(results.rglob("coverage.cobertura.xml"))
    runs = sorted(results.glob("*.trx"))
    if runs:
        if len(runs) != 1:
            raise CoverageError(
                f"Expected one test run TRX in {results}, found {len(runs)}"
            )
        canonical = _trx_report(results, runs[0])
        others = [path for path in reports if path != canonical]
        if len(others) > 1 or any(
            not _same_collector_copy(results, canonical, path) for path in others
        ):
            raise CoverageError(
                f"Reports do not match the TRX collector identity in {results}"
            )
        return canonical
    if not reports:
        raise CoverageError(f"No Cobertura report found below {results}")
    if len(reports) != 1:
        raise CoverageError(
            f"Expected one Cobertura report for test run {results}, found {len(reports)}: "
            + ", ".join(str(path.relative_to(results)) for path in reports)
        )
    return reports[0]


def _branch_counts(line: ET.Element) -> tuple[int, int]:
    if line.get("branch", "false").lower() != "true":
        return 0, 0
    coverage = line.get("condition-coverage", "")
    match = CONDITION_COVERAGE.search(coverage)
    if not match:
        raise CoverageError(
            f"Branch line has no usable condition coverage: {ET.tostring(line, encoding='unicode')}"
        )
    return int(match.group(1)), int(match.group(2))


def read_report(report: Path) -> dict[str, dict[str, int]]:
    try:
        root = ET.parse(report).getroot()
    except (ET.ParseError, OSError) as error:
        raise CoverageError(f"Cannot read Cobertura XML {report}: {error}") from error

    projects: dict[str, dict[str, int]] = defaultdict(
        lambda: {"lines": 0, "covered_lines": 0, "branches": 0, "covered_branches": 0}
    )
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
        print(
            f"{project.removeprefix('CableRoutePlanner.'):20}{percent(stats['covered_lines'], stats['lines']):.1f}%"
        )
    passed = line_rate >= LINE_THRESHOLD and branch_rate >= BRANCH_THRESHOLD
    print(f"\nResult: {'PASS' if passed else 'FAIL'}")
    return passed


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "results", type=Path, help="Directory containing test result folders"
    )
    options = parser.parse_args(argv)
    try:
        report = find_report(options.results)
        return 0 if check(report) else 1
    except CoverageError as error:
        print(f"Coverage check failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
