"""Typed JSON options passed to the existing C# Verify crossing command.

Historical mode preserves the old omitted-underpass behaviour. Current mode uses
user-facing underpass defaults. Frozen scenario files themselves are immutable.
"""

from collections.abc import Mapping
from typing import Literal, TypedDict

RoutingMode = Literal["historical", "current"]


class RoutingOptions(TypedDict):
    CellSizeMetres: float
    MaxNodes: int
    ReserveFraction: float
    PreferOverheadGasCrossings: bool
    MaxRouteLengthMetres: float
    AllowExperimentalPedestrianUnderpasses: bool


def _number(values: Mapping[str, object], key: str, default: float) -> float:
    value = values.get(key, default)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{key}: expected a JSON number, got {value!r}")
    return value


def _boolean(values: Mapping[str, object], key: str, default: bool) -> bool:
    value = values.get(key, default)
    if not isinstance(value, bool):
        raise TypeError(f"{key}: expected a JSON boolean, got {value!r}")
    return value


def routing_options(
    mode: RoutingMode, overrides: Mapping[str, object] | None = None
) -> RoutingOptions:
    """Expand every significant option; C# remains responsible for domain ranges."""
    if mode not in ("historical", "current"):
        raise ValueError(f"routingMode: expected historical/current, got {mode!r}")
    values = overrides or {}
    max_nodes = values.get("MaxNodes", 100000)
    if isinstance(max_nodes, bool) or not isinstance(max_nodes, int):
        raise TypeError(f"MaxNodes: expected a JSON integer, got {max_nodes!r}")
    return {
        "CellSizeMetres": _number(values, "CellSizeMetres", 25),
        "MaxNodes": max_nodes,
        "ReserveFraction": _number(values, "ReserveFraction", 0.1),
        "PreferOverheadGasCrossings": _boolean(
            values, "PreferOverheadGasCrossings", True
        ),
        "MaxRouteLengthMetres": _number(values, "MaxRouteLengthMetres", 1000000),
        "AllowExperimentalPedestrianUnderpasses": _boolean(
            values, "AllowExperimentalPedestrianUnderpasses", mode == "current"
        ),
    }
