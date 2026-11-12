"""Small CI adapters for the routing-crossing pilot workflow."""

import argparse
import gzip
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path


def extract_database(data: Path) -> None:
    manifest = json.loads((data / "manifest.json").read_text(encoding="utf-8"))
    with (
        gzip.open(data / "geodata.sqlite.gz", "rb") as source,
        (data / "geodata.sqlite").open("wb") as target,
    ):
        shutil.copyfileobj(source, target, 1024 * 1024)

    digest = hashlib.sha256()
    with (data / "geodata.sqlite").open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != manifest["database_sha256"]:
        raise RuntimeError("database SHA-256 mismatch")


def report_pilot(report_path: Path, summary_path: Path) -> None:
    if not report_path.exists():
        raise RuntimeError("Harness did not produce a report")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    acceptance = report["acceptance"]
    with summary_path.open("a", encoding="utf-8") as summary:
        summary.write("## Pilot execution and acceptance\n\n")
        summary.write(f"Harness integrity: **{acceptance['harnessIntegrity']}**  \n")
        summary.write(f"Pilot accepted: **{acceptance['pilotAccepted']}**  \n")
        summary.write(
            "Allowed road-crossing routes: "
            f"**{acceptance['successfulRoutesWithAllowedRoadContact']}**\n\n"
        )
        for requirement in acceptance["unmetRequirements"]:
            summary.write(f"- Not met: {requirement}\n")
        for requirement in acceptance["unverifiedRequirements"]:
            summary.write(f"- Unverified: {requirement}\n")
    if acceptance["harnessIntegrity"] != "pass":
        raise RuntimeError(
            "Harness incomplete or a successful route was not validly audited"
        )


def verify_indirect(report_path: Path) -> None:
    if not report_path.exists():
        raise RuntimeError("Indirect crossing study did not produce a report")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if report["assessment"]["classification"] != "valid_route_found":
        raise RuntimeError(
            "Indirect crossing study did not return an independently valid route"
        )
    if report["invocation"]["userPointCount"] != 2:
        raise RuntimeError(
            "Indirect crossing study did not use exactly two user points"
        )
    if report["invocation"]["witnessVerticesPassedAsUserPoints"]:
        raise RuntimeError(
            "Indirect crossing study passed witness vertices as user points"
        )
    if not report["routeAudits"] or any(
        item["status"] != "valid" for item in report["routeAudits"]
    ):
        raise RuntimeError(
            "Indirect crossing study returned an unaudited or invalid alternative"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    extract = subparsers.add_parser("extract-db")
    extract.add_argument("--data", type=Path, required=True)
    report = subparsers.add_parser("report-pilot")
    report.add_argument("--report", type=Path, required=True)
    report.add_argument(
        "--summary", type=Path, default=Path(os.environ.get("GITHUB_STEP_SUMMARY", ""))
    )
    verify = subparsers.add_parser("verify-indirect")
    verify.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "extract-db":
            extract_database(args.data)
        elif args.command == "report-pilot":
            report_pilot(args.report, args.summary)
        else:
            verify_indirect(args.report)
    except (KeyError, json.JSONDecodeError, OSError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
