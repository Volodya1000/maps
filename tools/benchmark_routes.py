"""Run bounded cold-process route measurements; preserve failures as measurements."""

import argparse
import datetime
import json
import os
import platform
import subprocess
import time
from pathlib import Path


def measure(command, output, timeout_seconds):
    started = time.monotonic()
    environment = os.environ | {"DOTNET_GCHeapHardLimit": "60000000"}
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=timeout_seconds,
            env=environment,
        )
        status = "completed" if result.returncode == 0 else "failed"
        stdout, stderr, code = result.stdout, result.stderr, result.returncode
    except subprocess.TimeoutExpired as error:
        status, code = "timeout", None
        stdout = (error.stdout or b"").decode(errors="replace")
        stderr = (error.stderr or b"").decode(errors="replace")
    metrics = []
    if status == "completed":
        payload = json.loads(output.read_text())
        metrics = [feature["properties"]["metrics"] for feature in payload["features"]]
        if not metrics:
            status = "no_routes"
    return dict(
        status=status,
        exit_code=code,
        elapsed_wall_ms=round((time.monotonic() - started) * 1000),
        metrics=metrics,
        stdout=stdout[-6000:],
        stderr=stderr[-6000:],
    )


def write_reports(report, output):
    (output / "report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    )
    lines = [
        "# Routing benchmark",
        "",
        "Each scenario uses a cold process; timings include data loading.",
        "",
        "| Scenario | Result | Wall seconds | Routes |",
        "| --- | --- | ---: | ---: |",
    ]
    for result in report["results"]:
        lines.append(
            f"| {result['id']} | {result['status']} | {result['elapsed_wall_ms'] / 1000:.1f} | {len(result['metrics'])} |"
        )
    (output / "report.md").write_text("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path, required=True)
    parser.add_argument(
        "--scenarios", type=Path, default=Path("samples/routing-benchmarks.json")
    )
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((args.data / "manifest.json").read_text())
    report = dict(
        recorded_at=datetime.datetime.now(datetime.UTC).isoformat(),
        cpu_count=os.cpu_count(),
        host_load_average=os.getloadavg() if hasattr(os, "getloadavg") else None,
        schema="maps.routing-benchmark.v1",
        platform=platform.platform(),
        data_sha256=manifest["database_sha256"],
        timeout_seconds=args.timeout_seconds,
        managed_heap_limit_mib=1536,
        cold_process=True,
        results=[],
    )
    for scenario in json.loads(args.scenarios.read_text()):
        geometry = args.output / (scenario["id"] + ".geojson")
        command = [
            "dotnet",
            str(args.verify_dll),
            str(args.data),
            str(geometry),
            *scenario["points"],
        ]
        geometry.unlink(missing_ok=True)
        result = measure(command, geometry, args.timeout_seconds)
        report["results"].append(scenario | result)
        write_reports(report, args.output)
        print(
            f"{scenario['id']}: {result['status']}, {result['elapsed_wall_ms']} ms",
            flush=True,
        )


if __name__ == "__main__":
    main()
