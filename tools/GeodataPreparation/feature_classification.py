"""OSM tag classification shared by bounded and country importers."""


def classify(t):
    if (
        t.get("man_made") == "pipeline"
        and t.get("substance") == "gas"
        and t.get("location") == "overhead"
    ):
        return "OverheadGasPipeline"
    if t.get("natural") == "wetland":
        return "Marsh"
    if t.get("natural") == "water" or t.get("landuse") == "reservoir":
        return "River" if t.get("water") in ("river", "stream", "canal") else "Lake"
    if t.get("waterway") in ("river", "stream", "canal", "ditch", "drain"):
        return "River"
    if "building" in t and t["building"] != "no":
        return "Building"
    if t.get("natural") == "wood" or t.get("landuse") == "forest":
        return "Forest"
    if t.get("landuse") in ("farmland", "meadow") or t.get("natural") == "grassland":
        return "Field"
    if t.get("man_made") == "cutline":
        return "Clearing"
    # Planned highways are not physical barriers or existing bridge openings.
    # Continue classifying independent physical tags on the same object.
    existing_highway = "highway" in t and t["highway"] != "proposed"
    if t.get("bridge") not in (None, "no") and existing_highway:
        return "BridgeCandidate"
    if existing_highway and t["highway"] not in (
        "footway",
        "path",
        "steps",
        "cycleway",
        "pedestrian",
    ):
        return "Road"
    if t.get("railway") in ("rail", "light_rail", "narrow_gauge", "tram"):
        if t.get("tunnel") not in (None, "no"):
            return "RailwayTunnel"
        if t.get("bridge") not in (None, "no"):
            return "RailwayBridge"
        return "Railway"
    return None
