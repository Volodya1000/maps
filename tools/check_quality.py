"""Validate the one AXAML invariant not covered reliably by compilation: local includes exist."""

from __future__ import annotations

import argparse
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def line_for(path: Path, text: str) -> int:
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        if text in line:
            return number
    return 1


def source_path(source: str, path: Path, ui_root: Path) -> Path | None:
    if source.startswith("avares://"):
        assembly, _, relative = source[9:].partition("/")
        if assembly == "CableRoutePlanner.UI":
            return ui_root / relative
        return None
    return (path.parent / source).resolve()


def validate_axaml(root: Path = ROOT) -> list[str]:
    """Check local project includes while leaving Avalonia semantics to its compiler."""
    errors: list[str] = []
    ui_root = root / "src/CableRoutePlanner.UI"
    for path in sorted((root / "src").rglob("*.axaml")):
        try:
            tree = ElementTree.parse(path)
        except ElementTree.ParseError as error:
            line, column = error.position
            errors.append(
                f"{path.relative_to(root)}:{line}:{column}: invalid XML: {error.msg}"
            )
            continue
        for element in tree.getroot().iter():
            if local_name(element.tag) not in {"StyleInclude", "ResourceInclude"}:
                continue
            source = element.attrib.get("Source")
            target = source_path(source, path, ui_root) if source else None
            if target is not None and not target.is_file():
                errors.append(
                    f"{path.relative_to(root)}:{line_for(path, source)}: "
                    f"include source does not exist: {source}"
                )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    root = parser.parse_args().root.resolve()
    errors = validate_axaml(root)
    if errors:
        print("\n".join(errors))
        return 1
    print(
        "Project-local AXAML includes are valid; Avalonia compiler remains authoritative."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
