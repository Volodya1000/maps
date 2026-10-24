"""Validate fragments and require a new fragment in the PR/staged branch."""

import argparse
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
NAME = re.compile(r"\d+\.(feature|bugfix|refactor|doc)\.md$")


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def check(base: str, staged: bool) -> None:
    ancestor = git("merge-base", base, "HEAD")
    args = ["diff", "--name-only", "--diff-filter=ACMR"]
    args.extend(["--cached", ancestor] if staged else [f"{ancestor}...HEAD"])
    changed = git(*args).splitlines()
    fragments = [name for name in changed if name.startswith("changes/") and NAME.fullmatch(Path(name).name)]
    if not fragments:
        raise ValueError("Add changes/<issue-or-PR-number>.<feature|bugfix|refactor|doc>.md to this branch.")
    for path in (ROOT / "changes").iterdir():
        if path.name == "README.md":
            continue
        if not path.is_file() or not NAME.fullmatch(path.name) or not path.read_text(encoding="utf-8").strip():
            raise ValueError(f"Invalid or empty fragment: {path.relative_to(ROOT)}")
    for fragment in fragments:
        text = git("show", f":{fragment}") if staged else git("show", f"HEAD:{fragment}")
        if not text.strip():
            raise ValueError(f"Empty fragment in {'index' if staged else 'HEAD'}: {fragment}")
    print(f"Changelog: {len(fragments)} fragment(s) added or updated relative to {base}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default=os.environ.get("CHANGELOG_BASE", "origin/main"))
    parser.add_argument("--staged", action="store_true")
    options = parser.parse_args()
    try:
        check(options.base, options.staged)
    except (ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Changelog check failed: {error}\n")
