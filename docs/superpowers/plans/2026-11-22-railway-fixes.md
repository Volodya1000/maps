# Railway rendering, metrics and gas labels

Goal: implement the supplied railway/GAS correction specification on top of
`origin/feat/railway-rendering-and-routing` (c07dc81); main has no railway support.

Architecture: retain neutral railway traversal rules. Count crossings in Infrastructure
from final projected route geometry, publish via RouteMetrics and its existing formatter.
Use screen-pixel railway styles and Mapsui's existing RotateWithMap symbol rendering.

- [x] Pin overview/detail boundaries and duplicate/segment joins with rendered pixel tests.
  Replace black overview strokes with grey strokes narrower than roads; detail at <= 2 m/pixel.
  Deduplicate/merge display linework before styling so overlapping beds cannot cover inserts.
- [x] Add RailwayCrossings to RouteMetrics (optional default 0). Prepare railway linework
  once per area; union duplicate segments and merge split ways. Analyze final route visits
  independently, rejecting endpoints, tangencies and shared sections; preserve parallel tracks.
  Test multiple tracks, repeated crossings, split/duplicate ways, touch/overlap, alternatives
  and recalculation through the existing route builder/planner/formatter.
- [x] Move GAS annotation out of raster source to annotation layer. Render its existing
  label as a reusable bitmap symbol with RotateWithMap=true at its geographic anchor.
  Test angles 0/45/90/180/270/317 and anchor position; retain bridge/underpass tests.
- [x] Run relevant focused tests, lint and desktop CI entry points. Report Windows/Android
  device checks as unverified if unavailable. Add a changelog fragment and review diff.

Review focus: route vertices exactly on tracks, reversed/partial duplicates, repeated visits
to the same coordinate, endpoints/touch-and-return and tile LOD transitions. Thresholds and
geometric tolerances belong in the responsible style/analyzer, never in routing costs.

Validation: 744/744 tests passed, coverage 95.8% line / 88.1% branch.
See [evidence](../../evidence/railway-fixes.md) for commands and platform limitations.
