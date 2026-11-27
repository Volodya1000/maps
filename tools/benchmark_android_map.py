"""Benchmark real Android map splitting; verify every reconstructed SQLite hash.

Each case runs in a fresh process so peak RSS is not inherited from a previous
case. Reports include compression time/CPU/RSS and verified decompression time.
The input must already be a verified country package (country_data.py validate).
No caches or converter inputs are changed by this tool.
"""

import argparse
import gzip
import hashlib
import json
import os
import platform
import subprocess
import sys
import tempfile
import time
import zlib
from pathlib import Path

from split_android_map import COMPRESSION_LEVELS, WORKER_COUNTS, split_package


def peak_rss_bytes() -> int:
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        class MemoryCounters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
                (name, ctypes.c_size_t)
                for name in (
                    "peak_working_set",
                    "working_set",
                    "peak_paged_pool",
                    "paged_pool",
                    "peak_nonpaged_pool",
                    "nonpaged_pool",
                    "pagefile",
                    "peak_pagefile",
                )
            ]

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        psapi.GetProcessMemoryInfo.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(MemoryCounters),
            wintypes.DWORD,
        ]
        psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        counters = MemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(
            kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return counters.peak_working_set
    import resource

    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    return peak if sys.platform == "darwin" else peak * 1024


def measure(source: Path, destination: Path, level: int, workers: int) -> dict:
    started, cpu = time.perf_counter(), time.process_time()
    split_package(source, destination, compresslevel=level, workers=workers)
    seconds, cpu_seconds = time.perf_counter() - started, time.process_time() - cpu
    peak = peak_rss_bytes()
    chunks = sorted(destination.glob("geodata.*.gz"))
    digest, size = hashlib.sha256(), 0
    started = time.perf_counter()
    for chunk in chunks:
        with gzip.open(chunk, "rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                size += len(block)
                digest.update(block)
    decompression_seconds = time.perf_counter() - started
    actual = digest.hexdigest()
    expected = json.loads((source / "manifest.json").read_text(encoding="utf-8"))[
        "database_sha256"
    ]
    if actual != expected:
        raise ValueError(
            f"Reconstructed database SHA-256 {actual} differs from manifest {expected}"
        )
    return dict(
        compresslevel=level,
        workers=workers,
        wall_seconds=seconds,
        cpu_seconds=cpu_seconds,
        peak_rss_bytes=peak,
        chunks=len(chunks),
        compressed_bytes=sum(p.stat().st_size for p in chunks),
        database_bytes=size,
        database_sha256=actual,
        decompression_seconds=decompression_seconds,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument(
        "--levels",
        type=int,
        nargs="+",
        choices=COMPRESSION_LEVELS,
        default=[1, 3, 6, 9],
    )
    parser.add_argument(
        "--workers", type=int, nargs="+", choices=WORKER_COUNTS, default=[1, 2, 4]
    )
    parser.add_argument("--case", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.repeats < 1:
        parser.error("--repeats must be positive")
    if args.case:
        print(
            json.dumps(
                measure(args.source, args.output, args.levels[0], args.workers[0])
            )
        )
        return
    args.output.mkdir(parents=True, exist_ok=False)
    with (args.source / "geodata.sqlite.gz").open("rb") as stream:
        source_sha256 = hashlib.file_digest(stream, "sha256").hexdigest()
    report = dict(
        python=sys.version,
        platform=platform.platform(),
        logical_cpus=os.cpu_count(),
        zlib=zlib.ZLIB_RUNTIME_VERSION,
        source_gzip_sha256=source_sha256,
        cases=[],
    )
    for repeat in range(args.repeats):
        # Alternate order to expose rather than hide warm filesystem effects.
        combinations = [
            (level, workers) for level in args.levels for workers in args.workers
        ]
        if repeat % 2:
            combinations.reverse()
        for level, workers in combinations:
            with tempfile.TemporaryDirectory(
                prefix="case-", dir=args.output
            ) as scratch:
                result = subprocess.run(
                    [
                        sys.executable,
                        str(Path(__file__).resolve()),
                        str(args.source.resolve()),
                        str(Path(scratch) / "chunks"),
                        "--case",
                        "--levels",
                        str(level),
                        "--workers",
                        str(workers),
                    ],
                    check=True,
                    text=True,
                    stdout=subprocess.PIPE,
                )
                case = json.loads(result.stdout)
            case["repeat"] = repeat + 1
            report["cases"].append(case)
            (args.output / "results.json").write_text(
                json.dumps(report, indent=2) + "\n", encoding="utf-8"
            )
            print(json.dumps(case), flush=True)


if __name__ == "__main__":
    main()
