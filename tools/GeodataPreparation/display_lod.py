"""Display level selection shared by country preparation and lightweight tests."""


def include_in_display_lod(kind, tags, area, lod):
    """Keep overview layers readable while retaining local settlement detail."""
    if kind == "OverheadGasPipeline":
        return False
    if kind == "GasPipelineCrossingCandidate":
        # These are short, reviewable points rather than a dense pipeline layer.
        # Keep them visible at regional and country overview levels so users can
        # discover where a route may follow a marked overhead crossing.
        return lod in (0, 1, 2)
    road = tags.get("highway", "")
    if lod == 0:
        return (
            kind == "Settlement"
            and tags.get("place") == "city"
            or kind == "Road"
            and road in ("motorway", "trunk", "primary")
            or kind in ("Lake", "Forest", "Field")
            and area > 0.005
        )
    if lod == 1:
        return (
            kind == "Settlement"
            and tags.get("place") in ("city", "town")
            or kind == "Road"
            and road in ("motorway", "trunk", "primary", "secondary", "tertiary")
            or kind in ("River", "Lake", "Marsh", "Forest", "Field")
            and area > 0.00001
        )
    return lod == 2 and kind != "Building"
