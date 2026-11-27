# Routing reliability implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make route failures explainable and automatically reproducible, remove planned roads from physical barriers, and fix only demonstrated search defects.

**Architecture:** Keep existing Domain crossing permissions unchanged. Preparation classifies source lifecycle correctly; Infrastructure emits bounded structured search diagnostics; Verify persists them; Python independently audits frozen inputs and results. Reuse the existing country-data preparation and routing workflow.

**Tech Stack:** Python unittest/Shapely/osmium, .NET 8/xUnit, GitHub Actions.

**Spec:** User-approved five-step proposal in this chat, with the explicit constraint to preserve road-crossing rules. Track/service/unclassified permissions and application statistics for future track crossings are deferred. Service means access/internal roads and does not imply an unpaved surface.

## Global constraints

- Preserve 88 frozen point pairs and original options; identify actual package and binary by SHA-256.
- Keep track, service and unclassified permissions unchanged. Planned roads are source lifecycle metadata, not existing physical roads.
- No weakening analyzers, hooks, audit rules, coverage gates or known-positive assertions.
- Preserve cancellation, deterministic results and exact full-segment obstacle validation.
- No maps, SDKs or APKs in Git; use artifacts or external preparation directories.

## Review focus

- Planned highway tags combined with other physical objects must not hide real water/buildings/railways.
- NoRoute must retain diagnostics; absent/incomplete process results must fail evidence integrity.
- Modern connected-underpass geometry must be audited by source-node topology, including negative flag-off checks.
- Positive witnesses must fail the gate when missing; exploratory NoRoute is not proof of physical impossibility.
- Finer-grid/portal fixes must preserve endpoints and obstacle rules rather than merely return a geometry.

## Task 1: Source lifecycle classification

Files: `tools/GeodataPreparation/feature_classification.py`, geodata classification tests.

- [ ] Add failing cases for proposed roads/bridges and existing-road controls; run unittest to observe red.
- [ ] Exclude planned highway barriers without suppressing other physical classifications; run narrow and data checks.

## Task 2: Structured search diagnostics

Files: Infrastructure routing logging/search files, `tools/CableRoutePlanner.Verify`, focused xUnit tests.

- [ ] Add behavior-level tests for diagnostics on successful and failed searches.
- [ ] Emit actual mesh size, bounds, endpoint/portal connections and bounded failure facts through existing structured logging. Capture diagnostic events in Verify JSON for both statuses; no new Domain/UI dependencies.
- [ ] Verify no route or cost behavior change from diagnostics, including cancellation and repeated calls.

## Task 3: Automated frozen-corpus CI

Files: existing routing workflow, Python runner/audit helpers and focused unittest files.

- [ ] Test modern manifest provenance, unchanged frozen inputs, process failure, known-positive rejection and connected-underpass audits.
- [ ] Replace expired artifact/count assumptions with the existing verified country-data acquisition. Run frozen scenarios without regeneration or rewriting tracked corpus.
- [ ] Include known underpass/bridge controls with explicit expected outcomes. Preserve raw outputs and audit details; publish success/NoRoute/technical failure separately.
- [ ] Keep expensive runs manual/label driven, support current head code and stable pinned data, and persist reproducible metadata.

## Task 4: Prove and repair search defects

Files: focused routing regression tests and the smallest implicated graph/search code; evidence report.

- [ ] Investigate at least three representative failures with independent full-route witnesses and actual diagnostics.
- [ ] For any confirmed missed valid path, first demonstrate a failing public regression, then fix its root cause without changing crossing policy.
- [ ] Preserve unresolved NoRoute as unknown and include all investigated cases in the report; do not promise a percentage increase without measured evidence.

## Task 5: Validate and open PR

- [ ] Add this PR's nonempty changelog fragment; inspect architecture and the full diff.
- [ ] Run repository lint/data/desktop gates and applicable pre-commit/pre-push hooks. Report unavailable platform checks accurately.
- [ ] Run the frozen corpus and controls on the final binary, review independent evidence, commit using Conventional Commits, push and open the PR.
- [ ] Monitor relevant CI through completion and report measured route rates and remaining limitations.
