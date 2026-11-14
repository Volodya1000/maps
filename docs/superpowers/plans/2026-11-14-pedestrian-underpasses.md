# Pedestrian Underpasses Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable mapped pedestrian-underpass candidates by default for app users, present their use honestly, and render them at useful map detail.

**Architecture:** Settings normalization is the application-user boundary; explicit `RoutingOptions` still controls Verify and tests. Infrastructure records only candidates accepted by the existing local road-contact matcher, Domain transports IDs, ViewModels format the warning, and Mapsui renders LOD-gated styles without changing raster-tile loading.

**Tech Stack:** .NET 8, xUnit, Avalonia 11, Mapsui, NetTopologySuite, SQLite.

**Spec:** `docs/superpowers/specs/2026-11-14-pedestrian-underpasses-design.md`

## Global Constraints

- No user-facing switch and no A*/road-contact-policy change.
- Preserve old JSON and SQLite v1/v2 reads.
- Verify must accept explicit `AllowExperimentalPedestrianUnderpasses=false`.
- Detail visibility is driven by `metresPerPixel`, never GPS location.
- Do not change gas-candidate appearance.

## Review Focus

- Old JSON `false` is normalized only in user settings; `CrossingPilot` with `false` still has no route.
- Candidate IDs arise from the existing matched road/geometry crossing, not spatial proximity.
- A route without IDs has neither warning nor extra presentation text.
- Coarse map resolutions exclude the underpass style while detailed resolutions carry casing, dash, icon and label.
- SQLite v1/v2 do not query the v3 link table and still return map data.

---

### Task 1: Default settings and explicit internal override

**Files:** `JsonSettingsStore.cs`, `SettingsTests.cs`, Verify tests.

- [ ] Add failing tests for legacy JSON false, saved false and new defaults.
- [ ] Normalize both mandatory crossing flags in the shared settings-store normalization path.
- [ ] Run focused settings and Verify tests.

### Task 2: Typed used-underpass result

**Files:** `EdgeEvaluation.cs`, `TerrainEvaluator.cs`, `RouteAlternative.cs`, `RouteAlternativeBuilder.cs`, routing tests.

- [ ] Add failing tests that enabled valid matching candidates occur in a route result and disabled runs do not.
- [ ] Propagate IDs from the existing `UsedBy` result through edge and alternative builder; preserve ordering/deduplication.
- [ ] Run focused underpass routing tests and the existing road-contact regressions.

### Task 3: User explanation and Verify output

**Files:** `RouteMetricsFormatter.cs`, `MapViewModel` tests, `CrossingPilot.cs` and tests.

- [ ] Add failing tests for exact warning only when `UnderpassCandidateIds` is nonempty and Verify serialization.
- [ ] Format the required warning from the typed result and serialize IDs for comparison tooling.
- [ ] Run focused ViewModel and Verify tests.

### Task 4: Detail Mapsui rendering

**Files:** `MapsuiStyleFactory.cs`, `MapsuiMapAdapter.cs`, `UiTests.cs`, persistence tests.

- [ ] Add failing tests for hidden coarse LOD and detailed casing/dash/symbol/label using candidate geometry, including way/24422904 evidence geometry.
- [ ] Return no underpass style at coarse resolutions and detailed layered styles at local resolutions; keep gas branch unchanged.
- [ ] Run focused Avalonia UI and persistence tests.

### Task 5: Evidence and delivery

**Files:** `docs/evidence/routing-pr55/`, `changes/55.feature.md`.

- [ ] Generate deterministic screenshots for overview and local evidence based on way/24422904.
- [ ] Run lint, desktop and available Android checks; record platform evidence accurately.
- [ ] Commit conventional changes and open one PR without merging.
