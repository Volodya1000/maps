# Extract coverage

`belarus-coverage.json` stores the closed ring of the Geofabrik Belarus extract polygon in `[longitude, latitude]` order. It describes available extract coverage, not a surveyed or legal border. It is small package metadata, not the map database.

Source: <https://download.geofabrik.de/europe/belarus.poly>, retrieved 2026-10-29. Original `.poly` SHA-256: `426f5d237d2ac7fe48b28b3ad34b7a0949c429fd90801b3b91cf7de31df596d2`. The 369 coordinates are preserved without simplification. OpenStreetMap/Geofabrik attribution and ODbL apply, as for the existing map package; see the map notices in `samples/real-geodata-fixtures/minsk/licenses/`.

Persistence embeds this ring for existing `region_id=belarus` packages whose manifests contain only a rectangular envelope. A manifest can explicitly provide `coverage_boundary`, an array in the same format, which takes precedence. Other legacy packages retain their rectangular coverage. No runtime download or map reinstallation is required.
