# Real route recovery implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover independently proven real missed routes, explain all56 prior failures, and make the real regressions automatic without changing road permissions.

**Architecture:** Keep Domain rules, primary search, original inputs and user resource limits intact. Infrastructure retries only exhausted graph searches with bounded connector reach and finer grids. A bounded search-area retry is included only after source-complete experiments prove it useful within the same node budget. Existing evaluator and final geometry validation remain authoritative; no witness vertices enter production requests.

**Tech Stack:** .NET8/xUnit/NTS2.6, Python unittest/Shapely, existing SQLite and GitHubActions tooling.

**Spec:** User request of2026-11-26: investigate all56 failures, find admissible detours, fix confirmed search defects in the next PR, add real automatic regressions, keep road crossing rules unchanged. User has already authorized implementation and PR creation.

## Constraints and evidence

- Parent is PR64/head1711032; preserve the frozen88 corpus and all original points/options.
- Existing baseline routing tests:97 passed. Railway-only ways were ignored before import, so making them explicitly neutral did not remove a former barrier.55 of56 failures have no loaded underpass candidate.
- Four source-complete in-bounds witnesses pass independent Python audit AND actual C# full-segment evaluator. Fine-requested5m with the SAME100000-node budget recovers Gomel bridge01/03/05. Original-grid wider portal links recover Naliboki bridge02.
- Three more independent witnesses require a larger area; prove bounded recovery before adding those positive assertions.
- Preserve first successful ordinary alternatives and golden geometry. Never retry cancellation, invalid inputs, unrelated failures or a proven route-length rejection.
- Every attempt stays within package/source coverage and original MaxNodes. Geometry, permissions and final metrics are not relaxed.

## Review focus

- Real source subsets must declare their true complete coverage, never pretend to include missing country data.
- Finer cells shrink cell-count connector radii: do not accidentally replace the proven wider original-grid search with a shorter physical horizon.
- Cancellation between attempts must propagate; repeat runs cannot retain prior graph/edge state.
- Diagnostics must identify multiple attempts under one calculation; CI must validate actual final search bounds rather than reject legitimate multiple meshes or accept malformed evidence.
- Known positive witnesses must fail CI on NoRoute; blocked direct lines remain insufficient proof that no detour exists.

## Task1: Pin the real failures before changing production

Files: tests/CableRoutePlanner.Tests/Infrastructure/SourceRouteRecoveryTests.cs; Support/SourceRoutingCaseFixture.cs; Fixtures/SourceRouting/*.json.gz; test project content declaration.

- [x] Import seven byte-preserved bounded source extracts and provenance from the full package, plus independently audited witnesses.
- [x] Add KnownWitnessPassesOriginalTerrainRules and OriginalSourceEndpointsFindValidRouteThroughMappedTerrain for all seven exact IDs. Only the original two points/options enter PlanAsync.
- [x] Verify seven witnesses pass while seven route regressions fail with typed NoRoute. Assert endpoints, inclusive finite length, complete geometry and unchanged road permissions.

## Task2: Add bounded recovery after exhausted search

Files: Infrastructure/Routing/GridRoutePlanner.cs, Graph/GridSearchGraph.cs, Routes/RouteGeometryBuilder.cs, RoutingLog.cs; focused search diagnostics/regression tests.

- [x] Request the existing minimum cell size within the original MaxNodes after complete failure, using a bounded sparse SurfaceDetourSearch when the ordinary lattice misses a passage. Keep the initial successful path unchanged. A wider all-portals connector retry was rejected after profiling its excessive cost.
- [x] Preserve one calculation correlation with ordered attempt metadata, actual cell/bounds/radius and structured outcome; persist the new events in Verify JSON.
- [x] Add negative forbidden-road, unchanged-first-success, length-limit, cancellation at/between searches, source-budget and repeated-request assertions; run narrow red/green and real fixtures.

## Task3: Recover confirmed area-limited detours when source-complete probes justify it

Files: same planner, bounded real fixture extracts, routing algorithm documentation.

- [x] Test natural bounded2km/4km margin stages against three independent outside-area witnesses using full data and the SAME100000-node limit.
- [x] Add only proven source-complete real positive cases; clamp all queries to actual package coverage. Reuse existing maximum5km margin ceiling and full evaluator. A sparse constrained-triangle graph is needed for Pukhovichi07 within the node budget.
- [x] Keep capped/unresolved independent attempts explicitly unresolved, with per-case evidence for all56.

## Task4: Automate evidence and deliver the next PR

Files: tools/routing_regression.py, tools/routing_portal_witnesses.py and Python tests; docs/evidence/routing-recovery; docs/routing-algorithm.md; changes/2026112601.bugfix.md.

- [x] Promote independently proven recovered IDs into positive CI assertions without editing the frozen corpus. Store the full per-case56 investigation and portable research probe.
- [x] Test multi-attempt diagnostic integrity and final bounds; retain technical/malformed/provenance failure gates. Review exposed an escaping bridge splice and stale research-witness files; both received regression cases. Timeout stream normalization also received typed red/green cases.
- [ ] Run relevant narrow tests, then existing lint/data/desktop/Android gates and hooks. Review architecture, diff, exact test evidence and changelog.
- [ ] Compare parent/current on the same package and original88 inputs, record measured improvement and remaining unknowns; open a dependent PR if64 is still open, follow final CI to completion.
