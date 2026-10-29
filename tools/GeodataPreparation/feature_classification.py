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
    if t.get("bridge") not in (None, "no") and "highway" in t:
        return "BridgeCandidate"
    if "highway" in t and t["highway"] not in (
        "footway",
        "path",
        "steps",
        "cycleway",
        "pedestrian",
    ):
        return "Road"
    return None
