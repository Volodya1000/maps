"""Display level selection shared by country preparation and lightweight tests."""


def include_in_display_lod(kind, tags, area, lod):
    """Keep overview layers readable while retaining local settlement detail."""
    road = tags.get("highway", "")
    if lod == 0:
        return (
            kind == "Settlement"
            and tags.get("place") == "city"
            or kind == "Road"
            and road in ("motorway", "trunk", "primary")
            or kind in ("Lake", "Forest")
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
