"""Enforce project-reference boundaries for the CableRoutePlanner solution."""

import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = {
    "Domain": ROOT / "src/CableRoutePlanner.Domain/CableRoutePlanner.Domain.csproj",
    "Application": ROOT
    / "src/CableRoutePlanner.Application/CableRoutePlanner.Application.csproj",
    "Infrastructure": ROOT
    / "src/CableRoutePlanner.Infrastructure/CableRoutePlanner.Infrastructure.csproj",
    "Persistence": ROOT
    / "src/CableRoutePlanner.Persistence/CableRoutePlanner.Persistence.csproj",
    "ViewModels": ROOT
    / "src/CableRoutePlanner.ViewModels/CableRoutePlanner.ViewModels.csproj",
    "UI": ROOT / "src/CableRoutePlanner.UI/CableRoutePlanner.UI.csproj",
    "Desktop": ROOT / "src/CableRoutePlanner.Desktop/CableRoutePlanner.Desktop.csproj",
    "Android": ROOT / "src/CableRoutePlanner.Android/CableRoutePlanner.Android.csproj",
    "Verify": ROOT / "tools/CableRoutePlanner.Verify/CableRoutePlanner.Verify.csproj",
}
ALLOWED_REFERENCES = {
    "Domain": set(),
    "Application": {"Domain"},
    "Infrastructure": {"Application"},
    "Persistence": {"Application"},
    "ViewModels": {"Application"},
    "UI": {"ViewModels"},
    "Desktop": {"UI", "Infrastructure", "Persistence"},
    "Android": {"UI", "Infrastructure", "Persistence"},
    "Verify": {"Application", "Infrastructure", "Persistence"},
}


def references(project: Path) -> set[str]:
    root = ElementTree.parse(project).getroot()
    result = set()
    for item in root.iter("ProjectReference"):
        target = (project.parent / item.attrib["Include"]).resolve()
        result.add(target.parent.name.removeprefix("CableRoutePlanner."))
    return result


def packages(project: Path) -> set[str]:
    root = ElementTree.parse(project).getroot()
    return {item.attrib["Include"] for item in root.iter("PackageReference")}


def transitive(start: str, graph: dict[str, set[str]]) -> set[str]:
    found: set[str] = set()
    pending = list(graph[start])
    while pending:
        current = pending.pop()
        if current in found:
            continue
        found.add(current)
        pending.extend(graph.get(current, set()) - found)
    return found


def validate_graph(graph: dict[str, set[str]]) -> list[str]:
    errors = []
    for project, actual in graph.items():
        unexpected = actual - ALLOWED_REFERENCES[project]
        if unexpected:
            errors.append(
                f"{project} has forbidden project references: {', '.join(sorted(unexpected))}"
            )
    if graph["Verify"] & {"UI", "ViewModels"} or transitive("Verify", graph) & {
        "UI",
        "ViewModels",
    }:
        errors.append(
            "Verify must not reference Avalonia/Mapsui presentation projects, directly or transitively"
        )
    if "Persistence" in transitive(
        "Infrastructure", graph
    ) or "Infrastructure" in transitive("Persistence", graph):
        errors.append("Infrastructure and Persistence must remain independent")
    return errors


def main() -> int:
    graph = {name: references(path) for name, path in PROJECTS.items()}
    errors = validate_graph(graph)
    if packages(PROJECTS["Domain"]):
        errors.append("Domain must not reference external packages")
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("Project reference boundaries are valid.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
