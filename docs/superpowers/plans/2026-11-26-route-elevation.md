# Route elevation implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Track steps with checkboxes.

**Goal:** Ship one PR providing an offline selected-route elevation profile and terrain cable length.
**Architecture:** Domain owns validated profile arithmetic, Application owns sampling/analyzer contracts and revision lifecycle, Infrastructure samples final geometry, Persistence reads optional block SQLite DEM, ViewModels/UI present and select samples.
**Tech Stack:** Existing .NET 8, Avalonia 11.3/ReactiveUI, Microsoft.Data.Sqlite; existing locked Python/rasterio preparation.
**Spec:** ../specs/2026-11-26-route-elevation-design.md

## Global Constraints
- A* geometry, WeightedCost, LengthMetres, route limits and tracking preserve existing semantics.
- No runtime network, GDAL, new chart package, copied OsmAnd code, or analyzer suppressions.
- Missing heights remain nullable; incomplete profiles never produce a full surface length.
- Reserve applies exactly once to complete/applicable surface length, else an explicitly labeled 2D fallback.
- Domain is BCL-only; no new Infrastructure/Persistence edge.
- Every behavioral slice uses meaningful red/green evidence and existing repository checks.

## Review Focus
- Flat endpoints with an interior hill must increase length: Task 1 sampling test.
- Missing one raster neighbour must not produce an interpolated plausible height: Task 2 read test.
- Editing inputs during elevation analysis must prevent stale publication: Task 1 lifecycle test.
- A chart touch must not consume vertical sheet scrolling: Task 3 touch test.
- Android must install changed DEM even if OSM manifest is unchanged: Task 4 packaging test.

### Task 1: Profile model, analysis and lifecycle
Own Domain profile data/arithmetic, Application contracts/PlanningSession, Infrastructure elevation analyzer and DI, tests in Domain/Infrastructure/Application.
Produces ElevationSource, RouteElevationSample, RouteElevationProfile; IElevationProvider.Source/ReadAsync; IRouteElevationAnalyzer.AnalyzeAsync.
- [x] Write failing flat/slope/hill/missing/reverse and cancellation/revision cases; observe red.
- [x] Add WGS84 distance and bounded final-geometry sampling, Domain totals, nullable terrain cable base, immutable route enrichment.
- [x] Run focused tests and record assertions/results.

### Task 2: Offline DEM package
Own Persistence elevation reader/registration, tools/GeodataPreparation/prepare_elevation.py, focused Persistence and tooling tests.
Consumes shared provider/model contracts. Produces read-only block SQLite schema and documented preparation CLI.
- [x] Write failing interpolation, block seam, NoData, invalid metadata and missing-package cases.
- [x] Preserve native north-up geographic raster samples; metadata JSON in SQLite, source hashes, block floats; bounded cache and cancellation.
- [x] Run focused tests; prepare genuine small summit fixture with attribution and independent evidence if network available.

### Task 3: Selected-route profile UI
Own ViewModels/UI profile presentation/control/views/resources and focused VM/UI tests. Consume immutable route profile.
- [x] Write failing selected-alternative, clear, cursor, keyboard, touch and responsive cases.
- [x] Add compact responsive chart, text summary, missing-data state and dedicated map cursor; preserve compiled bindings/activation.
- [x] Show terrain/planar/correction/reserve metrics and real route name.
- [x] Run focused tests and render/screenshot at desktop and phone widths.

### Task 4: Delivery and verification
Own Android optional package installation, final integration, docs/evidence/changelog and PR.
- [x] Verify Android optional DEM installation and update detection.
- [x] Independently check real summit profile with geographic reference and report actual cable difference.
- [x] Run lint, desktop, data, terrain, applicable android; review architecture and whole diff.
- [x] Fix discovered issues, including the Mapsui gas-symbol cache race, and stage the changelog. Final delivery uses mandatory hooks, a Conventional Commit and one pull request.

### Task 5: Compatibility with saved calculations from current main
- [x] Preserve full/partial elevation profiles, original source, chosen alternative, reserve and options through save/reopen without another analysis.
- [x] Read legacy version1; version new profile-bearing records explicitly and reject inconsistent or truncated profiles before state mutation.
- [x] Prove roundtrip and Save/Open behavior with typed failures, then rerun the current-main gates.

### Task 6: Countrywide coverage after Android field feedback
- [x] Reproduce the cause: the reported Minsk coordinates are outside the demo crop; country APK preparation omitted DEM entirely.
- [x] Prepare pinned Copernicus GLO-30 inputs for the entire Belarus package bounds, with small compressed, independently verified shards.
- [x] Read only route-local shards and install a complete immutable generation before publishing its catalog; preserve legacy single-file packages.
- [x] Include country DEM in PR/full installers and reject missing/corrupt/incomplete DEM in distributable APK checks.
- [x] Verify the reported coordinates, border regions and tile seams on actual country data; build a full Belarus APK and run repository gates before updating PR #65.
